using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickshift.Core;
using Tickshift.Core.Models;
using Tickshift.Persistence;
using Tickshift.Security;

namespace Tickshift.Optimization.Optimisations;

public sealed record CleanupScan(long Bytes, int Files, bool Truncated);

/// <summary>
/// Base for deleting recognised disposable files. Every candidate passes <see cref="SafePathPolicy"/>
/// (inside an allowlisted root, no reparse points, never a personal folder). Files in use are skipped.
/// Deleted files cannot be restored, which is stated plainly in the UI and the change record.
/// </summary>
public abstract class FileCleanupOptimisation : IOptimisation
{
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(6);
    private readonly ILogger _logger;

    protected FileCleanupOptimisation(ILogger? logger)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Description { get; }
    public OptimisationCategory Category => OptimisationCategory.Cleanup;
    public RiskLevel Risk => RiskLevel.Low;
    public PrivilegeLevel RequiredPrivilege => PrivilegeLevel.StandardUser;
    public Reversibility Reversibility => Reversibility.NotReversible;
    public abstract IReadOnlyList<string> Preconditions { get; }
    public abstract OptimisationPreset AvailableIn { get; }
    public abstract OptimisationPreset SelectedByDefaultIn { get; }
    public virtual string? UncertaintyNote => "Frees disk space. It does not usually make Windows faster unless the drive is nearly full.";
    public virtual bool RequiresExplicitApproval => false;

    /// <summary>Only files older than this are eligible, so files an app is actively using are left alone.</summary>
    protected abstract TimeSpan MinimumAge { get; }

    /// <summary>The allowlisted roots for this cleanup.</summary>
    protected abstract IReadOnlyList<string> GetRoots();

    /// <summary>Extra preconditions (e.g. "browser must be closed"). Return a reason to block, or null.</summary>
    protected virtual string? CheckBlockingCondition(OptimisationContext context) => null;

    protected virtual long MinimumWorthwhileBytes => 5L * 1024 * 1024;

    public async Task<OptimisationAssessment> AssessAsync(OptimisationContext context, CancellationToken ct)
    {
        var blocking = CheckBlockingCondition(context);
        if (blocking is not null) return OptimisationAssessment.NotApplicable(blocking);

        var roots = GetRoots().Where(Directory.Exists).ToList();
        if (roots.Count == 0) return OptimisationAssessment.NotApplicable("Nothing to clean on this device.");

        var scan = await Task.Run(() => Scan(roots, collect: null, ct), ct).ConfigureAwait(false);
        if (scan.Bytes < MinimumWorthwhileBytes)
            return OptimisationAssessment.NotApplicable($"Only {Format.Bytes(scan.Bytes)} found — not worth cleaning.");

        return new OptimisationAssessment(true,
            $"{scan.Files:N0} eligible files{(scan.Truncated ? " (scan stopped early)" : string.Empty)}.",
            $"Delete {scan.Files:N0} files older than {Format.Duration(MinimumAge)} ({Format.Bytes(scan.Bytes)}). Files in use are skipped.",
            $"Up to {Format.Bytes(scan.Bytes)} of disk space",
            JsonSerializer.Serialize(new CleanupScan(scan.Bytes, scan.Files, scan.Truncated)));
    }

    public async Task<ApplyOutcome> ApplyAsync(OptimisationContext context, OptimisationAssessment assessment, IChangeRecorder recorder, CancellationToken ct)
    {
        var blocking = CheckBlockingCondition(context);
        if (blocking is not null) return new ApplyOutcome(OperationResult.Skipped(blocking), []);

        var roots = GetRoots().Where(Directory.Exists).ToList();
        var policy = new SafePathPolicy(roots);
        var changeId = await recorder.BeginAsync(string.Join("; ", roots.Select(LogRedactor.Redact)), Title, null, null, reversible: false, rollbackData: null, ct).ConfigureAwait(false);

        long freed = 0;
        int deleted = 0, skipped = 0;
        var deletedPaths = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                var candidates = new List<FileInfo>();
                Scan(roots, candidates, ct);
                foreach (var file in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!policy.IsDeletionAllowed(file.FullName, out _)) { skipped++; continue; }
                    try
                    {
                        var length = file.Length;
                        if (file.Attributes.HasFlag(FileAttributes.ReadOnly)) { skipped++; continue; }
                        file.Delete();
                        freed += length;
                        deleted++;
                        if (deletedPaths.Count < 2000) deletedPaths.Add(file.FullName);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped++; // in use or protected — leave it
                    }
                }

