using System.Windows;
using Microsoft.Win32;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;

namespace Tickshift.App.Infrastructure;

/// <summary>
/// Swaps the colour and density dictionaries at runtime. All UI references colours via DynamicResource,
/// so switching is instant and needs no restart. "Follow Windows" reads the documented
/// AppsUseLightTheme value and updates when Windows broadcasts a theme change.
/// </summary>
public sealed class ThemeService
{
    private const int ColorsIndex = 0;
    private const int DensityIndex = 1;
    private ThemePreference _preference = ThemePreference.Dark;
    private bool _compact;

    public bool IsDark { get; private set; } = true;

    public event EventHandler? ThemeChanged;

    public void Apply(AppSettings settings)
    {
        _preference = settings.Theme;
        _compact = settings.CompactMode;
        Refresh();
    }

    /// <summary>Called when Windows reports a settings change (WM_SETTINGCHANGE "ImmersiveColorSet").</summary>
    public void OnSystemThemeChanged()
    {
        if (_preference == ThemePreference.System) Refresh();
    }

    private void Refresh()
    {
        var dark = _preference switch
        {
            ThemePreference.Light => false,
            ThemePreference.System => !SystemUsesLightTheme(),
            _ => true,
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        var colors = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Tickshift;component/Themes/Colors.{(dark ? "Dark" : "Light")}.xaml") };
        var density = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Tickshift;component/Themes/Density.{(_compact ? "Compact" : "Comfortable")}.xaml") };

        if (merged.Count > ColorsIndex) merged[ColorsIndex] = colors; else merged.Add(colors);
        if (merged.Count > DensityIndex) merged[DensityIndex] = density; else merged.Add(density);

        IsDark = dark;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            return false;
        }
    }
}
