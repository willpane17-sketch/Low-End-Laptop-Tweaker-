using Tickshift.Core;
using Tickshift.Core.Models;

namespace Tickshift.Diagnostics.Hardware;

public enum MemoryTier { Unknown, VeryLow, Low, Adequate, Plenty }

public enum PrimaryLimitation { None, Processor, Memory, Storage, Thermal, Undetermined }

public sealed record HardwareInsight(
    string Title,
    string Explanation,
    Severity Severity,
    bool IsHardwareLimitation,
    string? UpgradeSuggestion = null);

public sealed record LimitationAssessment(PrimaryLimitation Primary, string Explanation, IReadOnlyDictionary<PrimaryLimitation, double> Scores);

public sealed record HardwareProfile(
    CpuModel Cpu,
    int PhysicalCores,
    int LogicalProcessors,
    MemoryTier Memory,
    double MemoryGiB,
    StorageMediaKind SystemDriveMedia,
    string? SystemDriveBus,
    IReadOnlyList<HardwareInsight> Insights);

/// <summary>Converts raw hardware facts into plain-English, model-specific guidance.</summary>
public static class HardwareInterpreter
{
    public static MemoryTier ClassifyMemory(double gib) => gib switch
    {
        <= 0 => MemoryTier.Unknown,
        < 4.5 => MemoryTier.VeryLow,
        < 8.5 => MemoryTier.Low,
        < 16.5 => MemoryTier.Adequate,
        _ => MemoryTier.Plenty,
    };

    public static HardwareProfile Interpret(CpuInfo cpu, MemoryStatus memory, IReadOnlyList<StorageVolume> storage)
    {
        var model = CpuModelParser.Parse(cpu.Name);
        var gib = memory.TotalGiB;
        var tier = ClassifyMemory(gib);
        var systemDrive = storage.FirstOrDefault(v => v.IsSystemVolume);
        var insights = new List<HardwareInsight>();

        switch (tier)
        {
            case MemoryTier.VeryLow:
                insights.Add(new HardwareInsight(
                    $"{gib:0.#} GB of memory is very limited",
                    "Windows plus a modern browser can use most of 4 GB on their own. When memory runs out, Windows moves data to the disk, " +
                    "which makes everything feel sluggish. Keeping startup apps and browser tabs to a minimum helps the most.",
                    Severity.High, true,
                    "If your laptop has a free or replaceable memory slot, upgrading to 8 GB is usually the most effective improvement."));
                break;
            case MemoryTier.Low:
                insights.Add(new HardwareInsight(
                    $"{gib:0.#} GB of memory is workable",
                    "8 GB is enough for trading platforms and a browser, but many open tabs or heavy extensions can still fill it.",
                    Severity.Low, false));
                break;
        }

        if (systemDrive is not null)
        {
            switch (systemDrive.MediaKind)
            {
                case StorageMediaKind.Rotational:
                    insights.Add(new HardwareInsight(
                        "Windows is installed on a mechanical hard drive (HDD)",
                        "Hard drives are dramatically slower than solid-state drives at the small, scattered reads Windows and browsers do constantly. " +
                        "This affects start-up, opening apps and behaviour under memory pressure. Software optimisation cannot reproduce the difference.",
                        Severity.High, true,
                        "Replacing the hard drive with an SSD is often the single biggest responsiveness upgrade for an older laptop."));
                    break;
                case StorageMediaKind.SolidState when systemDrive.BusType is "MMC" or "SD":
                    insights.Add(new HardwareInsight(
                        "Windows is installed on eMMC storage",
                        "eMMC is solid-state but considerably slower than a SATA or NVMe SSD, which can make updates and app launches slow.",
                        Severity.Medium, true));
                    break;
                case StorageMediaKind.Unknown:
                    insights.Add(new HardwareInsight(
                        "Drive type could not be confirmed",
                        "Windows did not report whether the system drive is an SSD or a hard drive, so TICKSHIFT does not assume either.",
                        Severity.Info, false));
                    break;
            }
        }

        var cores = cpu.PhysicalCores > 0 ? cpu.PhysicalCores : (int?)null;
        if (model.Segment == CpuSegment.LowPowerMobile)
        {
            var coreText = cores is { } c ? $"{c}-core" : "multi-core";
            insights.Add(new HardwareInsight(
                $"{model.Describe()}",
                $"This is a {coreText}, low-power processor designed for battery life. It handles trading platforms well, but sustained heavy work " +
                "(many chart tabs, video, updates running in the background) can keep it busy and warm, and it may reduce its speed to stay cool.",
                cores is <= 2 ? Severity.Medium : Severity.Info, cores is <= 2));
        }
        else if (model.Segment != CpuSegment.Unknown)
        {
            insights.Add(new HardwareInsight(model.Describe(),
                cores is { } c ? $"{c} physical cores and {cpu.LogicalProcessors} threads." : $"{cpu.LogicalProcessors} logical processors.",
                Severity.Info, false));
        }

        if (model.Vendor == CpuVendor.Intel && model.Generation is <= 4)
        {
            insights.Add(new HardwareInsight(
                "Older processor generation",
                $"A {model.Generation}{(model.Generation == 1 ? "st" : model.Generation == 2 ? "nd" : model.Generation == 3 ? "rd" : "th")}-generation processor lacks some efficiency features of newer chips, " +
                "and modern websites (including charting platforms) are more demanding than when it was designed. Keeping background activity low matters more on this hardware.",
                Severity.Medium, true));
        }

        if (cores is <= 2 && cpu.LogicalProcessors <= 2)
        {
            insights.Add(new HardwareInsight(
                "Two processing threads",
                "With only two threads, a single busy background task can noticeably slow everything else down.",
                Severity.Medium, true));
        }

        return new HardwareProfile(model, cpu.PhysicalCores, cpu.LogicalProcessors, tier, Math.Round(gib, 1),
            systemDrive?.MediaKind ?? StorageMediaKind.Unknown, systemDrive?.BusType, insights);
    }

