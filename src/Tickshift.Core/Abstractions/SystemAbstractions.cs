namespace Tickshift.Core.Abstractions;

/// <summary>Registers TICKSHIFT in the visible, per-user HKCU Run key. No hidden persistence.</summary>
public interface IAutostartManager
{
    bool IsEnabled();
    OperationResult SetEnabled(bool enabled);
}

public sealed record SystemRestoreStatus(bool? Enabled, bool DisabledByPolicy, string Explanation);

public interface ISystemRestoreInfo
{
    SystemRestoreStatus GetStatus();
}
