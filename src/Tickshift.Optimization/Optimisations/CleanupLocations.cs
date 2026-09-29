using Tickshift.Core.Models;

namespace Tickshift.Optimization.Optimisations;

/// <summary>The fixed set of folders cleanup is allowed to touch. Injected so tests can use a sandbox.</summary>
public interface ICleanupLocations
{
    IReadOnlyList<string> UserTemp { get; }
    IReadOnlyList<string> ErrorReports { get; }
    IReadOnlyList<string> BrowserCache(BrowserKind kind);
}

public sealed class WindowsCleanupLocations : ICleanupLocations
{
    private static readonly string[] CacheFolders = [Path.Combine("Cache", "Cache_Data"), "Code Cache", "GPUCache"];
    private readonly string _localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public IReadOnlyList<string> UserTemp
    {
        get
        {
            // Only the per-user temp directory, and only if it really lives under the user's profile.
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return !string.IsNullOrEmpty(profile) && temp.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? [temp]
                : [];
        }
    }

    public IReadOnlyList<string> ErrorReports =>
    [
        Path.Combine(_localAppData, "Microsoft", "Windows", "WER", "ReportArchive"),
        Path.Combine(_localAppData, "Microsoft", "Windows", "WER", "ReportQueue"),
    ];

    public IReadOnlyList<string> BrowserCache(BrowserKind kind)
    {
        var userData = kind == BrowserKind.Edge
            ? Path.Combine(_localAppData, "Microsoft", "Edge", "User Data")
            : Path.Combine(_localAppData, "Google", "Chrome", "User Data");
        if (!Directory.Exists(userData)) return [];

        var profiles = new List<string>();
        var def = Path.Combine(userData, "Default");
        if (Directory.Exists(def)) profiles.Add(def);
        try { profiles.AddRange(Directory.EnumerateDirectories(userData, "Profile *")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return profiles.SelectMany(p => CacheFolders.Select(f => Path.Combine(p, f))).Where(Directory.Exists).ToList();
    }
}
