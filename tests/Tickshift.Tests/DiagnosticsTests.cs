using Tickshift.Core.Models;
using Tickshift.Core.Settings;
using Tickshift.Diagnostics;
using Tickshift.Diagnostics.Benchmarks;
using Tickshift.Diagnostics.Hardware;
using Tickshift.Diagnostics.Monitoring;
using Tickshift.Diagnostics.Network;
using Tickshift.Diagnostics.Recommendations;
using Tickshift.Diagnostics.Thermal;
using Tickshift.Optimization;
using Tickshift.Optimization.Automation;
using Tickshift.Optimization.Optimisations;
using Tickshift.Tests.Fakes;

namespace Tickshift.Tests;

public class PerformanceHistoryTests
{
    [Fact]
    public void Ring_buffer_keeps_only_the_newest_samples()
    {
        var h = new PerformanceHistory(10);
        foreach (var s in FakeMetrics.Series(25, 10, 10)) h.Add(s);
        Assert.Equal(10, h.Count);
        var all = h.GetSince(DateTimeOffset.MinValue);
        Assert.True(all.Zip(all.Skip(1)).All(p => p.First.Timestamp < p.Second.Timestamp));
    }

    [Fact]
    public void Windows_filter_by_time()
    {
        var h = new PerformanceHistory(5000);
        var now = DateTimeOffset.Now;
        foreach (var s in FakeMetrics.Series(3600, 10, 10, now.AddHours(-1), TimeSpan.FromSeconds(1))) h.Add(s);
        Assert.InRange(h.GetWindow(HistoryWindow.FiveMinutes, now).Count, 295, 301);
        Assert.InRange(h.GetWindow(HistoryWindow.ThirtyMinutes, now).Count, 1795, 1801);
        Assert.Equal(3600, h.GetWindow(HistoryWindow.Session, now).Count);
    }

    [Fact]
    public void Downsampling_bounds_chart_points_and_preserves_averages()
    {
        var samples = FakeMetrics.Series(1000, 40, 60);
        var reduced = PerformanceHistory.Downsample(samples, 100);
        Assert.Equal(100, reduced.Count);
        Assert.All(reduced, s => Assert.Equal(40, s.CpuPercent, 3));
    }

    [Fact]
    public void Downsampling_keeps_missing_sensor_values_missing()
    {
        var reduced = PerformanceHistory.Downsample(FakeMetrics.Series(500, 10, 10, temp: null), 50);
        Assert.All(reduced, s => Assert.Null(s.TemperatureCelsius));
    }

    [Fact]
    public void Statistics_helpers()
    {
        Assert.Equal(95, SampleStatistics.Percentile(Enumerable.Range(0, 101).Select(i => (double)i), 95), 3);
        var rising = Enumerable.Range(0, 10).Select(i => (DateTimeOffset.Now.AddMinutes(i), 50.0 + i * 2)).ToList();
        Assert.Equal(2, SampleStatistics.SlopePerMinute(rising)!.Value, 3);
        Assert.Null(SampleStatistics.SlopePerMinute(rising.Take(2).ToList()));
    }
}

public class ThermalAnalyzerTests
{
    private static readonly CpuModel UltraLow = CpuModelParser.Parse("Intel(R) Core(TM) i3-7020U CPU @ 2.30GHz");
    private static readonly PowerStatus Mains = new(PowerSource.Mains, true, 90, false, null, null);

    private static ThermalSnapshot Reading(double c, ThrottlingIndicators? t = null) =>
        new(DateTimeOffset.Now, SensorAvailability.Available, [new TemperatureReading("CPU", ThermalSourceKind.CpuPackage, c)], [], "fake", "x", t);

    [Fact]
    public void Missing_sensor_is_unknown_and_never_estimated()
    {
        var a = ThermalAnalyzer.Assess(ThermalSnapshot.Unavailable("none", "No sensor."), UltraLow, FakeMetrics.Series(30, 95, 50), Mains, []);
        Assert.Equal(ThermalState.Unknown, a.State);
        Assert.Null(a.Celsius);
        Assert.Contains(a.Insights, i => i.Title.Contains("No temperature sensor"));
    }

