using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tickshift.App.Infrastructure;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;
using Tickshift.Trading;

namespace Tickshift.App.ViewModels;

public sealed record BrowserTip(string Title, string Body, string? SettingsKey, string? ButtonText, Severity Severity);

public sealed record BrowserMeasurement(DateTimeOffset At, int Processes, double CpuPercent, long MemoryBytes, int Samples);

public sealed partial class BrowserViewModel : PageViewModel
{
    private readonly IBrowserProvider _browsers;
    private readonly IProcessProvider _processes;
    private readonly ILinkLauncher _links;
    private readonly ISettingsStore _settings;
    private readonly NavigationService _navigation;
    private readonly Dictionary<BrowserKind, BrowserMeasurement> _baselines = new();

    public BrowserViewModel(IBrowserProvider browsers, IProcessProvider processes, ILinkLauncher links, ISettingsStore settings, NavigationService navigation)
    {
        _browsers = browsers;
        _processes = processes;
        _links = links;
        _settings = settings;
        _navigation = navigation;
    }

    public override string Title => "Browser Booster";

    [ObservableProperty] private BrowserKind _selectedBrowser = BrowserKind.Edge;
    [ObservableProperty] private BrowserAnalysis? _analysis;
    [ObservableProperty] private string _installedText = string.Empty;
    [ObservableProperty] private string _usageText = string.Empty;
    [ObservableProperty] private string _backgroundText = string.Empty;
    [ObservableProperty] private string _efficiencyText = string.Empty;
    [ObservableProperty] private string _extensionsText = string.Empty;
    [ObservableProperty] private string _cacheText = string.Empty;
    [ObservableProperty] private string _newSite = string.Empty;
    [ObservableProperty] private bool _isMeasuring;
    [ObservableProperty] private BrowserMeasurement? _baseline;
    [ObservableProperty] private BrowserMeasurement? _latestMeasurement;
    [ObservableProperty] private string _comparisonText = string.Empty;
    [ObservableProperty] private string _featureName = "Sleeping tabs";

    public ObservableCollection<BrowserTip> Tips { get; } = [];
    public ObservableCollection<ProtectedWebsite> Sites { get; } = [];
    public ObservableCollection<string> GuideSteps { get; } = [];
    public ObservableCollection<string> Patterns { get; } = [];

    public override async Task OnNavigatedToAsync()
    {
        LoadSites();
        await RefreshAsync();
    }

