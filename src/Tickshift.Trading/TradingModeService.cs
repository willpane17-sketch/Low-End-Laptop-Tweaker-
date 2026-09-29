using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;
using Tickshift.Diagnostics;
using Tickshift.Diagnostics.Monitoring;
using Tickshift.Optimization;
using Tickshift.Optimization.Optimisations;
using Tickshift.Optimization.Recovery;
using Tickshift.Persistence;
using Tickshift.Security;

namespace Tickshift.Trading;

public enum TradingModeState { Inactive, Activating, Active, Ending }

public enum PressureLevel { Normal, Elevated, Severe }

public sealed record ProtectedAppStatus(ProtectedApplication App, bool Running, int ProcessCount, double CpuPercent, long MemoryBytes)
{
    public string StatusText => Running ? $"Running · {ProcessCount} process{(ProcessCount == 1 ? string.Empty : "es")} · {Format.Bytes(MemoryBytes)}" : "Not running";
}

public sealed record TradingSnapshot(
    DateTimeOffset At,
    double CpuPercent,
    double MemoryPercent,
    PressureLevel CpuPressure,
    PressureLevel MemoryPressure,
    IReadOnlyList<ProtectedAppStatus> ProtectedApps,
    int BrowserProcessCount,
    double BrowserCpuPercent,
    long BrowserMemoryBytes,
    IReadOnlyList<ProcessUsage> CloseSuggestions,
    double? TemperatureCelsius);

public sealed record TradingActivationOptions(bool AdjustPowerPlan, bool ReduceAnimations);

public sealed record TradingSessionRecord
{
    public Guid SessionId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public List<Guid> RunIds { get; init; } = [];
    public List<string> AppliedChanges { get; init; } = [];
}

public sealed record ActivationStep(string Name, string Detail, bool Done);

/// <summary>
/// Hysteresis-based pressure classification so notifications do not flap. Severe needs a sustained
/// high average; it clears only once load has clearly dropped.
/// </summary>
public sealed class PressureEvaluator
{
    private PressureLevel _cpu = PressureLevel.Normal;
    private PressureLevel _memory = PressureLevel.Normal;

    public (PressureLevel Cpu, PressureLevel Memory, bool CpuBecameSevere, bool MemoryBecameSevere) Evaluate(IReadOnlyList<MetricSample> recent)
    {
        if (recent.Count == 0) return (_cpu, _memory, false, false);
        var cpu = recent.Average(s => s.CpuPercent);
        var mem = recent.Average(s => s.MemoryPercent);
        var newCpu = Next(_cpu, cpu, severeAt: 90, clearBelow: 75, elevatedAt: 70);
        var newMem = Next(_memory, mem, severeAt: 92, clearBelow: 85, elevatedAt: 80);
        var cpuSevere = newCpu == PressureLevel.Severe && _cpu != PressureLevel.Severe;
        var memSevere = newMem == PressureLevel.Severe && _memory != PressureLevel.Severe;
        (_cpu, _memory) = (newCpu, newMem);
        return (_cpu, _memory, cpuSevere, memSevere);
    }

    private static PressureLevel Next(PressureLevel current, double value, double severeAt, double clearBelow, double elevatedAt)
    {
        if (value >= severeAt) return PressureLevel.Severe;
        if (current == PressureLevel.Severe && value >= clearBelow) return PressureLevel.Severe;
        return value >= elevatedAt ? PressureLevel.Elevated : PressureLevel.Normal;
    }
}

/// <summary>
/// Trading Mode. Records current settings, applies only safe temporary changes (via the same verified,
/// journalled optimisations), watches resource pressure and notifies — it never terminates or restarts
/// applications, never touches process priorities, never interacts with brokers, accounts or orders,
/// and restores every temporary change when the session ends (or on next launch after a crash).
/// </summary>
public sealed class TradingModeService : IAsyncDisposable
{
    private static readonly TimeSpan NotificationCooldown = TimeSpan.FromMinutes(10);

    private readonly OptimisationEngine _engine;
    private readonly RecoveryService _recovery;
    private readonly IChangeJournal _journal;
    private readonly IProcessProvider _processes;
    private readonly IMetricsSource _metrics;
    private readonly INotificationService _notifications;
    private readonly ISettingsStore _settings;
    private readonly AppDataPaths _paths;
    private readonly TradingSessionFlag _flag;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PressureEvaluator _pressure = new();
    private readonly Dictionary<string, DateTimeOffset> _lastNotified = new();
    private TradingSessionRecord? _session;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitor;

    public TradingModeService(OptimisationEngine engine, RecoveryService recovery, IChangeJournal journal, IProcessProvider processes,
        IMetricsSource metrics, INotificationService notifications, ISettingsStore settings, AppDataPaths paths, TradingSessionFlag flag, ILogger<TradingModeService>? logger = null)
    {
        _engine = engine;
        _recovery = recovery;
        _journal = journal;
        _processes = processes;
        _metrics = metrics;
        _notifications = notifications;
        _settings = settings;
        _paths = paths;
        _flag = flag;
        _logger = logger ?? NullLogger<TradingModeService>.Instance;
    }

