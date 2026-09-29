using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Errors;
using Tickshift.Core.Models;
using Tickshift.Persistence;
using Tickshift.Security;

namespace Tickshift.Optimization.Startup;

internal sealed record StartupRollback(StartupLocation Location, string Name, string? OriginalHex, string AppliedHex);

/// <summary>
/// Enables/disables startup entries the same way Task Manager does (Explorer StartupApproved values).
/// The Run entry itself is never deleted. Per-user entries need no elevation; machine-wide entries go
/// through the elevated helper after a UAC prompt. Every change is journalled, verified and reversible.
/// </summary>
public sealed class StartupManagerService : IChangeRollbackHandler
{
    public const string HandlerId = "startup.toggle";

    private readonly IStartupProvider _provider;
    private readonly IChangeJournal _journal;
    private readonly IElevatedOperationRunner _elevated;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    public StartupManagerService(IStartupProvider provider, IChangeJournal journal, IElevatedOperationRunner elevated, IClock clock, ILogger<StartupManagerService>? logger = null)
    {
        _provider = provider;
        _journal = journal;
        _elevated = elevated;
        _clock = clock;
        _logger = logger ?? NullLogger<StartupManagerService>.Instance;
    }

    public string Id => HandlerId;

    public IReadOnlyList<StartupEntry> GetEntries() => _provider.Enumerate();

    /// <summary>Returns a reason when TICKSHIFT refuses to change an entry, or null when allowed.</summary>
    public static string? GetChangeBlocker(StartupEntry entry, bool enable)
    {
        if (enable) return null;
        if (entry.ExecutablePath is { } exe && ProcessCatalog.IsSecurity(exe))
            return "This is security software. TICKSHIFT will not disable security protections.";
        if (entry.Name.Equals(Brand.AutostartValueName, StringComparison.OrdinalIgnoreCase))
            return "Use Settings › Launch with Windows to control TICKSHIFT itself.";
        return null;
    }

