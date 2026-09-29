using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tickshift.App.Infrastructure;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;
using Tickshift.Diagnostics;
using Tickshift.Diagnostics.Network;
using Tickshift.Optimization;
using Tickshift.Optimization.Automation;
using Tickshift.Optimization.Recovery;
using Tickshift.Optimization.Reports;
using Tickshift.Persistence;
using Tickshift.Trading;

namespace Tickshift.App.ViewModels;

// ============================== Network ==============================

public sealed partial class NetworkViewModel : PageViewModel
{
    private readonly NetworkDiagnosticsService _network;
    private readonly ISettingsStore _settings;
    private readonly RecordStore<LatencyResult> _history;
    private readonly ILinkLauncher _links;
    private readonly TradingModeService _trading;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    public NetworkViewModel(NetworkDiagnosticsService network, ISettingsStore settings, RecordStore<LatencyResult> history, ILinkLauncher links,
        TradingModeService trading, IDialogService dialogs)
    {
        _network = network;
        _settings = settings;
        _history = history;
        _links = links;
        _trading = trading;
        _dialogs = dialogs;
    }

    public override string Title => "Network Diagnostics";

    [ObservableProperty] private string _connectionSummary = "Checking…";
    [ObservableProperty] private string _mediumText = string.Empty;
    [ObservableProperty] private string _wifiText = string.Empty;
    [ObservableProperty] private string _dnsText = string.Empty;
    [ObservableProperty] private string _gatewayText = string.Empty;
    [ObservableProperty] private bool _vpnDetected;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string _newHost = string.Empty;
    [ObservableProperty] private string _newPort = string.Empty;

    public string Disclaimer => NetworkDiagnosticsService.LatencyDisclaimer;

    public ObservableCollection<AdapterInfo> Adapters { get; } = [];
    public ObservableCollection<NetworkEndpoint> Endpoints { get; } = [];
    public ObservableCollection<LatencyResult> Results { get; } = [];
    public ObservableCollection<LatencyResult> History { get; } = [];

    public override async Task OnNavigatedToAsync()
    {
        Endpoints.Clear();
        foreach (var e in _settings.Current.NetworkEndpoints) Endpoints.Add(e);
        History.Clear();
        foreach (var h in _history.GetAll().Take(30)) History.Add(h);
        await CheckAsync();
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        try
        {
            var adapters = await Task.Run(_network.GetAdapters);
            Adapters.Clear();
            foreach (var a in adapters.Where(a => a.IsUp)) Adapters.Add(a);
            VpnDetected = adapters.Any(a => a.IsUp && a.Medium == NetworkMedium.Vpn);

            var status = await _network.CheckConnectivityAsync(CancellationToken.None);
            ConnectionSummary = status.Summary;
            MediumText = status.PrimaryAdapter is { } p ? $"{p.Medium switch { NetworkMedium.WiFi => "Wi-Fi", NetworkMedium.Ethernet => "Ethernet (wired)", NetworkMedium.Cellular => "Mobile broadband", NetworkMedium.Vpn => "VPN", _ => "Other" }} · {p.Description}" +
                         (p.SpeedBitsPerSecond > 0 ? $" · link {p.SpeedBitsPerSecond / 1_000_000:N0} Mbps" : string.Empty) : "No active adapter with a gateway";
            WifiText = status.Wifi is { } w ? $"Signal quality {w.SignalQualityPercent}%" + (w.LinkSpeedMbps is { } s ? $" · receive rate {s} Mbps" : string.Empty) : string.Empty;
            DnsText = status.DnsWorking switch { true => "Name resolution working", false => "Name resolution failing", _ => "Name resolution not tested" };
            GatewayText = status.GatewayReachable switch { true => "Router responds", null => "Router did not answer ping (many routers ignore it)", _ => string.Empty };
        }
        catch (Exception ex)
        {
            ConnectionSummary = "Network status could not be read: " + Core.Errors.ErrorClassifier.FriendlyMessage(Core.Errors.ErrorClassifier.Classify(ex));
        }
    }

