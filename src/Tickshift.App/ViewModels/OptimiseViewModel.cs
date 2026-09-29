using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tickshift.App.Infrastructure;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Diagnostics;
using Tickshift.Diagnostics.Benchmarks;
using Tickshift.Optimization;
using Tickshift.Optimization.Recovery;
using Tickshift.Trading;

namespace Tickshift.App.ViewModels;

public sealed partial class PlanItemViewModel(OptimisationPlanItem item) : ObservableObject
{
    [ObservableProperty] private bool _isSelected = item.SelectedByDefault;

    public OptimisationPlanItem Item { get; } = item;
    public IOptimisation Optimisation => Item.Optimisation;
    public string Title => Optimisation.Title;
    public string Description => Optimisation.Description;
    public string ProposedChange => Item.Assessment.ProposedChange;
    public string? EstimatedEffect => Item.Assessment.EstimatedEffect;
    public string Reason => Item.BlockedReason ?? Item.Assessment.Reason;
    public bool CanSelect => Item.CanSelect;
    public string? UncertaintyNote => Optimisation.UncertaintyNote;
    public bool RequiresExplicitApproval => Optimisation.RequiresExplicitApproval;
    public RiskLevel Risk => Optimisation.Risk;
    public Reversibility Reversibility => Optimisation.Reversibility;
    public PrivilegeLevel Privilege => Optimisation.RequiredPrivilege;
    public string Preconditions => string.Join(" · ", Optimisation.Preconditions);
}

public sealed record PipelineStep(int Number, string Name);

public sealed partial class OptimiseViewModel : PageViewModel
{
    private readonly OptimisationEngine _engine;
    private readonly IRestorePointService _restorePoints;
    private readonly RecoveryService _recovery;
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly DiagnosticEngine _diagnostics;
    private readonly NavigationService _navigation;
    private readonly TradingModeService _trading;
    private CancellationTokenSource? _cts;

    public OptimiseViewModel(OptimisationEngine engine, IRestorePointService restorePoints, RecoveryService recovery, ISettingsStore settings,
        IDialogService dialogs, INotificationService notifications, DiagnosticEngine diagnostics, NavigationService navigation, TradingModeService trading)
    {
        _engine = engine;
        _restorePoints = restorePoints;
        _recovery = recovery;
        _settings = settings;
        _dialogs = dialogs;
        _notifications = notifications;
        _diagnostics = diagnostics;
        _navigation = navigation;
        _trading = trading;
        _preset = settings.Current.DefaultPreset;
        _dryRun = settings.Current.DryRunMode;
        _createRestorePoint = settings.Current.OfferRestorePoint;
    }

    public override string Title => "One-Click Optimise";

    public IReadOnlyList<PipelineStep> Steps { get; } =
    [
        new(1, "Analyse"), new(2, "Identify"), new(3, "Snapshot"), new(4, "Review"),
        new(5, "Apply"), new(6, "Verify"), new(7, "Record"), new(8, "Measure"),
    ];

    [ObservableProperty] private OptimisationPreset _preset;
    [ObservableProperty] private bool _dryRun;
    [ObservableProperty] private bool _createRestorePoint;
    [ObservableProperty] private string _restorePointStatus = string.Empty;
    [ObservableProperty] private int _currentStep;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private OptimisationRunRecord? _lastRun;
    [ObservableProperty] private BenchmarkComparison? _comparison;
    [ObservableProperty] private string _presetDescription = string.Empty;
    [ObservableProperty] private bool _tradingActive;

    private OptimisationPlan? _plan;

    public ObservableCollection<PlanItemViewModel> PlanItems { get; } = [];
    public ObservableCollection<RunItemResult> Results { get; } = [];
    public ObservableCollection<BenchmarkDelta> Deltas { get; } = [];
    public ObservableCollection<string> Caveats { get; } = [];
    public ObservableCollection<Recommendation> ReviewItems { get; } = [];

    public int SelectedCount => PlanItems.Count(p => p.IsSelected && p.CanSelect);
    public bool CanUndoLastRun => LastRun is { DryRun: false } run && _recovery.GetActiveReversible().Any(r => r.RunId == run.Id);

