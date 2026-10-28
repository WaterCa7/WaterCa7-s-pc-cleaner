using System.Text;
using System.Text.Json;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core.Account;

/// <summary>
/// An answer of waterca7.ru that is not a success: the server's <c>error</c> code (branch on it, not on the HTTP status), its
/// <c>message</c> in the request's language (it may be shown as is) and the whole JSON for the extra fields. Two codes are the
/// client's own: <see cref="Network"/> (no answer) and <see cref="ServerError"/> (an answer that is not the API's JSON).
/// </summary>
public sealed record ApiError(string Code, string Message, int HttpStatus, JsonElement? Payload)
{
    public const string Network = "NETWORK";
    public const string ServerError = "SERVER_ERROR";
    public const string NotFound = "NOT_FOUND";
    public const string InvalidInput = "INVALID_INPUT";
    public const string AuthFailed = "AUTH_FAILED";
    public const string TokenRevoked = "TOKEN_REVOKED";
    public const string EmailNotVerified = "EMAIL_NOT_VERIFIED";
    public const string DeviceLimit = "DEVICE_LIMIT";
    public const string EmailTaken = "EMAIL_TAKEN";
    public const string EmailInvalid = "EMAIL_INVALID";
    public const string WeakPassword = "WEAK_PASSWORD";
    public const string ConsentRequired = "CONSENT_REQUIRED";
    public const string CodeInvalid = "CODE_INVALID";
    public const string RateLimited = "RATE_LIMITED";
    public const string UnbindQuota = "UNBIND_QUOTA";
    public const string PromoInvalid = "PROMO_INVALID";
    public const string Blocked = "BLOCKED";
    public const string PaymentsDisabled = "PAYMENTS_DISABLED";
    public const string PayMany = "PAY_MANY";
    public const string PaymentNotPending = "PAYMENT_NOT_PENDING";

    public static ApiError NetworkError() => new(Network, "", 0, null);

    /// <summary>
    /// The text for the user: the server's <see cref="Message"/> when it has one, otherwise the program's own for the code (no
    /// connection, the server did not answer, not signed in, anything else).
    /// </summary>
    public string UserText => Message is { Length: > 0 } ? Message : Code switch
    {
        Network => T._("Нет связи с сервером. Проверьте интернет и попробуйте ещё раз."),
        ServerError => T._("Сервер не ответил. Попробуйте позже."),
        TokenRevoked => T._("Нужно войти снова."),
        _ => T._("Что-то пошло не так. Попробуйте ещё раз."),
    };

    /// <summary>The <c>reason</c> field as text: 1–7 of <see cref="PromoInvalid"/>, "no_change" / "change_more" of <see cref="Blocked"/>.</summary>
    public string? Reason => Text("reason");

    /// <summary>The <c>status</c> field of <see cref="PaymentNotPending"/>: "paid" or "failed".</summary>
    public string? Status => Text("status");

    /// <summary>The computers and the one-time release permit of a <see cref="DeviceLimit"/> answer; null for any other answer.</summary>
    public DeviceLimitInfo? AsDeviceLimit()
    {
        if (Code != DeviceLimit || Payload is not { ValueKind: JsonValueKind.Object } p)
            return null;
        try
        {
            return p.Deserialize<DeviceLimitInfo>(ApiClient.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The generated ToString must not put the whole JSON into a log (DEVICE_LIMIT carries the unbind permit).
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Code = ").Append(Code);
        builder.Append(", Message = ").Append(Message);
        builder.Append(", HttpStatus = ").Append(HttpStatus);
        builder.Append(", Payload = ").Append(Payload is null ? "<none>" : "<json>");
        return true;
    }

    private string? Text(string name)
    {
        if (Payload is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }
}

/// <summary>A failed API call (<see cref="Error"/>). Cancellation by the caller is never wrapped: it stays an OperationCanceledException.</summary>
public sealed class ApiException : Exception
{
    public ApiException(ApiError error, Exception? inner = null)
        : base(error.Code, inner)
    {
        Error = error;
    }

    public ApiError Error { get; }
}
