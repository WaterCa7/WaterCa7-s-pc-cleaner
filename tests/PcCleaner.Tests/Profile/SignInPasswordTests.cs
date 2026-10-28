using PcCleaner.Core;
using PcCleaner.Core.Account;
using PcCleaner.Modules.Profile;
using PcCleaner.Tests.Account;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class SignInPasswordTests : IDisposable
{
    private readonly TestAccount _a = new();
    private int _completed;

    public SignInPasswordTests() => TestEnvironment.Init();

    public void Dispose() => _a.Dispose();

    private SignInViewModel Create(SignInTab tab = SignInTab.Password)
    {
        var vm = new SignInViewModel(_a.Service, _a.Open, tab, () => _a.Now);
        vm.Completed += () => _completed++;
        return vm;
    }

    private static async Task Run(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
            await async.LastTask;
    }

    [Fact]
    public async Task AGoodPassword_SignsIn_AndCompletes()
    {
        _a.Fake.On("POST /auth/login", 200, _a.Key.LoginJson(TestLicense.Json()));
        var vm = Create();
        vm.Email = " user@example.com ";
        vm.Password = "password1";

        await Run(vm.SignInCommand);

        Assert.Equal(1, _completed);
        Assert.True(_a.Service.IsSignedIn);
        Assert.Equal("user@example.com", Api.Body(_a.Fake.Requests[0]).GetProperty("email").GetString());
    }

    [Theory]
    [InlineData("not-an-email", "password1")]
    [InlineData("a@b", "password1")]
    [InlineData("user@example.com", "short")]
    public async Task BadInput_IsCaughtBeforeTheServer(string email, string password)
    {
        var vm = Create();
        vm.Email = email;
        vm.Password = password;

        await Run(vm.SignInCommand);

        Assert.NotNull(vm.Error);
        Assert.Empty(_a.Fake.Requests);
    }

    [Fact]
    public async Task AWrongPassword_SaysSo_AndPointsToTheBrowser()
    {
        _a.Fake.OnError("POST /auth/login", 401, "AUTH_FAILED");
        var vm = Create();
        vm.Email = "user@example.com";
        vm.Password = "password1";

        await Run(vm.SignInCommand);

        Assert.Equal(SignInStep.Password, vm.Step);
        Assert.Contains("браузер", vm.Error);
        Assert.Equal(0, _completed);
    }

    [Fact]
    public async Task AnUnverifiedEmail_GoesToTheCodeStep_AndSendsANewCode()
    {
        _a.Fake.OnError("POST /auth/login", 403, "EMAIL_NOT_VERIFIED")
            .On("POST /auth/resend", 200, "{\"ok\":true,\"retry_after\":60}")
            .On("POST /auth/verify", 200, _a.Key.LoginJson(TestLicense.Json()));
        var vm = Create();
        vm.Email = "user@example.com";
        vm.Password = "password1";

        await Run(vm.SignInCommand);

        Assert.Equal(SignInStep.VerifyCode, vm.Step);
        Assert.Equal("verify", Api.Body(_a.Fake.To("POST /auth/resend").Single()).GetProperty("purpose").GetString());
        vm.Code = "123456";
        await Run(vm.VerifyCommand);
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task TheReset_SendsACode_ThenSetsTheNewPassword_AndSignsIn()
    {
        _a.Fake.On("POST /auth/reset", 200, "{\"ok\":true}")
            .On("POST /auth/reset/confirm", 200, _a.Key.LoginJson(TestLicense.Json()));
        var vm = Create();
        vm.Email = "user@example.com";

        vm.ForgotCommand.Execute(null);
        Assert.Equal(SignInStep.ResetEmail, vm.Step);
        await Run(vm.SendResetCommand);
        Assert.Equal(SignInStep.ResetCode, vm.Step);
        Assert.Contains("если адрес верен", vm.Info);

        vm.Code = "111111";
        vm.Password = "new-password";
        vm.Password2 = "new-passwrd";
        await Run(vm.ConfirmResetCommand);
        Assert.NotNull(vm.Error); // the two passwords differ
        Assert.Empty(_a.Fake.To("POST /auth/reset/confirm"));

        vm.Password2 = "new-password";
        await Run(vm.ConfirmResetCommand);
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task ADeviceLimit_ShowsTheComputers_AndAfterReleaseSignsInWithThePassword()
    {
        _a.Fake.OnError("POST /auth/login", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office"), (6, "Laptop")))
            .On("POST /auth/login", 200, _a.Key.LoginJson(TestLicense.Json()))
            .On("POST /device/release", 200, "{\"ok\":true}");
        var vm = Create();
        vm.Email = "user@example.com";
        vm.Password = "password1";

        await Run(vm.SignInCommand);
        Assert.Equal(SignInStep.DeviceLimit, vm.Step);
        Assert.Equal(new[] { "Office", "Laptop" }, vm.Devices.Select(d => d.Name));

        await vm.ReleaseAsync(vm.Devices[0]);

        Assert.Equal(5, Api.Body(_a.Fake.To("POST /device/release").Single()).GetProperty("device_id").GetInt64());
        Assert.Equal(2, _a.Fake.To("POST /auth/login").Count());
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task ABurntPermit_AsksAgain_AndShowsTheNewList()
    {
        _a.Fake.OnError("POST /auth/login", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")))
            .OnError("POST /device/release", 429, "UNBIND_QUOTA");
        var vm = Create();
        vm.Email = "user@example.com";
        vm.Password = "password1";
        await Run(vm.SignInCommand);

        await vm.ReleaseAsync(vm.Devices[0]);

        Assert.Equal(SignInStep.DeviceLimit, vm.Step);
        Assert.Equal("msg UNBIND_QUOTA", vm.Error);
        Assert.Equal(2, _a.Fake.To("POST /auth/login").Count()); // a fresh DEVICE_LIMIT with a new permit
    }

    [Fact]
    public async Task NoNetwork_SaysSo()
    {
        _a.Fake.ThrowOnSend = new System.Net.Http.HttpRequestException("down");
        var vm = Create();
        vm.Email = "user@example.com";
        vm.Password = "password1";

        await Run(vm.SignInCommand);

        Assert.Contains("waterca7.ru", vm.Error);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Back_FromTheReset_ReturnsToThePassword()
    {
        var vm = Create();
        vm.ForgotCommand.Execute(null);

        vm.BackCommand.Execute(null);

        Assert.Equal(SignInStep.Password, vm.Step);
        Assert.True(vm.ShowTabs);
    }

    [Fact]
    public void ATab_PicksItsStep()
    {
        var vm = Create(SignInTab.Register);
        Assert.Equal(SignInStep.Register, vm.Step);

        vm.Tab = SignInTab.Password;
        Assert.Equal(SignInStep.Password, vm.Step);
        Assert.True(vm.IsPasswordStep);
    }
}