    [RelayCommand]
    private async Task RunTestsAsync()
    {
        if (IsTesting) return;
        IsTesting = true;
        Results.Clear();
        _cts = new CancellationTokenSource();
        try
        {
            var targets = new List<(string Name, string Host, int? Port)>();
            if (NetworkDiagnosticsService.GetDefaultGateway() is { } gw) targets.Add(("Your router", gw.ToString(), null));
            targets.Add(("Microsoft connectivity check", NetworkDiagnosticsService.DefaultDnsProbeHost, null));
            targets.AddRange(Endpoints.Select(e => (e.Name, e.Host, e.TcpPort)));

            foreach (var t in targets)
            {
                var ping = await _network.PingAsync(t.Host, 10, _cts.Token);
                Results.Add(ping with { Target = $"{t.Name} ({ping.Target})" });
                await _history.AppendAsync(ping with { Target = $"{t.Name} ({ping.Target})" });
                if (t.Port is { } port)
                {
                    var tcp = await _network.TcpConnectAsync(t.Host, port, 5, _cts.Token);
                    Results.Add(tcp with { Target = $"{t.Name} ({tcp.Target})" });
                    await _history.AppendAsync(tcp with { Target = $"{t.Name} ({tcp.Target})" });
                }
            }

            History.Clear();
            foreach (var h in _history.GetAll().Take(30)) History.Add(h);
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Tests cancelled.");
        }
        finally
        {
            IsTesting = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelTests() => _cts?.Cancel();

    [RelayCommand]
    private async Task AddEndpointAsync()
    {
        if (!EndpointValidator.TryNormalise(NewHost, out var host, out var error))
        {
            ShowMessage(error ?? "Invalid host.", true);
            return;
        }

        int? port = null;
        if (!string.IsNullOrWhiteSpace(NewPort))
        {
            if (!int.TryParse(NewPort, out var p) || p is <= 0 or > 65535)
            {
                ShowMessage("The port must be a number between 1 and 65535.", true);
                return;
            }

            port = p;
        }

        if (Endpoints.Count >= 10)
        {
            ShowMessage("Up to 10 endpoints can be saved.", true);
            return;
        }

        Endpoints.Add(new NetworkEndpoint(string.IsNullOrWhiteSpace(NewName) ? host : NewName.Trim(), host, port));
        await _settings.UpdateAsync(s => s.NetworkEndpoints = Endpoints.ToList());
        NewName = NewHost = NewPort = string.Empty;
        ShowMessage($"{host} added. Only connectivity checks are performed — no data is sent.");
    }

    [RelayCommand]
    private async Task RemoveEndpointAsync(NetworkEndpoint? e)
    {
        if (e is null) return;
        Endpoints.Remove(e);
        await _settings.UpdateAsync(s => s.NetworkEndpoints = Endpoints.ToList());
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        await _history.ClearAsync();
        History.Clear();
    }

    [RelayCommand]
    private void OpenTroubleshooter()
    {
        if (_trading.IsActive)
        {
            ShowMessage("Network repair tools are not offered during Trading Mode because they can briefly disconnect you.", true);
            return;
        }

        if (!_dialogs.Confirm(new ConfirmRequest("Open Windows network settings?",
                "Windows' network troubleshooting and reset options can disconnect you briefly, including from broker platforms and VPNs. Only use them when you are not trading. TICKSHIFT never resets adapters itself.",
                ConfirmText: "Open settings")))
            return;
        ShowResult(_links.Open("network-status"));
    }
}

// ============================== Automation ==============================

public sealed partial class UnattendedOption(IOptimisation o, bool approved) : ObservableObject
{
    [ObservableProperty] private bool _isApproved = approved;
    public IOptimisation Optimisation { get; } = o;
    public string Title => Optimisation.Title;
    public string Description => Optimisation.Description;
}

public sealed partial class AutomationViewModel : PageViewModel
{
    private readonly ISettingsStore _settings;
    private readonly AutomationService _automation;
    private readonly OptimisationEngine _engine;
    private readonly RecordStore<AutomationEvent> _events;
    private bool _loading;

    public AutomationViewModel(ISettingsStore settings, AutomationService automation, OptimisationEngine engine, RecordStore<AutomationEvent> events)
    {
        _settings = settings;
        _automation = automation;
        _engine = engine;
        _events = events;
    }

    public override string Title => "Automation";

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private bool _optimiseAtLogin;
    [ObservableProperty] private bool _dailyDiagnostic;
    [ObservableProperty] private bool _storageReminder;
    [ObservableProperty] private bool _memoryAlerts;
    [ObservableProperty] private bool _thermalMonitoring;
    [ObservableProperty] private bool _browserRecommendations;
    [ObservableProperty] private bool _bottleneckDetection;
    [ObservableProperty] private double _memoryThreshold;
    [ObservableProperty] private double _cpuThreshold;
    [ObservableProperty] private double _sustainedMinutes;
    [ObservableProperty] private double _lowStoragePercent;
    [ObservableProperty] private string _dailyTime = "09:00";
    [ObservableProperty] private bool _notificationsEnabled;

    public ObservableCollection<UnattendedOption> Unattended { get; } = [];
    public ObservableCollection<AutomationEvent> History { get; } = [];

    public override Task OnNavigatedToAsync()
    {
        _loading = true;
        var a = _settings.Current.Automation;
        Enabled = a.Enabled;
        Paused = a.Paused;
        OptimiseAtLogin = a.OptimiseAtLogin;
        DailyDiagnostic = a.DailyDiagnostic;
        StorageReminder = a.StorageReminder;
        MemoryAlerts = a.MemoryPressureAlerts;
        ThermalMonitoring = a.ThermalMonitoring;
        BrowserRecommendations = a.BrowserRecommendations;
        BottleneckDetection = a.BottleneckDetection;
        MemoryThreshold = a.MemoryPressureThresholdPercent;
        CpuThreshold = a.CpuBottleneckPercent;
        SustainedMinutes = a.SustainedMinutes;
        LowStoragePercent = a.LowStoragePercent;
        DailyTime = a.DailyDiagnosticTime.ToString("HH:mm");
        NotificationsEnabled = _settings.Current.Notifications.Enabled;

        Unattended.Clear();
        foreach (var o in _engine.Catalogue.Where(o => o.SuitableForUnattended))
        {
            var option = new UnattendedOption(o, a.ApprovedUnattendedOptimisations.Contains(o.Id));
            option.PropertyChanged += async (_, _) => await SaveAsync();
            Unattended.Add(option);
        }

        History.Clear();
        foreach (var e in _events.GetAll().Take(50)) History.Add(e);
        _loading = false;
        return Task.CompletedTask;
    }

    protected override async void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(StatusMessage) or nameof(StatusIsError) or nameof(IsBusy)) return;
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (_loading) return;
        var time = TimeOnly.TryParse(DailyTime, out var t) ? t : new TimeOnly(9, 0);
        await _settings.UpdateAsync(s =>
        {
            var a = s.Automation;
            a.Enabled = Enabled;
            a.Paused = Paused;
            a.OptimiseAtLogin = OptimiseAtLogin;
            a.DailyDiagnostic = DailyDiagnostic;
            a.StorageReminder = StorageReminder;
            a.MemoryPressureAlerts = MemoryAlerts;
            a.ThermalMonitoring = ThermalMonitoring;
            a.BrowserRecommendations = BrowserRecommendations;
            a.BottleneckDetection = BottleneckDetection;
            a.MemoryPressureThresholdPercent = (int)MemoryThreshold;
            a.CpuBottleneckPercent = (int)CpuThreshold;
            a.SustainedMinutes = (int)SustainedMinutes;
            a.LowStoragePercent = (int)LowStoragePercent;
            a.DailyDiagnosticTime = time;
            a.ApprovedUnattendedOptimisations = Unattended.Where(u => u.IsApproved).Select(u => u.Optimisation.Id).ToList();
            s.Notifications.Enabled = NotificationsEnabled;
        });
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        await _events.ClearAsync();
        History.Clear();
    }
}

