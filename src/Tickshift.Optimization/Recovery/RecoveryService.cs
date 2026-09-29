using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Errors;
using Tickshift.Persistence;

namespace Tickshift.Optimization.Recovery;

public sealed record RollbackItemResult(ChangeRecord Record, OperationResult Result);

public sealed record RollbackReport(IReadOnlyList<RollbackItemResult> Items)
{
    public int Restored => Items.Count(i => i.Result.IsSuccess);
    public int Conflicts => Items.Count(i => i.Result.Error == ErrorKind.Conflict);
    public int Failed => Items.Count(i => i.Result.Status == OperationStatus.Failed);
    public int NotReversible => Items.Count(i => i.Result.Status == OperationStatus.NotApplicable);
}

/// <summary>
/// Restores recorded changes. Rollbacks run newest-first, each one verified by its owner; conflicts
/// (something else changed the setting since) are left alone and reported, never overwritten.
/// Partially completed runs are handled because every change was journalled before it was attempted.
/// </summary>
public sealed class RecoveryService
{
    private readonly IChangeJournal _journal;
    private readonly Dictionary<string, IChangeRollbackHandler> _handlers;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RecoveryService(IChangeJournal journal, IEnumerable<IChangeRollbackHandler> handlers, ILogger<RecoveryService>? logger = null)
    {
        _journal = journal;
        _handlers = handlers.GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _logger = logger ?? NullLogger<RecoveryService>.Instance;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ChangeRecord> GetAll() => _journal.GetAll().OrderByDescending(r => r.Timestamp).ToList();

    public IReadOnlyList<ChangeRecord> GetActiveReversible() =>
        GetAll().Where(r => r.IsActiveReversible && r.Status != ChangeStatus.Pending).ToList();

    public IReadOnlyList<ChangeRecord> GetFailed() =>
        GetAll().Where(r => r.Status is ChangeStatus.Failed or ChangeStatus.VerificationFailed or ChangeStatus.RollbackFailed or ChangeStatus.Indeterminate).ToList();

    public Guid? LastRunWithReversibleChanges() =>
        GetActiveReversible().Where(r => !r.Temporary).OrderByDescending(r => r.Timestamp).Select(r => (Guid?)r.RunId).FirstOrDefault();

    public Task<RollbackReport> UndoRunAsync(Guid runId, CancellationToken ct) =>
        RollbackManyAsync(GetActiveReversible().Where(r => r.RunId == runId), ct);

    public async Task<RollbackReport> UndoLastAsync(CancellationToken ct)
    {
        var last = LastRunWithReversibleChanges();
        return last is null ? new RollbackReport([]) : await UndoRunAsync(last.Value, ct).ConfigureAwait(false);
    }

    /// <summary>Restores every reversible change still in effect (e.g. before uninstalling).</summary>
    public Task<RollbackReport> RestoreAllAsync(CancellationToken ct) => RollbackManyAsync(GetActiveReversible(), ct);

    public async Task<RollbackReport> RollbackManyAsync(IEnumerable<ChangeRecord> records, CancellationToken ct)
    {
        var results = new List<RollbackItemResult>();
        foreach (var record in records.OrderByDescending(r => r.Timestamp))
        {
            ct.ThrowIfCancellationRequested();
            results.Add(new RollbackItemResult(record, await RollbackAsync(record, ct).ConfigureAwait(false)));
        }

        return new RollbackReport(results);
    }

    public async Task<OperationResult> RollbackAsync(ChangeRecord record, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!record.Reversible)
                return OperationResult.NotApplicable("This change cannot be undone (for example, deleted temporary files).");
            if (!_handlers.TryGetValue(record.OptimisationId, out var handler))
                return OperationResult.Failure("No handler is available to restore this change.", ErrorKind.Unsupported);

            OperationResult result;
            try
            {
                result = await handler.RollbackAsync(record, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rollback failed for {Id}", record.Id);
                result = OperationResult.FromException(ex, "Restore");
            }

            var status = result.IsSuccess ? ChangeStatus.RolledBack
                : result.Error == ErrorKind.Conflict ? ChangeStatus.Superseded
                : result.Status == OperationStatus.NotApplicable ? record.Status
                : ChangeStatus.RollbackFailed;

            await _journal.UpdateAsync(record.Id, r => r with
            {
                Status = status,
                RolledBackAt = result.IsSuccess ? DateTimeOffset.Now : r.RolledBackAt,
                ErrorMessage = result.IsSuccess ? r.ErrorMessage : result.Message,
            }, CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation("Rollback of {Target}: {Status}", record.Target, status);
            return result;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Called at startup. Any change still marked Pending was interrupted (crash, power loss). The owner
    /// inspects the real system state so the record accurately says whether the change took effect.
    /// </summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken ct)
    {
        var pending = _journal.GetAll().Where(r => r.Status == ChangeStatus.Pending).ToList();
        foreach (var record in pending)
        {
            var state = InterruptedState.Unknown;
            if (_handlers.TryGetValue(record.OptimisationId, out var handler))
            {
                try { state = await handler.InspectInterruptedAsync(record, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Inspect failed for {Id}", record.Id); }
            }

            await _journal.UpdateAsync(record.Id, r => r with
            {
                Status = state switch
                {
                    InterruptedState.Applied => r.Reversible ? ChangeStatus.Applied : ChangeStatus.Completed,
                    InterruptedState.NotApplied => ChangeStatus.Failed,
                    _ => ChangeStatus.Indeterminate,
                },
                ErrorMessage = state switch
                {
                    InterruptedState.Applied => "Interrupted after the change took effect (not verified). You can restore it here.",
                    InterruptedState.NotApplied => "Interrupted before the change took effect. Nothing was changed.",
                    _ => "Interrupted; the final state could not be determined. Restoring will re-apply the original value where safe.",
                },
            }, ct).ConfigureAwait(false);
        }

        if (pending.Count > 0) Changed?.Invoke(this, EventArgs.Empty);
        return pending.Count;
    }
}
