using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;

namespace Tickshift.Diagnostics.Monitoring;

public interface IMetricsSource
{
    MetricSample? Latest { get; }
    ThermalSnapshot? LatestThermal { get; }
    PerformanceHistory History { get; }
    event EventHandler<MetricSample>? SampleAvailable;

    /// <summary>Collects samples at 1-second resolution for a fixed observation window (used by diagnostics and benchmarks).</summary>
    Task<IReadOnlyList<MetricSample>> CollectAsync(TimeSpan duration, CancellationToken ct);

    /// <summary>Reads the thermal sensors now instead of waiting for the next low-frequency poll.</summary>
    Task<ThermalSnapshot> RefreshThermalAsync(CancellationToken ct);
}

/// <summary>
/// The single background sampler for the whole app. One PeriodicTimer, one thread-pool callback per
/// tick; every page reads from here instead of polling hardware itself. Temperature is polled at a
/// lower rate, and the whole loop slows down when the window is hidden.
/// </summary>
public sealed class MetricsMonitor : IMetricsSource, IAsyncDisposable
{
    private static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ThermalInterval = TimeSpan.FromSeconds(10);

    private readonly ISystemSampler _sampler;
    private readonly ISystemInfoProvider _info;
    private readonly IThermalSensorProvider _thermal;
    private readonly ISettingsStore _settings;
    private readonly ILogger _logger;
    private readonly Lock _collectorsGate = new();
    private readonly List<List<MetricSample>> _collectors = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private PeriodicTimer? _timer;
    private DateTimeOffset _lastThermal = DateTimeOffset.MinValue;
    private volatile bool _backgroundMode;
    private int _fastModeRequests;

    public MetricsMonitor(ISystemSampler sampler, ISystemInfoProvider info, IThermalSensorProvider thermal,
        ISettingsStore settings, PerformanceHistory history, ILogger<MetricsMonitor>? logger = null)
    {
        _sampler = sampler;
        _info = info;
        _thermal = thermal;
        _settings = settings;
        History = history;
        _logger = logger ?? NullLogger<MetricsMonitor>.Instance;
    }

    public MetricSample? Latest { get; private set; }
    public ThermalSnapshot? LatestThermal { get; private set; }
    public PerformanceHistory History { get; }
    public event EventHandler<MetricSample>? SampleAvailable;
    public event EventHandler<ThermalSnapshot>? ThermalUpdated;

    /// <summary>When true (window hidden/minimised to tray), sampling slows to reduce overhead.</summary>
    public bool BackgroundMode
    {
        get => _backgroundMode;
        set
        {
            _backgroundMode = value;
            ApplyInterval();
        }
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _sampler.SampleCpuPercent(); // establish the CPU baseline
        _timer = new PeriodicTimer(CurrentInterval());
        _loop = Task.Run(() => RunAsync(_timer, _cts.Token));
    }

    private TimeSpan CurrentInterval()
    {
        if (Volatile.Read(ref _fastModeRequests) > 0) return TimeSpan.FromSeconds(1);
        var configured = TimeSpan.FromSeconds(Math.Clamp(_settings.Current.RefreshIntervalSeconds, 1, 30));
        return _backgroundMode && configured < BackgroundInterval ? BackgroundInterval : configured;
    }

    /// <summary>Applies a changed interval immediately (settings change, window shown/hidden, fast collection).</summary>
    public void ApplyInterval()
    {
        var timer = _timer;
        var interval = CurrentInterval();
        try
        {
            if (timer is not null && timer.Period != interval) timer.Period = interval;
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Metrics sampling failed");
                }

                var interval = CurrentInterval();
                if (timer.Period != interval) timer.Period = interval;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            timer.Dispose();
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        if (now - _lastThermal >= ThermalInterval)
        {
            _lastThermal = now;
            await RefreshThermalAsync(ct).ConfigureAwait(false);
        }

        var memory = _info.GetMemory();
        var sample = new MetricSample(
            now,
            Math.Round(_sampler.SampleCpuPercent(), 1),
            memory.MemoryLoadPercent,
            memory.AvailablePhysicalBytes,
            _sampler.SampleDiskActivePercent() is { } d ? Math.Round(d, 1) : null,
            LatestThermal?.PrimaryCelsius);

        Latest = sample;
        History.Add(sample);
        lock (_collectorsGate)
        {
            foreach (var c in _collectors) c.Add(sample);
        }

        SampleAvailable?.Invoke(this, sample);
    }

    public async Task<ThermalSnapshot> RefreshThermalAsync(CancellationToken ct)
    {
        try
        {
            LatestThermal = await _thermal.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("Thermal read failed: {Message}", ex.Message);
            LatestThermal = ThermalSnapshot.Unavailable(_thermal.Name, "Temperature could not be read.");
        }

        ThermalUpdated?.Invoke(this, LatestThermal);
        return LatestThermal;
    }

    public async Task<IReadOnlyList<MetricSample>> CollectAsync(TimeSpan duration, CancellationToken ct)
    {
        var bucket = new List<MetricSample>();
        lock (_collectorsGate) _collectors.Add(bucket);
        Interlocked.Increment(ref _fastModeRequests);
        ApplyInterval();
        try
        {
            await Task.Delay(duration, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _fastModeRequests);
            ApplyInterval();
            lock (_collectorsGate) _collectors.Remove(bucket);
        }

        lock (_collectorsGate) return bucket.ToList();
    }

    public async ValueTask DisposeAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return; // already disposed (safe to call twice)
        await cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        cts.Dispose();
    }
}
