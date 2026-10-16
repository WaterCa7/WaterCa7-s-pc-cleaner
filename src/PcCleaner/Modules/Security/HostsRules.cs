using System.Net;
using System.Net.Sockets;

namespace PcCleaner.Modules.Security;

/// <summary>Classifies hosts-file entries: site hijacks, blocked security/update sites, harmless ad-blocking.</summary>
public static class HostsRules
{
    public enum DomainCategory
    {
        Popular,
        Bank,
        Update,
        Security,
    }

    private enum MatchKind
    {
        /// <summary>host == pattern or host ends with "." + pattern.</summary>
        Suffix,

        /// <summary>Any dot-separated label starts with the pattern (e.g. "google" → google.ru, googleapis.com).</summary>
        LabelPrefix,

        /// <summary>Any label equals the pattern.</summary>
        Label,

        /// <summary>Pattern appears anywhere in the host name.</summary>
        Contains,
    }

    private sealed record DomainRule(string Pattern, MatchKind Kind, DomainCategory Category, string Display, string? Owner = null);

    private static readonly DomainRule[] Rules =
    {
        // Antivirus / security vendors and help sites
        new("kaspersky", MatchKind.Contains, DomainCategory.Security, "Касперский"),
        new("drweb", MatchKind.Contains, DomainCategory.Security, "Dr.Web"),
        new("eset", MatchKind.LabelPrefix, DomainCategory.Security, "ESET"),
        new("avast", MatchKind.LabelPrefix, DomainCategory.Security, "Avast"),
        new("avg", MatchKind.Label, DomainCategory.Security, "AVG"),
        new("malwarebytes", MatchKind.Contains, DomainCategory.Security, "Malwarebytes"),
        new("virustotal", MatchKind.Contains, DomainCategory.Security, "VirusTotal"),
        new("norton", MatchKind.LabelPrefix, DomainCategory.Security, "Norton"),
        new("symantec", MatchKind.Contains, DomainCategory.Security, "Symantec"),
        new("mcafee", MatchKind.Contains, DomainCategory.Security, "McAfee"),
        new("bitdefender", MatchKind.Contains, DomainCategory.Security, "Bitdefender"),
        new("avira", MatchKind.LabelPrefix, DomainCategory.Security, "Avira"),
        new("sophos", MatchKind.Contains, DomainCategory.Security, "Sophos"),
        new("trendmicro", MatchKind.Contains, DomainCategory.Security, "Trend Micro"),
        new("f-secure", MatchKind.Contains, DomainCategory.Security, "F-Secure"),
        new("comodo", MatchKind.Contains, DomainCategory.Security, "Comodo"),
        new("emsisoft", MatchKind.Contains, DomainCategory.Security, "Emsisoft"),
        new("hitmanpro", MatchKind.Contains, DomainCategory.Security, "HitmanPro"),
        new("zemana", MatchKind.Contains, DomainCategory.Security, "Zemana"),
        new("360totalsecurity", MatchKind.Contains, DomainCategory.Security, "360 Total Security"),
        new("virusinfo.info", MatchKind.Suffix, DomainCategory.Security, "VirusInfo"),
        new("safezone.cc", MatchKind.Suffix, DomainCategory.Security, "SafeZone"),
        new("wdcp.microsoft.com", MatchKind.Suffix, DomainCategory.Security, "Microsoft Defender"),
        new("wd.microsoft.com", MatchKind.Suffix, DomainCategory.Security, "Microsoft Defender"),
        new("definitionupdates.microsoft.com", MatchKind.Suffix, DomainCategory.Security, "обновления Microsoft Defender"),
        new("smartscreen.microsoft.com", MatchKind.Suffix, DomainCategory.Security, "SmartScreen"),
        new("smartscreen-prod.microsoft.com", MatchKind.Suffix, DomainCategory.Security, "SmartScreen"),
        new("security.microsoft.com", MatchKind.Suffix, DomainCategory.Security, "Microsoft Defender"),

        // Windows Update
        new("windowsupdate", MatchKind.Contains, DomainCategory.Update, "Центр обновления Windows"),
        new("update.microsoft.com", MatchKind.Suffix, DomainCategory.Update, "Центр обновления Windows"),
        new("delivery.mp.microsoft.com", MatchKind.Suffix, DomainCategory.Update, "Центр обновления Windows"),
        new("wustat.windows.com", MatchKind.Suffix, DomainCategory.Update, "Центр обновления Windows"),

        // Banks, payments, government services
        new("sberbank.ru", MatchKind.Suffix, DomainCategory.Bank, "Сбербанк"),
        new("sberbank.com", MatchKind.Suffix, DomainCategory.Bank, "Сбербанк"),
        new("sber.ru", MatchKind.Suffix, DomainCategory.Bank, "Сбер"),
        new("tinkoff.ru", MatchKind.Suffix, DomainCategory.Bank, "Т-Банк"),
        new("tbank.ru", MatchKind.Suffix, DomainCategory.Bank, "Т-Банк"),
        new("vtb.ru", MatchKind.Suffix, DomainCategory.Bank, "ВТБ"),
        new("alfabank.ru", MatchKind.Suffix, DomainCategory.Bank, "Альфа-Банк"),
        new("gazprombank.ru", MatchKind.Suffix, DomainCategory.Bank, "Газпромбанк"),
        new("raiffeisen.ru", MatchKind.Suffix, DomainCategory.Bank, "Райффайзенбанк"),
        new("pochtabank.ru", MatchKind.Suffix, DomainCategory.Bank, "Почта Банк"),
        new("sovcombank.ru", MatchKind.Suffix, DomainCategory.Bank, "Совкомбанк"),
        new("yoomoney.ru", MatchKind.Suffix, DomainCategory.Bank, "ЮMoney"),
        new("qiwi.com", MatchKind.Suffix, DomainCategory.Bank, "QIWI"),
        new("paypal.com", MatchKind.Suffix, DomainCategory.Bank, "PayPal"),
        new("gosuslugi.ru", MatchKind.Suffix, DomainCategory.Bank, "Госуслуги"),
        new("nalog.ru", MatchKind.Suffix, DomainCategory.Bank, "ФНС"),
        new("nalog.gov.ru", MatchKind.Suffix, DomainCategory.Bank, "ФНС"),
        new("mos.ru", MatchKind.Suffix, DomainCategory.Bank, "mos.ru"),

        // Popular sites
        new("google", MatchKind.LabelPrefix, DomainCategory.Popular, "Google", "Google"),
        new("youtube.com", MatchKind.Suffix, DomainCategory.Popular, "YouTube", "Google"),
        new("youtu.be", MatchKind.Suffix, DomainCategory.Popular, "YouTube", "Google"),
        new("ytimg.com", MatchKind.Suffix, DomainCategory.Popular, "YouTube", "Google"),
        new("ggpht.com", MatchKind.Suffix, DomainCategory.Popular, "Google", "Google"),
        new("gstatic.com", MatchKind.Suffix, DomainCategory.Popular, "Google", "Google"),
        new("yandex", MatchKind.LabelPrefix, DomainCategory.Popular, "Яндекс"),
        new("ya.ru", MatchKind.Suffix, DomainCategory.Popular, "Яндекс"),
        new("dzen.ru", MatchKind.Suffix, DomainCategory.Popular, "Дзен"),
        new("vk.com", MatchKind.Suffix, DomainCategory.Popular, "ВКонтакте"),
        new("vk.ru", MatchKind.Suffix, DomainCategory.Popular, "ВКонтакте"),
        new("vkontakte.ru", MatchKind.Suffix, DomainCategory.Popular, "ВКонтакте"),
        new("mail.ru", MatchKind.Suffix, DomainCategory.Popular, "Mail.ru"),
        new("ok.ru", MatchKind.Suffix, DomainCategory.Popular, "Одноклассники"),
        new("odnoklassniki.ru", MatchKind.Suffix, DomainCategory.Popular, "Одноклассники"),
        new("rutube.ru", MatchKind.Suffix, DomainCategory.Popular, "Rutube"),
        new("ozon.ru", MatchKind.Suffix, DomainCategory.Popular, "Ozon"),
        new("wildberries.ru", MatchKind.Suffix, DomainCategory.Popular, "Wildberries"),
        new("wb.ru", MatchKind.Suffix, DomainCategory.Popular, "Wildberries"),
        new("avito.ru", MatchKind.Suffix, DomainCategory.Popular, "Авито"),
        new("microsoft.com", MatchKind.Suffix, DomainCategory.Popular, "Microsoft"),
        new("live.com", MatchKind.Suffix, DomainCategory.Popular, "Microsoft (Outlook)"),
        new("outlook.com", MatchKind.Suffix, DomainCategory.Popular, "Outlook"),
        new("office.com", MatchKind.Suffix, DomainCategory.Popular, "Microsoft Office"),
        new("bing.com", MatchKind.Suffix, DomainCategory.Popular, "Bing"),
        new("apple.com", MatchKind.Suffix, DomainCategory.Popular, "Apple"),
        new("icloud.com", MatchKind.Suffix, DomainCategory.Popular, "iCloud"),
        new("steam", MatchKind.LabelPrefix, DomainCategory.Popular, "Steam"),
        new("epicgames.com", MatchKind.Suffix, DomainCategory.Popular, "Epic Games"),
        new("battle.net", MatchKind.Suffix, DomainCategory.Popular, "Battle.net"),
        new("github.com", MatchKind.Suffix, DomainCategory.Popular, "GitHub"),
        new("facebook.com", MatchKind.Suffix, DomainCategory.Popular, "Facebook", "Meta"),
        new("fb.com", MatchKind.Suffix, DomainCategory.Popular, "Facebook", "Meta"),
        new("fbcdn.net", MatchKind.Suffix, DomainCategory.Popular, "Facebook", "Meta"),
        new("messenger.com", MatchKind.Suffix, DomainCategory.Popular, "Messenger", "Meta"),
        new("instagram.com", MatchKind.Suffix, DomainCategory.Popular, "Instagram", "Meta"),
        new("cdninstagram.com", MatchKind.Suffix, DomainCategory.Popular, "Instagram", "Meta"),
        new("whatsapp.com", MatchKind.Suffix, DomainCategory.Popular, "WhatsApp", "Meta"),
        new("whatsapp.net", MatchKind.Suffix, DomainCategory.Popular, "WhatsApp", "Meta"),
        new("telegram.org", MatchKind.Suffix, DomainCategory.Popular, "Telegram", "Telegram"),
        new("telegram.me", MatchKind.Suffix, DomainCategory.Popular, "Telegram", "Telegram"),
        new("t.me", MatchKind.Suffix, DomainCategory.Popular, "Telegram", "Telegram"),
        new("twitter.com", MatchKind.Suffix, DomainCategory.Popular, "Twitter"),
        new("x.com", MatchKind.Suffix, DomainCategory.Popular, "X (Twitter)"),
        new("discord.com", MatchKind.Suffix, DomainCategory.Popular, "Discord"),
        new("discord.gg", MatchKind.Suffix, DomainCategory.Popular, "Discord"),
        new("twitch.tv", MatchKind.Suffix, DomainCategory.Popular, "Twitch"),
        new("wikipedia.org", MatchKind.Suffix, DomainCategory.Popular, "Википедия"),
        new("amazon.com", MatchKind.Suffix, DomainCategory.Popular, "Amazon"),
    };

