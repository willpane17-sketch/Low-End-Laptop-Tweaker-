using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tickshift.App.Infrastructure;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Diagnostics;
using Tickshift.Diagnostics.Hardware;
using Tickshift.Diagnostics.Monitoring;
using Tickshift.Diagnostics.Thermal;
using Tickshift.Trading;

namespace Tickshift.App.ViewModels;

/// <summary>Wraps a recommendation with the command that performs its suggested action.</summary>
public sealed class RecommendationItem(Recommendation model, IRelayCommand<Recommendation> action)
{
    public Recommendation Model { get; } = model;
    public IRelayCommand<Recommendation> Action { get; } = action;
    public bool HasAction => Model.ActionKind != RecommendationActionKind.None && Model.ActionLabel is not null;
}

/// <summary>Performs a recommendation's suggested action: navigate, open an allowlisted settings page, or open a browser page.</summary>
public sealed class RecommendationActions(NavigationService navigation, ILinkLauncher links, INotificationService notifications)
{
    public void Execute(Recommendation? r)
    {
        if (r is null) return;
        switch (r.ActionKind)
        {
            case RecommendationActionKind.Navigate when r.ActionTarget is not null:
                navigation.Navigate(r.ActionTarget);
                break;
            case RecommendationActionKind.RunOptimisation:
                navigation.PendingOptimisationFocus = r.ActionTarget;
                navigation.Navigate(PageKeys.Optimise);
                break;
            case RecommendationActionKind.OpenSettings when r.ActionTarget is not null:
                Report(links.Open(r.ActionTarget));
                break;
            case RecommendationActionKind.OpenBrowserPage when r.ActionTarget?.Split(':') is [var browser, var page]:
                Report(links.OpenInBrowser(Enum.Parse<BrowserKind>(browser), page));
                break;
        }
    }

