using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tickshift.App.Composition;
using Tickshift.App.Infrastructure;
using Tickshift.App.ViewModels;
using Tickshift.App.Views;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Errors;
using Tickshift.Diagnostics.Monitoring;
using Tickshift.Optimization.Automation;
using Tickshift.Optimization.Recovery;
using Tickshift.Persistence;
using Tickshift.Trading;

namespace Tickshift.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private SingleInstance? _instance;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private ILogger? _logger;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = StartupArguments.Parse(e.Args);

        _instance = SingleInstance.TryAcquire(ShowMainWindow);
        if (_instance is null)
        {
            Shutdown(0);
            return;
        }

        var paths = new AppDataPaths();
        try
        {
            paths.EnsureCreated();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{Brand.ProductName} could not create its data folder:\n{paths.Root}\n\n{ex.Message}", Brand.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var initialRetention = Persistence.AtomicJsonFile.Read<Core.Settings.AppSettings>(paths.SettingsFile).Value?.DataRetentionDays ?? 30;
        try
        {
            _services = ServiceRegistration.Build(paths, initialRetention);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{Brand.ProductName} could not start:\n\n{ex.Message}", Brand.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("App");
        HookExceptionHandlers();
        _logger.LogInformation("{Product} {Version} starting (autostart={Autostart}, elevated={Elevated}, os={Os})",
            Brand.ProductName, Brand.Version, args.Autostart, _services.GetRequiredService<IElevationState>().IsElevated, Environment.OSVersion.VersionString);

        var settings = _services.GetRequiredService<ISettingsStore>();
        if (args.DryRun && !settings.Current.DryRunMode) _ = settings.UpdateAsync(s => s.DryRunMode = true);

        _services.GetRequiredService<ThemeService>().Apply(settings.Current);

        var notifications = _services.GetRequiredService<NotificationService>();
        try
        {
            _tray = new TrayIcon();
            _tray.OpenRequested += (_, _) => ShowMainWindow();
            _tray.ExitRequested += async (_, _) => await ExitAsync();
            _tray.TradingModeToggleRequested += (_, _) =>
            {
                ShowMainWindow();
                _services.GetRequiredService<NavigationService>().Navigate(PageKeys.Trading);
            };
            notifications.Tray = _tray;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification area icon unavailable");
        }

        var trading = _services.GetRequiredService<TradingModeService>();
        trading.ActiveChanged += (_, active) =>
        {
            if (_tray is null) return;
            _tray.TradingModeActive = active;
            _tray.SetTooltip(active ? $"{Brand.ProductName} — Trading Mode active" : $"{Brand.ProductName} — {Brand.Tagline}");
        };

        var main = _services.GetRequiredService<MainViewModel>();
        _window = new MainWindow(main, _services.GetRequiredService<ThemeService>(), settings, _services.GetRequiredService<MetricsMonitor>());
        _window.CloseRequested += OnWindowCloseRequested;
        MainWindow = _window;
        notifications.IsAppInForeground = () => _window is { IsVisible: true, IsActive: true };

        _services.GetRequiredService<MetricsMonitor>().Start();
        _ = RecoverAsync();
        _services.GetRequiredService<AutomationService>().Start(args.Autostart);

        var startHidden = args.Minimised || (args.Autostart && settings.Current.StartMinimised) || (settings.Current.StartMinimised && _tray is not null);
        if (!settings.Current.OnboardingCompleted)
        {
            startHidden = false;
            main.ShowOnboarding();
        }
        else
        {
            main.Navigate(PageKeys.Dashboard);
        }

        if (startHidden && _tray is not null)
        {
            _services.GetRequiredService<MetricsMonitor>().BackgroundMode = true;
        }
        else
        {
            _window.Show();
        }
    }

    /// <summary>Recovers from an unexpected exit: interrupted changes and an unfinished Trading Mode session.</summary>
    private async Task RecoverAsync()
    {
        if (_services is null) return;
        try
        {
            var recovery = _services.GetRequiredService<RecoveryService>();
            var interrupted = await recovery.RecoverInterruptedAsync(CancellationToken.None);
            var tradingResult = await _services.GetRequiredService<TradingModeService>().RecoverAfterUnexpectedExitAsync(CancellationToken.None);
            var notify = _services.GetRequiredService<INotificationService>();
            if (interrupted > 0)
                notify.Notify(new NotificationMessage("Recovery", $"{interrupted} change(s) were interrupted last time. Review them in the Recovery Centre.", NotificationKind.Warning));
            if (tradingResult is not null)
                notify.Notify(new NotificationMessage("Recovery", tradingResult.Message, NotificationKind.Info));

            var settings = _services.GetRequiredService<ISettingsStore>().Current;
            await _services.GetRequiredService<IChangeJournal>().PruneAsync(settings.DataRetentionDays);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Startup recovery failed");
        }
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _services?.GetRequiredService<MetricsMonitor>().SetBackground(false);
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.ViewModel.SetVisible(true);
    }

    private async void OnWindowCloseRequested(object? sender, EventArgs e)
    {
        if (_services is null) return;
        var settings = _services.GetRequiredService<ISettingsStore>().Current;
        if (settings.MinimiseToTray && _tray is not null && !_exiting)
        {
            _window!.Hide();
            _window.ViewModel.SetVisible(false);
            _services.GetRequiredService<MetricsMonitor>().SetBackground(true);
            _services.GetRequiredService<INotificationService>().Notify(new NotificationMessage(Brand.ProductName,
                "TICKSHIFT is still running in the notification area. Right-click its icon to exit.", NotificationKind.Info, "tray-hint"));
            return;
        }

        await ExitAsync();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            var trading = _services?.GetService<TradingModeService>();
            if (trading?.State == TradingModeState.Active)
            {
                // Always restore temporary Trading Mode settings before exiting.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await trading.EndAsync(timeout.Token);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Ending Trading Mode during exit failed; it will be recovered on next launch");
        }

        _window?.ForceClose();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_services is not null)
            {
                _logger?.LogInformation("{Product} exiting", Brand.ProductName);
                // DisposeAsync handles singletons that are only IAsyncDisposable (monitor, automation, Trading Mode).
                _services.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception)
        {
            // Never block shutdown.
        }

        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void HookExceptionHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            _logger?.LogError(e.Exception, "Unhandled UI exception");
            var kind = ErrorClassifier.Classify(e.Exception);
            _services?.GetService<INotificationService>()?.Notify(new NotificationMessage(Brand.ProductName,
                "Something went wrong: " + ErrorClassifier.FriendlyMessage(kind) + " No changes were left half-applied; see the Recovery Centre for details.", NotificationKind.Error));
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating={Terminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger?.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }
}

internal static class MetricsMonitorExtensions
{
    public static void SetBackground(this MetricsMonitor monitor, bool background) => monitor.BackgroundMode = background;
}
