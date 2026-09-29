using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tickshift.App.Infrastructure;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Diagnostics;
using Tickshift.Diagnostics.Benchmarks;
using Tickshift.Diagnostics.Monitoring;
using Tickshift.Diagnostics.Thermal;
using Tickshift.Persistence;
using Tickshift.Security;

namespace Tickshift.App.ViewModels;

public sealed partial class ThermalViewModel : PageViewModel
{
    private readonly IMetricsSource _metrics;
    private readonly SystemSummaryService _summary;
    private readonly ISystemInfoProvider _info;
    private readonly IProcessProvider _processes;
    private bool _subscribed;
    private DateTimeOffset _lastAssess;

    public ThermalViewModel(IMetricsSource metrics, SystemSummaryService summary, ISystemInfoProvider info, IProcessProvider processes)
    {
        _metrics = metrics;
        _summary = summary;
        _info = info;
        _processes = processes;
    }

    public override string Title => "Thermal Monitor";

    [ObservableProperty] private ThermalState _state = ThermalState.Unknown;
    [ObservableProperty] private string _temperatureText = "—";
    [ObservableProperty] private string _sourceText = string.Empty;
    [ObservableProperty] private string _headline = "Reading sensors…";
    [ObservableProperty] private string _thresholdText = string.Empty;
    [ObservableProperty] private string _trendText = "—";
    [ObservableProperty] private string _passiveLimitText = "Not reported";
    [ObservableProperty] private string _performanceText = "Not reported";
    [ObservableProperty] private string _powerText = "—";
    [ObservableProperty] private string _fanText = "Fan speed is not exposed to Windows on most laptops. TICKSHIFT never controls fans.";
    [ObservableProperty] private bool _sensorAvailable;
    [ObservableProperty] private string _unavailableExplanation = string.Empty;
    [ObservableProperty] private IReadOnlyList<MetricSample>? _chartSamples;
    [ObservableProperty] private HistoryWindow _selectedWindow = HistoryWindow.ThirtyMinutes;

    public ObservableCollection<ThermalInsight> Insights { get; } = [];
    public ObservableCollection<TemperatureReading> Readings { get; } = [];

    public IReadOnlyList<string> HardwareTips { get; } =
    [
        "Use the laptop on a hard, flat surface — beds and sofas block the air intakes underneath.",
        "Keep the side and rear vents clear, and leave a few centimetres of space behind the laptop.",
        "A simple laptop stand raises the base and improves airflow.",
        "Dust in the fan and heatsink builds up over years. Consider professional fan cleaning if the laptop is several years old.",
        "Avoid direct sunlight and warm rooms during long trading sessions.",
    ];

    public override async Task OnNavigatedToAsync()
    {
        if (!_subscribed)
        {
            _metrics.SampleAvailable += OnSample;
            _subscribed = true;
        }

        await _metrics.RefreshThermalAsync(CancellationToken.None);
        await AssessAsync();
    }

    public override void OnNavigatedFrom()
    {
        if (!_subscribed) return;
        _metrics.SampleAvailable -= OnSample;
        _subscribed = false;
    }

    partial void OnSelectedWindowChanged(HistoryWindow value) =>
        ChartSamples = PerformanceHistory.Downsample(_metrics.History.GetWindow(value, DateTimeOffset.Now), 240);

    private void OnSample(object? sender, MetricSample e) => Ui.Post(() =>
    {
        if (DateTimeOffset.Now - _lastAssess >= TimeSpan.FromSeconds(10)) _ = AssessAsync();
    });

    private async Task AssessAsync()
    {
        try
        {
            await AssessCoreAsync();
        }
        catch (Exception ex)
        {
            ShowMessage("Thermal information could not be refreshed: " + Core.Errors.ErrorClassifier.FriendlyMessage(Core.Errors.ErrorClassifier.Classify(ex)), true);
        }
    }

