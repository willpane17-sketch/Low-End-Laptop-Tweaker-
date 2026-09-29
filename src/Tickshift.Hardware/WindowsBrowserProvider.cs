using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;

namespace Tickshift.Hardware;

/// <summary>
/// Read-only browser analysis. Settings come from documented enterprise policy keys (authoritative)
/// or the browser's "Local State" file (best-effort, read with shared access). Nothing is ever written
/// to browser configuration, and cookies, passwords, history and sessions are never read.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBrowserProvider : IBrowserProvider
{
    private static readonly string[] DisposableCacheFolders = [@"Cache\Cache_Data", "Code Cache", "GPUCache"];
    private readonly ILogger _logger;

    public WindowsBrowserProvider(ILogger<WindowsBrowserProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<WindowsBrowserProvider>.Instance;
    }

    public IReadOnlyList<BrowserInstallation> DetectInstalled()
    {
        var list = new List<BrowserInstallation>();
        foreach (var kind in new[] { BrowserKind.Edge, BrowserKind.Chrome })
        {
            var exe = FindExecutable(kind);
            if (exe is null) continue;
            string? version = null;
            try { version = FileVersionInfo.GetVersionInfo(exe).ProductVersion; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            list.Add(new BrowserInstallation(kind, kind == BrowserKind.Edge ? "Microsoft Edge" : "Google Chrome", exe, version, UserDataPath(kind)));
        }

        return list;
    }

    public bool IsRunning(BrowserKind kind)
    {
        var processes = Process.GetProcessesByName(ProcessName(kind));
        var running = processes.Length > 0;
        foreach (var p in processes) p.Dispose();
        return running;
    }

    public BrowserAnalysis Analyse(BrowserKind kind, IReadOnlyList<ProcessSnapshot> processes)
    {
        var install = DetectInstalled().FirstOrDefault(b => b.Kind == kind);
        var name = ProcessName(kind);
        var own = processes.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();

        var analysis = new BrowserAnalysis
        {
            Kind = kind,
            Installed = install is not null,
            Version = install?.Version,
            ExecutablePath = install?.ExecutablePath,
            ProcessCount = own.Count,
            CpuPercent = Math.Round(own.Sum(p => p.CpuPercent), 1),
            MemoryBytes = own.Sum(p => p.PrivateBytes > 0 ? p.PrivateBytes : p.WorkingSetBytes),
        };

        if (install is null) return analysis;

        var policyRoot = kind == BrowserKind.Edge ? @"SOFTWARE\Policies\Microsoft\Edge" : @"SOFTWARE\Policies\Google\Chrome";
        var backgroundPolicy = ReadPolicyBool(policyRoot, "BackgroundModeEnabled");
        var localState = ReadLocalState(install.UserDataPath);
        var backgroundPref = localState is { } ls ? TryGetBool(ls, "background_mode", "enabled") : null;

        bool? efficiency = kind == BrowserKind.Edge
            ? ReadPolicyBool(policyRoot, "SleepingTabsEnabled")
            : ReadPolicyBool(policyRoot, "HighEfficiencyModeEnabled");
        bool? startupBoost = kind == BrowserKind.Edge ? ReadPolicyBool(policyRoot, "StartupBoostEnabled") : null;

        return analysis with
        {
            BackgroundModeEnabled = backgroundPolicy ?? backgroundPref,
            BackgroundModeEnforcedByPolicy = backgroundPolicy is not null,
            EfficiencyFeatureEnabled = efficiency,
            StartupBoostEnabled = startupBoost,
            Profiles = ReadProfiles(install.UserDataPath),
        };
    }

    /// <summary>Disposable cache folders for a browser. Used by the cache cleanup optimisation.</summary>
    public static IEnumerable<string> GetDisposableCacheDirectories(BrowserKind kind)
    {
        var root = UserDataPath(kind);
        if (!Directory.Exists(root)) yield break;
        foreach (var profile in EnumerateProfileDirectories(root))
        {
            foreach (var folder in DisposableCacheFolders)
            {
                var path = Path.Combine(profile, folder);
                if (Directory.Exists(path)) yield return path;
            }
        }
    }

    public static string UserDataPath(BrowserKind kind)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return kind == BrowserKind.Edge
            ? Path.Combine(local, "Microsoft", "Edge", "User Data")
            : Path.Combine(local, "Google", "Chrome", "User Data");
    }

    public static string ProcessName(BrowserKind kind) => kind == BrowserKind.Edge ? "msedge" : "chrome";

    private static IEnumerable<string> EnumerateProfileDirectories(string userData)
    {
        var def = Path.Combine(userData, "Default");
        if (Directory.Exists(def)) yield return def;
        IEnumerable<string> others;
        try { others = Directory.EnumerateDirectories(userData, "Profile *").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var d in others) yield return d;
    }

    private IReadOnlyList<BrowserProfileInfo> ReadProfiles(string userData)
    {
        var result = new List<BrowserProfileInfo>();
        if (!Directory.Exists(userData)) return result;
        foreach (var profile in EnumerateProfileDirectories(userData).Take(12))
        {
            var extensions = 0;
            var extDir = Path.Combine(profile, "Extensions");
            try
            {
                if (Directory.Exists(extDir))
                    extensions = Directory.EnumerateDirectories(extDir).Count(d => !Path.GetFileName(d).Equals("Temp", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            long cache = 0;
            foreach (var folder in DisposableCacheFolders)
                cache += DirectorySize(Path.Combine(profile, folder));

            result.Add(new BrowserProfileInfo(Path.GetFileName(profile), profile, extensions, cache));
        }

        return result;
    }

    private static long DirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options)) total += file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    private JsonDocument? ReadLocalState(string userData)
    {
        var path = Path.Combine(userData, "Local State");
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > 16 * 1024 * 1024) return null;
            return JsonDocument.Parse(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug("Local State not readable: {Message}", ex.Message);
            return null;
        }
    }

    private static bool? TryGetBool(JsonDocument doc, params string[] path)
    {
        var element = doc.RootElement;
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element)) return null;
        }

        return element.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    private static bool? ReadPolicyBool(string keyPath, string valueName)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(keyPath);
                if (key?.GetValue(valueName) is int v) return v != 0;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }

        return null;
    }

    private static string? FindExecutable(BrowserKind kind)
    {
        var exeName = kind == BrowserKind.Edge ? "msedge.exe" : "chrome.exe";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
                    if (key?.GetValue(null) is string path)
                    {
                        path = path.Trim('"');
                        if (File.Exists(path)) return path;
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            }
        }

        var candidates = kind == BrowserKind.Edge
            ? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
            }
            : new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
            };
        return candidates.FirstOrDefault(File.Exists);
    }
}
