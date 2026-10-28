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

        Assert.True(await service.ApplyPromoAsync("FRIEND-1", None));

        Assert.Equal(Tier.Pro, service.Tier);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task APromoTicketThatDoesNotFit_IsReportedAsNotTaken()
    {
        using var other = new TestLicense();
        _fake.On("POST /promo/apply", 200, other.TicketJson(TestLicense.Json(plan: "pro", kind: "promo")));
        var service = SignedIn(plan: "free");

        Assert.False(await service.ApplyPromoAsync("FRIEND-1", None));

        Assert.Equal(Tier.Free, service.Tier);
        Assert.Equal(0, _changes);
    }

    [Fact]
    public async Task AComputerIdThatCannotBeRead_GivesFree_AndNeverThrows()
    {
        var t = _key.Sign(TestLicense.Json(plan: "pro"));
        _store.Data = new AccountData("tok", "user@example.com", TestLicense.DeviceId, TestLicense.Sub, t.Ticket, t.Signature);
        var service = new AccountService(Api.Client(_fake), _store, new ThrowingHwHash(), "windows", "PC", "1.5.0", _key.Keys, () => _now,
            (_, _) => Task.CompletedTask);

        await service.WarmUpAsync();

        Assert.Equal(Tier.Free, service.Tier);
        Assert.Equal(LicenseReason.WrongDevice, service.Status.Reason);
        Assert.False(service.IsAvailable(Features.HomeFull));
        Assert.False(service.AcceptTicket(_key.Sign(TestLicense.Json(plan: "pro"))));
        Assert.Equal(Tier.Guest, new AccountService(Api.Client(_fake), new MemoryAccountStore(), new ThrowingHwHash(), "windows", "PC", "1.5.0",
            _key.Keys).Tier);
    }

    [Fact]
    public async Task AComputerIdThatCannotBeRead_NeverMakesTheSignInCallsThrowBeforeTheTask()
    {
        var service = new AccountService(Api.Client(_fake), _store, new ThrowingHwHash(), "windows", "PC", "1.5.0", _key.Keys, () => _now,
            (_, _) => Task.CompletedTask);
        _fake.On("POST /auth/register", 200, "{\"pending\":true}")
            .On("POST /auth/code", 200, "{\"code\":\"ABCD-1234\",\"url\":\"https://waterca7.ru/auth/device?code=ABCD-1234\",\"expires_in\":600}");

        var register = service.RegisterAsync("user@example.com", "password1", "ru", None); // not awaited: must not throw here
        var browser = service.StartBrowserLoginAsync(None);

        Assert.Equal("", service.Device.HwHash); // the unreadable id is sent empty; the server decides
        await register;
        Assert.Equal("ABCD-1234", (await browser).Code);
    }

    [Fact]
    public async Task TheNotSignedInError_HasAText()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Service().DevicesAsync(None));

        Assert.Equal(PcCleaner.Core.Localization.T._("Нужно войти снова."), ex.Error.UserText);
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

    // A call that waits on a held answer is started with Task.Run: started on the test's thread, every step after the answer would be
    // queued on xUnit's synchronization context (one thread per core, shared by all test classes) and wait for other classes' tests.
    private static async Task WaitForRequests(FakeApi fake, int count)
        => Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => fake.Requests.Count >= count, TimeSpan.FromSeconds(10))));

    [Fact]
    public async Task ARevokedAnswer_ForAnOldToken_DoesNotWipeTheNewSignIn()
    {
        var gate = new TaskCompletionSource();
        _fake.OnError("GET /devices", 401, "TOKEN_REVOKED").On("POST /auth/login", 200, _key.LoginJson(TestLicense.Json(), token: "t2"));
        _fake.Hold("GET /devices", gate.Task);
        var service = SignedIn();

        var pending = Task.Run(() => service.DevicesAsync(None));
        await WaitForRequests(_fake, 1);
        await service.LoginAsync("user@example.com", "password1", None); // the user signs in again meanwhile (token t2)
        gate.SetResult();
        var ex = await Assert.ThrowsAsync<ApiException>(() => pending);

        Assert.Equal(ApiError.TokenRevoked, ex.Error.Code);
        Assert.True(service.IsSignedIn);
        Assert.Equal("t2", _store.Data.Token);
        Assert.False(service.SessionEnded);
    }

    [Fact]
    public async Task ARevokedAnswer_AfterTheUserSignedOut_DoesNotShowSessionEnded()
    {
        var gate = new TaskCompletionSource();
        _fake.OnError("GET /devices", 401, "TOKEN_REVOKED").On("POST /auth/logout", 200, "{\"ok\":true}");
        _fake.Hold("GET /devices", gate.Task);
        var service = SignedIn();

        var pending = Task.Run(() => service.DevicesAsync(None));
        await WaitForRequests(_fake, 1);
        await service.LogoutAsync(None);
        gate.SetResult();
        await Assert.ThrowsAsync<ApiException>(() => pending);

        Assert.False(service.IsSignedIn);
        Assert.False(service.SessionEnded);
    }

    [Fact]
    public async Task AnUnbindOfThisComputer_FinishingAfterANewSignIn_KeepsTheNewSignIn()
    {
        var gate = new TaskCompletionSource();
        _fake.On("POST /device/unbind", 200, "{\"ok\":true}").On("POST /auth/login", 200, _key.LoginJson(TestLicense.Json(), token: "t2"));
        _fake.Hold("POST /device/unbind", gate.Task);
        var service = SignedIn();

        var pending = Task.Run(() => service.UnbindAsync(null, None));
        await WaitForRequests(_fake, 1);
        await service.LoginAsync("user@example.com", "password1", None);
        gate.SetResult();
        await pending;

        Assert.True(service.IsSignedIn);
        Assert.Equal("t2", _store.Data.Token);
    }

    [Fact]
    public async Task ASignInWithAnUnverifiedTicket_AdoptsTheAccount_FromTheNextGoodTicket()
    {
        using var other = new TestLicense();
        _fake.On("POST /auth/login", 200, other.LoginJson(TestLicense.Json())); // signed by a key we do not know
        var service = Service();
        await service.LoginAsync("user@example.com", "password1", None);
        Assert.Equal(Tier.Free, service.Tier);
        Assert.Null(_store.Data.AccountId);

        Assert.False(service.AcceptTicket(_key.Sign(TestLicense.Json(deviceId: 99))));
        Assert.True(service.AcceptTicket(_key.Sign(TestLicense.Json())));

        Assert.Equal(Tier.Pro, service.Tier);
        Assert.Equal(TestLicense.Sub, _store.Data.AccountId);
    }

    [Fact]
    public async Task Support_WithARevokedToken_SignsOut_AndThrows()
    {
        _fake.OnError("POST /ticket", 401, "TOKEN_REVOKED");
        var service = SignedIn();

        var ex = await Assert.ThrowsAsync<ApiException>(() => service.SendSupportAsync(null, "bug", "Окно не открывается", null, None));

        Assert.Equal(ApiError.TokenRevoked, ex.Error.Code);
        Assert.False(service.IsSignedIn);
        Assert.True(service.SessionEnded);
    }

    [Fact]
    public async Task Logout_Cancelled_StillSignsOutHere_AndRethrows()
    {
        _fake.On("POST /auth/logout", 200, "{\"ok\":true}");
        var service = SignedIn();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LogoutAsync(cts.Token));

        Assert.False(service.IsSignedIn);
        Assert.Null(_store.Data.Token);
        Assert.False(service.SessionEnded);
    }

    // ----- the final review -----

    public static TheoryData<string> StoreFailures => new() { "io", "crypto", "access" };

    private static Exception StoreFailure(string kind) => kind switch
    {
        "io" => new System.IO.IOException("disk full"),
        "crypto" => new System.Security.Cryptography.CryptographicException("profile broken"),
        _ => new UnauthorizedAccessException("locked"),
    };

    [Theory]
    [MemberData(nameof(StoreFailures))]
    public async Task AStoreThatCannotSave_KeepsTheStateInMemory_AndStillTellsTheScreens(string kind)
    {
        var store = new ThrowingAccountStore(() => StoreFailure(kind));
        _fake.On("POST /auth/login", 200, _key.LoginJson(TestLicense.Json(plan: "free", noExpiry: true)))
            .On("POST /promo/apply", 200, _key.TicketJson(TestLicense.Json(plan: "pro", kind: "promo")))
            .On("POST /auth/logout", 200, "{\"ok\":true}");
        var service = new AccountService(Api.Client(_fake), store, new FixedHwHash(), "windows", "PC", "1.5.0", _key.Keys, () => _now,
            (_, _) => Task.CompletedTask);
        service.Changed += () => _changes++;

        await service.LoginAsync("user@example.com", "password1", None);
        Assert.True(service.IsSignedIn);
        Assert.Equal(1, _changes);

        await service.ApplyPromoAsync("FRIEND-1", None);
        Assert.Equal(Tier.Pro, service.Tier);
        Assert.Equal(2, _changes);

        await service.LogoutAsync(None);
        Assert.False(service.IsSignedIn);
        Assert.Equal(3, _changes);
        Assert.Equal(3, store.Attempts);
        Assert.Null(store.Data.Token); // nothing reached the disk
    }

    [Fact]
    public void AnOlderTicket_IsRefused_ButALaterDowngradeIsTaken()
    {
        var service = SignedIn(); // issued a day ago
        var older = _key.Sign(TestLicense.Json(plan: "pro", issued: TestLicense.Now.AddDays(-2)));
        var laterFree = _key.Sign(TestLicense.Json(plan: "free", noExpiry: true, issued: TestLicense.Now.AddHours(-1)));

        Assert.False(service.AcceptTicket(older));
        Assert.Equal(Tier.Pro, service.Tier);
        Assert.True(service.AcceptTicket(laterFree));
        Assert.Equal(Tier.Free, service.Tier);
        Assert.False(service.AcceptTicket(_key.Sign(TestLicense.Json(plan: "pro")))); // the day-old one is older now
        Assert.Equal(Tier.Free, service.Tier);
    }

    [Fact]
    public async Task NotSignedIn_FailsTheTask_NotTheCall()
    {
        var service = Service();
        var request = new QuoteRequest("pro", "year", 1);

        var calls = new List<Func<Task>>
        {
            () => service.DevicesAsync(None),
            () => service.QuoteAsync(request, None),
            () => service.StartPaymentAsync(request, None),
            () => service.PaymentLinkAsync(91, None),
            () => service.PaymentStatusAsync(91, None),
            () => service.WaitPaymentAsync(91, None),
            () => service.ApplyPromoAsync("FRIEND-1", None),
            () => service.UnbindAsync(5, None),
        };
        foreach (var call in calls)
        {
            var task = call(); // must not throw here
            var ex = await Assert.ThrowsAsync<ApiException>(() => task);
            Assert.Equal(ApiError.TokenRevoked, ex.Error.Code);
        }
        Assert.Empty(_fake.Requests);
    }

    [Fact]
    public async Task ALogoutFinishingAfterANewSignIn_KeepsTheNewSignIn()
    {
        var gate = new TaskCompletionSource();
        _fake.On("POST /auth/logout", 200, "{\"ok\":true}").On("POST /auth/login", 200, _key.LoginJson(TestLicense.Json(), token: "t2"));
        _fake.Hold("POST /auth/logout", gate.Task);
        var service = SignedIn();

        var pending = Task.Run(() => service.LogoutAsync(None));
        await WaitForRequests(_fake, 1);
        await service.LoginAsync("user@example.com", "password1", None); // the user signs in again meanwhile (token t2)
        gate.SetResult();
        await pending;

        Assert.Equal("tok", _fake.To("POST /auth/logout").Single().Bearer);
        Assert.True(service.IsSignedIn);
        Assert.Equal("t2", _store.Data.Token);
    }

    [Fact]
    public async Task ALogoutAfterAFailedServerAnswer_StillSignsOutHere()
    {
        _fake.OnError("POST /auth/logout", 500, "SERVER_ERROR");
        var service = SignedIn();

        await service.LogoutAsync(None);

        Assert.False(service.IsSignedIn);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task Logout_AsAGuest_TouchesNothing()
    {
        var service = Service();

        await service.LogoutAsync(None);

        Assert.Empty(_fake.Requests);
        Assert.Equal(0, _store.Saves);
        Assert.Equal(0, _changes);
    }

    [Fact]
    public void TheDeviceName_IsCleanedAndCut()
    {
        var service = new AccountService(Api.Client(_fake), _store, new FixedHwHash(), "windows", "  My\tPC " + new string('x', 80), "1.5.0", _key.Keys);

        Assert.Equal(64, service.Device.Name.Length);
        Assert.StartsWith("MyPC", service.Device.Name);
    }
}
