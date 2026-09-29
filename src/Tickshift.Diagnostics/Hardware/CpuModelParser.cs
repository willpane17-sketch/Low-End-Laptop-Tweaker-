using System.Text.RegularExpressions;

namespace Tickshift.Diagnostics.Hardware;

public enum CpuVendor { Unknown, Intel, Amd, Other }

public enum CpuSegment
{
    Unknown,
    /// <summary>U, Y, G1–G7, N-series: 6–15 W laptop parts. Limited sustained performance by design.</summary>
    LowPowerMobile,
    /// <summary>M/MQ/P: mainstream laptop parts.</summary>
    Mobile,
    /// <summary>H/HQ/HX/HS: performance laptop parts.</summary>
    HighPerformanceMobile,
    DesktopLowPower,
    Desktop,
}

public sealed record CpuModel(
    CpuVendor Vendor,
    string Family,
    int? Tier,
    int? Generation,
    string? ModelNumber,
    string? Suffix,
    CpuSegment Segment,
    bool IsEntryLevel,
    string RawName)
{
    public bool IsMobile => Segment is CpuSegment.LowPowerMobile or CpuSegment.Mobile or CpuSegment.HighPerformanceMobile;

    public string Describe()
    {
        var parts = new List<string>();
        // "Core i3" already contains its tier; "Core Ultra", "Core" and "Ryzen" need it appended.
        var title = Tier is { } tier && Family is "Core Ultra" or "Core" or "Ryzen" ? $"{Family} {tier}" : Family;
        parts.Add($"{VendorName} {title}".Trim());
        if (ModelNumber is not null) parts[0] += $" {ModelNumber}{Suffix}";
        if (Generation is { } g)
            parts.Add(Vendor == CpuVendor.Amd ? $"{g}000 series" : $"{Ordinal(g)} generation");
        parts.Add(Segment switch
        {
            CpuSegment.LowPowerMobile => "low-power laptop processor",
            CpuSegment.Mobile => "laptop processor",
            CpuSegment.HighPerformanceMobile => "performance laptop processor",
            CpuSegment.DesktopLowPower => "low-power desktop processor",
            CpuSegment.Desktop => "desktop processor",
            _ => "processor",
        });
        return string.Join(" · ", parts);
    }

    private string VendorName => Vendor switch { CpuVendor.Intel => "Intel", CpuVendor.Amd => "AMD", _ => string.Empty };

    private static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}th",
        _ => (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" },
    };
}