    [Theory]
    [InlineData(60, ThermalState.Normal)]
    [InlineData(82, ThermalState.Elevated)]
    [InlineData(91, ThermalState.High)]
    [InlineData(99, ThermalState.Critical)]
    public void Classifies_with_processor_appropriate_thresholds(double c, ThermalState expected) =>
        Assert.Equal(expected, ThermalAnalyzer.Assess(Reading(c), UltraLow, [], Mains, []).State);

    [Fact]
    public void Acpi_zones_use_adjusted_thresholds_and_say_why()
    {
        var t = ThermalAnalyzer.ThresholdsFor(UltraLow, ThermalSourceKind.AcpiThermalZone);
        Assert.True(t.Elevated < ThermalAnalyzer.ThresholdsFor(UltraLow, ThermalSourceKind.CpuPackage).Elevated);
        Assert.Contains("firmware thermal zone", t.Basis);
    }

    [Fact]
    public void Warm_at_low_load_is_explained_as_a_possibility()
    {
        var a = ThermalAnalyzer.Assess(Reading(92), UltraLow, FakeMetrics.Series(30, 15, 50, temp: 92), Mains, []);
        var insight = Assert.Single(a.Insights, i => i.Title.Contains("lightly loaded"));
        Assert.Contains("not a definitive diagnosis", insight.Explanation);
    }

    [Fact]
    public void Firmware_passive_limit_is_reported_and_never_overridden()
    {
        var a = ThermalAnalyzer.Assess(Reading(90, new ThrottlingIndicators(70, 60)), UltraLow, FakeMetrics.Series(30, 80, 50), Mains, []);
        var insight = Assert.Single(a.Insights, i => i.Title.Contains("limiting"));
        Assert.Contains("never override", insight.Explanation);
    }

    [Fact]
    public void Trend_detection()
    {
        var rising = FakeMetrics.Series(30, 50, 50, DateTimeOffset.Now.AddMinutes(-10), TimeSpan.FromSeconds(20))
            .Select((s, i) => s with { TemperatureCelsius = 60 + i }).ToList();
        Assert.Equal(ThermalTrend.Rising, ThermalAnalyzer.Trend(rising).Trend);
    }
}

public class BenchmarkComparisonTests
{
    private static BenchmarkResult Result(PowerSource power, params (string Key, double? Value)[] values) =>
        new(Guid.NewGuid(), DateTimeOffset.Now, TimeSpan.FromSeconds(10), "x",
            values.Select(v => new BenchmarkMeasurement(v.Key, v.Key, v.Value, "%", v.Key != BenchmarkService.AvailableMemory,
                v.Key == BenchmarkService.AvailableMemory ? 100 : 3)).ToList(),
            new BenchmarkConditions(power, 100, false, false));

    [Fact]
    public void Small_differences_are_reported_as_unchanged()
    {
        var c = BenchmarkService.Compare(Result(PowerSource.Mains, (BenchmarkService.IdleCpu, 20)), Result(PowerSource.Mains, (BenchmarkService.IdleCpu, 18.5)));
        Assert.Equal(DeltaAssessment.Unchanged, c.Deltas.Single().Assessment);
    }

    [Fact]
    public void Direction_respects_lower_is_better()
    {
        var c = BenchmarkService.Compare(
            Result(PowerSource.Mains, (BenchmarkService.IdleCpu, 30), (BenchmarkService.AvailableMemory, 800)),
            Result(PowerSource.Mains, (BenchmarkService.IdleCpu, 12), (BenchmarkService.AvailableMemory, 1500)));
        Assert.All(c.Deltas, d => Assert.Equal(DeltaAssessment.Improved, d.Assessment));
    }

    [Fact]
    public void Changed_power_source_makes_cpu_not_comparable_and_adds_a_caveat()
    {
        var c = BenchmarkService.Compare(Result(PowerSource.Battery, (BenchmarkService.IdleCpu, 30)), Result(PowerSource.Mains, (BenchmarkService.IdleCpu, 10)));
        Assert.Equal(DeltaAssessment.NotComparable, c.Deltas.Single().Assessment);
        Assert.Contains(c.Caveats, x => x.Contains("power source"));
    }

    [Fact]
    public void Missing_measurements_are_not_comparable_rather_than_zero()
    {
        var c = BenchmarkService.Compare(Result(PowerSource.Mains, (BenchmarkService.Temperature, null)), Result(PowerSource.Mains, (BenchmarkService.Temperature, 70)));
        Assert.Equal(DeltaAssessment.NotComparable, c.Deltas.Single().Assessment);
    }
}

