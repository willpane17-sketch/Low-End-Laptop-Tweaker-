using System.Globalization;

namespace Tickshift.Core;

/// <summary>Consistent, culture-aware formatting for UI, reports and notifications.</summary>
public static class Format
{
    public static string Bytes(long bytes) => Bytes(bytes < 0 ? 0UL : (ulong)bytes);

    public static string Bytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 ? "0" : value >= 100 ? "0" : "0.0";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string Percent(double? value, int decimals = 0) =>
        value is null ? "—" : Math.Round(value.Value, decimals).ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), CultureInfo.CurrentCulture) + "%";

    public static string Celsius(double? value) =>
        value is null ? "Sensor unavailable" : value.Value.ToString("0", CultureInfo.CurrentCulture) + " °C";

    public static string Duration(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{Math.Max(0, (int)span.TotalSeconds)}s";
    }

    public static string Relative(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is null) return "Never";
        var delta = now - when.Value;
        if (delta < TimeSpan.FromMinutes(1)) return "Just now";
        if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes} min ago";
        if (delta < TimeSpan.FromDays(1)) return $"{(int)delta.TotalHours} h ago";
        if (delta < TimeSpan.FromDays(7)) return $"{(int)delta.TotalDays} d ago";
        return when.Value.LocalDateTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }
}
