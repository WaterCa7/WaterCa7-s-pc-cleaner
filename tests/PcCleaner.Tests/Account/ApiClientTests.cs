using System.Net.Http;
using PcCleaner.Core.Account;
using Xunit;

namespace PcCleaner.Tests.Account;

public class ApiClientTests
{
    private sealed record Pong(bool Ok);

    [Fact]
    public async Task ARequest_CarriesTheLanguageTheBearerAndJson()
    {
        var fake = new FakeApi().On("POST /ping", 200, "{\"ok\":true}");
        using var api = Api.Client(fake, "en");

        var pong = await api.SendAsync<Pong>(HttpMethod.Post, "/ping", new { email = "a@b.c" }, "tok", CancellationToken.None);

        Assert.True(pong.Ok);
        var r = Assert.Single(fake.Requests);
        Assert.Equal("en", r.AcceptLanguage);
        Assert.Equal("tok", r.Bearer);
        Assert.Equal("a@b.c", Api.Body(r).GetProperty("email").GetString());
    }

    [Fact]
    public async Task WithoutAToken_ThereIsNoAuthorizationHeader()
    {
        var fake = new FakeApi().On("GET /ping", 200, "{\"ok\":true}");
        using var api = Api.Client(fake);

        await api.SendAsync<Pong>(HttpMethod.Get, "/ping", null, null, CancellationToken.None);

        Assert.Null(fake.Requests[0].Bearer);
        Assert.Null(fake.Requests[0].Body);
    }

    [Fact]
    public async Task AnErrorAnswer_BecomesApiException_WithTheCodeMessageAndPayload()
    {
        var fake = new FakeApi().OnError("POST /promo/apply", 422, "PROMO_INVALID", ",\"reason\":7");
        using var api = Api.Client(fake);

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.SendAsync<Pong>(HttpMethod.Post, "/promo/apply", new { code = "X" }, "t", CancellationToken.None));

        Assert.Equal(ApiError.PromoInvalid, ex.Error.Code);
        Assert.Equal("msg PROMO_INVALID", ex.Error.Message);
        Assert.Equal(422, ex.Error.HttpStatus);
        Assert.Equal("7", ex.Error.Reason);
    }

    [Fact]
    public async Task ATextReason_AndAStatus_AreReadToo()
    {
        var fake = new FakeApi()
            .OnError("POST /pay/quote", 409, "BLOCKED", ",\"reason\":\"no_change\"")
            .OnError("POST /pay/link", 409, "PAYMENT_NOT_PENDING", ",\"status\":\"paid\"");
        using var api = Api.Client(fake);

        var blocked = await Assert.ThrowsAsync<ApiException>(() => api.SendAsync<Pong>(HttpMethod.Post, "/pay/quote", new { }, "t", CancellationToken.None));
        var notPending = await Assert.ThrowsAsync<ApiException>(() => api.SendAsync<Pong>(HttpMethod.Post, "/pay/link", new { }, "t", CancellationToken.None));

        Assert.Equal("no_change", blocked.Error.Reason);
        Assert.Equal("paid", notPending.Error.Status);
    }

    [Fact]
    public async Task ANonJsonAnswer_IsAServerError()
    {
        var fake = new FakeApi().On("GET /ping", 502, "<html>Bad gateway</html>");
        using var api = Api.Client(fake);

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.SendAsync<Pong>(HttpMethod.Get, "/ping", null, null, CancellationToken.None));

        Assert.Equal(ApiError.ServerError, ex.Error.Code);
        Assert.Equal(502, ex.Error.HttpStatus);
    }

    [Fact]
    public async Task ABrokenSuccessBody_IsAServerError()
    {
        var fake = new FakeApi().On("GET /ping", 200, "{not json");
        using var api = Api.Client(fake);

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.SendAsync<Pong>(HttpMethod.Get, "/ping", null, null, CancellationToken.None));

        Assert.Equal(ApiError.ServerError, ex.Error.Code);
    }

    [Fact]
    public async Task NoNetwork_IsTheNetworkCode()
    {
        var fake = new FakeApi { ThrowOnSend = new HttpRequestException("down") };
        using var api = Api.Client(fake);

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.SendAsync<Pong>(HttpMethod.Get, "/ping", null, null, CancellationToken.None));

        Assert.Equal(ApiError.Network, ex.Error.Code);
        Assert.Equal(0, ex.Error.HttpStatus);
    }

    [Fact]
    public async Task ATimeout_IsTheNetworkCode_ButACancellationIsACancellation()
    {
        var fake = new FakeApi { ThrowOnSend = new TaskCanceledException("timeout") };
        using var api = Api.Client(fake);

        var timeout = await Assert.ThrowsAsync<ApiException>(() => api.SendAsync<Pong>(HttpMethod.Get, "/ping", null, null, CancellationToken.None));
        Assert.Equal(ApiError.Network, timeout.Error.Code);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        fake.ThrowOnSend = new TaskCanceledException("cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.SendAsync<Pong>(HttpMethod.Get, "/ping", null, null, cts.Token));
    }

    [Fact]
    public void OnlyHttps_IsAccepted()
        => Assert.Throws<ArgumentException>(() => new ApiClient(() => "ru", new FakeApi(), "http://api.test/api/v1"));

    [Fact]
    public void TheRealClient_TalksToWaterca7()
        => Assert.Equal("https://waterca7.ru/api/v1", ApiClient.LiveBaseUrl);

    [Fact]
    public void ADeviceLimit_CarriesTheComputersAndTheReleasePermit()
    {
        var error = ApiClient.ParseError(409, "{\"error\":\"DEVICE_LIMIT\",\"message\":\"m\",\"bind_limit\":2,\"unbind_token\":\"" + new string('a', 64) + "\"," +
            "\"devices\":[{\"id\":5,\"name\":\"Office\",\"platform\":\"windows\",\"bound_at\":\"2026-10-01T10:00:00Z\",\"last_seen_at\":null,\"current\":false," +
            "\"whose\":\"me@x.y\",\"member\":true}]}");

        var limit = error.AsDeviceLimit()!;

        Assert.Equal(2, limit.BindLimit);
        Assert.Equal(new string('a', 64), limit.UnbindToken);
        var device = Assert.Single(limit.Devices);
        Assert.Equal(5, device.Id);
        Assert.Equal("Office", device.Name);
        Assert.Null(device.LastSeenAt);
        Assert.Equal("me@x.y", device.Whose);
        Assert.Null(new ApiError(ApiError.AuthFailed, "", 401, null).AsDeviceLimit());
    }
}