    partial void OnSelectedBrowserChanged(BrowserKind value)
    {
        Baseline = _baselines.GetValueOrDefault(value);
        LatestMeasurement = null;
        ComparisonText = string.Empty;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void SelectBrowser(string name) => SelectedBrowser = Enum.Parse<BrowserKind>(name);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var kind = SelectedBrowser;
            var analysis = await Task.Run(() =>
            {
                _processes.GetProcesses();
                Thread.Sleep(600); // short window so per-process CPU figures are meaningful
                return _browsers.Analyse(kind, _processes.GetProcesses());
            });
            Analysis = analysis;
            FeatureName = analysis.EfficiencyFeatureName;
            BuildTexts(analysis);
            BuildTips(analysis);
            BuildGuide();
        }
        catch (Exception ex)
        {
            ShowMessage("Browser analysis failed: " + Core.Errors.ErrorClassifier.FriendlyMessage(Core.Errors.ErrorClassifier.Classify(ex)), true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BuildTexts(BrowserAnalysis a)
    {
        InstalledText = a.Installed ? $"{a.DisplayName} {a.Version} is installed." : $"{a.DisplayName} was not found on this computer.";
        UsageText = a.IsRunning
            ? $"{a.ProcessCount} processes using {Format.Bytes(a.MemoryBytes)} of memory and {a.CpuPercent:0}% of the processor right now."
            : "Not running right now.";
        BackgroundText = a.BackgroundModeEnabled switch
        {
            true when a.BackgroundModeEnforcedByPolicy => "Keeps running after it is closed — set by your organisation's policy.",
            true => "Keeps running in the background after you close it.",
            false => "Stops completely when closed.",
            null => "Could not be detected; check the browser's System settings.",
        };
        EfficiencyText = a.EfficiencyFeatureEnabled switch
        {
            true => $"{a.EfficiencyFeatureName} is enforced on by policy.",
            false => $"{a.EfficiencyFeatureName} is turned off by policy.",
            null => $"{a.EfficiencyFeatureName} status is stored privately by the browser; check it in the browser's performance settings.",
        };
        ExtensionsText = a.TotalExtensions == 0 ? "No extensions found." : $"{a.TotalExtensions} extension folders across {a.Profiles.Count} profile(s).";
        CacheText = $"{Format.Bytes(a.TotalDisposableCacheBytes)} of disposable cache (page, code and graphics caches only).";
    }

    private void BuildTips(BrowserAnalysis a)
    {
        Tips.Clear();
        if (!a.Installed) return;
        var total = (long)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var edge = a.Kind == BrowserKind.Edge;

        Tips.Add(new BrowserTip($"Turn on {a.EfficiencyFeatureName}",
            $"{a.EfficiencyFeatureName} frees memory from tabs you are not using. Add your trading sites to its exception list (below) so charts and order tickets are never put to sleep.",
            "performance", "Open performance settings", a.IsRunning && total > 0 && a.MemoryBytes > total / 4 ? Severity.Medium : Severity.Low));

        if (a.BackgroundModeEnabled == true && !a.BackgroundModeEnforcedByPolicy)
        {
            Tips.Add(new BrowserTip("Stop running in the background",
                $"Turn off \"Continue running background extensions and apps when {a.DisplayName} is closed\" so it frees memory when you close it.",
                edge ? "performance" : "system", "Open system settings", Severity.Medium));
        }

        if (edge && a.StartupBoostEnabled != false)
        {
            Tips.Add(new BrowserTip("Consider turning off Startup boost",
                "Startup boost keeps Edge partly loaded after you sign in. On a 4 GB laptop you may prefer to keep that memory free for your trading platform.",
                "performance", "Open system settings", Severity.Low));
        }

        if (a.TotalExtensions >= 5)
        {
            Tips.Add(new BrowserTip("Review your extensions",
                "Every extension can run on every page. Remove the ones you no longer use; keep ones you rely on.",
                "extensions", "Open extensions", a.TotalExtensions >= 10 ? Severity.Medium : Severity.Low));
        }

        if (a.IsRunning && a.ProcessCount >= 25)
        {
            Tips.Add(new BrowserTip("Many tabs or windows are open",
                $"{a.ProcessCount} browser processes are running. Each open tab uses memory; bookmark and close tabs you are not actively watching.",
                null, null, Severity.Medium));
        }

        Tips.Add(new BrowserTip("Keep the browser up to date",
            "Updates include performance and security fixes. The About page checks for updates.",
            "about", "Check for updates", Severity.Info));

        Tips.Add(new BrowserTip("Graphics troubleshooting",
            "If charts stutter or flicker, try toggling \"Use graphics acceleration when available\" in the browser's System settings and restart the browser. Results differ between laptops, so compare before deciding.",
            edge ? "performance" : "system", "Open system settings", Severity.Info));

        Tips.Add(new BrowserTip("Startup pages",
            "Opening many pages at launch slows start-up. Consider \"Open the new tab page\" or a single trading page instead of restoring every tab.",
            "startup", "Open start-up settings", Severity.Info));
    }

    private void LoadSites()
    {
        Sites.Clear();
        foreach (var s in _settings.Current.ProtectedWebsites) Sites.Add(s);
    }

    private void BuildGuide()
    {
        var guide = ProtectedWebsites.GuideFor(SelectedBrowser, Sites);
        GuideSteps.Clear();
        foreach (var s in guide.Steps) GuideSteps.Add(s);
        Patterns.Clear();
        foreach (var p in guide.PatternsToAdd) Patterns.Add(p);
    }

    [RelayCommand]
    private async Task AddSiteAsync(string? value)
    {
        var input = value ?? NewSite;
        if (!ProtectedWebsites.TryNormalise(input, out var domain))
        {
            ShowMessage("Enter a website address such as tradingview.com.", true);
            return;
        }

        if (Sites.Any(s => s.Domain == domain))
        {
            ShowMessage($"{domain} is already protected.");
            return;
        }

        var label = ProtectedWebsites.Suggestions.FirstOrDefault(s => s.Domain == domain)?.Label;
        Sites.Add(new ProtectedWebsite(domain, label));
        await _settings.UpdateAsync(s => s.ProtectedWebsites = Sites.ToList());
        NewSite = string.Empty;
        BuildGuide();
        ShowMessage($"{domain} added. Follow the steps below to add it to the browser's exception list.");
    }

    [RelayCommand]
    private async Task RemoveSiteAsync(ProtectedWebsite? site)
    {
        if (site is null) return;
        Sites.Remove(site);
        await _settings.UpdateAsync(s => s.ProtectedWebsites = Sites.ToList());
        BuildGuide();
    }

    [RelayCommand]
    private void CopyPattern(string? pattern)
    {
        if (pattern is null) return;
        ShowMessage(ClipboardHelper.TrySetText(pattern) ? $"Copied {pattern} — paste it into the browser's exception list." : "The clipboard is busy; please try again.");
    }

    [RelayCommand]
    private void OpenPage(string? key)
    {
        if (key is null) return;
        var result = _links.OpenInBrowser(SelectedBrowser, key);
        if (!result.IsSuccess) ShowResult(result);
    }

    [RelayCommand]
    private void OpenCleanup() => _navigation.Navigate(PageKeys.Cleanup);

    [RelayCommand]
    private async Task MeasureAsync()
    {
        IsMeasuring = true;
        try
        {
            var kind = SelectedBrowser;
            var measurement = await Task.Run(() =>
            {
                // Average over 10 seconds so a single spike does not distort the comparison.
                var name = kind == BrowserKind.Edge ? "msedge" : "chrome";
                _processes.GetProcesses();
                var cpu = new List<double>();
                var mem = new List<long>();
                var counts = new List<int>();
                for (var i = 0; i < 5; i++)
                {
                    Thread.Sleep(2000);
                    var own = _processes.GetProcesses().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                    cpu.Add(own.Sum(p => p.CpuPercent));
                    mem.Add(own.Sum(p => p.PrivateBytes));
                    counts.Add(own.Count);
                }

                return new BrowserMeasurement(DateTimeOffset.Now, (int)Math.Round(counts.Average()), Math.Round(cpu.Average(), 1), (long)mem.Average(), 5);
            });

            if (Baseline is null)
            {
                Baseline = measurement;
                _baselines[kind] = measurement;
                ComparisonText = "Baseline recorded. Make your changes in the browser (for example, enable the efficiency feature and close unused tabs), then measure again.";
            }
            else
            {
                LatestMeasurement = measurement;
                ComparisonText =
                    $"Memory: {Format.Bytes(Baseline.MemoryBytes)} → {Format.Bytes(measurement.MemoryBytes)} · " +
                    $"Processor: {Baseline.CpuPercent:0.#}% → {measurement.CpuPercent:0.#}% · " +
                    $"Processes: {Baseline.Processes} → {measurement.Processes}. " +
                    "These are averages over 10 seconds; what you have open in the browser matters more than any setting.";
            }
        }
        finally
        {
            IsMeasuring = false;
        }
    }

    [RelayCommand]
    private void ResetBaseline()
    {
        Baseline = null;
        LatestMeasurement = null;
        _baselines.Remove(SelectedBrowser);
        ComparisonText = string.Empty;
    }
}