    private async Task AssessCoreAsync()
    {
        _lastAssess = DateTimeOffset.Now;
        var thermal = _metrics.LatestThermal ?? await _metrics.RefreshThermalAsync(CancellationToken.None);
        var summary = await _summary.GetAsync();
        var power = await _info.GetPowerAsync();
        var recent = _metrics.History.GetSince(DateTimeOffset.Now.AddMinutes(-10));
        var processes = await Task.Run(() => _processes.GetProcesses());
        var assessment = ThermalAnalyzer.Assess(thermal, summary.CpuModel, recent, power, processes);

        SensorAvailable = thermal.PrimaryCelsius is not null;
        State = assessment.State;
        TemperatureText = SensorAvailable ? $"{assessment.Celsius:0} °C" : "Sensor unavailable";
        SourceText = thermal.Primary is { } p ? $"{p.SensorName} · {thermal.ProviderName}" : thermal.ProviderName;
        UnavailableExplanation = SensorAvailable ? string.Empty : thermal.Explanation;
        Headline = assessment.Headline;
        ThresholdText = $"Elevated from {assessment.Thresholds.Elevated:0} °C · High from {assessment.Thresholds.High:0} °C · Critical from {assessment.Thresholds.Critical:0} °C. {assessment.Thresholds.Basis}";
        TrendText = assessment.Trend switch
        {
            ThermalTrend.Rising => $"Rising ({assessment.TrendPerMinute:+0.0} °C/min)",
            ThermalTrend.Falling => $"Falling ({assessment.TrendPerMinute:0.0} °C/min)",
            ThermalTrend.Stable => "Stable",
            _ => SensorAvailable ? "Gathering data" : "—",
        };
        PassiveLimitText = thermal.Throttling?.PassiveLimitPercent is { } pl ? (pl < 100 ? $"{pl:0}% — firmware is limiting speed for cooling" : "100% — no cooling limit active") : "Not reported";
        PerformanceText = thermal.Throttling?.ProcessorPerformancePercent is { } pp ? $"{pp:0}% of nominal speed" : "Not reported";
        PowerText = power.Describe() + (power.ActivePlan is { } plan ? $" · {plan.Name} plan" : string.Empty);

        Insights.Clear();
        foreach (var i in assessment.Insights) Insights.Add(i);
        Readings.Clear();
        foreach (var r in thermal.Readings) Readings.Add(r);
        ChartSamples = PerformanceHistory.Downsample(_metrics.History.GetWindow(SelectedWindow, DateTimeOffset.Now), 240);
    }

    [RelayCommand]
    private void SelectWindow(string name) => SelectedWindow = Enum.Parse<HistoryWindow>(name);
}

public sealed partial class ProcessRow(ProcessSnapshot p) : ObservableObject
{
    public ProcessSnapshot Process { get; } = p;
    public string Name => Process.Name;
    public int Pid => Process.Pid;
    public double Cpu => Process.CpuPercent;
    public long Memory => Process.PrivateBytes > 0 ? Process.PrivateBytes : Process.WorkingSetBytes;
    public string DiskText => Process.DiskBytesPerSecond is { } d ? $"{Format.Bytes((long)d)}/s" : "—";
    public string Publisher => Process.Publisher ?? string.Empty;
    public ProcessCategory Category => Process.Category;
    public bool CanClose => ProcessClassifier.MayOfferClose(Process) && Process.HasVisibleWindow;
    public double CpuBar => Math.Min(100, Process.CpuPercent);
}

public sealed partial class PerformanceViewModel : PageViewModel
{
    private readonly IMetricsSource _metrics;
    private readonly IProcessProvider _processes;
    private readonly IDialogService _dialogs;
    private readonly BenchmarkService _benchmarks;
    private readonly RecordStore<BenchmarkResult> _benchmarkHistory;
    private bool _subscribed;
    private CancellationTokenSource? _processLoop;
    private DateTimeOffset _lastChart;

    public PerformanceViewModel(IMetricsSource metrics, IProcessProvider processes, IDialogService dialogs, BenchmarkService benchmarks, RecordStore<BenchmarkResult> benchmarkHistory)
    {
        _metrics = metrics;
        _processes = processes;
        _dialogs = dialogs;
        _benchmarks = benchmarks;
        _benchmarkHistory = benchmarkHistory;
    }

    public override string Title => "Performance Monitor";

