using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Security;

namespace Tickshift.Hardware;

/// <summary>
/// Enumerates the same startup locations Task Manager shows, and reads/writes the Explorer
/// "StartupApproved" state that Task Manager and Settings use to enable/disable them. The original
/// Run entries are never deleted, which keeps every change reversible.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupProvider : IStartupProvider
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private readonly ISignatureVerifier _signatures;
    private readonly ILogger _logger;

    public WindowsStartupProvider(ISignatureVerifier signatures, ILogger<WindowsStartupProvider>? logger = null)
    {
        _signatures = signatures;
        _logger = logger ?? NullLogger<WindowsStartupProvider>.Instance;
    }

    public IReadOnlyList<StartupEntry> Enumerate()
    {
        var entries = new List<StartupEntry>();
        ReadRunKey(entries, RegistryHive.CurrentUser, RegistryView.Default, StartupLocation.UserRun);
        ReadRunKey(entries, RegistryHive.LocalMachine, RegistryView.Registry64, StartupLocation.MachineRun);
        if (Environment.Is64BitOperatingSystem)
            ReadRunKey(entries, RegistryHive.LocalMachine, RegistryView.Registry32, StartupLocation.MachineRun32);
        ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupLocation.UserStartupFolder);
        ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupLocation.CommonStartupFolder);
        return entries.OrderBy(e => e.IsMicrosoftComponent).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public byte[]? ReadApprovalValue(StartupEntry entry)
    {
        var (hive, list) = ApprovalLocation(entry.Location);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(ApprovedRoot + list);
        return key?.GetValue(entry.Name) as byte[];
    }

    public void WriteApprovalValue(StartupEntry entry, byte[]? value)
    {
        var (hive, list) = ApprovalLocation(entry.Location);
        if (hive != RegistryHive.CurrentUser)
            throw new UnauthorizedAccessException("Machine-wide startup entries require administrator approval.");

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(ApprovedRoot + list, writable: true);
        if (value is null) key.DeleteValue(entry.Name, throwOnMissingValue: false);
        else key.SetValue(entry.Name, value, RegistryValueKind.Binary);
    }

    /// <summary>Maps a startup location to its StartupApproved hive and list name ("Run", "Run32", "StartupFolder").</summary>
    public static (RegistryHive Hive, string List) ApprovalLocation(StartupLocation location) => location switch
    {
        StartupLocation.UserRun => (RegistryHive.CurrentUser, "Run"),
        StartupLocation.MachineRun => (RegistryHive.LocalMachine, "Run"),
        StartupLocation.MachineRun32 => (RegistryHive.LocalMachine, "Run32"),
        StartupLocation.UserStartupFolder => (RegistryHive.CurrentUser, "StartupFolder"),
        StartupLocation.CommonStartupFolder => (RegistryHive.LocalMachine, "StartupFolder"),
        _ => throw new ArgumentOutOfRangeException(nameof(location)),
    };

    private void ReadRunKey(List<StartupEntry> entries, RegistryHive hive, RegistryView view, StartupLocation location)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(RunPath);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                var command = key.GetValue(name) as string ?? string.Empty;
                var exe = CommandLineParser.ExtractExecutable(command);
                entries.Add(Build(name, command, exe, location));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogInformation("Cannot read {Location}: {Message}", location, ex.Message);
        }
    }

    private void ReadFolder(List<StartupEntry> entries, string folder, StartupLocation location)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutResolver.TryResolve(file) : file;
                entries.Add(Build(fileName, file, target, location) with { Name = fileName });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogInformation("Cannot read startup folder {Location}: {Message}", location, ex.Message);
        }
    }

    private StartupEntry Build(string name, string command, string? exe, StartupLocation location)
    {
        var exists = exe is not null && File.Exists(exe);
        string? publisher = null;
        var signature = SignatureState.Unknown;
        if (exists)
        {
            try { publisher = FileVersionInfo.GetVersionInfo(exe!).CompanyName?.Trim(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            signature = _signatures.Verify(exe!);
        }

        var entry = new StartupEntry
        {
            Id = StartupEntry.MakeId(location, name),
            Name = name,
            Command = command,
            ExecutablePath = exe,
            Publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher,
            Location = location,
            Signature = signature,
            ExecutableExists = exists,
            IsMicrosoftComponent = IsWindowsComponent(exe, publisher),
        };

        byte[]? approval = null;
        try { approval = ReadApprovalValue(entry); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }

        return entry with { IsEnabled = StartupApprovalValue.IsEnabled(approval) };
    }

    private static bool IsWindowsComponent(string? exe, string? publisher)
    {
        if (exe is null) return false;
        if (ProcessCatalog.IsSecurity(exe)) return true;
        return ProcessCatalog.IsInWindowsDirectory(exe) && publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;
    }
}

public static class CommandLineParser
{
    /// <summary>Extracts the executable from a Run command such as "\"C:\\A B\\app.exe\" --minimized".</summary>
    public static string? ExtractExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());

        if (expanded.StartsWith('"'))
        {
            var end = expanded.IndexOf('"', 1);
            return end > 1 ? expanded[1..end] : null;
        }

        var exeIndex = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0) return expanded[..(exeIndex + 4)];

        var space = expanded.IndexOf(' ');
        return space > 0 ? expanded[..space] : expanded;
    }
}

/// <summary>Resolves .lnk targets through the documented IShellLink COM interface (read-only).</summary>
[SupportedOSPlatform("windows")]
internal static class ShortcutResolver
{
    public static string? TryResolve(string lnkPath)
    {
        object? link = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
            if (type is null) return null;
            link = Activator.CreateInstance(type);
            if (link is null) return null;
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var target = sb.ToString();
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FileNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    }
}
