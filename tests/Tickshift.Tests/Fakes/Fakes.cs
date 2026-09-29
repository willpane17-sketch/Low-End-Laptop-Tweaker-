using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;
using Tickshift.Diagnostics.Monitoring;
using Tickshift.Optimization;
using Tickshift.Persistence;
using Tickshift.Security;

namespace Tickshift.Tests.Fakes;

/// <summary>A disposable sandbox directory. Tests never touch real user folders or system settings.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tickshift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative, string content = "x", TimeSpan? age = null)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        if (age is { } a) System.IO.File.SetLastWriteTimeUtc(full, DateTime.UtcNow - a);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class FakeClock(DateTimeOffset? now = null) : IClock
{
    public DateTimeOffset Now { get; set; } = now ?? new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
}

public sealed class InMemorySettingsStore(AppSettings? initial = null) : ISettingsStore
{
    public AppSettings Current { get; private set; } = initial ?? new AppSettings();
    public event EventHandler<AppSettings>? Changed;

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        var copy = settings.Clone();
        copy.Normalise();
        Current = copy;
        Changed?.Invoke(this, copy);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        var copy = Current.Clone();
        mutate(copy);
        return SaveAsync(copy, ct);
    }

    public Task<AppSettings> ResetAsync(CancellationToken ct = default)
    {
        Current = new AppSettings();
        return Task.FromResult(Current);
    }
}

public sealed class RecordingNotifications : INotificationService
{
    public List<NotificationMessage> Messages { get; } = [];

    public bool Notify(NotificationMessage message)
    {
        Messages.Add(message);
        return true;
    }
}

public sealed class FakeElevation(bool elevated = false) : IElevationState
{
    public bool IsElevated { get; } = elevated;
}

public sealed class FakePowerPlans : IPowerPlanManager
{
    public List<PowerPlan> Plans { get; } =
    [
        new(PowerPlan.PowerSaverId, "Power saver"),
        new(PowerPlan.BalancedId, "Balanced"),
        new(PowerPlan.HighPerformanceId, "High performance"),
    ];

    public Guid Active { get; set; } = PowerPlan.BalancedId;

    /// <summary>Simulates a policy/OEM tool that silently ignores plan changes.</summary>
    public bool IgnoreChanges { get; set; }

    public int SetCalls { get; private set; }

    public IReadOnlyList<PowerPlan> GetPlans() => Plans;
    public PowerPlan? GetActivePlan() => Plans.FirstOrDefault(p => p.Id == Active);

    public void SetActivePlan(Guid planId)
    {
        SetCalls++;
        if (Plans.All(p => p.Id != planId)) throw new InvalidOperationException("No such plan");
        if (!IgnoreChanges) Active = planId;
    }
}

public sealed class FakeVisualEffects : IVisualEffectsManager
{
    public VisualEffectsState State { get; set; } = new(true, true, true, true, true, true);
    public VisualEffectsState GetCurrent() => State;
    public void Apply(VisualEffectsState state) => State = state;
}

public sealed class FakeSystemInfo : ISystemInfoProvider
{
    public CpuInfo Cpu { get; set; } = new("Intel(R) Core(TM) i3-7020U CPU @ 2.30GHz", "Intel", 2, 4, 2300);
    public MemoryStatus Memory { get; set; } = new(4UL << 30, 1UL << 30, 75, 5UL << 30, 8UL << 30);
    public List<StorageVolume> Storage { get; set; } = [new("C:\\", "Local Disk", "NTFS", 128UL << 30, 40UL << 30, true, StorageMediaKind.SolidState, "SATA")];
    public OsInfo Os { get; set; } = new("Windows 10 Home", "22H2", 19045, 4000, "Core", true);
    public PowerStatus Power { get; set; } = new(PowerSource.Mains, true, 80, false, new PowerPlan(PowerPlan.BalancedId, "Balanced"), null);
    public bool ThrowOnStorage { get; set; }

    public Task<CpuInfo> GetCpuAsync(CancellationToken ct = default) => Task.FromResult(Cpu);
    public MemoryStatus GetMemory() => Memory;

