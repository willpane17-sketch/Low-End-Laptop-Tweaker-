using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Tickshift.App.Infrastructure;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Settings;
using Tickshift.Trading;

namespace Tickshift.App.ViewModels;

public sealed partial class NavItem : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public NavItem(string key, string title, string icon, string? group = null, string? shortcut = null)
    {
        Key = key;
        Title = title;
        IconKey = icon;
        Group = group;
        Shortcut = shortcut;
    }

    public string Key { get; }
    public string Title { get; }
    public string IconKey { get; }
    public string? Group { get; }
    public string? Shortcut { get; }
    public string AccessibleName => Shortcut is null ? Title : $"{Title} ({Shortcut})";
}

/// <summary>Shell view-model: sidebar navigation, lazy page creation, mode indicators, toasts and onboarding.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private static readonly Dictionary<string, Type> PageTypes = new()
    {
        [PageKeys.Dashboard] = typeof(DashboardViewModel),
        [PageKeys.Optimise] = typeof(OptimiseViewModel),
        [PageKeys.Trading] = typeof(TradingViewModel),
        [PageKeys.Browser] = typeof(BrowserViewModel),
        [PageKeys.Cleanup] = typeof(CleanupViewModel),
        [PageKeys.Startup] = typeof(StartupViewModel),
        [PageKeys.Thermal] = typeof(ThermalViewModel),
        [PageKeys.Performance] = typeof(PerformanceViewModel),
        [PageKeys.Network] = typeof(NetworkViewModel),
        [PageKeys.Automation] = typeof(AutomationViewModel),
        [PageKeys.Recovery] = typeof(RecoveryViewModel),
        [PageKeys.Settings] = typeof(SettingsViewModel),
    };

    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly TradingModeService _trading;
    private readonly NotificationService _notifications;

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private string _currentPageKey = string.Empty;
    [ObservableProperty] private bool _isSidebarCompact;
    [ObservableProperty] private bool _isTradingModeActive;
    [ObservableProperty] private bool _isDryRun;
    [ObservableProperty] private double _uiScale = 1.0;
    [ObservableProperty] private bool _reducedMotion;
    [ObservableProperty] private OnboardingViewModel? _onboarding;
    [ObservableProperty] private string _windowTitle = Brand.ProductName;

    public MainViewModel(IServiceProvider services, ISettingsStore settings, TradingModeService trading, NotificationService notifications, NavigationService navigation)
    {
        _services = services;
        _settings = settings;
        _trading = trading;
        _notifications = notifications;

        NavItems =
        [
            new(PageKeys.Dashboard, "Dashboard", "Icon.Dashboard", shortcut: "Ctrl+1"),
            new(PageKeys.Optimise, "One-Click Optimise", "Icon.Bolt", shortcut: "Ctrl+2"),
            new(PageKeys.Trading, "Trading Mode", "Icon.Trading", shortcut: "Ctrl+3"),
            new(PageKeys.Browser, "Browser Booster", "Icon.Globe", shortcut: "Ctrl+4"),
            new(PageKeys.Cleanup, "System Cleanup", "Icon.Cleanup", "Maintain", "Ctrl+5"),
            new(PageKeys.Startup, "Startup Manager", "Icon.Startup", "Maintain", "Ctrl+6"),
            new(PageKeys.Thermal, "Thermal Monitor", "Icon.Thermometer", "Monitor", "Ctrl+7"),
            new(PageKeys.Performance, "Performance Monitor", "Icon.Pulse", "Monitor", "Ctrl+8"),
            new(PageKeys.Network, "Network Diagnostics", "Icon.Wifi", "Monitor", "Ctrl+9"),
            new(PageKeys.Automation, "Automation", "Icon.Automation", "Control"),
            new(PageKeys.Recovery, "Recovery Centre", "Icon.Shield", "Control"),
            new(PageKeys.Settings, "Settings", "Icon.Settings", "Control"),
        ];

        navigation.NavigationRequested += (_, key) => Ui.Post(() => Navigate(key));
        _trading.ActiveChanged += (_, active) => Ui.Post(() => IsTradingModeActive = active);
        _settings.Changed += (_, s) => Ui.Post(() => ApplySettings(s));
        ApplySettings(settings.Current);
    }

    public ObservableCollection<NavItem> NavItems { get; }

    public ObservableCollection<ToastItem> Toasts => _notifications.Toasts;

    public string ProductName => Brand.ProductName;
    public string Tagline => Brand.Tagline;
    public string Version => "v" + Brand.Version;

    private void ApplySettings(AppSettings s)
    {
        IsDryRun = s.DryRunMode;
        UiScale = s.UiScale;
        ReducedMotion = s.ReducedMotion;
    }

    [RelayCommand]
    public void Navigate(string? key)
    {
        if (key is null || !PageTypes.TryGetValue(key, out var type)) return;
        var page = (PageViewModel)_services.GetRequiredService(type);
        if (ReferenceEquals(page, CurrentPage)) return;

        CurrentPage?.OnNavigatedFrom();
        CurrentPage = page;
        CurrentPageKey = key;
        foreach (var item in NavItems) item.IsSelected = item.Key == key;
        WindowTitle = $"{page.Title} · {Brand.ProductName}";
        _ = page.OnNavigatedToAsync();
    }

    [RelayCommand]
    private void DismissToast(ToastItem? toast)
    {
        if (toast is not null) _notifications.Dismiss(toast);
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCompact = !IsSidebarCompact;

    public void ShowOnboarding()
    {
        var vm = _services.GetRequiredService<OnboardingViewModel>();
        vm.Completed += (_, _) => Ui.Post(() =>
        {
            Onboarding = null;
            Navigate(PageKeys.Dashboard);
        });
        Onboarding = vm;
        _ = vm.StartAsync();
    }

    /// <summary>Called when the window is hidden or restored so the current page can pause/resume its live updates.</summary>
    public void SetVisible(bool visible)
    {
        if (CurrentPage is null) return;
        if (visible) _ = CurrentPage.OnNavigatedToAsync();
        else CurrentPage.OnNavigatedFrom();
    }
}
