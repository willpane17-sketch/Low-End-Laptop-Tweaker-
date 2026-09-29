using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tickshift.App.Infrastructure;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;
using Tickshift.Diagnostics;
using Tickshift.Diagnostics.Network;
using Tickshift.Trading;

namespace Tickshift.App.ViewModels;

public sealed partial class ProtectedAppOption : ObservableObject
{
    [ObservableProperty] private bool _isProtected;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isRunning;

    public ProtectedAppOption(ProtectedApplication app, bool isProtected, string hint)
    {
        App = app;
        _isProtected = isProtected;
        Hint = hint;
    }

    public ProtectedApplication App { get; }
    public string Name => App.DisplayName;
    public string Executable => App.ExecutablePath ?? App.ExecutableName;
    public string Hint { get; }
    public bool IsCustom => App.Kind == ProtectedAppKind.Custom;
}

public sealed partial class TradingViewModel : PageViewModel
{
    private readonly TradingModeService _trading;
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly IProcessProvider _processes;
    private readonly NetworkDiagnosticsService _network;
    private readonly INotificationService _notifications;
    private bool _subscribed;
    private bool _loading;

    public TradingViewModel(TradingModeService trading, ISettingsStore settings, IDialogService dialogs, IProcessProvider processes,
        NetworkDiagnosticsService network, INotificationService notifications)
    {
        _trading = trading;
        _settings = settings;
        _dialogs = dialogs;
        _processes = processes;
        _network = network;
        _notifications = notifications;
        _adjustPowerPlan = settings.Current.TradingModeAdjustPowerPlan;
        _reduceAnimations = settings.Current.TradingModeReduceAnimations;
    }

    public override string Title => "Trading Mode";

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isTransitioning;
    [ObservableProperty] private string _stateText = "Trading Mode is off";
    [ObservableProperty] private string _activeSince = string.Empty;
    [ObservableProperty] private bool _adjustPowerPlan;
    [ObservableProperty] private bool _reduceAnimations;
    [ObservableProperty] private string _activationStep = string.Empty;
    [ObservableProperty] private TradingSnapshot? _snapshot;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _memoryText = "—";
    [ObservableProperty] private PressureLevel _cpuPressure;
    [ObservableProperty] private PressureLevel _memoryPressure;
    [ObservableProperty] private string _browserText = "—";
    [ObservableProperty] private string _thermalText = "Sensor unavailable";
    [ObservableProperty] private string _networkText = "Not tested yet";
    [ObservableProperty] private bool _isTestingNetwork;

    public ObservableCollection<ProtectedAppOption> Apps { get; } = [];
    public ObservableCollection<ProtectedAppStatus> ProtectedStatuses { get; } = [];
    public ObservableCollection<ProcessUsage> CloseSuggestions { get; } = [];
    public ObservableCollection<string> AppliedChanges { get; } = [];

    public override async Task OnNavigatedToAsync()
    {
        if (!_subscribed)
        {
            _trading.SnapshotUpdated += OnSnapshot;
            _trading.StateChanged += OnStateChanged;
            _subscribed = true;
        }

        SyncState();
        await LoadAppsAsync();
        if (_trading.Latest is { } latest) ApplySnapshot(latest);
    }

    public override void OnNavigatedFrom()
    {
        if (!_subscribed) return;
        _trading.SnapshotUpdated -= OnSnapshot;
        _trading.StateChanged -= OnStateChanged;
        _subscribed = false;
    }

    private void OnStateChanged(object? sender, TradingModeState e) => Ui.Post(SyncState);

