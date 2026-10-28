using System.Text;
using System.Text.Json.Serialization;

namespace PcCleaner.Core.Account;

/// <summary>The fields of this computer every sign-in sends: <c>hw_hash, platform, name, app_version</c>.</summary>
public sealed record DeviceFields(string HwHash, string Platform, string Name, string AppVersion)
{
    /// <summary>The computer's name as the server takes it: control characters removed, trimmed, at most 64 characters.</summary>
    public static string CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= 64 ? clean : clean[..64];
    }

    // The generated ToString must not put hw_hash into a log.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("HwHash = ").Append(Secret.Mark(HwHash));
        builder.Append(", Platform = ").Append(Platform);
        builder.Append(", Name = ").Append(Name);
        builder.Append(", AppVersion = ").Append(AppVersion);
        return true;
    }
}

/// <summary>How the records with secrets print them: only set / unset.</summary>
internal static class Secret
{
    public static string Mark(string? secret) => string.IsNullOrEmpty(secret) ? "<none>" : "<set>";
}

/// <summary>A license ticket as the server sends it: base64 of the JSON and base64 of its DER signature.</summary>
public sealed record SignedTicket(
    [property: JsonPropertyName("ticket")] string Ticket,
    [property: JsonPropertyName("signature")] string Signature)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Ticket = ").Append(Secret.Mark(Ticket));
        builder.Append(", Signature = ").Append(Secret.Mark(Signature));
        return true;
    }
}

public sealed record AccountInfo(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("verified")] bool Verified);

public sealed record DeviceRef(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name);

/// <summary>The answer of every successful sign-in: login, verify, reset/confirm, the browser code.</summary>
public sealed record LoginResult(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("account")] AccountInfo Account,
    [property: JsonPropertyName("device")] DeviceRef Device,
    [property: JsonPropertyName("license")] SignedTicket License)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Token = ").Append(Secret.Mark(Token));
        builder.Append(", Account = ").Append(Account);
        builder.Append(", Device = ").Append(Device);
        builder.Append(", License = ").Append(License);
        return true;
    }
}

/// <summary>The code of a sign-in through the browser: open <see cref="Url"/>, show <see cref="Code"/> large, poll until <see cref="ExpiresIn"/>.</summary>
public sealed record BrowserCode(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

public sealed record DeviceEntry(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("bound_at")] DateTimeOffset BoundAt,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset? LastSeenAt,
    [property: JsonPropertyName("current")] bool Current,
    [property: JsonPropertyName("whose")] string? Whose = null,
    [property: JsonPropertyName("member")] bool? Member = null);

public sealed record DeviceList(
    [property: JsonPropertyName("devices")] IReadOnlyList<DeviceEntry> Devices,
    [property: JsonPropertyName("bind_limit")] int BindLimit);

/// <summary>The extra fields of DEVICE_LIMIT: the account's computers and a one-time permit for <c>POST /device/release</c> (10 minutes).</summary>
public sealed record DeviceLimitInfo(
    [property: JsonPropertyName("devices")] IReadOnlyList<DeviceEntry> Devices,
    [property: JsonPropertyName("bind_limit")] int BindLimit,
    [property: JsonPropertyName("unbind_token")] string UnbindToken)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Devices = ").Append(Devices?.Count).Append(" computers");
        builder.Append(", BindLimit = ").Append(BindLimit);
        builder.Append(", UnbindToken = ").Append(Secret.Mark(UnbindToken));
        return true;
    }
}

public sealed record ResendResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("retry_after")] int RetryAfter);

public sealed record PriceTerms(
    [property: JsonPropertyName("month")] int Month,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("lifetime")] int Lifetime);

public sealed record FamilyPrices(
    [property: JsonPropertyName("min")] int Min,
    [property: JsonPropertyName("max")] int Max,
    [property: JsonPropertyName("default")] int Default,
    [property: JsonPropertyName("table")] IReadOnlyDictionary<string, PriceTerms> Table);

public sealed record PriceList(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("pro")] PriceTerms Pro,
    [property: JsonPropertyName("family")] FamilyPrices Family,
    [property: JsonPropertyName("payments_enabled")] bool PaymentsEnabled);

/// <summary>What the purchase window asks about: plan "pro" | "family", term "month" | "year" | "lifetime".</summary>
public sealed record QuoteRequest(string Plan, string Term, int Devices, string? Promo = null, bool Change = false);

public sealed record Quote(
    [property: JsonPropertyName("list")] int List,
    [property: JsonPropertyName("discount")] int Discount,
    [property: JsonPropertyName("credit")] int Credit,
    [property: JsonPropertyName("amount")] int Amount,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("term")] string Term,
    [property: JsonPropertyName("devices")] int Devices,
    [property: JsonPropertyName("from_devices")] int? FromDevices,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt);

public sealed record PaymentLink(
    [property: JsonPropertyName("payment_id")] long PaymentId,
    [property: JsonPropertyName("url")] string Url);

/// <summary>The state of a payment: "pending" | "paid" | "failed"; a paid one carries the new ticket.</summary>
public sealed record PaymentStatus(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("license")] SignedTicket? License);

/// <summary>"Write to us": topic "question" | "bug" | "other", message 10–5000, the log as strict base64 (≤ 1 MiB) or null.</summary>
public sealed record SupportRequest(string Email, string Topic, string Message, string? LogBase64);
