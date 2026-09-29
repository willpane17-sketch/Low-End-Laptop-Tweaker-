using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Errors;
using Tickshift.Core.Models;
using Tickshift.Diagnostics.Benchmarks;
using Tickshift.Optimization.Optimisations;
using Tickshift.Persistence;

namespace Tickshift.Optimization;

public sealed record OptimisationPlanItem(
    IOptimisation Optimisation,
    OptimisationAssessment Assessment,
    bool SelectedByDefault,
    string? BlockedReason)
{
    public string Id => Optimisation.Id;
    public bool CanSelect => Assessment.Applicable && BlockedReason is null;
}

public sealed record OptimisationPlan(Guid RunId, OptimisationPreset Preset, DateTimeOffset CreatedAt, IReadOnlyList<OptimisationPlanItem> Items);

public enum RunItemStatus { Verified, AppliedNotVerified, Failed, Skipped, NotApplicable, DryRun, Cancelled, Rejected }

public sealed record RunItemResult(string OptimisationId, string Title, RunItemStatus Status, string Message, string? VerificationMessage = null, string? MeasuredEffect = null, ErrorKind Error = ErrorKind.None);

public sealed record OptimisationRunRecord
{
    public Guid Id { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public OptimisationPreset Preset { get; init; }
    public string Source { get; init; } = "One-Click Optimise";
    public bool DryRun { get; init; }
    public bool Cancelled { get; init; }
    public List<RunItemResult> Items { get; init; } = [];
    public BenchmarkResult? Before { get; init; }
    public BenchmarkResult? After { get; init; }
    public string? RestorePointMessage { get; init; }

    public int VerifiedCount => Items.Count(i => i.Status == RunItemStatus.Verified);
    public int FailedCount => Items.Count(i => i.Status is RunItemStatus.Failed or RunItemStatus.AppliedNotVerified);
}

public sealed record ExecutionOptions(
    bool DryRun,
    bool CreateRestorePoint,
    bool CaptureBenchmarks,
    TimeSpan BenchmarkWindow,
    string Source = "One-Click Optimise",
    bool Temporary = false)
{
    public static ExecutionOptions Interactive(bool dryRun, bool restorePoint) => new(dryRun, restorePoint, true, TimeSpan.FromSeconds(10));
}

public sealed record ExecutionProgress(int Step, string StepName, string Detail, double Percent);

/// <summary>
/// The optimisation pipeline:
/// 1 analyse · 2 identify supported optimisations · 3 recovery snapshot · 4 show proposed changes ·
/// 5 apply approved changes · 6 verify each · 7 record history · 8 before/after measurements.
/// Steps 1, 2 and 4 are <see cref="AnalyseAsync"/>; the UI shows the plan; the rest is <see cref="ExecuteAsync"/>.
/// </summary>
public sealed class OptimisationEngine
{
    /// <summary>
    /// Strict allowlist. The engine refuses any optimisation whose ID is not listed here, even if one
    /// were registered by mistake. There is no mechanism to add operations at runtime.
    /// </summary>
    public static readonly IReadOnlySet<string> Allowlist = new HashSet<string>(StringComparer.Ordinal)
    {
        UserTempCleanupOptimisation.OptimisationId,
        ErrorReportCleanupOptimisation.OptimisationId,
        BrowserCacheCleanupOptimisation.OptimisationId,
        BalancedWhenPluggedInOptimisation.OptimisationId,
        HighPerformanceWhenPluggedInOptimisation.OptimisationId,
        VisualEffectsOptimisation.OptimisationId,
    };

    private readonly IReadOnlyList<IOptimisation> _catalogue;
    private readonly IChangeJournal _journal;
    private readonly RecordStore<OptimisationRunRecord> _history;
    private readonly IRestorePointService _restorePoints;
    private readonly BenchmarkService? _benchmarks;
    private readonly ISettingsStore _settings;
    private readonly ITradingSessionState _trading;
    private readonly IElevationState _elevation;
    private readonly ILogger _logger;

    public OptimisationEngine(
        IEnumerable<IOptimisation> optimisations,
        IChangeJournal journal,
        RecordStore<OptimisationRunRecord> history,
        IRestorePointService restorePoints,
        BenchmarkService? benchmarks,
        ISettingsStore settings,
        ITradingSessionState trading,
        IElevationState elevation,
        ILogger<OptimisationEngine>? logger = null)
    {
        _catalogue = optimisations.Where(o => Allowlist.Contains(o.Id)).ToList();
        _journal = journal;
        _history = history;
        _restorePoints = restorePoints;
        _benchmarks = benchmarks;
        _settings = settings;
        _trading = trading;
        _elevation = elevation;
        _logger = logger ?? NullLogger<OptimisationEngine>.Instance;
    }

    public IReadOnlyList<IOptimisation> Catalogue => _catalogue;

    public IOptimisation? Find(string id) => _catalogue.FirstOrDefault(o => o.Id == id);

