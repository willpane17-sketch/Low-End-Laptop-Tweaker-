using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tickshift.App.Infrastructure;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;
using Tickshift.Diagnostics;
using Tickshift.Diagnostics.Hardware;
using Tickshift.Optimization.Reports;
using Tickshift.Persistence;
using Tickshift.Trading;

namespace Tickshift.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsStore _settings;
    private readonly IAutostartManager _autostart;
    private readonly ThemeService _themeService;
    private readonly IDialogService _dialogs;
    private readonly DiagnosticReportExporter _exporter;
    private readonly DiagnosticEngine _diagnostics;
    private readonly NavigationService _navigation;
    private readonly IChangeJournal _journal;
    private readonly RecordStore<Tickshift.Optimization.OptimisationRunRecord> _runs;
    private readonly IElevationState _elevation;
    private bool _loading;

    public SettingsViewModel(ISettingsStore settings, IAutostartManager autostart, ThemeService theme, IDialogService dialogs, DiagnosticReportExporter exporter,
        DiagnosticEngine diagnostics, NavigationService navigation, IChangeJournal journal, RecordStore<Tickshift.Optimization.OptimisationRunRecord> runs, IElevationState elevation)
    {
        _settings = settings;
        _autostart = autostart;
        _themeService = theme;
        _dialogs = dialogs;
        _exporter = exporter;
        _diagnostics = diagnostics;
        _navigation = navigation;
        _journal = journal;
        _runs = runs;
        _elevation = elevation;
    }

    public override string Title => "Settings";

    [ObservableProperty] private ThemePreference _theme;
    [ObservableProperty] private bool _compactMode;
    [ObservableProperty] private bool _reducedMotion;
    [ObservableProperty] private double _uiScale;
    [ObservableProperty] private bool _launchWithWindows;
    [ObservableProperty] private bool _startMinimised;
    [ObservableProperty] private bool _minimiseToTray;
    [ObservableProperty] private double _refreshInterval;
    [ObservableProperty] private OptimisationPreset _defaultPreset;
    [ObservableProperty] private bool _dryRunMode;
    [ObservableProperty] private bool _offerRestorePoint;
    [ObservableProperty] private bool _notificationsEnabled;
    [ObservableProperty] private bool _notifyMemory;
    [ObservableProperty] private bool _notifyThermal;
    [ObservableProperty] private bool _notifyStorage;
    [ObservableProperty] private bool _notifyTrading;
    [ObservableProperty] private bool _notifyRecommendations;
    [ObservableProperty] private double _retentionDays;
    [ObservableProperty] private string _protectedSummary = string.Empty;

    public string VersionText => $"{Brand.ProductName} {Brand.Version}";
    public string PrivilegeText => _elevation.IsElevated
        ? "Running as administrator. TICKSHIFT does not need this; starting it normally is recommended."
        : "Running as a standard user. Administrator approval is requested only for specific actions.";
    public string DataFolder => new AppDataPaths().Root;

    public override Task OnNavigatedToAsync()
    {
        _loading = true;
        var s = _settings.Current;
        Theme = s.Theme;
        CompactMode = s.CompactMode;
        ReducedMotion = s.ReducedMotion;
        UiScale = s.UiScale;
        LaunchWithWindows = _autostart.IsEnabled();
        StartMinimised = s.StartMinimised;
        MinimiseToTray = s.MinimiseToTray;
        RefreshInterval = s.RefreshIntervalSeconds;
        DefaultPreset = s.DefaultPreset;
        DryRunMode = s.DryRunMode;
        OfferRestorePoint = s.OfferRestorePoint;
        NotificationsEnabled = s.Notifications.Enabled;
        NotifyMemory = s.Notifications.MemoryPressure;
        NotifyThermal = s.Notifications.Thermal;
        NotifyStorage = s.Notifications.Storage;
        NotifyTrading = s.Notifications.TradingMode;
        NotifyRecommendations = s.Notifications.Recommendations;
        RetentionDays = s.DataRetentionDays;
        ProtectedSummary = $"{s.ProtectedApplications.Count(a => a.Enabled)} protected application(s) · {s.ProtectedWebsites.Count} protected website(s)";
        _loading = false;
        return Task.CompletedTask;
    }

    protected override async void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(StatusMessage) or nameof(StatusIsError) or nameof(IsBusy) or nameof(ProtectedSummary)) return;

        if (e.PropertyName == nameof(LaunchWithWindows))
        {
            var result = _autostart.SetEnabled(LaunchWithWindows);
            ShowResult(result);
            if (!result.IsSuccess)
            {
                _loading = true;
                LaunchWithWindows = _autostart.IsEnabled();
                _loading = false;
            }
        }

        await _settings.UpdateAsync(s =>
        {
            s.Theme = Theme;
            s.CompactMode = CompactMode;
            s.ReducedMotion = ReducedMotion;
            s.UiScale = Math.Round(UiScale, 2);
            s.LaunchWithWindows = LaunchWithWindows;
            s.StartMinimised = StartMinimised;
            s.MinimiseToTray = MinimiseToTray;
            s.RefreshIntervalSeconds = (int)RefreshInterval;
            s.DefaultPreset = DefaultPreset;
            s.DryRunMode = DryRunMode;
            s.OfferRestorePoint = OfferRestorePoint;
            s.Notifications.Enabled = NotificationsEnabled;
            s.Notifications.MemoryPressure = NotifyMemory;
            s.Notifications.Thermal = NotifyThermal;
            s.Notifications.Storage = NotifyStorage;
            s.Notifications.TradingMode = NotifyTrading;
            s.Notifications.Recommendations = NotifyRecommendations;
            s.DataRetentionDays = (int)RetentionDays;
        });

        if (e.PropertyName is nameof(Theme) or nameof(CompactMode)) _themeService.Apply(_settings.Current);
    }

    [RelayCommand]
    private void ManageProtected() => _navigation.Navigate(PageKeys.Trading);

    [RelayCommand]
    private void ManageWebsites() => _navigation.Navigate(PageKeys.Browser);

    [RelayCommand]
    private void ManageAutomation() => _navigation.Navigate(PageKeys.Automation);

    [RelayCommand]
    private async Task ExportReportAsync()
    {
        var path = _dialogs.SaveFile("Export diagnostic report", $"TICKSHIFT-report-{DateTime.Now:yyyyMMdd-HHmm}.txt", "Text report (*.txt)|*.txt|JSON (*.json)|*.json");
        if (path is null) return;
        await _exporter.ExportAsync(path, _diagnostics.LastReport, CancellationToken.None);
        ShowMessage($"Report saved to {path}.");
    }

    [RelayCommand]
    private async Task ApplyRetentionAsync()
    {
        var days = (int)RetentionDays;
        await _journal.PruneAsync(days);
        await _runs.PruneAsync(DateTimeOffset.Now.AddDays(-days));
        ShowMessage($"History older than {days} days was removed. Records needed to undo active changes were kept.");
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (!_dialogs.Confirm(new ConfirmRequest("Reset application settings?",
                "All TICKSHIFT settings return to their defaults, including protected applications, websites and automation. Your recovery records and history are kept so changes can still be undone.",
                ConfirmText: "Reset settings", Destructive: true)))
            return;
        await _settings.ResetAsync();
        if (_autostart.IsEnabled()) _autostart.SetEnabled(false);
        _themeService.Apply(_settings.Current);
        await OnNavigatedToAsync();
        ShowMessage("Settings were reset to their defaults.");
    }
}

