using System.Text.RegularExpressions;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;

namespace Tickshift.Trading;

public sealed record BrowserExclusionGuide(BrowserKind Browser, string FeatureName, string SettingsKey, IReadOnlyList<string> Steps, IReadOnlyList<string> PatternsToAdd);

/// <summary>
/// Protected trading websites. Browsers do not offer a supported API for third-party apps to change
/// sleeping-tab exclusions, so TICKSHIFT generates exact patterns and step-by-step instructions instead
/// of editing browser preference files.
/// </summary>
public static partial class ProtectedWebsites
{
    public static readonly IReadOnlyList<ProtectedWebsite> Suggestions =
    [
        new("tradingview.com", "TradingView"),
        new("forexfactory.com", "Forex Factory (economic calendar)"),
        new("investing.com", "Investing.com (economic calendar)"),
    ];

    /// <summary>Normalises input like "https://www.tradingview.com/chart/xyz" to "tradingview.com".</summary>
    public static bool TryNormalise(string? input, out string domain)
    {
        domain = string.Empty;
        var value = (input ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0 || value.Length > 253) return false;

        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            value = uri.Host;
        }
        else
        {
            var slash = value.IndexOfAny(['/', '?', '#']);
            if (slash >= 0) value = value[..slash];
        }

        if (value.StartsWith("[*.]", StringComparison.Ordinal)) value = value[4..];
        if (value.StartsWith("*.", StringComparison.Ordinal)) value = value[2..];
        if (value.StartsWith("www.", StringComparison.Ordinal)) value = value[4..];
        value = value.TrimEnd('.');
        if (value.Contains('@') || value.Contains(':')) return false;
        if (!DomainRegex().IsMatch(value)) return false;

        domain = value;
        return true;
    }

    /// <summary>Chromium site pattern that also covers subdomains (e.g. "[*.]tradingview.com").</summary>
    public static string ToSitePattern(string domain) => $"[*.]{domain}";

    public static BrowserExclusionGuide GuideFor(BrowserKind browser, IEnumerable<ProtectedWebsite> sites)
    {
        var patterns = sites.Select(s => ToSitePattern(s.Domain)).ToList();
        return browser == BrowserKind.Edge
            ? new BrowserExclusionGuide(browser, "Sleeping tabs", "performance",
            [
                "Open Edge Settings › System and performance.",
                "Under \"Optimize performance\", make sure \"Save resources with sleeping tabs\" is on.",
                "Find \"Never put these sites to sleep\" and select Add.",
                "Paste one of the patterns below and select Add. Repeat for each site.",
            ], patterns)
            : new BrowserExclusionGuide(browser, "Memory Saver", "performance",
            [
                "Open Chrome Settings › Performance.",
                "Turn on \"Memory Saver\".",
                "Under \"Always keep these sites active\", select Add.",
                "Paste one of the patterns below and select Add. Repeat for each site.",
            ], patterns);
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainRegex();
}
