using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Dashboard;

namespace PcCleaner.Modules.Profile;

/// <summary>The lines of Профиль about the license and the computers (spec 3.3), as pure functions.</summary>
public static class ProfileTexts
{
    public static string PlanName(string? plan) => plan switch
    {
        "pro" => "Pro",
        "family" => T._("Pro Семья"),
        "custom" => T._("Особый"),
        _ => "Free",
    };

    public static string? Term(LicenseStatus s) => s.Tier != Tier.Pro
        ? null
        : s.ExpiresAt is { } end
            ? T.F("до {0}", Date(end))
            : T._("навсегда");

    public static string? Checked(LicenseStatus s, DateTimeOffset now)
        => s.IssuedAt is { } t ? T.F("проверено {0}", HealthScoring.RelativeTime(t.LocalDateTime, now.LocalDateTime)) : null;

    public static string? Note(LicenseStatus s, DateTimeOffset now) => s.Reason switch
    {
        LicenseReason.Stale => T._("Нужна проверка лицензии через интернет — нажмите «Обновить»"),
        LicenseReason.ClockBack => T._("Часы компьютера отстают — проверьте дату и время Windows"),
        LicenseReason.WrongDevice => T._("Лицензия выдана другому компьютеру — нажмите «Обновить»"),
        LicenseReason.Expired when s.ExpiresAt is { } end => T.F("Подписка закончилась {0}", Date(end)),
        LicenseReason.None when s.Tier == Tier.Pro && s.IssuedAt is { } t && now - t > TimeSpan.FromDays(1)
            => T.F("Pro без интернета ещё {0} дн.", s.OfflineDaysLeft ?? 0),
        _ => null,
    };

    public static string DeviceDetails(DeviceEntry d, DateTimeOffset now)
    {
        var system = d.Platform == "mac" ? "Mac" : "Windows";
        var text = T.F("{0} · привязан {1}", system, Date(d.BoundAt));
        return d.LastSeenAt is { } seen
            ? text + " · " + T.F("в сети {0}", HealthScoring.RelativeTime(seen.LocalDateTime, now.LocalDateTime))
            : text;
    }

    private static string Date(DateTimeOffset t) => t.LocalDateTime.ToString("dd.MM.yyyy", T.Culture);
}
