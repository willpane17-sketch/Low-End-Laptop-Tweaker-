using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tickshift.App.Infrastructure;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Hardware;
using Tickshift.Optimization;
using Tickshift.Optimization.Optimisations;
using Tickshift.Persistence;
using Tickshift.Trading;

namespace Tickshift.App.ViewModels;

public sealed partial class CleanupTarget : ObservableObject
{
    [ObservableProperty] private string _sizeText = "Not measured";
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private bool _canRun;
    [ObservableProperty] private bool _isWorking;

    public CleanupTarget(string id, string title, string description, string safety, bool irreversible)
    {
        Id = id;
        Title = title;
        Description = description;
        Safety = safety;
        Irreversible = irreversible;
    }

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public string Safety { get; }
    public bool Irreversible { get; }
}

public sealed record DriveUsage(string Name, string Detail, double UsedPercent, bool Low);

public sealed partial class CleanupViewModel : PageViewModel
{
    private const string RecycleBinId = "cleanup.recycle-bin";
    private readonly OptimisationEngine _engine;
    private readonly ISystemInfoProvider _info;
    private readonly IRecycleBin _recycleBin;
    private readonly IDialogService _dialogs;
    private readonly ILinkLauncher _links;
    private readonly TradingModeService _trading;
    private readonly ISettingsStore _settings;
    private readonly IChangeJournal _journal;

    public CleanupViewModel(OptimisationEngine engine, ISystemInfoProvider info, IRecycleBin recycleBin, IDialogService dialogs,
        ILinkLauncher links, TradingModeService trading, ISettingsStore settings, IChangeJournal journal)
    {
        _engine = engine;
        _info = info;
        _recycleBin = recycleBin;
        _dialogs = dialogs;
        _links = links;
        _trading = trading;
        _settings = settings;
        _journal = journal;

        Targets =
        [
            new(UserTempCleanupOptimisation.OptimisationId, "Old temporary files", "Files in your personal Temp folder that apps left behind more than 48 hours ago.",
                "Files in use are skipped. Shortcuts, junctions and links are never followed.", true),
            new(ErrorReportCleanupOptimisation.OptimisationId, "Old error reports", "Windows Error Reporting crash reports older than a week, for your account only.",
                "Only affects diagnostic reports that have already been processed or queued.", true),
            new(BrowserCacheCleanupOptimisation.OptimisationId, "Browser caches (Edge and Chrome)", "Disposable page, script and graphics caches that the browser rebuilds automatically.",
                "Never touches passwords, cookies, history, open tabs or sign-ins. The browser must be closed — you close it.", true),
            new(RecycleBinId, "Recycle Bin", "Files you have already deleted. Emptying the Recycle Bin removes them permanently.",
                "Only runs when you confirm. Check the Recycle Bin first if you are unsure.", true),
        ];
    }

    public override string Title => "System Cleanup";

    [ObservableProperty] private bool _tradingActive;
    [ObservableProperty] private bool _isScanning;

    public IReadOnlyList<CleanupTarget> Targets { get; }
    public ObservableCollection<DriveUsage> Drives { get; } = [];

    public override Task OnNavigatedToAsync() => ScanAsync();

