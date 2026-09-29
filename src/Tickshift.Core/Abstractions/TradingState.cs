namespace Tickshift.Core.Abstractions;

/// <summary>
/// Read-only view of Trading Mode used by other modules (automation, cleanup, recommendations) to
/// defer disruptive work without depending on the Trading module.
/// </summary>
public interface ITradingSessionState
{
    bool IsActive { get; }
    event EventHandler<bool>? ActiveChanged;
}
