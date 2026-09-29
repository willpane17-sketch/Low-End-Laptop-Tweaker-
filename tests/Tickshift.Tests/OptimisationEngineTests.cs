using Tickshift.Core.Abstractions;
using Tickshift.Core.Errors;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;
using Tickshift.Optimization;
using Tickshift.Optimization.Recovery;
using Tickshift.Persistence;
using Tickshift.Tests.Fakes;

namespace Tickshift.Tests;

public sealed class EngineHarness : IDisposable
{
    public EngineHarness(params IOptimisation[] optimisations)
    {
        Paths = new AppDataPaths(Dir.Path);
        Journal = new ChangeJournal(Paths, Clock);
        History = new RecordStore<OptimisationRunRecord>(Paths.RunHistoryFile, r => r.StartedAt);
        Engine = new OptimisationEngine(optimisations, Journal, History, RestorePoints, null, Settings, Trading, new FakeElevation());
        Recovery = new RecoveryService(Journal, optimisations);
    }

    public TempDir Dir { get; } = new();
    public AppDataPaths Paths { get; }
    public FakeClock Clock { get; } = new();
    public ChangeJournal Journal { get; }
    public RecordStore<OptimisationRunRecord> History { get; }
    public FakeRestorePoints RestorePoints { get; } = new();
    public InMemorySettingsStore Settings { get; } = new();
    public TradingSessionFlag Trading { get; } = new();
    public OptimisationEngine Engine { get; }
    public RecoveryService Recovery { get; }

    public static ExecutionOptions Options(bool dryRun = false, bool restorePoint = false) => new(dryRun, restorePoint, false, TimeSpan.Zero);

    public void Dispose() => Dir.Dispose();
}

public class OptimisationEngineTests
{
    [Fact]
    public async Task Analyse_preselects_only_applicable_default_items()
    {
        var applicable = new FakeOptimisation("ui.reduce-animations");
        var notApplicable = new FakeOptimisation("cleanup.user-temp") { Applicable = false };
        var explicitOnly = new FakeOptimisation("cleanup.browser-cache") { RequiresExplicitApproval = true };
        using var h = new EngineHarness(applicable, notApplicable, explicitOnly);

        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Balanced, null, CancellationToken.None);

