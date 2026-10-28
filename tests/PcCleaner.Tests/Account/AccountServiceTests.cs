using PcCleaner.Core.Account;
using Xunit;

namespace PcCleaner.Tests.Account;

public class AccountServiceTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TestLicense _key = new();
    private readonly FakeApi _fake = new();
    private readonly MemoryAccountStore _store = new();
    private DateTimeOffset _now = TestLicense.Now;
    private int _changes;

    public AccountServiceTests() => TestEnvironment.Init();

    public void Dispose() => _key.Dispose();

    private AccountService Service()
    {
        var service = new AccountService(Api.Client(_fake), _store, new FixedHwHash(), "windows", "PC", "1.5.0", _key.Keys, () => _now,
            (_, _) => Task.CompletedTask);
        service.Changed += () => _changes++;
        return service;
    }

    private AccountService SignedIn(string plan = "pro")
    {
        var t = _key.Sign(TestLicense.Json(plan: plan));
        _store.Data = new AccountData("tok", "user@example.com", TestLicense.DeviceId, TestLicense.Sub, t.Ticket, t.Signature, "inst-1");
        return Service();
    }

    [Fact]
    public void Nothing_Saved_IsAGuest()
    {
        var service = Service();

        Assert.False(service.IsSignedIn);
        Assert.Equal(Tier.Guest, service.Tier);
        Assert.False(service.IsAvailable(Features.HomeFull));
        Assert.True(service.IsAvailable(Features.HomeQuick));
    }

    [Fact]
    public void ASavedProSignIn_IsPro_WithoutTheNetwork()
    {
        var service = SignedIn();

        Assert.True(service.IsSignedIn);
        Assert.Equal("user@example.com", service.Email);
        Assert.Equal(Tier.Pro, service.Tier);
        Assert.Empty(_fake.Requests);
    }

    [Fact]
    public void ASavedTicketWithAForeignSignature_IsFree()
    {
        using var other = new TestLicense();
        var t = other.Sign(TestLicense.Json());
        _store.Data = new AccountData("tok", "u@x.y", TestLicense.DeviceId, TestLicense.Sub, t.Ticket, t.Signature);

        Assert.Equal(Tier.Free, Service().Tier);
    }

    [Fact]
    public async Task Login_SavesTheTokenDeviceAccountAndTicket_AndTellsTheScreens()
    {
        _fake.On("POST /auth/login", 200, _key.LoginJson(TestLicense.Json()));
        _store.Data = new AccountData(InstallId: "inst-1");
        var service = Service();

        await service.LoginAsync("user@example.com", "password1", None);

        Assert.Equal(Tier.Pro, service.Tier);
        Assert.Equal("t0ken", _store.Data.Token);
        Assert.Equal(TestLicense.DeviceId, _store.Data.DeviceId);
        Assert.Equal(TestLicense.Sub, _store.Data.AccountId);
        Assert.Equal("user@example.com", _store.Data.Email);
        Assert.Equal("inst-1", _store.Data.InstallId);
        Assert.Equal(1, _changes);
        Assert.Equal(TestLicense.Hw, Api.Body(_fake.Requests[0]).GetProperty("hw_hash").GetString());
    }

    [Fact]
    public async Task AFreeAccount_IsSignedIn_AndFree()
    {
        _fake.On("POST /auth/login", 200, _key.LoginJson(TestLicense.Json(plan: "free", kind: "none", noExpiry: true, devicesMax: 0)));
        var service = Service();

        await service.LoginAsync("user@example.com", "password1", None);

        Assert.True(service.IsSignedIn);
        Assert.Equal(Tier.Free, service.Tier);
    }

    [Theory]
    [InlineData("AUTH_FAILED", 401)]
    [InlineData("EMAIL_NOT_VERIFIED", 403)]
    [InlineData("RATE_LIMITED", 429)]
    public async Task AFailedLogin_SavesNothing_AndThrowsTheCode(string code, int status)
    {
        _fake.OnError("POST /auth/login", status, code);
        var service = Service();

        var ex = await Assert.ThrowsAsync<ApiException>(() => service.LoginAsync("u@x.y", "password1", None));

        Assert.Equal(code, ex.Error.Code);
        Assert.False(service.IsSignedIn);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task ADeviceLimit_GivesTheComputers_ThenReleaseAndLoginAgain()
    {
        var limit = $",\"devices\":[{{\"id\":5,\"name\":\"Old\",\"platform\":\"windows\",\"bound_at\":\"2026-01-01T00:00:00Z\",\"last_seen_at\":null,\"current\":false}}]," +
                    $"\"bind_limit\":1,\"unbind_token\":\"{new string('a', 64)}\"";
        _fake.OnError("POST /auth/login", 409, "DEVICE_LIMIT", limit)
            .On("POST /auth/login", 200, _key.LoginJson(TestLicense.Json()))
            .On("POST /device/release", 200, "{\"ok\":true}");
        var service = Service();

        var ex = await Assert.ThrowsAsync<ApiException>(() => service.LoginAsync("u@x.y", "password1", None));
        var info = ex.Error.AsDeviceLimit()!;
        await service.ReleaseAsync(info.UnbindToken, info.Devices[0].Id, None);
        await service.LoginAsync("u@x.y", "password1", None);

        Assert.Equal(Tier.Pro, service.Tier);
        Assert.Equal(5, Api.Body(_fake.To("POST /device/release").Single()).GetProperty("device_id").GetInt64());
    }

    [Fact]
    public async Task RegisterThenVerify_SignsIn()
    {
        _fake.On("POST /auth/register", 200, "{\"pending\":true}")
            .On("POST /auth/verify", 200, _key.LoginJson(TestLicense.Json(plan: "free", noExpiry: true)));
        var service = Service();

        await service.RegisterAsync("new@x.y", "password1", "ru", None);
        Assert.False(service.IsSignedIn);
        await service.VerifyAsync("new@x.y", "123456", None);

        Assert.True(service.IsSignedIn);
        Assert.Equal("ru", Api.Body(_fake.To("POST /auth/register").Single()).GetProperty("lang").GetString());
    }

    [Fact]
    public async Task Resend_GivesTheSecondsToWait()
    {
        _fake.On("POST /auth/resend", 200, "{\"ok\":true,\"retry_after\":60}");

        Assert.Equal(60, await Service().ResendAsync("u@x.y", "reset", None));
    }

    [Fact]
    public async Task ResetThenConfirm_SignsInWithTheNewPassword()
    {
        _fake.On("POST /auth/reset", 200, "{\"ok\":true}")
            .On("POST /auth/reset/confirm", 200, _key.LoginJson(TestLicense.Json()));
        var service = Service();

        await service.ResetAsync("u@x.y", None);
        await service.ResetConfirmAsync("u@x.y", "111111", "new-password", None);

        Assert.Equal(Tier.Pro, service.Tier);
    }

    [Fact]
    public async Task Logout_RevokesTheToken_AndForgetsEverythingButTheInstallNumber()
    {
        _fake.On("POST /auth/logout", 200, "{\"ok\":true}");
        var service = SignedIn();

        await service.LogoutAsync(None);

        Assert.Equal("tok", _fake.Requests.Single().Bearer);
        Assert.Equal(new AccountData(InstallId: "inst-1"), _store.Data);
        Assert.Equal(Tier.Guest, service.Tier);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task Logout_WithoutTheNetwork_StillSignsOutHere()
    {
        _fake.ThrowOnSend = new System.Net.Http.HttpRequestException("down");
        var service = SignedIn();

        await service.LogoutAsync(None);

        Assert.False(service.IsSignedIn);
    }

    [Fact]
    public async Task TokenRevoked_SignsOut_RemembersIt_AndThrows()
    {
        _fake.OnError("GET /devices", 401, "TOKEN_REVOKED");
        var service = SignedIn();

        var ex = await Assert.ThrowsAsync<ApiException>(() => service.DevicesAsync(None));

        Assert.Equal(ApiError.TokenRevoked, ex.Error.Code);
        Assert.False(service.IsSignedIn);
        Assert.True(service.SessionEnded);
        Assert.Equal("inst-1", _store.Data.InstallId);
        Assert.Null(_store.Data.Token);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task UnbindingAnotherComputer_KeepsThisOne_UnbindingThisOne_SignsOut()
    {
        _fake.On("POST /device/unbind", 200, "{\"ok\":true}");
        var service = SignedIn();

        await service.UnbindAsync(5, None);
        Assert.True(service.IsSignedIn);

        await service.UnbindAsync(TestLicense.DeviceId, None);
        Assert.False(service.IsSignedIn);
        Assert.False(service.SessionEnded); // the user's own choice, not a revoked token
    }

    [Fact]
    public async Task APromoCode_GivesANewTicket_AtOnce()
    {
        _fake.On("POST /promo/apply", 200, _key.TicketJson(TestLicense.Json(plan: "pro", kind: "promo")));
        var service = SignedIn(plan: "free");
        Assert.Equal(Tier.Free, service.Tier);

        await service.ApplyPromoAsync("FRIEND-1", None);

        Assert.Equal(Tier.Pro, service.Tier);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void AcceptTicket_TakesOnlyAGoodTicketOfThisComputer()
    {
        var service = SignedIn(plan: "free");
        using var other = new TestLicense();

        Assert.False(service.AcceptTicket(other.Sign(TestLicense.Json())));
        Assert.False(service.AcceptTicket(_key.Sign(TestLicense.Json(deviceId: 99))));
        Assert.Equal(Tier.Free, service.Tier);
        Assert.True(service.AcceptTicket(_key.Sign(TestLicense.Json())));
        Assert.Equal(Tier.Pro, service.Tier);
    }

    [Fact]
    public async Task Support_UsesTheAccountsEmail_AndToken_WhenSignedIn()
    {
        _fake.On("POST /ticket", 200, "{\"id\":1}");
        var service = SignedIn();

        await service.SendSupportAsync(null, "question", "Как включить Pro на втором ПК?", null, None);

        var r = _fake.Requests.Single();
        Assert.Equal("tok", r.Bearer);
        Assert.Equal("user@example.com", Api.Body(r).GetProperty("email").GetString());
    }

    [Fact]
    public async Task Support_AsAGuest_NeedsTheEmailTyped()
    {
        _fake.On("POST /ticket", 200, "{\"id\":1}");
        var service = Service();

        await Assert.ThrowsAsync<ArgumentException>(() => service.SendSupportAsync(null, "bug", "Окно не открывается", null, None));
        await service.SendSupportAsync("guest@x.y", "bug", "Окно не открывается", null, None);

        Assert.Null(_fake.Requests.Single().Bearer);
    }

    [Fact]
    public void TheDeviceName_IsCleanedAndCut()
    {
        var service = new AccountService(Api.Client(_fake), _store, new FixedHwHash(), "windows", "  My\tPC " + new string('x', 80), "1.5.0", _key.Keys);

        Assert.Equal(64, service.Device.Name.Length);
        Assert.StartsWith("MyPC", service.Device.Name);
    }
}
