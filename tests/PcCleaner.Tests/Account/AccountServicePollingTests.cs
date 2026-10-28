using PcCleaner.Core.Account;
using Xunit;

namespace PcCleaner.Tests.Account;

public class AccountServicePollingTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TestLicense _key = new();
    private readonly FakeApi _fake = new();
    private readonly MemoryAccountStore _store = new();
    private readonly List<TimeSpan> _waits = new();
    private DateTimeOffset _now = TestLicense.Now;

    public AccountServicePollingTests() => TestEnvironment.Init();

    public void Dispose() => _key.Dispose();

    /// <summary>The delay of the service moves the clock instead of waiting.</summary>
    private AccountService Service() => new(Api.Client(_fake), _store, new FixedHwHash(), "windows", "PC", "1.5.0", _key.Keys, () => _now,
        (d, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            _waits.Add(d);
            _now += d;
            return Task.CompletedTask;
        });

    private AccountService SignedIn(string json)
    {
        var t = _key.Sign(json);
        _store.Data = new AccountData("tok", "user@example.com", TestLicense.DeviceId, TestLicense.Sub, t.Ticket, t.Signature);
        return Service();
    }

    private static readonly BrowserCode Code = new("ABCD", "https://waterca7.ru/auth/device?code=ABCD", 600);

    [Fact]
    public async Task TheBrowserCode_IsPolledEveryFiveSeconds_UntilTheSiteConfirms()
    {
        _fake.On("GET /auth/code/ABCD", 202, "{\"status\":\"pending\"}")
            .On("GET /auth/code/ABCD", 202, "{\"status\":\"pending\"}")
            .On("GET /auth/code/ABCD", 200, _key.LoginJson(TestLicense.Json()));
        var service = Service();

        var ok = await service.WaitBrowserLoginAsync(Code, _ => Task.CompletedTask, None);

        Assert.True(ok);
        Assert.Equal(Tier.Pro, service.Tier);
        Assert.All(_waits, w => Assert.Equal(TimeSpan.FromSeconds(5), w));
        Assert.Equal(3, _fake.Requests.Count);
    }

    [Fact]
    public async Task ThePoll_StopsAtExpiresIn()
    {
        _fake.On("GET /auth/code/ABCD", 202, "{\"status\":\"pending\"}");

        var ok = await Service().WaitBrowserLoginAsync(Code with { ExpiresIn = 20 }, _ => Task.CompletedTask, None);

        Assert.False(ok);
        Assert.InRange(_fake.Requests.Count, 3, 4);
    }

    [Fact]
    public async Task AGoneCode_EndsThePoll()
    {
        _fake.OnError("GET /auth/code/ABCD", 410, "NOT_FOUND");

        Assert.False(await Service().WaitBrowserLoginAsync(Code, _ => Task.CompletedTask, None));
        Assert.Single(_fake.Requests);
    }

    [Fact]
    public async Task RateLimited_DoublesThePause_UpToAMinute()
    {
        for (var i = 0; i < 6; i++)
            _fake.OnError("GET /auth/code/ABCD", 429, "RATE_LIMITED");
        _fake.On("GET /auth/code/ABCD", 200, _key.LoginJson(TestLicense.Json()));

        await Service().WaitBrowserLoginAsync(Code, _ => Task.CompletedTask, None);

        Assert.Equal(new[] { 5, 10, 20, 40, 60, 60, 60 }, _waits.Select(w => (int)w.TotalSeconds));
    }

    [Fact]
    public async Task ADeviceLimit_PausesThePoll_UntilTheDialogIsDone()
    {
        var limit = $",\"devices\":[],\"bind_limit\":1,\"unbind_token\":\"{new string('a', 64)}\"";
        _fake.OnError("GET /auth/code/ABCD", 409, "DEVICE_LIMIT", limit)
            .On("GET /auth/code/ABCD", 200, _key.LoginJson(TestLicense.Json()));
        var service = Service();
        var requestsWhileOpen = -1;

        var ok = await service.WaitBrowserLoginAsync(Code, async info =>
        {
            Assert.Equal(new string('a', 64), info.UnbindToken);
            await Task.Yield();
            requestsWhileOpen = _fake.Requests.Count;
        }, None);

        Assert.True(ok);
        Assert.Equal(1, requestsWhileOpen); // nothing was polled while the dialog was open
    }

    [Fact]
    public async Task NoNetwork_KeepsPolling()
    {
        var service = Service();
        _fake.ThrowOnSend = new System.Net.Http.HttpRequestException("down");
        var task = service.WaitBrowserLoginAsync(Code with { ExpiresIn = 15 }, _ => Task.CompletedTask, None);

        Assert.False(await task);
        Assert.True(_waits.Count >= 3);
    }

    [Fact]
    public async Task Cancelling_StopsThePoll()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().WaitBrowserLoginAsync(Code, _ => Task.CompletedTask, cts.Token));
    }

    [Fact]
    public async Task Refresh_TakesTheNewTicket()
    {
        var service = SignedIn(TestLicense.Json(plan: "free", noExpiry: true));
        _fake.On("GET /license/ticket", 200, _key.TicketJson(TestLicense.Json(plan: "pro")));

        Assert.True(await service.RefreshTicketAsync(None));
        Assert.Equal(Tier.Pro, service.Tier);
    }

    [Theory]
    [InlineData(502, "<html>bad gateway</html>")]
    [InlineData(500, "{\"error\":\"SERVER_ERROR\",\"message\":\"x\"}")]
    public async Task AFailedRefresh_KeepsTheTicket(int status, string body)
    {
        var service = SignedIn(TestLicense.Json(plan: "pro"));
        _fake.On("GET /license/ticket", status, body);

        Assert.False(await service.RefreshTicketAsync(None));
        Assert.Equal(Tier.Pro, service.Tier);
    }

    [Fact]
    public async Task ABadlySignedRefresh_KeepsTheTicket()
    {
        using var other = new TestLicense();
        var service = SignedIn(TestLicense.Json(plan: "pro"));
        _fake.On("GET /license/ticket", 200, other.TicketJson(TestLicense.Json(plan: "free")));

        Assert.False(await service.RefreshTicketAsync(None));
        Assert.Equal(Tier.Pro, service.Tier);
    }

    [Fact]
    public async Task ARevokedTokenOnRefresh_SignsOut()
    {
        var service = SignedIn(TestLicense.Json());
        _fake.OnError("GET /license/ticket", 401, "TOKEN_REVOKED");

        Assert.False(await service.RefreshTicketAsync(None));
        Assert.False(service.IsSignedIn);
        Assert.True(service.SessionEnded);
    }

    [Fact]
    public void TheNextRefresh_IsADayLater_OrJustAfterTheSubscriptionEnds_OrSoonAfterAFailure()
    {
        Assert.Equal(TimeSpan.FromHours(24), SignedIn(TestLicense.Json(expires: TestLicense.Now.AddDays(30))).NextRefreshDelay(true));
        Assert.Equal(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1),
            SignedIn(TestLicense.Json(expires: TestLicense.Now.AddHours(3))).NextRefreshDelay(true));
        Assert.Equal(TimeSpan.FromMinutes(15), SignedIn(TestLicense.Json()).NextRefreshDelay(false));
        Assert.Equal(TimeSpan.FromHours(24), Service().NextRefreshDelay(true)); // not signed in: nothing to refresh soon
    }

    [Fact]
    public async Task TheRefreshLoop_RefreshesAtOnce_ThenWaits()
    {
        SignedIn(TestLicense.Json(plan: "free", noExpiry: true)); // fills the store
        _fake.On("GET /license/ticket", 200, _key.TicketJson(TestLicense.Json(plan: "pro", expires: TestLicense.Now.AddDays(300))));
        using var cts = new CancellationTokenSource();
        var service2 = new AccountService(Api.Client(_fake), _store, new FixedHwHash(), "windows", "PC", "1.5.0", _key.Keys, () => _now,
            (d, ct) =>
            {
                _waits.Add(d);
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service2.RunRefreshLoopAsync(cts.Token));

        Assert.Single(_fake.To("GET /license/ticket"));
        Assert.Equal(TimeSpan.FromHours(24), Assert.Single(_waits));
        Assert.Equal(Tier.Pro, service2.Tier);
    }

    [Fact]
    public async Task APaidPayment_GivesTheTicket_AndPro()
    {
        var service = SignedIn(TestLicense.Json(plan: "free", noExpiry: true));
        var t = _key.Sign(TestLicense.Json(plan: "pro"));
        _fake.On("GET /pay/91", 200, "{\"status\":\"pending\"}")
            .On("GET /pay/91", 200, $"{{\"status\":\"paid\",\"license\":{{\"ticket\":\"{t.Ticket}\",\"signature\":\"{t.Signature}\"}}}}");

        var status = await service.WaitPaymentAsync(91, None);

        Assert.Equal("paid", status.Status);
        Assert.Equal(Tier.Pro, service.Tier);
        Assert.All(_waits, w => Assert.Equal(TimeSpan.FromSeconds(5), w));
    }

    [Fact]
    public async Task AFailedPayment_IsWatchedUntilTheEnd_BecauseItMayStillBePaid()
    {
        var service = SignedIn(TestLicense.Json(plan: "free", noExpiry: true));
        var t = _key.Sign(TestLicense.Json(plan: "pro"));
        _fake.On("GET /pay/91", 200, "{\"status\":\"failed\"}")
            .On("GET /pay/91", 200, "{\"status\":\"failed\"}")
            .On("GET /pay/91", 200, $"{{\"status\":\"paid\",\"license\":{{\"ticket\":\"{t.Ticket}\",\"signature\":\"{t.Signature}\"}}}}");
        var seen = new List<string>();

        var status = await service.WaitPaymentAsync(91, new SyncProgress<PaymentStatus>(s => seen.Add(s.Status)), None);

        Assert.Equal("paid", status.Status);
        Assert.Equal(new[] { "failed", "failed" }, seen); // the screen may say «Оплата не прошла» meanwhile
        Assert.Equal(Tier.Pro, service.Tier);
    }

    [Fact]
    public async Task AStillPendingPayment_StopsAfterTheWait()
    {
        var service = SignedIn(TestLicense.Json(plan: "free", noExpiry: true));
        _fake.On("GET /pay/91", 200, "{\"status\":\"pending\"}");

        var status = await service.WaitPaymentAsync(91, None);

        Assert.Equal("pending", status.Status);
        // 3 minutes every 5 seconds; the refresh RefreshSoon starts afterwards runs in the background and is not counted here.
        Assert.Equal(36, _fake.To("GET /pay/91").Count());
    }

    [Fact]
    public async Task ThePaymentMethods_SendTheToken()
    {
        var service = SignedIn(TestLicense.Json());
        _fake.On("GET /prices", 200, "{\"currency\":\"RUB\",\"pro\":{\"month\":1,\"year\":2,\"lifetime\":3},\"family\":{\"min\":2,\"max\":5,\"default\":3,\"table\":{}},\"payments_enabled\":true}")
            .On("POST /pay/quote", 200, "{\"list\":2,\"discount\":0,\"credit\":0,\"amount\":2,\"action\":\"new\",\"term\":\"year\",\"devices\":1,\"from_devices\":null,\"expires_at\":null}")
            .On("POST /pay/start", 200, "{\"payment_id\":91,\"url\":\"https://waterca7.ru/pay/app/91?t=x\"}")
            .On("POST /pay/link", 200, "{\"payment_id\":91,\"url\":\"https://waterca7.ru/pay/app/91?t=y\"}");
        var request = new QuoteRequest("pro", "year", 1);

        await service.PricesAsync(None);
        await service.QuoteAsync(request, None);
        await service.StartPaymentAsync(request, None);
        await service.PaymentLinkAsync(91, None);

        Assert.All(_fake.Requests, r => Assert.Equal("tok", r.Bearer));
    }

    [Fact]
    public async Task PricesForAGuest_GoWithoutAToken()
    {
        _fake.On("GET /prices", 200, "{\"currency\":\"RUB\",\"pro\":{\"month\":1,\"year\":2,\"lifetime\":3},\"family\":{\"min\":2,\"max\":5,\"default\":3,\"table\":{}},\"payments_enabled\":false}");

        await Service().PricesAsync(None);

        Assert.Null(_fake.Requests.Single().Bearer);
    }

    [Fact]
    public void TheInstallNumber_IsMadeOnce_AndCanBeReset()
    {
        var service = Service();

        var first = service.EnsureInstallId();
        Assert.True(Guid.TryParse(first, out _));
        Assert.Equal(first, service.EnsureInstallId());
        service.ResetInstallId();
        Assert.Null(service.InstallId);
        Assert.NotEqual(first, service.EnsureInstallId());
    }

    // ----- beyond the brief: races and the contract's finer points -----

    [Fact]
    public async Task EveryNewDeviceLimit_HandsTheLatestPermitToTheDialog()
    {
        string Limit(char c) => $",\"devices\":[],\"bind_limit\":1,\"unbind_token\":\"{new string(c, 64)}\"";
        _fake.OnError("GET /auth/code/ABCD", 409, "DEVICE_LIMIT", Limit('a'))
            .OnError("GET /auth/code/ABCD", 409, "DEVICE_LIMIT", Limit('b'))
            .On("GET /auth/code/ABCD", 200, _key.LoginJson(TestLicense.Json()));
        var permits = new List<string>();

        var ok = await Service().WaitBrowserLoginAsync(Code, info =>
        {
            permits.Add(info.UnbindToken);
            return Task.CompletedTask;
        }, None);

        Assert.True(ok);
        Assert.Equal(new[] { new string('a', 64), new string('b', 64) }, permits);
    }

    [Fact]
    public async Task AnUnexpectedPollError_IsPassedOn()
    {
        _fake.OnError("GET /auth/code/ABCD", 403, "EMAIL_NOT_VERIFIED");

        var ex = await Assert.ThrowsAsync<ApiException>(() => Service().WaitBrowserLoginAsync(Code, _ => Task.CompletedTask, None));

        Assert.Equal(ApiError.EmailNotVerified, ex.Error.Code);
    }

    private async Task WaitForRequestAsync(string route)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!_fake.To(route).Any())
        {
            Assert.True(DateTime.UtcNow < deadline, "the request never arrived");
            await Task.Yield();
        }
    }

    [Fact]
    public async Task ARevokedAnswerToAnOldRefresh_DoesNotEndTheNewerSignIn()
    {
        var service = SignedIn(TestLicense.Json());
        var release = new TaskCompletionSource();
        _fake.Hold("GET /license/ticket", release.Task);
        _fake.OnError("GET /license/ticket", 401, "TOKEN_REVOKED");
        _fake.On("POST /auth/login", 200, _key.LoginJson(TestLicense.Json(), token: "newer"));

        var refresh = service.RefreshTicketAsync(None);
        await WaitForRequestAsync("GET /license/ticket");
        await service.LoginAsync("user@example.com", "password1", None); // the user signs in again meanwhile
        release.SetResult();

        Assert.False(await refresh);
        Assert.True(service.IsSignedIn);
        Assert.False(service.SessionEnded);
    }

    [Fact]
    public async Task ARevokedTokenWhilePaying_StopsTheWait_AndSignsOut()
    {
        var service = SignedIn(TestLicense.Json(plan: "free", noExpiry: true));
        _fake.OnError("GET /pay/91", 401, "TOKEN_REVOKED");

        var ex = await Assert.ThrowsAsync<ApiException>(() => service.WaitPaymentAsync(91, None));

        Assert.Equal(ApiError.TokenRevoked, ex.Error.Code);
        Assert.False(service.IsSignedIn);
        Assert.Single(_fake.To("GET /pay/91"));
    }

    [Fact]
    public async Task AServerHiccupWhilePaying_JustWaitsForTheNextPoll()
    {
        var service = SignedIn(TestLicense.Json(plan: "free", noExpiry: true));
        var t = _key.Sign(TestLicense.Json(plan: "pro"));
        _fake.OnError("GET /pay/91", 500, "SERVER_ERROR")
            .On("GET /pay/91", 200, $"{{\"status\":\"paid\",\"license\":{{\"ticket\":\"{t.Ticket}\",\"signature\":\"{t.Signature}\"}}}}");

        Assert.Equal("paid", (await service.WaitPaymentAsync(91, None)).Status);
        Assert.Equal(2, _fake.To("GET /pay/91").Count());
        Assert.Equal(Tier.Pro, service.Tier);
    }

    [Fact]
    public async Task APaymentWaitForAGuest_FailsAtOnce()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Service().WaitPaymentAsync(91, None));

        Assert.Equal(ApiError.TokenRevoked, ex.Error.Code);
        Assert.Empty(_fake.Requests);
    }

    [Fact]
    public async Task RefreshSoon_AsksForTheTicketAfterTheDelay_InTheBackground()
    {
        SignedIn(TestLicense.Json(plan: "free", noExpiry: true)); // fills the store
        _fake.On("GET /license/ticket", 200, _key.TicketJson(TestLicense.Json(plan: "pro")));
        var delays = new List<TimeSpan>();
        var service = new AccountService(Api.Client(_fake), _store, new FixedHwHash(), "windows", "PC", "1.5.0", _key.Keys, () => _now,
            (d, _) =>
            {
                lock (delays)
                    delays.Add(d);
                return Task.CompletedTask;
            });
        var done = new TaskCompletionSource();
        service.Changed += () => done.TrySetResult();

        service.RefreshSoon(TimeSpan.FromMinutes(5));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the ticket was taken; the timeout only guards a hang
        Assert.Equal(Tier.Pro, service.Tier);
        lock (delays)
            Assert.Equal(TimeSpan.FromMinutes(5), Assert.Single(delays));
    }

    [Fact]
    public async Task TheRefreshLoop_SurvivesAFailureThatIsNotAnApiError()
    {
        SignedIn(TestLicense.Json(plan: "free", noExpiry: true)); // fills the store
        _fake.ThrowOnSend = new InvalidOperationException("not a network failure"); // escapes the client as it is
        using var cts = new CancellationTokenSource();
        var service = new AccountService(Api.Client(_fake), _store, new FixedHwHash(), "windows", "PC", "1.5.0", _key.Keys, () => _now,
            (d, ct) =>
            {
                _waits.Add(d);
                if (_waits.Count == 2)
                    cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunRefreshLoopAsync(cts.Token));

        Assert.Equal(new[] { AccountService.RetryAfterFailure, AccountService.RetryAfterFailure }, _waits);
    }

    // ----- the final review -----

    [Fact]
    public async Task ARefreshForAnOldSignIn_DoesNotOverwriteTheNewSignInsTicket()
    {
        var service = SignedIn(TestLicense.Json(plan: "free", noExpiry: true));
        var release = new TaskCompletionSource();
        _fake.Hold("GET /license/ticket", release.Task);
        _fake.On("GET /license/ticket", 200, _key.TicketJson(TestLicense.Json(plan: "pro", issued: TestLicense.Now)));
        _fake.On("POST /auth/login", 200, _key.LoginJson(TestLicense.Json(plan: "free", noExpiry: true), token: "newer"));

        var refresh = service.RefreshTicketAsync(None);
        await WaitForRequestAsync("GET /license/ticket");
        await service.LoginAsync("user@example.com", "password1", None); // the user signs in again meanwhile
        release.SetResult();

        Assert.False(await refresh);
        Assert.Equal(Tier.Free, service.Tier);
        Assert.Equal("newer", _store.Data.Token);
    }

    private AccountService LoopService(ManualDelays delays, List<Tier> tiers)
    {
        var service = new AccountService(Api.Client(_fake), _store, new FixedHwHash(), "windows", "PC", "1.5.0", _key.Keys, () => _now,
            delays.Delay);
        service.Changed += () =>
        {
            lock (tiers)
                tiers.Add(service.Tier);
        };
        return service;
    }

    private static List<Tier> Seen(List<Tier> tiers)
    {
        lock (tiers)
            return tiers.ToList();
    }

    [Fact]
    public async Task TheRefreshLoop_TellsTheScreens_AfterEveryPass_AndWhenTheTierRunsOut()
    {
        var ticket = TestLicense.Json(plan: "pro", expires: TestLicense.Now.AddHours(2));
        SignedIn(ticket); // fills the store
        _fake.OnError("GET /license/ticket", 500, "SERVER_ERROR").On("GET /license/ticket", 200, _key.TicketJson(ticket));
        var delays = new ManualDelays();
        var tiers = new List<Tier>();
        using var cts = new CancellationTokenSource();
        var loop = LoopService(delays, tiers).RunRefreshLoopAsync(cts.Token);

        // pass 1 fails: nothing changed, the screens are told anyway; the retry comes before the subscription ends
        Assert.Equal(AccountService.RetryAfterFailure, await delays.RequestedAsync(0));
        Assert.Equal(new[] { Tier.Pro }, Seen(tiers));
        _now += AccountService.RetryAfterFailure;
        delays.Complete(0);

        // pass 2 takes the ticket; the loop wakes when it runs out (not only a minute later, at the refresh)
        Assert.Equal(TimeSpan.FromHours(2) - AccountService.RetryAfterFailure, await delays.RequestedAsync(1));
        var beforeEnd = Seen(tiers).Count;
        Assert.All(Seen(tiers), t => Assert.Equal(Tier.Pro, t));
        _now = TestLicense.Now.AddHours(2);
        delays.Complete(1);

        Assert.Equal(TimeSpan.FromMinutes(1), await delays.RequestedAsync(2)); // the refresh stays at expires_at + 1 min
        Assert.Equal(beforeEnd + 1, Seen(tiers).Count);
        Assert.Equal(Tier.Free, Seen(tiers)[^1]);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        Assert.Equal(2, _fake.To("GET /license/ticket").Count());
    }

    [Fact]
    public async Task ALateWakeAtTheTierBoundary_GoesStraightToTheRefresh_AndTheLoopLives()
    {
        var ticket = TestLicense.Json(plan: "pro", expires: TestLicense.Now.AddHours(2));
        SignedIn(ticket); // fills the store
        _fake.On("GET /license/ticket", 200, _key.TicketJson(ticket)).On("GET /license/ticket", 200, _key.TicketJson(ticket));
        var delays = new ManualDelays();
        var tiers = new List<Tier>();
        using var cts = new CancellationTokenSource();
        var loop = LoopService(delays, tiers).RunRefreshLoopAsync(cts.Token);

        Assert.Equal(TimeSpan.FromHours(2), await delays.RequestedAsync(0)); // pass 1 done; asleep until the subscription ends
        _now = TestLicense.Now.AddHours(2) + TimeSpan.FromMinutes(5); // the computer slept: awake well past expires_at + 1 min
        delays.Complete(0);

        // the refresh time is already behind: no sleep with a negative duration, the next ticket is asked for at once
        Assert.Equal(AccountService.DailyRefresh, await delays.RequestedAsync(1)); // pass 2 is over, the loop sleeps again
        Assert.False(loop.IsFaulted);
        Assert.Equal(2, _fake.To("GET /license/ticket").Count());
        Assert.Equal(Tier.Free, Seen(tiers)[^1]); // the expired ticket gives Free

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public async Task ASignInWhileTheLoopSleeps_RecomputesTheSleep()
    {
        _fake.On("POST /auth/login", 200, _key.LoginJson(TestLicense.Json(plan: "pro", expires: TestLicense.Now.AddHours(3))));
        var delays = new ManualDelays();
        var tiers = new List<Tier>();
        using var cts = new CancellationTokenSource();
        var service = LoopService(delays, tiers);
        var loop = service.RunRefreshLoopAsync(cts.Token);

        Assert.Equal(AccountService.DailyRefresh, await delays.RequestedAsync(0)); // a guest: nothing to refresh soon
        Assert.Equal(new[] { Tier.Guest }, Seen(tiers));
        await service.LoginAsync("user@example.com", "password1", None);

        Assert.Equal(TimeSpan.FromHours(3), await delays.RequestedAsync(1)); // woken: the sleep now ends with the subscription
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        Assert.Empty(_fake.To("GET /license/ticket"));
    }

    private AccountService RecordingService(List<TimeSpan> delays, Action<TimeSpan, CancellationToken>? onDelay = null)
        => new(Api.Client(_fake), _store, new FixedHwHash(), "windows", "PC", "1.5.0", _key.Keys, () => _now, (d, ct) =>
        {
            DelayGuard.NotNegative(d);
            lock (delays)
                delays.Add(d);
            onDelay?.Invoke(d, ct);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task APaidStatusWithoutATicket_ChecksAgainInAMinute()
    {
        SignedIn(TestLicense.Json(plan: "free", noExpiry: true)); // fills the store
        _fake.On("GET /pay/91", 200, "{\"status\":\"paid\"}")
            .On("GET /license/ticket", 200, _key.TicketJson(TestLicense.Json(plan: "pro")));
        var delays = new List<TimeSpan>();
        var service = RecordingService(delays);
        var done = new TaskCompletionSource();
        service.Changed += () => done.TrySetResult();

        Assert.Equal("paid", (await service.PaymentStatusAsync(91, None)).Status);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the background refresh took the ticket; the timeout only guards a hang
        Assert.Equal(Tier.Pro, service.Tier); // screens go by the tier, not by "paid"
        lock (delays)
            Assert.Contains(AccountService.RefreshAfterPaidWithoutTicket, delays);
    }

    [Fact]
    public async Task APaidWaitWithARejectedTicket_ReturnsAtOnce_AndChecksAgainInAMinute()
    {
        using var other = new TestLicense();
        SignedIn(TestLicense.Json(plan: "free", noExpiry: true)); // fills the store
        var foreign = other.Sign(TestLicense.Json(plan: "pro"));
        _fake.On("GET /pay/91", 200, $"{{\"status\":\"paid\",\"license\":{{\"ticket\":\"{foreign.Ticket}\",\"signature\":\"{foreign.Signature}\"}}}}")
            .On("GET /license/ticket", 200, _key.TicketJson(TestLicense.Json(plan: "pro")));
        var delays = new List<TimeSpan>();
        var service = RecordingService(delays);
        var done = new TaskCompletionSource();
        service.Changed += () => done.TrySetResult();

        Assert.Equal("paid", (await service.WaitPaymentAsync(91, None)).Status);

        Assert.Single(_fake.To("GET /pay/91"));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Tier.Pro, service.Tier);
        lock (delays)
            Assert.Contains(AccountService.RefreshAfterPaidWithoutTicket, delays);
    }

    [Fact]
    public async Task ACancelledPaymentWait_StillChecksInTheBackground()
    {
        SignedIn(TestLicense.Json(plan: "free", noExpiry: true)); // fills the store
        _fake.On("GET /pay/91", 200, "{\"status\":\"pending\"}")
            .On("GET /license/ticket", 200, _key.TicketJson(TestLicense.Json(plan: "pro")));
        using var cts = new CancellationTokenSource();
        var delays = new List<TimeSpan>();
        var polls = 0;
        var service = RecordingService(delays, (d, ct) =>
        {
            if (d == AccountService.PaymentPollInterval && ++polls == 2)
                cts.Cancel(); // the user closes the window during the second pause
            ct.ThrowIfCancellationRequested();
        });
        var done = new TaskCompletionSource();
        service.Changed += () => done.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WaitPaymentAsync(91, cts.Token));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Tier.Pro, service.Tier);
        lock (delays)
            Assert.Contains(AccountService.RefreshAfterUnfinishedPayment, delays);
    }
}
