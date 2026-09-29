using System.Runtime.Versioning;
using System.Security.Principal;
using Tickshift.Core.Abstractions;

namespace Tickshift.Security;

[SupportedOSPlatform("windows")]
public sealed class WindowsElevationState : IElevationState
{
    private readonly Lazy<bool> _isElevated = new(() =>
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    });

    public bool IsElevated => _isElevated.Value;
}
