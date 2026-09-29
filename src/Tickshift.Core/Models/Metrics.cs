namespace Tickshift.Core.Models;

/// <summary>One live measurement. Nullable fields are genuinely unavailable, never estimated.</summary>
public sealed record MetricSample(
    DateTimeOffset Timestamp,
    double CpuPercent,
    double MemoryPercent,
    ulong AvailableMemoryBytes,
    double? DiskActivePercent,
    double? TemperatureCelsius);

public enum ProcessCategory
{
    Unknown,
    /// <summary>Kernel or core Windows process that must never be closed.</summary>
    Critical,
    /// <summary>Other Windows component; closing is not offered.</summary>
    WindowsComponent,
    /// <summary>Antivirus or security software; never closed.</summary>
    Security,
    /// <summary>User-designated trading application; never closed automatically.</summary>
    ProtectedTrading,
    Browser,
    Application,
    Self,
}

public sealed record ProcessSnapshot(
    int Pid,
    string Name,
    string? Path,
    string? Publisher,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    double? DiskBytesPerSecond,
    bool HasVisibleWindow,
    int SessionId,
    ProcessCategory Category)
{
    public bool IsCloseable => Category is ProcessCategory.Application or ProcessCategory.Browser or ProcessCategory.Unknown && SessionId != 0;
}
