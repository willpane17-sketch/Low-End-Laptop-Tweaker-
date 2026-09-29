using Tickshift.Core;
using Tickshift.Core.Models;
using Tickshift.Diagnostics.Hardware;
using Tickshift.Diagnostics.Monitoring;

namespace Tickshift.Diagnostics.Recommendations;

public sealed record RecommendationContext(
    SystemSnapshot Snapshot,
    HardwareProfile Hardware,
    ObservationSummary Observation,
    IReadOnlyList<MetricSample> History,
    ThermalState ThermalState,
    bool TradingModeActive);

public interface IRecommendationRule
{
    string Id { get; }
    Recommendation? Evaluate(RecommendationContext context);
}

/// <summary>
/// Local, deterministic, rule-based engine. No cloud services, no AI subscription. Each rule looks at
/// measurements and returns at most one explained recommendation; the engine ranks them by measured
/// relevance, safety and reversibility, and defers disruptive items during Trading Mode.
/// </summary>
public sealed class RecommendationEngine
{
    private readonly IReadOnlyList<IRecommendationRule> _rules;

    public RecommendationEngine(IEnumerable<IRecommendationRule>? rules = null)
    {
        _rules = (rules ?? DefaultRules()).ToList();
    }

    public static IEnumerable<IRecommendationRule> DefaultRules() =>
    [
        new LowMemoryCapacityRule(),
        new SustainedMemoryPressureRule(),
        new LowStorageRule(),
        new ManyStartupAppsRule(),
        new MechanicalDriveRule(),
        new DominantProcessRule(),
        new BrowserHeavyRule(),
        new BrowserBackgroundModeRule(),
        new ManyExtensionsRule(),
        new ThermalHighRule(),
        new WarmAtLowLoadRule(),
        new PowerSaverWhilePluggedInRule(),
        new BatteryHealthRule(),
        new LongUptimeRule(),
        new TempFilesRule(),
        new BrowserCacheRule(),
        new VisualEffectsRule(),
    ];

    public IReadOnlyList<Recommendation> Evaluate(RecommendationContext context)
    {
        var results = new List<Recommendation>();
        foreach (var rule in _rules)
        {
            Recommendation? rec;
            try
            {
                rec = rule.Evaluate(context);
            }
            catch (Exception)
            {
                continue; // one faulty rule must never break the scan
            }

            if (rec is null) continue;
            if (context.TradingModeActive && rec.Disruptive)
            {
                rec = rec with
                {
                    DeferredForTrading = true,
                    SuggestedAction = rec.SuggestedAction + " (Deferred until Trading Mode ends.)",
                };
            }

            results.Add(rec with { Relevance = Math.Clamp(rec.Relevance, 0, 1) });
        }

        return results.OrderByDescending(r => r.Score).ThenByDescending(r => r.Severity).ToList();
    }
}

internal static class Gb
{
    public const double Bytes = 1024d * 1024 * 1024;
    public static double Of(ulong b) => b / Bytes;
    public static double Of(long b) => b / Bytes;
}

internal sealed class LowMemoryCapacityRule : IRecommendationRule
{
    public string Id => "memory.capacity";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        if (c.Hardware.Memory != MemoryTier.VeryLow) return null;
        return new Recommendation
        {
            RuleId = Id,
            Issue = $"Only {c.Hardware.MemoryGiB:0.#} GB of memory installed",
            Impact = "Modern browsers and Windows can fill 4 GB quickly, causing slowdowns when Windows swaps to disk.",
            SuggestedAction = "Keep startup apps and browser tabs to a minimum. If your laptop allows it, a memory upgrade to 8 GB is the most effective fix.",
            Evidence = $"Installed memory: {c.Hardware.MemoryGiB:0.#} GB; average use during the scan: {c.Observation.AverageMemory:0}%.",
            Severity = Severity.High,
            Relevance = 0.6 + Math.Clamp((c.Observation.AverageMemory - 60) / 100, 0, 0.4),
            IsHardwareLimitation = true,
            Risk = RiskLevel.Low,
            Reversible = Reversibility.FullyReversible,
            ActionKind = RecommendationActionKind.Navigate,
            ActionTarget = "Startup",
            ActionLabel = "Review startup apps",
        };
    }
}

