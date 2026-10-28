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
    [InlineData("user@", "password1")]
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
    public async Task Release_AsksFirst_AndANoSendsNothing()
    {
        _a.Fake.OnError("POST /auth/login", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")))
            .On("POST /auth/login", 200, _a.Key.LoginJson(TestLicense.Json()))
            .On("POST /device/release", 200, "{\"ok\":true}");
        var questions = new List<(string Title, string Text)>();
        var answer = false;
        var vm = new SignInViewModel(_a.Service, _a.Open, SignInTab.Password, () => _a.Now, (title, text) =>
        {
            questions.Add((title, text));
            return answer;
        });
        vm.Completed += () => _completed++;
        vm.Email = "user@example.com";
        vm.Password = "password1";
        await Run(vm.SignInCommand);

        vm.ReleaseCommand.Execute(vm.Devices[0]);
        await vm.ReleaseCommand.LastTask;

        Assert.Single(questions);
        Assert.Contains("Office", questions[0].Text);
        Assert.Empty(_a.Fake.To("POST /device/release"));
        Assert.Equal(SignInStep.DeviceLimit, vm.Step);
        Assert.Equal(0, _completed);

        answer = true;
        await vm.ReleaseAsync(vm.Devices[0]);

        Assert.Single(_a.Fake.To("POST /device/release"));
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task TheCode_TakesOnlyLatinDigits()
    {
        _a.Fake.On("POST /auth/reset", 200, "{\"ok\":true}").On("POST /auth/reset/confirm", 200, _a.Key.LoginJson(TestLicense.Json()));
        var vm = Create();
        vm.Email = "user@example.com";
        vm.ForgotCommand.Execute(null);
        await Run(vm.SendResetCommand);
        vm.Password = vm.Password2 = "newpassword";
        vm.Code = "١٢٣٤٥٦"; // Arabic-Indic digits: char.IsDigit says yes, the server says no

        await Run(vm.ConfirmResetCommand);

        Assert.Equal("Введите 6 цифр из письма.", vm.Error);
        Assert.Empty(_a.Fake.To("POST /auth/reset/confirm"));
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

    private const string BrowserStart = "{\"code\":\"ABCD-1234\",\"url\":\"https://waterca7.ru/auth/device?code=ABCD-1234\",\"expires_in\":600}";

    private static string Token(char c) => new(c, 64);

    private static string UnbindTokenOf(FakeApi.Recorded request) => Api.Body(request).GetProperty("unbind_token").GetString()!;

    [Theory]
    [InlineData("AUTH_FAILED", 401)]
    [InlineData("NOT_FOUND", 404)]
    public async Task ASpentPermit_SaysThePermitIsOld_NotThatThePasswordIsWrong(string code, int status)
    {
        _a.Fake.OnError("POST /auth/login", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")))
            .OnError("POST /device/release", status, code);
        var vm = Create();
        vm.Email = "user@example.com";
        vm.Password = "password1";
        await Run(vm.SignInCommand);

        await vm.ReleaseAsync(vm.Devices[0]);

        Assert.Equal(SignInStep.DeviceLimit, vm.Step);
        Assert.Contains("Разрешение на отвязку устарело", vm.Error);
        Assert.DoesNotContain("пароль", vm.Error);
    }

    [Fact]
    public async Task TheResetThenADeviceLimit_SignsInWithTheNewPassword_AfterTheRelease()
    {
        _a.Fake.OnError("POST /auth/reset/confirm", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")))
            .On("POST /device/release", 200, "{\"ok\":true}")
            .On("POST /auth/login", 200, _a.Key.LoginJson(TestLicense.Json()));
        var vm = Create();
        vm.Email = "user@example.com";
        vm.ForgotCommand.Execute(null);
        vm.Code = "111111";
        vm.Password = vm.Password2 = "new-password";

        await Run(vm.ConfirmResetCommand);
        Assert.Equal(SignInStep.DeviceLimit, vm.Step);
        await vm.ReleaseAsync(vm.Devices[0]);

        Assert.Equal("new-password", Api.Body(_a.Fake.To("POST /auth/login").Single()).GetProperty("password").GetString());
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task ASecondDeviceLimit_ReplacesThePermit_TheNextReleaseCarriesTheLatest()
    {
        _a.Fake.OnError("POST /auth/login", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")))
            .OnError("POST /auth/login", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")).Replace(Token('a'), Token('b')))
            .On("POST /auth/login", 200, _a.Key.LoginJson(TestLicense.Json()))
            .OnError("POST /device/release", 401, "AUTH_FAILED")
            .On("POST /device/release", 200, "{\"ok\":true}");
        var vm = Create();
        vm.Email = "user@example.com";
        vm.Password = "password1";
        await Run(vm.SignInCommand);

        await vm.ReleaseAsync(vm.Devices[0]); // the permit is refused, the second DEVICE_LIMIT brings a new one
        Assert.Equal(SignInStep.DeviceLimit, vm.Step);
        await vm.ReleaseAsync(vm.Devices[0]);

        Assert.Equal(new[] { Token('a'), Token('b') }, _a.Fake.To("POST /device/release").Select(UnbindTokenOf));
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task TheBrowserPoll_StaysAliveWhileTheListIsOpen_AndSignsInAfterTheRelease()
    {
        _a.Fake.On("POST /auth/code", 200, BrowserStart)
            .OnError("GET /auth/code/ABCD-1234", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")))
            .On("GET /auth/code/ABCD-1234", 200, _a.Key.LoginJson(TestLicense.Json()))
            .On("POST /device/release", 200, "{\"ok\":true}");
        var vm = Create(SignInTab.Browser);
        await Wait.Until(() => vm.Step == SignInStep.DeviceLimit || vm.BrowserTask.IsCompleted, "the list of computers");
        Assert.Equal(SignInStep.DeviceLimit, vm.Step);

        await vm.ReleaseAsync(vm.Devices[0]);
        await Wait.Within(vm.BrowserTask, "the browser poll");

        Assert.Equal(1, _completed);
        Assert.True(_a.Service.IsSignedIn);
    }

    [Fact]
    public async Task TheBrowserPoll_ARefusedRelease_ShowsWhyOnTheReappearingList()
    {
        _a.Fake.On("POST /auth/code", 200, BrowserStart)
            .OnError("GET /auth/code/ABCD-1234", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")))
            .OnError("POST /device/release", 429, "UNBIND_QUOTA");
        var vm = Create(SignInTab.Browser);
        await Wait.Until(() => vm.Step == SignInStep.DeviceLimit || vm.BrowserTask.IsCompleted, "the list of computers");

        await vm.ReleaseAsync(vm.Devices[0]);
        await Wait.Until(() => (_a.Fake.To("GET /auth/code/ABCD-1234").Count() >= 2 && vm.Step == SignInStep.DeviceLimit) || vm.BrowserTask.IsCompleted,
            "the list again after the refused release");

        Assert.Equal(SignInStep.DeviceLimit, vm.Step);
        Assert.Equal("msg UNBIND_QUOTA", vm.Error);
        vm.Tab = SignInTab.Password; // stops the poll
        await Wait.Within(vm.BrowserTask, "the browser poll");
    }

    [Fact]
    public async Task AnEmailThatIsTaken_MovesTheTabToThePassword_Too()
    {
        _a.Fake.OnError("POST /auth/register", 409, "EMAIL_TAKEN");
        var vm = Create(SignInTab.Register);
        vm.Email = "user@example.com";
        vm.Password = vm.Password2 = "password1";
        vm.Consent = true;

        await Run(vm.RegisterCommand);

        Assert.Equal(SignInStep.Password, vm.Step);
        Assert.Equal(SignInTab.Password, vm.Tab);
        Assert.NotNull(vm.Error);
    }

    [Fact]
    public async Task AnUnverifiedEmail_TheTextHintsAtSpam()
    {
        _a.Fake.OnError("POST /auth/login", 403, "EMAIL_NOT_VERIFIED")
            .On("POST /auth/resend", 200, "{\"ok\":true,\"retry_after\":60}");
        var vm = Create();
        vm.Email = "user@example.com";
        vm.Password = "password1";

        await Run(vm.SignInCommand);

        Assert.Contains("Спам", vm.Info);
    }

    [Fact]
    public async Task ChoosingTheBrowserTabAgain_DoesNotAskForANewCode()
    {
        _a.Fake.On("POST /auth/code", 200, BrowserStart).On("GET /auth/code/ABCD-1234", 202, "{\"status\":\"pending\"}");
        var vm = Create(SignInTab.Browser);

        vm.Tab = SignInTab.Browser;
        await Wait.Within(vm.BrowserTask, "the browser poll");

        Assert.Single(_a.Fake.To("POST /auth/code"));
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