/// <summary>
/// Parses the processor brand string (e.g. "Intel(R) Core(TM) i3-7020U CPU @ 2.30GHz") into model,
/// generation and market segment. Two "Core i3" chips can differ enormously (a 1st-gen dual-core
/// i3-330M versus a 12th-gen i3-1215U), so recommendations are based on the parsed details, never on
/// the family name alone. Unrecognised strings produce an honest "unknown" model.
/// </summary>
public static partial class CpuModelParser
{
    public static CpuModel Parse(string? brandString)
    {
        var raw = (brandString ?? string.Empty).Trim();
        var s = Regex.Replace(raw, @"\((R|TM|tm|r)\)", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();

        var vendor = s.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? CpuVendor.Intel
            : s.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? CpuVendor.Amd
            : s.Length == 0 ? CpuVendor.Unknown : CpuVendor.Other;

        Match m;

        // Intel Core iN-XXXX(suffix), including 10th+ gen 4/5-digit models and N-series (i3-N305).
        if ((m = CoreINewRegex().Match(s)).Success)
        {
            var tier = int.Parse(m.Groups["tier"].Value);
            if (m.Groups["n"].Success)
            {
                return new CpuModel(CpuVendor.Intel, $"Core i{tier}", tier, 12, "N" + m.Groups["model"].Value, null, CpuSegment.LowPowerMobile, true, raw);
            }

            var model = m.Groups["model"].Value;
            var suffix = m.Groups["suffix"].Value;
            var gen = IntelGeneration(model);
            return new CpuModel(CpuVendor.Intel, $"Core i{tier}", tier, gen, model, suffix.Length > 0 ? suffix : null,
                IntelSegment(suffix, gen), tier == 3, raw);
        }

        // First-generation naming: "Core i3 CPU M 350".
        if ((m = CoreIOldRegex().Match(s)).Success)
        {
            var tier = int.Parse(m.Groups["tier"].Value);
            var suffix = m.Groups["suffix"].Value;
            return new CpuModel(CpuVendor.Intel, $"Core i{tier}", tier, 1, m.Groups["model"].Value, suffix,
                IntelSegment(suffix, 1), tier == 3, raw);
        }

        // Core Ultra 5 125U / Core 5 120U (2023+ naming).
        if ((m = CoreUltraRegex().Match(s)).Success)
        {
            var ultra = m.Groups["ultra"].Success;
            var tier = int.Parse(m.Groups["tier"].Value);
            var suffix = m.Groups["suffix"].Value;
            return new CpuModel(CpuVendor.Intel, ultra ? "Core Ultra" : "Core", tier, null, m.Groups["model"].Value,
                suffix.Length > 0 ? suffix : null, IntelSegment(suffix, 14), tier <= 3, raw);
        }

        // Pentium / Celeron / Atom.
        if ((m = IntelEntryRegex().Match(s)).Success)
        {
            var family = m.Groups["family"].Value;
            var model = m.Groups["model"].Success ? m.Groups["model"].Value : null;
            var prefix = m.Groups["prefix"].Value;
            var suffix = m.Groups["suffix"].Value;
            var segment = prefix.ToUpperInvariant() switch
            {
                "N" => CpuSegment.LowPowerMobile,
                "J" => CpuSegment.DesktopLowPower,
                _ => IntelSegment(suffix, null),
            };
            if (family.Equals("Atom", StringComparison.OrdinalIgnoreCase)) segment = CpuSegment.LowPowerMobile;
            return new CpuModel(CpuVendor.Intel, Capitalise(family), null, null, model is null ? null : prefix + model,
                suffix.Length > 0 ? suffix : null, segment, true, raw);
        }

        // AMD Ryzen N XXXX(suffix).
        if ((m = RyzenRegex().Match(s)).Success)
        {
            var tier = int.Parse(m.Groups["tier"].Value);
            var model = m.Groups["model"].Value;
            var suffix = m.Groups["suffix"].Value;
            var series = model.Length == 4 ? model[0] - '0' : (int?)null;
            return new CpuModel(CpuVendor.Amd, "Ryzen", tier, series, model, suffix.Length > 0 ? suffix : null,
                AmdSegment(suffix), tier == 3, raw);
        }

        // AMD A-series / Athlon / E-series.
        if ((m = AmdEntryRegex().Match(s)).Success)
        {
            var suffix = m.Groups["suffix"].Value;
            return new CpuModel(CpuVendor.Amd, m.Groups["family"].Value.Trim(), null, null, m.Groups["model"].Value,
                suffix.Length > 0 ? suffix : null, AmdSegment(suffix), true, raw);
        }

        return new CpuModel(vendor, vendor == CpuVendor.Unknown ? "Unknown processor" : "Processor", null, null, null, null, CpuSegment.Unknown, false, raw);
    }

    /// <summary>
    /// 3-digit models are 1st gen; 4-digit models starting 2–9 give the generation directly; 4-digit models
    /// starting with "1" (1005G1, 1115G4, 1215U) and 5-digit models (10110U, 12100) use the first two digits.
    /// </summary>
    internal static int? IntelGeneration(string model) => model.Length switch
    {
        3 => 1,
        4 when model[0] == '1' => int.Parse(model[..2]),
        4 => model[0] - '0',
        5 => int.Parse(model[..2]),
        _ => null,
    };

    private static CpuSegment IntelSegment(string suffix, int? gen)
    {
        var s = suffix.ToUpperInvariant();
        if (s.StartsWith('U') || s.StartsWith('Y') || (s.StartsWith('G') && s.Length == 2 && char.IsDigit(s[1]))) return CpuSegment.LowPowerMobile;
        if (s.StartsWith('H')) return CpuSegment.HighPerformanceMobile;
        if (s is "M" or "MQ" or "QM" or "XM" or "MX" or "P" or "PE") return CpuSegment.Mobile;
        if (s.StartsWith('T')) return CpuSegment.DesktopLowPower;
        if (s.Length == 0 || s is "K" or "KF" or "F" or "S" or "KS" or "E") return gen is null && s.Length == 0 ? CpuSegment.Unknown : CpuSegment.Desktop;
        return CpuSegment.Unknown;
    }

    private static CpuSegment AmdSegment(string suffix)
    {
        var s = suffix.ToUpperInvariant();
        if (s.StartsWith('U') || s is "E" || s.StartsWith('C')) return CpuSegment.LowPowerMobile;
        if (s.StartsWith('H')) return CpuSegment.HighPerformanceMobile;
        if (s is "G" or "X" or "GE" or "" or "XT" or "X3D") return CpuSegment.Desktop;
        return CpuSegment.Unknown;
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    [GeneratedRegex(@"Core i(?<tier>[3579])[- ](?:(?<n>N)(?<model>\d{3})|(?<model>\d{3,5})(?<suffix>[A-Z]{1,2}\d?)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CoreINewRegex();

    [GeneratedRegex(@"Core i(?<tier>[3579]) CPU (?<suffix>[A-Z]{1,2}) (?<model>\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex CoreIOldRegex();

    [GeneratedRegex(@"Core (?<ultra>Ultra )?(?<tier>[3579]) (?<model>\d{3})(?<suffix>[A-Z]{1,2})?\b", RegexOptions.IgnoreCase)]
    private static partial Regex CoreUltraRegex();

    [GeneratedRegex(@"(?<family>Pentium|Celeron|Atom)(?: (?:Silver|Gold|Dual-Core|CPU))*(?: (?<prefix>[NJGx])?(?<model>\d{3,4})(?<suffix>[A-Z]{1,2})?)?", RegexOptions.IgnoreCase)]
    private static partial Regex IntelEntryRegex();

    [GeneratedRegex(@"Ryzen (?:AI )?(?<tier>[3579]) (?:PRO )?(?<model>\d{3,4})(?<suffix>[A-Z]{1,3}\d?)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex RyzenRegex();

    [GeneratedRegex(@"(?<family>A\d{1,2}|E\d|E-Series|Athlon(?: Silver| Gold)?)[- ](?<model>\d{3,4})(?<suffix>[A-Z]{1,2})?", RegexOptions.IgnoreCase)]
    private static partial Regex AmdEntryRegex();
}