public class NetworkTests
{
    [Theory]
    [InlineData("broker.example.com", "broker.example.com")]
    [InlineData("https://Trade.Example.com/login?x=1", "trade.example.com")]
    [InlineData("192.168.1.1", "192.168.1.1")]
    [InlineData("[::1]", "::1")]
    public void Accepts_hosts_and_extracts_from_urls(string input, string expected)
    {
        Assert.True(EndpointValidator.TryNormalise(input, out var host, out _));
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://user:secret@broker.com")]
    [InlineData("user@broker.com")]
    [InlineData("bad host name")]
    [InlineData("-leading.example.com")]
    public void Rejects_unsafe_or_invalid_endpoints(string input) => Assert.False(EndpointValidator.TryNormalise(input, out _, out _));

    [Fact]
    public void Summarises_latency_jitter_and_loss()
    {
        var r = NetworkDiagnosticsService.Summarise("x", "ICMP echo", 10, [20, 30, 20, 30, 20, 30, 20, 30], null);
        Assert.Equal(20, r.LossPercent);
        Assert.Equal(25, r.AverageMs);
        Assert.Equal(10, r.JitterMs);
        Assert.Equal("Poor", r.Quality);
    }

    [Fact]
    public void All_lost_is_unreachable_not_zero_latency()
    {
        var r = NetworkDiagnosticsService.Summarise("x", "ICMP echo", 5, [], "TimedOut");
        Assert.Null(r.AverageMs);
        Assert.Equal("Unreachable", r.Quality);
        Assert.Equal(100, r.LossPercent);
    }

    [Fact]
    public void Disclaimer_distinguishes_network_from_broker_execution() =>
        Assert.Contains("not your broker's order execution time", NetworkDiagnosticsService.LatencyDisclaimer);
}

public class SystemConditionTests
{
    [Fact]
    public void Healthy_system_is_good() =>
        Assert.Equal(SystemCondition.Good, SystemConditionEvaluator.Evaluate(FakeMetrics.Series(10, 10, 40), null, null, ThermalState.Normal).Condition);

    [Fact]
    public void Heavy_pressure_is_critical_with_reasons()
    {
        var drive = new StorageVolume("C:\\", "", "NTFS", 100UL << 30, 1UL << 30, true, StorageMediaKind.SolidState, null);
        var r = SystemConditionEvaluator.Evaluate(FakeMetrics.Series(10, 95, 95), null, drive, ThermalState.High);
        Assert.Equal(SystemCondition.Critical, r.Condition);
        Assert.True(r.Reasons.Count >= 3);
    }