                RemoveEmptyDirectories(roots, policy, ct);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await recorder.MarkAppliedAsync(changeId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await recorder.MarkAppliedAsync(changeId, ct).ConfigureAwait(false);
        _logger.LogInformation("{Id}: deleted {Deleted} files ({Bytes} bytes), skipped {Skipped}", Id, deleted, freed, skipped);
        var message = $"Deleted {deleted:N0} files and freed {Format.Bytes(freed)}" + (skipped > 0 ? $"; skipped {skipped:N0} files that were in use or protected." : ".");
        var result = deleted > 0 ? OperationResult.Success(message) : OperationResult.Skipped(message);
        return new ApplyOutcome(result, changeId == Guid.Empty ? [] : [changeId], Format.Bytes(freed) + " freed",
            JsonSerializer.Serialize(deletedPaths.Take(200).ToList()));
    }

    public Task<VerificationResult> VerifyAsync(OptimisationContext context, OptimisationAssessment assessment, ApplyOutcome outcome, CancellationToken ct)
    {
        if (!outcome.Result.IsSuccess) return Task.FromResult(new VerificationResult(false, "Nothing was deleted."));
        List<string>? paths = null;
        try { paths = outcome.VerificationData is { } json ? JsonSerializer.Deserialize<List<string>>(json) : null; }
        catch (JsonException) { }

        if (paths is null || paths.Count == 0) return Task.FromResult(new VerificationResult(false, "Deleted files could not be verified."));
        var stillPresent = paths.Count(File.Exists);
        return Task.FromResult(stillPresent == 0
            ? new VerificationResult(true, $"Confirmed: sampled deleted files are gone ({paths.Count} checked).")
            : new VerificationResult(false, $"{stillPresent} of {paths.Count} sampled files still exist."));
    }

    public Task<OperationResult> RollbackAsync(ChangeRecord record, CancellationToken ct) =>
        Task.FromResult(OperationResult.NotApplicable("Deleted files cannot be restored. This was stated before the cleanup ran."));

    public Task<InterruptedState> InspectInterruptedAsync(ChangeRecord record, CancellationToken ct) =>
        Task.FromResult(InterruptedState.Unknown);

    private CleanupScan Scan(IReadOnlyList<string> roots, List<FileInfo>? collect, CancellationToken ct)
    {
        long bytes = 0;
        var count = 0;
        var truncated = false;
        var cutoff = DateTime.UtcNow - MinimumAge;
        var deadline = DateTime.UtcNow + ScanBudget;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };

        foreach (var root in roots)
        {
            try
            {
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (collect is null && (count & 255) == 0 && DateTime.UtcNow > deadline) { truncated = true; break; }
                    if (file.LastWriteTimeUtc > cutoff) continue;
                    bytes += file.Length;
                    count++;
                    collect?.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                truncated = true;
            }
        }

