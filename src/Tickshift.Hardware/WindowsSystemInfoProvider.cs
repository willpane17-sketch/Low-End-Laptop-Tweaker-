using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Hardware.Native;

namespace Tickshift.Hardware;

/// <summary>
/// Reads hardware and OS information through documented Windows APIs, the registry and WMI.
/// Every optional source degrades to "unknown" rather than guessing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemInfoProvider : ISystemInfoProvider
{
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(8);
    private readonly ILogger _logger;
    private readonly IPowerPlanManager _powerPlans;
    private CpuInfo? _cpu;
    private OsInfo? _os;
    private Dictionary<char, (StorageMediaKind Kind, string? Bus)>? _mediaCache;
    private BatteryHealth? _batteryHealth;
    private bool _batteryHealthQueried;

    public WindowsSystemInfoProvider(IPowerPlanManager powerPlans, ILogger<WindowsSystemInfoProvider>? logger = null)
    {
        _powerPlans = powerPlans;
        _logger = logger ?? NullLogger<WindowsSystemInfoProvider>.Instance;
    }

    public async Task<CpuInfo> GetCpuAsync(CancellationToken ct = default)
    {
        if (_cpu is not null) return _cpu;

        string name = "Unknown processor";
        string vendor = "Unknown";
        int? mhz = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? name;
            vendor = (key?.GetValue("VendorIdentifier") as string)?.Trim() ?? vendor;
            if (key?.GetValue("~MHz") is int m) mhz = m;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CPU registry read failed");
        }

        var physical = 0;
        var logical = Environment.ProcessorCount;
        try
        {
            var rows = await QueryWmiAsync(@"root\cimv2", "SELECT NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor", ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                physical += Convert.ToInt32(row["NumberOfCores"] ?? 0);
                if (row["MaxClockSpeed"] is uint max && max > 0) mhz = (int)max;
            }

            var logicalFromWmi = rows.Sum(r => Convert.ToInt32(r["NumberOfLogicalProcessors"] ?? 0));
            if (logicalFromWmi > 0) logical = logicalFromWmi;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("WMI processor query unavailable: {Message}", ex.Message);
        }

        vendor = vendor switch
        {
            "GenuineIntel" => "Intel",
            "AuthenticAMD" => "AMD",
            _ => vendor,
        };

        _cpu = new CpuInfo(name, vendor, physical, logical, mhz);
        return _cpu;
    }

    public MemoryStatus GetMemory()
    {
        var status = new NativeMethods.MemoryStatusEx { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            return new MemoryStatus(0, 0, 0, 0, 0);

        return new MemoryStatus(
            status.ullTotalPhys,
            status.ullAvailPhys,
            status.dwMemoryLoad,
            status.ullTotalPageFile - status.ullAvailPageFile,
            status.ullTotalPageFile);
    }

    public async Task<IReadOnlyList<StorageVolume>> GetStorageAsync(CancellationToken ct = default)
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        _mediaCache ??= await DetectMediaAsync(ct).ConfigureAwait(false);

        var result = new List<StorageVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var letter = char.ToUpperInvariant(drive.Name[0]);
                var media = _mediaCache.TryGetValue(letter, out var m) ? m : (StorageMediaKind.Unknown, null);
                result.Add(new StorageVolume(
                    drive.Name,
                    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel,
                    drive.DriveFormat,
                    (ulong)drive.TotalSize,
                    (ulong)drive.AvailableFreeSpace,
                    string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase),
                    media.Item1,
                    media.Item2));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug("Skipping drive {Drive}: {Message}", drive.Name, ex.Message);
            }
        }

        return result.OrderByDescending(v => v.IsSystemVolume).ThenBy(v => v.RootPath).ToList();
    }

    /// <summary>
    /// Uses the Storage Management API (MSFT_PhysicalDisk). MediaType 3 = HDD, 4 = SSD. Many older or
    /// virtualised disks report 0 (unspecified); we then say "unknown" instead of guessing.
    /// </summary>
    private async Task<Dictionary<char, (StorageMediaKind, string?)>> DetectMediaAsync(CancellationToken ct)
    {
        var map = new Dictionary<char, (StorageMediaKind, string?)>();
        try
        {
            var disks = await QueryWmiAsync(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk", ct).ConfigureAwait(false);
            var diskInfo = new Dictionary<uint, (StorageMediaKind, string?)>();
            foreach (var d in disks)
            {
                if (!uint.TryParse(d["DeviceId"]?.ToString(), out var id)) continue;
                var kind = Convert.ToInt32(d["MediaType"] ?? 0) switch
                {
                    3 => StorageMediaKind.Rotational,
                    4 => StorageMediaKind.SolidState,
                    _ => StorageMediaKind.Unknown,
                };
                var bus = Convert.ToInt32(d["BusType"] ?? 0) switch
                {
                    3 => "ATA",
                    7 => "USB",
                    8 => "RAID",
                    10 => "SAS",
                    11 => "SATA",
                    12 => "SD",
                    13 => "MMC",
                    17 => "NVMe",
                    _ => null,
                };

                // eMMC storage (common on budget laptops) is solid-state but slow; NVMe is always solid-state.
                if (kind == StorageMediaKind.Unknown && bus is "NVMe" or "MMC" or "SD") kind = StorageMediaKind.SolidState;
                diskInfo[id] = (kind, bus);
            }

            var partitions = await QueryWmiAsync(@"root\Microsoft\Windows\Storage", "SELECT DriveLetter, DiskNumber FROM MSFT_Partition", ct).ConfigureAwait(false);
            foreach (var p in partitions)
            {
                var letterObj = p["DriveLetter"];
                if (letterObj is null) continue;
                var letter = Convert.ToChar(letterObj);
                if (!char.IsLetter(letter)) continue;
                var diskNumber = Convert.ToUInt32(p["DiskNumber"] ?? uint.MaxValue);
                if (diskInfo.TryGetValue(diskNumber, out var info)) map[char.ToUpperInvariant(letter)] = info;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("Storage media type detection unavailable: {Message}", ex.Message);
        }

        return map;
    }

    public OsInfo GetOs()
    {
        if (_os is not null) return _os;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var display = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string;
            var build = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out var b) ? b : Environment.OSVersion.Version.Build;
            var ubr = key?.GetValue("UBR") is int u ? u : 0;
            var edition = key?.GetValue("EditionID") as string ?? "Unknown";
            _os = new OsInfo(product, display, build, ubr, edition, Environment.Is64BitOperatingSystem);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OS version read failed");
            _os = OsInfo.Unknown with { BuildNumber = Environment.OSVersion.Version.Build };
        }

        return _os;
    }

    public async Task<PowerStatus> GetPowerAsync(CancellationToken ct = default)
    {
        PowerSource source = PowerSource.Unknown;
        var hasBattery = false;
        int? percent = null;
        var saver = false;

        if (NativeMethods.GetSystemPowerStatus(out var s))
        {
            source = s.ACLineStatus switch { 0 => PowerSource.Battery, 1 => PowerSource.Mains, _ => PowerSource.Unknown };
            hasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255; // 128 = no system battery, 255 = unknown
            percent = s.BatteryLifePercent <= 100 && hasBattery ? s.BatteryLifePercent : null;
            saver = s.SystemStatusFlag == 1;
        }

        PowerPlan? plan = null;
        try { plan = _powerPlans.GetActivePlan(); }
        catch (Exception ex) { _logger.LogDebug("Active power plan unavailable: {Message}", ex.Message); }

        if (hasBattery && !_batteryHealthQueried)
        {
            _batteryHealthQueried = true;
            _batteryHealth = await ReadBatteryHealthAsync(ct).ConfigureAwait(false);
        }

        return new PowerStatus(source, hasBattery, percent, saver, plan, _batteryHealth);
    }

    private async Task<BatteryHealth?> ReadBatteryHealthAsync(CancellationToken ct)
    {
        try
        {
            var design = await QueryWmiAsync(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData", ct).ConfigureAwait(false);
            var full = await QueryWmiAsync(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", ct).ConfigureAwait(false);
            var designCap = design.Select(d => Convert.ToUInt32(d["DesignedCapacity"] ?? 0u)).FirstOrDefault();
            var fullCap = full.Select(d => Convert.ToUInt32(d["FullChargedCapacity"] ?? 0u)).FirstOrDefault();
            if (designCap == 0 || fullCap == 0) return null;
            // Some firmware reports nonsense (full > 150% of design); treat that as unavailable.
            if (fullCap > designCap * 1.5) return null;
            return new BatteryHealth(designCap, fullCap);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("Battery health unavailable: {Message}", ex.Message);
            return null;
        }
    }

    public TimeSpan GetUptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Runs a WMI query off the UI thread with a hard timeout and copies values out before disposing COM objects.</summary>
    internal static async Task<List<Dictionary<string, object?>>> QueryWmiAsync(string scope, string query, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var options = new System.Management.EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = false, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), options);
            using var collection = searcher.Get();
            var rows = new List<Dictionary<string, object?>>();
            foreach (var obj in collection)
            {
                using (obj)
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in obj.Properties) row[p.Name] = p.Value;
                    rows.Add(row);
                }
            }

            return rows;
        }, ct).WaitAsync(WmiTimeout + TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
    }
}