    public override Task OnNavigatedToAsync()
    {
        var status = _restorePoints.GetStatus();
        RestorePointStatus = status.Explanation;
        TradingActive = _trading.IsActive;
        DryRun = DryRun || _settings.Current.DryRunMode;
        OnPresetChanged(Preset);

        ReviewItems.Clear();
        if (_diagnostics.LastReport is { } report)
        {
            foreach (var r in report.Recommendations.Where(r => r.ActionKind is RecommendationActionKind.Navigate or RecommendationActionKind.OpenBrowserPage).Take(4))
                ReviewItems.Add(r);
        }

        return Task.CompletedTask;
    }

    partial void OnPresetChanged(OptimisationPreset value)
    {
        PresetDescription = value switch
        {
            OptimisationPreset.Safe => "Conservative, compatibility-first changes: clears old temporary files and error reports and fixes an unsuitable power plan. Nothing that affects how apps look or behave.",
            OptimisationPreset.Balanced => "Safe mode plus reversible interface tweaks such as reducing Windows animations. Each is explained and can be undone.",
            OptimisationPreset.Trading => "Responsiveness-focused choices that protect your trading apps, browser sessions and connectivity. Nothing is closed and no security setting is touched.",
            _ => string.Empty,
        };
        HasPlan = false;
        PlanItems.Clear();
    }

    [RelayCommand]
    private void SelectPreset(string name) => Preset = Enum.Parse<OptimisationPreset>(name);

