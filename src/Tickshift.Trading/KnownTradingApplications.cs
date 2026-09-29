using Tickshift.Core.Settings;

namespace Tickshift.Trading;

public sealed record TradingAppDefinition(string Id, string DisplayName, string ExecutableName, ProtectedAppKind Kind, string Hint);

public sealed record DetectedTradingApp(TradingAppDefinition Definition, string? Path, bool Installed, bool Running);

/// <summary>Built-in definitions for common trading software. Users can add custom executables too.</summary>
public static class KnownTradingApplications
{
    public static readonly IReadOnlyList<TradingAppDefinition> All =
    [
        new("mt5", "MetaTrader 5", "terminal64.exe", ProtectedAppKind.TradingTerminal, "Broker-branded MetaTrader 5 installs use the same terminal64.exe."),
        new("mt4", "MetaTrader 4", "terminal.exe", ProtectedAppKind.TradingTerminal, "Broker-branded MetaTrader 4 installs use the same terminal.exe."),
        new("tradingview-desktop", "TradingView Desktop", "TradingView.exe", ProtectedAppKind.TradingTerminal, "The TradingView desktop app. For TradingView in a browser, protect the browser."),
        new("ctrader", "cTrader", "cTrader.exe", ProtectedAppKind.BrokerTerminal, "cTrader desktop terminal."),
        new("chrome", "Google Chrome", "chrome.exe", ProtectedAppKind.Browser, "Protect if you use TradingView or a broker web terminal in Chrome."),
        new("edge", "Microsoft Edge", "msedge.exe", ProtectedAppKind.Browser, "Protect if you use TradingView or a broker web terminal in Edge."),
        new("telegram", "Telegram", "Telegram.exe", ProtectedAppKind.Messaging, "Telegram Desktop for signal groups and alerts."),
    ];

    public static ProtectedApplication ToProtected(TradingAppDefinition d, string? path = null) => new()
    {
        Id = d.Id,
        DisplayName = d.DisplayName,
        ExecutableName = d.ExecutableName,
        ExecutablePath = null, // name matching also covers broker-branded copies in other folders
        Kind = d.Kind,
    };

    /// <summary>Creates a protected entry for a user-chosen executable. Only .exe files are accepted.</summary>
    public static ProtectedApplication? FromCustomPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try { full = System.IO.Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return null; }
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var name = System.IO.Path.GetFileName(full);
        return new ProtectedApplication
        {
            Id = "custom:" + name.ToLowerInvariant(),
            DisplayName = System.IO.Path.GetFileNameWithoutExtension(name),
            ExecutableName = name,
            ExecutablePath = full,
            Kind = ProtectedAppKind.Custom,
        };
    }

    /// <summary>Looks for installed copies in the usual places (read-only, shallow search).</summary>
    public static IReadOnlyList<DetectedTradingApp> Detect(ISet<string> runningExecutableNames)
    {
        var results = new List<DetectedTradingApp>();
        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        }.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var def in All)
        {
            string? found = null;
            var candidates = def.Id switch
            {
                "telegram" => [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Telegram Desktop", "Telegram.exe")],
                "chrome" => programFiles.Select(p => Path.Combine(p, "Google", "Chrome", "Application", "chrome.exe")).Append(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")),
                "edge" => programFiles.Select(p => Path.Combine(p, "Microsoft", "Edge", "Application", "msedge.exe")),
                _ => FindInSubfolders(programFiles, def.ExecutableName),
            };
            found = candidates.FirstOrDefault(File.Exists);
            var running = runningExecutableNames.Contains(Path.GetFileNameWithoutExtension(def.ExecutableName));
            results.Add(new DetectedTradingApp(def, found, found is not null || running, running));
        }

        return results;
    }

    private static IEnumerable<string> FindInSubfolders(IEnumerable<string> roots, string exe)
    {
        foreach (var root in roots)
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root).Take(400).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var dir in dirs) yield return Path.Combine(dir, exe);
        }
    }
}
