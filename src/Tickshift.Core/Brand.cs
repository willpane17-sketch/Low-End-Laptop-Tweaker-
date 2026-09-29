namespace Tickshift.Core;

/// <summary>
/// Branding constants. The product name is deliberately centralised so it can be
/// changed in one place (window titles, notifications, reports, autostart value name).
/// </summary>
public static class Brand
{
    public const string ProductName = "TICKSHIFT";
    public const string Tagline = "Performance, when every second matters.";

    /// <summary>Folder name used under %LOCALAPPDATA% for settings, logs and recovery data.</summary>
    public const string DataFolderName = "Tickshift";

    /// <summary>Name of the HKCU Run value written only when the user enables "Launch with Windows".</summary>
    public const string AutostartValueName = "TICKSHIFT";

    /// <summary>File name of the narrowly scoped elevated helper shipped next to the main executable.</summary>
    public const string ElevatedHelperFileName = "Tickshift.ElevatedHelper.exe";

    public static string Version =>
        typeof(Brand).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
