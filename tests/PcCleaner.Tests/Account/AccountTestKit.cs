using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcCleaner.Core.Account;

namespace PcCleaner.Tests.Account;

/// <summary>A fake waterca7.ru: answers by "METHOD /path" (the query is ignored); the last answer of a route repeats.</summary>
internal sealed class FakeApi : HttpMessageHandler
{
    public const string Base = "https://api.test/api/v1";

    private readonly Dictionary<string, Queue<(int Status, string Body)>> _routes = new();

    public sealed record Recorded(string Method, string Path, string Query, string? Body, string? Bearer, string AcceptLanguage);

    private readonly object _gate = new();
    private readonly List<Recorded> _requests = new();

    /// <summary>A snapshot of the requests so far (background calls may add more meanwhile).</summary>
    public List<Recorded> Requests
    {
        get
        {
            lock (_gate)
                return _requests.ToList();
        }
    }

    /// <summary>When set, every request fails as if the network were down.</summary>
    public Exception? ThrowOnSend { get; set; }

    public FakeApi On(string route, int status, string body)
    {
        if (!_routes.TryGetValue(route, out var queue))
            _routes[route] = queue = new Queue<(int, string)>();
        queue.Enqueue((status, body));
        return this;
    }

    public FakeApi OnError(string route, int status, string code, string extraJson = "")
        => On(route, status, $"{{\"error\":\"{code}\",\"message\":\"msg {code}\"{extraJson}}}");

    public IEnumerable<Recorded> To(string route) => Requests.Where(r => $"{r.Method} {r.Path}" == route);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (ThrowOnSend is not null)
            throw ThrowOnSend;
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath["/api/v1".Length..];
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        lock (_gate)
            _requests.Add(new Recorded(request.Method.Method, path, uri.Query, body, request.Headers.Authorization?.Parameter,
                request.Headers.AcceptLanguage.ToString()));
        var key = $"{request.Method.Method} {path}";
        if (!_routes.TryGetValue(key, out var queue) || queue.Count == 0)
            return Answer(request, 404, "{\"error\":\"NOT_FOUND\",\"message\":\"not found\"}");
        var (status, text) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        return Answer(request, status, text);
    }

    private static HttpResponseMessage Answer(HttpRequestMessage request, int status, string body) => new((HttpStatusCode)status)
    {
        RequestMessage = request,
        Content = new StringContent(body, Encoding.UTF8, body.StartsWith('{') ? "application/json" : "text/html"),
    };
}

/// <summary>A license key made for one test run; tickets are signed the way the server signs them (DER).</summary>
internal sealed class TestLicense : IDisposable
{
    public const string Hw = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    public const long Sub = 7;
    public const long DeviceId = 42;

    public static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicPem => Key.ExportSubjectPublicKeyInfoPem();

    public IReadOnlyList<string> Keys => new[] { PublicPem };

    public static string Json(string plan = "pro", string kind = "subscription", long sub = Sub, long deviceId = DeviceId, string hw = Hw,
        DateTimeOffset? issued = null, DateTimeOffset? validUntil = null, DateTimeOffset? expires = null, bool noExpiry = false, int v = 1,
        int devicesMax = 1)
    {
        string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var issuedAt = issued ?? Now.AddDays(-1);
        var until = validUntil ?? issuedAt.AddDays(14);
        var exp = noExpiry ? "null" : $"\"{Iso(expires ?? Now.AddDays(300))}\"";
        return $"{{\"v\":{v},\"sub\":{sub},\"email\":\"user@example.com\",\"plan\":\"{plan}\",\"kind\":\"{kind}\",\"devices_max\":{devicesMax}," +
               $"\"expires_at\":{exp},\"hw_hash\":\"{hw}\",\"device_id\":{deviceId},\"issued_at\":\"{Iso(issuedAt)}\",\"valid_until\":\"{Iso(until)}\"}}";
    }

    public SignedTicket Sign(string json, DSASignatureFormat format = DSASignatureFormat.Rfc3279DerSequence)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return new SignedTicket(Convert.ToBase64String(bytes), Convert.ToBase64String(Key.SignData(bytes, HashAlgorithmName.SHA256, format)));
    }

    /// <summary>The JSON of a successful sign-in (login / verify / reset-confirm / code poll) with a ticket of this key.</summary>
    public string LoginJson(string ticketJson, string token = "t0ken", long deviceId = DeviceId)
    {
        var t = Sign(ticketJson);
        return $"{{\"token\":\"{token}\",\"account\":{{\"email\":\"user@example.com\",\"verified\":true}},\"device\":{{\"id\":{deviceId},\"name\":\"PC\"}}," +
               $"\"license\":{{\"ticket\":\"{t.Ticket}\",\"signature\":\"{t.Signature}\"}}}}";
    }

    public string TicketJson(string ticketJson)
    {
        var t = Sign(ticketJson);
        return $"{{\"ticket\":\"{t.Ticket}\",\"signature\":\"{t.Signature}\"}}";
    }

    public void Dispose() => Key.Dispose();
}

internal static class Api
{
    public static ApiClient Client(FakeApi fake, string language = "ru") => new(() => language, fake, FakeApi.Base);

    public static DeviceFields Device => new(TestLicense.Hw, "windows", "PC", "1.5.0");

    public static JsonElement Body(FakeApi.Recorded r) => JsonDocument.Parse(r.Body!).RootElement;
}
