using PcCleaner.Modules.Profile;
using PcCleaner.Tests.Account;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class SignInRegisterTests : IDisposable
{
    private readonly TestAccount _a = new();
    private int _completed;

    public SignInRegisterTests() => TestEnvironment.Init();

    public void Dispose() => _a.Dispose();

    private SignInViewModel Create()
    {
        var vm = new SignInViewModel(_a.Service, _a.Open, SignInTab.Register, () => _a.Now);
        vm.Completed += () => _completed++;
        vm.Email = "new@example.com";
        vm.Password = vm.Password2 = "password1";
        vm.Consent = true;
        return vm;
    }

    [Fact]
    public async Task Register_ThenTheCode_SignsIn()
    {
        _a.Fake.On("POST /auth/register", 200, "{\"pending\":true}")
            .On("POST /auth/verify", 200, _a.Key.LoginJson(TestLicense.Json(plan: "free", noExpiry: true)));
        var vm = Create();

        await vm.RunAsync(() => vm.RegisterForTestAsync());
        Assert.Equal(SignInStep.VerifyCode, vm.Step);
        Assert.Contains("если адрес верен", vm.Info);

        vm.Code = "12 34 56";
        await vm.RunAsync(() => vm.VerifyForTestAsync());

        Assert.Equal("123456", Api.Body(_a.Fake.To("POST /auth/verify").Single()).GetProperty("code").GetString());
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task WithoutConsent_OrDifferentPasswords_NothingIsSent()
    {
        var vm = Create();
        vm.Consent = false;
        await vm.RunAsync(() => vm.RegisterForTestAsync());
        Assert.NotNull(vm.Error);

        vm.Consent = true;
        vm.Password2 = "password2";
        await vm.RunAsync(() => vm.RegisterForTestAsync());
        Assert.NotNull(vm.Error);

        Assert.Empty(_a.Fake.Requests);
    }

    [Fact]
    public async Task ATakenEmail_SendsTheUserToSignIn()
    {
        _a.Fake.OnError("POST /auth/register", 409, "EMAIL_TAKEN");
        var vm = Create();

        await vm.RunAsync(() => vm.RegisterForTestAsync());

        Assert.Equal(SignInStep.Password, vm.Step);
        Assert.Contains("уже есть", vm.Error);
    }

    [Fact]
    public async Task Resend_WaitsItsMinute_ThenSendsAgain()
    {
        _a.Fake.On("POST /auth/register", 200, "{\"pending\":true}")
            .On("POST /auth/resend", 200, "{\"ok\":true,\"retry_after\":60}");
        var vm = Create();
        await vm.RunAsync(() => vm.RegisterForTestAsync());

        Assert.False(vm.CanResend);
        Assert.Contains("60", vm.ResendText);

        _a.Now += TimeSpan.FromSeconds(59);
        vm.Tick();
        Assert.False(vm.CanResend);
        Assert.Contains("1", vm.ResendText);

        _a.Now += TimeSpan.FromSeconds(1);
        vm.Tick();
        Assert.True(vm.CanResend);
        await vm.RunAsync(() => vm.ResendForTestAsync());

        Assert.Equal("verify", Api.Body(_a.Fake.To("POST /auth/resend").Single()).GetProperty("purpose").GetString());
        Assert.False(vm.CanResend);
    }

    [Fact]
    public async Task ADeviceLimitAfterVerify_SignsInWithThePassword_AfterTheRelease()
    {
        _a.Fake.On("POST /auth/register", 200, "{\"pending\":true}")
            .OnError("POST /auth/verify", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")))
            .On("POST /device/release", 200, "{\"ok\":true}")
            .On("POST /auth/login", 200, _a.Key.LoginJson(TestLicense.Json()));
        var vm = Create();
        await vm.RunAsync(() => vm.RegisterForTestAsync());
        vm.Code = "123456";
        await vm.RunAsync(() => vm.VerifyForTestAsync());
        Assert.Equal(SignInStep.DeviceLimit, vm.Step);

        await vm.ReleaseAsync(vm.Devices[0]);

        Assert.Single(_a.Fake.To("POST /auth/verify")); // the code is spent: not repeated
        Assert.Equal("password1", Api.Body(_a.Fake.To("POST /auth/login").Single()).GetProperty("password").GetString());
        Assert.Equal(1, _completed);
    }

    [Fact]
    public void TheDocuments_OpenInTheBrowser()
    {
        var vm = Create();

        vm.OpenOfferCommand.Execute(null);
        vm.OpenPrivacyCommand.Execute(null);

        Assert.Equal(new[] { SignInViewModel.OfferUrl, SignInViewModel.PrivacyUrl }, _a.Opened);
    }
}
