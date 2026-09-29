using System.Text.Json;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Persistence;

namespace Tickshift.Optimization.Optimisations;

internal sealed record PowerPlanRollback(Guid Original, string OriginalName, Guid Applied, string AppliedName);

/// <summary>Shared logic for switching the active power plan via the documented Power Management API.</summary>
public abstract class PowerPlanSwitchOptimisation(IPowerPlanManager plans, ISystemInfoProvider info) : IOptimisation
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Description { get; }
    public OptimisationCategory Category => OptimisationCategory.Power;
    public abstract RiskLevel Risk { get; }
    public PrivilegeLevel RequiredPrivilege => PrivilegeLevel.StandardUser;
    public Reversibility Reversibility => Reversibility.FullyReversible;
    public abstract IReadOnlyList<string> Preconditions { get; }
    public abstract OptimisationPreset AvailableIn { get; }
    public abstract OptimisationPreset SelectedByDefaultIn { get; }
    public abstract string? UncertaintyNote { get; }
    public bool RequiresExplicitApproval => false;

    /// <summary>Returns the target plan when the switch applies, or a reason it does not.</summary>
    protected abstract (PowerPlan? Target, string Reason) ChooseTarget(PowerStatus power, PowerPlan active, IReadOnlyList<PowerPlan> available);

    public async Task<OptimisationAssessment> AssessAsync(OptimisationContext context, CancellationToken ct)
    {
        var power = await info.GetPowerAsync(ct).ConfigureAwait(false);
        var active = plans.GetActivePlan();
        if (active is null) return OptimisationAssessment.NotApplicable("The active power plan could not be read.");
        var (target, reason) = ChooseTarget(power, active, plans.GetPlans());
        if (target is null) return OptimisationAssessment.NotApplicable(reason);

        return new OptimisationAssessment(true, reason, $"Switch power plan from \"{active.Name}\" to \"{target.Name}\".", null,
            JsonSerializer.Serialize(new PowerPlanRollback(active.Id, active.Name, target.Id, target.Name)));
    }

    public async Task<ApplyOutcome> ApplyAsync(OptimisationContext context, OptimisationAssessment assessment, IChangeRecorder recorder, CancellationToken ct)
    {
        var plan = JsonSerializer.Deserialize<PowerPlanRollback>(assessment.State!)!;
        var current = plans.GetActivePlan();
        if (current?.Id != plan.Original)
            return new ApplyOutcome(OperationResult.Skipped("The power plan changed since the analysis; nothing was changed."), []);

        var id = await recorder.BeginAsync("Active power plan", Title, plan.OriginalName, plan.AppliedName, true, assessment.State, ct).ConfigureAwait(false);
        try
        {
            plans.SetActivePlan(plan.Applied);
        }
        catch (Exception ex)
        {
            await recorder.MarkFailedAsync(id, ex.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await recorder.MarkAppliedAsync(id, ct).ConfigureAwait(false);
        return new ApplyOutcome(OperationResult.Success($"Power plan set to \"{plan.AppliedName}\"."), id == Guid.Empty ? [] : [id]);
    }

    public Task<VerificationResult> VerifyAsync(OptimisationContext context, OptimisationAssessment assessment, ApplyOutcome outcome, CancellationToken ct)
    {
        var plan = JsonSerializer.Deserialize<PowerPlanRollback>(assessment.State!)!;
        var active = plans.GetActivePlan();
        return Task.FromResult(active?.Id == plan.Applied
            ? new VerificationResult(true, $"Windows reports \"{active.Name}\" as the active plan.")
            : new VerificationResult(false, $"Windows reports \"{active?.Name ?? "unknown"}\" as the active plan; the change did not take effect (a policy or OEM utility may control it)."));
    }

    public Task<OperationResult> RollbackAsync(ChangeRecord record, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<PowerPlanRollback>(record.RollbackData ?? "null");
        if (data is null) return Task.FromResult(OperationResult.Failure("Rollback information is missing."));
        var active = plans.GetActivePlan();
        if (active?.Id == data.Original) return Task.FromResult(OperationResult.Success($"\"{data.OriginalName}\" is already active."));
        if (active?.Id != data.Applied)
        {
            return Task.FromResult(new OperationResult(OperationStatus.Skipped,
                $"The power plan was changed to \"{active?.Name}\" by something else, so it was left as is.", Core.Errors.ErrorKind.Conflict));
        }

        plans.SetActivePlan(data.Original);
        return Task.FromResult(plans.GetActivePlan()?.Id == data.Original
            ? OperationResult.Success($"Power plan restored to \"{data.OriginalName}\".")
            : OperationResult.Failure("The original power plan could not be verified after restoring."));
    }

    public Task<InterruptedState> InspectInterruptedAsync(ChangeRecord record, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<PowerPlanRollback>(record.RollbackData ?? "null");
        var active = plans.GetActivePlan();
        return Task.FromResult(data is null ? InterruptedState.Unknown
            : active?.Id == data.Applied ? InterruptedState.Applied
            : active?.Id == data.Original ? InterruptedState.NotApplied
            : InterruptedState.Unknown);
    }
}

/// <summary>Power saver while plugged in needlessly caps performance; Balanced is Microsoft's recommended plan.</summary>
public sealed class BalancedWhenPluggedInOptimisation(IPowerPlanManager plans, ISystemInfoProvider info) : PowerPlanSwitchOptimisation(plans, info)
{
    public const string OptimisationId = "power.balanced-on-mains";
    public override string Id => OptimisationId;
    public override string Title => "Use the Balanced power plan while plugged in";
    public override string Description => "Power saver limits processor speed. When the laptop is on mains power, Balanced (Microsoft's recommended plan) lets it speed up when needed and slow down when idle.";
    public override RiskLevel Risk => RiskLevel.Low;
    public override IReadOnlyList<string> Preconditions => ["Laptop is plugged in", "Power saver is currently active", "Balanced plan exists"];
    public override OptimisationPreset AvailableIn => OptimisationPreset.All;
    public override OptimisationPreset SelectedByDefaultIn => OptimisationPreset.All;
    public override string? UncertaintyNote => null;

    protected override (PowerPlan?, string) ChooseTarget(PowerStatus power, PowerPlan active, IReadOnlyList<PowerPlan> available)
    {
        if (power.Source != PowerSource.Mains) return (null, "Only applies while plugged in.");
        if (active.Id != PowerPlan.PowerSaverId) return (null, $"\"{active.Name}\" is active, not Power saver.");
        var balanced = available.FirstOrDefault(p => p.Id == PowerPlan.BalancedId);
        return balanced is null ? (null, "The Balanced plan is not available on this device.") : (balanced, "Power saver is active on mains power.");
    }
}

/// <summary>
/// High performance keeps the processor at higher clock states. On thermally limited laptops the gain
/// can be small and heat/fan noise higher, so it is optional and explained. Used temporarily by
/// Trading Mode when the user allows it; never on battery.
/// </summary>
public sealed class HighPerformanceWhenPluggedInOptimisation(IPowerPlanManager plans, ISystemInfoProvider info) : PowerPlanSwitchOptimisation(plans, info)
{
    public const string OptimisationId = "power.high-performance-on-mains";
    public override string Id => OptimisationId;
    public override string Title => "Use High performance power plan while plugged in";
    public override string Description => "Lets the processor respond faster to sudden load, such as a chart redrawing during volatile markets.";
    public override RiskLevel Risk => RiskLevel.Moderate;
    public override IReadOnlyList<string> Preconditions => ["Laptop is plugged in", "High performance plan exists (not present on many Modern Standby laptops)", "Balanced or Power saver currently active"];
    public override OptimisationPreset AvailableIn => OptimisationPreset.Balanced | OptimisationPreset.Trading;
    public override OptimisationPreset SelectedByDefaultIn => OptimisationPreset.None;
    public override string? UncertaintyNote => "Benefit varies. On small, warm laptops it can increase heat and fan noise, and the firmware may slow the processor anyway to stay cool. Try it and compare.";

    protected override (PowerPlan?, string) ChooseTarget(PowerStatus power, PowerPlan active, IReadOnlyList<PowerPlan> available)
    {
        if (power.Source != PowerSource.Mains) return (null, "Only applies while plugged in; on battery it would drain the battery faster.");
        if (active.Id == PowerPlan.HighPerformanceId || active.Id == PowerPlan.UltimatePerformanceId) return (null, $"\"{active.Name}\" is already active.");
        if (active.Id != PowerPlan.BalancedId && active.Id != PowerPlan.PowerSaverId) return (null, $"A custom plan (\"{active.Name}\") is active; TICKSHIFT will not override it.");
        var high = available.FirstOrDefault(p => p.Id == PowerPlan.HighPerformanceId);
        return high is null
            ? (null, "The High performance plan is not available on this device (common on Modern Standby laptops). Windows' power mode slider in Settings may offer \"Best performance\" instead.")
            : (high, "Plugged in with a standard plan active.");
    }
}
