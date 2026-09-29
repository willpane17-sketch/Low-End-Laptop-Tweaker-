using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Tickshift.Core;
using Tickshift.Core.Errors;

namespace Tickshift.Security;

/// <summary>
/// The complete, closed set of operations the elevated helper can perform. The helper accepts
/// nothing else — there is no generic "run this command" path, by design.
/// </summary>
public enum ElevatedOperationKind
{
    CreateRestorePoint,
    SetMachineStartupApproval,
}

public enum ElevatedExitCode
{
    Success = 0,
    Failure = 1,
    InvalidArguments = 2,
    AccessDenied = 3,
    Unsupported = 4,
    RestorePointFrequencyLimited = 5,
    NotFound = 6,
}

public sealed record ElevatedOperation(ElevatedOperationKind Kind, IReadOnlyList<string> Arguments)
{
    public const string RestorePointVerb = "restore-point";
    public const string StartupApprovalVerb = "startup-approval";
    public const string DeleteToken = "delete";

    public static ElevatedOperation CreateRestorePoint() => new(ElevatedOperationKind.CreateRestorePoint, [RestorePointVerb]);

    /// <param name="list">"Run", "Run32" or "StartupFolder" (machine scope).</param>
    /// <param name="valueName">The startup entry name; transported as base64 to avoid any quoting ambiguity.</param>
    /// <param name="value">12-byte StartupApproved value, or null to delete the value (restoring "never set").</param>
    public static ElevatedOperation SetMachineStartupApproval(string list, string valueName, byte[]? value)
    {
        var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(valueName));
        var encodedValue = value is null ? DeleteToken : Convert.ToHexString(value);
        var op = new ElevatedOperation(ElevatedOperationKind.SetMachineStartupApproval, [StartupApprovalVerb, list, encodedName, encodedValue]);
        if (!ElevatedArgumentValidator.TryParse(op.Arguments, out _, out var error))
            throw new ArgumentException(error);
        return op;
    }
}

public sealed record ParsedElevatedRequest(ElevatedOperationKind Kind, string? List, string? ValueName, byte[]? Value, bool Delete);

/// <summary>Strict validator shared by the client (pre-flight) and the helper (authoritative check).</summary>
public static class ElevatedArgumentValidator
{
    public static readonly IReadOnlySet<string> AllowedLists = new HashSet<string>(StringComparer.Ordinal) { "Run", "Run32", "StartupFolder" };

    public static bool TryParse(IReadOnlyList<string> args, out ParsedElevatedRequest? request, out string error)
    {
        request = null;
        error = string.Empty;
        if (args.Count == 0) { error = "No operation specified."; return false; }

        switch (args[0])
        {
            case ElevatedOperation.RestorePointVerb when args.Count == 1:
                request = new ParsedElevatedRequest(ElevatedOperationKind.CreateRestorePoint, null, null, null, false);
                return true;

            case ElevatedOperation.StartupApprovalVerb when args.Count == 4:
                if (!AllowedLists.Contains(args[1])) { error = "Unknown startup list."; return false; }

                string name;
                try
                {
                    if (args[2].Length > 2000) { error = "Name too long."; return false; }
                    name = Encoding.UTF8.GetString(Convert.FromBase64String(args[2]));
                }
                catch (FormatException)
                {
                    error = "Malformed name.";
                    return false;
                }

                if (name.Length is 0 or > 260 || name.Any(char.IsControl) || name.Contains('\\'))
                {
                    error = "Invalid startup entry name.";
                    return false;
                }

                if (args[3] == ElevatedOperation.DeleteToken)
                {
                    request = new ParsedElevatedRequest(ElevatedOperationKind.SetMachineStartupApproval, args[1], name, null, true);
                    return true;
                }

                if (args[3].Length != 24) { error = "Approval value must be 12 bytes."; return false; }
                byte[] value;
                try { value = Convert.FromHexString(args[3]); }
                catch (FormatException) { error = "Malformed approval value."; return false; }

                if (!StartupApprovalValue.IsRecognisedState(value[0])) { error = "Unrecognised approval state."; return false; }
                request = new ParsedElevatedRequest(ElevatedOperationKind.SetMachineStartupApproval, args[1], name, value, false);
                return true;

            default:
                error = "Unknown or malformed operation.";
                return false;
        }
    }
}

