using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Errors;
using Tickshift.Security;

namespace Tickshift.Optimization;

public interface IRestorePointService
{
    SystemRestoreStatus GetStatus();
    Task<OperationResult> CreateAsync(CancellationToken ct);
}

/// <summary>
/// Optional Windows restore point, created by the narrowly scoped elevated helper after a UAC prompt.
/// Detects when System Restore is off or disabled by policy, and reports Windows' 24-hour frequency
/// limit honestly instead of claiming a restore point was made.
/// </summary>
public sealed class RestorePointService(ISystemRestoreInfo info, IElevatedOperationRunner runner) : IRestorePointService
{
    public SystemRestoreStatus GetStatus() => info.GetStatus();

    public async Task<OperationResult> CreateAsync(CancellationToken ct)
    {
        var status = info.GetStatus();
        if (status.DisabledByPolicy) return OperationResult.NotApplicable(status.Explanation);
        if (status.Enabled == false) return OperationResult.NotApplicable(status.Explanation + " TICKSHIFT's own recovery records are still kept.");
        if (!runner.IsAvailable) return OperationResult.NotApplicable("The administrator helper is not installed, so no restore point was created. TICKSHIFT's own recovery records are still kept.");

        var result = await runner.RunAsync(ElevatedOperation.CreateRestorePoint(), ct).ConfigureAwait(false);
        return result.Code switch
        {
            ElevatedExitCode.Success => OperationResult.Success("Windows restore point created."),
            ElevatedExitCode.RestorePointFrequencyLimited => OperationResult.Skipped(result.Message + " A recent restore point already exists."),
            ElevatedExitCode.AccessDenied => OperationResult.Skipped(result.Message),
            ElevatedExitCode.Unsupported => OperationResult.NotApplicable(result.Message),
            _ => OperationResult.Failure(result.Message, ErrorKind.Unknown),
        };
    }
}