    /// <summary>
    /// Identifies which resource appears to be the main constraint during an observation window. Each
    /// candidate gets a 0–1 score from measurements only; if nothing crosses its threshold we say so.
    /// </summary>
    public static LimitationAssessment AssessLimitation(ObservationSummary obs, HardwareProfile profile, ThermalSnapshot? thermal)
    {
        var scores = new Dictionary<PrimaryLimitation, double>
        {
            [PrimaryLimitation.Processor] = Score(obs.AverageCpu, 55, 90) * 0.6 + Score(obs.P95Cpu, 75, 99) * 0.4,
            [PrimaryLimitation.Memory] = Math.Max(Score(obs.AverageMemory, 75, 95), Score(obs.CommitPercent, 80, 98)),
            [PrimaryLimitation.Storage] = obs.AverageDiskActive is { } disk
                ? Score(disk, 40, 90) * (profile.SystemDriveMedia == StorageMediaKind.Rotational ? 1.0 : 0.7)
                : 0,
            [PrimaryLimitation.Thermal] = thermal?.Throttling is { } t
                ? Math.Max(t.PassiveLimitPercent is { } p ? Score(100 - p, 5, 40) : 0,
                           t.ProcessorPerformancePercent is { } perf && obs.AverageCpu > 50 ? Score(100 - perf, 25, 60) : 0)
                : 0,
        };

        var best = scores.MaxBy(kv => kv.Value);
        if (obs.SampleCount < 3)
            return new LimitationAssessment(PrimaryLimitation.Undetermined, "Not enough measurements yet to identify a bottleneck.", scores);
        if (best.Value < 0.35)
            return new LimitationAssessment(PrimaryLimitation.None, "No single resource was under sustained pressure during the observation window.", scores);

        var explanation = best.Key switch
        {
            PrimaryLimitation.Processor => $"The processor averaged {obs.AverageCpu:0}% busy (95th percentile {obs.P95Cpu:0}%). Background work competes directly with your trading apps.",
            PrimaryLimitation.Memory => $"Memory use averaged {obs.AverageMemory:0}% (commit {obs.CommitPercent:0}%). Windows is likely paging to disk, which causes pauses.",
            PrimaryLimitation.Storage => $"The disk was busy {obs.AverageDiskActive:0}% of the time{(profile.SystemDriveMedia == StorageMediaKind.Rotational ? " on a mechanical hard drive" : string.Empty)}. Apps wait for data.",
            PrimaryLimitation.Thermal => "Windows reported reduced processor performance consistent with thermal limiting. Cooling may be restricting speed.",
            _ => string.Empty,
        };
        return new LimitationAssessment(best.Key, explanation, scores);
    }

    private static double Score(double value, double low, double high) => Math.Clamp((value - low) / (high - low), 0, 1);
}