    /// <summary>Networks that belong to the company behind a site (used to soften "redirect" findings).</summary>
    private static readonly (string Owner, string Cidr)[] OwnerNetworks =
    {
        ("Meta", "31.13.24.0/21"), ("Meta", "31.13.64.0/18"), ("Meta", "45.64.40.0/22"), ("Meta", "66.220.144.0/20"),
        ("Meta", "69.63.176.0/20"), ("Meta", "69.171.224.0/19"), ("Meta", "74.119.76.0/22"), ("Meta", "102.132.96.0/20"),
        ("Meta", "103.4.96.0/22"), ("Meta", "129.134.0.0/16"), ("Meta", "157.240.0.0/16"), ("Meta", "163.70.128.0/17"),
        ("Meta", "173.252.64.0/18"), ("Meta", "179.60.192.0/22"), ("Meta", "185.60.216.0/22"), ("Meta", "185.89.216.0/22"),
        ("Meta", "204.15.20.0/22"),
        ("Google", "8.8.4.0/24"), ("Google", "8.8.8.0/24"), ("Google", "64.233.160.0/19"), ("Google", "66.102.0.0/20"),
        ("Google", "66.249.64.0/19"), ("Google", "72.14.192.0/18"), ("Google", "74.125.0.0/16"), ("Google", "108.177.0.0/17"),
        ("Google", "142.250.0.0/15"), ("Google", "172.217.0.0/16"), ("Google", "172.253.0.0/16"), ("Google", "173.194.0.0/16"),
        ("Google", "209.85.128.0/17"), ("Google", "216.58.192.0/19"), ("Google", "216.239.32.0/19"),
        ("Telegram", "91.105.192.0/23"), ("Telegram", "91.108.4.0/22"), ("Telegram", "91.108.8.0/22"),
        ("Telegram", "91.108.12.0/22"), ("Telegram", "91.108.16.0/22"), ("Telegram", "91.108.20.0/22"),
        ("Telegram", "91.108.56.0/22"), ("Telegram", "149.154.160.0/20"), ("Telegram", "185.76.151.0/24"),
    };

