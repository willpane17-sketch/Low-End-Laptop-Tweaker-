using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tickshift.App.Infrastructure;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Optimization.Recovery;
using Tickshift.Optimization.Startup;
using Tickshift.Persistence;

namespace Tickshift.App.ViewModels;

public sealed partial class StartupEntryViewModel(StartupEntry entry) : ObservableObject
{
    [ObservableProperty] private StartupEntry _entry = entry;
    [ObservableProperty] private bool _isWorking;

    public string Name => Entry.Name;
    public string Publisher => Entry.Publisher ?? "Unknown publisher";
    public string Path => Entry.ExecutablePath ?? Entry.Command;
    public string Mechanism => Entry.LocationDescription;
    public SignatureState Signature => Entry.Signature;
    public bool IsEnabled => Entry.IsEnabled;
    public string StateText => Entry.IsEnabled ? "Enabled" : "Disabled";
    public bool IsWindows => Entry.IsMicrosoftComponent;
    public string Kind => Entry.IsMicrosoftComponent ? "Windows component" : "Third-party";
    public string ImpactText => Entry.RunningMemoryBytes is { } m ? $"Using {Format.Bytes(m)} now" : Entry.IsEnabled ? "Not running now" : "—";
    public bool NeedsAdmin => Entry.RequiresElevation;
    public string? Blocker => StartupManagerService.GetChangeBlocker(Entry, !Entry.IsEnabled);
    public bool Missing => Entry.ExecutablePath is not null && !Entry.ExecutableExists;

    partial void OnEntryChanged(StartupEntry value) => Refresh();

    /// <summary>Re-reads every bound property, e.g. to undo a toggle's visual state after the user cancels.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed partial class StartupViewModel : PageViewModel
{
    private readonly StartupManagerService _startup;
    private readonly IProcessProvider _processes;
    private readonly IDialogService _dialogs;
    private readonly ISettingsStore _settings;
    private readonly RecoveryService _recovery;
    private List<StartupEntryViewModel> _all = [];

    public StartupViewModel(StartupManagerService startup, IProcessProvider processes, IDialogService dialogs, ISettingsStore settings, RecoveryService recovery)
    {
        _startup = startup;
        _processes = processes;
        _dialogs = dialogs;
        _settings = settings;
        _recovery = recovery;
    }

    public override string Title => "Startup Manager";

    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private string _filter = "All";
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private int _recentChanges;

    public ObservableCollection<StartupEntryViewModel> Entries { get; } = [];

    public override Task OnNavigatedToAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var entries = await Task.Run(() =>
            {
                var list = _startup.GetEntries();
                var running = _processes.GetProcesses().Where(p => p.Path is not null)
                    .GroupBy(p => p.Path!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Sum(p => p.WorkingSetBytes), StringComparer.OrdinalIgnoreCase);
                return list.Select(e => e.ExecutablePath is not null && running.TryGetValue(e.ExecutablePath, out var mem) ? e with { RunningMemoryBytes = mem } : e).ToList();
            });
            _all = entries.Select(e => new StartupEntryViewModel(e)).ToList();
            ApplyFilter();
            RecentChanges = _recovery.GetActiveReversible().Count(r => r.OptimisationId == StartupManagerService.HandlerId);
        }
        catch (Exception ex)
        {
            ShowMessage("Startup entries could not be read: " + Core.Errors.ErrorClassifier.FriendlyMessage(Core.Errors.ErrorClassifier.Classify(ex)), true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void SetFilter(string value) => Filter = value;

    private void ApplyFilter()
    {
        IEnumerable<StartupEntryViewModel> q = _all;
        q = Filter switch
        {
            "Enabled" => q.Where(e => e.IsEnabled),
            "Disabled" => q.Where(e => !e.IsEnabled),
            "ThirdParty" => q.Where(e => !e.IsWindows),
            "Windows" => q.Where(e => e.IsWindows),
            _ => q,
        };
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(e => e.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase) || e.Publisher.Contains(s, StringComparison.CurrentCultureIgnoreCase)
                             || e.Path.Contains(s, StringComparison.CurrentCultureIgnoreCase));
        }

        Entries.Clear();
        foreach (var e in q) Entries.Add(e);
        var enabledThirdParty = _all.Count(e => e.IsEnabled && !e.IsWindows);
        Summary = $"{_all.Count} startup entries · {_all.Count(e => e.IsEnabled)} enabled · {enabledThirdParty} third-party apps start automatically";
    }

    [RelayCommand]
    private async Task ToggleAsync(StartupEntryViewModel? vm)
    {
        if (vm is null || vm.IsWorking) return;
        try
        {
            await ToggleCoreAsync(vm);
        }
        finally
        {
            vm.Refresh(); // the switch always reflects the real state, including after a cancel
        }
    }

    private async Task ToggleCoreAsync(StartupEntryViewModel vm)
    {
        var enable = !vm.IsEnabled;
        if (vm.Blocker is { } blocker)
        {
            ShowMessage(blocker);
            return;
        }

        var details = new List<string>
        {
            $"Program: {vm.Path}",
            $"Publisher: {vm.Publisher}",
            $"Mechanism: {vm.Mechanism}",
            "The startup entry itself is kept, so this can be undone at any time.",
        };
        if (vm.NeedsAdmin) details.Add("This entry applies to all users, so Windows will ask for administrator approval.");
        if (vm.IsWindows && !enable) details.Add("This is a Windows component. Disabling it is not recommended unless you know what it does.");

        var ok = _dialogs.Confirm(new ConfirmRequest(
            enable ? $"Enable {vm.Name} at startup?" : $"Stop {vm.Name} starting automatically?",
            enable ? "It will start automatically the next time you sign in." : "It will no longer start when you sign in. You can still open it yourself whenever you need it.",
            details,
            enable ? "Enable" : "Disable"));
        if (!ok) return;

        vm.IsWorking = true;
        try
        {
            var result = await _startup.SetEnabledAsync(vm.Entry, enable, _settings.Current.DryRunMode, CancellationToken.None);
            ShowResult(result);
            if (result.IsSuccess) vm.Entry = vm.Entry with { IsEnabled = enable };
            RecentChanges = _recovery.GetActiveReversible().Count(r => r.OptimisationId == StartupManagerService.HandlerId);
            ApplyFilter();
        }
        finally
        {
            vm.IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task RestoreRecentAsync()
    {
        var records = _recovery.GetActiveReversible().Where(r => r.OptimisationId == StartupManagerService.HandlerId).ToList();
        if (records.Count == 0) return;
        if (!_dialogs.Confirm(new ConfirmRequest("Restore previous startup settings?",
                $"TICKSHIFT will undo {records.Count} startup change(s) it made. Entries changed since by something else are left alone.", ConfirmText: "Restore")))
            return;

        var report = await _recovery.RollbackManyAsync(records, CancellationToken.None);
        ShowMessage($"Restored {report.Restored} startup setting(s)" + (report.Conflicts > 0 ? $"; {report.Conflicts} left unchanged because they were changed elsewhere" : string.Empty) +
                    (report.Failed > 0 ? $"; {report.Failed} failed — see Recovery Centre" : string.Empty) + ".", report.Failed > 0);
        await LoadAsync();
    }
}
