using PcCleaner.Core;

namespace PcCleaner.Modules.Profile;

/// <summary>
/// The start screen over the blurred window, after the splash and before the mode/language screen: «Войти»,
/// «Зарегистрироваться», «Продолжить без входа» (spec 3.1). Once per install: a choice, a sign-in or a signed-in user
/// sets <see cref="AppSettings.AccountIntroShown"/>. For an updated install it adds «Появились аккаунты и Pro».
/// While it is shown it follows the account: when the account signs in by any path (also after the sign-in window was
/// closed with a request still on its way) the screen closes by itself.
/// </summary>
public sealed class AccountIntroViewModel : ObservableObject
{
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _save;
    private readonly Func<bool> _isSignedIn;
    private readonly Func<SignInTab, bool> _signIn;
    private readonly Func<Action, Action>? _follow;
    private Action? _unfollow;
    private bool _isVisible;
    private bool _isUpdate;

    /// <param name="follow">
    /// Subscribes the given handler to "the account changed" (the caller marshals it to the UI thread) and returns the
    /// function that unsubscribes it; null — the screen does not follow the account.
    /// </param>
    public AccountIntroViewModel(Func<AppSettings> settings, Action<AppSettings> save, Func<bool> isSignedIn, Func<SignInTab, bool> signIn,
        Func<Action, Action>? follow = null)
    {
        _settings = settings;
        _save = save;
        _isSignedIn = isSignedIn;
        _signIn = signIn;
        _follow = follow;
        SignInCommand = new RelayCommand(() => SignIn(SignInTab.Password));
        RegisterCommand = new RelayCommand(() => SignIn(SignInTab.Register));
        SkipCommand = new RelayCommand(Close);
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => Set(ref _isVisible, value);
    }

    /// <summary>The mode was chosen before: this is an install that was updated from a version without accounts.</summary>
    public bool IsUpdate
    {
        get => _isUpdate;
        private set => Set(ref _isUpdate, value);
    }

    public event Action? Closed;

    public RelayCommand SignInCommand { get; }

    public RelayCommand RegisterCommand { get; }

    public RelayCommand SkipCommand { get; }

    /// <summary>Shows the screen when it is due (see the class); returns whether it is shown now.</summary>
    public bool ShowAtStart(bool screenshotRun, bool forced = false)
    {
        var settings = _settings();
        if (screenshotRun)
        {
            if (forced)
                (IsUpdate, IsVisible) = (settings.UiMode is not null, true);
            return IsVisible;
        }
        if (settings.AccountIntroShown)
            return false;
        if (_isSignedIn())
        {
            settings.AccountIntroShown = true;
            _save(settings);
            return false;
        }
        IsUpdate = settings.UiMode is not null;
        IsVisible = true;
        // Only a real start follows the account; the forced screen of a screenshot run never closes by itself.
        _unfollow ??= _follow?.Invoke(OnAccountChanged);
        return true;
    }

    /// <summary>Closes the screen without a choice (the screenshot run, after its picture).</summary>
    public void Hide()
    {
        Unfollow();
        IsVisible = false;
    }

    private void SignIn(SignInTab tab)
    {
        // The window's own answer, or an account that is already signed in when it returns. A request that is still on its
        // way when the window is closed signs in later: that is caught by OnAccountChanged, not here.
        if (_signIn(tab) || _isSignedIn())
            Close();
    }

    private void OnAccountChanged()
    {
        if (IsVisible && _isSignedIn())
            Close();
    }

    private void Unfollow()
    {
        var unfollow = _unfollow;
        _unfollow = null;
        unfollow?.Invoke();
    }

    private void Close()
    {
        Unfollow();
        var settings = _settings();
        settings.AccountIntroShown = true;
        _save(settings);
        IsVisible = false;
        Closed?.Invoke();
    }
}