    [Fact]
    public void No_data_is_unknown() => Assert.Equal(SystemCondition.Unknown, SystemConditionEvaluator.Evaluate([], null, null, ThermalState.Unknown).Condition);
}

public class AutomationRulesTests
{
    [Fact]
    public void Sustained_requires_the_whole_window()
    {
        var shortBurst = FakeMetrics.Series(60, 95, 95, step: TimeSpan.FromSeconds(1));
        Assert.False(AutomationRules.IsSustained(shortBurst, s => s.MemoryPercent, 88, TimeSpan.FromMinutes(5)));
        var sustained = FakeMetrics.Series(160, 95, 95, DateTimeOffset.Now.AddMinutes(-6), TimeSpan.FromSeconds(2));
        Assert.True(AutomationRules.IsSustained(sustained, s => s.MemoryPercent, 88, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Cooldown_prevents_repeated_alerts()
    {
        var now = DateTimeOffset.Now;
        var state = new AutomationState { LastAlerts = new() { ["memory"] = now.AddMinutes(-5) } };
        Assert.False(AutomationRules.CooledDown(state, "memory", now, TimeSpan.FromMinutes(30)));
        Assert.True(AutomationRules.CooledDown(state, "thermal", now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Daily_diagnostic_runs_once_per_day_after_the_scheduled_time()
    {
        var at = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 29)));
        Assert.True(AutomationRules.DailyDiagnosticDue(new AutomationState(), new TimeOnly(9, 0), at));
        Assert.False(AutomationRules.DailyDiagnosticDue(new AutomationState(), new TimeOnly(11, 0), at));
        Assert.False(AutomationRules.DailyDiagnosticDue(new AutomationState { LastDailyDiagnostic = DateOnly.FromDateTime(at.LocalDateTime) }, new TimeOnly(9, 0), at));
    }

    [Fact]
    public void Only_approved_low_risk_reversible_items_may_run_unattended()
    {
        IOptimisation[] catalogue =
        [
            new VisualEffectsOptimisation(new FakeVisualEffects()),
            new UserTempCleanupOptimisation(new SandboxLocations()),
            new HighPerformanceWhenPluggedInOptimisation(new FakePowerPlans(), new FakeSystemInfo()),
            new BalancedWhenPluggedInOptimisation(new FakePowerPlans(), new FakeSystemInfo()),
        ];
        var approved = new[] { "ui.reduce-animations", "cleanup.user-temp", "power.high-performance-on-mains" };

        var allowed = AutomationRules.FilterUnattended(catalogue, approved);

        Assert.Equal(["ui.reduce-animations"], allowed); // cleanup is irreversible; high performance is moderate risk; balanced was not approved
    }
}

public class DiagnosticEngineTests
{
    private static DiagnosticEngine Create(FakeSystemInfo info, FakeMetrics metrics, IStartupProviderOverride? startup = null) =>
        new(info, metrics, new FakeProcessProvider(), startup?.Provider ?? new FakeStartupProvider(), new FakeBrowsers(), new FakeVisualEffects(),
            new RecommendationEngine(), new Core.Abstractions.TradingSessionFlag());

    public sealed record IStartupProviderOverride(Core.Abstractions.IStartupProvider Provider);

    [Fact]
    public async Task Completes_without_a_temperature_sensor_and_says_so()
    {
        var metrics = new FakeMetrics { CollectResult = FakeMetrics.Series(8, 30, 70) };
        var report = await Create(new FakeSystemInfo(), metrics).RunAsync(DiagnosticOptions.Quick, null, default);

        Assert.Contains(report.Limitations, l => l.Contains("temperature is not available"));
        Assert.Equal(8, report.Observation.SampleCount);
        Assert.Equal(MemoryTier.VeryLow, report.Hardware.Memory);
        Assert.Contains(report.Recommendations, r => r.RuleId == "memory.capacity");
    }

    [Fact]
    public async Task A_failing_provider_becomes_a_limitation_not_a_crash()
    {
        var info = new FakeSystemInfo { ThrowOnStorage = true };
        var report = await Create(info, new FakeMetrics { CollectResult = FakeMetrics.Series(5, 20, 50) }).RunAsync(DiagnosticOptions.Quick, null, default);
        Assert.Contains(report.Limitations, l => l.Contains("Storage information could not be read"));
        Assert.Empty(report.Snapshot.Storage);
    }

    [Fact]
    public async Task Unsupported_windows_build_is_flagged()
    {
        var info = new FakeSystemInfo { Os = new OsInfo("Windows 10", "1607", 14393, 0, "Core", true) };
        var report = await Create(info, new FakeMetrics()).RunAsync(DiagnosticOptions.Quick, null, default);
        Assert.Contains(report.Limitations, l => l.Contains("older than TICKSHIFT's supported minimum"));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var metrics = new CancellingMetrics();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DiagnosticEngine(new FakeSystemInfo(), metrics, new FakeProcessProvider(), new FakeStartupProvider(), new FakeBrowsers(),
                new FakeVisualEffects(), new RecommendationEngine(), new Core.Abstractions.TradingSessionFlag())
            .RunAsync(DiagnosticOptions.Quick, null, new CancellationToken(true)));
    }

    private sealed class CancellingMetrics : IMetricsSource
    {
        public MetricSample? Latest => null;
        public ThermalSnapshot? LatestThermal => null;
        public PerformanceHistory History { get; } = new(10);
        public event EventHandler<MetricSample>? SampleAvailable { add { } remove { } }
        public Task<IReadOnlyList<MetricSample>> CollectAsync(TimeSpan duration, CancellationToken ct) => Task.FromCanceled<IReadOnlyList<MetricSample>>(ct);
        public Task<ThermalSnapshot> RefreshThermalAsync(CancellationToken ct) => Task.FromCanceled<ThermalSnapshot>(ct);
    }
}