        Assert.True(plan.Items.Single(i => i.Id == "ui.reduce-animations").SelectedByDefault);
        Assert.False(plan.Items.Single(i => i.Id == "cleanup.user-temp").SelectedByDefault);
        Assert.False(plan.Items.Single(i => i.Id == "cleanup.browser-cache").SelectedByDefault);
    }

    [Fact]
    public async Task Uncertain_items_are_not_preselected_in_safe_mode()
    {
        var uncertain = new FakeOptimisation("ui.reduce-animations") { UncertaintyNote = "benefit varies" };
        using var h = new EngineHarness(uncertain);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Safe, null, CancellationToken.None);
        Assert.False(plan.Items.Single().SelectedByDefault);
    }

    [Fact]
    public void Operations_outside_the_allowlist_are_never_registered()
    {
        using var h = new EngineHarness(new FakeOptimisation("registry.folklore-tweak"), new FakeOptimisation("ui.reduce-animations"));
        Assert.DoesNotContain(h.Engine.Catalogue, o => o.Id == "registry.folklore-tweak");
        Assert.Single(h.Engine.Catalogue);
    }

    [Fact]
    public async Task Verified_change_is_recorded_as_verified_with_history()
    {
        var o = new FakeOptimisation();
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Balanced, null, CancellationToken.None);

        var run = await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(), null, CancellationToken.None);

        Assert.Equal(RunItemStatus.Verified, run.Items.Single(i => i.OptimisationId == o.Id).Status);
        var record = Assert.Single(h.Journal.GetAll());
        Assert.Equal(ChangeStatus.Verified, record.Status);
        Assert.Equal("original", record.OriginalValue);
        Assert.Single(h.History.GetAll());
        Assert.NotNull(h.Settings.Current.LastOptimisedAt);
    }

    [Fact]
    public async Task Failed_verification_is_never_reported_as_success()
    {
        var o = new FakeOptimisation { VerifyResult = false };
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Balanced, null, CancellationToken.None);

        var run = await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(), null, CancellationToken.None);

        Assert.Equal(RunItemStatus.AppliedNotVerified, run.Items.Single(i => i.OptimisationId == o.Id).Status);
        Assert.Equal(ChangeStatus.VerificationFailed, h.Journal.GetAll().Single().Status);
        Assert.Equal(0, run.VerifiedCount);
        Assert.Null(h.Settings.Current.LastOptimisedAt);
    }

    [Fact]
    public async Task Dry_run_changes_nothing_and_writes_no_journal_entries()
    {
        var o = new FakeOptimisation();
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Balanced, null, CancellationToken.None);

        var run = await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(dryRun: true, restorePoint: true), null, CancellationToken.None);

        Assert.Equal(RunItemStatus.DryRun, run.Items.Single(i => i.OptimisationId == o.Id).Status);
        Assert.Equal(0, o.ApplyCount);
        Assert.Empty(h.Journal.GetAll());
        Assert.Equal(0, h.RestorePoints.Calls);
        Assert.True(run.DryRun);
    }

    [Fact]
    public async Task Access_denied_is_classified_and_recorded_as_failed()
    {
        var o = new FakeOptimisation { ThrowOnApply = new UnauthorizedAccessException("nope") };
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Balanced, null, CancellationToken.None);

        var run = await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(), null, CancellationToken.None);

        var item = run.Items.Single(i => i.OptimisationId == o.Id);
        Assert.Equal(RunItemStatus.Failed, item.Status);
        Assert.Equal(ErrorKind.AccessDenied, item.Error);
        Assert.Equal(ChangeStatus.Failed, h.Journal.GetAll().Single().Status);
    }

    [Fact]
    public async Task Items_that_stop_applying_before_execution_are_skipped()
    {
        var o = new FakeOptimisation();
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Balanced, null, CancellationToken.None);
        o.Applicable = false; // something else changed the system in the meantime

        var run = await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(), null, CancellationToken.None);

        Assert.Equal(RunItemStatus.NotApplicable, run.Items.Single(i => i.OptimisationId == o.Id).Status);
        Assert.Equal(0, o.ApplyCount);
    }

    [Fact]
    public async Task Cancellation_stops_remaining_items_and_keeps_completed_ones_recoverable()
    {
        using var cts = new CancellationTokenSource();
        var first = new FakeOptimisation("ui.reduce-animations");
        var second = new FakeOptimisation("power.balanced-on-mains") { DuringApply = _ => { cts.Cancel(); return Task.CompletedTask; } };
        var third = new FakeOptimisation("power.high-performance-on-mains");
        using var h = new EngineHarness(first, second, third);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Balanced, null, CancellationToken.None);

        var run = await h.Engine.ExecuteAsync(plan, [first.Id, second.Id, third.Id], EngineHarness.Options(), null, cts.Token);

        Assert.True(run.Cancelled);
        Assert.Equal(0, third.ApplyCount);
        Assert.Equal(RunItemStatus.Cancelled, run.Items.Single(i => i.OptimisationId == third.Id).Status);
        Assert.Contains(h.Recovery.GetActiveReversible(), r => r.OptimisationId == first.Id);
    }

    [Fact]
    public async Task Restore_point_is_requested_only_when_enabled_and_not_dry_run()
    {
        var o = new FakeOptimisation();
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Balanced, null, CancellationToken.None);
        var run = await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(restorePoint: true), null, CancellationToken.None);
        Assert.Equal(1, h.RestorePoints.Calls);
        Assert.Equal("created", run.RestorePointMessage);
    }

    [Fact]
    public async Task Unselected_items_are_reported_as_skipped()
    {
        var a = new FakeOptimisation("ui.reduce-animations");
        var b = new FakeOptimisation("power.balanced-on-mains");
        using var h = new EngineHarness(a, b);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Balanced, null, CancellationToken.None);
        var run = await h.Engine.ExecuteAsync(plan, [a.Id], EngineHarness.Options(), null, CancellationToken.None);
        Assert.Equal(RunItemStatus.Skipped, run.Items.Single(i => i.OptimisationId == b.Id).Status);
        Assert.Equal(0, b.ApplyCount);
    }
}

