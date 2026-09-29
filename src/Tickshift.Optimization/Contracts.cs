using Tickshift.Core;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;
using Tickshift.Persistence;

namespace Tickshift.Optimization;

public enum OptimisationCategory { Cleanup, Power, Interface, Browser }

/// <summary>Context for one assessment/apply pass.</summary>
public sealed record OptimisationContext(
    Guid RunId,
    AppSettings Settings,
    bool TradingModeActive,
    bool IsElevated,
    string Source = "One-Click Optimise",
    bool Temporary = false);

/// <summary>Result of checking whether an optimisation applies to this machine right now.</summary>
public sealed record OptimisationAssessment(
    bool Applicable,
    string Reason,
    string ProposedChange,
    string? EstimatedEffect = null,
    string? State = null)
{
    public static OptimisationAssessment NotApplicable(string reason) => new(false, reason, "No change needed.");
}

/// <param name="VerificationData">Opaque data the optimisation needs to verify its own result (not shown to users).</param>
public sealed record ApplyOutcome(OperationResult Result, IReadOnlyList<Guid> ChangeIds, string? MeasuredEffect = null, string? VerificationData = null);

public sealed record VerificationResult(bool Verified, string Message);

public enum InterruptedState { NotApplied, Applied, Unknown }

/// <summary>Anything that writes change records must be able to undo (or honestly refuse to undo) them.</summary>
public interface IChangeRollbackHandler
{
    string Id { get; }

    /// <summary>Restores the original value from the record. Must detect external changes (conflicts) and not overwrite them.</summary>
    Task<OperationResult> RollbackAsync(ChangeRecord record, CancellationToken ct);

    /// <summary>After a crash, determines whether a Pending change actually took effect.</summary>
    Task<InterruptedState> InspectInterruptedAsync(ChangeRecord record, CancellationToken ct);
}

/// <summary>
/// One allowlisted optimisation. Every implementation provides: identity and description, preconditions,
/// risk, required privileges, an applicability check, apply, verification, rollback when possible,
/// logging (through the change recorder) and error handling (exceptions are classified by the engine).
/// </summary>
public interface IOptimisation : IChangeRollbackHandler
{
    string Title { get; }
    string Description { get; }
    OptimisationCategory Category { get; }
    RiskLevel Risk { get; }
    PrivilegeLevel RequiredPrivilege { get; }
    Reversibility Reversibility { get; }
    IReadOnlyList<string> Preconditions { get; }

    /// <summary>Presets in which this optimisation is offered.</summary>
    OptimisationPreset AvailableIn { get; }

    /// <summary>Presets in which it is pre-selected. Uncertain-benefit items are never pre-selected in Safe.</summary>
    OptimisationPreset SelectedByDefaultIn { get; }

    /// <summary>When the benefit is uncertain, the UI shows this note and the item stays optional.</summary>
    string? UncertaintyNote { get; }

    /// <summary>Items that must never run without the user explicitly ticking them (e.g. cache deletion).</summary>
    bool RequiresExplicitApproval { get; }

    bool SuitableForUnattended => Risk == RiskLevel.Low && Reversibility == Reversibility.FullyReversible
        && RequiredPrivilege == PrivilegeLevel.StandardUser && !RequiresExplicitApproval;

    Task<OptimisationAssessment> AssessAsync(OptimisationContext context, CancellationToken ct);
    Task<ApplyOutcome> ApplyAsync(OptimisationContext context, OptimisationAssessment assessment, IChangeRecorder recorder, CancellationToken ct);
    Task<VerificationResult> VerifyAsync(OptimisationContext context, OptimisationAssessment assessment, ApplyOutcome outcome, CancellationToken ct);
}

/// <summary>Write-ahead change recording: Begin is persisted before the system is touched.</summary>
public interface IChangeRecorder
{
    Task<Guid> BeginAsync(string target, string description, string? originalValue, string? newValue, bool reversible, string? rollbackData, CancellationToken ct);
    Task MarkAppliedAsync(Guid changeId, CancellationToken ct);
    Task MarkFailedAsync(Guid changeId, string error, CancellationToken ct);
}

internal sealed class JournalChangeRecorder(IChangeJournal journal, string optimisationId, OptimisationContext context) : IChangeRecorder
{
    public async Task<Guid> BeginAsync(string target, string description, string? originalValue, string? newValue, bool reversible, string? rollbackData, CancellationToken ct)
    {
        var record = await journal.AddAsync(new ChangeRecord
        {
            RunId = context.RunId,
            OptimisationId = optimisationId,
            Description = description,
            Target = target,
            OriginalValue = originalValue,
            NewValue = newValue,
            Timestamp = DateTimeOffset.Now,
            Status = ChangeStatus.Pending,
            Reversible = reversible,
            RollbackData = rollbackData,
            Temporary = context.Temporary,
            Source = context.Source,
        }, ct).ConfigureAwait(false);
        return record.Id;
    }

    public Task MarkAppliedAsync(Guid changeId, CancellationToken ct) =>
        journal.UpdateAsync(changeId, r => r with { Status = r.Reversible ? ChangeStatus.Applied : ChangeStatus.Completed }, ct);

    public Task MarkFailedAsync(Guid changeId, string error, CancellationToken ct) =>
        journal.UpdateAsync(changeId, r => r with { Status = ChangeStatus.Failed, ErrorMessage = error }, ct);
}

/// <summary>Records nothing — used for dry runs so no journal entries are created.</summary>
internal sealed class NullChangeRecorder : IChangeRecorder
{
    public Task<Guid> BeginAsync(string target, string description, string? originalValue, string? newValue, bool reversible, string? rollbackData, CancellationToken ct) => Task.FromResult(Guid.Empty);
    public Task MarkAppliedAsync(Guid changeId, CancellationToken ct) => Task.CompletedTask;
    public Task MarkFailedAsync(Guid changeId, string error, CancellationToken ct) => Task.CompletedTask;
}
