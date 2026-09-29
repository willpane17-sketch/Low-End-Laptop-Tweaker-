using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;

namespace Tickshift.Hardware.Sensors;

/// <summary>
/// Reads the "Thermal Zone Information" performance counters that Windows exposes for ACPI thermal
/// zones. No driver and no administrator rights are needed. These zones are defined by the laptop's
/// firmware: on some machines they track the CPU closely, on others they are a chassis sensor, and on
/// many they do not exist at all. We label the source honestly and never synthesise a value.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AcpiThermalZoneProvider : IThermalSensorProvider, IDisposable
{
    private const string Category = "Thermal Zone Information";
    private const double MinPlausibleCelsius = 5;
    private const double MaxPlausibleCelsius = 125;

    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private List<(string Instance, PerformanceCounter Temp, PerformanceCounter? Passive, bool HighPrecision)>? _zones;
    private PerformanceCounter? _processorPerformance;
    private bool _processorPerformanceUnavailable;
    private string? _unavailableReason;

    public AcpiThermalZoneProvider(ILogger<AcpiThermalZoneProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<AcpiThermalZoneProvider>.Instance;
    }

    public string Name => "Windows ACPI thermal zones";

    public Task<ThermalSnapshot> ReadAsync(CancellationToken ct = default) => Task.Run(Read, ct);

    private ThermalSnapshot Read()
    {
        lock (_gate)
        {
            EnsureInitialised();
            var throttling = ReadThrottling();

            if (_zones is null || _zones.Count == 0)
            {
                return ThermalSnapshot.Unavailable(Name, _unavailableReason ??
                    "This device's firmware does not report a temperature sensor to Windows. Many laptops only expose CPU temperature " +
                    "through manufacturer-specific drivers. TICKSHIFT does not estimate temperature from CPU usage.", throttling);
            }

            var readings = new List<TemperatureReading>();
            foreach (var zone in _zones)
            {
                try
                {
                    var raw = zone.Temp.NextValue();
                    var kelvin = zone.HighPrecision ? raw / 10.0 : raw;
                    var celsius = Math.Round(kelvin - 273.15, 1);
                    if (celsius is >= MinPlausibleCelsius and <= MaxPlausibleCelsius)
                        readings.Add(new TemperatureReading(FriendlyZoneName(zone.Instance), ThermalSourceKind.AcpiThermalZone, celsius));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _logger.LogDebug("Thermal zone {Zone} read failed: {Message}", zone.Instance, ex.Message);
                }
            }

            if (readings.Count == 0)
            {
                return ThermalSnapshot.Unavailable(Name,
                    "Thermal zones exist but report implausible values, so they are ignored rather than shown.", throttling);
            }

            return new ThermalSnapshot(DateTimeOffset.Now, SensorAvailability.Available, readings, [], Name,
                "Temperature reported by the laptop firmware (ACPI thermal zone). This may be a motherboard sensor and can read lower than the CPU core temperature.",
                throttling);
        }
    }

    private ThrottlingIndicators? ReadThrottling()
    {
        double? passive = null;
        if (_zones is not null)
        {
            foreach (var zone in _zones.Where(z => z.Passive is not null))
            {
                try
                {
                    var value = zone.Passive!.NextValue();
                    if (value is > 0 and <= 100) passive = passive is null ? value : Math.Min(passive.Value, value);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }

        double? performance = null;
        if (!_processorPerformanceUnavailable)
        {
            try
            {
                if (_processorPerformance is null)
                {
                    _processorPerformance = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", readOnly: true);
                    _processorPerformance.NextValue();
                }
                else
                {
                    var v = _processorPerformance.NextValue();
                    if (v > 0) performance = Math.Round(v, 0);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                _processorPerformanceUnavailable = true;
                _logger.LogInformation("Processor performance counter unavailable: {Message}", ex.Message);
            }
        }

        return passive is null && performance is null ? null : new ThrottlingIndicators(passive, performance);
    }

    private void EnsureInitialised()
    {
        if (_zones is not null) return;
        _zones = [];
        try
        {
            if (!PerformanceCounterCategory.Exists(Category))
            {
                _unavailableReason = "Windows reports no thermal zones on this device. Temperature cannot be read without a manufacturer driver.";
                return;
            }

            var category = new PerformanceCounterCategory(Category);
            var highPrecision = category.CounterExists("High Precision Temperature");
            var hasPassive = category.CounterExists("% Passive Limit");
            foreach (var instance in category.GetInstanceNames().Take(8))
            {
                var temp = new PerformanceCounter(Category, highPrecision ? "High Precision Temperature" : "Temperature", instance, readOnly: true);
                var passive = hasPassive ? new PerformanceCounter(Category, "% Passive Limit", instance, readOnly: true) : null;
                _zones.Add((instance, temp, passive, highPrecision));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            _unavailableReason = "Windows performance counters for thermal zones could not be read on this device.";
            _logger.LogInformation("Thermal zone counters unavailable: {Message}", ex.Message);
        }
    }

    private static string FriendlyZoneName(string instance)
    {
        // Instances look like "\_TZ.THM0" or "\_TZ.CPUZ"; keep the firmware name for transparency.
        var trimmed = instance.TrimStart('\\');
        return $"Thermal zone {trimmed}";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_zones is not null)
            {
                foreach (var z in _zones)
                {
                    z.Temp.Dispose();
                    z.Passive?.Dispose();
                }
            }

            _processorPerformance?.Dispose();
        }
    }
}

/// <summary>
/// Tries each registered provider in order and returns the first with real readings. This is the
/// extension point for additional, appropriately licensed hardware monitoring providers.
/// </summary>
public sealed class CompositeThermalSensorProvider(IEnumerable<IThermalSensorProvider> providers) : IThermalSensorProvider
{
    private readonly IReadOnlyList<IThermalSensorProvider> _providers = providers.ToList();

    public string Name => "Composite";

    public async Task<ThermalSnapshot> ReadAsync(CancellationToken ct = default)
    {
        ThermalSnapshot? firstUnavailable = null;
        foreach (var provider in _providers)
        {
            try
            {
                var snapshot = await provider.ReadAsync(ct).ConfigureAwait(false);
                if (snapshot.Availability == SensorAvailability.Available) return snapshot;
                firstUnavailable ??= snapshot;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstUnavailable ??= new ThermalSnapshot(DateTimeOffset.Now, SensorAvailability.Error, [], [], provider.Name,
                    "The temperature provider failed; details are in the log.", null);
            }
        }

        return firstUnavailable ?? ThermalSnapshot.Unavailable("None", "No temperature provider is available.");
    }
}
