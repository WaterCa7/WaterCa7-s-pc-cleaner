using System.Security.Cryptography;

namespace PcCleaner.Core.Updates;

/// <summary>ECDSA P-256 / SHA-256 signatures in DER form — what tools/make-update-manifest.ps1 (and `openssl dgst -sign`) produce.</summary>
public static class UpdateSignature
{
    public static bool Verify(byte[] data, byte[] signature, string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.KeySize == 256 &&
                   key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
