using Tickshift.Core.Errors;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;
using Tickshift.Persistence;
using Tickshift.Tests.Fakes;

namespace Tickshift.Tests;

public class SettingsPersistenceTests
{
    [Fact]
    public async Task Settings_survive_a_restart()
    {
        using var dir = new TempDir();
        var paths = new AppDataPaths(dir.Path);
        var store = new JsonSettingsStore(paths);
        await store.UpdateAsync(s =>
        {
            s.Theme = ThemePreference.Light;
            s.CompactMode = true;
            s.RefreshIntervalSeconds = 5;
            s.ProtectedWebsites = [new ProtectedWebsite("tradingview.com")];
            s.Automation.Enabled = true;
            s.Automation.ApprovedUnattendedOptimisations = ["ui.reduce-animations"];
        });

        var reloaded = new JsonSettingsStore(paths).Current;
        Assert.Equal(ThemePreference.Light, reloaded.Theme);
        Assert.True(reloaded.CompactMode);
        Assert.Equal(5, reloaded.RefreshIntervalSeconds);
        Assert.Equal("tradingview.com", Assert.Single(reloaded.ProtectedWebsites).Domain);
        Assert.True(reloaded.Automation.Enabled);
        Assert.Contains("ui.reduce-animations", reloaded.Automation.ApprovedUnattendedOptimisations);
    }

    [Fact]
    public async Task Corrupt_settings_fall_back_to_the_last_good_backup()
    {
        using var dir = new TempDir();
        var paths = new AppDataPaths(dir.Path);
        var store = new JsonSettingsStore(paths);
        await store.UpdateAsync(s => s.Theme = ThemePreference.Light);
        await store.UpdateAsync(s => s.CompactMode = true); // creates .bak containing Theme=Light
        await File.WriteAllTextAsync(paths.SettingsFile, "{ this is not json");

        var recovered = new JsonSettingsStore(paths);

        Assert.Equal(LoadOutcome.RecoveredFromBackup, recovered.LoadOutcome);
        Assert.Equal(ThemePreference.Light, recovered.Current.Theme);
        Assert.NotEmpty(Directory.GetFiles(dir.Path, "settings.json.corrupt-*"));
    }

    [Fact]
    public async Task Corrupt_settings_without_backup_use_safe_defaults()
    {
        using var dir = new TempDir();
        var paths = new AppDataPaths(dir.Path);
        await File.WriteAllTextAsync(paths.SettingsFile, "garbage");
        var store = new JsonSettingsStore(paths);
        Assert.Equal(LoadOutcome.CorruptReplacedWithDefault, store.LoadOutcome);
        Assert.Equal(2, store.Current.RefreshIntervalSeconds);
    }

    [Fact]
    public void Invalid_values_are_clamped()
    {
        var s = new AppSettings
        {
            RefreshIntervalSeconds = 0,
            UiScale = double.NaN,
            DataRetentionDays = 100000,
            Theme = (ThemePreference)42,
            DefaultPreset = OptimisationPreset.All,
            NetworkEndpoints = [new NetworkEndpoint("x", "host", 70000), new NetworkEndpoint("ok", "broker.com", 443)],
        };
        s.Automation.MemoryPressureThresholdPercent = 5;

        Assert.True(s.Normalise());
        Assert.Equal(1, s.RefreshIntervalSeconds);
        Assert.Equal(1.0, s.UiScale);
        Assert.Equal(365, s.DataRetentionDays);
        Assert.Equal(ThemePreference.Dark, s.Theme);
        Assert.Equal(OptimisationPreset.Safe, s.DefaultPreset);
        Assert.Equal(60, s.Automation.MemoryPressureThresholdPercent);
        Assert.Equal("broker.com", Assert.Single(s.NetworkEndpoints).Host);
    }

    [Fact]
    public void Valid_settings_are_unchanged_by_normalisation() => Assert.False(new AppSettings().Normalise());

    [Fact]
    public async Task Reset_keeps_onboarding_state()
    {
        using var dir = new TempDir();
        var store = new JsonSettingsStore(new AppDataPaths(dir.Path));
        await store.UpdateAsync(s => { s.OnboardingCompleted = true; s.Theme = ThemePreference.Light; });
        var reset = await store.ResetAsync();
        Assert.True(reset.OnboardingCompleted);
        Assert.Equal(ThemePreference.Dark, reset.Theme);
    }

    [Fact]
    public async Task Record_store_is_bounded()
    {
        using var dir = new TempDir();
        var store = new RecordStore<ChangeRecord>(Path.Combine(dir.Path, "h.json"), r => r.Timestamp, maxCount: 5);
        for (var i = 0; i < 12; i++)
            await store.AppendAsync(new ChangeRecord { OptimisationId = "x", Description = $"{i}", Target = "t", Timestamp = DateTimeOffset.Now.AddMinutes(i) });
        var all = store.GetAll();
        Assert.Equal(5, all.Count);
        Assert.Equal("11", all[0].Description);
    }
}

public class ErrorClassifierTests
{
    [Fact]
    public void Classifies_common_failures()
    {
        Assert.Equal(ErrorKind.AccessDenied, ErrorClassifier.Classify(new UnauthorizedAccessException()));
        Assert.Equal(ErrorKind.Interrupted, ErrorClassifier.Classify(new OperationCanceledException()));
        Assert.Equal(ErrorKind.NotFound, ErrorClassifier.Classify(new FileNotFoundException()));
        Assert.Equal(ErrorKind.Unsupported, ErrorClassifier.Classify(new PlatformNotSupportedException()));
        Assert.Equal(ErrorKind.InUse, ErrorClassifier.Classify(new IOException("in use", unchecked((int)0x80070020))));
        Assert.Equal(ErrorKind.AccessDenied, ErrorClassifier.Classify(new System.ComponentModel.Win32Exception(5)));
        Assert.Equal(ErrorKind.Interrupted, ErrorClassifier.Classify(new System.ComponentModel.Win32Exception(1223)));
        Assert.Equal(ErrorKind.InvalidConfiguration, ErrorClassifier.Classify(new System.Text.Json.JsonException()));
        Assert.Equal(ErrorKind.Unknown, ErrorClassifier.Classify(new InvalidOperationException()));
    }

    [Fact]
    public void Every_kind_has_a_friendly_message()
    {
        foreach (var kind in Enum.GetValues<ErrorKind>())
            Assert.False(string.IsNullOrWhiteSpace(ErrorClassifier.FriendlyMessage(kind)));
    }

    [Fact]
    public void Failure_results_never_look_successful()
    {
        var r = Core.OperationResult.FromException(new UnauthorizedAccessException("x"), "Change setting");
        Assert.False(r.IsSuccess);
        Assert.Equal(ErrorKind.AccessDenied, r.Error);
    }
}
