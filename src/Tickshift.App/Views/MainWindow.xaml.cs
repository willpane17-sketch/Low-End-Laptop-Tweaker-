using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Tickshift.App.Infrastructure;
using Tickshift.App.ViewModels;
using Tickshift.Core.Abstractions;
using Tickshift.Diagnostics.Monitoring;

namespace Tickshift.App.Views;

public partial class MainWindow : Window
{
    private const int WmSettingChange = 0x001A;
    private const double CompactBreakpoint = 1120;
    private readonly ThemeService _theme;
    private readonly MetricsMonitor _monitor;
    private bool _forceClose;
    private bool? _userCompactChoice;

    public MainWindow(MainViewModel viewModel, ThemeService theme, ISettingsStore settings, MetricsMonitor monitor)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        _theme = theme;
        _monitor = monitor;

        FitToWorkArea();
        SizeChanged += (_, _) => UpdateSidebar();
        StateChanged += OnStateChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateSidebar();
    }

    public MainViewModel ViewModel { get; }

    public event EventHandler? CloseRequested;

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(WndProc);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSettingChange)
        {
            var area = System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam);
            if (area == "ImmersiveColorSet") _theme.OnSystemThemeChanged();
        }

        return IntPtr.Zero;
    }

    /// <summary>Keeps the window fully on screen on small (e.g. 1366×768) displays.</summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 24));
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 24));
        if (area.Width < 1400 || area.Height < 820) WindowState = WindowState.Maximized;
    }

    private void UpdateSidebar()
    {
        var compact = _userCompactChoice ?? ActualWidth / Math.Max(0.5, ViewModel.UiScale) < CompactBreakpoint;
        if (ViewModel.IsSidebarCompact != compact) ViewModel.IsSidebarCompact = compact;
        Sidebar.Width = ViewModel.IsSidebarCompact ? 70 : 236;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSidebarCompact))
        {
            Sidebar.Width = ViewModel.IsSidebarCompact ? 70 : 236;
            // Remember explicit toggles so resizing doesn't fight the user.
            var automatic = ActualWidth / Math.Max(0.5, ViewModel.UiScale) < CompactBreakpoint;
            if (ViewModel.IsSidebarCompact != automatic) _userCompactChoice = ViewModel.IsSidebarCompact;
            else _userCompactChoice = null;
        }
        else if (e.PropertyName == nameof(MainViewModel.UiScale))
        {
            UpdateSidebar();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // A maximised custom-chrome window extends past the screen edge by the resize border; compensate.
        Frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        Frame.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
        MaxIcon.Data = (System.Windows.Media.Geometry)FindResource(WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize");
        MaxButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, WindowState == WindowState.Maximized ? "Restore" : "Maximise");

        var minimised = WindowState == WindowState.Minimized;
        _monitor.BackgroundMode = minimised;
        ViewModel.SetVisible(!minimised);
    }

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
