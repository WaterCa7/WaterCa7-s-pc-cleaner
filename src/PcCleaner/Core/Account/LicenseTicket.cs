using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace PcCleaner.Core.Account;

/// <summary>The fields of a license ticket (the site's License/Ticket.php), read only after its signature was checked.</summary>
public sealed record LicenseTicketData(int V, long Sub, string Email, string Plan, string Kind, int DevicesMax, DateTimeOffset? ExpiresAt,
    string HwHash, long DeviceId, DateTimeOffset IssuedAt, DateTimeOffset ValidUntil);

/// <summary>
/// Checks a ticket the way the contract says: base64 → bytes → ECDSA P-256 / SHA-256 signature in DER over exactly these bytes by
/// any of the keys → only then the JSON, which must say <c>v: 1</c> and have every field. Anything else is null: an unsigned,
/// foreign, changed or broken ticket is no ticket.
/// </summary>
public static class LicenseTicket
{
    private const string P256 = "1.2.840.10045.3.1.7";

    public static LicenseTicketData? Verify(string? ticketB64, string? signatureB64, IReadOnlyList<string> publicKeysPem)
    {
        if (string.IsNullOrEmpty(ticketB64) || string.IsNullOrEmpty(signatureB64))
            return null;
        byte[] bytes, signature;
        try
        {
            bytes = Convert.FromBase64String(ticketB64);
            signature = Convert.FromBase64String(signatureB64);
        }
        catch (FormatException)
        {
            return null;
        }
        return publicKeysPem.Any(pem => SignedBy(pem, bytes, signature)) ? Parse(bytes) : null;
    }

    private static bool SignedBy(string pem, byte[] data, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            return key.ExportParameters(false).Curve.Oid.Value == P256 &&
                   key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    internal static LicenseTicketData? Parse(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.GetProperty("v").GetInt32() != 1)
                return null;
            var expires = r.GetProperty("expires_at");
            return new LicenseTicketData(
                1,
                r.GetProperty("sub").GetInt64(),
                r.GetProperty("email").GetString() ?? "",
                r.GetProperty("plan").GetString() ?? "",
                r.GetProperty("kind").GetString() ?? "",
                r.GetProperty("devices_max").GetInt32(),
                expires.ValueKind == JsonValueKind.Null ? null : Date(expires.GetString()),
                r.GetProperty("hw_hash").GetString() ?? "",
                r.GetProperty("device_id").GetInt64(),
                Date(r.GetProperty("issued_at").GetString()),
                Date(r.GetProperty("valid_until").GetString()));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static DateTimeOffset Date(string? iso)
        => DateTimeOffset.Parse(iso ?? throw new FormatException("no date"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
