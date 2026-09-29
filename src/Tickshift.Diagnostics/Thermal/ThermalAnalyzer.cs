using Tickshift.Core.Models;
using Tickshift.Diagnostics.Hardware;
using Tickshift.Diagnostics.Monitoring;

namespace Tickshift.Diagnostics.Thermal;

public sealed record ThermalThresholds(double Elevated, double High, double Critical, string Basis);

public enum ThermalTrend { Unknown, Falling, Stable, Rising }

public sealed record ThermalInsight(string Title, string Explanation, Severity Severity, string? Suggestion = null);

public sealed record ThermalAssessment(
    ThermalState State,
    double? Celsius,
    ThermalThresholds Thresholds,
    ThermalTrend Trend,
    double? TrendPerMinute,
    string Headline,
    IReadOnlyList<ThermalInsight> Insights);

/// <summary>
/// Classifies temperatures with conservative, processor-appropriate thresholds and builds a calm,
/// practical diagnostic. It never estimates a temperature when no sensor exists.
/// </summary>
public static class ThermalAnalyzer
{
    public static ThermalThresholds ThresholdsFor(CpuModel cpu, ThermalSourceKind? source)
    {
        var basis = "General guidance. Safe limits vary by processor; many Intel and AMD laptop chips allow up to about 95–105 °C before protecting themselves.";
        ThermalThresholds t = cpu.Segment switch
        {
            CpuSegment.LowPowerMobile => new(80, 90, 97, basis),
            CpuSegment.Mobile or CpuSegment.HighPerformanceMobile => new(82, 92, 98, basis),
            CpuSegment.Desktop or CpuSegment.DesktopLowPower => new(75, 85, 95, "General guidance for desktop processors. Safe limits vary by model."),
            _ => new(80, 90, 97, basis),
        };

        // ACPI zones are frequently chassis/board sensors that read lower than the CPU die, so a high
        // value there is more significant. Lower the thresholds modestly and say why.
        if (source == ThermalSourceKind.AcpiThermalZone)
        {
            t = t with
            {
                Elevated = t.Elevated - 5,
                High = t.High - 5,
                Critical = t.Critical - 5,
                Basis = t.Basis + " Adjusted slightly because this reading comes from a firmware thermal zone, which often reads below the CPU core temperature.",
            };
        }

        return t;
    }

    public static ThermalState Classify(double? celsius, ThermalThresholds t) => celsius switch
    {
        null => ThermalState.Unknown,
        >= 0 when celsius >= t.Critical => ThermalState.Critical,
        >= 0 when celsius >= t.High => ThermalState.High,
        >= 0 when celsius >= t.Elevated => ThermalState.Elevated,
        _ => ThermalState.Normal,
    };

    public static (ThermalTrend Trend, double? PerMinute) Trend(IReadOnlyList<MetricSample> samples)
    {
        var points = samples.Where(s => s.TemperatureCelsius is not null)
            .Select(s => (s.Timestamp, s.TemperatureCelsius!.Value)).ToList();
        var slope = SampleStatistics.SlopePerMinute(points);
        return slope switch
        {
            null => (ThermalTrend.Unknown, null),
            > 0.5 => (ThermalTrend.Rising, slope),
            < -0.5 => (ThermalTrend.Falling, slope),
            _ => (ThermalTrend.Stable, slope),
        };
    }

    public static ThermalAssessment Assess(
        ThermalSnapshot snapshot,
        CpuModel cpu,
        IReadOnlyList<MetricSample> recent,
        PowerStatus power,
        IReadOnlyList<ProcessSnapshot> topProcesses)
    {
        var primary = snapshot.Primary;
        var thresholds = ThresholdsFor(cpu, primary?.Kind);
        var celsius = snapshot.PrimaryCelsius;
        var state = Classify(celsius, thresholds);
        var (trend, perMinute) = Trend(recent);
        var insights = new List<ThermalInsight>();

        var avgCpu = recent.Count > 0 ? recent.Average(s => s.CpuPercent) : (double?)null;
        var sustainedLoad = recent.Count >= 10 && SampleStatistics.FractionAtOrAbove(recent, s => s.CpuPercent, 70) >= 0.6;

        if (celsius is null)
        {
            insights.Add(new ThermalInsight("No temperature sensor available",
                snapshot.Explanation + " TICKSHIFT can still look for heat-related symptoms such as sustained processor load and reduced processor speed.",
                Severity.Info));
        }

        if (sustainedLoad)
        {
            var heavy = topProcesses.Where(p => p.CpuPercent >= 5).OrderByDescending(p => p.CpuPercent).Take(3).ToList();
            var list = heavy.Count > 0 ? " Main contributors right now: " + string.Join(", ", heavy.Select(p => $"{p.Name} ({p.CpuPercent:0}%)")) + "." : string.Empty;
            insights.Add(new ThermalInsight("Sustained heavy processor load",
                $"The processor has been above 70% busy for most of the recent period (average {avgCpu:0}%). Continuous load is the most common cause of a hot laptop." + list,
                Severity.Medium, "Close or pause background work you do not need while trading."));
        }

        if (state is ThermalState.Elevated or ThermalState.High or ThermalState.Critical && avgCpu is < 35)
        {
            insights.Add(new ThermalInsight("Warm while lightly loaded",
                $"The temperature is {state.ToString().ToLowerInvariant()} although the processor is only about {avgCpu:0}% busy. " +
                "That pattern can point to restricted airflow or dust build-up, but it can also be caused by a warm room or a soft surface. It is not a definitive diagnosis.",
                Severity.Medium,
                "Use the laptop on a hard, flat surface, keep the vents clear, and consider having the fan and heatsink professionally cleaned."));
        }

        if (snapshot.Throttling is { } t)
        {
            if (t.FirmwarePassiveCoolingActive)
            {
                insights.Add(new ThermalInsight("Firmware is limiting processor speed for cooling",
                    $"Windows reports a passive cooling limit of {t.PassiveLimitPercent:0}%. The laptop is deliberately slowing the processor to manage heat — this is a protective feature and TICKSHIFT will never override it.",
                    Severity.High, "Reduce background load and improve airflow; the limit lifts automatically as the system cools."));
            }
            else if (t.ProcessorPerformancePercent is { } perf && perf < 60 && avgCpu > 60 && power.Source == PowerSource.Mains)
            {
                insights.Add(new ThermalInsight("Processor running below its normal speed under load",
                    $"The processor is at about {perf:0}% of its nominal speed while busy and plugged in. Thermal limits are one possible cause; power settings are another.",
                    Severity.Medium));
            }
        }

        if (power.Source == PowerSource.Battery)
        {
            insights.Add(new ThermalInsight("Running on battery",
                "On battery, Windows and the firmware usually limit performance to save energy, which also reduces heat. Plugged-in behaviour can differ.",
                Severity.Info));
        }

        if (trend == ThermalTrend.Rising && state is not ThermalState.Normal and not ThermalState.Unknown)
        {
            insights.Add(new ThermalInsight("Temperature is climbing",
                $"Rising about {perMinute:0.0} °C per minute over the recent period.", Severity.Low));
        }

        var headline = state switch
        {
            ThermalState.Normal => "Temperatures look normal.",
            ThermalState.Elevated => "Running warm, which is common under load. Keep an eye on it.",
            ThermalState.High => "Running hot. Reducing background load and improving airflow will help.",
            ThermalState.Critical => "Very hot. Your laptop will protect itself by slowing down; consider pausing heavy tasks and checking ventilation.",
            _ => "Temperature is not reported by this device.",
        };

        return new ThermalAssessment(state, celsius, thresholds, trend, perMinute, headline, insights);
    }
}