// ============================== Onboarding ==============================

public sealed partial class OnboardingViewModel : ObservableObject
{
    private readonly SystemSummaryService _summary;
    private readonly ISettingsStore _settings;
    private readonly IProcessProvider _processes;
    private readonly DiagnosticEngine _diagnostics;

    public OnboardingViewModel(SystemSummaryService summary, ISettingsStore settings, IProcessProvider processes, DiagnosticEngine diagnostics)
    {
        _summary = summary;
        _settings = settings;
        _processes = processes;
        _diagnostics = diagnostics;
    }

    public event EventHandler? Completed;

    public const int LastStep = 7;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepLabel), nameof(IsFirst), nameof(NextText))]
    private int _step;

    [ObservableProperty] private PrimaryUse _primaryUse = PrimaryUse.Trading;
    [ObservableProperty] private bool _detecting = true;
    [ObservableProperty] private string _cpuText = "Detecting…";
    [ObservableProperty] private string _memoryText = string.Empty;
    [ObservableProperty] private string _storageText = string.Empty;
    [ObservableProperty] private string _windowsText = string.Empty;
    [ObservableProperty] private string _powerText = string.Empty;
    [ObservableProperty] private bool _scanning;
    [ObservableProperty] private double _scanProgress;
    [ObservableProperty] private string _scanStage = string.Empty;
    [ObservableProperty] private string _scanSummary = string.Empty;

    public ObservableCollection<HardwareInsight> Insights { get; } = [];
    public ObservableCollection<ProtectedAppOption> Apps { get; } = [];
    public ObservableCollection<string> TopFindings { get; } = [];

    public string StepLabel => $"Step {Step + 1} of {LastStep + 1}";
    public bool IsFirst => Step == 0;
    public string NextText => Step switch { LastStep => "Open my dashboard", 6 => Scanning ? "Scanning…" : "Continue", _ => "Continue" };
    public string ProductName => Brand.ProductName;
    public string Tagline => Brand.Tagline;

    public async Task StartAsync()
    {
        try
        {
            var s = await _summary.GetAsync(refresh: true);
            CpuText = $"{s.Cpu.Name}\n{s.CpuModel.Describe()}";
            MemoryText = $"{Format.Bytes(s.Memory.TotalPhysicalBytes)} memory";
            StorageText = s.StorageDescription + (s.SystemDrive is { } d ? $" · {Format.Bytes(d.FreeBytes)} free" : string.Empty);
            WindowsText = s.Os.FriendlyName;
            PowerText = s.Power.Describe();
            Insights.Clear();
            foreach (var i in s.Profile.Insights) Insights.Add(i);

            var running = await Task.Run(() => _processes.GetProcesses().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
            var detected = await Task.Run(() => KnownTradingApplications.Detect(running));
            Apps.Clear();
            foreach (var d2 in detected)
            {
                var app = KnownTradingApplications.ToProtected(d2.Definition);
                Apps.Add(new ProtectedAppOption(app, d2.Installed && d2.Definition.Kind != ProtectedAppKind.Messaging, d2.Definition.Hint)
                {
                    Status = d2.Running ? "Running now" : d2.Installed ? "Installed" : "Not detected",
                    IsRunning = d2.Running,
                });
            }
        }
        catch (Exception)
        {
            CpuText = "Some hardware details could not be read. You can continue safely.";
        }
        finally
        {
            Detecting = false;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 0 && !Scanning) Step--;
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (Scanning) return;
        if (Step == LastStep)
        {
            await FinishAsync();
            return;
        }

        Step++;
        if (Step == 6) await RunScanAsync();
        OnPropertyChanged(nameof(NextText));
    }

    [RelayCommand]
    private void SelectUse(string name) => PrimaryUse = Enum.Parse<PrimaryUse>(name);

    [RelayCommand]
    private async Task SkipAsync() => await FinishAsync();

    private async Task RunScanAsync()
    {
        Scanning = true;
        OnPropertyChanged(nameof(NextText));
        try
        {
            var report = await _diagnostics.RunAsync(DiagnosticOptions.Quick, new Progress<DiagnosticProgress>(p =>
            {
                ScanProgress = p.Percent;
                ScanStage = p.Stage;
            }), CancellationToken.None);
            ScanSummary = $"{report.Condition.Summary} {report.Limitation.Explanation}";
            TopFindings.Clear();
            foreach (var r in report.Recommendations.Take(3)) TopFindings.Add(r.Issue);
            if (TopFindings.Count == 0) TopFindings.Add("No significant issues were found.");
        }
        catch (Exception)
        {
            ScanSummary = "The initial scan could not complete. You can run it any time from the dashboard.";
        }
        finally
        {
            Scanning = false;
            OnPropertyChanged(nameof(NextText));
        }
    }

    private async Task FinishAsync()
    {
        var chosen = Apps.Where(a => a.IsProtected).Select(a => a.App).ToList();
        await _settings.UpdateAsync(s =>
        {
            s.OnboardingCompleted = true;
            s.PrimaryUse = PrimaryUse;
            s.DefaultPreset = PrimaryUse == PrimaryUse.Trading ? OptimisationPreset.Trading : OptimisationPreset.Safe;
            if (chosen.Count > 0) s.ProtectedApplications = chosen;
            if (PrimaryUse == PrimaryUse.Trading && s.ProtectedWebsites.Count == 0)
                s.ProtectedWebsites = [new ProtectedWebsite("tradingview.com", "TradingView")];
        });
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
