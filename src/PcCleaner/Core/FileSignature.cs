using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace PcCleaner.Core;

public enum SignatureStatus
{
    /// <summary>Valid Authenticode signature (embedded or Windows catalog).</summary>
    Valid,
    /// <summary>No signature at all.</summary>
    Unsigned,
    /// <summary>Signature present but broken, tampered, revoked or explicitly distrusted.</summary>
    Invalid,
    /// <summary>Signed, but the certificate chain is not trusted (self-signed, unknown root).</summary>
    Untrusted,
    NotFound,
    Error,
}

public sealed record SignatureInfo(SignatureStatus Status, string? Publisher, bool IsCatalogSigned = false)
{
    public bool IsValid => Status == SignatureStatus.Valid;

    /// <summary>
    /// The file has no Authenticode signature of its own but belongs to a signed Microsoft Store / MSIX package that
    /// Windows installed into %ProgramFiles%\WindowsApps (see <see cref="PackageSignature"/>). <see cref="Publisher"/>
    /// is then the package's publisher display name and <see cref="PackagePublisher"/> its full publisher name.
    /// </summary>
    public bool IsPackageSigned { get; init; }

    /// <summary>Publisher of the package ("CN=..., O=..., C=US") when <see cref="IsPackageSigned"/>; otherwise null.</summary>
    public string? PackagePublisher { get; init; }

    /// <summary>
    /// Valid signature by Microsoft. For package-signed files this comes from the package publisher (O=Microsoft
    /// Corporation), never from the display name, which is not checked against the certificate.
    /// </summary>
    public bool IsMicrosoft => IsValid && (IsPackageSigned
        ? PackageSignature.IsMicrosoftPublisher(PackagePublisher)
        : Publisher is not null && Publisher.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase));

    public string StatusText => Status switch
    {
        SignatureStatus.Valid when IsPackageSigned => Publisher is null
            ? "Подписан (пакет приложения)"
            : $"Подписан: {Publisher} (пакет приложения)",
        SignatureStatus.Valid => Publisher is null ? "Подписан" : $"Подписан: {Publisher}",
        SignatureStatus.Unsigned => "Без подписи",
        SignatureStatus.Invalid => "Подпись повреждена!",
        SignatureStatus.Untrusted => Publisher is null ? "Недоверенная подпись" : $"Недоверенная подпись: {Publisher}",
        SignatureStatus.NotFound => "Файл не найден",
        _ => "Не удалось проверить",
    };
}