    public TradingModeState State { get; private set; } = TradingModeState.Inactive;
    public bool IsActive => State is TradingModeState.Active or TradingModeState.Activating;
    public DateTimeOffset? StartedAt => _session?.StartedAt;
    public IReadOnlyList<string> AppliedChanges => _session?.AppliedChanges ?? [];
    public TradingSnapshot? Latest { get; private set; }

    public event EventHandler<bool>? ActiveChanged;
    public event EventHandler<TradingSnapshot>? SnapshotUpdated;
    public event EventHandler<TradingModeState>? StateChanged;

    public ProtectedApplicationMatcher CreateMatcher() => new(_settings.Current.ProtectedApplications);

    public async Task<OperationResult> ActivateAsync(TradingActivationOptions options, IProgress<ActivationStep>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != TradingModeState.Inactive) return OperationResult.NotApplicable("Trading Mode is already active.");
            SetState(TradingModeState.Activating);

            // 1. Record the session first so a crash can always be recovered from.
            progress?.Report(new ActivationStep("Recording current settings", "Saving a recovery record before changing anything", false));
            _session = new TradingSessionRecord { SessionId = Guid.NewGuid(), StartedAt = DateTimeOffset.Now };
            await PersistSessionAsync(ct).ConfigureAwait(false);

            // 2–3. Identify protected apps and review pressure.
            progress?.Report(new ActivationStep("Identifying protected applications", $"{_settings.Current.ProtectedApplications.Count(a => a.Enabled)} protected", false));
            _processes.GetProcesses(); // baseline for per-process CPU
            progress?.Report(new ActivationStep("Reviewing resource pressure", "Processor, memory and background apps", false));

            // 5–6. Temporary, reversible, verified changes only.
            var ids = new List<string>();
            if (options.AdjustPowerPlan)
            {
                ids.Add(BalancedWhenPluggedInOptimisation.OptimisationId);
                ids.Add(HighPerformanceWhenPluggedInOptimisation.OptimisationId);
            }

            if (options.ReduceAnimations) ids.Add(VisualEffectsOptimisation.OptimisationId);

            if (ids.Count > 0)
            {
                progress?.Report(new ActivationStep("Applying temporary optimisations", "These are undone when Trading Mode ends", false));
                var run = await _engine.RunDirectAsync(ids,
                    new ExecutionOptions(_settings.Current.DryRunMode, false, false, TimeSpan.Zero, "Trading Mode", Temporary: true), ct).ConfigureAwait(false);
                _session = _session with
                {
                    RunIds = [run.Id],
                    AppliedChanges = run.Items.Where(i => i.Status is RunItemStatus.Verified or RunItemStatus.DryRun).Select(i => i.Title + (i.Status == RunItemStatus.DryRun ? " (dry run)" : string.Empty)).ToList(),
                };
                await PersistSessionAsync(ct).ConfigureAwait(false);
            }

