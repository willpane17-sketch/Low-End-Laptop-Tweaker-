using Tickshift.Core.Models;
using Tickshift.Core.Settings;

namespace Tickshift.Core.Abstractions;

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

public interface ISettingsStore
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
    Task UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default);
    Task<AppSettings> ResetAsync(CancellationToken ct = default);
}

public enum NotificationKind { Info, Success, Warning, Error }

public sealed record NotificationMessage(string Title, string Body, NotificationKind Kind = NotificationKind.Info, string Category = "general");

public interface INotificationService
{
    /// <summary>Shows a notification, respecting user preferences and rate limits. Returns false when suppressed.</summary>
    bool Notify(NotificationMessage message);
}

/// <summary>Static and slow-changing system information.</summary>
public interface ISystemInfoProvider
{
    Task<CpuInfo> GetCpuAsync(CancellationToken ct = default);
    MemoryStatus GetMemory();
    Task<IReadOnlyList<StorageVolume>> GetStorageAsync(CancellationToken ct = default);
    OsInfo GetOs();
    Task<PowerStatus> GetPowerAsync(CancellationToken ct = default);
    TimeSpan GetUptime();
}

/// <summary>Instantaneous counters. CPU usage is computed from the delta since the previous call.</summary>
public interface ISystemSampler
{
    double SampleCpuPercent();
    double? SampleDiskActivePercent();
}

/// <summary>
/// Hardware sensor abstraction. Implementations MUST report <see cref="SensorAvailability.Unavailable"/>
/// rather than estimate a value. Third-party providers (for example a licensed hardware-monitoring
/// library) can be added by registering another implementation.
/// </summary>
public interface IThermalSensorProvider
{
    string Name { get; }
    Task<ThermalSnapshot> ReadAsync(CancellationToken ct = default);
}

public interface IProcessProvider
{
    /// <summary>Snapshot of processes; CPU percentages are relative to the previous call.</summary>
    IReadOnlyList<ProcessSnapshot> GetProcesses();

    /// <summary>Asks the application to close its main window (it can prompt to save work).</summary>
    OperationResult RequestGracefulClose(int pid);

    int GetProcessCount();
}

public interface IStartupProvider
{
    IReadOnlyList<StartupEntry> Enumerate();

    /// <summary>Reads the raw StartupApproved value (null when absent).</summary>
    byte[]? ReadApprovalValue(StartupEntry entry);

    /// <summary>Writes the StartupApproved value for a per-user entry (null deletes it).</summary>
    void WriteApprovalValue(StartupEntry entry, byte[]? value);
}

public interface IBrowserProvider
{
    IReadOnlyList<BrowserInstallation> DetectInstalled();
    BrowserAnalysis Analyse(BrowserKind kind, IReadOnlyList<ProcessSnapshot> processes);
    bool IsRunning(BrowserKind kind);
}

public interface IPowerPlanManager
{
    IReadOnlyList<PowerPlan> GetPlans();
    PowerPlan? GetActivePlan();
    void SetActivePlan(Guid planId);
}

/// <summary>Documented SystemParametersInfo animation settings.</summary>
public sealed record VisualEffectsState(
    bool ClientAreaAnimation,
    bool MenuAnimation,
    bool ComboBoxAnimation,
    bool ListBoxSmoothScrolling,
    bool TooltipAnimation,
    bool MinimizeMaximizeAnimation)
{
    public static VisualEffectsState Reduced { get; } = new(false, false, false, false, false, false);
    public bool AnyEnabled => ClientAreaAnimation || MenuAnimation || ComboBoxAnimation || ListBoxSmoothScrolling || TooltipAnimation || MinimizeMaximizeAnimation;
}

public interface IVisualEffectsManager
{
    VisualEffectsState GetCurrent();
    void Apply(VisualEffectsState state);
}

/// <summary>Opens a URI or settings page from a fixed allowlist only.</summary>
public interface ILinkLauncher
{
    OperationResult Open(string allowlistedKey);
    OperationResult OpenInBrowser(BrowserKind browser, string allowlistedKey);
}

public sealed record WifiInfo(string InterfaceName, int SignalQualityPercent, string? Band, int? LinkSpeedMbps);

public interface IWifiInfoProvider
{
    WifiInfo? GetCurrent();
}

public interface IElevationState
{
    bool IsElevated { get; }
}