internal sealed class SustainedMemoryPressureRule : IRecommendationRule
{
    public string Id => "memory.pressure";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        var samples = c.History.Count >= 30 ? c.History : null;
        var sustained = samples is not null ? SampleStatistics.FractionAtOrAbove(samples, s => s.MemoryPercent, 85) : (c.Observation.AverageMemory >= 85 ? 1 : 0);
        if (sustained < 0.5 && c.Observation.CommitPercent < 90) return null;

        var top = c.Observation.TopByMemory.Where(p => p.Category is ProcessCategory.Application or ProcessCategory.Browser).Take(3)
            .Select(p => $"{p.Name} ({Format.Bytes(p.MemoryBytes)})");
        return new Recommendation
        {
            RuleId = Id,
            Issue = "Memory is consistently under pressure",
            Impact = "When memory is full, Windows pages to disk, which causes stutters when switching apps or loading charts.",
            SuggestedAction = "Review the largest memory users and close apps or browser tabs you are not using.",
            Evidence = $"Memory at or above 85% for {sustained:P0} of recent samples; commit charge {c.Observation.CommitPercent:0}%. Largest: {string.Join(", ", top)}.",
            Severity = Severity.High,
            Relevance = Math.Max(sustained, (c.Observation.CommitPercent - 80) / 20),
            ActionKind = RecommendationActionKind.Navigate,
            ActionTarget = "Performance",
            ActionLabel = "Open process analyser",
        };
    }
}

internal sealed class LowStorageRule : IRecommendationRule
{
    public string Id => "storage.low";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        var drive = c.Snapshot.SystemDrive;
        if (drive is null) return null;
        var freeGb = Gb.Of(drive.FreeBytes);
        var critical = drive.FreePercent < 5 || freeGb < 3;
        if (!critical && drive.FreePercent >= 12 && freeGb >= 10) return null;

        return new Recommendation
        {
            RuleId = Id,
            Issue = critical ? "System drive is almost full" : "System drive space is getting low",
            Impact = "Windows needs free space for updates, the page file and temporary data. A full drive can cause slowdowns and failed updates.",
            SuggestedAction = "Run a safe cleanup of temporary files, then review large files and unused apps yourself.",
            Evidence = $"{Format.Bytes(drive.FreeBytes)} free of {Format.Bytes(drive.TotalBytes)} ({drive.FreePercent:0.#}%) on {drive.RootPath}.",
            Severity = critical ? Severity.Critical : Severity.Medium,
            Relevance = critical ? 1.0 : 0.6,
            Disruptive = true,
            Reversible = Reversibility.NotReversible,
            ActionKind = RecommendationActionKind.Navigate,
            ActionTarget = "Cleanup",
            ActionLabel = "Open System Cleanup",
        };
    }
}

internal sealed class ManyStartupAppsRule : IRecommendationRule
{
    public string Id => "startup.many";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        var count = c.Snapshot.EnabledThirdPartyStartupCount;
        var threshold = c.Hardware.Memory == MemoryTier.VeryLow ? 4 : 7;
        if (count < threshold) return null;
        return new Recommendation
        {
            RuleId = Id,
            Issue = "Excessive startup applications",
            Impact = "Potentially slower startup and increased background resource usage.",
            SuggestedAction = "Review unnecessary startup applications and disable the ones you do not need at sign-in.",
            Evidence = $"{count} third-party apps start automatically when you sign in.",
            Severity = count >= threshold * 2 ? Severity.Medium : Severity.Low,
            Relevance = Math.Clamp(0.4 + (count - threshold) * 0.08, 0.4, 0.95),
            Risk = RiskLevel.Low,
            Reversible = Reversibility.FullyReversible,
            ActionKind = RecommendationActionKind.Navigate,
            ActionTarget = "Startup",
            ActionLabel = "Review startup apps",
        };
    }
}

internal sealed class MechanicalDriveRule : IRecommendationRule
{
    public string Id => "storage.hdd";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        if (c.Hardware.SystemDriveMedia != StorageMediaKind.Rotational) return null;
        var busy = c.Observation.AverageDiskActive;
        return new Recommendation
        {
            RuleId = Id,
            Issue = "Windows runs from a mechanical hard drive",
            Impact = "Slow random access makes start-up, app launches and memory paging much slower than on an SSD.",
            SuggestedAction = "Consider upgrading to an SSD. Software optimisation cannot reproduce the improvement an SSD provides.",
            Evidence = busy is { } b ? $"System drive reported as HDD; disk busy {b:0}% on average during the scan." : "System drive reported as HDD by Windows.",
            Severity = Severity.High,
            Relevance = 0.7 + Math.Clamp((busy ?? 0) / 300, 0, 0.3),
            IsHardwareLimitation = true,
            ActionKind = RecommendationActionKind.None,
        };
    }
}

