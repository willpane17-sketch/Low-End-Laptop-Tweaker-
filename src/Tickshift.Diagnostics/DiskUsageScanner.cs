namespace Tickshift.Diagnostics;

public sealed record DiskUsage(string Path, long Bytes, int FileCount, bool Truncated);

/// <summary>Read-only size scan that never follows junctions or symbolic links and stops at a time budget.</summary>
public static class DiskUsageScanner
{
    public static Task<DiskUsage> ScanAsync(string path, TimeSpan? olderThan, TimeSpan budget, CancellationToken ct) => Task.Run(() =>
    {
        long bytes = 0;
        var count = 0;
        var truncated = false;
        if (!Directory.Exists(path)) return new DiskUsage(path, 0, 0, false);

        var cutoff = olderThan is { } age ? DateTime.UtcNow - age : DateTime.MaxValue;
        var deadline = DateTime.UtcNow + budget;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };

        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                if ((count & 255) == 0 && DateTime.UtcNow > deadline)
                {
                    truncated = true;
                    break;
                }

                if (olderThan is not null && file.LastWriteTimeUtc > cutoff) continue;
                bytes += file.Length;
                count++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            truncated = true;
        }

        return new DiskUsage(path, bytes, count, truncated);
    }, ct);
}
