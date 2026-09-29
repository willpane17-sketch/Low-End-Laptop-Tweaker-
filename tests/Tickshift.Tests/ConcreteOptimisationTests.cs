using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;
using Tickshift.Optimization;
using Tickshift.Optimization.Optimisations;
using Tickshift.Persistence;
using Tickshift.Tests.Fakes;

namespace Tickshift.Tests;

public class PowerPlanOptimisationTests
{
    private static OptimisationContext Ctx() => new(Guid.NewGuid(), new AppSettings(), false, false);

    [Fact]
    public async Task Balanced_switch_applies_only_on_mains_with_power_saver()
    {
        var plans = new FakePowerPlans { Active = PowerPlan.PowerSaverId };
        var info = new FakeSystemInfo();
        var o = new BalancedWhenPluggedInOptimisation(plans, info);
        Assert.True((await o.AssessAsync(Ctx(), default)).Applicable);

        info.Power = info.Power with { Source = PowerSource.Battery };
        Assert.False((await o.AssessAsync(Ctx(), default)).Applicable);
    }

    [Fact]
    public async Task High_performance_is_never_applied_on_battery()
    {
        var info = new FakeSystemInfo { Power = new PowerStatus(PowerSource.Battery, true, 50, false, null, null) };
        var o = new HighPerformanceWhenPluggedInOptimisation(new FakePowerPlans(), info);
        var a = await o.AssessAsync(Ctx(), default);
        Assert.False(a.Applicable);
        Assert.Contains("battery", a.Reason);
    }

    [Fact]
    public async Task High_performance_is_not_applicable_when_plan_missing()
    {
        var plans = new FakePowerPlans();
        plans.Plans.RemoveAll(p => p.Id == PowerPlan.HighPerformanceId);
        var a = await new HighPerformanceWhenPluggedInOptimisation(plans, new FakeSystemInfo()).AssessAsync(Ctx(), default);
        Assert.False(a.Applicable);
        Assert.Contains("Modern Standby", a.Reason);
    }

    [Fact]
    public async Task Custom_plans_are_left_alone()
    {
        var custom = Guid.NewGuid();
        var plans = new FakePowerPlans { Active = custom };
        plans.Plans.Add(new PowerPlan(custom, "OEM Silent"));
        var a = await new HighPerformanceWhenPluggedInOptimisation(plans, new FakeSystemInfo()).AssessAsync(Ctx(), default);
        Assert.False(a.Applicable);
    }

    [Fact]
    public async Task Apply_verify_and_rollback_round_trip()
    {
        var plans = new FakePowerPlans { Active = PowerPlan.BalancedId };
        var o = new HighPerformanceWhenPluggedInOptimisation(plans, new FakeSystemInfo());
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Trading, null, default);

        var run = await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(), null, default);
        Assert.Equal(RunItemStatus.Verified, run.Items.Single(i => i.OptimisationId == o.Id).Status);
        Assert.Equal(PowerPlan.HighPerformanceId, plans.Active);

        var report = await h.Recovery.UndoRunAsync(run.Id, default);
        Assert.Equal(1, report.Restored);
        Assert.Equal(PowerPlan.BalancedId, plans.Active);
    }

    [Fact]
    public async Task Policy_that_blocks_the_change_fails_verification()
    {
        var plans = new FakePowerPlans { Active = PowerPlan.BalancedId, IgnoreChanges = true };
        var o = new HighPerformanceWhenPluggedInOptimisation(plans, new FakeSystemInfo());
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Trading, null, default);
        var run = await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(), null, default);
        Assert.Equal(RunItemStatus.AppliedNotVerified, run.Items.Single(i => i.OptimisationId == o.Id).Status);
    }

    [Fact]
    public async Task Rollback_does_not_override_a_plan_the_user_chose_later()
    {
        var plans = new FakePowerPlans { Active = PowerPlan.BalancedId };
        var o = new HighPerformanceWhenPluggedInOptimisation(plans, new FakeSystemInfo());
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Trading, null, default);
        var run = await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(), null, default);
        plans.Active = PowerPlan.PowerSaverId; // user changed it themselves

        var report = await h.Recovery.UndoRunAsync(run.Id, default);

        Assert.Equal(1, report.Conflicts);
        Assert.Equal(PowerPlan.PowerSaverId, plans.Active);
    }
}

