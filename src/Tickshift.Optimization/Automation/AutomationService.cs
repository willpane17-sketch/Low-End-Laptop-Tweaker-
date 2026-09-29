using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;
using Tickshift.Diagnostics;
using Tickshift.Diagnostics.Hardware;
using Tickshift.Diagnostics.Monitoring;
using Tickshift.Diagnostics.Thermal;
using Tickshift.Persistence;

namespace Tickshift.Optimization.Automation;

public sealed record AutomationEvent(DateTimeOffset At, string Kind, string Message, bool ActionTaken);

public sealed record AutomationState
{
    public DateOnly? LastDailyDiagnostic { get; init; }
    public DateTimeOffset? LastStorageCheck { get; init; }
    public Dictionary<string, DateTimeOffset> LastAlerts { get; init; } = new();
}

/// <summary>Pure decision logic, separated from timers so it can be unit-tested.</summary>
public static class AutomationRules
{
    public static bool IsSustained(IReadOnlyList<MetricSample> window, Func<MetricSample, double> selector, double threshold, TimeSpan required, double fraction = 0.8)
    {
        if (window.Count < 3) return false;
        var span = window[^1].Timestamp - window[0].Timestamp;
        if (span < required * 0.9) return false;
        return SampleStatistics.FractionAtOrAbove(window, selector, threshold) >= fraction;
    }

    public static bool CooledDown(AutomationState state, string key, DateTimeOffset now, TimeSpan cooldown) =>
        !state.LastAlerts.TryGetValue(key, out var last) || now - last >= cooldown;

    public static bool DailyDiagnosticDue(AutomationState state, TimeOnly scheduled, DateTimeOffset now) =>
        state.LastDailyDiagnostic != DateOnly.FromDateTime(now.LocalDateTime) && TimeOnly.FromDateTime(now.LocalDateTime) >= scheduled;

    /// <summary>
    /// Only individually approved, low-risk, fully reversible, non-administrator optimisations may run
    /// unattended. Anything else in the approved list is ignored.
    /// </summary>
    public static IReadOnlyList<string> FilterUnattended(IEnumerable<IOptimisation> catalogue, IEnumerable<string> approvedIds)
    {
        var approved = approvedIds.ToHashSet(StringComparer.Ordinal);
        return catalogue.Where(o => approved.Contains(o.Id) && o.SuitableForUnattended && OptimisationEngine.Allowlist.Contains(o.Id))
            .Select(o => o.Id).ToList();
    }
}

