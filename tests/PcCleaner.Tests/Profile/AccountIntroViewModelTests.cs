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

    private AccountIntroViewModel Create() => new(() => _settings, _ => _saves++, () => _signedIn, tab =>
    {
        _windows.Add(tab);
        return _windowResult;
    });

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
    public void AWindowClosedWhileItsRequestStillSignedIn_ClosesTheScreenToo()
    {
        var vm = Create();
        vm.ShowAtStart(false);
        var closed = 0;
        vm.Closed += () => closed++;

        // The window was closed (ShowDialog false) while the password request was on its way; it signed in all the same.
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
}
