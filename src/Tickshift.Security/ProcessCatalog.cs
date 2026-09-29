namespace Tickshift.Security;

/// <summary>
/// Processes TICKSHIFT will never offer to close. The lists are intentionally conservative:
/// a false "protected" costs nothing, a false "closeable" could destabilise Windows or weaken security.
/// </summary>
public static class ProcessCatalog
{
    public static readonly IReadOnlySet<string> Critical = Set(
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "memory compression", "secure system", "fontdrvhost", "dwm", "lsm", "sihost", "ctfmon",
        "conhost", "spoolsv", "audiodg", "wudfhost", "dllhost", "taskhostw", "runtimebroker", "explorer",
        "startmenuexperiencehost", "shellexperiencehost", "searchhost", "searchapp", "textinputhost",
        "applicationframehost", "systemsettings", "useroobebroker", "logonui", "userinit", "wlanext",
        "trustedinstaller", "tiworker", "msiexec", "wmiprvse", "unsecapp", "dashost", "igfxem", "igfxcuiservice",
        "backgroundtaskhost", "lockapp", "crss", "ntoskrnl", "smartscreen", "consent", "searchindexer",
        "searchprotocolhost", "searchfilterhost", "wermgr", "werfault", "musnotification", "usocoreworker",
        "tabtip", "securityhealthhost", "pwsh", "cmd", "powershell", "windowsterminal", "openconsole");

    /// <summary>Microsoft Defender plus widely used third-party security products.</summary>
    public static readonly IReadOnlySet<string> Security = Set(
        "msmpeng", "nissrv", "mpdefendercoreservice", "securityhealthservice", "securityhealthsystray", "mssense",
        "sensendr", "sensecncproxy", "mpcmdrun", "smartscreen",
        "avp", "avpui", "ekrn", "egui", "bdagent", "bdservicehost", "vsserv", "mbam", "mbamservice", "mbamtray",
        "ccsvchst", "nortonsecurity", "ns", "mcshield", "mfemms", "mfevtps", "mcafee", "avastsvc", "avastui",
        "avgsvc", "avgui", "savservice", "sophosui", "sophoshealth", "wrsa", "fsav", "fshoster32", "psanhost",
        "cylancesvc", "csfalconservice", "sentinelagent", "cb", "repmgr", "tmbmsrv", "ntrtscan", "pccntmon",
        "hitmanpro", "emsisoft", "a2service", "zam", "zillya", "360tray", "qhsafetray");

    private static IReadOnlySet<string> Set(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    /// <summary>Normalises "Name.exe" / "C:\path\Name.EXE" to "name".</summary>
    public static string Normalise(string processNameOrPath)
    {
        var name = Path.GetFileName(processNameOrPath.Trim());
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }

    public static bool IsCritical(string name) => Critical.Contains(Normalise(name));
    public static bool IsSecurity(string name) => Security.Contains(Normalise(name));

    public static bool IsBrowser(string name) => Normalise(name) is "chrome" or "msedge" or "msedgewebview2" or "firefox" or "brave" or "opera";

    public static bool IsInWindowsDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windir.Length > 0 && path.StartsWith(windir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
