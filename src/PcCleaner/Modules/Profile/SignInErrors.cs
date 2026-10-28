using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

/// <summary>What the sign-in window says for an API error (spec 3.2), and the checks the site's form makes too.</summary>
public static class SignInErrors
{
    public static string Text(ApiError error) => error.Code switch
    {
        ApiError.AuthFailed => T._("Неверная почта или пароль. Аккаунт создан через Google? Войдите через браузер."),
        ApiError.EmailTaken => T._("Аккаунт с этой почтой уже есть — войдите в него."),
        ApiError.CodeInvalid => T._("Неверный или устаревший код. Проверьте письмо или отправьте код ещё раз."),
        ApiError.WeakPassword => T._("Пароль должен быть от 8 до 128 символов."),
        ApiError.EmailInvalid or ApiError.InvalidInput => T._("Проверьте адрес почты."),
        ApiError.ConsentRequired => T._("Нужно согласие с офертой и политикой конфиденциальности."),
        ApiError.RateLimited => T._("Слишком много попыток. Подождите несколько минут и попробуйте снова."),
        ApiError.UnbindQuota => error.Message.Length > 0 ? error.Message : T._("Отвязать компьютер можно раз в 7 дней."),
        ApiError.Network => T._("Нет связи с waterca7.ru. Проверьте интернет."),
        ApiError.ServerError => T._("Сервер не ответил. Попробуйте позже."),
        ApiError.NotFound => T._("Код устарел. Начните вход заново."),
        _ => error.Message.Length > 0 ? error.Message : T.F("Не получилось ({0}). Попробуйте ещё раз.", error.Code),
    };

    /// <summary>
    /// What the list of computers says when «Отвязать» was refused. A fresh list is on the screen already, so a spent permit
    /// (AUTH_FAILED / NOT_FOUND of /device/release) is not «wrong password» or «code out of date»; the server's own words
    /// for the quota stay.
    /// </summary>
    public static string ReleaseText(ApiError error) => error.Code is ApiError.AuthFailed or ApiError.NotFound
        ? T._("Разрешение на отвязку устарело — список обновлён, выберите компьютер ещё раз.")
        : Text(error);

    /// <summary>The form's check: up to 254 characters, an «@» and a dot after it with something on both sides.</summary>
    public static bool IsValidEmail(string? email)
    {
        var e = (email ?? "").Trim();
        var at = e.IndexOf('@');
        if (at < 0 || at + 2 > e.Length)
            return false; // no «@», or nothing after it
        var dot = e.IndexOf('.', at + 2);
        return e.Length is > 0 and <= 254 && at > 0 && dot > at + 1 && dot < e.Length - 1 && !e.Contains(' ');
    }

    /// <summary>8–128 characters, spaces count (the site does not trim passwords).</summary>
    public static bool IsValidPassword(string? password) => password is { Length: >= 8 and <= 128 };
}
