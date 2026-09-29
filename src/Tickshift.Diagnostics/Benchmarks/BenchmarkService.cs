using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Diagnostics.Monitoring;

namespace Tickshift.Diagnostics.Benchmarks;

public sealed record BenchmarkMeasurement(string Key, string Label, double? Value, string Unit, bool LowerIsBetter, double NoiseBand);

public sealed record BenchmarkResult(
    Guid Id,
    DateTimeOffset CapturedAt,
    TimeSpan Duration,
    string Label,
    IReadOnlyList<BenchmarkMeasurement> Measurements,
    BenchmarkConditions Conditions)
{
    public BenchmarkMeasurement? Get(string key) => Measurements.FirstOrDefault(m => m.Key == key);
}

/// <summary>Conditions recorded so we can warn when before/after runs are not like-for-like.</summary>
public sealed record BenchmarkConditions(PowerSource PowerSource, int ProcessCount, bool BrowserRunning, bool TemperatureAvailable);

public enum DeltaAssessment { Improved, Worse, Unchanged, NotComparable }

public sealed record BenchmarkDelta(string Key, string Label, double? Before, double? After, string Unit, double? Change, DeltaAssessment Assessment);

public sealed record BenchmarkComparison(BenchmarkResult Before, BenchmarkResult After, IReadOnlyList<BenchmarkDelta> Deltas, IReadOnlyList<string> Caveats);

/// <summary>
/// Transparent before/after measurement. Everything is a real reading over a fixed observation window;
/// no synthetic scores and no "X% faster" claims. Differences inside a metric's noise band are
/// reported as unchanged.
/// </summary>
public sealed class BenchmarkService
{
    public const string IdleCpu = "cpu.avg";
    public const string CpuP95 = "cpu.p95";
    public const string AvailableMemory = "memory.available";
    public const string MemoryLoad = "memory.load";
    public const string CommitCharge = "memory.commit";
    public const string StartupApps = "startup.enabled";
    public const string DiskFree = "disk.free";
    public const string DiskActive = "disk.active";
    public const string BrowserMemory = "browser.memory";
    public const string BrowserProcesses = "browser.processes";
    public const string ProcessCount = "processes.count";
    public const string Temperature = "thermal.avg";

    public static readonly IReadOnlyList<string> StandardCaveats =
    [
        "Measurements reflect this computer at the moment they were taken. Background activity (updates, antivirus scans, sync), open apps, room temperature and power source all influence results.",
        "Lower memory use does not by itself mean better performance; Windows deliberately uses spare memory for caching.",
        "For the most meaningful comparison, run both measurements with the same apps open and on the same power source.",
    ];

    private readonly IMetricsSource _metrics;
    private readonly ISystemInfoProvider _info;
    private readonly IProcessProvider _processes;
    private readonly IStartupProvider _startup;

    public BenchmarkService(IMetricsSource metrics, ISystemInfoProvider info, IProcessProvider processes, IStartupProvider startup)
    {
        _metrics = metrics;
        _info = info;
        _processes = processes;
        _startup = startup;
    }