    public Task<IReadOnlyList<StorageVolume>> GetStorageAsync(CancellationToken ct = default) =>
        ThrowOnStorage ? throw new UnauthorizedAccessException("denied") : Task.FromResult<IReadOnlyList<StorageVolume>>(Storage);

    public OsInfo GetOs() => Os;
    public Task<PowerStatus> GetPowerAsync(CancellationToken ct = default) => Task.FromResult(Power);
    public TimeSpan GetUptime() => TimeSpan.FromHours(5);
}

public sealed class FakeStartupProvider : IStartupProvider
{
    public List<StartupEntry> Entries { get; } = [];
    public Dictionary<string, byte[]?> Approvals { get; } = new();

    public IReadOnlyList<StartupEntry> Enumerate() =>
        Entries.Select(e => e with { IsEnabled = StartupApprovalValue.IsEnabled(Approvals.GetValueOrDefault(e.Id)) }).ToList();

    public byte[]? ReadApprovalValue(StartupEntry entry) => Approvals.GetValueOrDefault(entry.Id);

    public void WriteApprovalValue(StartupEntry entry, byte[]? value)
    {
        if (entry.RequiresElevation) throw new UnauthorizedAccessException("machine scope");
        Approvals[entry.Id] = value;
    }

    public static StartupEntry Entry(string name, StartupLocation location = StartupLocation.UserRun, string? exe = null) => new()
    {
        Id = StartupEntry.MakeId(location, name),
        Name = name,
        Command = $"\"{exe ?? $@"C:\Apps\{name}.exe"}\"",
        ExecutablePath = exe ?? $@"C:\Apps\{name}.exe",
        Location = location,
        IsEnabled = true,
        ExecutableExists = true,
    };
}

public sealed class FakeElevatedRunner : IElevatedOperationRunner
{
    public List<ElevatedOperation> Calls { get; } = [];
    public ElevatedExitCode Result { get; set; } = ElevatedExitCode.Success;
    public Action<ElevatedOperation>? OnRun { get; set; }
    public bool IsAvailable { get; set; } = true;

    public Task<ElevatedResult> RunAsync(ElevatedOperation operation, CancellationToken ct = default)
    {
        Calls.Add(operation);
        if (Result == ElevatedExitCode.Success) OnRun?.Invoke(operation);
        return Task.FromResult(new ElevatedResult(Result, Result.ToString()));
    }
}

public sealed class FakeRestorePoints : IRestorePointService
{
    public int Calls { get; private set; }
    public SystemRestoreStatus GetStatus() => new(true, false, "On");

    public Task<OperationResult> CreateAsync(CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(OperationResult.Success("created"));
    }
}

public sealed class FakeProcessProvider : IProcessProvider
{
    public List<ProcessSnapshot> Processes { get; } = [];
    public int CloseRequests { get; private set; }

    public IReadOnlyList<ProcessSnapshot> GetProcesses() => Processes.ToList();

    public OperationResult RequestGracefulClose(int pid)
    {
        CloseRequests++;
        return OperationResult.Success("closed");
    }

    public int GetProcessCount() => Processes.Count;

    public static ProcessSnapshot Proc(string name, double cpu = 1, long mem = 100 << 20, ProcessCategory category = ProcessCategory.Application, bool window = true, int pid = 0) =>
        new(pid == 0 ? Random.Shared.Next(100, 60000) : pid, name, $@"C:\Apps\{name}.exe", null, cpu, mem, mem, null, window, 1, category);
}

public sealed class FakeMetrics : IMetricsSource
{
    public MetricSample? Latest { get; set; }
    public ThermalSnapshot? LatestThermal { get; set; } = ThermalSnapshot.Unavailable("Fake", "No sensor on this test machine.");
    public PerformanceHistory History { get; } = new(1000);
    public List<MetricSample> CollectResult { get; set; } = [];
    public event EventHandler<MetricSample>? SampleAvailable;