/// <summary>
/// Optional background automation. Defaults to monitoring and recommendations; it only applies
/// changes that the user individually approved for unattended use, never during Trading Mode, and
/// never closes applications. One timer ticking once a minute; it reads the shared metrics history
/// instead of polling hardware itself.
/// </summary>
public sealed class AutomationService : IAsyncDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StorageInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan LoginDelay = TimeSpan.FromMinutes(2);

    private readonly ISettingsStore _settings;
    private readonly IMetricsSource _metrics;
    private readonly ISystemInfoProvider _info;
    private readonly DiagnosticEngine _diagnostics;
    private readonly OptimisationEngine _engine;
    private readonly ITradingSessionState _trading;
    private readonly INotificationService _notifications;
    private readonly RecordStore<AutomationEvent> _events;
    private readonly AppDataPaths _paths;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private AutomationState _state;
    private CpuModel? _cpuModel;

    public AutomationService(ISettingsStore settings, IMetricsSource metrics, ISystemInfoProvider info, DiagnosticEngine diagnostics,
        OptimisationEngine engine, ITradingSessionState trading, INotificationService notifications, RecordStore<AutomationEvent> events,
        AppDataPaths paths, IClock clock, ILogger<AutomationService>? logger = null)
    {
        _settings = settings;
        _metrics = metrics;
        _info = info;
        _diagnostics = diagnostics;
        _engine = engine;
        _trading = trading;
        _notifications = notifications;
        _events = events;
        _paths = paths;
        _clock = clock;
        _logger = logger ?? NullLogger<AutomationService>.Instance;
        _state = AtomicJsonFile.Read<AutomationState>(paths.StateFile).Value ?? new AutomationState();
    }

    public IReadOnlyList<AutomationEvent> History => _events.GetAll();

    public void Start(bool launchedAtLogin)
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(launchedAtLogin, _cts.Token));
    }

    private async Task RunAsync(bool launchedAtLogin, CancellationToken ct)
    {
        try
        {
            if (launchedAtLogin) await LoginOptimiseAsync(ct).ConfigureAwait(false);

            using var timer = new PeriodicTimer(Tick);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await EvaluateAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Automation tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool Active(out AutomationSettings a)
    {
        a = _settings.Current.Automation;
        return a.Enabled && !a.Paused;
    }

    private async Task LoginOptimiseAsync(CancellationToken ct)
    {
        if (!Active(out var a) || !a.OptimiseAtLogin) return;
        await Task.Delay(LoginDelay, ct).ConfigureAwait(false); // let sign-in settle first
        if (_trading.IsActive)
        {
            await LogAsync("login-optimise", "Skipped login optimisation because Trading Mode is active.", false).ConfigureAwait(false);
            return;
        }

        var ids = AutomationRules.FilterUnattended(_engine.Catalogue, a.ApprovedUnattendedOptimisations);
        if (ids.Count == 0)
        {
            await LogAsync("login-optimise", "No optimisations are approved for unattended use; nothing was changed.", false).ConfigureAwait(false);
            return;
        }

        var run = await _engine.RunDirectAsync(ids, new ExecutionOptions(_settings.Current.DryRunMode, false, false, TimeSpan.Zero, "Automation (login)"), ct).ConfigureAwait(false);
        var message = run.DryRun
            ? $"Dry run at login: {run.Items.Count(i => i.Status == RunItemStatus.DryRun)} change(s) would be applied."
            : $"Login optimisation: {run.VerifiedCount} verified, {run.FailedCount} not verified or failed.";
        await LogAsync("login-optimise", message, run.VerifiedCount > 0).ConfigureAwait(false);
        if (run.VerifiedCount > 0)
            _notifications.Notify(new NotificationMessage(Brand.ProductName, message, NotificationKind.Success, "automation"));
    }

    internal async Task EvaluateAsync(CancellationToken ct)
    {
        if (!Active(out var a)) return;
        var now = _clock.Now;
        var window = _metrics.History.GetSince(now - TimeSpan.FromMinutes(a.SustainedMinutes));

        if (a.MemoryPressureAlerts && AutomationRules.IsSustained(window, s => s.MemoryPercent, a.MemoryPressureThresholdPercent, TimeSpan.FromMinutes(a.SustainedMinutes))
            && AutomationRules.CooledDown(_state, "memory", now, AlertCooldown))
        {
            await AlertAsync("memory", $"Memory has been above {a.MemoryPressureThresholdPercent}% for {a.SustainedMinutes} minutes. Closing unused apps or browser tabs will help.", now).ConfigureAwait(false);
        }

        if (a.BottleneckDetection && AutomationRules.IsSustained(window, s => s.CpuPercent, a.CpuBottleneckPercent, TimeSpan.FromMinutes(a.SustainedMinutes))
            && AutomationRules.CooledDown(_state, "cpu", now, AlertCooldown))
        {
            await AlertAsync("cpu", $"The processor has been above {a.CpuBottleneckPercent}% for {a.SustainedMinutes} minutes. Open Performance Monitor to see what is using it.", now).ConfigureAwait(false);
        }

        if (a.ThermalMonitoring && _metrics.LatestThermal?.PrimaryCelsius is { } temp)
        {
            _cpuModel ??= CpuModelParser.Parse((await _info.GetCpuAsync(ct).ConfigureAwait(false)).Name);
            var thresholds = ThermalAnalyzer.ThresholdsFor(_cpuModel, _metrics.LatestThermal.Primary?.Kind);
            var recent = _metrics.History.GetSince(now.AddMinutes(-3));
            if (recent.Count >= 3 && recent.All(s => s.TemperatureCelsius is null || s.TemperatureCelsius >= thresholds.High)
                && AutomationRules.CooledDown(_state, "thermal", now, AlertCooldown))
            {
                await AlertAsync("thermal", $"Your laptop has been running hot ({temp:0} °C) for a few minutes. Reducing background work and keeping vents clear will help.", now).ConfigureAwait(false);
            }
        }

        if (a.StorageReminder && (_state.LastStorageCheck is null || now - _state.LastStorageCheck >= StorageInterval))
        {
            _state = _state with { LastStorageCheck = now };
            var system = (await _info.GetStorageAsync(ct).ConfigureAwait(false)).FirstOrDefault(v => v.IsSystemVolume);
            if (system is not null && system.FreePercent < a.LowStoragePercent && AutomationRules.CooledDown(_state, "storage", now, TimeSpan.FromHours(12)))
                await AlertAsync("storage", $"Only {Format.Bytes(system.FreeBytes)} free on {system.RootPath}. Open System Cleanup to free space safely.", now).ConfigureAwait(false);
            await SaveStateAsync().ConfigureAwait(false);
        }

        if (a.DailyDiagnostic && !_trading.IsActive && AutomationRules.DailyDiagnosticDue(_state, a.DailyDiagnosticTime, now))
        {
            _state = _state with { LastDailyDiagnostic = DateOnly.FromDateTime(now.LocalDateTime) };
            await SaveStateAsync().ConfigureAwait(false);
            var report = await _diagnostics.RunAsync(DiagnosticOptions.Quick, null, ct).ConfigureAwait(false);
            var important = report.Recommendations.Where(r => r.Severity >= Severity.Medium && !r.DeferredForTrading)
                .Where(r => a.BrowserRecommendations || !r.RuleId.StartsWith("browser.", StringComparison.Ordinal)).ToList();
            var summary = important.Count == 0 ? "Daily check: no significant issues found." : $"Daily check: {important.Count} recommendation(s), top: {important[0].Issue}.";
            await LogAsync("daily-diagnostic", summary, false).ConfigureAwait(false);
            if (important.Count > 0)
                _notifications.Notify(new NotificationMessage(Brand.ProductName, summary, NotificationKind.Info, "recommendations"));
        }
    }

    private async Task AlertAsync(string key, string message, DateTimeOffset now)
    {
        var alerts = new Dictionary<string, DateTimeOffset>(_state.LastAlerts) { [key] = now };
        _state = _state with { LastAlerts = alerts };
        await SaveStateAsync().ConfigureAwait(false);
        var category = key switch { "memory" or "cpu" => "memory", "thermal" => "thermal", "storage" => "storage", _ => "general" };
        var shown = _notifications.Notify(new NotificationMessage(Brand.ProductName, message, NotificationKind.Warning, category));
        await LogAsync(key, message + (shown ? string.Empty : " (notification suppressed by your settings)"), false).ConfigureAwait(false);
    }

    private Task LogAsync(string kind, string message, bool actionTaken) =>
        _events.AppendAsync(new AutomationEvent(_clock.Now, kind, message, actionTaken));

    private Task SaveStateAsync() => AtomicJsonFile.WriteAsync(_paths.StateFile, _state);

    public async ValueTask DisposeAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return; // already disposed (safe to call twice)
        await cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        cts.Dispose();
    }
}
