using Tickshift.Core;

namespace Tickshift.Persistence;

/// <summary>All locations TICKSHIFT writes to. Everything lives under %LOCALAPPDATA%\Tickshift.</summary>
public sealed class AppDataPaths
{
    public AppDataPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Brand.DataFolderName);
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string JournalFile => Path.Combine(Root, "recovery", "change-journal.json");
    public string RunHistoryFile => Path.Combine(Root, "history", "optimisation-runs.json");
    public string AutomationHistoryFile => Path.Combine(Root, "history", "automation.json");
    public string NetworkHistoryFile => Path.Combine(Root, "history", "network.json");
    public string BenchmarkHistoryFile => Path.Combine(Root, "history", "benchmarks.json");
    public string TradingSessionFile => Path.Combine(Root, "recovery", "trading-session.json");
    public string StateFile => Path.Combine(Root, "state.json");
    public string LogDirectory => Path.Combine(Root, "logs");
    public string ReportDirectory => Path.Combine(Root, "reports");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "recovery"));
        Directory.CreateDirectory(Path.Combine(Root, "history"));
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ReportDirectory);
    }
}
