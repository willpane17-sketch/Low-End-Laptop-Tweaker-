using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Hardware.Native;

namespace Tickshift.Hardware;

/// <summary>Power schemes through the documented Power Management API (powrprof.dll). No powercfg shelling.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerPlanManager : IPowerPlanManager
{
    public IReadOnlyList<PowerPlan> GetPlans()
    {
        var plans = new List<PowerPlan>();
        var guidSize = (uint)Marshal.SizeOf<Guid>();
        var buffer = Marshal.AllocHGlobal((int)guidSize);
        try
        {
            for (uint index = 0; index < 64; index++)
            {
                var size = guidSize;
                var rc = NativeMethods.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeMethods.AccessScheme, index, buffer, ref size);
                if (rc != 0) break; // ERROR_NO_MORE_ITEMS
                var id = Marshal.PtrToStructure<Guid>(buffer);
                plans.Add(new PowerPlan(id, ReadName(id)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return plans;
    }

    public PowerPlan? GetActivePlan()
    {
        if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
        try
        {
            var id = Marshal.PtrToStructure<Guid>(ptr);
            return new PowerPlan(id, ReadName(id));
        }
        finally
        {
            NativeMethods.LocalFree(ptr);
        }
    }

    public void SetActivePlan(Guid planId)
    {
        if (!GetPlans().Any(p => p.Id == planId))
            throw new InvalidOperationException("The requested power plan does not exist on this device.");
        var rc = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, planId);
        if (rc != 0) throw new Win32Exception((int)rc);
    }

    private static string ReadName(Guid id)
    {
        uint size = 0;
        NativeMethods.PowerReadFriendlyName(IntPtr.Zero, id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0 || size > 4096) return KnownName(id);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return NativeMethods.PowerReadFriendlyName(IntPtr.Zero, id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                ? Marshal.PtrToStringUni(buffer) ?? KnownName(id)
                : KnownName(id);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string KnownName(Guid id) =>
        id == PowerPlan.BalancedId ? "Balanced" :
        id == PowerPlan.HighPerformanceId ? "High performance" :
        id == PowerPlan.PowerSaverId ? "Power saver" :
        id == PowerPlan.UltimatePerformanceId ? "Ultimate Performance" : "Custom plan";
}

/// <summary>
/// Animation settings via documented SystemParametersInfo actions — the same switches as
/// Settings › Accessibility › Visual effects and the classic Performance Options dialog.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVisualEffectsManager : IVisualEffectsManager
{
    private const uint SpiGetAnimation = 0x0048, SpiSetAnimation = 0x0049;
    private const uint SpiGetMenuAnimation = 0x1002, SpiSetMenuAnimation = 0x1003;
    private const uint SpiGetComboBoxAnimation = 0x1004, SpiSetComboBoxAnimation = 0x1005;
    private const uint SpiGetListBoxSmoothScrolling = 0x1006, SpiSetListBoxSmoothScrolling = 0x1007;
    private const uint SpiGetTooltipAnimation = 0x1016, SpiSetTooltipAnimation = 0x1017;
    private const uint SpiGetClientAreaAnimation = 0x1042, SpiSetClientAreaAnimation = 0x1043;

    public VisualEffectsState GetCurrent() => new(
        GetBool(SpiGetClientAreaAnimation),
        GetBool(SpiGetMenuAnimation),
        GetBool(SpiGetComboBoxAnimation),
        GetBool(SpiGetListBoxSmoothScrolling),
        GetBool(SpiGetTooltipAnimation),
        GetMinAnimate());

    public void Apply(VisualEffectsState state)
    {
        SetBool(SpiSetClientAreaAnimation, state.ClientAreaAnimation);
        SetBool(SpiSetMenuAnimation, state.MenuAnimation);
        SetBool(SpiSetComboBoxAnimation, state.ComboBoxAnimation);
        SetBool(SpiSetListBoxSmoothScrolling, state.ListBoxSmoothScrolling);
        SetBool(SpiSetTooltipAnimation, state.TooltipAnimation);
        SetMinAnimate(state.MinimizeMaximizeAnimation);
    }

    private static bool GetBool(uint action)
    {
        if (!NativeMethods.SystemParametersInfoGetBool(action, 0, out var value, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return value != 0;
    }

    private static void SetBool(uint action, bool value)
    {
        if (!NativeMethods.SystemParametersInfoSetBool(action, 0, value ? 1 : 0, NativeMethods.SpifUpdateIniFile | NativeMethods.SpifSendChange))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static bool GetMinAnimate()
    {
        var info = new NativeMethods.AnimationInfo { cbSize = (uint)Marshal.SizeOf<NativeMethods.AnimationInfo>() };
        if (!NativeMethods.SystemParametersInfoAnimation(SpiGetAnimation, info.cbSize, ref info, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.iMinAnimate != 0;
    }

    private static void SetMinAnimate(bool value)
    {
        var info = new NativeMethods.AnimationInfo { cbSize = (uint)Marshal.SizeOf<NativeMethods.AnimationInfo>(), iMinAnimate = value ? 1 : 0 };
        if (!NativeMethods.SystemParametersInfoAnimation(SpiSetAnimation, info.cbSize, ref info, NativeMethods.SpifUpdateIniFile | NativeMethods.SpifSendChange))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}

/// <summary>"Launch with Windows" via a visible HKCU Run value that appears in Task Manager and Startup Manager.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAutostartManager : IAutostartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Brand.AutostartValueName) is string;
    }

    public OperationResult SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return OperationResult.Failure("Could not determine the application path.");
                key.SetValue(Brand.AutostartValueName, $"\"{exe}\" --autostart", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(Brand.AutostartValueName, throwOnMissingValue: false);
            }

            return IsEnabled() == enabled
                ? OperationResult.Success(enabled ? "TICKSHIFT will start with Windows." : "TICKSHIFT will no longer start with Windows.")
                : OperationResult.Failure("The setting could not be verified after writing it.");
        }
        catch (Exception ex)
        {
            return OperationResult.FromException(ex, "Launch with Windows");
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsSystemRestoreInfo : ISystemRestoreInfo
{
    public SystemRestoreStatus GetStatus()
    {
        try
        {
            using var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore");
            if (policy?.GetValue("DisableSR") is int d && d == 1)
                return new SystemRestoreStatus(false, true, "System Restore is disabled by your organisation's policy.");

            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            if (key is null) return new SystemRestoreStatus(null, false, "System Restore status could not be determined.");

            if (key.GetValue("RPSessionInterval") is int interval)
            {
                return interval >= 1
                    ? new SystemRestoreStatus(true, false, "System Protection is turned on. Creating a restore point needs administrator approval.")
                    : new SystemRestoreStatus(false, false, "System Protection is turned off for all drives. You can turn it on in System Properties › System Protection.");
            }

            return new SystemRestoreStatus(null, false, "System Restore status could not be determined. TICKSHIFT's own recovery records still apply.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new SystemRestoreStatus(null, false, "System Restore status could not be read.");
        }
    }
}
