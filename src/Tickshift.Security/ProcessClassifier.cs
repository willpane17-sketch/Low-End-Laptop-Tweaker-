using Tickshift.Core.Models;

namespace Tickshift.Security;

public static class ProcessClassifier
{
    public static ProcessCategory Classify(string name, string? path, int sessionId, int ownPid, int pid, ProtectedApplicationMatcher matcher)
    {
        if (pid == ownPid) return ProcessCategory.Self;
        if (pid is 0 or 4 || ProcessCatalog.IsCritical(name)) return ProcessCategory.Critical;
        if (ProcessCatalog.IsSecurity(name)) return ProcessCategory.Security;
        if (matcher.IsProtected(name, path)) return ProcessCategory.ProtectedTrading;
        if (sessionId == 0) return ProcessCategory.WindowsComponent; // services session
        if (ProcessCatalog.IsInWindowsDirectory(path)) return ProcessCategory.WindowsComponent;
        if (ProcessCatalog.IsBrowser(name)) return ProcessCategory.Browser;
        return path is null ? ProcessCategory.Unknown : ProcessCategory.Application;
    }

    /// <summary>Final gate before any close request. Defence in depth on top of UI filtering.</summary>
    public static bool MayOfferClose(ProcessSnapshot p) =>
        p.Category is ProcessCategory.Application or ProcessCategory.Browser && p.SessionId != 0
        && !ProcessCatalog.IsCritical(p.Name) && !ProcessCatalog.IsSecurity(p.Name);
}
