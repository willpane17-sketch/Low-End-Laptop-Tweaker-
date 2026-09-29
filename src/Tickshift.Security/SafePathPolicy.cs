namespace Tickshift.Security;

/// <summary>
/// Guards every file deletion. A path may only be deleted when it lies strictly inside one of the
/// explicitly allowlisted cleanup roots, contains no reparse points between root and target, and is
/// not inside a personal folder. This defends against junction/symlink tricks and path traversal.
/// </summary>
public sealed class SafePathPolicy
{
    private readonly List<string> _allowedRoots;
    private readonly List<string> _forbiddenRoots;

    public SafePathPolicy(IEnumerable<string> allowedRoots, IEnumerable<string>? forbiddenRoots = null)
    {
        _allowedRoots = allowedRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(NormaliseDir).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _forbiddenRoots = (forbiddenRoots ?? DefaultForbiddenRoots()).Where(r => !string.IsNullOrWhiteSpace(r)).Select(NormaliseDir).ToList();
    }

    public IReadOnlyList<string> AllowedRoots => _allowedRoots;

    public static IEnumerable<string> DefaultForbiddenRoots()
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.Favorites,
            Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
        ];
        foreach (var f in folders)
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p)) yield return p;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            yield return Path.Combine(profile, "Downloads");
            yield return Path.Combine(profile, "OneDrive");
        }
    }

    public bool IsDeletionAllowed(string candidatePath, out string reason)
    {
        string full;
        try
        {
            full = Path.GetFullPath(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "Invalid path.";
            return false;
        }

        var root = _allowedRoots.FirstOrDefault(r => IsStrictlyUnder(full, r));
        if (root is null)
        {
            reason = "Outside the allowlisted cleanup locations.";
            return false;
        }

        // Personal and system folders are never eligible, even if a cleanup root was misconfigured to sit inside one.
        if (_forbiddenRoots.Any(f => IsUnderOrEqual(full, f)))
        {
            reason = "Inside a personal or protected folder.";
            return false;
        }

        // Never follow junctions/symlinks: every segment between the root and the target must be a real directory/file.
        var current = full;
        while (!PathEquals(current, root))
        {
            try
            {
                var attrs = File.GetAttributes(current);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    reason = "Path contains a reparse point (junction or symbolic link).";
                    return false;
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }

            var parent = Path.GetDirectoryName(current);
            if (parent is null) break;
            current = parent;
        }

        reason = string.Empty;
        return true;
    }

    private static string NormaliseDir(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar), PathComparison);

    private static bool IsStrictlyUnder(string path, string root) =>
        path.Length > root.Length + 1 && path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static bool IsUnderOrEqual(string path, string root) => PathEquals(path, root) || IsStrictlyUnder(path, root);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
