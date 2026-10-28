using System.Net.Http;

namespace PcCleaner.Core.Account;

// The methods of /api/v1 used by the program (the site's docs/api-contract-for-app.md).
public sealed partial class ApiClient
{
    private sealed record Ok(bool? ok);

    private static Dictionary<string, object?> WithDevice(DeviceFields device, params (string Name, object? Value)[] fields)
    {
        var body = new Dictionary<string, object?>();
        foreach (var (name, value) in fields)
            body[name] = value;
        body["hw_hash"] = device.HwHash;
        body["platform"] = device.Platform;
        body["name"] = device.Name;
        body["app_version"] = device.AppVersion;
        return body;
    }

    private static Dictionary<string, object?> Quote(QuoteRequest r)
    {
        var body = new Dictionary<string, object?> { ["plan"] = r.Plan, ["term"] = r.Term, ["devices"] = r.Devices };
        if (!string.IsNullOrEmpty(r.Promo))
            body["promo"] = r.Promo;
        if (r.Change)
            body["change"] = true;
        return body;
    }

    // ----- sign-in -----

    public Task<LoginResult> LoginAsync(string email, string password, DeviceFields device, CancellationToken ct)
        => SendAsync<LoginResult>(HttpMethod.Post, "/auth/login", WithDevice(device, ("email", email), ("password", password)), null, ct);

    public Task RegisterAsync(string email, string password, string lang, DeviceFields device, CancellationToken ct)
        => SendAsync<Ok>(HttpMethod.Post, "/auth/register",
            WithDevice(device, ("email", email), ("password", password), ("consent", true), ("lang", lang)), null, ct);

    public Task<LoginResult> VerifyAsync(string email, string code, DeviceFields device, CancellationToken ct)
        => SendAsync<LoginResult>(HttpMethod.Post, "/auth/verify", WithDevice(device, ("email", email), ("code", code)), null, ct);

    /// <summary>A new code by mail; purpose "verify" or "reset". Always ok: it never tells whether the account exists.</summary>
    public Task<ResendResult> ResendAsync(string email, string purpose, CancellationToken ct)
        => SendAsync<ResendResult>(HttpMethod.Post, "/auth/resend", new { email, purpose }, null, ct);

    public Task ResetAsync(string email, CancellationToken ct)
        => SendAsync<Ok>(HttpMethod.Post, "/auth/reset", new { email }, null, ct);

    public Task<LoginResult> ResetConfirmAsync(string email, string code, string password, DeviceFields device, CancellationToken ct)
        => SendAsync<LoginResult>(HttpMethod.Post, "/auth/reset/confirm",
            WithDevice(device, ("email", email), ("code", code), ("password", password)), null, ct);

    public Task<BrowserCode> StartBrowserCodeAsync(DeviceFields device, CancellationToken ct)
        => SendAsync<BrowserCode>(HttpMethod.Post, "/auth/code", WithDevice(device), null, ct);

    /// <summary>One poll of a browser code: null while the site has not confirmed it yet (202), the sign-in when it has.</summary>
    public async Task<LoginResult?> PollBrowserCodeAsync(string code, CancellationToken ct)
    {
        var (status, text) = await SendRawAsync(HttpMethod.Get, "/auth/code/" + Uri.EscapeDataString(code), null, null, ct);
        if (status == 202)
            return null;
        if (status is >= 200 and < 300)
            return Read<LoginResult>(text, status);
        throw new ApiException(ParseError(status, text));
    }

    // ----- with the device token -----

    public Task LogoutAsync(string token, CancellationToken ct)
        => SendAsync<Ok>(HttpMethod.Post, "/auth/logout", new { }, token, ct);

    public Task<SignedTicket> TicketAsync(string token, CancellationToken ct)
        => SendAsync<SignedTicket>(HttpMethod.Get, "/license/ticket", null, token, ct);

    public Task<DeviceList> DevicesAsync(string token, CancellationToken ct)
        => SendAsync<DeviceList>(HttpMethod.Get, "/devices", null, token, ct);

    /// <summary>Unbinds a computer of the account; without an id — this one (its token dies).</summary>
    public Task UnbindAsync(string token, long? deviceId, CancellationToken ct)
        => SendAsync<Ok>(HttpMethod.Post, "/device/unbind",
            deviceId is { } id ? new Dictionary<string, object?> { ["device_id"] = id } : new Dictionary<string, object?>(), token, ct);

    public Task<SignedTicket> PromoApplyAsync(string token, string code, CancellationToken ct)
        => SendAsync<SignedTicket>(HttpMethod.Post, "/promo/apply", new { code }, token, ct);

    public Task<Quote> QuoteAsync(string token, QuoteRequest request, CancellationToken ct)
        => SendAsync<Quote>(HttpMethod.Post, "/pay/quote", Quote(request), token, ct);

    public Task<PaymentLink> PayStartAsync(string token, QuoteRequest request, CancellationToken ct)
        => SendAsync<PaymentLink>(HttpMethod.Post, "/pay/start", Quote(request), token, ct);

    public Task<PaymentLink> PayLinkAsync(string token, long paymentId, CancellationToken ct)
        => SendAsync<PaymentLink>(HttpMethod.Post, "/pay/link", new { payment_id = paymentId }, token, ct);

    public Task<PaymentStatus> PayStatusAsync(string token, long paymentId, CancellationToken ct)
        => SendAsync<PaymentStatus>(HttpMethod.Get, "/pay/" + paymentId, null, token, ct);

    // ----- without a token -----

    /// <summary>Releases a computer when a sign-in hit DEVICE_LIMIT: the one-time permit of the last DEVICE_LIMIT answer.</summary>
    public Task ReleaseAsync(string unbindToken, long deviceId, CancellationToken ct)
        => SendAsync<Ok>(HttpMethod.Post, "/device/release", new { unbind_token = unbindToken, device_id = deviceId }, null, ct);

    /// <summary>The price list; with the token <c>payments_enabled</c> tells whether this account may buy.</summary>
    public Task<PriceList> PricesAsync(string? token, CancellationToken ct)
        => SendAsync<PriceList>(HttpMethod.Get, "/prices", null, token, ct);

    /// <summary>"Write to us" (support request); the token (optional) ties the request to the account.</summary>
    public Task SupportAsync(string? token, SupportRequest request, string appVersion, CancellationToken ct)
        => SendAsync<Ok>(HttpMethod.Post, "/ticket", new
        {
            email = request.Email,
            topic = request.Topic,
            message = request.Message,
            app_version = appVersion,
            log = request.LogBase64,
        }, token, ct);
}