internal sealed class DominantProcessRule : IRecommendationRule
{
    public string Id => "cpu.dominant-process";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        if (c.Observation.AverageCpu < 60) return null;
        var top = c.Observation.TopByCpu.FirstOrDefault(p => p.Category is ProcessCategory.Application or ProcessCategory.Browser or ProcessCategory.Unknown);
        if (top is null || top.CpuPercent < 20) return null;
        var isBrowser = top.Category == ProcessCategory.Browser;
        return new Recommendation
        {
            RuleId = Id,
            Issue = $"{top.Name} is using a large share of the processor",
            Impact = "Other apps, including your trading platform, have less processing time available and may respond slowly.",
            SuggestedAction = isBrowser
                ? "Close tabs you do not need and enable the browser's efficiency feature (see Browser Booster)."
                : "If you are not actively using it, close it from its own window.",
            Evidence = $"Processor averaged {c.Observation.AverageCpu:0}% busy; {top.Name} ({top.Instances} process{(top.Instances == 1 ? string.Empty : "es")}) used about {top.CpuPercent:0}%.",
            Severity = Severity.Medium,
            Relevance = Math.Clamp(top.CpuPercent / 60, 0.3, 1),
            ActionKind = RecommendationActionKind.Navigate,
            ActionTarget = isBrowser ? "Browser" : "Performance",
            ActionLabel = isBrowser ? "Open Browser Booster" : "Open process analyser",
        };
    }
}

internal sealed class BrowserHeavyRule : IRecommendationRule
{
    public string Id => "browser.heavy";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        var total = (long)c.Snapshot.Memory.TotalPhysicalBytes;
        var heavy = c.Snapshot.Browsers.Where(b => b.IsRunning && total > 0 && (b.MemoryBytes > total * 0.30 || b.ProcessCount >= 30)).OrderByDescending(b => b.MemoryBytes).FirstOrDefault();
        if (heavy is null) return null;
        var share = total > 0 ? heavy.MemoryBytes * 100.0 / total : 0;
        return new Recommendation
        {
            RuleId = Id,
            Issue = $"{heavy.DisplayName} is using a lot of memory",
            Impact = "Each tab and extension runs in its own process. On limited hardware this competes with trading apps.",
            SuggestedAction = $"Close unused tabs and turn on {heavy.EfficiencyFeatureName}, with your trading sites added to its exception list.",
            Evidence = $"{heavy.ProcessCount} {heavy.DisplayName} processes using about {Format.Bytes(heavy.MemoryBytes)} ({share:0}% of installed memory).",
            Severity = share > 45 ? Severity.High : Severity.Medium,
            Relevance = Math.Clamp(share / 50, 0.4, 1),
            ActionKind = RecommendationActionKind.Navigate,
            ActionTarget = "Browser",
            ActionLabel = "Open Browser Booster",
        };
    }
}

internal sealed class BrowserBackgroundModeRule : IRecommendationRule
{
    public string Id => "browser.background";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        var b = c.Snapshot.Browsers.FirstOrDefault(x => x.Installed && x.BackgroundModeEnabled == true && !x.BackgroundModeEnforcedByPolicy);
        if (b is null) return null;
        return new Recommendation
        {
            RuleId = Id,
            Issue = $"{b.DisplayName} keeps running after you close it",
            Impact = "Background apps and extensions keep using memory and processor time even when no window is open.",
            SuggestedAction = $"In {b.DisplayName} settings, turn off \"Continue running background extensions and apps when {b.DisplayName} is closed\".",
            Evidence = "Detected from the browser's own settings file (read-only).",
            Severity = Severity.Low,
            Relevance = c.Hardware.Memory <= MemoryTier.Low ? 0.55 : 0.35,
            ActionKind = RecommendationActionKind.OpenBrowserPage,
            ActionTarget = b.Kind + ":" + (b.Kind == BrowserKind.Edge ? "performance" : "system"),
            ActionLabel = "Open browser setting",
        };
    }
}

