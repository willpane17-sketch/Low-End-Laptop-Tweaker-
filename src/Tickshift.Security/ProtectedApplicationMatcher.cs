using Tickshift.Core.Settings;

namespace Tickshift.Security;

/// <summary>Decides whether a running process belongs to a user-protected application.</summary>
public sealed class ProtectedApplicationMatcher
{
    private readonly Dictionary<string, List<ProtectedApplication>> _byName;

    public ProtectedApplicationMatcher(IEnumerable<ProtectedApplication> apps)
    {
        _byName = apps.Where(a => a.Enabled && !string.IsNullOrWhiteSpace(a.ExecutableName))
            .GroupBy(a => ProcessCatalog.Normalise(a.ExecutableName))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public bool IsEmpty => _byName.Count == 0;

    /// <summary>
    /// Returns the matching protected application. When an entry specifies a full path, the running
    /// process must be at that path; otherwise the executable name is sufficient. If the process path
    /// cannot be read (access denied), name matching is used — erring on the side of protection.
    /// </summary>
    public ProtectedApplication? Match(string processName, string? processPath)
    {
        if (!_byName.TryGetValue(ProcessCatalog.Normalise(processName), out var candidates)) return null;

        foreach (var app in candidates.Where(a => !string.IsNullOrWhiteSpace(a.ExecutablePath)))
        {
            if (processPath is null || PathsEqual(app.ExecutablePath!, processPath)) return app;
        }

        return candidates.FirstOrDefault(a => string.IsNullOrWhiteSpace(a.ExecutablePath));
    }

    public bool IsProtected(string processName, string? processPath) => Match(processName, processPath) is not null;

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