    [ObservableProperty] private IReadOnlyList<MetricSample>? _chartSamples;
    [ObservableProperty] private HistoryWindow _selectedWindow = HistoryWindow.FiveMinutes;
    [ObservableProperty] private bool _showCpu = true;
    [ObservableProperty] private bool _showMemory = true;
    [ObservableProperty] private bool _showDisk = true;
    [ObservableProperty] private string _statsText = string.Empty;
    [ObservableProperty] private string _processFilter = string.Empty;
    [ObservableProperty] private string _sortBy = "Cpu";
    [ObservableProperty] private bool _groupByApp = true;
    [ObservableProperty] private bool _hideWindows = true;
    [ObservableProperty] private bool _isBenchmarking;
    [ObservableProperty] private string _benchmarkStatus = "Measure your system before and after changes to compare real figures.";

    private IReadOnlyList<ProcessSnapshot> _lastProcesses = [];

    public ObservableCollection<ProcessRow> Processes { get; } = [];
    public ObservableCollection<BenchmarkDelta> BenchmarkDeltas { get; } = [];
    public ObservableCollection<BenchmarkResult> BenchmarkHistory { get; } = [];

    public override Task OnNavigatedToAsync()
    {
        if (!_subscribed)
        {
            _metrics.SampleAvailable += OnSample;
            _subscribed = true;
        }

        RefreshChart();
        BenchmarkHistory.Clear();
        foreach (var b in _benchmarkHistory.GetAll().Take(10)) BenchmarkHistory.Add(b);
        StartProcessLoop();
        return Task.CompletedTask;
    }

    public override void OnNavigatedFrom()
    {
        if (_subscribed)
        {
            _metrics.SampleAvailable -= OnSample;
            _subscribed = false;
        }

        _processLoop?.Cancel();
        _processLoop = null;
    }

