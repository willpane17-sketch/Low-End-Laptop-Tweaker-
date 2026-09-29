namespace Tickshift.Core.Models;

public sealed record CpuInfo(
    string Name,
    string Manufacturer,
    int PhysicalCores,
    int LogicalProcessors,
    int? BaseClockMHz)
{
    public static CpuInfo Unknown { get; } = new("Unknown processor", "Unknown", 0, Environment.ProcessorCount, null);
}

public sealed record MemoryStatus(
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    uint MemoryLoadPercent,
    ulong CommitTotalBytes,
    ulong CommitLimitBytes)
{
    public ulong UsedPhysicalBytes => TotalPhysicalBytes - Math.Min(TotalPhysicalBytes, AvailablePhysicalBytes);
    public double TotalGiB => TotalPhysicalBytes / 1024d / 1024 / 1024;

    /// <summary>Commit charge as a share of the commit limit; a high value means paging pressure.</summary>
    public double CommitPercent => CommitLimitBytes == 0 ? 0 : CommitTotalBytes * 100d / CommitLimitBytes;
}

public enum StorageMediaKind { Unknown, SolidState, Rotational }

public sealed record StorageVolume(
    string RootPath,
    string Label,
    string FileSystem,
    ulong TotalBytes,
    ulong FreeBytes,
    bool IsSystemVolume,
    StorageMediaKind MediaKind,
    string? BusType)
{
    public double FreePercent => TotalBytes == 0 ? 0 : FreeBytes * 100d / TotalBytes;
}

public sealed record OsInfo(
    string ProductName,
    string? DisplayVersion,
    int BuildNumber,
    int Revision,
    string Edition,
    bool Is64Bit)
{
    public bool IsWindows11 => BuildNumber >= 22000;

    /// <summary>Windows 10 1809 (17763) is the oldest build we consider supported.</summary>
    public bool IsSupported => BuildNumber >= 17763;

    public string FriendlyName
    {
        get
        {
            // The registry ProductName still says "Windows 10" on Windows 11, so derive from the build.
            var family = IsWindows11 ? ProductName.Replace("Windows 10", "Windows 11", StringComparison.Ordinal) : ProductName;
            return DisplayVersion is { Length: > 0 } ? $"{family} {DisplayVersion} (build {BuildNumber}.{Revision})" : $"{family} (build {BuildNumber}.{Revision})";
        }
    }

    public static OsInfo Unknown { get; } = new("Windows", null, 0, 0, "Unknown", Environment.Is64BitOperatingSystem);
}

public enum PowerSource { Unknown, Mains, Battery }

public sealed record PowerPlan(Guid Id, string Name)
{
    public static readonly Guid PowerSaverId = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid BalancedId = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformanceId = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimatePerformanceId = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
}

public sealed record BatteryHealth(uint DesignCapacityMWh, uint FullChargeCapacityMWh)
{
    public double? HealthPercent => DesignCapacityMWh == 0 ? null : Math.Round(FullChargeCapacityMWh * 100d / DesignCapacityMWh, 1);
}

public sealed record PowerStatus(
    PowerSource Source,
    bool HasBattery,
    int? BatteryPercent,
    bool BatterySaverOn,
    PowerPlan? ActivePlan,
    BatteryHealth? Health)
{
    public static PowerStatus Unknown { get; } = new(PowerSource.Unknown, false, null, false, null, null);

    public string Describe() => Source switch
    {
        PowerSource.Mains when HasBattery => BatteryPercent is { } p ? $"Plugged in · {p}%" : "Plugged in",
        PowerSource.Mains => "Mains power",
        PowerSource.Battery => BatteryPercent is { } p ? $"On battery · {p}%" + (BatterySaverOn ? " · saver on" : string.Empty) : "On battery",
        _ => "Power state unknown",
    };
}
