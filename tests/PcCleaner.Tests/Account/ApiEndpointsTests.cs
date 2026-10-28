using PcCleaner.Core.Account;
using Xunit;

namespace PcCleaner.Tests.Account;

public class ApiEndpointsTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static void HasDevice(System.Text.Json.JsonElement body)
    {
        Assert.Equal(TestLicense.Hw, body.GetProperty("hw_hash").GetString());
        Assert.Equal("windows", body.GetProperty("platform").GetString());
        Assert.Equal("PC", body.GetProperty("name").GetString());
        Assert.Equal("1.5.0", body.GetProperty("app_version").GetString());
    }

    [Fact]
    public async Task Login_SendsTheEmailPasswordAndDevice_AndReadsTheAnswer()
    {
        using var key = new TestLicense();
        var fake = new FakeApi().On("POST /auth/login", 200, key.LoginJson(TestLicense.Json()));
        using var api = Api.Client(fake);

        var result = await api.LoginAsync("user@example.com", "secret-pass", Api.Device, None);

        Assert.Equal("t0ken", result.Token);
        Assert.Equal(TestLicense.DeviceId, result.Device.Id);
        var body = Api.Body(fake.Requests[0]);
        Assert.Equal("user@example.com", body.GetProperty("email").GetString());
        Assert.Equal("secret-pass", body.GetProperty("password").GetString());
        HasDevice(body);
    }

    [Fact]
    public async Task Register_SendsConsentAsJsonTrue_AndTheLanguage()
    {
        var fake = new FakeApi().On("POST /auth/register", 200, "{\"pending\":true}");
        using var api = Api.Client(fake);

        await api.RegisterAsync("a@b.c", "password1", "en", Api.Device, None);

        var body = Api.Body(fake.Requests[0]);
        Assert.Equal(System.Text.Json.JsonValueKind.True, body.GetProperty("consent").ValueKind);
        Assert.Equal("en", body.GetProperty("lang").GetString());
        HasDevice(body);
    }

    [Fact]
    public async Task VerifyAndResetConfirm_SendTheCodeAndDevice()
    {
        using var key = new TestLicense();
        var fake = new FakeApi()
            .On("POST /auth/verify", 200, key.LoginJson(TestLicense.Json()))
            .On("POST /auth/reset/confirm", 200, key.LoginJson(TestLicense.Json()));
        using var api = Api.Client(fake);

        await api.VerifyAsync("a@b.c", "123456", Api.Device, None);
        await api.ResetConfirmAsync("a@b.c", "654321", "new-password", Api.Device, None);

        var verify = Api.Body(fake.To("POST /auth/verify").Single());
        Assert.Equal("123456", verify.GetProperty("code").GetString());
        HasDevice(verify);
        var confirm = Api.Body(fake.To("POST /auth/reset/confirm").Single());
        Assert.Equal("654321", confirm.GetProperty("code").GetString());
        Assert.Equal("new-password", confirm.GetProperty("password").GetString());
        HasDevice(confirm);
    }

    [Fact]
    public async Task ResendAndReset_AlwaysAnswerOk()
    {
        var fake = new FakeApi()
            .On("POST /auth/resend", 200, "{\"ok\":true,\"retry_after\":60}")
            .On("POST /auth/reset", 200, "{\"ok\":true}");
        using var api = Api.Client(fake);

        var resend = await api.ResendAsync("a@b.c", "verify", None);
        await api.ResetAsync("a@b.c", None);

        Assert.Equal(60, resend.RetryAfter);
        Assert.Equal("verify", Api.Body(fake.To("POST /auth/resend").Single()).GetProperty("purpose").GetString());
        Assert.Equal("a@b.c", Api.Body(fake.To("POST /auth/reset").Single()).GetProperty("email").GetString());
    }

    [Fact]
    public async Task TheBrowserCode_Starts_AndItsPollTellsWaitingFromDone()
    {
        using var key = new TestLicense();
        var fake = new FakeApi()
            .On("POST /auth/code", 200, "{\"code\":\"ABCD-1234\",\"url\":\"https://waterca7.ru/auth/device?code=ABCD-1234\",\"expires_in\":600}")
            .On("GET /auth/code/ABCD-1234", 202, "{\"status\":\"pending\"}")
            .On("GET /auth/code/ABCD-1234", 200, key.LoginJson(TestLicense.Json()));
        using var api = Api.Client(fake);

        var code = await api.StartBrowserCodeAsync(Api.Device, None);
        var first = await api.PollBrowserCodeAsync(code.Code, None);
        var second = await api.PollBrowserCodeAsync(code.Code, None);

        Assert.Equal(600, code.ExpiresIn);
        Assert.Null(first);
        Assert.Equal("t0ken", second!.Token);
        HasDevice(Api.Body(fake.To("POST /auth/code").Single()));
    }

    [Fact]
    public async Task AGoneCode_IsNotFound()
    {
        var fake = new FakeApi().OnError("GET /auth/code/X", 410, "NOT_FOUND");
        using var api = Api.Client(fake);

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.PollBrowserCodeAsync("X", None));

        Assert.Equal(ApiError.NotFound, ex.Error.Code);
        Assert.Equal(410, ex.Error.HttpStatus);
    }

    [Fact]
    public async Task TheTokenMethods_SendTheBearer_AndTheirBodies()
    {
        using var key = new TestLicense();
        var ticket = key.TicketJson(TestLicense.Json());
        var fake = new FakeApi()
            .On("POST /auth/logout", 200, "{\"ok\":true}")
            .On("GET /license/ticket", 200, ticket)
            .On("GET /devices", 200, "{\"devices\":[],\"bind_limit\":1}")
            .On("POST /device/unbind", 200, "{\"ok\":true}")
            .On("POST /promo/apply", 200, ticket);
        using var api = Api.Client(fake);

        await api.LogoutAsync("tok", None);
        var t = await api.TicketAsync("tok", None);
        var devices = await api.DevicesAsync("tok", None);
        await api.UnbindAsync("tok", 5, None);
        await api.UnbindAsync("tok", null, None);
        await api.PromoApplyAsync("tok", "FRIEND-1", None);

        Assert.All(fake.Requests, r => Assert.Equal("tok", r.Bearer));
        Assert.False(string.IsNullOrEmpty(t.Signature));
        Assert.Equal(1, devices.BindLimit);
        var unbinds = fake.To("POST /device/unbind").ToList();
        Assert.Equal(5, Api.Body(unbinds[0]).GetProperty("device_id").GetInt64());
        Assert.False(Api.Body(unbinds[1]).TryGetProperty("device_id", out _)); // without an id: this computer
        Assert.Equal("FRIEND-1", Api.Body(fake.To("POST /promo/apply").Single()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Release_HasNoBearer_ButThePermitAndTheComputer()
    {
        var fake = new FakeApi().On("POST /device/release", 200, "{\"ok\":true}");
        using var api = Api.Client(fake);

        await api.ReleaseAsync(new string('b', 64), 5, None);

        var r = fake.Requests.Single();
        Assert.Null(r.Bearer);
        Assert.Equal(new string('b', 64), Api.Body(r).GetProperty("unbind_token").GetString());
        Assert.Equal(5, Api.Body(r).GetProperty("device_id").GetInt64());
    }

    [Fact]
    public async Task Prices_QuoteStartLinkAndStatus()
    {
        using var key = new TestLicense();
        var t = key.Sign(TestLicense.Json());
        var fake = new FakeApi()
            .On("GET /prices", 200, "{\"currency\":\"RUB\",\"pro\":{\"month\":199,\"year\":1490,\"lifetime\":3990}," +
                                    "\"family\":{\"min\":2,\"max\":5,\"default\":3,\"table\":{\"2\":{\"month\":299,\"year\":2290,\"lifetime\":5990}}},\"payments_enabled\":true}")
            .On("POST /pay/quote", 200, "{\"list\":1490,\"discount\":149,\"credit\":0,\"amount\":1341,\"action\":\"new\",\"term\":\"year\",\"devices\":1,\"from_devices\":null,\"expires_at\":\"2027-11-01T00:00:00Z\"}")
            .On("POST /pay/start", 200, "{\"payment_id\":91,\"url\":\"https://waterca7.ru/pay/app/91?t=" + new string('c', 64) + "\"}")
            .On("POST /pay/link", 200, "{\"payment_id\":91,\"url\":\"https://waterca7.ru/pay/app/91?t=" + new string('d', 64) + "\"}")
            .On("GET /pay/91", 200, $"{{\"status\":\"paid\",\"license\":{{\"ticket\":\"{t.Ticket}\",\"signature\":\"{t.Signature}\"}}}}");
        using var api = Api.Client(fake);
        var request = new QuoteRequest("pro", "year", 1, "SALE10");

        var prices = await api.PricesAsync(null, None);
        var quote = await api.QuoteAsync("tok", request, None);
        var start = await api.PayStartAsync("tok", request, None);
        var link = await api.PayLinkAsync("tok", start.PaymentId, None);
        var status = await api.PayStatusAsync("tok", start.PaymentId, None);

        Assert.Equal(1490, prices.Pro.Year);
        Assert.Equal(299, prices.Family.Table["2"].Month);
        Assert.Null(fake.To("GET /prices").Single().Bearer);
        Assert.Equal(1341, quote.Amount);
        var quoteBody = Api.Body(fake.To("POST /pay/quote").Single());
        Assert.Equal("pro", quoteBody.GetProperty("plan").GetString());
        Assert.Equal("year", quoteBody.GetProperty("term").GetString());
        Assert.Equal(1, quoteBody.GetProperty("devices").GetInt32());
        Assert.Equal("SALE10", quoteBody.GetProperty("promo").GetString());
        Assert.False(quoteBody.TryGetProperty("change", out _)); // sent only when true
        Assert.Equal(91, start.PaymentId);
        Assert.Equal(91, Api.Body(fake.To("POST /pay/link").Single()).GetProperty("payment_id").GetInt64());
        Assert.NotEqual(start.Url, link.Url);
        Assert.Equal("paid", status.Status);
        Assert.NotNull(status.License);
    }

    [Fact]
    public async Task Support_SendsTheTopicTextVersionAndLog_WithAnOptionalBearer()
    {
        var fake = new FakeApi().On("POST /ticket", 200, "{\"id\":12}");
        using var api = Api.Client(fake);

        await api.SupportAsync(null, new SupportRequest("a@b.c", "bug", "Не открывается окно", "bG9n"), "1.5.0", None);

        var r = fake.Requests.Single();
        Assert.Null(r.Bearer);
        var body = Api.Body(r);
        Assert.Equal("a@b.c", body.GetProperty("email").GetString());
        Assert.Equal("bug", body.GetProperty("topic").GetString());
        Assert.Equal("1.5.0", body.GetProperty("app_version").GetString());
        Assert.Equal("bG9n", body.GetProperty("log").GetString());
    }
}
