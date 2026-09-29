using System.Text.Json;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Models;
using Tickshift.Persistence;

namespace Tickshift.Optimization.Optimisations;

/// <summary>
/// Turns off Windows interface animations through documented SystemParametersInfo settings (the same
/// switches as Settings › Accessibility › Visual effects). Each original value is stored and restored.
/// </summary>
public sealed class VisualEffectsOptimisation(IVisualEffectsManager visuals) : IOptimisation
{
    public const string OptimisationId = "ui.reduce-animations";

    public string Id => OptimisationId;
    public string Title => "Reduce Windows animations";
    public string Description => "Turns off window, menu and list animations so the interface responds immediately. Your wallpaper, theme and fonts are not changed.";
    public OptimisationCategory Category => OptimisationCategory.Interface;
    public RiskLevel Risk => RiskLevel.Low;
    public PrivilegeLevel RequiredPrivilege => PrivilegeLevel.StandardUser;
    public Reversibility Reversibility => Reversibility.FullyReversible;
    public IReadOnlyList<string> Preconditions => ["At least one animation setting is on"];
    public OptimisationPreset AvailableIn => OptimisationPreset.All;
    public OptimisationPreset SelectedByDefaultIn => OptimisationPreset.Balanced | OptimisationPreset.Trading;
    public string? UncertaintyNote => "Makes the interface feel snappier on older graphics; it does not change how fast apps or charts compute.";
    public bool RequiresExplicitApproval => false;

    public Task<OptimisationAssessment> AssessAsync(OptimisationContext context, CancellationToken ct)
    {
        var current = visuals.GetCurrent();
        if (!current.AnyEnabled) return Task.FromResult(OptimisationAssessment.NotApplicable("Animations are already off."));
        return Task.FromResult(new OptimisationAssessment(true, "Some interface animations are on.",
            $"Turn off: {string.Join(", ", Describe(current))}.", null, JsonSerializer.Serialize(current)));
    }

    public async Task<ApplyOutcome> ApplyAsync(OptimisationContext context, OptimisationAssessment assessment, IChangeRecorder recorder, CancellationToken ct)
    {
        var original = JsonSerializer.Deserialize<VisualEffectsState>(assessment.State!)!;
        var id = await recorder.BeginAsync("Windows animation settings", Title, string.Join(", ", Describe(original)) + " on", "All off", true, assessment.State, ct).ConfigureAwait(false);
        try
        {
            visuals.Apply(VisualEffectsState.Reduced);
        }
        catch (Exception ex)
        {
            // Best-effort: restore whatever may have partially changed.
            try { visuals.Apply(original); } catch { }
            await recorder.MarkFailedAsync(id, ex.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await recorder.MarkAppliedAsync(id, ct).ConfigureAwait(false);
        return new ApplyOutcome(OperationResult.Success("Windows animations turned off."), id == Guid.Empty ? [] : [id]);
    }

    public Task<VerificationResult> VerifyAsync(OptimisationContext context, OptimisationAssessment assessment, ApplyOutcome outcome, CancellationToken ct)
    {
        var now = visuals.GetCurrent();
        return Task.FromResult(!now.AnyEnabled
            ? new VerificationResult(true, "Windows reports all targeted animations are off.")
            : new VerificationResult(false, $"Still on: {string.Join(", ", Describe(now))}."));
    }

    public Task<OperationResult> RollbackAsync(ChangeRecord record, CancellationToken ct)
    {
        var original = JsonSerializer.Deserialize<VisualEffectsState>(record.RollbackData ?? "null");
        if (original is null) return Task.FromResult(OperationResult.Failure("Rollback information is missing."));
        var now = visuals.GetCurrent();
        if (now == original) return Task.FromResult(OperationResult.Success("Animation settings already match the original values."));
        if (now != VisualEffectsState.Reduced)
            return Task.FromResult(new OperationResult(OperationStatus.Skipped, "Animation settings were changed by something else since, so they were left as is.", Core.Errors.ErrorKind.Conflict));

        visuals.Apply(original);
        return Task.FromResult(visuals.GetCurrent() == original
            ? OperationResult.Success("Original animation settings restored.")
            : OperationResult.Failure("Animation settings could not be verified after restoring."));
    }

    public Task<InterruptedState> InspectInterruptedAsync(ChangeRecord record, CancellationToken ct)
    {
        var original = JsonSerializer.Deserialize<VisualEffectsState>(record.RollbackData ?? "null");
        var now = visuals.GetCurrent();
        return Task.FromResult(original is null ? InterruptedState.Unknown
            : now == original ? InterruptedState.NotApplied
            : InterruptedState.Applied);
    }

    private static IEnumerable<string> Describe(VisualEffectsState s)
    {
        if (s.ClientAreaAnimation) yield return "window content animations";
        if (s.MinimizeMaximizeAnimation) yield return "minimise/maximise animation";
        if (s.MenuAnimation) yield return "menu animation";
        if (s.ComboBoxAnimation) yield return "drop-down animation";
        if (s.ListBoxSmoothScrolling) yield return "smooth list scrolling";
        if (s.TooltipAnimation) yield return "tooltip animation";
    }
}
