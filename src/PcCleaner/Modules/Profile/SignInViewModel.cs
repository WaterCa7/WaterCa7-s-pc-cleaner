using System.Collections.ObjectModel;
using PcCleaner.Core;
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

/// <summary>The tab of the sign-in window that opens first: the password form, the sign-up form or the sign-in through the browser.</summary>
public enum SignInTab
{
    Password,
    Register,
    Browser,
}

public enum SignInStep
{
    Password,
    ResetEmail,
    ResetCode,
    Register,
    VerifyCode,
    Browser,
    DeviceLimit,
}

/// <summary>A computer of the account in the DEVICE_LIMIT list.</summary>
public sealed record LimitDevice(long Id, string Name, string Details);

/// <summary>
/// The sign-in window (spec 3.2): password, sign-up with the mail code, password reset, the browser code, and the list of the
/// account's computers when DEVICE_LIMIT stops a sign-in. Everything happens here, no browser except for the browser code
/// and the documents. <see cref="Completed"/> fires once a sign-in succeeded; the window then closes.
/// </summary>
public sealed partial class SignInViewModel : ObservableObject
{
    private readonly AccountService _account;
    private readonly Action<string> _openUrl;
    private readonly Func<DateTimeOffset> _now;
    private SignInStep _step;
    private SignInTab _tab;
    private string _email = "";
    private string _password = "";
    private string _password2 = "";
    private string _code = "";
    private bool _consent;
    private bool _isBusy;
    private string? _error;
    private string? _info;
    private string? _limitText;
    private DeviceLimitInfo? _limit;
    private Func<Task>? _retry;

    public SignInViewModel(AccountService account, Action<string> openUrl, SignInTab tab = SignInTab.Password, Func<DateTimeOffset>? now = null)
    {
        _account = account;
        _openUrl = openUrl;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        SignInCommand = new AsyncRelayCommand(() => RunAsync(SignInAsync), () => !IsBusy);
        ForgotCommand = new RelayCommand(() => GoTo(SignInStep.ResetEmail));
        SendResetCommand = new AsyncRelayCommand(() => RunAsync(SendResetAsync), () => !IsBusy);
        ConfirmResetCommand = new AsyncRelayCommand(() => RunAsync(ConfirmResetAsync), () => !IsBusy);
        BackCommand = new RelayCommand(Back);
        InitRegister();
        InitBrowser();
        Tab = tab;
    }

    public event Action? Completed;

