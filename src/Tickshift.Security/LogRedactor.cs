using System.Text.RegularExpressions;

namespace Tickshift.Security;

/// <summary>
/// Removes personal data from log lines and diagnostic reports: user profile paths, user names,
/// e-mail addresses, URL paths/queries (browsing activity) and anything that looks like a secret.
/// </summary>
public static partial class LogRedactor
{
    private static readonly string? ProfilePath = SafeGet(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private static readonly string? UserName = SafeGet(() => Environment.UserName);
    private static readonly string? MachineName = SafeGet(() => Environment.MachineName);

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var result = text;
        if (!string.IsNullOrEmpty(ProfilePath))
            result = result.Replace(ProfilePath, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

        result = EmailRegex().Replace(result, "[email]");
        result = UrlRegex().Replace(result, m => $"{m.Groups["scheme"].Value}://{m.Groups["host"].Value}/[redacted]");
        result = SecretRegex().Replace(result, m => $"{m.Groups["key"].Value}=[redacted]");

        if (!string.IsNullOrEmpty(UserName) && UserName.Length >= 3)
            result = Regex.Replace(result, $@"\b{Regex.Escape(UserName)}\b", "[user]", RegexOptions.IgnoreCase);
        if (!string.IsNullOrEmpty(MachineName) && MachineName.Length >= 3)
            result = Regex.Replace(result, $@"\b{Regex.Escape(MachineName)}\b", "[machine]", RegexOptions.IgnoreCase);

        return result;
    }

    private static string? SafeGet(Func<string> f)
    {
        try { return f(); } catch { return null; }
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<scheme>https?)://(?<host>[A-Za-z0-9.-]+(:\d+)?)(/[^\s""']*)?", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?<key>password|passwd|pwd|token|secret|apikey|api_key|auth|login)\s*[=:]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretRegex();
}