public class RecoveryServiceTests
{
    [Fact]
    public async Task Undo_restores_the_original_value_and_marks_rolled_back()
    {
        var o = new FakeOptimisation();
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Balanced, null, CancellationToken.None);
        var run = await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(), null, CancellationToken.None);

        var report = await h.Recovery.UndoRunAsync(run.Id, CancellationToken.None);

        Assert.Equal(1, report.Restored);
        Assert.Equal("original", o.Value);
        Assert.Equal(ChangeStatus.RolledBack, h.Journal.GetAll().Single().Status);
        Assert.Empty(h.Recovery.GetActiveReversible());
    }

    [Fact]
    public async Task External_changes_are_not_overwritten_on_rollback()
    {
        var o = new FakeOptimisation();
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Balanced, null, CancellationToken.None);
        await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(), null, CancellationToken.None);
        o.Value = "user-set-something-else";

        var report = await h.Recovery.UndoLastAsync(CancellationToken.None);

        Assert.Equal(1, report.Conflicts);
        Assert.Equal("user-set-something-else", o.Value);
        Assert.Equal(ChangeStatus.Superseded, h.Journal.GetAll().Single().Status);
    }

    [Fact]
    public async Task Rollback_runs_newest_first()
    {
        var order = new List<string>();
        var a = new OrderedFake("ui.reduce-animations", order);
        var b = new OrderedFake("power.balanced-on-mains", order);
        using var h = new EngineHarness(a, b);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Balanced, null, CancellationToken.None);
        var run = await h.Engine.ExecuteAsync(plan, [a.Id, b.Id], EngineHarness.Options(), null, CancellationToken.None);
        h.Clock.Now = h.Clock.Now.AddMinutes(1);

        await h.Recovery.UndoRunAsync(run.Id, CancellationToken.None);

        Assert.Equal([b.Id, a.Id], order);
    }

    [Fact]
    public async Task Irreversible_changes_are_honestly_reported()
    {
        var o = new FakeOptimisation("cleanup.user-temp") { Reversibility = Reversibility.NotReversible };
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Safe, null, CancellationToken.None);
        await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(), null, CancellationToken.None);

        var record = h.Journal.GetAll().Single();
        Assert.Equal(ChangeStatus.Completed, record.Status);
        var result = await h.Recovery.RollbackAsync(record, CancellationToken.None);
        Assert.Equal(Core.OperationStatus.NotApplicable, result.Status);
        Assert.Equal(0, o.RollbackCount);
    }

    [Fact]
    public async Task Interrupted_changes_are_resolved_from_real_state_at_startup()
    {
        var applied = new FakeOptimisation("ui.reduce-animations") { Value = "changed" };
        var notApplied = new FakeOptimisation("power.balanced-on-mains");
        using var h = new EngineHarness(applied, notApplied);
        await h.Journal.AddAsync(new ChangeRecord { OptimisationId = applied.Id, Description = "a", Target = "t", Status = ChangeStatus.Pending, Reversible = true, Timestamp = h.Clock.Now });
        await h.Journal.AddAsync(new ChangeRecord { OptimisationId = notApplied.Id, Description = "b", Target = "t", Status = ChangeStatus.Pending, Reversible = true, Timestamp = h.Clock.Now });
        await h.Journal.AddAsync(new ChangeRecord { OptimisationId = "unknown.handler", Description = "c", Target = "t", Status = ChangeStatus.Pending, Reversible = true, Timestamp = h.Clock.Now });

        var count = await h.Recovery.RecoverInterruptedAsync(CancellationToken.None);

        Assert.Equal(3, count);
        var records = h.Journal.GetAll();
        Assert.Equal(ChangeStatus.Applied, records.Single(r => r.OptimisationId == applied.Id).Status);
        Assert.Equal(ChangeStatus.Failed, records.Single(r => r.OptimisationId == notApplied.Id).Status);
        Assert.Equal(ChangeStatus.Indeterminate, records.Single(r => r.OptimisationId == "unknown.handler").Status);
    }

    [Fact]
    public async Task Journal_survives_restart()
    {
        var o = new FakeOptimisation();
        using var h = new EngineHarness(o);
        var plan = await h.Engine.AnalyseAsync(OptimisationPreset.Balanced, null, CancellationToken.None);
        await h.Engine.ExecuteAsync(plan, [o.Id], EngineHarness.Options(), null, CancellationToken.None);

        var reloaded = new ChangeJournal(h.Paths, h.Clock);
        Assert.Equal(ChangeStatus.Verified, reloaded.GetAll().Single().Status);
    }

    [Fact]
    public async Task Pruning_never_discards_active_reversible_changes()
    {
        using var h = new EngineHarness();
        await h.Journal.AddAsync(new ChangeRecord { OptimisationId = "x", Description = "old active", Target = "t", Status = ChangeStatus.Verified, Reversible = true, Timestamp = h.Clock.Now.AddDays(-400) });
        await h.Journal.AddAsync(new ChangeRecord { OptimisationId = "x", Description = "old done", Target = "t", Status = ChangeStatus.RolledBack, Reversible = true, Timestamp = h.Clock.Now.AddDays(-400) });

        await h.Journal.PruneAsync(30);

        var remaining = Assert.Single(h.Journal.GetAll());
        Assert.Equal("old active", remaining.Description);
    }

    private sealed class OrderedFake(string id, List<string> order) : IOptimisation
    {
        private readonly FakeOptimisation _inner = new(id);
        public string Id => id;
        public string Title => _inner.Title;
        public string Description => _inner.Description;
        public OptimisationCategory Category => _inner.Category;
        public RiskLevel Risk => _inner.Risk;
        public PrivilegeLevel RequiredPrivilege => _inner.RequiredPrivilege;
        public Reversibility Reversibility => _inner.Reversibility;
        public IReadOnlyList<string> Preconditions => _inner.Preconditions;
        public OptimisationPreset AvailableIn => _inner.AvailableIn;
        public OptimisationPreset SelectedByDefaultIn => _inner.SelectedByDefaultIn;
        public string? UncertaintyNote => null;
        public bool RequiresExplicitApproval => false;
        public Task<OptimisationAssessment> AssessAsync(OptimisationContext c, CancellationToken ct) => _inner.AssessAsync(c, ct);
        public Task<ApplyOutcome> ApplyAsync(OptimisationContext c, OptimisationAssessment a, IChangeRecorder r, CancellationToken ct) => _inner.ApplyAsync(c, a, r, ct);
        public Task<VerificationResult> VerifyAsync(OptimisationContext c, OptimisationAssessment a, ApplyOutcome o, CancellationToken ct) => _inner.VerifyAsync(c, a, o, ct);
        public Task<InterruptedState> InspectInterruptedAsync(ChangeRecord r, CancellationToken ct) => _inner.InspectInterruptedAsync(r, ct);

        public Task<Core.OperationResult> RollbackAsync(ChangeRecord record, CancellationToken ct)
        {
            order.Add(id);
            return _inner.RollbackAsync(record, ct);
        }
    }
}