            SetState(TradingModeState.Active);
            ActiveChanged?.Invoke(this, true);
            StartMonitor();
            progress?.Report(new ActivationStep("Monitoring", "Watching for severe resource pressure", true));
            _logger.LogInformation("Trading Mode activated with {Count} temporary change(s)", _session.AppliedChanges.Count);
            return OperationResult.Success("Trading Mode is active.");
        }
        catch (OperationCanceledException)
        {
            await RestoreSessionChangesAsync(CancellationToken.None).ConfigureAwait(false);
            SetState(TradingModeState.Inactive);
            return OperationResult.Cancelled("Trading Mode activation was cancelled; any temporary changes were restored.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trading Mode activation failed");
            await RestoreSessionChangesAsync(CancellationToken.None).ConfigureAwait(false);
            SetState(TradingModeState.Inactive);
            return OperationResult.FromException(ex, "Start Trading Mode");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> EndAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != TradingModeState.Active) return OperationResult.NotApplicable("Trading Mode is not active.");
            SetState(TradingModeState.Ending);
            await StopMonitorAsync().ConfigureAwait(false);
            var report = await RestoreSessionChangesAsync(ct).ConfigureAwait(false);
            SetState(TradingModeState.Inactive);
            ActiveChanged?.Invoke(this, false);
            Latest = null;

            var message = report is null || report.Items.Count == 0
                ? "Trading Mode ended. No temporary settings needed restoring."
                : $"Trading Mode ended. Restored {report.Restored} setting(s)" +
                  (report.Conflicts > 0 ? $"; {report.Conflicts} had been changed by something else and were left as is" : string.Empty) +
                  (report.Failed > 0 ? $"; {report.Failed} could not be restored — see the Recovery Centre" : string.Empty) + ".";
            return report?.Failed > 0 ? OperationResult.Failure(message) : OperationResult.Success(message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>At launch: if a session file exists, the app exited during Trading Mode — restore its temporary changes.</summary>
    public async Task<OperationResult?> RecoverAfterUnexpectedExitAsync(CancellationToken ct)
    {
        var session = AtomicJsonFile.Read<TradingSessionRecord>(_paths.TradingSessionFile).Value;
        if (session is null) return null;
        _session = session;
        var report = await RestoreSessionChangesAsync(ct).ConfigureAwait(false);
        return OperationResult.Success($"TICKSHIFT closed unexpectedly during Trading Mode. {report?.Restored ?? 0} temporary setting(s) were restored.");
    }

    private async Task<RollbackReport?> RestoreSessionChangesAsync(CancellationToken ct)
    {
        var session = _session;
        if (session is null) return null;
        var records = _journal.GetAll().Where(r => r.Temporary && session.RunIds.Contains(r.RunId) && r.IsActiveReversible).ToList();
        var report = await _recovery.RollbackManyAsync(records, ct).ConfigureAwait(false);
        _session = null;
        try { File.Delete(_paths.TradingSessionFile); }
        catch (IOException ex) { _logger.LogWarning(ex, "Could not delete trading session file"); }
        return report;
    }

    private Task PersistSessionAsync(CancellationToken ct) => AtomicJsonFile.WriteAsync(_paths.TradingSessionFile, _session, ct);

    private void SetState(TradingModeState state)
    {
        State = state;
        _flag.Set(IsActive);
        StateChanged?.Invoke(this, state);
    }

    private void StartMonitor()
    {
        _monitorCts = new CancellationTokenSource();
        var token = _monitorCts.Token;
        _monitor = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(_settings.Current.TradingMonitorIntervalSeconds, 5, 60)));
            try
            {
                UpdateSnapshot();
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    try { UpdateSnapshot(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Trading Mode monitor tick failed"); }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private async Task StopMonitorAsync()
    {
        if (_monitorCts is null) return;
        await _monitorCts.CancelAsync().ConfigureAwait(false);
        if (_monitor is not null)
        {
            try { await _monitor.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _monitorCts.Dispose();
        _monitorCts = null;
    }

    internal TradingSnapshot UpdateSnapshot()
    {
        var processes = _processes.GetProcesses();
        var matcher = CreateMatcher();
        var statuses = _settings.Current.ProtectedApplications.Where(a => a.Enabled).Select(app =>
        {
            var single = new ProtectedApplicationMatcher([app]);
            var own = processes.Where(p => single.IsProtected(p.Name, p.Path)).ToList();
            return new ProtectedAppStatus(app, own.Count > 0, own.Count, Math.Round(own.Sum(p => p.CpuPercent), 1), own.Sum(p => p.PrivateBytes > 0 ? p.PrivateBytes : p.WorkingSetBytes));
        }).ToList();

        var browsers = processes.Where(p => ProcessCatalog.IsBrowser(p.Name)).ToList();
        var recent = _metrics.History.GetSince(DateTimeOffset.Now.AddSeconds(-30));
        var (cpuLevel, memLevel, cpuSevere, memSevere) = _pressure.Evaluate(recent);
        var totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; // physical memory visible to this process
        var suggestions = ProcessAnalysis.CloseCandidates(processes.Where(p => !matcher.IsProtected(p.Name, p.Path)).ToList(), totalMemory);

        if (cpuSevere) NotifyOnce("cpu", "The processor is under heavy load. Consider closing apps you are not using — TICKSHIFT will not close anything for you.");
        if (memSevere) NotifyOnce("memory", "Memory is nearly full. Closing unused browser tabs or apps will help keep your trading apps responsive.");

        var snapshot = new TradingSnapshot(DateTimeOffset.Now, _metrics.Latest?.CpuPercent ?? 0, _metrics.Latest?.MemoryPercent ?? 0, cpuLevel, memLevel,
            statuses, browsers.Count, Math.Round(browsers.Sum(b => b.CpuPercent), 1), browsers.Sum(b => b.PrivateBytes), suggestions, _metrics.Latest?.TemperatureCelsius);
        Latest = snapshot;
        SnapshotUpdated?.Invoke(this, snapshot);
        return snapshot;
    }

    private void NotifyOnce(string key, string body)
    {
        if (!_settings.Current.Notifications.TradingMode) return;
        var now = DateTimeOffset.Now;
        if (_lastNotified.TryGetValue(key, out var last) && now - last < NotificationCooldown) return;
        _lastNotified[key] = now;
        _notifications.Notify(new NotificationMessage("Trading Mode", body, NotificationKind.Warning, "trading"));
    }

    public async ValueTask DisposeAsync() => await StopMonitorAsync().ConfigureAwait(false);
}