        return new CleanupScan(bytes, count, truncated);
    }

    private void RemoveEmptyDirectories(IReadOnlyList<string> roots, SafePathPolicy policy, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - MinimumAge;
        foreach (var root in roots)
        {
            IEnumerable<string> dirs;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                dirs = Directory.EnumerateDirectories(root, "*", options).OrderByDescending(d => d.Length).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var dir in dirs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) > cutoff) continue;
                    if (Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                    if (!policy.IsDeletionAllowed(dir, out _)) continue;
                    Directory.Delete(dir, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}

/// <summary>Old files in the user's own %TEMP% folder.</summary>
public sealed class UserTempCleanupOptimisation(ICleanupLocations locations, ILogger<UserTempCleanupOptimisation>? logger = null) : FileCleanupOptimisation(logger)
{
    public const string OptimisationId = "cleanup.user-temp";
    public override string Id => OptimisationId;
    public override string Title => "Remove old temporary files";
    public override string Description => "Deletes files in your personal Temp folder that have not changed for at least two days. Apps create these and usually forget to remove them.";
    public override IReadOnlyList<string> Preconditions => ["Only files older than 48 hours", "Files in use are skipped", "Never follows shortcuts, junctions or links"];
    public override OptimisationPreset AvailableIn => OptimisationPreset.All;
    public override OptimisationPreset SelectedByDefaultIn => OptimisationPreset.Safe | OptimisationPreset.Balanced;
    protected override TimeSpan MinimumAge => TimeSpan.FromHours(48);
    protected override IReadOnlyList<string> GetRoots() => locations.UserTemp;
}

/// <summary>Windows Error Reporting files for the current user (crash reports already processed or queued).</summary>
public sealed class ErrorReportCleanupOptimisation(ICleanupLocations locations, ILogger<ErrorReportCleanupOptimisation>? logger = null) : FileCleanupOptimisation(logger)
{
    public const string OptimisationId = "cleanup.error-reports";
    public override string Id => OptimisationId;
    public override string Title => "Remove old error reports";
    public override string Description => "Deletes your old Windows Error Reporting files (crash reports) older than a week.";
    public override IReadOnlyList<string> Preconditions => ["Current user's reports only", "Older than 7 days"];
    public override OptimisationPreset AvailableIn => OptimisationPreset.All;
    public override OptimisationPreset SelectedByDefaultIn => OptimisationPreset.Safe | OptimisationPreset.Balanced;
    protected override TimeSpan MinimumAge => TimeSpan.FromDays(7);
    protected override long MinimumWorthwhileBytes => 1L * 1024 * 1024;
    protected override IReadOnlyList<string> GetRoots() => locations.ErrorReports;
}

/// <summary>
/// Chromium disposable caches (Cache_Data, Code Cache, GPUCache). Never cookies, logins, history,
/// sessions or local storage. Only runs when the browser is closed, and only after explicit approval.
/// </summary>
public sealed class BrowserCacheCleanupOptimisation(ICleanupLocations locations, Core.Abstractions.IBrowserProvider browsers, ILogger<BrowserCacheCleanupOptimisation>? logger = null) : FileCleanupOptimisation(logger)
{
    public const string OptimisationId = "cleanup.browser-cache";
    public override string Id => OptimisationId;
    public override string Title => "Clear disposable browser cache (Edge and Chrome)";
    public override string Description => "Deletes the page, code and graphics caches that the browser rebuilds automatically. Saved passwords, cookies, history, open tabs and sign-ins are never touched.";
    public override IReadOnlyList<string> Preconditions => ["The browser must be closed (you close it — TICKSHIFT will not)", "Requires your explicit approval"];
    public override OptimisationPreset AvailableIn => OptimisationPreset.All;
    public override OptimisationPreset SelectedByDefaultIn => OptimisationPreset.None;
    public override bool RequiresExplicitApproval => true;
    public override string? UncertaintyNote => "Frees disk space only. Pages may load slightly slower the first time afterwards while the cache rebuilds.";
    protected override TimeSpan MinimumAge => TimeSpan.Zero;
    protected override long MinimumWorthwhileBytes => 50L * 1024 * 1024;

    protected override IReadOnlyList<string> GetRoots() =>
        [.. locations.BrowserCache(BrowserKind.Edge), .. locations.BrowserCache(BrowserKind.Chrome)];

    protected override string? CheckBlockingCondition(OptimisationContext context)
    {
        if (context.TradingModeActive) return "Deferred while Trading Mode is active.";
        var running = new[] { BrowserKind.Edge, BrowserKind.Chrome }.Where(browsers.IsRunning).Select(k => k == BrowserKind.Edge ? "Microsoft Edge" : "Google Chrome").ToList();
        return running.Count > 0 ? $"Please close {string.Join(" and ", running)} yourself first. TICKSHIFT never closes your browser." : null;
    }
}
