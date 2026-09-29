using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tickshift.App.Infrastructure;
using Tickshift.App.ViewModels;
using Tickshift.Core.Abstractions;
using Tickshift.Diagnostics;
using Tickshift.Diagnostics.Benchmarks;
using Tickshift.Diagnostics.Monitoring;
using Tickshift.Diagnostics.Network;
using Tickshift.Diagnostics.Recommendations;
using Tickshift.Hardware;
using Tickshift.Hardware.Sensors;
using Tickshift.Optimization;
using Tickshift.Optimization.Automation;
using Tickshift.Optimization.Optimisations;
using Tickshift.Optimization.Recovery;
using Tickshift.Optimization.Reports;
using Tickshift.Optimization.Startup;
using Tickshift.Persistence;
using Tickshift.Security;
using Tickshift.Trading;

namespace Tickshift.App.Composition;

/// <summary>
/// Composition root. Every system-touching component is registered behind an interface so the logic
/// modules can be tested with fakes and never perform real changes during tests.
/// </summary>
public static class ServiceRegistration
{
    public static ServiceProvider Build(AppDataPaths paths, int logRetentionDays)
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b
            .ClearProviders()
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new FileLoggerProvider(paths.LogDirectory, logRetentionDays)));

        // Core / persistence
        services.AddSingleton(paths);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<JsonSettingsStore>();
        services.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<JsonSettingsStore>());
        services.AddSingleton<IChangeJournal, ChangeJournal>();
        services.AddSingleton(new RecordStore<OptimisationRunRecord>(paths.RunHistoryFile, r => r.StartedAt, 200));
        services.AddSingleton(new RecordStore<AutomationEvent>(paths.AutomationHistoryFile, e => e.At, 500));
        services.AddSingleton(new RecordStore<LatencyResult>(paths.NetworkHistoryFile, r => r.At, 500));
        services.AddSingleton(new RecordStore<BenchmarkResult>(paths.BenchmarkHistoryFile, r => r.CapturedAt, 100));

        // Security
        services.AddSingleton<ISignatureVerifier>(new CachingSignatureVerifier(new AuthenticodeVerifier()));
        services.AddSingleton<IElevationState, WindowsElevationState>();
        services.AddSingleton<IElevatedOperationRunner, ElevatedHelperRunner>();

        // Hardware adapters (Windows)
        services.AddSingleton<IPowerPlanManager, WindowsPowerPlanManager>();
        services.AddSingleton<ISystemInfoProvider, WindowsSystemInfoProvider>();
        services.AddSingleton<ISystemSampler, WindowsSystemSampler>();
        services.AddSingleton<AcpiThermalZoneProvider>();
        services.AddSingleton<IThermalSensorProvider>(sp => new CompositeThermalSensorProvider([sp.GetRequiredService<AcpiThermalZoneProvider>()]));
        services.AddSingleton<IProcessProvider>(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsStore>();
            return new WindowsProcessProvider(() => new ProtectedApplicationMatcher(settings.Current.ProtectedApplications),
                sp.GetRequiredService<ILogger<WindowsProcessProvider>>());
        });
        services.AddSingleton<IStartupProvider, WindowsStartupProvider>();
        services.AddSingleton<IBrowserProvider, WindowsBrowserProvider>();
        services.AddSingleton<IVisualEffectsManager, WindowsVisualEffectsManager>();
        services.AddSingleton<ILinkLauncher, WindowsLinkLauncher>();
        services.AddSingleton<IWifiInfoProvider, WlanWifiInfoProvider>();
        services.AddSingleton<IAutostartManager, WindowsAutostartManager>();
        services.AddSingleton<ISystemRestoreInfo, WindowsSystemRestoreInfo>();
        services.AddSingleton<IRecycleBin, WindowsRecycleBin>();

        // Diagnostics
        services.AddSingleton(new PerformanceHistory());
        services.AddSingleton<MetricsMonitor>();
        services.AddSingleton<IMetricsSource>(sp => sp.GetRequiredService<MetricsMonitor>());
        services.AddSingleton(new RecommendationEngine());
        services.AddSingleton<DiagnosticEngine>();
        services.AddSingleton<BenchmarkService>();
        services.AddSingleton<NetworkDiagnosticsService>();

        // Optimisation (strict allowlist is enforced by the engine as well)
        services.AddSingleton<ICleanupLocations, WindowsCleanupLocations>();
        services.AddSingleton<IOptimisation, UserTempCleanupOptimisation>();
        services.AddSingleton<IOptimisation, ErrorReportCleanupOptimisation>();
        services.AddSingleton<IOptimisation, BrowserCacheCleanupOptimisation>();
        services.AddSingleton<IOptimisation, BalancedWhenPluggedInOptimisation>();
        services.AddSingleton<IOptimisation, HighPerformanceWhenPluggedInOptimisation>();
        services.AddSingleton<IOptimisation, VisualEffectsOptimisation>();
        services.AddSingleton<StartupManagerService>();
        services.AddSingleton(sp => new RecoveryService(
            sp.GetRequiredService<IChangeJournal>(),
            sp.GetServices<IOptimisation>().Cast<IChangeRollbackHandler>().Append(sp.GetRequiredService<StartupManagerService>()),
            sp.GetRequiredService<ILogger<RecoveryService>>()));
        services.AddSingleton<IRestorePointService, RestorePointService>();
        services.AddSingleton<OptimisationEngine>();
        services.AddSingleton<DiagnosticReportExporter>();
        services.AddSingleton<AutomationService>();

        // Trading
        services.AddSingleton<TradingSessionFlag>();
        services.AddSingleton<ITradingSessionState>(sp => sp.GetRequiredService<TradingSessionFlag>());
        services.AddSingleton<TradingModeService>();

        // UI services
        services.AddSingleton<ThemeService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<SystemSummaryService>();
        services.AddSingleton<RecommendationActions>();

        // View-models: singletons so page state survives navigation, but each is only created on first visit.
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<OptimiseViewModel>();
        services.AddSingleton<TradingViewModel>();
        services.AddSingleton<BrowserViewModel>();
        services.AddSingleton<CleanupViewModel>();
        services.AddSingleton<StartupViewModel>();
        services.AddSingleton<ThermalViewModel>();
        services.AddSingleton<PerformanceViewModel>();
        services.AddSingleton<NetworkViewModel>();
        services.AddSingleton<AutomationViewModel>();
        services.AddSingleton<RecoveryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<OnboardingViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