    /// <summary>0.0.0.0, 127.x.x.x, ::1 and :: — the "send nowhere" addresses used for blocking.</summary>
    public static bool IsBlockingAddress(IPAddress address)
        => IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    /// <summary>Returns the most important matching category of a host name (Security &gt; Update &gt; Bank &gt; Popular).</summary>
    public static (DomainCategory Category, string Display, string? Owner)? Categorize(string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0)
            return null;
        var labels = h.Split('.');
        DomainRule? best = null;
        foreach (var rule in Rules)
        {
            var match = rule.Kind switch
            {
                MatchKind.Suffix => h == rule.Pattern || h.EndsWith("." + rule.Pattern, StringComparison.Ordinal),
                MatchKind.LabelPrefix => labels.Any(l => l.StartsWith(rule.Pattern, StringComparison.Ordinal)),
                MatchKind.Label => labels.Any(l => l == rule.Pattern),
                MatchKind.Contains => h.Contains(rule.Pattern, StringComparison.Ordinal),
                _ => false,
            };
            if (match && (best is null || rule.Category > best.Category))
                best = rule;
        }
        return best is null ? null : (best.Category, best.Display, best.Owner);
    }

    /// <summary>Returns the owner (Meta/Google/Telegram) of a public IPv4 address, if known.</summary>
    public static string? OwnerOf(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return null;
        foreach (var (owner, cidr) in OwnerNetworks)
        {
            if (InCidr(address, cidr))
                return owner;
        }
        return null;
    }

    internal static bool InCidr(IPAddress address, string cidr)
    {
        var slash = cidr.IndexOf('/');
        var network = IPAddress.Parse(cidr[..slash]);
        var bits = int.Parse(cidr[(slash + 1)..]);
        if (address.AddressFamily != network.AddressFamily)
            return false;
        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (n[i] & mask))
                return false;
        }
        return true;
    }

    /// <summary>Sets <see cref="HostsEntry.Risk"/> and <see cref="HostsEntry.Reason"/>.</summary>
    public static void Classify(HostsEntry entry)
    {
        var (risk, reason) = Evaluate(entry);
        entry.Risk = risk;
        entry.Reason = reason;
    }

    public static (SecurityStatus Risk, string Reason) Evaluate(HostsEntry entry)
    {
        if (!HostsFile.TryParseAddress(entry.Address, out var ip) || ip is null)
            return (SecurityStatus.Info, "Строка записана с ошибкой — Windows её не учитывает.");
        if (entry.Hostnames.Count == 0)
            return (SecurityStatus.Info, "В строке нет имени сайта — Windows её не учитывает.");

        var blocking = IsBlockingAddress(ip);
        var worst = (Risk: SecurityStatus.Ok, Reason: "");
        foreach (var host in entry.Hostnames)
        {
            var r = EvaluateHost(host, ip, blocking);
            if (r.Risk.Rank() > worst.Risk.Rank() || worst.Reason.Length == 0)
                worst = r;
        }
        return worst;
    }

    private static (SecurityStatus Risk, string Reason) EvaluateHost(string host, IPAddress ip, bool blocking)
    {
        var h = host.ToLowerInvariant();
        if (h is "localhost" or "localhost.localdomain" or "local" or "broadcasthost" or "ip6-localhost" or "ip6-loopback")
            return (SecurityStatus.Ok, "Стандартная запись: имя этого компьютера.");

        var cat = Categorize(host);
        if (cat is null)
        {
            return blocking
                ? (SecurityStatus.Ok, "Блокировка сайта — так обычно отключают рекламу или слежку. Это безопасно.")
                : (SecurityStatus.Info,
                    $"Пользовательская запись: {host} → {ip}. Обычно её добавляют программы или администратор сети.");
        }

        var (category, display, owner) = cat.Value;
        if (blocking)
        {
            return category switch
            {
                DomainCategory.Security => (SecurityStatus.Warning,
                    $"Заблокирован сайт {display} ({host}). Так вирусы мешают антивирусу обновляться и не дают скачать средства лечения."),
                DomainCategory.Update => (SecurityStatus.Warning,
                    $"Заблокирован {host} — Windows не сможет получать обновления безопасности."),
                _ => (SecurityStatus.Info, $"Сайт {host} ({display}) заблокирован: он не будет открываться."),
            };
        }

        var ipOwner = OwnerOf(ip);
        if (owner is not null && ipOwner == owner)
        {
            return (SecurityStatus.Warning,
                $"{host} указывает на сервер самой компании {owner} ({ip}). Обычно такие строки добавляют вручную, " +
                "чтобы обойти блокировку. Если вы этого не делали — удалите строку.");
        }

        var extra = category == DomainCategory.Bank ? " Для банков и госуслуг это особенно опасно." : "";
        return (SecurityStatus.Danger,
            $"Перенаправление: сайт {display} ({host}) будет открываться с адреса {ip}. Так вирусы подсовывают " +
            $"поддельные страницы, чтобы украсть пароли.{extra} Если вы не добавляли эту строку сами — удалите её.");
    }
}