    private void SyncState()
    {
        IsActive = _trading.State == TradingModeState.Active;
        IsTransitioning = _trading.State is TradingModeState.Activating or TradingModeState.Ending;
        StateText = _trading.State switch
        {
            TradingModeState.Active => "TRADING MODE ACTIVE",
            TradingModeState.Activating => "Starting Trading Mode…",
            TradingModeState.Ending => "Ending Trading Mode and restoring settings…",
            _ => "Trading Mode is off",
        };
        ActiveSince = _trading.StartedAt is { } s ? $"Active since {s.LocalDateTime:t}" : string.Empty;
        AppliedChanges.Clear();
        foreach (var c in _trading.AppliedChanges) AppliedChanges.Add(c);
    }

    private async Task LoadAppsAsync()
    {
        _loading = true;
        try
        {
            var running = await Task.Run(() => _processes.GetProcesses().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
            var detected = await Task.Run(() => KnownTradingApplications.Detect(running));
            var current = _settings.Current.ProtectedApplications;
            Apps.Clear();
            foreach (var d in detected)
            {
                var existing = current.FirstOrDefault(a => a.Id == d.Definition.Id);
                var option = new ProtectedAppOption(existing ?? KnownTradingApplications.ToProtected(d.Definition), existing?.Enabled == true, d.Definition.Hint)
                {
                    IsRunning = d.Running,
                    Status = d.Running ? "Running now" : d.Installed ? "Installed" : "Not detected",
                };
                option.PropertyChanged += OnOptionChanged;
                Apps.Add(option);
            }

            foreach (var custom in current.Where(a => a.Kind == ProtectedAppKind.Custom))
            {
                var option = new ProtectedAppOption(custom, custom.Enabled, "Custom application you added.")
                {
                    IsRunning = running.Contains(System.IO.Path.GetFileNameWithoutExtension(custom.ExecutableName)),
                    Status = System.IO.File.Exists(custom.ExecutablePath) ? "Installed" : "File not found",
                };
                option.PropertyChanged += OnOptionChanged;
                Apps.Add(option);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private async void OnOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loading || e.PropertyName != nameof(ProtectedAppOption.IsProtected)) return;
        await SaveAppsAsync();
    }

    private Task SaveAppsAsync()
    {
        var list = Apps.Where(a => a.IsProtected || a.IsCustom).Select(a => a.App with { Enabled = a.IsProtected }).ToList();
        return _settings.UpdateAsync(s => s.ProtectedApplications = list);
    }

    [RelayCommand]
    private async Task AddCustomAppAsync()
    {
        var path = _dialogs.OpenExecutable("Choose a trading application to protect");
        if (path is null) return;
        var app = KnownTradingApplications.FromCustomPath(path);
        if (app is null)
        {
            ShowMessage("Only .exe applications can be protected.", true);
            return;
        }

        if (Apps.Any(a => a.App.Id == app.Id))
        {
            ShowMessage($"{app.DisplayName} is already in the list.");
            return;
        }

        var option = new ProtectedAppOption(app, true, "Custom application you added.") { Status = "Installed" };
        option.PropertyChanged += OnOptionChanged;
        Apps.Add(option);
        await SaveAppsAsync();
        ShowMessage($"{app.DisplayName} is now protected.");
    }

    [RelayCommand]
    private async Task RemoveCustomAppAsync(ProtectedAppOption? option)
    {
        if (option is null || !option.IsCustom) return;
        Apps.Remove(option);
        await SaveAppsAsync();
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (_trading.IsActive) return;
        if (!Apps.Any(a => a.IsProtected))
        {
            if (!_dialogs.Confirm(new ConfirmRequest("No protected applications",
                    "You have not selected any trading applications to protect. Trading Mode will still monitor your system, but it cannot report on your trading apps. Continue?",
                    ConfirmText: "Start anyway")))
                return;
        }

        await _settings.UpdateAsync(s =>
        {
            s.TradingModeAdjustPowerPlan = AdjustPowerPlan;
            s.TradingModeReduceAnimations = ReduceAnimations;
        });

        IsTransitioning = true;
        var progress = new Progress<ActivationStep>(s => ActivationStep = $"{s.Name} — {s.Detail}");
        var result = await _trading.ActivateAsync(new TradingActivationOptions(AdjustPowerPlan, ReduceAnimations), progress, CancellationToken.None);
        ShowResult(result);
        SyncState();
        ActivationStep = string.Empty;
    }

    [RelayCommand]
    private async Task EndAsync()
    {
        IsTransitioning = true;
        var result = await _trading.EndAsync(CancellationToken.None);
        ShowResult(result);
        _notifications.Notify(new NotificationMessage("Trading Mode", result.Message, result.IsSuccess ? NotificationKind.Success : NotificationKind.Warning));
        Snapshot = null;
        ProtectedStatuses.Clear();
        CloseSuggestions.Clear();
        SyncState();
    }

    [RelayCommand]
    private void AskToClose(ProcessUsage? usage)
    {
        if (usage is null) return;
        var ok = _dialogs.Confirm(new ConfirmRequest($"Ask {usage.Name} to close?",
            $"TICKSHIFT will ask {usage.Name} to close, exactly like clicking its close button. If it has unsaved work it can ask you first. It will never be force-terminated.",
            ConfirmText: "Ask to close"));
        if (!ok) return;

        var messages = new List<string>();
        foreach (var p in _processes.GetProcesses().Where(p => p.Name.Equals(usage.Name, StringComparison.OrdinalIgnoreCase) && p.HasVisibleWindow))
        {
            var result = _processes.RequestGracefulClose(p.Pid);
            messages.Add(result.Message);
        }

        ShowMessage(messages.FirstOrDefault() ?? $"{usage.Name} has no open window to close.");
    }

    [RelayCommand]
    private async Task TestNetworkAsync()
    {
        IsTestingNetwork = true;
        try
        {
            var status = await _network.CheckConnectivityAsync(CancellationToken.None);
            var endpoint = _settings.Current.NetworkEndpoints.FirstOrDefault();
            var target = endpoint?.Host ?? NetworkDiagnosticsService.GetDefaultGateway()?.ToString() ?? NetworkDiagnosticsService.DefaultDnsProbeHost;
            var result = endpoint?.TcpPort is { } port
                ? await _network.TcpConnectAsync(target, port, 4, CancellationToken.None)
                : await _network.PingAsync(target, 6, CancellationToken.None);
            NetworkText = result.Received == 0
                ? $"{status.Summary} {target}: no replies ({result.Error})."
                : $"{status.Summary} {target}: {result.AverageMs:0} ms average, {result.JitterMs ?? 0:0} ms jitter, {result.LossPercent:0}% loss ({result.Quality}).";
        }
        catch (Exception ex)
        {
            NetworkText = "Network test failed: " + Core.Errors.ErrorClassifier.FriendlyMessage(Core.Errors.ErrorClassifier.Classify(ex));
        }
        finally
        {
            IsTestingNetwork = false;
        }
    }

    private void OnSnapshot(object? sender, TradingSnapshot s) => Ui.Post(() => ApplySnapshot(s));

    private void ApplySnapshot(TradingSnapshot s)
    {
        Snapshot = s;
        CpuText = $"{s.CpuPercent:0}%";
        MemoryText = $"{s.MemoryPercent:0}%";
        CpuPressure = s.CpuPressure;
        MemoryPressure = s.MemoryPressure;
        BrowserText = s.BrowserProcessCount == 0 ? "No browser running"
            : $"{s.BrowserProcessCount} processes · {s.BrowserCpuPercent:0}% CPU · {Format.Bytes(s.BrowserMemoryBytes)}";
        ThermalText = s.TemperatureCelsius is { } t ? $"{t:0} °C" : "Sensor unavailable";
        ProtectedStatuses.Clear();
        foreach (var p in s.ProtectedApps) ProtectedStatuses.Add(p);
        CloseSuggestions.Clear();
        foreach (var c in s.CloseSuggestions) CloseSuggestions.Add(c);
    }
}
