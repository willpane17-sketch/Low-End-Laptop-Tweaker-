namespace Tickshift.Core.Models;

public enum SensorAvailability { Available, Unavailable, Error }

public enum ThermalSourceKind
{
    /// <summary>CPU package/die temperature from a hardware monitoring provider.</summary>
    CpuPackage,
    CpuCore,
    /// <summary>ACPI thermal zone reported by firmware. Often a motherboard/chassis sensor, not the CPU die.</summary>
    AcpiThermalZone,
    Other,
}

public sealed record TemperatureReading(string SensorName, ThermalSourceKind Kind, double Celsius);

public sealed record FanReading(string Name, int Rpm);

/// <summary>
/// Throttling hints exposed by Windows performance counters.
/// PassiveLimitPercent &lt; 100 means firmware is actively limiting CPU performance for cooling.
/// ProcessorPerformancePercent is the current frequency as a share of nominal frequency.
/// </summary>
public sealed record ThrottlingIndicators(double? PassiveLimitPercent, double? ProcessorPerformancePercent)
{
    public bool FirmwarePassiveCoolingActive => PassiveLimitPercent is < 100;
}

public sealed record ThermalSnapshot(
    DateTimeOffset Timestamp,
    SensorAvailability Availability,
    IReadOnlyList<TemperatureReading> Readings,
    IReadOnlyList<FanReading> Fans,
    string ProviderName,
    string Explanation,
    ThrottlingIndicators? Throttling)
{
    public static ThermalSnapshot Unavailable(string provider, string explanation, ThrottlingIndicators? throttling = null) =>
        new(DateTimeOffset.Now, SensorAvailability.Unavailable, [], [], provider, explanation, throttling);

    /// <summary>Best available reading, preferring true CPU sensors over ACPI zones. Null when nothing is reported.</summary>
    public TemperatureReading? Primary =>
        Readings.Where(r => r.Kind == ThermalSourceKind.CpuPackage).MaxBy(r => r.Celsius)
        ?? Readings.Where(r => r.Kind == ThermalSourceKind.CpuCore).MaxBy(r => r.Celsius)
        ?? Readings.Where(r => r.Kind == ThermalSourceKind.AcpiThermalZone).MaxBy(r => r.Celsius)
        ?? Readings.MaxBy(r => r.Celsius);

    public double? PrimaryCelsius => Availability == SensorAvailability.Available ? Primary?.Celsius : null;
}
