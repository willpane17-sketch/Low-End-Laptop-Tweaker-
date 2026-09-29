using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;
using Tickshift.Optimization;
using Tickshift.Optimization.Optimisations;
using Tickshift.Optimization.Recovery;
using Tickshift.Persistence;
using Tickshift.Tests.Fakes;
using Tickshift.Trading;

namespace Tickshift.Tests;

public sealed class TradingHarness : IDisposable
{
    public TradingHarness(AppSettings? settings = null)
    {
        Settings = new InMemorySettingsStore(settings ?? new AppSettings
        {
            ProtectedApplications = [KnownTradingApplications.ToProtected(KnownTradingApplications.All.First(a => a.Id == "mt5"))],
        });
        Paths = new AppDataPaths(Dir.Path);
        Journal = new ChangeJournal(Paths, new FakeClock());
        IOptimisation[] optimisations =
        [
            new BalancedWhenPluggedInOptimisation(Plans, Info),
            new HighPerformanceWhenPluggedInOptimisation(Plans, Info),
            new VisualEffectsOptimisation(Visuals),
        ];
        Engine = new OptimisationEngine(optimisations, Journal, new RecordStore<OptimisationRunRecord>(Paths.RunHistoryFile, r => r.StartedAt),
            new FakeRestorePoints(), null, Settings, Flag, new FakeElevation());
        Recovery = new RecoveryService(Journal, optimisations);
        Service = CreateService();
    }

    public TradingModeService CreateService() =>
        new(Engine, Recovery, Journal, Processes, Metrics, Notifications, Settings, Paths, Flag);

    public TempDir Dir { get; } = new();
    public InMemorySettingsStore Settings { get; }
    public AppDataPaths Paths { get; }
    public ChangeJournal Journal { get; }
    public FakePowerPlans Plans { get; } = new() { Active = PowerPlan.PowerSaverId };
    public FakeSystemInfo Info { get; } = new();
    public FakeVisualEffects Visuals { get; } = new();
    public FakeProcessProvider Processes { get; } = new();
    public FakeMetrics Metrics { get; } = new();
    public RecordingNotifications Notifications { get; } = new();
    public TradingSessionFlag Flag { get; } = new();
    public OptimisationEngine Engine { get; }
    public RecoveryService Recovery { get; }
    public TradingModeService Service { get; }

    public void Dispose()
    {
        Service.DisposeAsync().AsTask().Wait();
        Dir.Dispose();
    }
}

