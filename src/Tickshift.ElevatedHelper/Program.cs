using System.Management;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Tickshift.Security;

namespace Tickshift.ElevatedHelper;

/// <summary>
/// TICKSHIFT's administrator helper. Security properties:
///  - performs only the operations in <see cref="ElevatedOperationKind"/>, validated by the same strict
///    validator the app uses; anything else exits with InvalidArguments;
///  - never executes a shell, script, or any path supplied on the command line;
///  - verifies that a startup entry actually exists in the machine Run key / common Startup folder
///    before touching its approval value, and only writes the Explorer StartupApproved value;
///  - exits immediately after one operation. The result is reported through the exit code only.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private static int Main(string[] args)
    {
        try
        {
            if (!IsElevated()) return (int)ElevatedExitCode.AccessDenied;
            if (!ElevatedArgumentValidator.TryParse(args, out var request, out _) || request is null)
                return (int)ElevatedExitCode.InvalidArguments;

            return (int)(request.Kind switch
            {
                ElevatedOperationKind.CreateRestorePoint => CreateRestorePoint(),
                ElevatedOperationKind.SetMachineStartupApproval => SetStartupApproval(request),
                _ => ElevatedExitCode.InvalidArguments,
            });
        }
        catch (UnauthorizedAccessException)
        {
            return (int)ElevatedExitCode.AccessDenied;
        }
        catch (Exception)
        {
            return (int)ElevatedExitCode.Failure;
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static ElevatedExitCode CreateRestorePoint()
    {
        var scope = new ManagementScope(@"\\.\root\default");
        var before = LatestSequenceNumber(scope);

        using var systemRestore = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
        using var parameters = systemRestore.GetMethodParameters("CreateRestorePoint");
        parameters["Description"] = "TICKSHIFT: before optimisation";
        parameters["RestorePointType"] = 12; // MODIFY_SETTINGS
        parameters["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
        using var result = systemRestore.InvokeMethod("CreateRestorePoint", parameters, null);
        var code = Convert.ToUInt32(result?["ReturnValue"] ?? 1u);

        if (code == 1058) return ElevatedExitCode.Unsupported; // System Restore service disabled
        if (code != 0) return ElevatedExitCode.Failure;

        // Windows silently skips creation when a restore point was made in the last 24 hours.
        var after = LatestSequenceNumber(scope);
        return after is not null && after != before ? ElevatedExitCode.Success : ElevatedExitCode.RestorePointFrequencyLimited;
    }

    private static uint? LatestSequenceNumber(ManagementScope scope)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT SequenceNumber FROM SystemRestore"));
            using var results = searcher.Get();
            uint? max = null;
            foreach (var o in results)
            {
                using (o)
                {
                    var n = Convert.ToUInt32(o["SequenceNumber"]);
                    if (max is null || n > max) max = n;
                }
            }

            return max;
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static ElevatedExitCode SetStartupApproval(ParsedElevatedRequest request)
    {
        var list = request.List!;
        var name = request.ValueName!;
        if (!EntryExists(list, name)) return ElevatedExitCode.NotFound;

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.CreateSubKey(ApprovedRoot + list, writable: true);
        if (request.Delete) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, request.Value!, RegistryValueKind.Binary);

        // Verify.
        var now = key.GetValue(name) as byte[];
        var ok = request.Delete ? now is null : now is not null && now.AsSpan().SequenceEqual(request.Value);
        return ok ? ElevatedExitCode.Success : ElevatedExitCode.Failure;
    }

    private static bool EntryExists(string list, string name)
    {
        switch (list)
        {
            case "Run":
            case "Run32":
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, list == "Run" ? RegistryView.Registry64 : RegistryView.Registry32))
                using (var run = hklm.OpenSubKey(RunPath))
                {
                    return run?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
                }

            case "StartupFolder":
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                return !string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, name))
                    && string.Equals(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(folder, name))), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);

            default:
                return false;
        }
    }
}