/// <summary>
/// Encoding of the Explorer "StartupApproved" values used by Task Manager and Settings › Apps › Startup.
/// First byte: 0x02 (or 0x06) enabled, 0x03 (or 0x07) disabled; bytes 4-11 hold the FILETIME of the change.
/// </summary>
public static class StartupApprovalValue
{
    public static bool IsRecognisedState(byte b) => b is 0x02 or 0x03 or 0x06 or 0x07;

    /// <summary>Absent value means enabled (Windows default).</summary>
    public static bool IsEnabled(byte[]? value) => value is null || value.Length == 0 || (value[0] & 0x01) == 0;

    public static byte[] Create(bool enabled, DateTimeOffset when)
    {
        var bytes = new byte[12];
        bytes[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(when.UtcDateTime.ToFileTimeUtc()).CopyTo(bytes, 4);
        return bytes;
    }
}

public sealed record ElevatedResult(ElevatedExitCode Code, string Message)
{
    public bool Succeeded => Code == ElevatedExitCode.Success;
}

public interface IElevatedOperationRunner
{
    bool IsAvailable { get; }
    Task<ElevatedResult> RunAsync(ElevatedOperation operation, CancellationToken ct = default);
}

/// <summary>
/// Launches the helper with a UAC prompt for one validated operation. The main application
/// itself always runs as a standard user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedHelperRunner : IElevatedOperationRunner
{
    private readonly string _helperPath = Path.Combine(AppContext.BaseDirectory, Brand.ElevatedHelperFileName);

    public bool IsAvailable => File.Exists(_helperPath) && !File.GetAttributes(_helperPath).HasFlag(FileAttributes.ReparsePoint);

    public async Task<ElevatedResult> RunAsync(ElevatedOperation operation, CancellationToken ct = default)
    {
        if (!IsAvailable) return new(ElevatedExitCode.Unsupported, "The administrator helper is not installed next to TICKSHIFT.");
        if (!ElevatedArgumentValidator.TryParse(operation.Arguments, out _, out var error))
            return new(ElevatedExitCode.InvalidArguments, error);

        var psi = new ProcessStartInfo(_helperPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = string.Join(' ', operation.Arguments.Select(QuoteArgument)),
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return new(ElevatedExitCode.Failure, "The administrator helper could not be started.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var code = Enum.IsDefined(typeof(ElevatedExitCode), process.ExitCode) ? (ElevatedExitCode)process.ExitCode : ElevatedExitCode.Failure;
            return new(code, Describe(code));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(ElevatedExitCode.AccessDenied, "Administrator approval was declined. No change was made.");
        }
        catch (OperationCanceledException)
        {
            return new(ElevatedExitCode.Failure, "The administrator helper did not finish in time.");
        }
        catch (Exception ex)
        {
            return new(ElevatedExitCode.Failure, ErrorClassifier.FriendlyMessage(ErrorClassifier.Classify(ex)));
        }
    }

    public static string Describe(ElevatedExitCode code) => code switch
    {
        ElevatedExitCode.Success => "Completed with administrator approval.",
        ElevatedExitCode.InvalidArguments => "The request was rejected by the helper's validation.",
        ElevatedExitCode.AccessDenied => "Windows denied the change even with administrator approval.",
        ElevatedExitCode.Unsupported => "This operation is not supported on this device.",
        ElevatedExitCode.RestorePointFrequencyLimited => "Windows skipped creating a new restore point because one was created recently (Windows allows one every 24 hours by default).",
        ElevatedExitCode.NotFound => "The item no longer exists.",
        _ => "The administrator helper reported a failure.",
    };

    // Arguments are already validated to base64/hex/fixed tokens; quoting is defence in depth.
    private static string QuoteArgument(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || c is '-' or '+' or '/' or '=') ? arg : "\"" + arg.Replace("\"", string.Empty) + "\"";
}
