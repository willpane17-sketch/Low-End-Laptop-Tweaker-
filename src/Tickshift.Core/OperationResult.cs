using Tickshift.Core.Errors;

namespace Tickshift.Core;

public enum OperationStatus
{
    Succeeded,
    Failed,
    Skipped,
    NotApplicable,
    Cancelled,
    DryRun,
}

/// <summary>Result of any system operation. Never report success without verification.</summary>
public sealed record OperationResult(
    OperationStatus Status,
    string Message,
    ErrorKind Error = ErrorKind.None,
    string? TechnicalDetail = null)
{
    public bool IsSuccess => Status == OperationStatus.Succeeded;

    public static OperationResult Success(string message) => new(OperationStatus.Succeeded, message);
    public static OperationResult Skipped(string message) => new(OperationStatus.Skipped, message);
    public static OperationResult NotApplicable(string message) => new(OperationStatus.NotApplicable, message);
    public static OperationResult Cancelled(string message = "Cancelled.") => new(OperationStatus.Cancelled, message, ErrorKind.Interrupted);
    public static OperationResult Dry(string message) => new(OperationStatus.DryRun, message);

    public static OperationResult Failure(string message, ErrorKind error = ErrorKind.Unknown, string? detail = null)
        => new(OperationStatus.Failed, message, error, detail);

    public static OperationResult FromException(Exception ex, string context)
    {
        var kind = ErrorClassifier.Classify(ex);
        if (kind == ErrorKind.Interrupted) return Cancelled($"{context}: cancelled.");
        return new(OperationStatus.Failed, $"{context}: {ErrorClassifier.FriendlyMessage(kind)}", kind,
            $"{ex.GetType().Name}: {ex.Message}");
    }
}
