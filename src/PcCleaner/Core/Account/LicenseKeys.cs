using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PcCleaner.Core.Account;

/// <summary>
/// The public keys a license ticket may be signed with: the live key of waterca7.ru (Core/Account/license_pub.pem, embedded),
/// and a second one only while the server's key is being changed. Never the server's test key (a test checks the fingerprint).
/// A build without the file trusts no ticket: everyone stays free.
/// </summary>
public static class LicenseKeys
{
    internal const string ResourceName = "PcCleaner.Account.license_pub.pem";

    private static readonly Regex PemBlock = new("-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Live { get; } = Load(typeof(LicenseKeys).Assembly.GetManifestResourceStream(ResourceName));

    /// <summary>Every PEM public key block of the stream (null — no resource: none).</summary>
    internal static IReadOnlyList<string> Load(Stream? stream)
    {
        if (stream is null)
            return Array.Empty<string>();
        using (stream)
        {
            var text = new StreamReader(stream).ReadToEnd();
            return PemBlock.Matches(text).Select(m => m.Value).ToList();
        }
    }

    /// <summary>SHA-256 (lowercase hex) of the key's SubjectPublicKeyInfo: the same for any formatting of the PEM.</summary>
    public static string Fingerprint(string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }
}