    private void Report(OperationResult result)
    {
        if (!result.IsSuccess)
            notifications.Notify(new NotificationMessage(Brand.ProductName, result.Message, NotificationKind.Warning));
    }
}

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly IMetricsSource _metrics;
    private readonly SystemSummaryService _summary;
    private readonly ISystemInfoProvider _info;
    private readonly IProcessProvider _processes;
    private readonly DiagnosticEngine _diagnostics;
    private readonly ISettingsStore _settings;
    private readonly TradingModeService _trading;
    private readonly RecommendationActions _actions;
    private CancellationTokenSource? _scanCts;
    private DateTimeOffset _lastChartUpdate;
    private DateTimeOffset _lastProcessCount;
    private bool _subscribed;
    private CpuModel? _cpuModel;

    public DashboardViewModel(IMetricsSource metrics, SystemSummaryService summary, ISystemInfoProvider info, IProcessProvider processes,
        DiagnosticEngine diagnostics, ISettingsStore settings, TradingModeService trading, RecommendationActions actions)
    {
        _metrics = metrics;
        _summary = summary;
        _info = info;
        _processes = processes;
        _diagnostics = diagnostics;
        _settings = settings;
        _trading = trading;
        _actions = actions;
        RunActionCommand = new RelayCommand<Recommendation>(actions.Execute);
        if (diagnostics.LastReport is { } last) ApplyReport(last);
    }

    public override string Title => "Dashboard";

    public IRelayCommand<Recommendation> RunActionCommand { get; }

    // Header
    [ObservableProperty] private string _greeting = "Welcome";
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private SystemCondition _condition = SystemCondition.Unknown;
    [ObservableProperty] private string _conditionText = "Gathering measurements…";
    [ObservableProperty] private string _modeText = "Standard mode";

    // Device
    [ObservableProperty] private string _cpuName = "Detecting…";
    [ObservableProperty] private string _cpuDescription = string.Empty;
    [ObservableProperty] private string _memoryDescription = "—";
    [ObservableProperty] private string _storageDescription = "—";
    [ObservableProperty] private string _windowsDescription = "—";
    [ObservableProperty] private string _powerDescription = "—";
    [ObservableProperty] private string _lastOptimisedText = "Never";

    // Live metrics
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _cpuCaption = string.Empty;
    [ObservableProperty] private string _memoryText = "—";
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private string _memoryCaption = string.Empty;
    [ObservableProperty] private string _diskText = "—";
    [ObservableProperty] private double _diskPercent = -1;
    [ObservableProperty] private string _diskCaption = string.Empty;
    [ObservableProperty] private string _temperatureText = "—";
    [ObservableProperty] private string _temperatureUnit = string.Empty;
    [ObservableProperty] private string _temperatureCaption = string.Empty;
    [ObservableProperty] private ThermalState _thermalState = ThermalState.Unknown;
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string _processCountText = "—";
    [ObservableProperty] private string _storageFreeText = "—";
    [ObservableProperty] private string _storageFreeCaption = string.Empty;
    [ObservableProperty] private double _storageUsedPercent = -1;

    // Chart
    [ObservableProperty] private IReadOnlyList<MetricSample>? _chartSamples;
    [ObservableProperty] private HistoryWindow _selectedWindow = HistoryWindow.FiveMinutes;
    [ObservableProperty] private bool _showCpu = true;
    [ObservableProperty] private bool _showMemory = true;
    [ObservableProperty] private bool _showDisk;
    [ObservableProperty] private string _windowStats = string.Empty;

    // Analysis
    [ObservableProperty] private bool _isAnalysing;
    [ObservableProperty] private double _analysisProgress;
    [ObservableProperty] private string _analysisStage = string.Empty;
    [ObservableProperty] private DiagnosticReport? _report;
    [ObservableProperty] private string _limitationText = string.Empty;
    [ObservableProperty] private string _reportTime = string.Empty;

    public ObservableCollection<RecommendationItem> Recommendations { get; } = [];
    public ObservableCollection<HardwareInsight> Insights { get; } = [];
    public ObservableCollection<string> NotMeasured { get; } = [];

    public override async Task OnNavigatedToAsync()
    {
        UpdateGreeting();
        if (!_subscribed)
        {
            _metrics.SampleAvailable += OnSample;
            _subscribed = true;
        }

        if (_metrics.Latest is { } latest) Apply(latest);
        RefreshChart(force: true);

        try
        {
            var summary = await _summary.GetAsync().ConfigureAwait(true);
            _cpuModel = summary.CpuModel;
            CpuName = summary.Cpu.Name;
            CpuDescription = summary.CpuModel.Describe() + (summary.Cpu.PhysicalCores > 0 ? $" · {summary.Cpu.PhysicalCores} cores / {summary.Cpu.LogicalProcessors} threads" : string.Empty);
            MemoryDescription = $"{Format.Bytes(summary.Memory.TotalPhysicalBytes)} installed";
            StorageDescription = summary.StorageDescription;
            WindowsDescription = summary.Os.FriendlyName;
            PowerDescription = summary.Power.Describe() + (summary.Power.ActivePlan is { } p ? $" · {p.Name} plan" : string.Empty);
            if (summary.SystemDrive is { } d)
            {
                StorageFreeText = Format.Bytes(d.FreeBytes);
                StorageFreeCaption = $"free on {d.RootPath.TrimEnd('\\')} ({d.FreePercent:0}%)";
                StorageUsedPercent = 100 - d.FreePercent;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowMessage("Some system details could not be read. " + Core.Errors.ErrorClassifier.FriendlyMessage(Core.Errors.ErrorClassifier.Classify(ex)), true);
        }

        UpdateProcessCount(force: true);
    }

    public override void OnNavigatedFrom()
    {
        if (_subscribed)
        {
            _metrics.SampleAvailable -= OnSample;
            _subscribed = false;
        }
    }

    private void UpdateGreeting()
    {
        var hour = DateTime.Now.Hour;
        var part = hour switch { < 5 => "Good evening", < 12 => "Good morning", < 18 => "Good afternoon", _ => "Good evening" };
        var state = Condition switch
        {
            SystemCondition.Strained => "your laptop is working hard right now.",
            SystemCondition.Critical => "your laptop is under heavy pressure.",
            _ => "your laptop is ready.",
        };
        Greeting = $"{part}, {state}";
        Subtitle = DateTime.Now.ToString("dddd d MMMM") + " · " + Brand.Tagline;
        LastOptimisedText = Format.Relative(_settings.Current.LastOptimisedAt, DateTimeOffset.Now);
        ModeText = _trading.IsActive ? "Trading Mode active" : _settings.Current.DryRunMode ? "Dry-run mode (no changes are applied)" : "Standard mode";
    }

    private void OnSample(object? sender, MetricSample sample) => Ui.Post(() =>
    {
        Apply(sample);
        RefreshChart(force: false);
        UpdateProcessCount(force: false);
    });

    private void Apply(MetricSample s)
    {
        CpuPercent = s.CpuPercent;
        CpuText = $"{s.CpuPercent:0}";
        CpuCaption = s.CpuPercent >= 85 ? "Very busy" : s.CpuPercent >= 60 ? "Busy" : "Comfortable";

        MemoryPercent = s.MemoryPercent;
        MemoryText = $"{s.MemoryPercent:0}";
        MemoryCaption = $"{Format.Bytes(s.AvailableMemoryBytes)} available";

        if (s.DiskActivePercent is { } disk)
        {
            DiskPercent = disk;
            DiskText = $"{disk:0}";
            DiskCaption = "of the time the disk was busy";
        }
        else
        {
            DiskPercent = -1;
            DiskText = "—";
            DiskCaption = "Counter unavailable on this device";
        }

        var thermal = _metrics.LatestThermal;
        if (thermal?.PrimaryCelsius is { } t && _cpuModel is not null)
        {
            TemperatureText = $"{t:0}";
            TemperatureUnit = "°C";
            var thresholds = ThermalAnalyzer.ThresholdsFor(_cpuModel, thermal.Primary?.Kind);
            ThermalState = ThermalAnalyzer.Classify(t, thresholds);
            TemperatureCaption = thermal.Primary?.Kind == ThermalSourceKind.AcpiThermalZone ? "Firmware thermal zone" : "CPU sensor";
        }
        else
        {
            TemperatureText = "Sensor unavailable";
            TemperatureUnit = string.Empty;
            ThermalState = ThermalState.Unknown;
            TemperatureCaption = "Not reported by this device — never estimated";
        }

        UptimeText = Format.Duration(_info.GetUptime());

        var recent = _metrics.History.GetSince(DateTimeOffset.Now.AddMinutes(-1));
        var condition = SystemConditionEvaluator.Evaluate(recent, null, null, ThermalState);
        Condition = condition.Condition;
        ConditionText = condition.Summary;
    }

    private void UpdateProcessCount(bool force)
    {
        if (!force && DateTimeOffset.Now - _lastProcessCount < TimeSpan.FromSeconds(10)) return;
        _lastProcessCount = DateTimeOffset.Now;
        Task.Run(() =>
        {
            try
            {
                var count = _processes.GetProcessCount();
                Ui.Post(() => ProcessCountText = count.ToString("N0"));
            }
            catch (Exception)
            {
                Ui.Post(() => ProcessCountText = "—");
            }
        });
    }

    private void RefreshChart(bool force)
    {
        // Redraw at most every 2 seconds, and only while the dashboard is visible.
        if (!force && DateTimeOffset.Now - _lastChartUpdate < TimeSpan.FromSeconds(2)) return;
        _lastChartUpdate = DateTimeOffset.Now;
        var samples = _metrics.History.GetWindow(SelectedWindow, DateTimeOffset.Now);
        ChartSamples = PerformanceHistory.Downsample(samples, 240);
        WindowStats = samples.Count == 0 ? string.Empty
            : $"Processor avg {samples.Average(s => s.CpuPercent):0}% · peak {samples.Max(s => s.CpuPercent):0}%   ·   Memory avg {samples.Average(s => s.MemoryPercent):0}%";
    }

    partial void OnSelectedWindowChanged(HistoryWindow value) => RefreshChart(force: true);

    [RelayCommand]
    private async Task AnalyseAsync()
    {
        if (IsAnalysing) return;
        IsAnalysing = true;
        StatusMessage = null;
        _scanCts = new CancellationTokenSource();
        var progress = new Progress<DiagnosticProgress>(p =>
        {
            AnalysisProgress = p.Percent;
            AnalysisStage = p.Stage;
        });

        try
        {
            var report = await _diagnostics.RunAsync(DiagnosticOptions.Default, progress, _scanCts.Token);
            ApplyReport(report);
            UpdateGreeting();
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Analysis cancelled. Nothing was changed.");
        }
        catch (Exception ex)
        {
            ShowMessage("The analysis could not complete: " + Core.Errors.ErrorClassifier.FriendlyMessage(Core.Errors.ErrorClassifier.Classify(ex)), true);
        }
        finally
        {
            IsAnalysing = false;
            _scanCts.Dispose();
            _scanCts = null;
        }
    }

    [RelayCommand]
    private void CancelAnalysis() => _scanCts?.Cancel();

    private void ApplyReport(DiagnosticReport report)
    {
        Report = report;
        ReportTime = $"Analysed {report.CreatedAt.LocalDateTime:t} · {report.Observation.SampleCount} measurements over {report.Observation.Duration.TotalSeconds:0} s";
        LimitationText = report.Limitation.Explanation;
        Recommendations.Clear();
        foreach (var r in report.Recommendations) Recommendations.Add(new RecommendationItem(r, RunActionCommand));
        Insights.Clear();
        foreach (var i in report.Hardware.Insights) Insights.Add(i);
        NotMeasured.Clear();
        foreach (var l in report.Limitations) NotMeasured.Add(l);
    }
}