internal sealed class ManyExtensionsRule : IRecommendationRule
{
    public string Id => "browser.extensions";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        var b = c.Snapshot.Browsers.Where(x => x.Installed).OrderByDescending(x => x.TotalExtensions).FirstOrDefault();
        if (b is null || b.TotalExtensions < 10) return null;
        return new Recommendation
        {
            RuleId = Id,
            Issue = $"Many extensions installed in {b.DisplayName}",
            Impact = "Extensions can run on every page you open, adding memory and processor load.",
            SuggestedAction = "Review your extensions and remove or disable those you no longer use.",
            Evidence = $"{b.TotalExtensions} extension folders found across {b.Profiles.Count} profile(s).",
            Severity = Severity.Low,
            Relevance = Math.Clamp(b.TotalExtensions / 25.0, 0.3, 0.8),
            ActionKind = RecommendationActionKind.OpenBrowserPage,
            ActionTarget = b.Kind + ":extensions",
            ActionLabel = "Open extensions",
        };
    }
}

internal sealed class ThermalHighRule : IRecommendationRule
{
    public string Id => "thermal.high";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        if (c.ThermalState is not (ThermalState.High or ThermalState.Critical)) return null;
        return new Recommendation
        {
            RuleId = Id,
            Issue = "The laptop is running hot",
            Impact = "When hot, the processor slows itself down to stay safe, which reduces responsiveness.",
            SuggestedAction = "Reduce background load, keep vents clear and use a hard, flat surface or a laptop stand.",
            Evidence = $"Current reading {Format.Celsius(c.Snapshot.Thermal.PrimaryCelsius)} ({c.Snapshot.Thermal.Primary?.SensorName}).",
            Severity = c.ThermalState == ThermalState.Critical ? Severity.High : Severity.Medium,
            Relevance = c.ThermalState == ThermalState.Critical ? 1 : 0.75,
            ActionKind = RecommendationActionKind.Navigate,
            ActionTarget = "Thermal",
            ActionLabel = "Open Thermal Monitor",
        };
    }
}

internal sealed class WarmAtLowLoadRule : IRecommendationRule
{
    public string Id => "thermal.low-load";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        if (c.ThermalState is not (ThermalState.Elevated or ThermalState.High or ThermalState.Critical) || c.Observation.AverageCpu >= 35) return null;
        return new Recommendation
        {
            RuleId = Id,
            Issue = "Warm even though the processor is lightly used",
            Impact = "Possible cooling-related factors: blocked vents, dust in the fan or heatsink, a warm room or a soft surface.",
            SuggestedAction = "Check ventilation and consider professional fan cleaning. This is a possibility, not a definitive diagnosis.",
            Evidence = $"{Format.Celsius(c.Snapshot.Thermal.PrimaryCelsius)} while the processor averaged {c.Observation.AverageCpu:0}%.",
            Severity = Severity.Medium,
            Relevance = 0.6,
            IsHardwareLimitation = true,
            ActionKind = RecommendationActionKind.Navigate,
            ActionTarget = "Thermal",
            ActionLabel = "Open Thermal Monitor",
        };
    }
}

internal sealed class PowerSaverWhilePluggedInRule : IRecommendationRule
{
    public string Id => "power.saver-plugged";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        var p = c.Snapshot.Power;
        if (p.Source != PowerSource.Mains || p.ActivePlan?.Id != PowerPlan.PowerSaverId) return null;
        return new Recommendation
        {
            RuleId = Id,
            Issue = "Power saver plan is active while plugged in",
            Impact = "Power saver limits processor speed to save energy, which is usually unnecessary on mains power.",
            SuggestedAction = "Switch to the Balanced power plan while plugged in.",
            Evidence = $"Active plan: {p.ActivePlan.Name}; power source: mains.",
            Severity = Severity.Medium,
            Relevance = 0.7,
            ActionKind = RecommendationActionKind.OpenSettings,
            ActionTarget = "power",
            ActionLabel = "Open power settings",
        };
    }
}

internal sealed class BatteryHealthRule : IRecommendationRule
{
    public string Id => "power.battery-health";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        if (c.Snapshot.Power.Health?.HealthPercent is not { } health || health >= 60) return null;
        return new Recommendation
        {
            RuleId = Id,
            Issue = "Battery has lost much of its original capacity",
            Impact = "A worn battery runs down quickly; some laptops also limit performance when the battery is weak.",
            SuggestedAction = "Keep the laptop plugged in while trading, and consider a replacement battery from the manufacturer.",
            Evidence = $"Full-charge capacity is {health:0}% of the design capacity (reported by the battery).",
            Severity = Severity.Low,
            Relevance = Math.Clamp((60 - health) / 40 + 0.3, 0.3, 0.9),
            IsHardwareLimitation = true,
        };
    }
}