// ============================== Recovery ==============================

public sealed partial class RecoveryViewModel : PageViewModel
{
    private readonly RecoveryService _recovery;
    private readonly IRestorePointService _restorePoints;
    private readonly RecordStore<OptimisationRunRecord> _runs;
    private readonly DiagnosticReportExporter _exporter;
    private readonly DiagnosticEngine _diagnostics;
    private readonly IDialogService _dialogs;
    private readonly AppDataPaths _paths;

    public RecoveryViewModel(RecoveryService recovery, IRestorePointService restorePoints, RecordStore<OptimisationRunRecord> runs,
        DiagnosticReportExporter exporter, DiagnosticEngine diagnostics, IDialogService dialogs, AppDataPaths paths)
    {
        _recovery = recovery;
        _restorePoints = restorePoints;
        _runs = runs;
        _exporter = exporter;
        _diagnostics = diagnostics;
        _dialogs = dialogs;
        _paths = paths;
    }

    public override string Title => "Recovery Centre";

    [ObservableProperty] private string _restorePointText = string.Empty;
    [ObservableProperty] private bool _restorePointAvailable;
    [ObservableProperty] private string _tab = "Active";

    public ObservableCollection<ChangeRecord> Active { get; } = [];
    public ObservableCollection<ChangeRecord> AllChanges { get; } = [];
    public ObservableCollection<ChangeRecord> Failed { get; } = [];
    public ObservableCollection<OptimisationRunRecord> Runs { get; } = [];

