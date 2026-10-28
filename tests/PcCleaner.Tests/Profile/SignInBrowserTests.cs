using PcCleaner.Modules.Profile;
using PcCleaner.Tests.Account;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class SignInBrowserTests : IDisposable
{
    private readonly TestAccount _a = new();
    private int _completed;

    public SignInBrowserTests() => TestEnvironment.Init();

    public void Dispose() => _a.Dispose();

    private const string Start = "{\"code\":\"ABCD-1234\",\"url\":\"https://waterca7.ru/auth/device?code=ABCD-1234\",\"expires_in\":600}";

    private SignInViewModel Create()
    {
        // The tab is chosen after Completed is subscribed: with the fake server a whole sign-in can finish inside the constructor.
        var vm = new SignInViewModel(_a.Service, _a.Open, SignInTab.Password, () => _a.Now);
        vm.Completed += () => _completed++;
        vm.Tab = SignInTab.Browser;
        return vm;
    }

    private static Task AtTheLimit(SignInViewModel vm)
        => Wait.Until(() => vm.Step == SignInStep.DeviceLimit || vm.BrowserTask.IsCompleted, "the list of computers");

    [Fact]
    public async Task TheTab_GetsACode_OpensTheSite_AndSignsInWhenConfirmed()
    {
        _a.Fake.On("POST /auth/code", 200, Start)
            .On("GET /auth/code/ABCD-1234", 202, "{\"status\":\"pending\"}")
            .On("GET /auth/code/ABCD-1234", 200, _a.Key.LoginJson(TestLicense.Json()));

        var vm = Create();
        await Wait.Within(vm.BrowserTask, "the browser sign-in");

        Assert.Equal("ABCD-1234", vm.BrowserCode);
        Assert.Equal(new[] { "https://waterca7.ru/auth/device?code=ABCD-1234" }, _a.Opened);
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task AnExpiredCode_SaysSo_AndOffersANewOne()
    {
        _a.Fake.On("POST /auth/code", 200, Start).OnError("GET /auth/code/ABCD-1234", 410, "NOT_FOUND");

        var vm = Create();
        await Wait.Within(vm.BrowserTask, "the poll");

        Assert.Contains("устарел", vm.Error);
        Assert.Equal(0, _completed);
        vm.RestartBrowserCommand.Execute(null);
        await Wait.Within(vm.BrowserTask, "the second poll");
        Assert.Equal(2, _a.Fake.To("POST /auth/code").Count());
    }

    [Fact]
    public async Task AfterAnExpiredCode_TheNewCodeIsAvailable_AndTheTabAloneDoesNotStartOne()
    {
        _a.Fake.On("POST /auth/code", 200, Start).OnError("GET /auth/code/ABCD-1234", 410, "NOT_FOUND");
        var vm = Create();
        await Wait.Within(vm.BrowserTask, "the poll");

        vm.Tab = SignInTab.Browser; // the tab is chosen again: by design nothing starts
        await Wait.Within(vm.BrowserTask, "the poll");

        Assert.True(vm.BrowserEnded);
        Assert.True(vm.RestartBrowserCommand.CanExecute(null));
        Assert.Single(_a.Fake.To("POST /auth/code"));

        vm.RestartBrowserCommand.Execute(null);
        await Wait.Within(vm.BrowserTask, "the second poll");
        Assert.Equal(2, _a.Fake.To("POST /auth/code").Count());
        Assert.True(vm.BrowserEnded); // the fake answers 410 again
    }

    [Fact]
    public async Task AFailedRequestForACode_ShowsTheError_AndTheNewCodeIsAvailable()
    {
        _a.Fake.OnError("POST /auth/code", 500, "SERVER_ERROR")
            .On("POST /auth/code", 200, Start)
            .On("GET /auth/code/ABCD-1234", 200, _a.Key.LoginJson(TestLicense.Json()));
        var vm = Create();
        await Wait.Within(vm.BrowserTask, "the request");

        Assert.NotNull(vm.Error);
        Assert.Null(vm.BrowserCode);
        Assert.True(vm.BrowserEnded);
        Assert.True(vm.RestartBrowserCommand.CanExecute(null));

        vm.RestartBrowserCommand.Execute(null);
        await Wait.Within(vm.BrowserTask, "the second attempt");

        Assert.Null(vm.Error);
        Assert.False(vm.BrowserEnded);
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task ADeviceLimit_PausesThePoll_UntilAComputerIsReleased()
    {
        _a.Fake.On("POST /auth/code", 200, Start)
            .OnError("GET /auth/code/ABCD-1234", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")))
            .On("GET /auth/code/ABCD-1234", 200, _a.Key.LoginJson(TestLicense.Json()))
            .On("POST /device/release", 200, "{\"ok\":true}");

        var vm = Create();
        await AtTheLimit(vm);
        Assert.Equal(SignInStep.DeviceLimit, vm.Step);
        var polls = _a.Fake.To("GET /auth/code/ABCD-1234").Count();

        await vm.ReleaseAsync(vm.Devices[0]);
        await Wait.Within(vm.BrowserTask, "the poll after the release");

        Assert.Equal(1, polls);
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task Back_FromTheLimit_StopsThePoll()
    {
        _a.Fake.On("POST /auth/code", 200, Start)
            .OnError("GET /auth/code/ABCD-1234", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")));

        var vm = Create();
        await AtTheLimit(vm);
        vm.BackCommand.Execute(null);
        vm.Tab = SignInTab.Password;
        await Wait.Within(vm.BrowserTask, "the stopped poll");

        Assert.Equal(SignInStep.Password, vm.Step);
        Assert.Equal(0, _completed);
    }

    [Fact]
    public async Task ReopenBrowser_OpensTheSamePageAgain()
    {
        _a.Fake.On("POST /auth/code", 200, Start).On("GET /auth/code/ABCD-1234", 202, "{\"status\":\"pending\"}");
        var vm = Create();
        await Wait.Until(() => vm.BrowserCode is not null && _a.Opened.Count >= 1, "the first opening");

        vm.ReopenBrowserCommand.Execute(null);
        vm.Tab = SignInTab.Password; // stops the poll
        await Wait.Within(vm.BrowserTask, "the stopped poll");

        Assert.Equal(2, _a.Opened.Count);
        Assert.Equal(_a.Opened[0], _a.Opened[1]);
    }

    [Fact]
    public async Task ALimitThatArrivesAfterTheTabWasLeft_DoesNotBringTheListBack()
    {
        var answer = new TaskCompletionSource();
        _a.Fake.On("POST /auth/code", 200, Start)
            .OnError("GET /auth/code/ABCD-1234", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")));
        _a.Fake.Hold("GET /auth/code/ABCD-1234", answer.Task);
        var vm = Create();
        var poll = vm.BrowserTask;
        await Wait.Until(() => _a.Fake.To("GET /auth/code/ABCD-1234").Any(), "the poll request");

        vm.Tab = SignInTab.Password; // the answer is still on its way
        answer.SetResult();
        await Wait.Within(poll, "the cancelled poll");

        Assert.Equal(SignInStep.Password, vm.Step);
        Assert.Empty(vm.Devices);
    }

    [Fact]
    public async Task Completed_FiresOnce_EvenIfTheReleaseAlsoReportsTheSignIn()
    {
        _a.Fake.On("POST /auth/code", 200, Start)
            .On("GET /auth/code/ABCD-1234", 200, _a.Key.LoginJson(TestLicense.Json()));
        var vm = Create();
        await Wait.Within(vm.BrowserTask, "the browser sign-in");
        Assert.Equal(1, _completed);

        vm.Done(); // what ReleaseAsync does after the resumed poll has signed in on its own

        Assert.Equal(1, _completed);
    }

    // What the window does when it is closed (SignInWindow.Closed -> Stop).

    [Fact]
    public async Task Stop_EndsARunningPoll_WithoutAnotherRequest_OrAnError()
    {
        var answer = new TaskCompletionSource();
        _a.Fake.On("POST /auth/code", 200, Start)
            .On("GET /auth/code/ABCD-1234", 202, "{\"status\":\"pending\"}");
        _a.Fake.Hold("GET /auth/code/ABCD-1234", answer.Task);
        var vm = Create();
        var poll = vm.BrowserTask;
        await Wait.Until(() => _a.Fake.To("GET /auth/code/ABCD-1234").Any(), "the poll request");

        vm.Stop(); // the window was closed while the answer is on its way
        answer.SetResult();
        await Wait.Within(poll, "the cancelled poll");

        Assert.Single(_a.Fake.To("GET /auth/code/ABCD-1234"));
        Assert.Null(vm.Error);
        Assert.False(vm.BrowserEnded);
        Assert.Equal(0, _completed);
    }

    [Fact]
    public async Task Stop_EndsTheWaitBehindTheListOfComputers()
    {
        _a.Fake.On("POST /auth/code", 200, Start)
            .OnError("GET /auth/code/ABCD-1234", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")));
        var vm = Create();
        await AtTheLimit(vm);
        Assert.Equal(SignInStep.DeviceLimit, vm.Step);
        Assert.False(vm.BrowserTask.IsCompleted); // the poll stands behind the list

        vm.Stop();
        await Wait.Within(vm.BrowserTask, "the wait for a release");

        Assert.Equal(0, _completed);
        Assert.Single(_a.Fake.To("GET /auth/code/ABCD-1234"));
    }

    [Fact]
    public async Task ACodeThatArrivesAfterTheWindowClosed_IsNotShown_AndTheBrowserStaysShut()
    {
        var answer = new TaskCompletionSource();
        _a.Fake.AnswerDespiteCancel = true; // the answer was in before the cancel was seen
        _a.Fake.On("POST /auth/code", 200, Start).On("GET /auth/code/ABCD-1234", 202, "{\"status\":\"pending\"}");
        _a.Fake.Hold("POST /auth/code", answer.Task);
        var vm = Create();
        var run = vm.BrowserTask;
        await Wait.Until(() => _a.Fake.To("POST /auth/code").Any(), "the request for a code");

        vm.Stop(); // the window closed while the code was on its way
        answer.SetResult();
        await Wait.Within(run, "the cancelled run");

        Assert.Null(vm.BrowserCode);
        Assert.Null(vm.BrowserUrl);
        Assert.Empty(_a.Opened);
        Assert.Empty(_a.Fake.To("GET /auth/code/ABCD-1234"));
    }

    [Fact]
    public async Task ALimitThatArrivesAfterTheTabWasLeft_DoesNotTakeOverTheReleaseOfThePasswordSignIn()
    {
        var answer = new TaskCompletionSource();
        _a.Fake.AnswerDespiteCancel = true;
        _a.Fake.On("POST /auth/code", 200, Start)
            .OnError("GET /auth/code/ABCD-1234", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")))
            .OnError("POST /auth/login", 409, "DEVICE_LIMIT", _a.LimitJson((6, "Laptop")))
            .On("POST /auth/login", 200, _a.Key.LoginJson(TestLicense.Json()))
            .On("POST /device/release", 200, "{\"ok\":true}");
        _a.Fake.Hold("GET /auth/code/ABCD-1234", answer.Task);
        var vm = Create();
        var poll = vm.BrowserTask;
        await Wait.Until(() => _a.Fake.To("GET /auth/code/ABCD-1234").Any(), "the poll request");

        // The user goes to the password tab and runs into the limit there; the old poll's limit arrives only then.
        vm.Tab = SignInTab.Password;
        vm.Email = "user@example.com";
        vm.Password = "password1";
        vm.SignInCommand.Execute(null);
        await vm.SignInCommand.LastTask;
        Assert.Equal(SignInStep.DeviceLimit, vm.Step);
        answer.SetResult();
        await Wait.Within(poll, "the cancelled poll");

        await vm.ReleaseAsync(vm.Devices[0]);

        Assert.Equal(6, Api.Body(_a.Fake.To("POST /device/release").Single()).GetProperty("device_id").GetInt64());
        Assert.Equal(2, _a.Fake.To("POST /auth/login").Count()); // the password sign-in was repeated after the release
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task ANewCode_ForgetsTheOldPage_UntilItArrives()
    {
        _a.Fake.On("POST /auth/code", 200, Start).OnError("GET /auth/code/ABCD-1234", 410, "NOT_FOUND");
        var vm = Create();
        await Wait.Within(vm.BrowserTask, "the poll");
        Assert.NotNull(vm.BrowserUrl);
        _a.Fake.OnError("POST /auth/code", 500, "SERVER_ERROR");

        // The queue of the route holds the 200 and the 500 now: the first restart takes the 200 again, the second the 500.
        vm.RestartBrowserCommand.Execute(null);
        await Wait.Within(vm.BrowserTask, "the second poll");
        vm.RestartBrowserCommand.Execute(null);
        await Wait.Within(vm.BrowserTask, "the failed request");

        Assert.True(vm.BrowserEnded);
        Assert.Null(vm.BrowserCode);
        Assert.Null(vm.BrowserUrl);
        var opened = _a.Opened.Count;
        vm.ReopenBrowserCommand.Execute(null);
        Assert.Equal(opened, _a.Opened.Count);
    }

    [Fact]
    public void Stop_DropsThePasswords_AndCanBeCalledTwice()
    {
        var vm = Create();
        vm.Password = "secret-pass";
        vm.Password2 = "secret-pass";
        vm.Code = "123456";

        vm.Stop();
        vm.Stop();

        Assert.Equal("", vm.Password);
        Assert.Equal("", vm.Password2);
        Assert.Equal("", vm.Code);
    }
}