internal sealed class LongUptimeRule : IRecommendationRule
{
    public string Id => "system.uptime";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        if (c.Snapshot.Uptime < TimeSpan.FromDays(7)) return null;
        return new Recommendation
        {
            RuleId = Id,
            Issue = "Windows has not restarted for a long time",
            Impact = "Long uptimes can leave pending updates and accumulated background state that a restart would clear.",
            SuggestedAction = "Restart Windows at a convenient time outside trading hours.",
            Evidence = $"Uptime: {Format.Duration(c.Snapshot.Uptime)}. (Fast Startup can make shutdowns not reset this counter.)",
            Severity = Severity.Low,
            Relevance = Math.Clamp(c.Snapshot.Uptime.TotalDays / 30, 0.3, 0.7),
            Disruptive = true,
        };
    }
}

internal sealed class TempFilesRule : IRecommendationRule
{
    public string Id => "cleanup.temp";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        if (c.Snapshot.TempFiles is not { } t || t.Bytes < 500L * 1024 * 1024) return null;
        return new Recommendation
        {
            RuleId = Id,
            Issue = "Large amount of old temporary files",
            Impact = "Temporary files take up disk space. Removing them frees space but does not usually make Windows faster by itself.",
            SuggestedAction = "Remove temporary files older than two days. Files in use are skipped automatically.",
            Evidence = $"{Format.Bytes(t.Bytes)} in {t.FileCount:N0} temporary files older than 2 days{(t.Truncated ? " (scan stopped early; actual amount may be larger)" : string.Empty)}.",
            Severity = Severity.Low,
            Relevance = Math.Clamp(Gb.Of(t.Bytes) / 5, 0.3, 0.8),
            Disruptive = true,
            Reversible = Reversibility.NotReversible,
            ActionKind = RecommendationActionKind.Navigate,
            ActionTarget = "Cleanup",
            ActionLabel = "Open System Cleanup",
        };
    }
}

internal sealed class BrowserCacheRule : IRecommendationRule
{
    public string Id => "cleanup.browser-cache";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        var b = c.Snapshot.Browsers.Where(x => x.Installed).OrderByDescending(x => x.TotalDisposableCacheBytes).FirstOrDefault();
        if (b is null || b.TotalDisposableCacheBytes < 1024L * 1024 * 1024) return null;
        var lowSpace = c.Snapshot.SystemDrive is { FreePercent: < 15 };
        return new Recommendation
        {
            RuleId = Id,
            Issue = $"{b.DisplayName} cache is large",
            Impact = "The cache only uses disk space; it is re-created as you browse. Clearing it helps when space is short, not speed.",
            SuggestedAction = $"Optionally clear the disposable cache with {b.DisplayName} closed. Passwords, cookies, history and sessions are never touched.",
            Evidence = $"{Format.Bytes(b.TotalDisposableCacheBytes)} of disposable cache across {b.Profiles.Count} profile(s).",
            Severity = Severity.Info,
            Relevance = lowSpace ? 0.6 : 0.25,
            Disruptive = true,
            Reversible = Reversibility.NotReversible,
            ActionKind = RecommendationActionKind.Navigate,
            ActionTarget = "Cleanup",
            ActionLabel = "Open System Cleanup",
        };
    }
}

internal sealed class VisualEffectsRule : IRecommendationRule
{
    public string Id => "ui.animations";

    public Recommendation? Evaluate(RecommendationContext c)
    {
        if (c.Snapshot.VisualEffects is not { AnyAnimationsEnabled: true }) return null;
        if (c.Hardware.Memory > MemoryTier.Low && c.Hardware.Cpu.Segment != CpuSegment.LowPowerMobile) return null;
        return new Recommendation
        {
            RuleId = Id,
            Issue = "Windows animations are enabled",
            Impact = "Turning off window and menu animations can make the interface feel snappier on older graphics. The measurable benefit is usually small.",
            SuggestedAction = "Optionally reduce animations. This is fully reversible from the Recovery Centre.",
            Evidence = "One or more Windows animation settings are on, on a low-power or limited-memory laptop.",
            Severity = Severity.Info,
            Relevance = 0.3,
            ActionKind = RecommendationActionKind.RunOptimisation,
            ActionTarget = "ui.reduce-animations",
            ActionLabel = "Review in One-Click Optimise",
        };
    }
}