    public override Task OnNavigatedToAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    private void Reload()
    {
        var status = _restorePoints.GetStatus();
        RestorePointText = status.Explanation;
        RestorePointAvailable = status.Enabled != false && !status.DisabledByPolicy;
        Fill(Active, _recovery.GetActiveReversible());
        Fill(AllChanges, _recovery.GetAll().Take(200));
        Fill(Failed, _recovery.GetFailed());
        Runs.Clear();
        foreach (var r in _runs.GetAll().Take(30)) Runs.Add(r);
    }

    private static void Fill(ObservableCollection<ChangeRecord> target, IEnumerable<ChangeRecord> source)
    {
        target.Clear();
        foreach (var r in source) target.Add(r);
    }

    [RelayCommand]
    private void SetTab(string tab) => Tab = tab;

    [RelayCommand]
    private async Task RestoreAsync(ChangeRecord? record)
    {
        if (record is null) return;
        if (!_dialogs.Confirm(new ConfirmRequest("Restore this setting?", $"{record.Description}\n{record.Target}: {record.NewValue ?? "—"} → {record.OriginalValue ?? "original"}",
                ConfirmText: "Restore")))
            return;
        var result = await _recovery.RollbackAsync(record, CancellationToken.None);
        ShowResult(result);
        Reload();
    }

    [RelayCommand]
    private async Task UndoLastAsync()
    {
        if (_recovery.LastRunWithReversibleChanges() is null)
        {
            ShowMessage("There is no optimisation with reversible changes to undo.");
            return;
        }

        if (!_dialogs.Confirm(new ConfirmRequest("Undo the last optimisation?", "All reversible changes from the most recent optimisation will be restored, newest first.", ConfirmText: "Undo")))
            return;
        var report = await _recovery.UndoLastAsync(CancellationToken.None);
        ShowMessage(Describe(report), report.Failed > 0);
        Reload();
    }

    [RelayCommand]
    private async Task RestoreAllAsync()
    {
        var count = _recovery.GetActiveReversible().Count;
        if (count == 0)
        {
            ShowMessage("There are no active changes to restore.");
            return;
        }

        if (!_dialogs.Confirm(new ConfirmRequest("Restore previous configuration?",
                $"TICKSHIFT will restore all {count} reversible change(s) it has made. Do this before uninstalling to leave Windows exactly as you configured it.",
                ConfirmText: "Restore everything")))
            return;
        var report = await _recovery.RestoreAllAsync(CancellationToken.None);
        ShowMessage(Describe(report), report.Failed > 0);
        Reload();
    }

    [RelayCommand]
    private async Task CreateRestorePointAsync()
    {
        IsBusy = true;
        try
        {
            ShowResult(await _restorePoints.CreateAsync(CancellationToken.None));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportReportAsync()
    {
        var path = _dialogs.SaveFile("Export diagnostic report", $"TICKSHIFT-report-{DateTime.Now:yyyyMMdd-HHmm}.txt", "Text report (*.txt)|*.txt|JSON (*.json)|*.json");
        if (path is null) return;
        try
        {
            await _exporter.ExportAsync(path, _diagnostics.LastReport, CancellationToken.None);
            ShowMessage($"Report saved to {path}. Personal details such as your user name and browsing activity are removed.");
        }
        catch (Exception ex)
        {
            ShowMessage("The report could not be saved: " + Core.Errors.ErrorClassifier.FriendlyMessage(Core.Errors.ErrorClassifier.Classify(ex)), true);
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_paths.Root) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowMessage("The folder could not be opened: " + ex.Message, true);
        }
    }

    private static string Describe(RollbackReport report) =>
        $"Restored {report.Restored} change(s)" +
        (report.Conflicts > 0 ? $"; {report.Conflicts} left as is because they were changed elsewhere" : string.Empty) +
        (report.NotReversible > 0 ? $"; {report.NotReversible} cannot be undone (e.g. deleted files)" : string.Empty) +
        (report.Failed > 0 ? $"; {report.Failed} failed" : string.Empty) + ".";
}
