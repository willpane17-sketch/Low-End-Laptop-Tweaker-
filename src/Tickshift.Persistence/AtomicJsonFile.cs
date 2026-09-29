using System.Text.Json;
using Tickshift.Core.Settings;

namespace Tickshift.Persistence;

public enum LoadOutcome { Loaded, Missing, RecoveredFromBackup, CorruptReplacedWithDefault }

/// <summary>
/// Crash-safe JSON persistence: write to a temp file with write-through, keep the previous good
/// copy as .bak, then atomically replace. Corrupt files are preserved for diagnosis, never silently lost.
/// </summary>
public static class AtomicJsonFile
{
    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(fs, value, SettingsJson.Options, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }

        if (File.Exists(path))
        {
            try { File.Copy(path, path + ".bak", overwrite: true); }
            catch (IOException) { /* backup is best effort */ }
        }

        File.Move(tmp, path, overwrite: true);
    }

    public static (T? Value, LoadOutcome Outcome) Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            var fromBackupOnly = TryRead<T>(path + ".bak");
            return fromBackupOnly is not null ? (fromBackupOnly, LoadOutcome.RecoveredFromBackup) : (null, LoadOutcome.Missing);
        }

        var value = TryRead<T>(path);
        if (value is not null) return (value, LoadOutcome.Loaded);

        // Preserve the corrupt file for troubleshooting.
        try { File.Copy(path, $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true); }
        catch (IOException) { }

        var backup = TryRead<T>(path + ".bak");
        return backup is not null ? (backup, LoadOutcome.RecoveredFromBackup) : (null, LoadOutcome.CorruptReplacedWithDefault);
    }

    private static T? TryRead<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length > 32 * 1024 * 1024) return null; // refuse absurd files
            return JsonSerializer.Deserialize<T>(fs, SettingsJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