    public async Task<OperationResult> SetEnabledAsync(StartupEntry entry, bool enable, bool dryRun, CancellationToken ct)
    {
        var blocker = GetChangeBlocker(entry, enable);
        if (blocker is not null) return OperationResult.Skipped(blocker);

        byte[]? original;
        try { original = _provider.ReadApprovalValue(entry); }
        catch (Exception ex) { return OperationResult.FromException(ex, "Read startup state"); }

        if (StartupApprovalValue.IsEnabled(original) == enable)
            return OperationResult.NotApplicable($"{entry.Name} is already {(enable ? "enabled" : "disabled")}.");

        var applied = StartupApprovalValue.Create(enable, _clock.Now);
        if (dryRun) return OperationResult.Dry($"Dry run — would {(enable ? "enable" : "disable")} {entry.Name}.");

        var rollback = new StartupRollback(entry.Location, entry.Name, original is null ? null : Convert.ToHexString(original), Convert.ToHexString(applied));
        var record = await _journal.AddAsync(new ChangeRecord
        {
            RunId = Guid.NewGuid(),
            OptimisationId = HandlerId,
            Description = $"{(enable ? "Enable" : "Disable")} startup app \"{entry.Name}\"",
            Target = $"{entry.LocationDescription} › {entry.Name}",
            OriginalValue = StartupApprovalValue.IsEnabled(original) ? "Enabled" : "Disabled",
            NewValue = enable ? "Enabled" : "Disabled",
            Timestamp = _clock.Now,
            Status = ChangeStatus.Pending,
            Reversible = true,
            RollbackData = JsonSerializer.Serialize(rollback),
            Source = "Startup Manager",
        }, ct).ConfigureAwait(false);

        var write = await WriteAsync(entry, applied, ct).ConfigureAwait(false);
        if (!write.IsSuccess)
        {
            await _journal.UpdateAsync(record.Id, r => r with { Status = ChangeStatus.Failed, ErrorMessage = write.Message }, CancellationToken.None).ConfigureAwait(false);
            return write;
        }

        // Verify by reading the value back from the registry.
        var now = SafeRead(entry);
        var verified = StartupApprovalValue.IsEnabled(now) == enable;
        await _journal.UpdateAsync(record.Id, r => r with
        {
            Status = verified ? ChangeStatus.Verified : ChangeStatus.VerificationFailed,
            VerificationMessage = verified ? "Read back from the registry and confirmed." : "The registry did not show the expected value after writing.",
        }, CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation("Startup entry {Location}/{Name} set to {State}; verified={Verified}", entry.Location, LogRedactor.Redact(entry.Name), enable, verified);
        return verified
            ? OperationResult.Success($"{entry.Name} will {(enable ? "now" : "no longer")} start with Windows. You can undo this any time.")
            : OperationResult.Failure("The change could not be verified. See the Recovery Centre.", ErrorKind.Unknown);
    }

    private async Task<OperationResult> WriteAsync(StartupEntry entry, byte[]? value, CancellationToken ct)
    {
        if (!entry.RequiresElevation)
        {
            try
            {
                _provider.WriteApprovalValue(entry, value);
                return OperationResult.Success("Written.");
            }
            catch (Exception ex)
            {
                return OperationResult.FromException(ex, "Change startup app");
            }
        }

        var list = ListName(entry.Location);
        var result = await _elevated.RunAsync(ElevatedOperation.SetMachineStartupApproval(list, entry.Name, value), ct).ConfigureAwait(false);
        return result.Succeeded
            ? OperationResult.Success("Written with administrator approval.")
            : OperationResult.Failure(result.Message, result.Code == ElevatedExitCode.AccessDenied ? ErrorKind.AccessDenied : ErrorKind.Unknown);
    }

    public static string ListName(StartupLocation location) => location switch
    {
        StartupLocation.UserRun or StartupLocation.MachineRun => "Run",
        StartupLocation.MachineRun32 => "Run32",
        _ => "StartupFolder",
    };

    private byte[]? SafeRead(StartupEntry entry)
    {
        try { return _provider.ReadApprovalValue(entry); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return null; }
    }

    private StartupEntry? FindEntry(StartupRollback data) =>
        _provider.Enumerate().FirstOrDefault(e => e.Location == data.Location && e.Name == data.Name);

    public async Task<OperationResult> RollbackAsync(ChangeRecord record, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<StartupRollback>(record.RollbackData ?? "null");
        if (data is null) return OperationResult.Failure("Rollback information is missing.");
        var entry = FindEntry(data);
        if (entry is null) return OperationResult.Skipped("The startup entry no longer exists (the app may have been uninstalled).") with { Error = ErrorKind.Conflict };

        var current = SafeRead(entry);
        var currentHex = current is null ? null : Convert.ToHexString(current);
        if (currentHex == data.OriginalHex) return OperationResult.Success($"{entry.Name} is already in its original state.");
        if (StartupApprovalValue.IsEnabled(current) != StartupApprovalValue.IsEnabled(Convert.FromHexString(data.AppliedHex)))
            return new OperationResult(OperationStatus.Skipped, $"{entry.Name} was changed by something else since, so it was left as is.", ErrorKind.Conflict);

        var original = data.OriginalHex is null ? null : Convert.FromHexString(data.OriginalHex);
        var write = await WriteAsync(entry, original, ct).ConfigureAwait(false);
        if (!write.IsSuccess) return write;
        var after = SafeRead(entry);
        return StartupApprovalValue.IsEnabled(after) == StartupApprovalValue.IsEnabled(original)
            ? OperationResult.Success($"{entry.Name} restored to {(StartupApprovalValue.IsEnabled(original) ? "enabled" : "disabled")}.")
            : OperationResult.Failure("The restored value could not be verified.");
    }

    public Task<InterruptedState> InspectInterruptedAsync(ChangeRecord record, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<StartupRollback>(record.RollbackData ?? "null");
        if (data is null) return Task.FromResult(InterruptedState.Unknown);
        var entry = FindEntry(data);
        if (entry is null) return Task.FromResult(InterruptedState.Unknown);
        var current = SafeRead(entry);
        var appliedEnabled = StartupApprovalValue.IsEnabled(Convert.FromHexString(data.AppliedHex));
        return Task.FromResult(StartupApprovalValue.IsEnabled(current) == appliedEnabled ? InterruptedState.Applied : InterruptedState.NotApplied);
    }
}
