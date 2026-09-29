using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tickshift.Core.Models;

namespace Tickshift.Security;

public interface ISignatureVerifier
{
    SignatureState Verify(string filePath);
}

/// <summary>
/// Verifies embedded Authenticode signatures with WinVerifyTrust (no network revocation check,
/// so it is fast and works offline). Catalog-signed Windows binaries report
/// <see cref="SignatureState.NotSigned"/> here, which the UI words as "no embedded signature".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AuthenticodeVerifier : ISignatureVerifier
{
    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    private const uint TrustENoSignature = 0x800B0100;
    private const uint TrustESubjectFormUnknown = 0x800B0003;
    private const uint TrustEProviderUnknown = 0x800B0001;

    public SignatureState Verify(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return SignatureState.Unknown;

        var fileInfo = new WintrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            pcwszFilePath = filePath,
        };

        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPtr,
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdCacheOnlyUrlRetrieval,
            };

            var action = WintrustActionGenericVerifyV2;
            var result = (uint)WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            data.dwStateAction = WtdStateActionClose;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            return result switch
            {
                0 => SignatureState.Valid,
                TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown => SignatureState.NotSigned,
                _ => SignatureState.Invalid,
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return SignatureState.Unknown;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WintrustData pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}

/// <summary>Caches signature results per path; verification is comparatively expensive.</summary>
public sealed class CachingSignatureVerifier(ISignatureVerifier inner) : ISignatureVerifier
{
    private readonly Dictionary<string, SignatureState> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public SignatureState Verify(string filePath)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(filePath, out var cached)) return cached;
        }

        var state = inner.Verify(filePath);
        lock (_gate)
        {
            if (_cache.Count > 2000) _cache.Clear();
            _cache[filePath] = state;
        }

        return state;
    }
}
