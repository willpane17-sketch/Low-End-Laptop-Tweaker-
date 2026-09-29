using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Diagnostics.Hardware;
using Tickshift.Diagnostics.Monitoring;
using Tickshift.Diagnostics.Recommendations;
using Tickshift.Diagnostics.Thermal;

namespace Tickshift.Diagnostics;

public sealed record DiagnosticProgress(string Stage, double Percent);

public sealed record DiagnosticOptions(TimeSpan ObservationWindow, bool IncludeBrowsers = true, bool IncludeTempScan = true)
{
    public static DiagnosticOptions Default { get; } = new(TimeSpan.FromSeconds(8));
    public static DiagnosticOptions Quick { get; } = new(TimeSpan.FromSeconds(4), IncludeTempScan: false);
}

/// <summary>
/// Performs a genuine scan: reads hardware, observes live load for a short window, inspects startup
/// entries, browsers and temporary files, then runs the recommendation rules. Optional sources that
/// fail are recorded as limitations — the scan never crashes because one provider is unavailable.
/// </summary>
public sealed class DiagnosticEngine
{
    private readonly ISystemInfoProvider _info;
    private readonly IMetricsSource _metrics;
    private readonly IProcessProvider _processes;
    private readonly IStartupProvider _startup;
    private readonly IBrowserProvider _browsers;
    private readonly IVisualEffectsManager _visuals;
    private readonly RecommendationEngine _recommendations;
    private readonly ITradingSessionState _trading;
    private readonly ILogger _logger;

    public DiagnosticEngine(
        ISystemInfoProvider info,
        IMetricsSource metrics,
        IProcessProvider processes,
        IStartupProvider startup,
        IBrowserProvider browsers,
        IVisualEffectsManager visuals,
        RecommendationEngine recommendations,
        ITradingSessionState trading,
        ILogger<DiagnosticEngine>? logger = null)
    {
        _info = info;
        _metrics = metrics;
        _processes = processes;
        _startup = startup;
        _browsers = browsers;
        _visuals = visuals;
        _recommendations = recommendations;
        _trading = trading;
        _logger = logger ?? NullLogger<DiagnosticEngine>.Instance;
    }

    public DiagnosticReport? LastReport { get; private set; }
    public event EventHandler<DiagnosticReport>? ReportCompleted;

    public async Task<DiagnosticReport> RunAsync(DiagnosticOptions options, IProgress<DiagnosticProgress>? progress, CancellationToken ct)
    {
        var limitations = new List<string>();
        void Report(string stage, double pct) => progress?.Report(new DiagnosticProgress(stage, pct));

        Report("Reading processor, memory and Windows version", 5);
        var cpu = await Safe(() => _info.GetCpuAsync(ct), CpuInfo.Unknown, "Processor details", limitations).ConfigureAwait(false);
        var memory = _info.GetMemory();
        var os = _info.GetOs();
        if (!os.IsSupported && os.BuildNumber > 0)
            limitations.Add($"Windows build {os.BuildNumber} is older than TICKSHIFT's supported minimum (Windows 10 1809). Some features may not work.");

        Report("Checking storage", 12);
        var storage = await Safe(() => _info.GetStorageAsync(ct), (IReadOnlyList<StorageVolume>)[], "Storage information", limitations).ConfigureAwait(false);
        if (storage.FirstOrDefault(s => s.IsSystemVolume) is { MediaKind: StorageMediaKind.Unknown })
            limitations.Add("The system drive type (SSD or HDD) was not reported by Windows.");

        Report("Checking power and temperature sensors", 20);
        var power = await Safe(() => _info.GetPowerAsync(ct), PowerStatus.Unknown, "Power status", limitations).ConfigureAwait(false);
        var thermal = await Safe(() => _metrics.RefreshThermalAsync(ct), ThermalSnapshot.Unavailable("None", "Not read."), "Temperature", limitations).ConfigureAwait(false);
        if (thermal.Availability != SensorAvailability.Available)
            limitations.Add("CPU temperature is not available on this device; temperature-based checks were skipped.");

        Report("Observing live system load", 28);
        SafeSync(() => _processes.GetProcesses(), (IReadOnlyList<ProcessSnapshot>)[], "Process list", limitations); // baseline for CPU deltas
        var observeTask = _metrics.CollectAsync(options.ObservationWindow, ct);
        var start = DateTimeOffset.Now;
        while (!observeTask.IsCompleted)
        {
            await Task.WhenAny(observeTask, Task.Delay(500, ct)).ConfigureAwait(false);
            var fraction = Math.Min(1, (DateTimeOffset.Now - start) / options.ObservationWindow);
            Report("Observing live system load", 28 + fraction * 40);
        }

        var samples = await observeTask.ConfigureAwait(false);
        var processes = SafeSync(() => _processes.GetProcesses(), (IReadOnlyList<ProcessSnapshot>)[], "Process list", limitations);

        Report("Reviewing startup applications", 72);
        var startup = SafeSync(() => _startup.Enumerate(), (IReadOnlyList<StartupEntry>)[], "Startup applications", limitations);
        var runningPaths = processes.Where(p => p.Path is not null).GroupBy(p => p.Path!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.WorkingSetBytes), StringComparer.OrdinalIgnoreCase);
        startup = startup.Select(e => e.ExecutablePath is not null && runningPaths.TryGetValue(e.ExecutablePath, out var mem) ? e with { RunningMemoryBytes = mem } : e).ToList();