    public SignInStep Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, value))
                return;
            foreach (var name in new[] { nameof(IsPasswordStep), nameof(IsResetEmailStep), nameof(IsResetCodeStep), nameof(IsRegisterStep),
                         nameof(IsVerifyStep), nameof(IsBrowserStep), nameof(IsDeviceLimitStep), nameof(ShowTabs) })
                OnPropertyChanged(name);
        }
    }

    /// <summary>The three tabs at the top; choosing one starts its step (the browser tab asks for a code at once).</summary>
    public SignInTab Tab
    {
        get => _tab;
        set
        {
            Set(ref _tab, value);
            Error = null;
            Info = null;
            switch (value)
            {
                case SignInTab.Password:
                    GoTo(SignInStep.Password);
                    break;
                case SignInTab.Register:
                    GoTo(SignInStep.Register);
                    break;
                case SignInTab.Browser:
                    GoTo(SignInStep.Browser);
                    break;
            }
        }
    }

    public bool IsPasswordStep => Step == SignInStep.Password;
    public bool IsResetEmailStep => Step == SignInStep.ResetEmail;
    public bool IsResetCodeStep => Step == SignInStep.ResetCode;
    public bool IsRegisterStep => Step == SignInStep.Register;
    public bool IsVerifyStep => Step == SignInStep.VerifyCode;
    public bool IsBrowserStep => Step == SignInStep.Browser;
    public bool IsDeviceLimitStep => Step == SignInStep.DeviceLimit;

    /// <summary>The tabs are shown on the first step of each tab only.</summary>
    public bool ShowTabs => Step is SignInStep.Password or SignInStep.Register or SignInStep.Browser;

    public string Email { get => _email; set => Set(ref _email, value); }

    public string Password { get => _password; set => Set(ref _password, value); }

    public string Password2 { get => _password2; set => Set(ref _password2, value); }

    public string Code { get => _code; set => Set(ref _code, value); }

    public bool Consent { get => _consent; set => Set(ref _consent, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                RelayCommand.Refresh();
        }
    }

    public string? Error { get => _error; private set => Set(ref _error, value); }

    public string? Info { get => _info; private set => Set(ref _info, value); }

    /// <summary>The server's words about the limit (the plain limit, the family owner, a family member).</summary>
    public string? LimitText { get => _limitText; private set => Set(ref _limitText, value); }

    public ObservableCollection<LimitDevice> Devices { get; } = new();

    public AsyncRelayCommand SignInCommand { get; }
    public RelayCommand ForgotCommand { get; }
    public AsyncRelayCommand SendResetCommand { get; }
    public AsyncRelayCommand ConfirmResetCommand { get; }
    public RelayCommand BackCommand { get; }

    // ----- password -----

    private async Task SignInAsync()
    {
        if (!CheckEmail() || !CheckPassword(Password))
            return;
        var (email, password) = (Email.Trim(), Password);
        _retry = () => _account.LoginAsync(email, password, CancellationToken.None);
        await _retry();
        Done();
    }

    // ----- reset -----

    private async Task SendResetAsync()
    {
        if (!CheckEmail())
            return;
        await _account.ResetAsync(Email.Trim(), CancellationToken.None);
        StartResendCountdown(60);
        GoTo(SignInStep.ResetCode);
        Info = T._("Мы отправили код на почту, если адрес верен. Не пришло — проверьте «Спам».");
    }

    private async Task ConfirmResetAsync()
    {
        if (!CheckCode() || !CheckPassword(Password) || !CheckSame())
            return;
        var (email, password) = (Email.Trim(), Password);
        // DEVICE_LIMIT after reset/confirm: the code is spent, the password changed — after a release, log in with the new password.
        _retry = () => _account.LoginAsync(email, password, CancellationToken.None);
        await _account.ResetConfirmAsync(email, Code, password, CancellationToken.None);
        Done();
    }

    // ----- DEVICE_LIMIT -----

    /// <summary>«Отвязать» in the list: releases the computer with the last permit, then repeats the sign-in the right way.</summary>
    public async Task ReleaseAsync(LimitDevice device)
    {
        if (_limit is null || IsBusy)
            return;
        await RunAsync(async () =>
        {
            try
            {
                await _account.ReleaseAsync(_limit.UnbindToken, device.Id, CancellationToken.None);
            }
            catch (ApiException ex) when (ex.Error.Code is ApiError.NotFound or ApiError.UnbindQuota or ApiError.AuthFailed)
            {
                // The permit is spent: show why, and ask again for a fresh DEVICE_LIMIT (a new list and permit).
                var reason = SignInErrors.Text(ex.Error);
                if (_retry is not null)
                {
                    try
                    {
                        await _retry();
                        Done();
                        return;
                    }
                    catch (ApiException again) when (again.Error.Code == ApiError.DeviceLimit && again.Error.AsDeviceLimit() is { } limit)
                    {
                        ShowLimit(limit, again.Error.Message);
                    }
                }
                Error = reason;
                return;
            }
            if (_retry is not null)
                await _retry();
            Done();
        });
    }

    private void ShowLimit(DeviceLimitInfo limit, string message)
    {
        _limit = limit;
        LimitText = message.Length > 0 ? message : T.F("Заняты все компьютеры аккаунта ({0}). Отвяжите один, чтобы войти на этом.", limit.BindLimit);
        Devices.Clear();
        foreach (var d in limit.Devices)
            Devices.Add(new LimitDevice(d.Id, d.Name, ProfileTexts.DeviceDetails(d, _now())));
        GoTo(SignInStep.DeviceLimit);
    }

    // ----- common -----

    /// <summary>Runs one action of the window: busy while it runs, errors become <see cref="Error"/>, DEVICE_LIMIT and an unverified e-mail open their steps.</summary>
    internal async Task RunAsync(Func<Task> action)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Error = null;
        try
        {
            await action();
        }
        catch (ApiException ex) when (ex.Error.Code == ApiError.DeviceLimit && ex.Error.AsDeviceLimit() is { } limit)
        {
            ShowLimit(limit, ex.Error.Message);
        }
        catch (ApiException ex) when (ex.Error.Code == ApiError.EmailNotVerified)
        {
            await GoVerifyAsync();
        }
        catch (ApiException ex)
        {
            Error = SignInErrors.Text(ex.Error);
            if (ex.Error.Code == ApiError.EmailTaken)
                GoTo(SignInStep.Password);
        }
        catch (OperationCanceledException)
        {
            // the user cancelled
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Done()
    {
        if (!_account.IsSignedIn)
            return;
        Password = Password2 = Code = "";
        Completed?.Invoke();
    }

    private void GoTo(SignInStep step)
    {
        if (step != SignInStep.Browser)
            StopBrowser();
        Step = step;
        if (step == SignInStep.Browser)
            StartBrowser();
    }

    private void Back()
    {
        Error = null;
        Info = null;
        GoTo(Step switch
        {
            SignInStep.ResetEmail or SignInStep.ResetCode => SignInStep.Password,
            SignInStep.VerifyCode => _tab == SignInTab.Register ? SignInStep.Register : SignInStep.Password,
            SignInStep.DeviceLimit => _tab switch
            {
                SignInTab.Register => SignInStep.Register,
                SignInTab.Browser => SignInStep.Browser,
                _ => SignInStep.Password,
            },
            _ => Step,
        });
        CancelDeviceLimitWait();
    }

    private bool CheckEmail()
    {
        if (SignInErrors.IsValidEmail(Email))
            return true;
        Error = T._("Проверьте адрес почты.");
        return false;
    }

    private bool CheckPassword(string password)
    {
        if (SignInErrors.IsValidPassword(password))
            return true;
        Error = T._("Пароль должен быть от 8 до 128 символов.");
        return false;
    }

    private bool CheckSame()
    {
        if (Password == Password2)
            return true;
        Error = T._("Пароли не совпадают.");
        return false;
    }

    private bool CheckCode()
    {
        Code = new string(Code.Where(char.IsDigit).ToArray());
        if (Code.Length == 6)
            return true;
        Error = T._("Введите 6 цифр из письма.");
        return false;
    }
}