    public async Task<OptimisationPlan> AnalyseAsync(OptimisationPreset preset, IProgress<ExecutionProgress>? progress, CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        var context = CreateContext(runId, "One-Click Optimise", false);
        var items = new List<OptimisationPlanItem>();
        var candidates = _catalogue.Where(o => o.AvailableIn.HasFlag(preset)).ToList();
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var o = candidates[i];
            progress?.Report(new ExecutionProgress(2, "Identifying supported optimisations", o.Title, 10 + 80.0 * i / Math.Max(1, candidates.Count)));
            OptimisationAssessment assessment;
            try
            {
                assessment = await o.AssessAsync(context, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Assessment failed for {Id}", o.Id);
                assessment = OptimisationAssessment.NotApplicable($"Could not be assessed: {ErrorClassifier.FriendlyMessage(ErrorClassifier.Classify(ex))}");
            }

            // Every catalogue item runs as a standard user today. Should an administrator item ever be added,
            // it must route through the elevated helper; until then it is blocked rather than run unelevated.
            string? blocked = o.RequiredPrivilege == PrivilegeLevel.Administrator && !_elevation.IsElevated
                ? "Requires administrator rights."
                : null;

            var selected = assessment.Applicable && blocked is null && o.SelectedByDefaultIn.HasFlag(preset) && !o.RequiresExplicitApproval
                && !(preset == OptimisationPreset.Safe && o.UncertaintyNote is not null && o.Category != OptimisationCategory.Cleanup);
            items.Add(new OptimisationPlanItem(o, assessment, selected, blocked));
        }

        progress?.Report(new ExecutionProgress(4, "Proposed changes ready", $"{items.Count(i => i.CanSelect)} applicable", 100));
        return new OptimisationPlan(runId, preset, DateTimeOffset.Now, items);
    }