        var browsers = new List<BrowserAnalysis>();
        if (options.IncludeBrowsers)
        {
            Report("Analysing browsers", 80);
            foreach (var kind in new[] { BrowserKind.Edge, BrowserKind.Chrome })
            {
                var analysis = SafeSync(() => _browsers.Analyse(kind, processes), (BrowserAnalysis?)null, $"{kind} analysis", limitations);
                if (analysis is { Installed: true }) browsers.Add(analysis);
            }
        }

        DiskUsage? temp = null;
        if (options.IncludeTempScan)
        {
            Report("Measuring temporary files", 88);
            temp = await Safe<DiskUsage?>(async () => await DiskUsageScanner.ScanAsync(Path.GetTempPath(), TimeSpan.FromDays(2), TimeSpan.FromSeconds(4), ct).ConfigureAwait(false), null, "Temporary files", limitations).ConfigureAwait(false);
        }

        var visuals = SafeSync(() => new VisualEffectsSummary(_visuals.GetCurrent().AnyEnabled), (VisualEffectsSummary?)null, "Visual effects", limitations);

        Report("Building recommendations", 95);
        var snapshot = new SystemSnapshot(DateTimeOffset.Now, cpu, memory, storage, os, power, thermal, _info.GetUptime(),
            processes, startup, browsers, temp, visuals);
        var hardware = HardwareInterpreter.Interpret(cpu, memory, storage);
        var observation = ObservationSummary.From(samples, _info.GetMemory(), processes);
        var limitation = HardwareInterpreter.AssessLimitation(observation, hardware, thermal);
        var thermalAssessment = ThermalAnalyzer.Assess(thermal, hardware.Cpu, samples, power, processes);
        var condition = SystemConditionEvaluator.Evaluate(samples, memory, snapshot.SystemDrive, thermalAssessment.State);

        var history = _metrics.History.GetSince(DateTimeOffset.Now.AddMinutes(-15));
        var recommendations = _recommendations.Evaluate(new RecommendationContext(snapshot, hardware, observation, history, thermalAssessment.State, _trading.IsActive));

        var report = new DiagnosticReport(Guid.NewGuid(), DateTimeOffset.Now, snapshot, hardware, observation, limitation, condition, recommendations, limitations);
        LastReport = report;
        Report("Done", 100);
        ReportCompleted?.Invoke(this, report);
        _logger.LogInformation("Diagnostic scan finished: {Count} recommendations, condition {Condition}, primary limitation {Limitation}",
            recommendations.Count, condition.Condition, limitation.Primary);
        return report;
    }

    private async Task<T> Safe<T>(Func<Task<T>> action, T fallback, string what, List<string> limitations)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{What} unavailable during scan", what);
            limitations.Add($"{what} could not be read ({Core.Errors.ErrorClassifier.FriendlyMessage(Core.Errors.ErrorClassifier.Classify(ex))}).");
            return fallback;
        }
    }

    private T SafeSync<T>(Func<T> action, T fallback, string what, List<string> limitations)
    {
        try
        {
            return action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{What} unavailable during scan", what);
            limitations.Add($"{what} could not be read.");
            return fallback;
        }
    }
}
