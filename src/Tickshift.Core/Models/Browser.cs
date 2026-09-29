namespace Tickshift.Core.Models;

public enum BrowserKind { Edge, Chrome }

public sealed record BrowserInstallation(
    BrowserKind Kind,
    string DisplayName,
    string ExecutablePath,
    string? Version,
    string UserDataPath);

public sealed record BrowserProfileInfo(string Name, string Path, int ExtensionCount, long DisposableCacheBytes);

/// <summary>
/// A read-only analysis. Settings detected from the browser's "Local State" file are best-effort
/// and labelled as such; TICKSHIFT never writes to browser preference files.
/// </summary>
public sealed record BrowserAnalysis
{
    public required BrowserKind Kind { get; init; }
    public bool Installed { get; init; }
    public string? Version { get; init; }
    public string? ExecutablePath { get; init; }
    public int ProcessCount { get; init; }
    public double CpuPercent { get; init; }
    public long MemoryBytes { get; init; }
    public bool? BackgroundModeEnabled { get; init; }
    public bool BackgroundModeEnforcedByPolicy { get; init; }
    public bool? EfficiencyFeatureEnabled { get; init; }
    public bool? StartupBoostEnabled { get; init; }
    public IReadOnlyList<BrowserProfileInfo> Profiles { get; init; } = [];

    public bool IsRunning => ProcessCount > 0;
    public int TotalExtensions => Profiles.Sum(p => p.ExtensionCount);
    public long TotalDisposableCacheBytes => Profiles.Sum(p => p.DisposableCacheBytes);
    public string DisplayName => Kind == BrowserKind.Edge ? "Microsoft Edge" : "Google Chrome";
    public string EfficiencyFeatureName => Kind == BrowserKind.Edge ? "Sleeping tabs" : "Memory Saver";
}
