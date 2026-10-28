using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core.AccountWindows;

/// <summary>Builds the AccountService of this run: the real one, or a local one for --screenshots (no network, any tier).</summary>
public static class AccountSetup
{
    public static AccountService CreateReal() => new(new ApiClient(() => T.Language), new DpapiAccountStore(), new WmiHwHash(), "windows",
        Environment.MachineName, AppInfo.Version, LicenseKeys.Live);

    /// <summary>
    /// A screenshot run's account: in memory, its ticket signed by a key made here (trusted by this account only), the network
    /// replaced by <see cref="ScreenshotApi"/>. Nothing is read from or written to the user's account.bin.
    /// </summary>
    public static AccountService ForScreenshots(Tier tier)
    {
        const string hw = "screenshot";
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = new MemoryStore();
        var now = DateTimeOffset.UtcNow;
        if (tier != Tier.Guest)
        {
            string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            var plan = tier == Tier.Pro ? "pro" : "free";
            var expires = tier == Tier.Pro ? $"\"{Iso(now.AddDays(300))}\"" : "null";
            var json = $"{{\"v\":1,\"sub\":7,\"email\":\"user@example.com\",\"plan\":\"{plan}\",\"kind\":\"{(tier == Tier.Pro ? "subscription" : "none")}\"," +
                       $"\"devices_max\":{(tier == Tier.Pro ? 1 : 0)},\"expires_at\":{expires},\"hw_hash\":\"{HwHash.Of(hw)}\",\"device_id\":42," +
                       $"\"issued_at\":\"{Iso(now.AddHours(-2))}\",\"valid_until\":\"{Iso(now.AddDays(13))}\"}}";
            var bytes = Encoding.UTF8.GetBytes(json);
            var signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            store.Save(new AccountData("screenshot", "user@example.com", 42, 7, Convert.ToBase64String(bytes), Convert.ToBase64String(signature)));
        }
        var api = new ApiClient(() => T.Language, new ScreenshotApi(), ApiClient.LiveBaseUrl);
        return new AccountService(api, store, new FixedHash(HwHash.Of(hw)), "windows", "PC", AppInfo.Version,
            new[] { key.ExportSubjectPublicKeyInfoPem() });
    }

    private sealed class MemoryStore : IAccountStore
    {
        private AccountData _data = new();

        public AccountData Load() => _data;

        public void Save(AccountData data) => _data = data;
    }

    private sealed class FixedHash(string value) : IHwHash
    {
        public string Value { get; } = value;
    }

    /// <summary>The «server» of a screenshot run: two sample computers; everything else fails as if offline.</summary>
    private sealed class ScreenshotApi : HttpMessageHandler
    {
        private const string Devices = "{\"devices\":[" +
            "{\"id\":42,\"name\":\"DESKTOP-HOME\",\"platform\":\"windows\",\"bound_at\":\"2026-10-24T10:00:00Z\",\"last_seen_at\":\"2026-10-28T09:00:00Z\",\"current\":true}," +
            "{\"id\":43,\"name\":\"MacBook Air\",\"platform\":\"mac\",\"bound_at\":\"2026-10-25T18:30:00Z\",\"last_seen_at\":\"2026-10-27T21:15:00Z\",\"current\":false}" +
            "],\"bind_limit\":2}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/devices", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(Devices, Encoding.UTF8, "application/json"),
                });
            throw new HttpRequestException("screenshot run: no network");
        }
    }
}
