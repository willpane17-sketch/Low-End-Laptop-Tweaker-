using Tickshift.Core.Models;
using Tickshift.Diagnostics.Hardware;
using Tickshift.Diagnostics.Monitoring;

namespace Tickshift.Diagnostics;

public sealed record ProcessUsage(string Name, int Instances, double CpuPercent, long MemoryBytes, ProcessCategory Category);

/// <summary>Measurements from an observation window. Nulls mean "not measurable", never zero.</summary>
public sealed record ObservationSummary(
    int SampleCount,
    TimeSpan Duration,
    double AverageCpu,
    double P95Cpu,
    double PeakCpu,
    double AverageMemory,
    double PeakMemory,
    double CommitPercent,
    double? AverageDiskActive,
    double? AverageTemperature,
    double? MaxTemperature,
    IReadOnlyList<ProcessUsage> TopByCpu,
    IReadOnlyList<ProcessUsage> TopByMemory)
{
    public static ObservationSummary From(IReadOnlyList<MetricSample> samples, MemoryStatus memory, IReadOnlyList<ProcessSnapshot> processes)
    {
        var grouped = ProcessAnalysis.GroupByName(processes);
        if (samples.Count == 0)
        {
            return new ObservationSummary(0, TimeSpan.Zero, 0, 0, 0, memory.MemoryLoadPercent, memory.MemoryLoadPercent, memory.CommitPercent,
                null, null, null, grouped.OrderByDescending(g => g.CpuPercent).Take(5).ToList(), grouped.OrderByDescending(g => g.MemoryBytes).Take(5).ToList());
        }

        var disk = samples.Where(s => s.DiskActivePercent is not null).Select(s => s.DiskActivePercent!.Value).ToList();
        var temps = samples.Where(s => s.TemperatureCelsius is not null).Select(s => s.TemperatureCelsius!.Value).ToList();
        return new ObservationSummary(
            samples.Count,
            samples[^1].Timestamp - samples[0].Timestamp,
            Math.Round(samples.Average(s => s.CpuPercent), 1),
            Math.Round(SampleStatistics.Percentile(samples.Select(s => s.CpuPercent), 95), 1),
            samples.Max(s => s.CpuPercent),
            Math.Round(samples.Average(s => s.MemoryPercent), 1),
            samples.Max(s => s.MemoryPercent),
            Math.Round(memory.CommitPercent, 1),
            disk.Count > 0 ? Math.Round(disk.Average(), 1) : null,
            temps.Count > 0 ? Math.Round(temps.Average(), 1) : null,
            temps.Count > 0 ? temps.Max() : null,
            grouped.OrderByDescending(g => g.CpuPercent).Take(5).ToList(),
            grouped.OrderByDescending(g => g.MemoryBytes).Take(5).ToList());
    }
}

/// <summary>Everything collected by one diagnostic scan.</summary>
public sealed record SystemSnapshot(
    DateTimeOffset CapturedAt,
    CpuInfo Cpu,
    MemoryStatus Memory,
    IReadOnlyList<StorageVolume> Storage,
    OsInfo Os,
    PowerStatus Power,
    ThermalSnapshot Thermal,
    TimeSpan Uptime,
    IReadOnlyList<ProcessSnapshot> Processes,
    IReadOnlyList<StartupEntry> StartupEntries,
    IReadOnlyList<BrowserAnalysis> Browsers,
    DiskUsage? TempFiles,
    VisualEffectsSummary? VisualEffects)
{
    public StorageVolume? SystemDrive => Storage.FirstOrDefault(v => v.IsSystemVolume);
    public int EnabledThirdPartyStartupCount => StartupEntries.Count(e => e.IsEnabled && !e.IsMicrosoftComponent);
}

public sealed record VisualEffectsSummary(bool AnyAnimationsEnabled);

public enum RecommendationActionKind { None, Navigate, RunOptimisation, OpenSettings, OpenBrowserPage }

/// <summary>
/// An explained recommendation. Every one says what was measured (<see cref="Evidence"/>), why it
/// matters, what to do, how risky it is and whether it can be undone. No invented percentages.
/// </summary>
public sealed record Recommendation
{
    public required string RuleId { get; init; }
    public required string Issue { get; init; }
    public required string Impact { get; init; }
    public required string SuggestedAction { get; init; }
    public required string Evidence { get; init; }
    public RiskLevel Risk { get; init; } = RiskLevel.Low;
    public Reversibility Reversible { get; init; } = Reversibility.FullyReversible;
    public Severity Severity { get; init; } = Severity.Low;

    /// <summary>0–1: how strongly the measurements support this recommendation.</summary>
    public double Relevance { get; init; }

    public bool IsHardwareLimitation { get; init; }

    /// <summary>True for disruptive maintenance that is postponed while Trading Mode is active.</summary>
    public bool Disruptive { get; init; }
    public bool DeferredForTrading { get; init; }

    public RecommendationActionKind ActionKind { get; init; }
    public string? ActionTarget { get; init; }
    public string? ActionLabel { get; init; }

    /// <summary>Ranking: measured relevance first, then safety, then reversibility.</summary>
    public double Score =>
        Relevance * 0.6
        + (Risk switch { RiskLevel.Low => 1.0, RiskLevel.Moderate => 0.6, _ => 0.2 }) * 0.25
        + (Reversible switch { Reversibility.FullyReversible => 1.0, Reversibility.PartiallyReversible => 0.6, _ => 0.3 }) * 0.15
        - (DeferredForTrading ? 0.5 : 0);

    public string RiskText => Risk switch
    {
        RiskLevel.Low => "Low",
        RiskLevel.Moderate => "Moderate",
        _ => "Elevated",
    };

    public string ReversibleText => Reversible switch
    {
        Reversibility.FullyReversible => "Yes",
        Reversibility.PartiallyReversible => "Partly",
        _ => "No",
    };
}

public sealed record DiagnosticReport(
    Guid Id,
    DateTimeOffset CreatedAt,
    SystemSnapshot Snapshot,
    HardwareProfile Hardware,
    ObservationSummary Observation,
    LimitationAssessment Limitation,
    ConditionResult Condition,
    IReadOnlyList<Recommendation> Recommendations,
    IReadOnlyList<string> Limitations);

public static class ProcessAnalysis
{
    public static IReadOnlyList<ProcessUsage> GroupByName(IReadOnlyList<ProcessSnapshot> processes) =>
        processes
            .Where(p => p.Category is not ProcessCategory.Self && p.Pid > 4)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessUsage(
                g.First().Name,
                g.Count(),
                Math.Round(g.Sum(p => p.CpuPercent), 1),
                g.Sum(p => p.PrivateBytes > 0 ? p.PrivateBytes : p.WorkingSetBytes),
                g.First().Category))
            .ToList();

    /// <summary>Heavy, user-closeable applications — candidates to suggest (never to close automatically).</summary>
    public static IReadOnlyList<ProcessUsage> CloseCandidates(IReadOnlyList<ProcessSnapshot> processes, long totalMemory) =>
        GroupByName(processes.Where(p => p.HasVisibleWindow && p.Category == ProcessCategory.Application).ToList())
            .Where(g => g.CpuPercent >= 10 || g.MemoryBytes >= Math.Max(300L * 1024 * 1024, totalMemory / 12))
            .OrderByDescending(g => g.CpuPercent * 20 + g.MemoryBytes / (1024.0 * 1024))
            .Take(6)
            .ToList();
}
