using CommunityToolkit.Mvvm.ComponentModel;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Diagnostics;
using Tickshift.Diagnostics.Hardware;

namespace Tickshift.App.ViewModels;

/// <summary>Base for every page. Pages are created lazily on first visit and pause their updates while hidden.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _statusIsError;

    public abstract string Title { get; }

    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    public virtual void OnNavigatedFrom()
    {
    }

    protected void ShowResult(OperationResult result)
    {
        StatusMessage = result.Message;
        StatusIsError = result.Status == OperationStatus.Failed;
    }

    protected void ShowMessage(string message, bool isError = false)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }
}

/// <summary>Page keys used for navigation, deep links from recommendations, and keyboard shortcuts.</summary>
public static class PageKeys
{
    public const string Dashboard = "Dashboard";
    public const string Optimise = "Optimise";
    public const string Trading = "Trading";
    public const string Browser = "Browser";
    public const string Cleanup = "Cleanup";
    public const string Startup = "Startup";
    public const string Thermal = "Thermal";
    public const string Performance = "Performance";
    public const string Network = "Network";
    public const string Automation = "Automation";
    public const string Recovery = "Recovery";
    public const string Settings = "Settings";
}

public sealed class NavigationService
{
    public event EventHandler<string>? NavigationRequested;

    public string? PendingOptimisationFocus { get; set; }

    public void Navigate(string key) => NavigationRequested?.Invoke(this, key);
}

public sealed record SystemSummary(
    CpuInfo Cpu,
    CpuModel CpuModel,
    MemoryStatus Memory,
    IReadOnlyList<StorageVolume> Storage,
    OsInfo Os,
    PowerStatus Power,
    HardwareProfile Profile)
{
    public StorageVolume? SystemDrive => Storage.FirstOrDefault(s => s.IsSystemVolume);

    public string StorageDescription => SystemDrive is { } d
        ? $"{Format.Bytes(d.TotalBytes)} {d.MediaKind switch { StorageMediaKind.SolidState => d.BusType is "NVMe" ? "NVMe SSD" : d.BusType is "MMC" or "SD" ? "eMMC" : "SSD", StorageMediaKind.Rotational => "hard drive (HDD)", _ => "drive (type not reported)" }}"
        : "Unknown";
}

/// <summary>Caches slow-changing system information so pages do not re-query WMI on every visit.</summary>
public sealed class SystemSummaryService(ISystemInfoProvider info)
{
    private SystemSummary? _cached;
    private DateTimeOffset _at;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SystemSummary> GetAsync(bool refresh = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null && !refresh && DateTimeOffset.Now - _at < TimeSpan.FromMinutes(2))
                return _cached with { Memory = info.GetMemory() };

            var cpu = await info.GetCpuAsync(ct).ConfigureAwait(false);
            var memory = info.GetMemory();
            IReadOnlyList<StorageVolume> storage;
            try { storage = await info.GetStorageAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { storage = []; }
            PowerStatus power;
            try { power = await info.GetPowerAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { power = PowerStatus.Unknown; }

            var profile = HardwareInterpreter.Interpret(cpu, memory, storage);
            _cached = new SystemSummary(cpu, profile.Cpu, memory, storage, info.GetOs(), power, profile);
            _at = DateTimeOffset.Now;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
