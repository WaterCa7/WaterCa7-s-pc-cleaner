namespace PcCleaner.Core.Updates;

/// <summary>
/// Public half of the release signing key — the same as tools/update-signing-public.pem (a test compares them). The private
/// half exists only as the GitHub Actions secret UPDATE_SIGNING_KEY and in the owner's backup.
/// </summary>
public static class UpdateKeys
{
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEk7IxP/4/ZylvTlXFCncc8Unat91/
        rSW52ALDU1jzKWUO2rd25u9Pckhu23vqImc3fVuQutnSHU4d9ZT6XaNywg==
        -----END PUBLIC KEY-----
        """;
}
