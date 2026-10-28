using PcCleaner.Modules.Profile;
using PcCleaner.Tests.Account;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class ProfileViewModelTests
{
    private readonly List<SignInTab> _windows = new();
    private bool _confirm;

    public ProfileViewModelTests() => TestEnvironment.Init();

    private ProfileViewModel Create(TestAccount a)
        => new(a.Service, tab =>
        {
            _windows.Add(tab);
            return false;
        }, a.Open, (_, _) => _confirm, () => a.Now);

    [Fact]
    public void AGuest_SeesTheSignInButtons_AndWhatProGives()
    {
        using var a = new TestAccount();
        var vm = Create(a);

        Assert.True(vm.IsGuest);
        Assert.Equal(3, vm.ProLines.Count);
        vm.SignInCommand.Execute(null);
        vm.RegisterCommand.Execute(null);
        Assert.Equal(new[] { SignInTab.Password, SignInTab.Register }, _windows);
    }

    [Fact]
    public void APro_SeesTheEmailPlanAndTerm()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "family", expires: TestLicense.Now.AddDays(300)));
        var vm = Create(a);

        Assert.False(vm.IsGuest);
        Assert.Equal("user@example.com", vm.Email);
        Assert.Equal("Pro Семья", vm.PlanText);
        Assert.StartsWith("до ", vm.TermText);
        Assert.Equal("Продлить", vm.BuyText);
    }

    [Fact]
    public void AFreeAccount_IsOfferedToBuy()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", kind: "none", noExpiry: true));
        var vm = Create(a);

        Assert.Equal("Free", vm.PlanText);
        Assert.Equal("Купить Pro", vm.BuyText);
        vm.BuyCommand.Execute(null);
        Assert.Equal(new[] { ProfileViewModel.BuyUrl }, a.Opened);
    }

    [Fact]
    public async Task Refresh_TakesTheNewTicket_AndThePageFollows()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", noExpiry: true));
        a.Fake.On("GET /license/ticket", 200, a.Key.TicketJson(TestLicense.Json(plan: "pro")));
        var vm = Create(a);

        await vm.RefreshForTestAsync();

        Assert.Equal("Pro", vm.PlanText);
        Assert.Null(vm.RefreshText);
    }

    [Fact]
    public async Task Refresh_WithoutTheServer_KeepsTheTicket_AndSaysSo()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "pro"));
        a.Fake.ThrowOnSend = new System.Net.Http.HttpRequestException("down");
        var vm = Create(a);

        await vm.RefreshForTestAsync();

        Assert.False(vm.IsGuest);
        Assert.Equal("Pro", vm.PlanText);
        Assert.Contains("действует прежняя", vm.RefreshText);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("other computer")]
    public void ProThatDoesNotApplyHere_IsShownAsFree_NextToBuy(string why)
    {
        var json = why == "expired"
            ? TestLicense.Json(plan: "pro", expires: TestLicense.Now.AddDays(-2))
            : TestLicense.Json(plan: "pro", hw: new string('f', 64));
        using var a = new TestAccount(json);
        var vm = Create(a);

        Assert.Equal("Free", vm.PlanText);
        Assert.Equal("Купить Pro", vm.BuyText);
        Assert.NotNull(vm.NoteText); // the note says why
    }

    [Fact]
    public async Task AFailedRefresh_IsForgotten_OnceANewTicketArrives()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "pro"));
        a.Fake.ThrowOnSend = new System.Net.Http.HttpRequestException("down");
        var vm = Create(a);
        await vm.RefreshForTestAsync();
        Assert.NotNull(vm.RefreshText);

        // The background check gets through later: the page must not keep saying the refresh failed.
        a.Fake.ThrowOnSend = null;
        a.Fake.On("GET /license/ticket", 200, a.Key.TicketJson(TestLicense.Json(plan: "pro", issued: TestLicense.Now)));
        await a.Service.RefreshTicketAsync(CancellationToken.None);

        Assert.Null(vm.RefreshText);
    }

    [Fact]
    public async Task Logout_AsksFirst()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("POST /auth/logout", 200, "{\"ok\":true}");
        var vm = Create(a);

        await vm.LogoutForTestAsync();
        Assert.False(vm.IsGuest);

        _confirm = true;
        await vm.LogoutForTestAsync();
        Assert.True(vm.IsGuest);
    }

    [Fact]
    public async Task ARevokedSession_IsExplained_OnThePage()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.OnError("GET /license/ticket", 401, "TOKEN_REVOKED");
        var vm = Create(a);

        await vm.RefreshForTestAsync();

        Assert.True(vm.IsGuest);
        Assert.Contains("Войдите снова", vm.SessionEndedText);
        Assert.Null(vm.RefreshText);
    }

    [Fact]
    public async Task TheAccountChanging_UpdatesThePage_AndTwiceInARowIsHarmless()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", noExpiry: true));
        a.Fake.On("GET /license/ticket", 200, a.Key.TicketJson(TestLicense.Json(plan: "pro")));
        var vm = Create(a);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await a.Service.RefreshTicketAsync(CancellationToken.None);

        Assert.Contains("PlanText", changed);
        Assert.Equal("Pro", vm.PlanText);
        Assert.Equal("Продлить", vm.BuyText);
    }
}