    private void StartProcessLoop()
    {
        _processLoop?.Cancel();
        var cts = new CancellationTokenSource();
        _processLoop = cts;
        _ = Task.Run(async () =>
        {
            // The process list is the most expensive view, so it refreshes only while this page is visible.
            _processes.GetProcesses();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            try
            {
                do
                {
                    var list = _processes.GetProcesses();
                    Ui.Post(() =>
                    {
                        if (cts.IsCancellationRequested) return;
                        _lastProcesses = list;
                        ApplyProcessView();
                    });
                }
                while (await timer.WaitForNextTickAsync(cts.Token));
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void OnSample(object? sender, MetricSample e) => Ui.Post(() =>
    {
        if (DateTimeOffset.Now - _lastChart >= TimeSpan.FromSeconds(2)) RefreshChart();
    });

    partial void OnSelectedWindowChanged(HistoryWindow value) => RefreshChart();
    partial void OnProcessFilterChanged(string value) => ApplyProcessView();
    partial void OnSortByChanged(string value) => ApplyProcessView();
    partial void OnGroupByAppChanged(bool value) => ApplyProcessView();
    partial void OnHideWindowsChanged(bool value) => ApplyProcessView();

    [RelayCommand]
    private void SelectWindow(string name) => SelectedWindow = Enum.Parse<HistoryWindow>(name);

    [RelayCommand]
    private void Sort(string key) => SortBy = key;

    private void RefreshChart()
    {
        _lastChart = DateTimeOffset.Now;
        var samples = _metrics.History.GetWindow(SelectedWindow, DateTimeOffset.Now);
        ChartSamples = PerformanceHistory.Downsample(samples, 300);
        if (samples.Count == 0)
        {
            StatsText = "Collecting measurements…";
            return;
        }

        var disk = samples.Where(s => s.DiskActivePercent is not null).Select(s => s.DiskActivePercent!.Value).ToList();
        StatsText = $"Processor avg {samples.Average(s => s.CpuPercent):0}% · 95th pct {SampleStatistics.Percentile(samples.Select(s => s.CpuPercent), 95):0}% · peak {samples.Max(s => s.CpuPercent):0}%    " +
                    $"Memory avg {samples.Average(s => s.MemoryPercent):0}% · peak {samples.Max(s => s.MemoryPercent):0}%" +
                    (disk.Count > 0 ? $"    Disk busy avg {disk.Average():0}%" : string.Empty);
    }

    private void ApplyProcessView()
    {
        IEnumerable<ProcessSnapshot> list = _lastProcesses.Where(p => p.Category != ProcessCategory.Self && p.Pid > 4);
        if (HideWindows) list = list.Where(p => p.Category is not (ProcessCategory.Critical or ProcessCategory.WindowsComponent));
        if (!string.IsNullOrWhiteSpace(ProcessFilter))
            list = list.Where(p => p.Name.Contains(ProcessFilter.Trim(), StringComparison.OrdinalIgnoreCase) || (p.Publisher?.Contains(ProcessFilter.Trim(), StringComparison.OrdinalIgnoreCase) ?? false));

        if (GroupByApp)
        {
            list = list.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                var first = g.OrderByDescending(p => p.HasVisibleWindow).First();
                return first with
                {
                    CpuPercent = Math.Round(g.Sum(p => p.CpuPercent), 1),
                    WorkingSetBytes = g.Sum(p => p.WorkingSetBytes),
                    PrivateBytes = g.Sum(p => p.PrivateBytes),
                    DiskBytesPerSecond = g.Any(p => p.DiskBytesPerSecond is not null) ? g.Sum(p => p.DiskBytesPerSecond ?? 0) : null,
                    HasVisibleWindow = g.Any(p => p.HasVisibleWindow),
                };
            });
        }

        list = SortBy switch
        {
            "Memory" => list.OrderByDescending(p => p.PrivateBytes > 0 ? p.PrivateBytes : p.WorkingSetBytes),
            "Disk" => list.OrderByDescending(p => p.DiskBytesPerSecond ?? 0),
            "Name" => list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => list.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.PrivateBytes),
        };

        var rows = list.Take(60).Select(p => new ProcessRow(p)).ToList();
        Processes.Clear();
        foreach (var r in rows) Processes.Add(r);
    }

    [RelayCommand]
    private void AskToClose(ProcessRow? row)
    {
        if (row is null || !row.CanClose) return;
        var ok = _dialogs.Confirm(new ConfirmRequest($"Ask {row.Name} to close?",
            "TICKSHIFT sends the same request as clicking the window's close button. If the app has unsaved work, it can ask you to save first. Apps are never force-terminated.",
            ["If this app has unsaved documents, save them first to be safe."], "Ask to close"));
        if (!ok) return;

        var targets = _lastProcesses.Where(p => p.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase) && p.HasVisibleWindow).ToList();
        var result = targets.Count == 0 ? OperationResult.NotApplicable($"{row.Name} has no window that can be asked to close.") : _processes.RequestGracefulClose(targets[0].Pid);
        ShowResult(result);
    }

    [RelayCommand]
    private async Task MeasureAsync()
    {
        IsBenchmarking = true;
        BenchmarkStatus = "Measuring for 15 seconds… keep using the laptop as you normally would.";
        try
        {
            var result = await _benchmarks.CaptureAsync($"Manual measurement {DateTime.Now:g}", TimeSpan.FromSeconds(15), CancellationToken.None);
            var previous = _benchmarkHistory.GetAll().FirstOrDefault();
            await _benchmarkHistory.AppendAsync(result);
            BenchmarkHistory.Insert(0, result);
            BenchmarkDeltas.Clear();
            if (previous is not null)
            {
                var comparison = BenchmarkService.Compare(previous, result);
                foreach (var d in comparison.Deltas) BenchmarkDeltas.Add(d);
                BenchmarkStatus = $"Compared with the measurement from {previous.CapturedAt.LocalDateTime:g}. {comparison.Caveats[0]}";
            }
            else
            {
                BenchmarkStatus = "First measurement recorded. Measure again after making changes to compare.";
            }
        }
        catch (Exception ex)
        {
            BenchmarkStatus = "Measurement failed: " + Core.Errors.ErrorClassifier.FriendlyMessage(Core.Errors.ErrorClassifier.Classify(ex));
        }
        finally
        {
            IsBenchmarking = false;
        }
    }
}