public class VisualEffectsOptimisationTests
{
    [Fact]
    public async Task Restores_each_original_setting()
    {
        var original = new VisualEffectsState(true, false, true, false, true, true);
        var fx = new FakeVisualEffects { State = original };
        var o = new VisualEffectsOptimisation(fx);
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Balanced, null, default);
        var run = await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(), null, default);

        Assert.False(fx.State.AnyEnabled);
        await h.Recovery.UndoRunAsync(run.Id, default);
        Assert.Equal(original, fx.State);
    }

    [Fact]
    public async Task Not_applicable_when_already_reduced()
    {
        var o = new VisualEffectsOptimisation(new FakeVisualEffects { State = VisualEffectsState.Reduced });
        Assert.False((await o.AssessAsync(new OptimisationContext(Guid.NewGuid(), new AppSettings(), false, false), default)).Applicable);
    }
}

public sealed class SandboxLocations(params string[] temp) : ICleanupLocations
{
    public IReadOnlyList<string> UserTemp => temp;
    public IReadOnlyList<string> ErrorReports => [];
    public List<string> Cache { get; } = [];
    public IReadOnlyList<string> BrowserCache(BrowserKind kind) => kind == BrowserKind.Chrome ? Cache : [];
}

/// <summary>Integration tests for real file deletion — confined to a sandbox directory.</summary>
public class FileCleanupTests
{
    private static OptimisationContext Ctx(bool trading = false) => new(Guid.NewGuid(), new AppSettings(), trading, false);

    [Fact]
    public async Task Deletes_only_old_files_and_verifies()
    {
        using var dir = new TempDir();
        var old = dir.File("old/a.tmp", new string('x', 6 * 1024 * 1024), TimeSpan.FromDays(3));
        var fresh = dir.File("fresh.tmp", new string('x', 1024), TimeSpan.FromMinutes(5));
        var o = new UserTempCleanupOptimisation(new SandboxLocations(dir.Path));
        using var h = new EngineHarness(o);

        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Safe, null, default);
        Assert.True(plan.Items.Single().Assessment.Applicable);
        var run = await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(), null, default);

        Assert.Equal(RunItemStatus.Verified, run.Items.Single(i => i.OptimisationId == o.Id).Status);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.False(h.Journal.GetAll().Single().Reversible);
    }

    [Fact]
    public async Task Small_amounts_are_not_worth_cleaning()
    {
        using var dir = new TempDir();
        dir.File("tiny.tmp", "x", TimeSpan.FromDays(10));
        var a = await new UserTempCleanupOptimisation(new SandboxLocations(dir.Path)).AssessAsync(Ctx(), default);
        Assert.False(a.Applicable);
    }

    [Fact]
    public async Task Never_follows_symbolic_links_out_of_the_cleanup_root()
    {
        using var dir = new TempDir();
        using var outside = new TempDir();
        var precious = outside.File("important.docx", new string('x', 6 * 1024 * 1024), TimeSpan.FromDays(30));
        dir.File("junk.tmp", new string('x', 6 * 1024 * 1024), TimeSpan.FromDays(30));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(dir.Path, "link"), outside.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // symlink creation not permitted on this host (e.g. Windows without developer mode)
        }

        var o = new UserTempCleanupOptimisation(new SandboxLocations(dir.Path));
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Safe, null, default);
        await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(), null, default);

        Assert.True(File.Exists(precious));
    }

    [Fact]
    public async Task Browser_cache_requires_the_browser_to_be_closed_by_the_user()
    {
        using var dir = new TempDir();
        var locations = new SandboxLocations();
        locations.Cache.Add(dir.Path);
        dir.File("Cache_Data/f_000001", new string('x', 60 * 1024 * 1024));
        var browsers = new FakeBrowsers();
        browsers.Running.Add(BrowserKind.Chrome);
        var o = new BrowserCacheCleanupOptimisation(locations, browsers);

        var blocked = await o.AssessAsync(Ctx(), default);
        Assert.False(blocked.Applicable);
        Assert.Contains("close", blocked.Reason);
        Assert.True(o.RequiresExplicitApproval);
        Assert.False(((IOptimisation)o).SuitableForUnattended);

        browsers.Running.Clear();
        Assert.True((await o.AssessAsync(Ctx(), default)).Applicable);
        Assert.False((await o.AssessAsync(Ctx(trading: true), default)).Applicable);
    }
}
