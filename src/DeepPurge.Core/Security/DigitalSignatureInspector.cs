using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DeepPurge.Core.Security;

/// <summary>
/// Sysinternals-Autoruns-style signature validation using
/// <c>WinVerifyTrust</c> (the same API every AV and Windows itself use).
/// Results are cached by full path so we don't call into CryptoAPI repeatedly.
/// </summary>
public static class DigitalSignatureInspector
{
    private static readonly ConcurrentDictionary<string, SignatureInfo> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static SignatureInfo Inspect(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return SignatureInfo.Unknown;
        if (_cache.TryGetValue(filePath, out var cached)) return cached;

        SignatureInfo info;
        try
        {
            if (!File.Exists(filePath))
            {
                info = SignatureInfo.Missing;
            }
            else
            {
                var trust = VerifyTrust(filePath);
                var subject = trust == SignatureStatus.Signed
                    ? TryReadSubjectName(filePath)
                    : null;
                info = new SignatureInfo(trust, subject, filePath);
            }
        }
        catch
        {
            info = SignatureInfo.Unknown;
        }

        _cache[filePath] = info;
        return info;
    }

    public static void ClearCache() => _cache.Clear();

    // ═══════════════════════════════════════════════════════
    //  WinVerifyTrust
    // ═══════════════════════════════════════════════════════

    private static SignatureStatus VerifyTrust(string filePath)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfoPtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = null,
                dwProvFlags = WTD_SAFER_FLAG,
                dwUIContext = 0,
            };

            var policyGuid = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var result = WinVerifyTrust(IntPtr.Zero, ref policyGuid, ref data);

            // Always release trust data.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref policyGuid, ref data);

            return result switch
            {
                0 => SignatureStatus.Signed,
                unchecked((int)0x800B0100) => SignatureStatus.Unsigned,      // TRUST_E_NOSIGNATURE
                unchecked((int)0x800B010A) => SignatureStatus.ChainInvalid,  // CERT_E_UNTRUSTEDROOT
                unchecked((int)0x800B010C) => SignatureStatus.Revoked,       // TRUST_E_REVOKED
                _ => SignatureStatus.Invalid,
            };
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    /// <summary>
    /// Reads the signer's CN from the embedded certificate when a file is signed.
    /// Uses the legacy X509Certificate ctor — slower but avoids pulling
    /// System.Security.Cryptography.Pkcs at runtime.
    /// </summary>
    private static string? TryReadSubjectName(string filePath)
    {
        try
        {
            using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(filePath);
            var subject = cert.Subject;
            // "CN=Microsoft Corporation, O=Microsoft Corporation, ..." → "Microsoft Corporation"
            var cnIdx = subject.IndexOf("CN=", StringComparison.OrdinalIgnoreCase);
            if (cnIdx < 0) return subject;
            var remainder = subject[(cnIdx + 3)..];
            var comma = remainder.IndexOf(',');
            return comma > 0 ? remainder[..comma].Trim() : remainder.Trim();
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════
    //  P/Invoke — wintrust.dll
    // ═══════════════════════════════════════════════════════

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x00000100;

    private static Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("{00AAC56B-CD44-11D0-8CC2-00C04FC295EE}");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
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
        public string? pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);
}

public enum SignatureStatus
{
    Unknown,
    Signed,
    Unsigned,
    Invalid,
    ChainInvalid,
    Revoked,
    Missing,
}

public sealed record SignatureInfo(SignatureStatus Status, string? Subject, string FilePath)
{
    public static SignatureInfo Unknown { get; } = new(SignatureStatus.Unknown, null, "");
    public static SignatureInfo Missing { get; } = new(SignatureStatus.Missing, null, "");

    /// <summary>Short human-readable label for a DataGrid column.</summary>
    public string Display => Status switch
    {
        SignatureStatus.Signed => Subject ?? "Signed",
        SignatureStatus.Unsigned => "Unsigned",
        SignatureStatus.Invalid => "Invalid",
        SignatureStatus.ChainInvalid => "Untrusted",
        SignatureStatus.Revoked => "Revoked",
        SignatureStatus.Missing => "Missing",
        _ => "",
    };

    public bool IsTrusted => Status == SignatureStatus.Signed;
    public bool IsWarning => Status is SignatureStatus.Invalid or SignatureStatus.ChainInvalid or SignatureStatus.Revoked;
    public bool IsMissing => Status == SignatureStatus.Missing;
}
