using PcCleaner.Core;
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

/// <summary>
/// «Профиль» (spec 3.3): a guest gets «Войти», «Зарегистрироваться» and what Pro gives; a signed-in user — the e-mail, the plan,
/// the term, the last check, the computers, the promo code, «Написать нам» and «Выйти». Follows <see cref="AccountService.Changed"/>
/// (the page lives as long as the app, so the subscription is never removed; the handler only re-reads the account and is harmless to repeat).
/// </summary>
public sealed partial class ProfileViewModel : PageViewModel
{
    public const string BuyUrl = "https://waterca7.ru/pricing";

    private readonly AccountService _account;
    private readonly Func<SignInTab, bool> _signIn;
    private readonly Action<string> _openUrl;
    private readonly Func<string, string, bool> _confirm;
    private readonly Func<DateTimeOffset> _now;
    private string? _refreshText;

    // The ticket time when «Обновить» failed: a newer ticket (the background check got through) makes that text stale.
    private DateTimeOffset? _refreshFailedAt;

    /// <summary>The real page: the app's account, the sign-in window, the browser, the app's questions.</summary>
    public ProfileViewModel()
        : this(AppServices.Account, SignInDialog.Show, url => ProcessRunner.ShellOpen(url), Dialogs.Confirm)
    {
    }

    public ProfileViewModel(AccountService account, Func<SignInTab, bool> signIn, Action<string> openUrl, Func<string, string, bool> confirm,
        Func<DateTimeOffset>? now = null)
    {
        _account = account;
        _signIn = signIn;
        _openUrl = openUrl;
        _confirm = confirm;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        SignInCommand = new RelayCommand(() => _signIn(SignInTab.Password));
        RegisterCommand = new RelayCommand(() => _signIn(SignInTab.Register));
        BuyCommand = new RelayCommand(() => _openUrl(BuyUrl));
        RefreshCommand = new AsyncRelayCommand(() => RunBusyAsync(T._("Проверяем лицензию…"), RefreshAsync), () => !IsBusy && IsSignedIn);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync, () => !IsBusy && IsSignedIn);
        InitDevices();
        InitPromoSupport();
        _account.Changed += () => Ui.Post(Refresh);
    }

    public override string Title => T._("Профиль");

    public override string Subtitle => T._("Аккаунт WaterCa7, Pro и ваши компьютеры");

    public override string IconGlyph => "\uE77B";

    public bool IsSignedIn => _account.IsSignedIn;

    public bool IsGuest => !IsSignedIn;

    public string? Email => _account.Email;

    /// <summary>
    /// The plan that works here: a Pro ticket that does not apply now (stale, expired, another computer) is «Free» next to «Купить
    /// Pro»; <see cref="NoteText"/> says why.
    /// </summary>
    public string PlanText => _account.Status is { Tier: Tier.Pro } s ? ProfileTexts.PlanName(s.Plan) : ProfileTexts.PlanName(null);

    public string? TermText => ProfileTexts.Term(_account.Status);

    public string? CheckedText => ProfileTexts.Checked(_account.Status, _now());

    public string? NoteText => ProfileTexts.Note(_account.Status, _now());

    public string? SessionEndedText => _account.SessionEnded && !IsSignedIn
        ? T._("Вход больше не действует — компьютер отвязан или пароль сменён. Войдите снова.")
        : null;

    /// <summary>What «Обновить» says when the server could not be reached (the ticket in hand stays); null otherwise.</summary>
    public string? RefreshText
    {
        get => _refreshText;
        private set => Set(ref _refreshText, value);
    }

    /// <summary>«Что даёт Pro» is shown to everyone who has no Pro yet.</summary>
    public bool ShowProLines => _account.Tier != Tier.Pro;

    public string BuyText => _account.Tier == Tier.Pro ? T._("Продлить") : T._("Купить Pro");

    /// <summary>«Что даёт Pro» for a guest and a free account.</summary>
    public IReadOnlyList<string> ProLines { get; } = new[]
    {
        T._("Полная проверка и глубокая очистка: кэши игр, обновлений, дампы, DISM"),
        T._("Ускорение компьютера и проверка сети"),
        T._("Глубокие проверки процессов, автозагрузки, программ, дисков и защиты"),
    };

    public RelayCommand SignInCommand { get; }
    public RelayCommand RegisterCommand { get; }
    public RelayCommand BuyCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand LogoutCommand { get; }

    public override Task OnNavigatedToAsync() => IsSignedIn ? LoadDevicesAsync() : Task.CompletedTask;

    internal Task RefreshForTestAsync() => RefreshAsync(CancellationToken.None);

    internal Task LogoutForTestAsync() => LogoutAsync();

    private async Task RefreshAsync(CancellationToken ct)
    {
        var ok = await _account.RefreshTicketAsync(ct);
        // A revoked session is explained by SessionEndedText; a failure with a session still alive says so here.
        _refreshFailedAt = _account.Status.IssuedAt;
        RefreshText = ok || !IsSignedIn ? null : T._("Не получилось обновить лицензию — действует прежняя. Проверьте интернет и попробуйте ещё раз.");
        Refresh();
    }

    private async Task LogoutAsync()
    {
        if (!_confirm(T._("Выход из аккаунта"), T._("Выйти из аккаунта на этом компьютере? Настройки останутся, программа перейдёт в бесплатный режим.")))
            return;
        await _account.LogoutAsync(CancellationToken.None);
        Refresh();
    }

    /// <summary>Re-reads everything from the account (after a sign-in, a refresh, a purchase, a sign-out). Safe to repeat.</summary>
    private void Refresh()
    {
        if (!IsSignedIn || (RefreshText is not null && _account.Status.IssuedAt != _refreshFailedAt))
            RefreshText = null;
        foreach (var name in new[] { nameof(IsSignedIn), nameof(IsGuest), nameof(Email), nameof(PlanText), nameof(TermText), nameof(CheckedText),
                     nameof(NoteText), nameof(SessionEndedText), nameof(BuyText), nameof(ShowProLines), nameof(CanApplyPromo), nameof(NeedsSupportEmail) })
            OnPropertyChanged(name);
        RelayCommand.Refresh();
        SyncDevicesWithAccount();
        SyncPromoWithAccount();
    }
}