public class TradingModeTests
{
    [Fact]
    public async Task Activation_applies_temporary_changes_and_sets_shared_state()
    {
        using var h = new TradingHarness();
        var result = await h.Service.ActivateAsync(new TradingActivationOptions(true, true), null, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(TradingModeState.Active, h.Service.State);
        Assert.True(h.Flag.IsActive);
        Assert.Equal(PowerPlan.HighPerformanceId, h.Plans.Active);
        Assert.False(h.Visuals.State.AnyEnabled);
        Assert.All(h.Journal.GetAll(), r => Assert.True(r.Temporary));
        Assert.True(File.Exists(h.Paths.TradingSessionFile));
    }

    [Fact]
    public async Task Ending_restores_every_temporary_setting()
    {
        using var h = new TradingHarness();
        var originalVisuals = h.Visuals.State;
        await h.Service.ActivateAsync(new TradingActivationOptions(true, true), null, default);

        var result = await h.Service.EndAsync(default);

        Assert.True(result.IsSuccess);
        Assert.Equal(PowerPlan.PowerSaverId, h.Plans.Active);
        Assert.Equal(originalVisuals, h.Visuals.State);
        Assert.False(h.Flag.IsActive);
        Assert.Equal(TradingModeState.Inactive, h.Service.State);
        Assert.False(File.Exists(h.Paths.TradingSessionFile));
    }

    [Fact]
    public async Task Never_closes_or_restarts_any_application()
    {
        using var h = new TradingHarness();
        h.Processes.Processes.Add(FakeProcessProvider.Proc("terminal64", cpu: 60, category: ProcessCategory.ProtectedTrading));
        h.Processes.Processes.Add(FakeProcessProvider.Proc("hungrygame", cpu: 80, mem: 3L << 30));
        await h.Service.ActivateAsync(new TradingActivationOptions(false, false), null, default);
        h.Metrics.Push(new MetricSample(DateTimeOffset.Now, 99, 97, 1UL << 28, 5, null));

        var snapshot = h.Service.UpdateSnapshot();

        Assert.Equal(0, h.Processes.CloseRequests);
        Assert.DoesNotContain(snapshot.CloseSuggestions, s => s.Name == "terminal64");
        Assert.Contains(snapshot.CloseSuggestions, s => s.Name == "hungrygame");
        Assert.True(Assert.Single(snapshot.ProtectedApps).Running);
    }

    [Fact]
    public async Task Severe_pressure_notifies_once_not_repeatedly()
    {
        using var h = new TradingHarness();
        await h.Service.ActivateAsync(new TradingActivationOptions(false, false), null, default);
        foreach (var s in FakeMetrics.Series(20, 98, 60)) h.Metrics.Push(s);

        h.Service.UpdateSnapshot();
        h.Service.UpdateSnapshot();
        h.Service.UpdateSnapshot();

        Assert.Single(h.Notifications.Messages, m => m.Category == "trading");
    }

    [Fact]
    public async Task Double_activation_is_rejected()
    {
        using var h = new TradingHarness();
        await h.Service.ActivateAsync(new TradingActivationOptions(false, false), null, default);
        var second = await h.Service.ActivateAsync(new TradingActivationOptions(false, false), null, default);
        Assert.Equal(Core.OperationStatus.NotApplicable, second.Status);
    }

    [Fact]
    public async Task Temporary_settings_are_restored_after_an_unexpected_exit()
    {
        using var h = new TradingHarness();
        await h.Service.ActivateAsync(new TradingActivationOptions(true, true), null, default);
        Assert.Equal(PowerPlan.HighPerformanceId, h.Plans.Active);

        // Simulate a crash: a brand-new service instance starts with the session file still present.
        var afterRestart = h.CreateService();
        var result = await afterRestart.RecoverAfterUnexpectedExitAsync(default);

        Assert.NotNull(result);
        Assert.Equal(PowerPlan.PowerSaverId, h.Plans.Active);
        Assert.True(h.Visuals.State.AnyEnabled);
        Assert.False(File.Exists(h.Paths.TradingSessionFile));
    }

    [Fact]
    public async Task No_recovery_needed_after_a_clean_exit()
    {
        using var h = new TradingHarness();
        Assert.Null(await h.Service.RecoverAfterUnexpectedExitAsync(default));
    }

    [Fact]
    public async Task Dry_run_trading_mode_changes_nothing()
    {
        using var h = new TradingHarness(new AppSettings { DryRunMode = true });
        await h.Service.ActivateAsync(new TradingActivationOptions(true, true), null, default);
        Assert.Equal(PowerPlan.PowerSaverId, h.Plans.Active);
        Assert.True(h.Visuals.State.AnyEnabled);
        Assert.Empty(h.Journal.GetAll());
    }
}

public class PressureEvaluatorTests
{
    [Fact]
    public void Uses_hysteresis_to_avoid_flapping()
    {
        var p = new PressureEvaluator();
        var (cpu, _, becameSevere, _) = p.Evaluate(FakeMetrics.Series(5, 95, 50));
        Assert.Equal(PressureLevel.Severe, cpu);
        Assert.True(becameSevere);

        (cpu, _, becameSevere, _) = p.Evaluate(FakeMetrics.Series(5, 80, 50));
        Assert.Equal(PressureLevel.Severe, cpu); // still above the clear threshold
        Assert.False(becameSevere);

        (cpu, _, _, _) = p.Evaluate(FakeMetrics.Series(5, 40, 50));
        Assert.Equal(PressureLevel.Normal, cpu);
    }
}

public class ProtectedWebsiteTests
{
    [Theory]
    [InlineData("tradingview.com", "tradingview.com")]
    [InlineData("https://www.tradingview.com/chart/xyz/?symbol=EURUSD", "tradingview.com")]
    [InlineData("WWW.ForexFactory.com/calendar", "forexfactory.com")]
    [InlineData("[*.]investing.com", "investing.com")]
    [InlineData("webtrader.mybroker.co.uk", "webtrader.mybroker.co.uk")]
    public void Normalises_to_a_bare_domain(string input, string expected)
    {
        Assert.True(ProtectedWebsites.TryNormalise(input, out var domain));
        Assert.Equal(expected, domain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a domain")]
    [InlineData("https://user:pass@broker.com")]
    [InlineData("localhost")]
    [InlineData("javascript:alert(1)")]
    public void Rejects_invalid_input(string input) => Assert.False(ProtectedWebsites.TryNormalise(input, out _));

    [Fact]
    public void Generates_browser_specific_guidance_with_patterns()
    {
        var sites = new[] { new ProtectedWebsite("tradingview.com") };
        var edge = ProtectedWebsites.GuideFor(BrowserKind.Edge, sites);
        var chrome = ProtectedWebsites.GuideFor(BrowserKind.Chrome, sites);
        Assert.Contains("[*.]tradingview.com", edge.PatternsToAdd);
        Assert.Contains(edge.Steps, s => s.Contains("Never put these sites to sleep"));
        Assert.Contains(chrome.Steps, s => s.Contains("Always keep these sites active"));
    }

    [Fact]
    public void Custom_executables_must_be_exe_files()
    {
        Assert.Null(KnownTradingApplications.FromCustomPath(@"C:\scripts\run.bat"));
        var app = KnownTradingApplications.FromCustomPath(Path.Combine(Path.GetTempPath(), "BrokerTerminal.exe"));
        Assert.NotNull(app);
        Assert.Equal(Core.Settings.ProtectedAppKind.Custom, app!.Kind);
    }
}
