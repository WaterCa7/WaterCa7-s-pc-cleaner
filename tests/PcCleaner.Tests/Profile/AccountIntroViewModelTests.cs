using PcCleaner.Core;
using PcCleaner.Modules.Profile;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class AccountIntroViewModelTests
{
    private readonly AppSettings _settings = new();
    private int _saves;
    private bool _signedIn;
    private readonly List<SignInTab> _windows = new();
    private bool _windowResult;

    private readonly List<Action> _subscribers = new();

    private AccountIntroViewModel Create() => new(() => _settings, _ => _saves++, () => _signedIn, tab =>
    {
        _windows.Add(tab);
        return _windowResult;
    }, handler =>
    {
        _subscribers.Add(handler);
        return () => _subscribers.Remove(handler);
    });

    /// <summary>The account says it changed (to whoever is subscribed right now).</summary>
    private void AccountChanged()
    {
        foreach (var handler in _subscribers.ToArray())
            handler();
    }

    [Fact]
    public void AFirstRun_ShowsTheScreen_NotAsAnUpdate()
    {
        var vm = Create();

        Assert.True(vm.ShowAtStart(screenshotRun: false));
        Assert.False(vm.IsUpdate);
    }

    [Fact]
    public void AnUpdatedInstall_ShowsItOnce_AsAnUpdate()
    {
        _settings.UiMode = UiMode.Expert;
        var vm = Create();

        Assert.True(vm.ShowAtStart(false));
        Assert.True(vm.IsUpdate);
        vm.SkipCommand.Execute(null);

        Assert.True(_settings.AccountIntroShown);
        Assert.False(Create().ShowAtStart(false));
    }

    [Fact]
    public void ASignedInUser_NeverSeesIt_AndIsMarked()
    {
        _signedIn = true;
        var vm = Create();

        Assert.False(vm.ShowAtStart(false));
        Assert.True(_settings.AccountIntroShown);
        Assert.Equal(1, _saves);
    }

    [Fact]
    public void AScreenshotRun_ShowsItOnlyWhenForced()
    {
        Assert.False(Create().ShowAtStart(screenshotRun: true));
        Assert.True(Create().ShowAtStart(screenshotRun: true, forced: true));
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void SigningIn_OpensTheWindow_AndClosesTheScreenOnlyOnSuccess()
    {
        var vm = Create();
        vm.ShowAtStart(false);
        var closed = 0;
        vm.Closed += () => closed++;

        vm.SignInCommand.Execute(null);
        Assert.True(vm.IsVisible); // the window was closed without signing in

        _windowResult = true;
        vm.RegisterCommand.Execute(null);

        Assert.Equal(new[] { SignInTab.Password, SignInTab.Register }, _windows);
        Assert.False(vm.IsVisible);
        Assert.True(_settings.AccountIntroShown);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void AWindowClosedAfterTheAccountSignedIn_ClosesTheScreenToo()
    {
        var vm = Create();
        vm.ShowAtStart(false);
        var closed = 0;
        vm.Closed += () => closed++;

        // The account was already signed in when the window returned false.
        _windowResult = false;
        _signedIn = true;
        vm.SignInCommand.Execute(null);

        Assert.False(vm.IsVisible);
        Assert.True(_settings.AccountIntroShown);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Skipping_ClosesTheScreen_AndTellsTheStartToGoOn()
    {
        var vm = Create();
        vm.ShowAtStart(false);
        var closed = 0;
        vm.Closed += () => closed++;

        vm.SkipCommand.Execute(null);

        Assert.False(vm.IsVisible);
        Assert.Equal(1, closed);
        Assert.Empty(_windows);
    }

    [Fact]
    public void AWindowClosedWhileItsRequestIsStillOnItsWay_KeepsTheScreen_UntilTheAccountSignsIn()
    {
        var vm = Create();
        vm.ShowAtStart(false);
        var closed = 0;
        vm.Closed += () => closed++;

        // The window returns false at once (the request is not cancelled) and the account is not signed in yet.
        vm.SignInCommand.Execute(null);
        Assert.True(vm.IsVisible);
        AccountChanged(); // an unrelated change: still not signed in
        Assert.True(vm.IsVisible);

        _signedIn = true; // the request finishes and signs in
        AccountChanged();

        Assert.False(vm.IsVisible);
        Assert.True(_settings.AccountIntroShown);
        Assert.Equal(1, _saves);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void AfterTheScreenClosed_FurtherNotificationsDoNothing()
    {
        var vm = Create();
        vm.ShowAtStart(false);
        var closed = 0;
        vm.Closed += () => closed++;
        _signedIn = true;
        AccountChanged();
        Assert.Empty(_subscribers); // unsubscribed when it closed

        AccountChanged();
        AccountChanged();

        Assert.Equal(1, _saves);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void AScreenClosedByAChoice_StopsFollowingTheAccount()
    {
        var vm = Create();
        vm.ShowAtStart(false);
        Assert.Single(_subscribers);
        var closed = 0;
        vm.Closed += () => closed++;

        vm.SkipCommand.Execute(null);
        _signedIn = true;
        AccountChanged();

        Assert.Empty(_subscribers);
        Assert.Equal(1, _saves);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void AStaleHandlerCalledAfterTheScreenClosed_IsIgnored()
    {
        var vm = Create();
        vm.ShowAtStart(false);
        var stale = _subscribers.Single(); // what a posted-but-not-yet-run call would still hold
        var closed = 0;
        vm.Closed += () => closed++;
        vm.SkipCommand.Execute(null);

        _signedIn = true;
        stale();

        Assert.Equal(1, _saves);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void ASignInThatSignsInInsideTheWindow_ClosesTheScreenOnce()
    {
        // AccountService raises Changed inside ShowDialog(): the follow path closes the screen, then ShowDialog returns true.
        var vm = new AccountIntroViewModel(() => _settings, _ => _saves++, () => _signedIn, _ =>
        {
            _signedIn = true;
            AccountChanged();
            return true;
        }, handler =>
        {
            _subscribers.Add(handler);
            return () => _subscribers.Remove(handler);
        });
        vm.ShowAtStart(false);
        var closed = 0;
        vm.Closed += () => closed++;

        vm.SignInCommand.Execute(null);

        Assert.False(vm.IsVisible);
        Assert.Equal(1, _saves);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void AForcedScreenshotScreen_IgnoresTheAccount()
    {
        var vm = Create();
        Assert.True(vm.ShowAtStart(screenshotRun: true, forced: true));
        var closed = 0;
        vm.Closed += () => closed++;

        _signedIn = true;
        AccountChanged();

        Assert.Empty(_subscribers);
        Assert.True(vm.IsVisible);
        Assert.Equal(0, _saves);
        Assert.Equal(0, closed);
        vm.Hide();
        Assert.False(vm.IsVisible);
    }
}
