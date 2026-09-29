using Tickshift.Core.Models;
using Tickshift.Diagnostics;
using Tickshift.Diagnostics.Hardware;
using Tickshift.Diagnostics.Recommendations;

namespace Tickshift.Tests;

public class RecommendationEngineTests
{
    private static SystemSnapshot Snapshot(
        ulong freeBytes = 60UL << 30,
        int startupApps = 2,
        StorageMediaKind media = StorageMediaKind.SolidState,
        PowerStatus? power = null,
        ThermalSnapshot? thermal = null,
        IReadOnlyList<BrowserAnalysis>? browsers = null,
        TimeSpan? uptime = null,
        ulong totalMemory = 8UL << 30)
    {
        var startup = Enumerable.Range(0, startupApps).Select(i => new StartupEntry
        {
            Id = $"UserRun|App{i}", Name = $"App{i}", Command = "x", Location = StartupLocation.UserRun, IsEnabled = true,
        }).ToList();
        return new SystemSnapshot(DateTimeOffset.Now,
            new CpuInfo("Intel(R) Core(TM) i3-7020U CPU @ 2.30GHz", "Intel", 2, 4, 2300),
            new MemoryStatus(totalMemory, 2UL << 30, 60, 4UL << 30, 12UL << 30),
            [new StorageVolume("C:\\", "OS", "NTFS", 120UL << 30, freeBytes, true, media, "SATA")],
            new OsInfo("Windows 10", "22H2", 19045, 1, "Core", true),
            power ?? new PowerStatus(PowerSource.Mains, true, 90, false, new PowerPlan(PowerPlan.BalancedId, "Balanced"), null),
            thermal ?? ThermalSnapshot.Unavailable("t", "none"),
            uptime ?? TimeSpan.FromHours(3),
            [], startup, browsers ?? [], null, null);
    }

    private static RecommendationContext Context(SystemSnapshot snapshot, ObservationSummary? obs = null, ThermalState thermal = ThermalState.Unknown, bool trading = false)
    {
        var hardware = HardwareInterpreter.Interpret(snapshot.Cpu, snapshot.Memory, snapshot.Storage);
        return new RecommendationContext(snapshot, hardware,
            obs ?? new ObservationSummary(10, TimeSpan.FromSeconds(10), 20, 30, 40, 55, 60, 40, 5, null, null, [], []),
            [], thermal, trading);
    }

    private static readonly RecommendationEngine Engine = new();

    [Fact]
    public void Healthy_system_gets_no_urgent_recommendations()
    {
        var recs = Engine.Evaluate(Context(Snapshot()));
        Assert.DoesNotContain(recs, r => r.Severity >= Severity.High);
    }

    [Fact]
    public void Excessive_startup_apps_produce_the_documented_example()
    {
        var rec = Assert.Single(Engine.Evaluate(Context(Snapshot(startupApps: 12))), r => r.RuleId == "startup.many");
        Assert.Equal("Excessive startup applications", rec.Issue);
        Assert.Equal(RiskLevel.Low, rec.Risk);
        Assert.Equal(Reversibility.FullyReversible, rec.Reversible);
        Assert.Contains("12", rec.Evidence);
    }

    [Fact]
    public void Critically_low_storage_is_ranked_first()
    {
        var recs = Engine.Evaluate(Context(Snapshot(freeBytes: 2UL << 30, startupApps: 9)));
        Assert.Equal("storage.low", recs[0].RuleId);
        Assert.Equal(Severity.Critical, recs[0].Severity);
    }

    [Fact]
    public void Hdd_recommendation_is_marked_as_hardware_limitation()
    {
        var rec = Assert.Single(Engine.Evaluate(Context(Snapshot(media: StorageMediaKind.Rotational))), r => r.RuleId == "storage.hdd");
        Assert.True(rec.IsHardwareLimitation);
    }

    [Fact]
    public void Disruptive_items_are_deferred_during_trading_mode()
    {
        var recs = Engine.Evaluate(Context(Snapshot(freeBytes: 2UL << 30), trading: true));
        var storage = Assert.Single(recs, r => r.RuleId == "storage.low");
        Assert.True(storage.DeferredForTrading);
        Assert.Contains("Deferred", storage.SuggestedAction);
    }

    [Fact]
    public void Thermal_rules_do_not_fire_without_a_sensor()
    {
        var recs = Engine.Evaluate(Context(Snapshot(), thermal: ThermalState.Unknown));
        Assert.DoesNotContain(recs, r => r.RuleId.StartsWith("thermal."));
    }

    [Fact]
    public void Warm_at_low_load_suggests_cooling_without_definitive_diagnosis()
    {
        var thermal = new ThermalSnapshot(DateTimeOffset.Now, SensorAvailability.Available, [new TemperatureReading("TZ", ThermalSourceKind.AcpiThermalZone, 88)], [], "t", "x", null);
        var recs = Engine.Evaluate(Context(Snapshot(thermal: thermal), thermal: ThermalState.High));
        var rec = Assert.Single(recs, r => r.RuleId == "thermal.low-load");
        Assert.Contains("not a definitive diagnosis", rec.SuggestedAction);
    }

    [Fact]
    public void Power_saver_on_mains_is_flagged()
    {
        var power = new PowerStatus(PowerSource.Mains, true, 90, false, new PowerPlan(PowerPlan.PowerSaverId, "Power saver"), null);
        Assert.Contains(Engine.Evaluate(Context(Snapshot(power: power))), r => r.RuleId == "power.saver-plugged");
    }

    [Fact]
    public void Heavy_browser_is_explained_with_measurements()
    {
        var browser = new BrowserAnalysis { Kind = BrowserKind.Chrome, Installed = true, ProcessCount = 35, MemoryBytes = 3L << 30 };
        var rec = Assert.Single(Engine.Evaluate(Context(Snapshot(browsers: [browser]))), r => r.RuleId == "browser.heavy");
        Assert.Contains("35", rec.Evidence);
        Assert.Contains("Memory Saver", rec.SuggestedAction);
    }

    [Fact]
    public void A_throwing_rule_does_not_break_the_engine()
    {
        var engine = new RecommendationEngine([new ThrowingRule(), .. RecommendationEngine.DefaultRules()]);
        var recs = engine.Evaluate(Context(Snapshot(startupApps: 12)));
        Assert.Contains(recs, r => r.RuleId == "startup.many");
    }

    [Fact]
    public void Ranking_prefers_relevance_then_safety()
    {
        var high = new Recommendation { RuleId = "a", Issue = "", Impact = "", SuggestedAction = "", Evidence = "", Relevance = 0.9, Risk = RiskLevel.Elevated };
        var safe = new Recommendation { RuleId = "b", Issue = "", Impact = "", SuggestedAction = "", Evidence = "", Relevance = 0.9, Risk = RiskLevel.Low };
        var low = new Recommendation { RuleId = "c", Issue = "", Impact = "", SuggestedAction = "", Evidence = "", Relevance = 0.2, Risk = RiskLevel.Low };
        Assert.True(safe.Score > high.Score);
        Assert.True(high.Score > low.Score);
    }

    private sealed class ThrowingRule : IRecommendationRule
    {
        public string Id => "boom";
        public Recommendation? Evaluate(RecommendationContext context) => throw new InvalidOperationException("boom");
    }
}