    public async Task<BenchmarkResult> CaptureAsync(string label, TimeSpan window, CancellationToken ct)
    {
        _processes.GetProcesses(); // baseline for per-process CPU
        var samples = await _metrics.CollectAsync(window, ct).ConfigureAwait(false);
        var processes = _processes.GetProcesses();
        var memory = _info.GetMemory();
        var power = await _info.GetPowerAsync(ct).ConfigureAwait(false);
        var storage = await _info.GetStorageAsync(ct).ConfigureAwait(false);
        int? startupCount = null;
        try { startupCount = _startup.Enumerate().Count(e => e.IsEnabled); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        var browserProcs = processes.Where(p => p.Category == ProcessCategory.Browser).ToList();
        var system = storage.FirstOrDefault(s => s.IsSystemVolume);
        var disk = samples.Where(s => s.DiskActivePercent is not null).Select(s => s.DiskActivePercent!.Value).ToList();
        var temps = samples.Where(s => s.TemperatureCelsius is not null).Select(s => s.TemperatureCelsius!.Value).ToList();

        var measurements = new List<BenchmarkMeasurement>
        {
            new(IdleCpu, "Average processor use", samples.Count > 0 ? Math.Round(samples.Average(s => s.CpuPercent), 1) : null, "%", true, 3),
            new(CpuP95, "Processor use (95th percentile)", samples.Count > 0 ? Math.Round(SampleStatistics.Percentile(samples.Select(s => s.CpuPercent), 95), 1) : null, "%", true, 5),
            new(AvailableMemory, "Available memory", Math.Round(memory.AvailablePhysicalBytes / 1024d / 1024, 0), "MB", false, 100),
            new(MemoryLoad, "Memory in use", samples.Count > 0 ? Math.Round(samples.Average(s => s.MemoryPercent), 1) : memory.MemoryLoadPercent, "%", true, 2),
            new(CommitCharge, "Commit charge (paging pressure)", Math.Round(memory.CommitPercent, 1), "%", true, 2),
            new(StartupApps, "Enabled startup apps", startupCount, "apps", true, 0),
            new(DiskFree, "Free space on system drive", system is null ? null : Math.Round(system.FreeBytes / 1024d / 1024 / 1024, 2), "GB", false, 0.05),
            new(DiskActive, "Disk busy time", disk.Count > 0 ? Math.Round(disk.Average(), 1) : null, "%", true, 5),
            new(BrowserMemory, "Browser memory", browserProcs.Count > 0 ? Math.Round(browserProcs.Sum(p => p.PrivateBytes) / 1024d / 1024, 0) : 0, "MB", true, 75),
            new(BrowserProcesses, "Browser processes", browserProcs.Count, "processes", true, 0),
            new(ProcessCount, "Running processes", processes.Count, "processes", true, 3),
            new(Temperature, "Average temperature", temps.Count > 0 ? Math.Round(temps.Average(), 1) : null, "°C", true, 2),
        };

        var conditions = new BenchmarkConditions(power.Source, processes.Count, browserProcs.Count > 0, temps.Count > 0);
        var duration = samples.Count > 1 ? samples[^1].Timestamp - samples[0].Timestamp : window;
        return new BenchmarkResult(Guid.NewGuid(), DateTimeOffset.Now, duration, label, measurements, conditions);
    }

    public static BenchmarkComparison Compare(BenchmarkResult before, BenchmarkResult after)
    {
        var caveats = new List<string>(StandardCaveats);
        var powerChanged = before.Conditions.PowerSource != after.Conditions.PowerSource;
        if (powerChanged)
            caveats.Insert(0, "The power source changed between measurements, so processor and temperature figures are not directly comparable.");
        if (before.Conditions.BrowserRunning != after.Conditions.BrowserRunning)
            caveats.Insert(0, "A browser was running during only one of the measurements.");

        var deltas = new List<BenchmarkDelta>();
        foreach (var b in before.Measurements)
        {
            var a = after.Get(b.Key);
            if (a is null) continue;
            if (b.Value is null || a.Value is null)
            {
                deltas.Add(new BenchmarkDelta(b.Key, b.Label, b.Value, a.Value, b.Unit, null, DeltaAssessment.NotComparable));
                continue;
            }

            var change = Math.Round(a.Value.Value - b.Value.Value, 2);
            var notComparable = powerChanged && b.Key is IdleCpu or CpuP95 or Temperature;
            var assessment = notComparable ? DeltaAssessment.NotComparable
                : Math.Abs(change) <= b.NoiseBand ? DeltaAssessment.Unchanged
                : (change < 0) == b.LowerIsBetter ? DeltaAssessment.Improved
                : DeltaAssessment.Worse;
            deltas.Add(new BenchmarkDelta(b.Key, b.Label, b.Value, a.Value, b.Unit, change, assessment));
        }

        return new BenchmarkComparison(before, after, deltas, caveats);
    }
}
