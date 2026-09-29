namespace Tickshift.Persistence;

/// <summary>A small bounded, retention-aware history list persisted as JSON.</summary>
public sealed class RecordStore<T> where T : class
{
    private readonly string _path;
    private readonly Func<T, DateTimeOffset> _timestamp;
    private readonly int _maxCount;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _sync = new();
    private List<T>? _items;

    public RecordStore(string path, Func<T, DateTimeOffset> timestamp, int maxCount = 500)
    {
        _path = path;
        _timestamp = timestamp;
        _maxCount = maxCount;
    }

    public event EventHandler? Changed;

    private List<T> Items => _items ??= AtomicJsonFile.Read<List<T>>(_path).Value ?? [];

    public IReadOnlyList<T> GetAll()
    {
        lock (_sync) return Items.OrderByDescending(_timestamp).ToList();
    }

    public async Task AppendAsync(T item, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                Items.Add(item);
                if (Items.Count > _maxCount)
                    _items = Items.OrderByDescending(_timestamp).Take(_maxCount).ToList();
            }

            await AtomicJsonFile.WriteAsync(_path, Items, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ReplaceAsync(Func<T, bool> match, T replacement, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                var index = Items.FindIndex(i => match(i));
                if (index >= 0) Items[index] = replacement; else Items.Add(replacement);
            }

            await AtomicJsonFile.WriteAsync(_path, Items, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task PruneAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync) _items = Items.Where(i => _timestamp(i) >= cutoff).ToList();
            await AtomicJsonFile.WriteAsync(_path, Items, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync) _items = [];
            await AtomicJsonFile.WriteAsync(_path, Items, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
