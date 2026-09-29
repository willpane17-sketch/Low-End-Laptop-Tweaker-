namespace Tickshift.Security;

/// <summary>
/// The only URIs TICKSHIFT will ever open. Nothing derived from user input is launched.
/// ms-settings pages are documented Windows Settings deep links; browser pages are opened by
/// launching the detected browser executable with the fixed internal URL.
/// </summary>
public static class LinkAllowlist
{
    public static readonly IReadOnlyDictionary<string, string> WindowsSettings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["startup-apps"] = "ms-settings:startupapps",
        ["storage-sense"] = "ms-settings:storagesense",
        ["storage"] = "ms-settings:storagepolicies",
        ["power"] = "ms-settings:powersleep",
        ["battery"] = "ms-settings:batterysaver",
        ["background-apps"] = "ms-settings:privacy-backgroundapps",
        ["apps"] = "ms-settings:appsfeatures",
        ["windows-update"] = "ms-settings:windowsupdate",
        ["network-status"] = "ms-settings:network-status",
        ["wifi"] = "ms-settings:network-wifi",
        ["recovery"] = "ms-settings:recovery",
        ["notifications"] = "ms-settings:notifications",
        ["visual-effects"] = "ms-settings:easeofaccess-visualeffects",
        ["windows-security"] = "windowsdefender:",
    };

    public static readonly IReadOnlyDictionary<string, string> EdgePages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["performance"] = "edge://settings/system",
        ["extensions"] = "edge://extensions",
        ["about"] = "edge://settings/help",
        ["startup"] = "edge://settings/startHomeNTP",
        ["clear-data"] = "edge://settings/clearBrowserData",
    };

    public static readonly IReadOnlyDictionary<string, string> ChromePages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["performance"] = "chrome://settings/performance",
        ["system"] = "chrome://settings/system",
        ["extensions"] = "chrome://extensions",
        ["about"] = "chrome://settings/help",
        ["startup"] = "chrome://settings/onStartup",
        ["clear-data"] = "chrome://settings/clearBrowserData",
    };

    /// <summary>System executables that open classic settings dialogs. Resolved under %WINDIR%\System32 only.</summary>
    public static readonly IReadOnlyDictionary<string, string> SystemTools = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["performance-options"] = "SystemPropertiesPerformance.exe",
        ["system-protection"] = "SystemPropertiesProtection.exe",
        ["disk-cleanup"] = "cleanmgr.exe",
        ["resource-monitor"] = "resmon.exe",
    };
}
