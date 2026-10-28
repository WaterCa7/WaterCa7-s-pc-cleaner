using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

// Sign-up, the mail code and «Отправить ещё раз».
public sealed partial class SignInViewModel
{
    public const string OfferUrl = "https://waterca7.ru/legal/offer";
    public const string PrivacyUrl = "https://waterca7.ru/legal/privacy";

    private DateTimeOffset _resendAt;

    public AsyncRelayCommand RegisterCommand { get; private set; } = null!;
    public AsyncRelayCommand VerifyCommand { get; private set; } = null!;
    public AsyncRelayCommand ResendCommand { get; private set; } = null!;
    public RelayCommand OpenOfferCommand { get; private set; } = null!;
    public RelayCommand OpenPrivacyCommand { get; private set; } = null!;

    public bool CanResend => _now() >= _resendAt;

    public string ResendText => CanResend
        ? T._("Отправить код ещё раз")
        : T.F("Отправить ещё раз через {0} с", (int)Math.Ceiling((_resendAt - _now()).TotalSeconds));

    /// <summary>The window calls it every second: the countdown of «Отправить ещё раз».</summary>
    public void Tick()
    {
        OnPropertyChanged(nameof(CanResend));
        OnPropertyChanged(nameof(ResendText));
        RelayCommand.Refresh();
    }

    private void InitRegister()
    {
        RegisterCommand = new AsyncRelayCommand(() => RunAsync(RegisterAsync), () => !IsBusy);
        VerifyCommand = new AsyncRelayCommand(() => RunAsync(VerifyAsync), () => !IsBusy);
        ResendCommand = new AsyncRelayCommand(() => RunAsync(ResendAsync), () => !IsBusy && CanResend);
        OpenOfferCommand = new RelayCommand(() => _openUrl(OfferUrl));
        OpenPrivacyCommand = new RelayCommand(() => _openUrl(PrivacyUrl));
    }

    internal Task RegisterForTestAsync() => RegisterAsync();
    internal Task VerifyForTestAsync() => VerifyAsync();
    internal Task ResendForTestAsync() => ResendAsync();

    private async Task RegisterAsync()
    {
        if (!CheckEmail() || !CheckPassword(Password) || !CheckSame())
            return;
        if (!Consent)
        {
            Error = T._("Нужно согласие с офертой и политикой конфиденциальности.");
            return;
        }
        await _account.RegisterAsync(Email.Trim(), Password, T.Language, CancellationToken.None);
        StartResendCountdown(60);
        GoTo(SignInStep.VerifyCode);
        Info = T._("Мы отправили код на почту, если адрес верен. Не пришло — проверьте «Спам».");
    }

    private async Task VerifyAsync()
    {
        if (!CheckCode())
            return;
        var (email, password) = (Email.Trim(), Password);
        // DEVICE_LIMIT after verify: the code is spent and the e-mail confirmed — after a release, log in with the password.
        _retry = () => _account.LoginAsync(email, password, CancellationToken.None);
        await _account.VerifyAsync(email, Code, CancellationToken.None);
        Done();
    }

    private async Task ResendAsync()
    {
        var purpose = Step == SignInStep.ResetCode ? "reset" : "verify";
        StartResendCountdown(await _account.ResendAsync(Email.Trim(), purpose, CancellationToken.None));
        Info = T._("Мы отправили новый код, если адрес верен. Не пришло — проверьте «Спам».");
    }

    /// <summary>The login of an unverified e-mail: the code step, with a new code sent at once.</summary>
    private async Task GoVerifyAsync()
    {
        var (email, password) = (Email.Trim(), Password);
        _retry = () => _account.LoginAsync(email, password, CancellationToken.None);
        GoTo(SignInStep.VerifyCode);
        Info = T._("Почта ещё не подтверждена. Мы отправили код, если адрес верен.");
        try
        {
            StartResendCountdown(await _account.ResendAsync(email, "verify", CancellationToken.None));
        }
        catch (Core.Account.ApiException ex)
        {
            Error = SignInErrors.Text(ex.Error);
        }
    }

    private void StartResendCountdown(int seconds)
    {
        _resendAt = _now() + TimeSpan.FromSeconds(Math.Max(seconds, 1));
        Tick();
    }
}
