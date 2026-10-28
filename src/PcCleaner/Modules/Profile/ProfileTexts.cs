using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Dashboard;

namespace PcCleaner.Modules.Profile;

/// <summary>The lines of Профиль about the license and the computers (spec 3.3), as pure functions.</summary>
public static class ProfileTexts
{
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
