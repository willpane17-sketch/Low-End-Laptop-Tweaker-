using Tickshift.Core.Models;

namespace Tickshift.Diagnostics;

public sealed record ConditionResult(SystemCondition Condition, string Summary, IReadOnlyList<string> Reasons);

/// <summary>Turns live measurements into an overall, plain-English condition for the dashboard.</summary>
public static class SystemConditionEvaluator
{
    public static ConditionResult Evaluate(
        IReadOnlyList<MetricSample> recent,
        MemoryStatus? memory,
        StorageVolume? systemDrive,
        ThermalState thermal)
    {
        if (recent.Count == 0 && memory is null) return new(SystemCondition.Unknown, "Gathering measurements…", []);

        var reasons = new List<string>();
        var points = 0;

        var avgCpu = recent.Count > 0 ? recent.Average(s => s.CpuPercent) : 0;
        if (avgCpu >= 90) { points += 3; reasons.Add($"Processor very busy ({avgCpu:0}% average)."); }
        else if (avgCpu >= 70) { points += 2; reasons.Add($"Processor busy ({avgCpu:0}% average)."); }
        else if (avgCpu >= 50) { points += 1; reasons.Add($"Moderate processor load ({avgCpu:0}%)."); }

        var mem = recent.Count > 0 ? recent.Average(s => s.MemoryPercent) : memory?.MemoryLoadPercent ?? 0;
        if (mem >= 92) { points += 3; reasons.Add($"Memory almost full ({mem:0}%)."); }
        else if (mem >= 85) { points += 2; reasons.Add($"Memory under pressure ({mem:0}%)."); }
        else if (mem >= 75) { points += 1; reasons.Add($"Memory fairly full ({mem:0}%)."); }

        if (systemDrive is not null)
        {
            if (systemDrive.FreePercent < 5 || systemDrive.FreeBytes < 3UL * 1024 * 1024 * 1024) { points += 3; reasons.Add("System drive almost full."); }
            else if (systemDrive.FreePercent < 10) { points += 1; reasons.Add("System drive space is getting low."); }
        }

        switch (thermal)
        {
            case ThermalState.Critical: points += 3; reasons.Add("Very high temperature."); break;
            case ThermalState.High: points += 2; reasons.Add("High temperature."); break;
            case ThermalState.Elevated: points += 1; reasons.Add("Running warm."); break;
        }

        var condition = points switch
        {
            0 => SystemCondition.Good,
            <= 2 => SystemCondition.Fair,
            <= 4 => SystemCondition.Strained,
            _ => SystemCondition.Critical,
        };

        var summary = condition switch
        {
            SystemCondition.Good => "Everything looks healthy.",
            SystemCondition.Fair => "Mostly healthy, with minor pressure.",
            SystemCondition.Strained => "Your laptop is working hard right now.",
            SystemCondition.Critical => "Your laptop is heavily constrained right now.",
            _ => "Gathering measurements…",
        };

        return new ConditionResult(condition, summary, reasons);
    }
}
