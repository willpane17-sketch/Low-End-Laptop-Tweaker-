using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Tickshift.Security;

/// <summary>Protects small pieces of locally stored data. TICKSHIFT stores no passwords or credentials.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string? Unprotect(string protectedText);
}

/// <summary>DPAPI (current user scope): data can only be decrypted by the same Windows user on the same machine.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "TICKSHIFT.v1.local"u8.ToArray();

    public string Protect(string plaintext) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string protectedText)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>Used on non-Windows test hosts only.</summary>
public sealed class PassthroughSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => plaintext;
    public string? Unprotect(string protectedText) => protectedText;
}