    [RelayCommand]
    private async Task AnalyseAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        StatusMessage = null;
        Results.Clear();
        Deltas.Clear();
        Caveats.Clear();
        LastRun = null;
        _cts = new CancellationTokenSource();
        try
        {
            CurrentStep = 1;
            ProgressText = "Analysing this laptop…";
            var progress = new Progress<ExecutionProgress>(p =>
            {
                CurrentStep = p.Step;
                Progress = p.Percent;
                ProgressText = string.IsNullOrEmpty(p.Detail) ? p.StepName : $"{p.StepName}: {p.Detail}";
            });
            _plan = await _engine.AnalyseAsync(Preset, progress, _cts.Token);
            PlanItems.Clear();
            var focus = _navigation.PendingOptimisationFocus;
            _navigation.PendingOptimisationFocus = null;
            foreach (var item in _plan.Items.OrderByDescending(i => i.CanSelect).ThenByDescending(i => i.SelectedByDefault))
            {
                var vm = new PlanItemViewModel(item);
                if (focus is not null && item.Id == focus && item.CanSelect) vm.IsSelected = true;
                vm.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SelectedCount));
                PlanItems.Add(vm);
            }

            HasPlan = true;
            CurrentStep = 4;
            ProgressText = PlanItems.Any(p => p.CanSelect)
                ? "Review the proposed changes below. Nothing has been changed yet."
                : "No supported optimisations apply to this laptop right now. That is a good sign.";
            OnPropertyChanged(nameof(SelectedCount));
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Analysis cancelled. Nothing was changed.");
            CurrentStep = 0;
        }
        catch (Exception ex)
        {
            ShowMessage("Analysis failed: " + Core.Errors.ErrorClassifier.FriendlyMessage(Core.Errors.ErrorClassifier.Classify(ex)), true);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (_plan is null || IsRunning) return;
        var selected = PlanItems.Where(p => p.IsSelected && p.CanSelect).ToList();
        if (selected.Count == 0)
        {
            ShowMessage("Select at least one change to apply.");
            return;
        }

        var details = selected.Select(s => $"{s.Title} — {s.ProposedChange} ({(s.Reversibility == Reversibility.NotReversible ? "cannot be undone" : "can be undone")})").ToList();
        var irreversible = selected.Any(s => s.Reversibility == Reversibility.NotReversible);
        var confirmed = _dialogs.Confirm(new ConfirmRequest(
            DryRun ? "Simulate these changes?" : "Apply these changes?",
            DryRun
                ? "Dry run is on: TICKSHIFT will show exactly what it would do, without changing anything."
                : "TICKSHIFT will record the current settings first, apply each change, then verify it. " +
                  (irreversible ? "Deleted files cannot be restored; all other changes can be undone from the Recovery Centre." : "Every change can be undone from the Recovery Centre."),
            details,
            DryRun ? "Run simulation" : $"Apply {selected.Count} change{(selected.Count == 1 ? string.Empty : "s")}"));
        if (!confirmed) return;

        IsRunning = true;
        StatusMessage = null;
        Results.Clear();
        _cts = new CancellationTokenSource();
        var progress = new Progress<ExecutionProgress>(p =>
        {
            CurrentStep = p.Step;
            Progress = p.Percent;
            ProgressText = string.IsNullOrEmpty(p.Detail) ? p.StepName : $"{p.StepName}: {p.Detail}";
        });

        try
        {
            var options = new ExecutionOptions(DryRun, CreateRestorePoint && !DryRun, !DryRun, TimeSpan.FromSeconds(10));
            var run = await _engine.ExecuteAsync(_plan, selected.Select(s => s.Item.Id).ToList(), options, progress, _cts.Token);
            LastRun = run;
            foreach (var r in run.Items.Where(i => selected.Any(s => s.Item.Id == i.OptimisationId))) Results.Add(r);
            if (run is { Before: { } before, After: { } after })
            {
                Comparison = BenchmarkService.Compare(before, after);
                Deltas.Clear();
                foreach (var d in Comparison.Deltas.Where(d => d.Before is not null || d.After is not null)) Deltas.Add(d);
                Caveats.Clear();
                foreach (var c in Comparison.Caveats) Caveats.Add(c);
            }

            HasPlan = false;
            var summary = run.DryRun
                ? $"Dry run complete: {run.Items.Count(i => i.Status == RunItemStatus.DryRun)} change(s) would be applied. Nothing was changed."
                : $"{run.VerifiedCount} change(s) applied and verified" + (run.FailedCount > 0 ? $", {run.FailedCount} could not be verified or failed" : string.Empty) +
                  (run.Cancelled ? " (cancelled part-way; completed changes are recorded and can be undone)" : string.Empty) + ".";
            ShowMessage(summary, run.FailedCount > 0);
            if (run.RestorePointMessage is { } rp) ShowMessage(summary + " Restore point: " + rp, run.FailedCount > 0);
            _notifications.Notify(new NotificationMessage("Optimisation", summary, run.FailedCount > 0 ? NotificationKind.Warning : NotificationKind.Success));
            OnPropertyChanged(nameof(CanUndoLastRun));
        }
        catch (Exception ex)
        {
            ShowMessage("Optimisation stopped: " + Core.Errors.ErrorClassifier.FriendlyMessage(Core.Errors.ErrorClassifier.Classify(ex)), true);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task UndoLastRunAsync()
    {
        if (LastRun is null) return;
        if (!_dialogs.Confirm(new ConfirmRequest("Undo this optimisation?", "TICKSHIFT will restore the settings recorded before this optimisation. Settings changed since by something else are left alone.", ConfirmText: "Undo changes")))
            return;
        var report = await _recovery.UndoRunAsync(LastRun.Id, CancellationToken.None);
        ShowMessage($"Restored {report.Restored} setting(s)" + (report.Conflicts > 0 ? $"; {report.Conflicts} left as is because they were changed elsewhere" : string.Empty) +
                    (report.Failed > 0 ? $"; {report.Failed} could not be restored" : string.Empty) + ".", report.Failed > 0);
        OnPropertyChanged(nameof(CanUndoLastRun));
    }

    [RelayCommand]
    private void OpenReview(Recommendation? r)
    {
        if (r?.ActionTarget is { } target && r.ActionKind == RecommendationActionKind.Navigate) _navigation.Navigate(target);
        else if (r?.ActionKind == RecommendationActionKind.OpenBrowserPage) _navigation.Navigate(PageKeys.Browser);
    }
}