    public async Task<OptimisationRunRecord> ExecuteAsync(
        OptimisationPlan plan,
        IReadOnlyCollection<string> approvedIds,
        ExecutionOptions options,
        IProgress<ExecutionProgress>? progress,
        CancellationToken ct)
    {
        var context = CreateContext(plan.RunId, options.Source, options.Temporary);
        var record = new OptimisationRunRecord
        {
            Id = plan.RunId,
            StartedAt = DateTimeOffset.Now,
            Preset = plan.Preset,
            Source = options.Source,
            DryRun = options.DryRun,
        };
        var results = new List<RunItemResult>();
        var approved = approvedIds.ToHashSet(StringComparer.Ordinal);
        var toRun = plan.Items.Where(i => approved.Contains(i.Id)).ToList();
        BenchmarkResult? before = null, after = null;
        string? restoreMessage = null;
        var cancelled = false;

        try
        {
            // Step 3 — recovery snapshot. Our own journal records every original value before changing
            // it (write-ahead); a Windows restore point is an optional extra.
            if (options.CaptureBenchmarks && _benchmarks is not null && !options.DryRun)
            {
                progress?.Report(new ExecutionProgress(3, "Measuring before changes", "Observing the system for a few seconds", 5));
                before = await _benchmarks.CaptureAsync("Before", options.BenchmarkWindow, ct).ConfigureAwait(false);
            }

            if (options.CreateRestorePoint && !options.DryRun && toRun.Any(i => i.Optimisation.Reversibility != Reversibility.NotReversible))
            {
                progress?.Report(new ExecutionProgress(3, "Creating recovery snapshot", "Windows restore point (may ask for administrator approval)", 15));
                var rp = await _restorePoints.CreateAsync(ct).ConfigureAwait(false);
                restoreMessage = rp.Message;
            }

            for (var i = 0; i < toRun.Count; i++)
            {
                var item = toRun[i];
                var pct = 20 + 60.0 * i / Math.Max(1, toRun.Count);
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    results.AddRange(toRun.Skip(i).Select(r => new RunItemResult(r.Id, r.Optimisation.Title, RunItemStatus.Cancelled, "Not started — the run was cancelled.")));
                    break;
                }

                progress?.Report(new ExecutionProgress(5, options.DryRun ? "Dry run — simulating" : "Applying approved changes", item.Optimisation.Title, pct));
                results.Add(await RunItemAsync(item, context, options, progress, pct, ct).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            var done = results.Select(r => r.OptimisationId).ToHashSet();
            results.AddRange(toRun.Where(r => !done.Contains(r.Id)).Select(r => new RunItemResult(r.Id, r.Optimisation.Title, RunItemStatus.Cancelled, "Not started — the run was cancelled.")));
        }

        // Items that were proposed but not approved are recorded as skipped for a complete report.
        results.AddRange(plan.Items.Where(i => !approved.Contains(i.Id)).Select(i =>
            new RunItemResult(i.Id, i.Optimisation.Title, i.Assessment.Applicable ? RunItemStatus.Skipped : RunItemStatus.NotApplicable,
                i.Assessment.Applicable ? "Not selected." : i.Assessment.Reason)));

        // Step 8 — after measurement.
        if (!cancelled && before is not null && _benchmarks is not null && results.Any(r => r.Status == RunItemStatus.Verified))
        {
            try
            {
                progress?.Report(new ExecutionProgress(8, "Measuring after changes", "Observing the system for a few seconds", 90));
                after = await _benchmarks.CaptureAsync("After", options.BenchmarkWindow, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "After-benchmark failed");
            }
        }

        record = record with
        {
            CompletedAt = DateTimeOffset.Now,
            Cancelled = cancelled,
            Items = results,
            Before = before,
            After = after,
            RestorePointMessage = restoreMessage,
        };

        // Step 7 — history.
        progress?.Report(new ExecutionProgress(7, "Recording history", string.Empty, 97));
        await _history.AppendAsync(record, CancellationToken.None).ConfigureAwait(false);
        if (!options.DryRun && record.VerifiedCount > 0 && !options.Temporary)
            await _settings.UpdateAsync(s => s.LastOptimisedAt = DateTimeOffset.Now, CancellationToken.None).ConfigureAwait(false);

        progress?.Report(new ExecutionProgress(8, cancelled ? "Cancelled" : "Complete", string.Empty, 100));
        return record;
    }

    private async Task<RunItemResult> RunItemAsync(OptimisationPlanItem item, OptimisationContext context, ExecutionOptions options,
        IProgress<ExecutionProgress>? progress, double pct, CancellationToken ct)
    {
        var o = item.Optimisation;
        if (!Allowlist.Contains(o.Id))
            return new RunItemResult(o.Id, o.Title, RunItemStatus.Rejected, "Rejected: not on the allowlist of supported operations.");

        try
        {
            // Re-assess right before applying: the system may have changed since the plan was shown.
            var assessment = await o.AssessAsync(context, ct).ConfigureAwait(false);
            if (!assessment.Applicable)
                return new RunItemResult(o.Id, o.Title, RunItemStatus.NotApplicable, assessment.Reason);

            if (options.DryRun)
                return new RunItemResult(o.Id, o.Title, RunItemStatus.DryRun, "Dry run — would: " + assessment.ProposedChange);

            var recorder = new JournalChangeRecorder(_journal, o.Id, context);
            var outcome = await o.ApplyAsync(context, assessment, recorder, ct).ConfigureAwait(false);
            if (outcome.Result.Status != OperationStatus.Succeeded)
            {
                var status = outcome.Result.Status == OperationStatus.Failed ? RunItemStatus.Failed : RunItemStatus.Skipped;
                return new RunItemResult(o.Id, o.Title, status, outcome.Result.Message, Error: outcome.Result.Error);
            }

            // Step 6 — verification. Success is only reported when verification confirms it.
            progress?.Report(new ExecutionProgress(6, "Verifying", o.Title, pct + 5));
            VerificationResult verification;
            try
            {
                verification = await o.VerifyAsync(context, assessment, outcome, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                verification = new VerificationResult(false, "Verification could not be completed: " + ErrorClassifier.FriendlyMessage(ErrorClassifier.Classify(ex)));
            }

            foreach (var changeId in outcome.ChangeIds)
            {
                await _journal.UpdateAsync(changeId, r => r with
                {
                    Status = verification.Verified ? (r.Reversible ? ChangeStatus.Verified : ChangeStatus.Completed) : ChangeStatus.VerificationFailed,
                    VerificationMessage = verification.Message,
                }, CancellationToken.None).ConfigureAwait(false);
            }

            return new RunItemResult(o.Id, o.Title, verification.Verified ? RunItemStatus.Verified : RunItemStatus.AppliedNotVerified,
                outcome.Result.Message, verification.Message, outcome.MeasuredEffect);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var kind = ErrorClassifier.Classify(ex);
            _logger.LogError(ex, "Optimisation {Id} failed ({Kind})", o.Id, kind);
            return new RunItemResult(o.Id, o.Title, RunItemStatus.Failed, ErrorClassifier.FriendlyMessage(kind), Error: kind);
        }
    }

    private OptimisationContext CreateContext(Guid runId, string source, bool temporary) =>
        new(runId, _settings.Current, _trading.IsActive, _elevation.IsElevated, source, temporary);

    /// <summary>
    /// Executes specific optimisations without the interactive plan UI (Trading Mode, automation).
    /// Only IDs from the allowlist are considered.
    /// </summary>
    public async Task<OptimisationRunRecord> RunDirectAsync(IEnumerable<string> ids, ExecutionOptions options, CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        var context = CreateContext(runId, options.Source, options.Temporary);
        var items = new List<OptimisationPlanItem>();
        foreach (var id in ids.Where(Allowlist.Contains).Distinct())
        {
            var o = Find(id);
            if (o is null) continue;
            var assessment = await o.AssessAsync(context, ct).ConfigureAwait(false);
            items.Add(new OptimisationPlanItem(o, assessment, true, null));
        }

        var plan = new OptimisationPlan(runId, OptimisationPreset.None, DateTimeOffset.Now, items);
        return await ExecuteAsync(plan, items.Where(i => i.Assessment.Applicable).Select(i => i.Id).ToList(), options, null, ct).ConfigureAwait(false);
    }
}
