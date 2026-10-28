using System.Text;

namespace PcCleaner.Core.Account;

/// <summary>
/// What a sign-in leaves on this computer: the device token, the account's e-mail, the device and account numbers, the last
/// ticket with its signature — and the install number of the update statistics, which outlives signing out.
/// </summary>
public sealed record AccountData(string? Token = null, string? Email = null, long? DeviceId = null, long? AccountId = null,
    string? Ticket = null, string? Signature = null, string? InstallId = null)
{
    /// <summary>The data after signing out or TOKEN_REVOKED: only the install number stays.</summary>
    public AccountData SignedOut() => new(InstallId: InstallId);

    // The generated ToString must never put the token, ticket or signature into a log: they are shown only as set/unset.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Token = ").Append(Mark(Token));
        builder.Append(", Email = ").Append(Email);
        builder.Append(", DeviceId = ").Append(DeviceId);
        builder.Append(", AccountId = ").Append(AccountId);
        builder.Append(", Ticket = ").Append(Mark(Ticket));
        builder.Append(", Signature = ").Append(Mark(Signature));
        builder.Append(", InstallId = ").Append(InstallId);
        return true;
    }

    private static string Mark(string? secret) => string.IsNullOrEmpty(secret) ? "<none>" : "<set>";
}

/// <summary>Where <see cref="AccountData"/> is kept: Windows — a DPAPI file of this user; Mac — the Keychain.</summary>
public interface IAccountStore
{
    /// <summary>The saved data; empty when there is none or it cannot be read.</summary>
    AccountData Load();

    void Save(AccountData data);
}
