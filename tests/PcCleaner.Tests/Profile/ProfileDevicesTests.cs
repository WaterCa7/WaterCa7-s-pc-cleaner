using PcCleaner.Modules.Profile;
using PcCleaner.Tests.Account;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class ProfileDevicesTests
{
    private const string Devices = "{\"devices\":[" +
        "{\"id\":42,\"name\":\"HOME\",\"platform\":\"windows\",\"bound_at\":\"2026-10-24T10:00:00Z\",\"last_seen_at\":\"2026-11-01T09:00:00Z\",\"current\":true}," +
        "{\"id\":43,\"name\":\"Mac\",\"platform\":\"mac\",\"bound_at\":\"2026-10-25T10:00:00Z\",\"last_seen_at\":null,\"current\":false,\"whose\":\"son@x.y\",\"member\":true}" +
        "],\"bind_limit\":3}";

    private readonly List<string> _questions = new();
    private bool _confirm = true;

    public ProfileDevicesTests() => TestEnvironment.Init();

    private ProfileViewModel Create(TestAccount a) => new(a.Service, _ => false, a.Open, (t, m) =>
    {
        _questions.Add(m);
        return _confirm;
    }, () => a.Now);

    [Fact]
    public async Task TheComputers_AreListed_WithThisOneMarked_AndWhose()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("GET /devices", 200, Devices);
        var vm = Create(a);

        await vm.LoadDevicesAsync();

        Assert.Equal(new[] { "HOME", "Mac" }, vm.Devices.Select(d => d.Name));
        Assert.True(vm.Devices[0].IsCurrent);
        Assert.Equal("son@x.y", vm.Devices[1].Whose);
        Assert.Equal("чей: son@x.y", vm.Devices[1].WhoseText);
        Assert.Null(vm.Devices[0].WhoseText);
        Assert.Contains("2", vm.DevicesText);
        Assert.Contains("3", vm.DevicesText);
    }

    [Fact]
    public async Task AnotherComputer_IsUnbound_AfterYes_AndTheListReloads()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("GET /devices", 200, Devices).On("POST /device/unbind", 200, "{\"ok\":true}");
        var vm = Create(a);
        await vm.LoadDevicesAsync();

        await vm.UnbindForTestAsync(vm.Devices[1]);

        Assert.Equal(43, Api.Body(a.Fake.To("POST /device/unbind").Single()).GetProperty("device_id").GetInt64());
        Assert.Equal(2, a.Fake.To("GET /devices").Count());
        Assert.True(a.Service.IsSignedIn);
    }

    [Fact]
    public async Task ThisComputer_IsUnbound_AsASignOut_AndTheQuestionSaysSo()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("GET /devices", 200, Devices).On("POST /device/unbind", 200, "{\"ok\":true}");
        var vm = Create(a);
        await vm.LoadDevicesAsync();

        await vm.UnbindForTestAsync(vm.Devices[0]);

        Assert.Contains("выйдете", _questions.Single());
        Assert.False(a.Service.IsSignedIn);
        Assert.Empty(vm.Devices);
    }

    [Fact]
    public async Task No_UnbindsNothing_AndAQuotaShowsTheServersWords()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("GET /devices", 200, Devices).OnError("POST /device/unbind", 429, "UNBIND_QUOTA");
        var vm = Create(a);
        await vm.LoadDevicesAsync();

        _confirm = false;
        await vm.UnbindForTestAsync(vm.Devices[1]);
        Assert.Empty(a.Fake.To("POST /device/unbind"));

        _confirm = true;
        await vm.UnbindForTestAsync(vm.Devices[1]);
        Assert.Equal("msg UNBIND_QUOTA", vm.DevicesText);
    }

    [Fact]
    public async Task Offline_TheListSaysSo()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.ThrowOnSend = new System.Net.Http.HttpRequestException("down");
        var vm = Create(a);

        await vm.LoadDevicesAsync();

        Assert.Contains("waterca7.ru", vm.DevicesText);
        Assert.Empty(vm.Devices);
    }

    [Fact]
    public async Task SigningIn_WhileThePageIsOpen_LoadsTheComputers_Once()
    {
        using var a = new TestAccount();
        a.Fake.On("POST /auth/login", 200, a.Key.LoginJson(TestLicense.Json())).On("GET /devices", 200, Devices)
            .On("GET /license/ticket", 200, a.Key.TicketJson(TestLicense.Json(plan: "family")));
        var vm = Create(a);
        Assert.True(vm.IsGuest);
        Assert.Empty(a.Fake.To("GET /devices"));

        await a.Service.LoginAsync("user@example.com", "password1", CancellationToken.None);

        await Wait.Until(() => vm.Devices.Count == 2, "the computers after the sign-in");
        // Later changes of the account (refreshed tickets, Changed twice in a row) must not load the list again.
        await vm.RefreshForTestAsync();
        await vm.RefreshForTestAsync();
        Assert.Equal("Pro Семья", vm.PlanText);
        await Task.Delay(300); // a wrongly started second load would have reached the server by now
        Assert.Single(a.Fake.To("GET /devices"));
    }

    [Fact]
    public async Task SigningOut_ClearsTheListAndItsText()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("GET /devices", 200, Devices).On("POST /auth/logout", 200, "{\"ok\":true}");
        var vm = Create(a);
        await vm.LoadDevicesAsync();
        Assert.Equal(2, vm.Devices.Count);
        Assert.NotNull(vm.DevicesText);

        await vm.LogoutForTestAsync();

        Assert.True(vm.IsGuest);
        Assert.Empty(vm.Devices);
        Assert.Null(vm.DevicesText);
    }

    [Fact]
    public async Task SigningOut_AndInAgainWithTheSameEmail_LoadsTheComputersAgain()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("GET /devices", 200, Devices).On("POST /auth/logout", 200, "{\"ok\":true}")
            .On("POST /auth/login", 200, a.Key.LoginJson(TestLicense.Json()));
        var vm = Create(a);
        await vm.LoadDevicesAsync();
        await vm.LogoutForTestAsync();
        Assert.Empty(vm.Devices);

        await a.Service.LoginAsync("user@example.com", "password1", CancellationToken.None);

        await Wait.Until(() => vm.Devices.Count == 2, "the computers after signing in again");
        Assert.Equal(2, a.Fake.To("GET /devices").Count());
    }

    private const string OtherDevices = "{\"devices\":[" +
        "{\"id\":50,\"name\":\"BOX\",\"platform\":\"windows\",\"bound_at\":\"2026-10-24T10:00:00Z\",\"last_seen_at\":null,\"current\":true}" +
        "],\"bind_limit\":2}";

    [Fact]
    public async Task ALateList_OfAnAccountThatSignedOut_DoesNotOverwriteTheNextAccount()
    {
        using var a = new TestAccount(TestLicense.Json());
        // The held request of the first account is answered last, so the second account's answer comes first in the queue.
        a.Fake.On("GET /devices", 200, OtherDevices).On("GET /devices", 200, Devices).On("POST /auth/logout", 200, "{\"ok\":true}")
            .On("POST /auth/login", 200, a.Key.LoginJson(TestLicense.Json()).Replace("user@example.com", "b@example.com"));
        var vm = Create(a);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Fake.Hold("GET /devices", gate.Task);
        var late = vm.LoadDevicesAsync();
        await Wait.Until(() => a.Fake.To("GET /devices").Any(), "the first account's request");

        await a.Service.LogoutAsync(CancellationToken.None);
        await a.Service.LoginAsync("b@example.com", "password1", CancellationToken.None);
        await Wait.Until(() => vm.Devices.Count == 1, "the second account's computers");
        Assert.Equal("b@example.com", vm.Email);
        gate.SetResult();
        await Wait.Within(late, "the first account's late answer");

        Assert.Equal(new[] { "BOX" }, vm.Devices.Select(d => d.Name));
        Assert.Equal("Компьютеров: 1 из 2", vm.DevicesText);
    }

    [Fact]
    public async Task ALateError_OfAnAccountThatSignedOut_ShowsNothingOnTheGuest()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.OnError("GET /devices", 500, "SERVER_ERROR").On("POST /auth/logout", 200, "{\"ok\":true}");
        var vm = Create(a);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Fake.Hold("GET /devices", gate.Task);
        var late = vm.LoadDevicesAsync();
        await Wait.Until(() => a.Fake.To("GET /devices").Any(), "the request");

        await a.Service.LogoutAsync(CancellationToken.None);
        gate.SetResult();
        await Wait.Within(late, "the late error");

        Assert.True(vm.IsGuest);
        Assert.Null(vm.DevicesText);
        Assert.Empty(vm.Devices);
    }

    [Fact]
    public async Task ALateError_OfAnAccountThatSignedOut_DoesNotOverwriteTheNextAccount()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("GET /devices", 200, OtherDevices).OnError("GET /devices", 500, "SERVER_ERROR").On("POST /auth/logout", 200, "{\"ok\":true}")
            .On("POST /auth/login", 200, a.Key.LoginJson(TestLicense.Json()).Replace("user@example.com", "b@example.com"));
        var vm = Create(a);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Fake.Hold("GET /devices", gate.Task);
        var late = vm.LoadDevicesAsync();
        await Wait.Until(() => a.Fake.To("GET /devices").Any(), "the first account's request");

        await a.Service.LogoutAsync(CancellationToken.None);
        await a.Service.LoginAsync("b@example.com", "password1", CancellationToken.None);
        await Wait.Until(() => vm.Devices.Count == 1, "the second account's computers");
        gate.SetResult();
        await Wait.Within(late, "the first account's late error");

        Assert.Equal("Компьютеров: 1 из 2", vm.DevicesText);
        Assert.Single(vm.Devices);
    }

    [Fact]
    public async Task ABackgroundLoad_ThatFailsUnexpectedly_SaysSoInsteadOfVanishing()
    {
        using var a = new TestAccount();
        a.Fake.On("POST /auth/login", 200, a.Key.LoginJson(TestLicense.Json())).On("GET /devices", 200, Devices);
        var vm = new ProfileViewModel(a.Service, _ => false, a.Open, (t, m) => true, () => throw new InvalidOperationException("boom"));

        await a.Service.LoginAsync("user@example.com", "password1", CancellationToken.None);

        await Wait.Until(() => vm.DevicesText is not null, "the text of the failed load");
        Assert.Empty(vm.Devices);
    }

    [Fact]
    public async Task AnAccountAlreadySignedIn_LoadsTheComputers_OnlyOnNavigation()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("GET /devices", 200, Devices);
        var vm = Create(a);
        Assert.Empty(a.Fake.To("GET /devices"));

        await vm.OnNavigatedToAsync();

        Assert.Equal(2, vm.Devices.Count);
        Assert.Single(a.Fake.To("GET /devices"));
    }
}