    public Task<IReadOnlyList<MetricSample>> CollectAsync(TimeSpan duration, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MetricSample>>(CollectResult);

    public Task<ThermalSnapshot> RefreshThermalAsync(CancellationToken ct) => Task.FromResult(LatestThermal!);

    public void Push(MetricSample s)
    {
        Latest = s;
        History.Add(s);
        SampleAvailable?.Invoke(this, s);
    }

    public static List<MetricSample> Series(int count, double cpu, double mem, DateTimeOffset? start = null, TimeSpan? step = null, double? temp = null)
    {
        var t = start ?? DateTimeOffset.Now.AddSeconds(-count);
        var dt = step ?? TimeSpan.FromSeconds(1);
        return Enumerable.Range(0, count).Select(i => new MetricSample(t + dt * i, cpu, mem, 1UL << 30, 5, temp)).ToList();
    }
}

public sealed class FakeBrowsers : IBrowserProvider
{
    public HashSet<BrowserKind> Running { get; } = [];
    public IReadOnlyList<BrowserInstallation> DetectInstalled() => [];
    public BrowserAnalysis Analyse(BrowserKind kind, IReadOnlyList<ProcessSnapshot> processes) => new() { Kind = kind, Installed = false };
    public bool IsRunning(BrowserKind kind) => Running.Contains(kind);
}

/// <summary>Configurable optimisation used to test the engine's pipeline without touching the system.</summary>
public sealed class FakeOptimisation : IOptimisation
{
    public FakeOptimisation(string id = "ui.reduce-animations") => Id = id;

    public string Id { get; }
    public string Title => "Fake " + Id;
    public string Description => "Test optimisation";
    public OptimisationCategory Category => OptimisationCategory.Interface;
    public RiskLevel Risk { get; set; } = RiskLevel.Low;
    public PrivilegeLevel RequiredPrivilege { get; set; } = PrivilegeLevel.StandardUser;
    public Reversibility Reversibility { get; set; } = Reversibility.FullyReversible;
    public IReadOnlyList<string> Preconditions => ["none"];
    public OptimisationPreset AvailableIn { get; set; } = OptimisationPreset.All;
    public OptimisationPreset SelectedByDefaultIn { get; set; } = OptimisationPreset.All;
    public string? UncertaintyNote { get; set; }
    public bool RequiresExplicitApproval { get; set; }

    public bool Applicable { get; set; } = true;
    public bool VerifyResult { get; set; } = true;
    public Exception? ThrowOnApply { get; set; }
    public int ApplyCount { get; private set; }
    public int RollbackCount { get; private set; }
    public string Value { get; set; } = "original";
    public Func<CancellationToken, Task>? DuringApply { get; set; }

    public Task<OptimisationAssessment> AssessAsync(OptimisationContext context, CancellationToken ct) =>
        Task.FromResult(Applicable ? new OptimisationAssessment(true, "Applies", "Change value", null, Value) : OptimisationAssessment.NotApplicable("Not needed"));

    public async Task<ApplyOutcome> ApplyAsync(OptimisationContext context, OptimisationAssessment assessment, IChangeRecorder recorder, CancellationToken ct)
    {
        var id = await recorder.BeginAsync("fake target", Title, Value, "changed", Reversibility != Reversibility.NotReversible, Value, ct);
        if (DuringApply is not null) await DuringApply(ct);
        if (ThrowOnApply is not null)
        {
            await recorder.MarkFailedAsync(id, ThrowOnApply.Message, CancellationToken.None);
            throw ThrowOnApply;
        }

        ApplyCount++;
        Value = "changed";
        await recorder.MarkAppliedAsync(id, ct);
        return new ApplyOutcome(OperationResult.Success("Applied"), [id]);
    }

    public Task<VerificationResult> VerifyAsync(OptimisationContext context, OptimisationAssessment assessment, ApplyOutcome outcome, CancellationToken ct) =>
        Task.FromResult(new VerificationResult(VerifyResult, VerifyResult ? "ok" : "mismatch"));

    public Task<OperationResult> RollbackAsync(ChangeRecord record, CancellationToken ct)
    {
        RollbackCount++;
        if (Value != "changed") return Task.FromResult(new OperationResult(OperationStatus.Skipped, "changed elsewhere", Core.Errors.ErrorKind.Conflict));
        Value = record.RollbackData ?? "original";
        return Task.FromResult(OperationResult.Success("restored"));
    }

    public Task<InterruptedState> InspectInterruptedAsync(ChangeRecord record, CancellationToken ct) =>
        Task.FromResult(Value == "changed" ? InterruptedState.Applied : InterruptedState.NotApplied);
}
