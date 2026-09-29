using Tickshift.Core.Models;

namespace Tickshift.Core.Settings;

public enum ProtectedAppKind { TradingTerminal, Browser, Messaging, BrokerTerminal, Custom }

/// <summary>An application the user wants protected from closure and prioritised in Trading Mode.</summary>
public sealed record ProtectedApplication
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>Executable file name, e.g. "terminal64.exe".</summary>
    public required string ExecutableName { get; init; }
    /// <summary>Optional full path for precise matching of custom executables.</summary>
    public string? ExecutablePath { get; init; }
    public ProtectedAppKind Kind { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>A website the user relies on while trading (e.g. tradingview.com). Stored only as a domain.</summary>
public sealed record ProtectedWebsite(string Domain, string? Label = null);

/// <summary>A user-chosen host used for connectivity checks. Never contains credentials.</summary>
public sealed record NetworkEndpoint(string Name, string Host, int? TcpPort = null);

public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;
    public bool MemoryPressure { get; set; } = true;
    public bool Thermal { get; set; } = true;
    public bool Storage { get; set; } = true;
    public bool TradingMode { get; set; } = true;
    public bool Recommendations { get; set; } = true;
}

public sealed class AutomationSettings
{
    public bool Enabled { get; set; }
    public bool Paused { get; set; }
    public bool OptimiseAtLogin { get; set; }
    public bool DailyDiagnostic { get; set; } = true;
    public bool StorageReminder { get; set; } = true;
    public bool MemoryPressureAlerts { get; set; } = true;
    public bool ThermalMonitoring { get; set; } = true;
    public bool BrowserRecommendations { get; set; } = true;
    public bool BottleneckDetection { get; set; } = true;

    /// <summary>Optimisation IDs the user individually approved for unattended use. Only low-risk, reversible ones are honoured.</summary>
    public List<string> ApprovedUnattendedOptimisations { get; set; } = [];

    public int MemoryPressureThresholdPercent { get; set; } = 88;
    public int SustainedMinutes { get; set; } = 5;
    public int LowStoragePercent { get; set; } = 10;
    public int CpuBottleneckPercent { get; set; } = 85;
    public TimeOnly DailyDiagnosticTime { get; set; } = new(9, 0);
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // Appearance
    public ThemePreference Theme { get; set; } = ThemePreference.Dark;
    public bool CompactMode { get; set; }
    public bool ReducedMotion { get; set; }
    public double UiScale { get; set; } = 1.0;

    // Behaviour
    public bool LaunchWithWindows { get; set; }
    public bool StartMinimised { get; set; }
    public bool MinimiseToTray { get; set; } = true;
    public int RefreshIntervalSeconds { get; set; } = 2;
    public OptimisationPreset DefaultPreset { get; set; } = OptimisationPreset.Safe;
    public bool DryRunMode { get; set; }
    public bool OfferRestorePoint { get; set; } = true;

    // Onboarding
    public bool OnboardingCompleted { get; set; }
    public PrimaryUse PrimaryUse { get; set; } = PrimaryUse.Balanced;

    // Trading
    public List<ProtectedApplication> ProtectedApplications { get; set; } = [];
    public List<ProtectedWebsite> ProtectedWebsites { get; set; } = [];
    public bool TradingModeAdjustPowerPlan { get; set; } = true;
    public bool TradingModeReduceAnimations { get; set; } = true;
    public int TradingMonitorIntervalSeconds { get; set; } = 10;

    // Network
    public List<NetworkEndpoint> NetworkEndpoints { get; set; } = [];

    public NotificationSettings Notifications { get; set; } = new();
    public AutomationSettings Automation { get; set; } = new();

    // Data retention
    public int DataRetentionDays { get; set; } = 30;

    public DateTimeOffset? LastOptimisedAt { get; set; }

    /// <summary>
    /// Clamps every value to a safe range so a hand-edited or corrupted file can never
    /// drive the app into an unsafe state (e.g. 0 ms polling).
    /// Returns true when anything had to be corrected.
    /// </summary>
    public bool Normalise()
    {
        var changed = false;

        T Clamp<T>(T value, T min, T max) where T : IComparable<T>
        {
            var result = value.CompareTo(min) < 0 ? min : value.CompareTo(max) > 0 ? max : value;
            if (!EqualityComparer<T>.Default.Equals(result, value)) changed = true;
            return result;
        }

        if (!Enum.IsDefined(Theme)) { Theme = ThemePreference.Dark; changed = true; }
        if (!Enum.IsDefined(PrimaryUse)) { PrimaryUse = PrimaryUse.Balanced; changed = true; }
        if (DefaultPreset is not (OptimisationPreset.Safe or OptimisationPreset.Balanced or OptimisationPreset.Trading))
        {
            DefaultPreset = OptimisationPreset.Safe;
            changed = true;
        }

        if (double.IsNaN(UiScale) || double.IsInfinity(UiScale)) { UiScale = 1.0; changed = true; }
        UiScale = Clamp(UiScale, 0.8, 1.5);
        RefreshIntervalSeconds = Clamp(RefreshIntervalSeconds, 1, 30);
        TradingMonitorIntervalSeconds = Clamp(TradingMonitorIntervalSeconds, 5, 60);
        DataRetentionDays = Clamp(DataRetentionDays, 1, 365);

        Notifications ??= new NotificationSettings();
        Automation ??= new AutomationSettings();
        Automation.ApprovedUnattendedOptimisations ??= [];
        Automation.MemoryPressureThresholdPercent = Clamp(Automation.MemoryPressureThresholdPercent, 60, 98);
        Automation.SustainedMinutes = Clamp(Automation.SustainedMinutes, 1, 60);
        Automation.LowStoragePercent = Clamp(Automation.LowStoragePercent, 3, 40);
        Automation.CpuBottleneckPercent = Clamp(Automation.CpuBottleneckPercent, 50, 99);

        ProtectedApplications ??= [];
        ProtectedWebsites ??= [];
        NetworkEndpoints ??= [];

        var apps = ProtectedApplications
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.ExecutableName) && !string.IsNullOrWhiteSpace(a.Id))
            .DistinctBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (apps.Count != ProtectedApplications.Count) { ProtectedApplications = apps; changed = true; }

        var sites = ProtectedWebsites.Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Domain))
            .DistinctBy(s => s.Domain, StringComparer.OrdinalIgnoreCase).ToList();
        if (sites.Count != ProtectedWebsites.Count) { ProtectedWebsites = sites; changed = true; }

        var endpoints = NetworkEndpoints.Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Host) && e.TcpPort is null or (> 0 and < 65536))
            .Take(20).ToList();
        if (endpoints.Count != NetworkEndpoints.Count) { NetworkEndpoints = endpoints; changed = true; }

        if (SchemaVersion != CurrentSchemaVersion) { SchemaVersion = CurrentSchemaVersion; changed = true; }
        return changed;
    }

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, SettingsJson.Options);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsJson.Options)!;
    }
}

public static class SettingsJson
{
    public static System.Text.Json.JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