    [RelayCommand]
    private async Task ScanAsync()
    {
        TradingActive = _trading.IsActive;
        IsScanning = true;
        try
        {
            var storage = await _info.GetStorageAsync();
            Drives.Clear();
            foreach (var d in storage)
            {
                Drives.Add(new DriveUsage($"{d.RootPath.TrimEnd('\\')} {d.Label}", $"{Format.Bytes(d.FreeBytes)} free of {Format.Bytes(d.TotalBytes)}" +
                    (d.MediaKind != StorageMediaKind.Unknown ? $" · {(d.MediaKind == StorageMediaKind.SolidState ? "SSD" : "HDD")}" : string.Empty),
                    100 - d.FreePercent, d.FreePercent < _settings.Current.Automation.LowStoragePercent));
            }

            var context = new OptimisationContext(Guid.NewGuid(), _settings.Current, _trading.IsActive, false, "System Cleanup");
            foreach (var t in Targets.Where(t => t.Id != RecycleBinId))
            {
                var o = _engine.Find(t.Id);
                if (o is null) continue;
                var assessment = await o.AssessAsync(context, CancellationToken.None);
                t.SizeText = assessment.EstimatedEffect ?? (assessment.Applicable ? "Ready" : "Nothing to clean");
                t.Detail = assessment.Applicable ? assessment.ProposedChange : assessment.Reason;
                t.CanRun = assessment.Applicable && !TradingActive;
            }

            var bin = Targets.First(t => t.Id == RecycleBinId);
            var info = _recycleBin.Query();
            bin.SizeText = info is null ? "Unknown" : Format.Bytes(info.SizeBytes);
            bin.Detail = info is null ? "The Recycle Bin size could not be read." : $"{info.ItemCount:N0} item(s).";
            bin.CanRun = info is { ItemCount: > 0 } && !TradingActive;
        }
        catch (Exception ex)
        {
            ShowMessage("Scan failed: " + Core.Errors.ErrorClassifier.FriendlyMessage(Core.Errors.ErrorClassifier.Classify(ex)), true);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task CleanAsync(CleanupTarget? target)
    {
        if (target is null || !target.CanRun) return;
        if (_trading.IsActive)
        {
            ShowMessage("Cleanup is deferred while Trading Mode is active, to avoid disk activity during trading.");
            return;
        }

        var confirmed = _dialogs.Confirm(new ConfirmRequest(
            $"Clean {target.Title.ToLowerInvariant()}?",
            target.Detail + " " + target.Safety,
            ["Deleted files cannot be restored by TICKSHIFT."],
            target.Id == RecycleBinId ? "Empty Recycle Bin" : "Clean now",
            Destructive: target.Id == RecycleBinId,
            Acknowledgement: target.Id == RecycleBinId ? "I understand the files in the Recycle Bin will be permanently deleted." : null));
        if (!confirmed) return;

        target.IsWorking = true;
        try
        {
            if (target.Id == RecycleBinId)
            {
                if (_settings.Current.DryRunMode)
                {
                    ShowMessage("Dry run — the Recycle Bin would be emptied. Nothing was changed.");
                    return;
                }

                var result = await Task.Run(_recycleBin.Empty);
                await _journal.AddAsync(new ChangeRecord
                {
                    RunId = Guid.NewGuid(),
                    OptimisationId = RecycleBinId,
                    Description = "Empty the Recycle Bin",
                    Target = "Recycle Bin",
                    OriginalValue = target.SizeText,
                    NewValue = "Empty",
                    Timestamp = DateTimeOffset.Now,
                    Status = result.IsSuccess ? ChangeStatus.Completed : ChangeStatus.Failed,
                    Reversible = false,
                    VerificationMessage = result.IsSuccess ? "Recycle Bin reported empty afterwards." : null,
                    ErrorMessage = result.IsSuccess ? null : result.Message,
                    Source = "System Cleanup",
                });
                ShowResult(result);
            }
            else
            {
                var run = await _engine.RunDirectAsync([target.Id], new ExecutionOptions(_settings.Current.DryRunMode, false, false, TimeSpan.Zero, "System Cleanup"), CancellationToken.None);
                var item = run.Items.FirstOrDefault(i => i.OptimisationId == target.Id);
                ShowMessage(item is null ? "Nothing was cleaned." : $"{item.Message}{(item.VerificationMessage is { } v ? " " + v : string.Empty)}",
                    item?.Status is RunItemStatus.Failed or RunItemStatus.AppliedNotVerified);
            }
        }
        finally
        {
            target.IsWorking = false;
            await ScanAsync();
        }
    }

    [RelayCommand]
    private void OpenLink(string? key)
    {
        if (key is null) return;
        var r = _links.Open(key);
        if (!r.IsSuccess) ShowResult(r);
    }
}
