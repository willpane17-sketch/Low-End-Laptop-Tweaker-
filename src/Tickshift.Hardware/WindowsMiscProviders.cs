using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Hardware.Native;
using Tickshift.Security;

namespace Tickshift.Hardware;

/// <summary>Opens only allowlisted Windows Settings pages, system tools, and browser settings pages.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLinkLauncher(IBrowserProvider browsers) : ILinkLauncher
{
    public OperationResult Open(string allowlistedKey)
    {
        try
        {
            if (LinkAllowlist.WindowsSettings.TryGetValue(allowlistedKey, out var uri))
            {
                using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return OperationResult.Success("Opened Windows Settings.");
            }

            if (LinkAllowlist.SystemTools.TryGetValue(allowlistedKey, out var exe))
            {
                var path = Path.Combine(Environment.SystemDirectory, exe);
                if (!File.Exists(path)) return OperationResult.NotApplicable("This Windows tool is not available on this device.");
                using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return OperationResult.Success("Opened the Windows tool.");
            }

            return OperationResult.Failure("This link is not on the allowlist.", Core.Errors.ErrorKind.InvalidConfiguration);
        }
        catch (Exception ex)
        {
            return OperationResult.FromException(ex, "Open settings");
        }
    }

    public OperationResult OpenInBrowser(BrowserKind browser, string allowlistedKey)
    {
        var pages = browser == BrowserKind.Edge ? LinkAllowlist.EdgePages : LinkAllowlist.ChromePages;
        if (!pages.TryGetValue(allowlistedKey, out var url))
            return OperationResult.Failure("This browser page is not on the allowlist.", Core.Errors.ErrorKind.InvalidConfiguration);

        var install = browsers.DetectInstalled().FirstOrDefault(b => b.Kind == browser);
        if (install is null) return OperationResult.NotApplicable("The browser is not installed.");

        try
        {
            var psi = new ProcessStartInfo(install.ExecutablePath) { UseShellExecute = false };
            psi.ArgumentList.Add(url);
            using var _ = Process.Start(psi);
            return OperationResult.Success($"Opened {url} in {install.DisplayName}.");
        }
        catch (Exception ex)
        {
            return OperationResult.FromException(ex, "Open browser settings");
        }
    }
}

/// <summary>Wi-Fi signal quality through the Native Wifi API. The network name is deliberately not read.</summary>
[SupportedOSPlatform("windows")]
public sealed class WlanWifiInfoProvider : IWifiInfoProvider
{
    private const int CurrentConnectionOpcode = 7;
    private const int InterfaceInfoSize = 532; // GUID (16) + WCHAR[256] (512) + state (4)
    private const int SignalQualityOffset = 576;
    private const int RxRateOffset = 580;

    public WifiInfo? GetCurrent()
    {
        IntPtr client = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            if (NativeMethods.WlanOpenHandle(2, IntPtr.Zero, out _, out client) != 0) return null;
            if (NativeMethods.WlanEnumInterfaces(client, IntPtr.Zero, out list) != 0) return null;

            var count = Marshal.ReadInt32(list, 0);
            for (var i = 0; i < count; i++)
            {
                var itemPtr = list + 8 + i * InterfaceInfoSize;
                var guid = Marshal.PtrToStructure<Guid>(itemPtr);
                var description = Marshal.PtrToStringUni(itemPtr + 16) ?? "Wi-Fi";
                var state = Marshal.ReadInt32(itemPtr + 16 + 512);
                if (state != 1) continue; // wlan_interface_state_connected

                if (NativeMethods.WlanQueryInterface(client, guid, CurrentConnectionOpcode, IntPtr.Zero, out var size, out var data, IntPtr.Zero) != 0) continue;
                try
                {
                    if (size < RxRateOffset + 4) continue;
                    var quality = Marshal.ReadInt32(data, SignalQualityOffset);
                    var rxKbps = Marshal.ReadInt32(data, RxRateOffset);
                    return new WifiInfo(description, Math.Clamp(quality, 0, 100), null, rxKbps > 0 ? rxKbps / 1000 : null);
                }
                finally
                {
                    NativeMethods.WlanFreeMemory(data);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null; // WLAN AutoConfig service or wlanapi.dll unavailable (e.g. desktop without Wi-Fi)
        }
        finally
        {
            if (list != IntPtr.Zero) NativeMethods.WlanFreeMemory(list);
            if (client != IntPtr.Zero) NativeMethods.WlanCloseHandle(client, IntPtr.Zero);
        }
    }
}

public sealed record RecycleBinInfo(long SizeBytes, long ItemCount);

public interface IRecycleBin
{
    RecycleBinInfo? Query();
    OperationResult Empty();
}

/// <summary>
/// Recycle Bin via the documented shell API. Emptying is irreversible and is therefore only ever
/// offered as an explicit, separately confirmed action — never as part of a preset or automation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBin : IRecycleBin
{
    public RecycleBinInfo? Query()
    {
        int hr;
        if (IntPtr.Size == 8)
        {
            var info = new NativeMethods.ShQueryRbInfo64 { cbSize = Marshal.SizeOf<NativeMethods.ShQueryRbInfo64>() };
            hr = NativeMethods.SHQueryRecycleBin64(null, ref info);
            return hr == 0 ? new RecycleBinInfo(info.i64Size, info.i64NumItems) : null;
        }
        else
        {
            var info = new NativeMethods.ShQueryRbInfo32 { cbSize = Marshal.SizeOf<NativeMethods.ShQueryRbInfo32>() };
            hr = NativeMethods.SHQueryRecycleBin32(null, ref info);
            return hr == 0 ? new RecycleBinInfo(info.i64Size, info.i64NumItems) : null;
        }
    }

    public OperationResult Empty()
    {
        var before = Query();
        if (before is { ItemCount: 0 }) return OperationResult.NotApplicable("The Recycle Bin is already empty.");
        var hr = NativeMethods.SHEmptyRecycleBinW(IntPtr.Zero, null, NativeMethods.SherbNoConfirmation | NativeMethods.SherbNoProgressUi | NativeMethods.SherbNoSound);
        var after = Query();
        if (hr != 0 && after is not { ItemCount: 0 })
            return OperationResult.Failure("Windows could not empty the Recycle Bin.", Core.Errors.ErrorKind.Unknown, $"HRESULT 0x{hr:X8}");
        return after is { ItemCount: 0 }
            ? OperationResult.Success($"Recycle Bin emptied ({Format.Bytes(before?.SizeBytes ?? 0)} freed).")
            : OperationResult.Failure("The Recycle Bin could not be verified as empty.");
    }
}