/// <summary>
/// Authenticode verification via WinVerifyTrust, including catalog-signed Windows files. Files of signed Microsoft
/// Store / MSIX packages in %ProgramFiles%\WindowsApps, which have no signature of their own, are checked through their
/// package (<see cref="SignatureInfo.IsPackageSigned"/>).
/// Works offline (no revocation checks). Results are cached per file version. Thread-safe.
/// </summary>
public static class FileSignature
{
    private static readonly ConcurrentDictionary<string, SignatureInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static SignatureInfo Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new SignatureInfo(SignatureStatus.NotFound, null);
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
                return new SignatureInfo(SignatureStatus.NotFound, null);
            var key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            return Cache.GetOrAdd(key, _ => Verify(fi.FullName));
        }
        catch (Exception ex)
        {
            Log.Warn($"Проверка подписи {path}: {ErrorText.For(ex)}");
            return new SignatureInfo(SignatureStatus.Error, null);
        }
    }

    public static Task<SignatureInfo> GetAsync(string? path, CancellationToken ct = default)
        => Task.Run(() => Get(path), ct);

    private static SignatureInfo Verify(string path)
    {
        var embedded = VerifyEmbedded(path);
        if (embedded.Status != SignatureStatus.Unsigned)
            return embedded;
        var catalog = VerifyCatalog(path);
        if (catalog is not null)
            return catalog;
        // Store / MSIX app files are covered by the package signature instead (only inside WindowsApps).
        return PackageSignature.TryVerify(path) ?? embedded;
    }

    private static SignatureInfo VerifyEmbedded(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            return RunWinVerifyTrust(WTD_CHOICE_FILE, pFile, isCatalog: false);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    private static SignatureInfo? VerifyCatalog(string path)
    {
        foreach (var algorithm in new[] { "SHA256", "SHA1" })
        {
            var result = VerifyCatalogWith(path, algorithm);
            if (result is not null)
                return result;
        }
        return null;
    }

    private static SignatureInfo? VerifyCatalogWith(string path, string algorithm)
    {
        if (!CryptCATAdminAcquireContext2(out var catAdmin, IntPtr.Zero, algorithm, IntPtr.Zero, 0))
            return null;
        var catInfoHandle = IntPtr.Zero;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            uint hashSize = 0;
            CryptCATAdminCalcHashFromFileHandle2(catAdmin, handle, ref hashSize, null, 0);
            if (hashSize == 0)
                return null;
            var hash = new byte[hashSize];
            if (!CryptCATAdminCalcHashFromFileHandle2(catAdmin, handle, ref hashSize, hash, 0))
                return null;

            var prev = IntPtr.Zero;
            catInfoHandle = CryptCATAdminEnumCatalogFromHash(catAdmin, hash, hashSize, 0, ref prev);
            if (catInfoHandle == IntPtr.Zero)
                return null;

            var catInfo = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
            if (!CryptCATCatalogInfoFromContext(catInfoHandle, ref catInfo, 0))
                return null;

            var hashPin = GCHandle.Alloc(hash, GCHandleType.Pinned);
            var info = new WINTRUST_CATALOG_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                pcwszCatalogFilePath = catInfo.wszCatalogFile,
                pcwszMemberTag = Convert.ToHexString(hash),
                pcwszMemberFilePath = path,
                pbCalculatedFileHash = hashPin.AddrOfPinnedObject(),
                cbCalculatedFileHash = hashSize,
                hCatAdmin = catAdmin,
            };
            var pInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
            try
            {
                Marshal.StructureToPtr(info, pInfo, false);
                var res = RunWinVerifyTrust(WTD_CHOICE_CATALOG, pInfo, isCatalog: true);
                return res;
            }
            finally
            {
                Marshal.DestroyStructure<WINTRUST_CATALOG_INFO>(pInfo);
                Marshal.FreeHGlobal(pInfo);
                hashPin.Free();
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (catInfoHandle != IntPtr.Zero)
                CryptCATAdminReleaseCatalogContext(catAdmin, catInfoHandle, 0);
            CryptCATAdminReleaseContext(catAdmin, 0);
        }
    }

    private static SignatureInfo RunWinVerifyTrust(uint unionChoice, IntPtr pInfo, bool isCatalog)
    {
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = unionChoice,
            pInfo = pInfo,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
        };
        var pData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
        try
        {
            Marshal.StructureToPtr(data, pData, false);
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var hr = WinVerifyTrust(IntPtr.Zero, ref action, pData);
            data = Marshal.PtrToStructure<WINTRUST_DATA>(pData);
            var publisher = data.hWVTStateData != IntPtr.Zero ? GetSigner(data.hWVTStateData) : null;

            data.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(data, pData, true);
            WinVerifyTrust(IntPtr.Zero, ref action, pData);

            var status = (uint)hr switch
            {
                0 => SignatureStatus.Valid,
                TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN => SignatureStatus.Unsigned,
                CERT_E_UNTRUSTEDROOT or CERT_E_CHAINING or CERT_E_UNTRUSTEDTESTROOT => SignatureStatus.Untrusted,
                CERT_E_EXPIRED when publisher is not null => SignatureStatus.Untrusted,
                _ => SignatureStatus.Invalid,
            };
            return new SignatureInfo(status, publisher, isCatalog && status == SignatureStatus.Valid);
        }
        finally
        {
            Marshal.FreeHGlobal(pData);
        }
    }

    private static string? GetSigner(IntPtr stateData)
    {
        try
        {
            var provData = WTHelperProvDataFromStateData(stateData);
            if (provData == IntPtr.Zero)
                return null;
            var signer = WTHelperGetProvSignerFromChain(provData, 0, false, 0);
            if (signer == IntPtr.Zero)
                return null;
            var provCert = WTHelperGetProvCertFromChain(signer, 0);
            if (provCert == IntPtr.Zero)
                return null;
            // CRYPT_PROVIDER_CERT { DWORD cbStruct; PCCERT_CONTEXT pCert; ... }
            var pCert = Marshal.ReadIntPtr(provCert, IntPtr.Size);
            if (pCert == IntPtr.Zero)
                return null;
            using var cert = new X509Certificate2(pCert);
            var name = cert.GetNameInfo(X509NameType.SimpleName, false);
            return string.IsNullOrWhiteSpace(name) ? cert.Subject : name;
        }
        catch
        {
            return null;
        }
    }

    // ---- interop ----

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_CHOICE_CATALOG = 2;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
    private const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;
    private const uint TRUST_E_PROVIDER_UNKNOWN = 0x800B0001;
    private const uint CERT_E_UNTRUSTEDROOT = 0x800B0109;
    private const uint CERT_E_CHAINING = 0x800B010A;
    private const uint CERT_E_UNTRUSTEDTESTROOT = 0x800B010D;
    private const uint CERT_E_EXPIRED = 0x800B0101;

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pInfo;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin, IntPtr pgSubsystem,
        string pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, SafeFileHandle hFile,
        ref uint pcbHash, byte[]? pbHash, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, uint cbHash,
        uint dwFlags, ref IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CATALOG_INFO psCatInfo, uint dwFlags);

    [DllImport("wintrust.dll")]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

    [DllImport("wintrust.dll")]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner,
        [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr pSgnr, uint idxCert);
}
