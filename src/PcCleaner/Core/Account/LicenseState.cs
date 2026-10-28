namespace PcCleaner.Core.Account;

/// <summary>What the program may do: a guest and a free account see exactly the same; Pro unlocks the Pro features.</summary>
public enum Tier
{
    Guest,
    Free,
    Pro,
}

/// <summary>Why the tier is what it is (Профиль shows a line for some of them).</summary>
public enum LicenseReason
{
    None,
    NotSignedIn,
    NoTicket,
    WrongDevice,
    ClockBack,
    Stale,
    NoPlan,
    Expired,
}

/// <summary>The tier and what Профиль shows about the license (from the ticket, when there is one).</summary>
public sealed record LicenseStatus(Tier Tier, LicenseReason Reason, string? Plan, string? Kind, int DevicesMax, DateTimeOffset? ExpiresAt,
    DateTimeOffset? IssuedAt, DateTimeOffset? ValidUntil, int? OfflineDaysLeft);

/// <summary>The rule of the spec (section 2), as a pure function.</summary>
public static class LicenseState
{
    public static readonly IReadOnlySet<string> ProPlans = new HashSet<string>(StringComparer.Ordinal) { "pro", "family", "custom" };

    public static LicenseStatus Evaluate(LicenseTicketData? ticket, bool signedIn, string hwHash, long? deviceId, long? accountId, DateTimeOffset now)
    {
        if (!signedIn)
            return new LicenseStatus(Tier.Guest, LicenseReason.NotSignedIn, null, null, 0, null, null, null, null);
        if (ticket is null)
            return new LicenseStatus(Tier.Free, LicenseReason.NoTicket, null, null, 0, null, null, null, null);

        LicenseStatus Free(LicenseReason reason)
            => new(Tier.Free, reason, ticket.Plan, ticket.Kind, ticket.DevicesMax, ticket.ExpiresAt, ticket.IssuedAt, ticket.ValidUntil, null);

        if (!string.Equals(ticket.HwHash, hwHash, StringComparison.OrdinalIgnoreCase) || ticket.DeviceId != deviceId || ticket.Sub != accountId)
            return Free(LicenseReason.WrongDevice);
        if (now < ticket.IssuedAt)
            return Free(LicenseReason.ClockBack);
        if (now >= ticket.ValidUntil)
            return Free(LicenseReason.Stale);
        if (!ProPlans.Contains(ticket.Plan))
            return Free(LicenseReason.NoPlan);
        if (ticket.ExpiresAt is { } end && now >= end)
            return Free(LicenseReason.Expired);

        var offline = (int)Math.Ceiling((ticket.ValidUntil - now).TotalDays);
        return new LicenseStatus(Tier.Pro, LicenseReason.None, ticket.Plan, ticket.Kind, ticket.DevicesMax, ticket.ExpiresAt, ticket.IssuedAt,
            ticket.ValidUntil, offline);
    }
}
