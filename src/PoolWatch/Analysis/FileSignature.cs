using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PoolWatch.Analysis;

/// <summary>How a file is signed: by an embedded Authenticode signature, through a catalog (.cat) that lists its hash
/// (most of Windows, and catalog-signed Microsoft binaries outside %WINDIR%), not at all, or with a signature that did not verify.</summary>
internal enum SignatureKind { None, Embedded, Catalog, Untrusted }

/// <param name="Signer">The signing certificate's simple name (from the verified signature; for <see cref="SignatureKind.Untrusted"/>
/// the name the signature claims, when Windows could read it), or null.</param>
/// <param name="Organization">The signing certificate's O= attribute (".NET" binaries: CN=.NET, O=Microsoft Corporation), or null.</param>
/// <param name="Subject">The signing certificate's whole subject (every attribute, as X.500 text), or null.</param>
/// <param name="RootThumbprint">The SHA-256 of the root certificate the signature's chain ends in, as WinVerifyTrust built it, or null.</param>
internal sealed record SignatureInfo(SignatureKind Kind, string? Signer, uint Result, string? Organization = null, string? Subject = null, string? RootThumbprint = null);

/// <summary>
/// Authenticode check with WinVerifyTrust: the embedded signature, else the system catalog that lists the file's hash
/// (CryptCATAdmin*, SHA-256 then SHA-1). The signer's name is read from the verified trust state (WTHelper*), so the
/// obsolete <c>X509Certificate.CreateFromSignedFile</c> (SYSLIB0057) is not used. Revocation checks are off and URL
/// retrieval is cache-only, so this never touches the network.
/// </summary>
internal static class FileSignature
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_CHOICE_CATALOG = 2, WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10, WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
    private const uint TRUST_E_NOSIGNATURE = 0x800B0100, TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003, TRUST_E_PROVIDER_UNKNOWN = 0x800B0001;
    private const uint CERT_NAME_SIMPLE_DISPLAY_TYPE = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszCatalogFilePath;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszMemberTag;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pInfo;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr pSgnr, uint idxCert);
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin, IntPtr pgSubsystem, string? pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, SafeFileHandle hFile, ref uint pcbHash, byte[]? pbHash, uint dwFlags);
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, uint cbHash, uint dwFlags, IntPtr phPrevCatInfo);
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CATALOG_INFO psCatInfo, uint dwFlags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);
    [DllImport("crypt32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CertGetNameStringW(IntPtr pCertContext, uint dwType, uint dwFlags, IntPtr pvTypePara, char[]? pszNameString, uint cchNameString);

    /// <summary>Returns (status text, signer subject or null), for incident reports.</summary>
    public static (string status, string? signer) Check(string path)
    {
        try
        {
            var s = Verify(path);
            string status = s.Kind switch
            {
                SignatureKind.Embedded => "valid embedded signature",
                SignatureKind.Catalog => "valid catalog signature",
                SignatureKind.None => "not signed (no embedded signature, not in a system catalog)",
                _ => $"signature not trusted (0x{s.Result:X8})",
            };
            return (status, s.Signer);
        }
        catch (Exception ex) { return ($"not checked ({ex.Message})", null); }
    }

    /// <summary>Verifies the embedded signature, else the catalog signature. Never throws for an unsigned file.</summary>
    public static SignatureInfo Verify(string path)
    {
        var (result, who) = VerifyEmbedded(path);
        if (result == 0) return new SignatureInfo(SignatureKind.Embedded, who.Name, 0, who.Org, who.Subject, who.Root);
        bool noEmbedded = result is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN;
        foreach (var alg in new[] { "SHA256", null }) // null: the SHA-1 catalogs of older binaries
            if (VerifyCatalog(path, alg) is { } cat) return cat;
        return noEmbedded ? new SignatureInfo(SignatureKind.None, null, result) : new SignatureInfo(SignatureKind.Untrusted, who.Name, result, who.Org);
    }

    /// <summary>
    /// Pure: whether <paramref name="candidate"/> is signed by the same publisher as <paramref name="reference"/>, through
    /// the same certificate authority: both valid embedded signatures, the same whole subject on the signing certificate
    /// (every attribute, not only the name) and chains that end in the same root certificate. A certificate someone made
    /// with that subject under a root of their own (one added to their own certificate store, say) ends in another root;
    /// a renewed certificate of the same publisher from the same authority still matches. Nothing names the publisher.
    /// </summary>
    internal static bool SamePublisher(SignatureInfo? candidate, SignatureInfo? reference) =>
        candidate is { Kind: SignatureKind.Embedded, Subject: { Length: > 0 } subject, RootThumbprint: { Length: > 0 } root } &&
        CanBeMatched(reference) && string.Equals(subject, reference!.Subject, StringComparison.Ordinal) &&
        string.Equals(root, reference.RootThumbprint, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether any signature could be <see cref="SamePublisher"/> as <paramref name="reference"/>: only a valid embedded one
    /// whose subject and root were read (an unsigned development build of PoolWatch has none, so nothing matches it).
    /// </summary>
    internal static bool CanBeMatched(SignatureInfo? reference) =>
        reference is { Kind: SignatureKind.Embedded, Subject.Length: > 0, RootThumbprint.Length: > 0 };

    private static (uint result, Who who) VerifyEmbedded(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        Marshal.StructureToPtr(fileInfo, pFile, false);
        try { return Run(WTD_CHOICE_FILE, pFile); }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    /// <summary>The file's hash looked up in the system catalogs, and the catalog's signature verified for it; null when no
    /// catalog lists the file.</summary>
    private static SignatureInfo? VerifyCatalog(string path, string? hashAlgorithm)
    {
        if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, hashAlgorithm, IntPtr.Zero, 0)) return null;
        IntPtr catInfo = IntPtr.Zero;
        try
        {
            byte[] hash;
            using (var fh = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                uint size = 0;
                CryptCATAdminCalcHashFromFileHandle2(admin, fh, ref size, null, 0);
                if (size == 0 || size > 64) return null;
                hash = new byte[size];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, fh, ref size, hash, 0)) return null;
            }
            catInfo = CryptCATAdminEnumCatalogFromHash(admin, hash, (uint)hash.Length, 0, IntPtr.Zero);
            if (catInfo == IntPtr.Zero) return null;
            var ci = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>(), wszCatalogFile = "" };
            if (!CryptCATCatalogInfoFromContext(catInfo, ref ci, 0)) return null;
            IntPtr pHash = Marshal.AllocHGlobal(hash.Length);
            IntPtr pCat = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
            try
            {
                Marshal.Copy(hash, 0, pHash, hash.Length);
                var info = new WINTRUST_CATALOG_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(), pcwszCatalogFilePath = ci.wszCatalogFile,
                    pcwszMemberTag = Convert.ToHexString(hash), pcwszMemberFilePath = path,
                    pbCalculatedFileHash = pHash, cbCalculatedFileHash = (uint)hash.Length, hCatAdmin = admin,
                };
                Marshal.StructureToPtr(info, pCat, false);
                try
                {
                    var (result, who) = Run(WTD_CHOICE_CATALOG, pCat);
                    return result == 0 ? new SignatureInfo(SignatureKind.Catalog, who.Name, 0, who.Org) : new SignatureInfo(SignatureKind.Untrusted, who.Name, result, who.Org);
                }
                finally { Marshal.DestroyStructure<WINTRUST_CATALOG_INFO>(pCat); }
            }
            finally
            {
                Marshal.FreeHGlobal(pCat);
                Marshal.FreeHGlobal(pHash);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        finally
        {
            if (catInfo != IntPtr.Zero) CryptCATAdminReleaseCatalogContext(admin, catInfo, 0);
            CryptCATAdminReleaseContext(admin, 0);
        }
    }

    /// <summary>One WinVerifyTrust verify + close, reading the signer from the trust state in between.</summary>
    private static (uint result, Who who) Run(uint choice, IntPtr pInfo)
    {
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = WTD_UI_NONE, fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = choice, pInfo = pInfo, dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
        };
        var action = GenericVerifyV2;
        uint result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        Who who = new(null, null, null, null);
        try { who = Signer(data.hWVTStateData); }
        finally
        {
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        return (result, who);
    }

    /// <summary>The first signer's certificate: its simple name, organization (O=) and whole subject, and the SHA-256 of the root its chain ends in.</summary>
    private sealed record Who(string? Name, string? Org, string? Subject, string? Root);

    /// <summary>The first signer, from WinVerifyTrust's state (each part null when it cannot be read).</summary>
    private static Who Signer(IntPtr state)
    {
        var none = new Who(null, null, null, null);
        if (state == IntPtr.Zero) return none;
        IntPtr prov = WTHelperProvDataFromStateData(state);
        if (prov == IntPtr.Zero) return none;
        IntPtr sgnr = WTHelperGetProvSignerFromChain(prov, 0, false, 0);
        if (sgnr == IntPtr.Zero) return none;
        IntPtr cert = CertOf(WTHelperGetProvCertFromChain(sgnr, 0));
        if (cert == IntPtr.Zero) return none;
        string? name = NameString(cert, CERT_NAME_SIMPLE_DISPLAY_TYPE, IntPtr.Zero);
        string? org, subject;
        IntPtr oid = Marshal.StringToHGlobalAnsi(OrganizationOid);
        try { org = NameString(cert, CERT_NAME_ATTR_TYPE, oid); }
        finally { Marshal.FreeHGlobal(oid); }
        IntPtr x500 = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(x500, CERT_X500_NAME_STR);
            subject = NameString(cert, CERT_NAME_RDN_TYPE, x500);
        }
        finally { Marshal.FreeHGlobal(x500); }
        // CRYPT_PROVIDER_SGNR { DWORD cbStruct; FILETIME sftVerifyAsOf; DWORD csCertChain; ... }: the chain's length, then its last (the root).
        int chain = Marshal.ReadInt32(sgnr, 12);
        string? root = chain > 0 && chain < 32 && CertOf(WTHelperGetProvCertFromChain(sgnr, (uint)(chain - 1))) is var top && top != IntPtr.Zero ? Thumbprint(top) : null;
        return new Who(name, org, subject, root);
    }

    /// <summary>CRYPT_PROVIDER_CERT { DWORD cbStruct; PCCERT_CONTEXT pCert; ... }: its certificate, or zero.</summary>
    private static IntPtr CertOf(IntPtr providerCert) => providerCert == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(providerCert, IntPtr.Size);

    /// <summary>CERT_CONTEXT { DWORD dwCertEncodingType; BYTE* pbCertEncoded; DWORD cbCertEncoded; ... }: the SHA-256 of the encoded certificate, or null.</summary>
    private static string? Thumbprint(IntPtr cert)
    {
        IntPtr bytes = Marshal.ReadIntPtr(cert, IntPtr.Size);
        int length = Marshal.ReadInt32(cert, 2 * IntPtr.Size);
        if (bytes == IntPtr.Zero || length is <= 0 or > 64 * 1024) return null;
        var encoded = new byte[length];
        Marshal.Copy(bytes, encoded, 0, length);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(encoded));
    }

    private const uint CERT_NAME_ATTR_TYPE = 3, CERT_NAME_RDN_TYPE = 2;
    private const int CERT_X500_NAME_STR = 3;
    private const string OrganizationOid = "2.5.4.10"; // szOID_ORGANIZATION_NAME

    private static string? NameString(IntPtr cert, uint type, IntPtr para)
    {
        uint n = CertGetNameStringW(cert, type, 0, para, null, 0);
        if (n <= 1 || n > 1024) return null;
        var buf = new char[n];
        n = CertGetNameStringW(cert, type, 0, para, buf, n);
        return n > 1 ? new string(buf, 0, (int)n - 1) : null;
    }
}
