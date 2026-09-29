using Tickshift.Core.Abstractions;

namespace Tickshift.Persistence;

public enum ChangeStatus
{
    /// <summary>Recorded before the change was attempted (write-ahead). If found at startup, the change was interrupted.</summary>
    Pending,
    Applied,
    Verified,
    VerificationFailed,
    Failed,
    RolledBack,
    RollbackFailed,
    /// <summary>Interrupted change whose final state could not be determined automatically.</summary>
    Indeterminate,
    /// <summary>The setting was changed by something else after TICKSHIFT changed it, so it was deliberately left alone.</summary>
    Superseded,
    /// <summary>A change that cannot be undone (for example, deleted temporary files).</summary>
    Completed,
}

/// <summary>One auditable system modification.</summary>
public sealed record ChangeRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RunId { get; init; }
    public required string OptimisationId { get; init; }
    public required string Description { get; init; }
    public required string Target { get; init; }
    public string? OriginalValue { get; init; }
    public string? NewValue { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public ChangeStatus Status { get; init; }
    public bool Reversible { get; init; }
    /// <summary>Opaque data the owning optimisation needs to roll back (JSON).</summary>
    public string? RollbackData { get; init; }
    public string? VerificationMessage { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset? RolledBackAt { get; init; }
    /// <summary>True for Trading Mode changes that are reverted when the session ends.</summary>
    public bool Temporary { get; init; }
    public string Source { get; init; } = "Optimisation";

    public bool IsActiveReversible => Reversible && Status is ChangeStatus.Applied or ChangeStatus.Verified or ChangeStatus.VerificationFailed or ChangeStatus.RollbackFailed or ChangeStatus.Indeterminate or ChangeStatus.Pending;
}

public interface IChangeJournal
{
    IReadOnlyList<ChangeRecord> GetAll();
    Task<ChangeRecord> AddAsync(ChangeRecord record, CancellationToken ct = default);
    Task<ChangeRecord> UpdateAsync(Guid id, Func<ChangeRecord, ChangeRecord> update, CancellationToken ct = default);
    Task PruneAsync(int retentionDays, CancellationToken ct = default);
    Task ClearInactiveAsync(CancellationToken ct = default);
}

/// <summary>
/// Persistent write-ahead journal. Every mutation is flushed to disk before the caller continues so
/// that an unexpected shutdown never loses the information needed to restore a setting.
/// </summary>
public sealed class ChangeJournal : IChangeJournal
{
    private const int MaxRecords = 5000;
    private readonly string _path;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ChangeRecord> _records;

    public ChangeJournal(AppDataPaths paths, IClock clock)
    {
        _path = paths.JournalFile;
        _clock = clock;
        _records = AtomicJsonFile.Read<List<ChangeRecord>>(_path).Value ?? [];
    }

    public IReadOnlyList<ChangeRecord> GetAll()
    {
        lock (_records) return _records.ToList();
    }

    public async Task<ChangeRecord> AddAsync(ChangeRecord record, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_records) _records.Add(record);
            await PersistAsync().ConfigureAwait(false);
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChangeRecord> UpdateAsync(Guid id, Func<ChangeRecord, ChangeRecord> update, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ChangeRecord updated;
            lock (_records)
            {
                var index = _records.FindIndex(r => r.Id == id);
                if (index < 0) throw new KeyNotFoundException($"Change record {id} not found.");
                updated = update(_records[index]) with { Id = id };
                _records[index] = updated;
            }

            await PersistAsync().ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes old records but never discards the information needed to undo an active change.</summary>
    public async Task PruneAsync(int retentionDays, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cutoff = _clock.Now.AddDays(-retentionDays);
            lock (_records)
            {
                _records = _records.Where(r => r.IsActiveReversible || r.Timestamp >= cutoff).ToList();
                if (_records.Count > MaxRecords)
                {
                    var excess = _records.Count - MaxRecords;
                    var removable = _records.Where(r => !r.IsActiveReversible).OrderBy(r => r.Timestamp).Take(excess).Select(r => r.Id).ToHashSet();
                    _records = _records.Where(r => !removable.Contains(r.Id)).ToList();
                }
            }

            await PersistAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearInactiveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_records) _records = _records.Where(r => r.IsActiveReversible).ToList();
            await PersistAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task PersistAsync()
    {
        List<ChangeRecord> snapshot;
        lock (_records) snapshot = _records.ToList();
        return AtomicJsonFile.WriteAsync(_path, snapshot);
    }
}
