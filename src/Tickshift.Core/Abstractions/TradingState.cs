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

/// <summary>
/// Shared Trading Mode flag. Written only by the Trading Mode service; read by everything else. Keeping
/// it separate avoids a dependency cycle between Trading Mode and the optimisation engine it uses.
/// </summary>
public sealed class TradingSessionFlag : ITradingSessionState
{
    private volatile bool _isActive;

    public bool IsActive => _isActive;

    public event EventHandler<bool>? ActiveChanged;

    public void Set(bool active)
    {
        if (_isActive == active) return;
        _isActive = active;
        ActiveChanged?.Invoke(this, active);
    }
}
