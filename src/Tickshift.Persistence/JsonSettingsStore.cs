using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Settings;

namespace Tickshift.Persistence;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current;

    public JsonSettingsStore(AppDataPaths paths, ILogger<JsonSettingsStore>? logger = null)
    {
        _path = paths.SettingsFile;
        _logger = logger ?? NullLogger<JsonSettingsStore>.Instance;
        (_current, LoadOutcome) = Load();
    }

    public LoadOutcome LoadOutcome { get; }

    public AppSettings Current => _current;

    public event EventHandler<AppSettings>? Changed;

    private (AppSettings, LoadOutcome) Load()
    {
        var (value, outcome) = AtomicJsonFile.Read<AppSettings>(_path);
        if (outcome is LoadOutcome.CorruptReplacedWithDefault or LoadOutcome.RecoveredFromBackup)
            _logger.LogWarning("Settings file problem: {Outcome}", outcome);

        var settings = value ?? new AppSettings();
        if (settings.Normalise())
            _logger.LogInformation("Settings contained out-of-range values and were normalised.");
        return (settings, outcome);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        copy.Normalise();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.WriteAsync(_path, copy, ct).ConfigureAwait(false);
            _current = copy;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, copy);
    }

    public async Task UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        var copy = _current.Clone();
        mutate(copy);
        await SaveAsync(copy, ct).ConfigureAwait(false);
    }

    public async Task<AppSettings> ResetAsync(CancellationToken ct = default)
    {
        // Keep onboarding state so a reset does not force the welcome flow again unexpectedly.
        var fresh = new AppSettings { OnboardingCompleted = _current.OnboardingCompleted };
        await SaveAsync(fresh, ct).ConfigureAwait(false);
        return _current;
    }
}
