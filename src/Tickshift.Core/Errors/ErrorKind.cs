using System.ComponentModel;
using System.Security;

namespace Tickshift.Core.Errors;

/// <summary>Coarse error categories used to produce accurate, user-friendly messages.</summary>
public enum ErrorKind
{
    None,
    AccessDenied,
    Unsupported,
    SensorUnavailable,
    Interrupted,
    NotFound,
    InvalidConfiguration,
    InUse,
    Conflict,
    Timeout,
    Unknown,
}

/// <summary>Exception carrying a classified <see cref="ErrorKind"/>.</summary>
public sealed class TickshiftException : Exception
{
    public TickshiftException(ErrorKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;

    public ErrorKind Kind { get; }
}

public static class ErrorClassifier
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorNotSupported = 50;
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED (e.g. UAC prompt declined)

    public static ErrorKind Classify(Exception? ex)
    {
        switch (ex)
        {
            case null:
                return ErrorKind.None;
            case TickshiftException t:
                return t.Kind;
            case OperationCanceledException:
                return ErrorKind.Interrupted;
            case UnauthorizedAccessException or SecurityException:
                return ErrorKind.AccessDenied;
            case PlatformNotSupportedException or NotSupportedException:
                return ErrorKind.Unsupported;
            case FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException:
                return ErrorKind.NotFound;
            case TimeoutException:
                return ErrorKind.Timeout;
            case System.Text.Json.JsonException or FormatException:
                return ErrorKind.InvalidConfiguration;
            case Win32Exception w:
                return w.NativeErrorCode switch
                {
                    ErrorAccessDenied => ErrorKind.AccessDenied,
                    ErrorCancelled => ErrorKind.Interrupted,
                    ErrorFileNotFound or ErrorPathNotFound => ErrorKind.NotFound,
                    ErrorSharingViolation or ErrorLockViolation => ErrorKind.InUse,
                    ErrorNotSupported => ErrorKind.Unsupported,
                    _ => ErrorKind.Unknown,
                };
            case IOException io:
                var code = io.HResult & 0xFFFF;
                if (code is ErrorSharingViolation or ErrorLockViolation) return ErrorKind.InUse;
                if (code == ErrorAccessDenied) return ErrorKind.AccessDenied;
                return ErrorKind.Unknown;
        }

        // Avoid a hard dependency on System.Management from Core.
        var typeName = ex.GetType().Name;
        if (typeName == "ManagementException")
        {
            return ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)
                ? ErrorKind.AccessDenied
                : ErrorKind.Unsupported;
        }

        if (ex is AggregateException agg && agg.InnerExceptions.Count == 1)
            return Classify(agg.InnerExceptions[0]);

        return ErrorKind.Unknown;
    }

    /// <summary>A short, honest, non-technical explanation for the user.</summary>
    public static string FriendlyMessage(ErrorKind kind) => kind switch
    {
        ErrorKind.None => "No error.",
        ErrorKind.AccessDenied => "Windows denied access. This change may need administrator approval, or it is protected by Windows.",
        ErrorKind.Unsupported => "This feature is not supported on this device or Windows version.",
        ErrorKind.SensorUnavailable => "The required hardware sensor is not available on this device.",
        ErrorKind.Interrupted => "The operation was cancelled before it finished.",
        ErrorKind.NotFound => "The item could not be found. It may have been removed or changed by another program.",
        ErrorKind.InvalidConfiguration => "Stored configuration was invalid and has been replaced with safe defaults.",
        ErrorKind.InUse => "The file or setting is currently in use by another program.",
        ErrorKind.Conflict => "Another program changed this setting in the meantime.",
        ErrorKind.Timeout => "The operation took too long and was stopped.",
        _ => "An unexpected problem occurred. Technical details have been written to the log.",
    };
}
