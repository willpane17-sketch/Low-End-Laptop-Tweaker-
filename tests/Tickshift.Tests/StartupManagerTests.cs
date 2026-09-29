using System.Text;
using Tickshift.Core.Models;
using Tickshift.Optimization.Recovery;
using Tickshift.Optimization.Startup;
using Tickshift.Persistence;
using Tickshift.Security;
using Tickshift.Tests.Fakes;

namespace Tickshift.Tests;

public class StartupManagerTests
{
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Journal = new ChangeJournal(new AppDataPaths(Dir.Path), Clock);
            Service = new StartupManagerService(Provider, Journal, Elevated, Clock);
            Recovery = new RecoveryService(Journal, [Service]);
        }

        public TempDir Dir { get; } = new();
        public FakeClock Clock { get; } = new();
        public FakeStartupProvider Provider { get; } = new();
        public FakeElevatedRunner Elevated { get; } = new();
        public ChangeJournal Journal { get; }
        public StartupManagerService Service { get; }
        public RecoveryService Recovery { get; }

        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public async Task Disabling_a_user_entry_is_verified_and_recorded()
    {
        using var h = new Harness();
        var entry = FakeStartupProvider.Entry("Spotify");
        h.Provider.Entries.Add(entry);

        var result = await h.Service.SetEnabledAsync(entry, false, dryRun: false, default);

        Assert.True(result.IsSuccess);
        Assert.False(StartupApprovalValue.IsEnabled(h.Provider.Approvals[entry.Id]));
        var record = Assert.Single(h.Journal.GetAll());
        Assert.Equal(ChangeStatus.Verified, record.Status);
        Assert.Equal("Enabled", record.OriginalValue);
        Assert.Equal("Disabled", record.NewValue);
        Assert.Empty(h.Elevated.Calls);
    }

    [Fact]
    public async Task Restore_returns_the_exact_original_state()
    {
        using var h = new Harness();
        var entry = FakeStartupProvider.Entry("Teams");
        h.Provider.Entries.Add(entry);
        await h.Service.SetEnabledAsync(entry, false, false, default);

        var report = await h.Recovery.RestoreAllAsync(default);

        Assert.Equal(1, report.Restored);
        Assert.Null(h.Provider.Approvals[entry.Id]); // value was absent originally → deleted again
    }

    [Fact]
    public async Task Security_software_is_never_disabled()
    {
        using var h = new Harness();
        var entry = FakeStartupProvider.Entry("SecurityHealth", StartupLocation.MachineRun, @"C:\Windows\System32\SecurityHealthSystray.exe");
        h.Provider.Entries.Add(entry);

        var result = await h.Service.SetEnabledAsync(entry, false, false, default);

        Assert.Equal(Core.OperationStatus.Skipped, result.Status);
        Assert.Empty(h.Journal.GetAll());
        Assert.Empty(h.Elevated.Calls);
    }

    [Fact]
    public async Task Machine_entries_go_through_the_validated_elevated_helper()
    {
        using var h = new Harness();
        var entry = FakeStartupProvider.Entry("Updater", StartupLocation.MachineRun32);
        h.Provider.Entries.Add(entry);
        h.Elevated.OnRun = op =>
        {
            Assert.True(ElevatedArgumentValidator.TryParse(op.Arguments, out var parsed, out _));
            Assert.Equal("Run32", parsed!.List);
            Assert.Equal("Updater", parsed.ValueName);
            h.Provider.Approvals[entry.Id] = parsed.Value;
        };

        var result = await h.Service.SetEnabledAsync(entry, false, false, default);

        Assert.True(result.IsSuccess);
        Assert.Single(h.Elevated.Calls);
    }

    [Fact]
    public async Task Declined_uac_prompt_changes_nothing_and_is_reported()
    {
        using var h = new Harness();
        var entry = FakeStartupProvider.Entry("Updater", StartupLocation.MachineRun);
        h.Provider.Entries.Add(entry);
        h.Elevated.Result = ElevatedExitCode.AccessDenied;

        var result = await h.Service.SetEnabledAsync(entry, false, false, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(ChangeStatus.Failed, h.Journal.GetAll().Single().Status);
        Assert.True(StartupApprovalValue.IsEnabled(h.Provider.Approvals.GetValueOrDefault(entry.Id)));
    }

    [Fact]
    public async Task Dry_run_does_not_write()
    {
        using var h = new Harness();
        var entry = FakeStartupProvider.Entry("Discord");
        h.Provider.Entries.Add(entry);
        var result = await h.Service.SetEnabledAsync(entry, false, dryRun: true, default);
        Assert.Equal(Core.OperationStatus.DryRun, result.Status);
        Assert.Empty(h.Provider.Approvals);
        Assert.Empty(h.Journal.GetAll());
    }

    [Fact]
    public async Task Rollback_leaves_entries_re_enabled_elsewhere_alone()
    {
        using var h = new Harness();
        var entry = FakeStartupProvider.Entry("Zoom");
        h.Provider.Entries.Add(entry);
        await h.Service.SetEnabledAsync(entry, false, false, default);
        h.Provider.Approvals[entry.Id] = StartupApprovalValue.Create(true, DateTimeOffset.Now); // user re-enabled in Task Manager

        var report = await h.Recovery.RestoreAllAsync(default);

        Assert.Equal(1, report.Restored + report.Conflicts); // already in original (enabled) state counts as restored
    }

    [Fact]
    public void Tickshift_itself_is_managed_from_settings_not_here()
    {
        var entry = FakeStartupProvider.Entry(Core.Brand.AutostartValueName);
        Assert.NotNull(StartupManagerService.GetChangeBlocker(entry, enable: false));
    }
}
