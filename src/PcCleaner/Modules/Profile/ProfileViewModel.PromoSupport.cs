using PcCleaner.Core;
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

// The promo code and «Написать нам».
public sealed partial class ProfileViewModel
{
    private string _promoCode = "";
    private string? _promoText;
    private bool _promoIsDiscount;
    private string _supportTopic = "question";
    private string _supportText = "";
    private string _supportEmail = "";
    private bool _supportAttach = true;
    private string? _supportResult;

    public string PromoCode { get => _promoCode; set => Set(ref _promoCode, value); }

    /// <summary>What the last «Применить» said: the plan and term it gave, or why the code was refused.</summary>
    public string? PromoText { get => _promoText; private set => Set(ref _promoText, value); }

    /// <summary>The code is a discount (reason 7): «Открыть покупку» is offered.</summary>
    public bool PromoIsDiscount { get => _promoIsDiscount; private set => Set(ref _promoIsDiscount, value); }

    public bool CanApplyPromo => IsSignedIn;

    /// <summary>question | bug | other.</summary>
    public string SupportTopic { get => _supportTopic; set => Set(ref _supportTopic, value); }
    public string SupportText { get => _supportText; set => Set(ref _supportText, value); }
    public string SupportEmail { get => _supportEmail; set => Set(ref _supportEmail, value); }
    public bool SupportAttach { get => _supportAttach; set => Set(ref _supportAttach, value); }
    public string? SupportResult { get => _supportResult; private set => Set(ref _supportResult, value); }

    /// <summary>A guest must say where to answer; a signed-in user is answered at the account's e-mail.</summary>
    public bool NeedsSupportEmail => !IsSignedIn;

    public AsyncRelayCommand ApplyPromoCommand { get; private set; } = null!;

    /// <summary>B2: the page of prices; the purchase window with this code replaces it later.</summary>
    public RelayCommand OpenBuyWithPromoCommand { get; private set; } = null!;

    public AsyncRelayCommand SendSupportCommand { get; private set; } = null!;

    private void InitPromoSupport()
    {
        ApplyPromoCommand = new AsyncRelayCommand(ApplyPromoAsync, () => !IsBusy && CanApplyPromo);
        OpenBuyWithPromoCommand = new RelayCommand(() => _openUrl(BuyUrl));
        SendSupportCommand = new AsyncRelayCommand(SendSupportAsync, () => !IsBusy);
    }

    /// <summary>Called by <see cref="Refresh"/>: what was said about a code does not outlive the sign-in it was said for.</summary>
    private void SyncPromoWithAccount()
    {
        if (IsSignedIn)
            return;
        PromoText = null;
        PromoIsDiscount = false;
    }

    internal Task ApplyPromoForTestAsync() => ApplyPromoAsync();
    internal Task SendSupportForTestAsync() => SendSupportAsync();

    private async Task ApplyPromoAsync()
    {
        var code = PromoCode.Trim();
        if (code.Length == 0 || !IsSignedIn)
            return;
        var owner = Email;
        PromoIsDiscount = false;
        string text;
        var discount = false;
        var consumed = false;
        try
        {
            var accepted = await _account.ApplyPromoAsync(code, CancellationToken.None);
            consumed = true;
            // «Pro is on» is decided by the tier. The server took the code, but its ticket may not fit this sign-in: say so, not «Pro».
            text = accepted && _account.Tier == Tier.Pro
                ? T.F("Промокод применён: {0} {1}", PlanText, TermText ?? "").Trim()
                : T._("Промокод применён. Лицензия обновится после проверки — нажмите «Обновить».");
        }
        catch (ApiException ex) when (ex.Error.Code == ApiError.PromoInvalid)
        {
            discount = ex.Error.Reason == "7";
            text = ex.Error.Reason switch
            {
                "2" => T._("Срок промокода истёк"),
                "3" => T._("Промокод уже использован максимальное число раз"),
                "4" => T._("Вы уже использовали этот промокод"),
                "5" => T._("На этом компьютере промокод уже применяли"),
                "6" => T._("Сначала войдите в аккаунт в программе на этом компьютере"),
                "7" => T._("Это код скидки — введите его в окне покупки"),
                _ => T._("Такого промокода нет"),
            };
        }
        catch (ApiException ex)
        {
            text = ex.Error.Code == ApiError.RateLimited ? T._("Слишком много попыток. Попробуйте через час.") : SignInErrors.Text(ex.Error);
        }
        // The user may have signed out (or in as another account) while the server was answering: the answer is not theirs to see.
        if (IsSignedIn && Email == owner)
        {
            PromoText = text;
            PromoIsDiscount = discount;
            if (consumed)
                PromoCode = "";
        }
        Refresh();
    }

    private async Task SendSupportAsync()
    {
        var text = SupportText.Trim();
        if (text.Length is < 10 or > 5000)
        {
            SupportResult = T._("Опишите вопрос: от 10 до 5000 символов.");
            return;
        }
        if (NeedsSupportEmail && !SignInErrors.IsValidEmail(SupportEmail))
        {
            SupportResult = T._("Укажите почту — ответ придёт на неё.");
            return;
        }
        try
        {
            await _account.SendSupportAsync(NeedsSupportEmail ? SupportEmail.Trim() : null, SupportTopic, text,
                SupportAttach ? SupportAttachment.Build() : null, CancellationToken.None);
            SupportResult = T._("Отправлено. Ответ придёт на почту.");
            SupportText = "";
        }
        catch (ApiException ex)
        {
            SupportResult = ex.Error.Code == ApiError.RateLimited
                ? T._("Слишком много обращений. Попробуйте через час.")
                : SignInErrors.Text(ex.Error);
        }
    }
}
