using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Security;

/// <summary>
/// Pure evaluation logic of the security checks (no system access), so it can be unit-tested.
/// </summary>
public static partial class SecurityRules
{
    public const int SignatureMaxAgeDays = 3;
    public const int UpdatesMaxAgeDays = 60;

    /// <summary>WMI "never" value for *Age properties.</summary>
    private const long NeverAge = 4294967295;

    // ===================== Common helpers =====================

    public static int? NormalizeAge(long? age) => age is null or < 0 or >= NeverAge or > 36500 ? null : (int)age.Value;

    /// <summary>0 → "сегодня", 1 → "вчера", 5 → "5 дней назад", null → "ни разу".</summary>
    public static string AgoText(int? days) => days switch
    {
        null => T._("ни разу"),
        <= 0 => T._("сегодня"),
        1 => T._("вчера"),
        _ => T.F("{0} назад", Days(days.Value)),
    };

    public static string Days(int days) => $"{days} {Format.Plural(days, "день", "дня", "дней")}";

    /// <summary>"2 проблемы и 1 совет", "1 совет", "проблем нет" — the same words as the dashboard tile.</summary>
    public static string FindingsText(int problems, int tips)
    {
        var parts = new List<string>(2);
        if (problems > 0)
            parts.Add(Format.Count(problems, "проблема", "проблемы", "проблем"));
        if (tips > 0)
            parts.Add(Format.Count(tips, "совет", "совета", "советов"));
        return parts.Count switch
        {
            0 => T._("проблем нет"),
            1 => parts[0],
            _ => T.F("{0} и {1}", parts[0], parts[1]),
        };
    }

    // ===================== Antivirus =====================

    /// <summary>Defender ThreatStatusID: 2 cleaned, 3 quarantined, 4 removed, 6 blocked (= neutralized).</summary>
    public static bool IsThreatResolved(int statusId) => statusId is 2 or 3 or 4 or 6;

    /// <summary>Defender ThreatStatusID 5: the user allowed the item on this device.</summary>
    public static bool IsThreatAllowed(int statusId) => statusId == 5;

    public static string ThreatStatusText(int statusId) => statusId switch
    {
        1 => T._("Обнаружена"),
        2 => T._("Вылечена"),
        3 => T._("В карантине"),
        4 => T._("Удалена"),
        5 => T._("Разрешена пользователем"),
        6 => T._("Заблокирована"),
        102 => T._("Ошибка помещения в карантин"),
        103 => T._("Не удалось удалить"),
        104 => T._("Ошибка разрешения"),
        105 => T._("Действие прервано"),
        107 => T._("Не удалось заблокировать"),
        _ => T._("Неизвестно"),
    };

    public static string ThreatSeverityText(int severityId) => severityId switch
    {
        1 => T._("Низкий"),
        2 => T._("Средний"),
        4 => T._("Высокий"),
        5 => T._("Критический"),
        _ => T._("Неизвестный"),
    };

    /// <summary>
    /// Microsoft's own products in Security Center ("Windows Defender", "Microsoft Defender Antivirus", …).
    /// Whole words only: "Bitdefender" is a third-party antivirus, not Microsoft Defender.
    /// </summary>
    // l10n:data-begin — product names that Windows Security Center gives in its language, matched and never shown
    private static readonly Regex MicrosoftProductName = new(
        @"\bMicrosoft\b|\bWindows\s+(Defender|Firewall|Security)\b|Защитник\s+Windows|Брандмауэр\s+(Защитника\s+)?Windows|Безопасность\s+Windows",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // l10n:data-end

    public static bool IsMicrosoftProduct(string? name)
        => !string.IsNullOrWhiteSpace(name) && MicrosoftProductName.IsMatch(name);

    public static SecurityProduct MakeProduct(string name, long productState)
    {
        var st = AvProductState.Decode(productState);
        return new SecurityProduct(name, st.Raw, st.Enabled, st.UpToDate, IsMicrosoftProduct(name));
    }

    /// <summary>
    /// "Kaspersky установлен, но его защита не работает (срок лицензии истёк)" for an installed but inactive
    /// third-party antivirus, or null if there is none.
    /// </summary>
    public static string? InactiveThirdPartyText(IEnumerable<SecurityProduct> products)
    {
        var inactive = products.Where(p => !p.IsMicrosoft && !p.Enabled).ToList();
        if (inactive.Count == 0)
            return null;
        var p = inactive[0];
        return T.F("{0} установлен, но его защита не работает ({1})", p.Name, AvProductState.Decode(p.ProductState).StateText);
    }

    /// <summary>Defender exclusions that cover too much (whole drives, user folders, temp, executable types).</summary>
    public static bool IsDangerousExclusion(string exclusion)
    {
        var e = exclusion.Trim().Trim('"').TrimEnd('\\', '/');
        if (e.Length == 0)
            return false;
        // "C:" / "C:\" or "*"
        if (e == "*" || (e.Length == 2 && e[1] == ':'))
            return true;
        var lower = e.ToLowerInvariant();
        if (lower is ".exe" or "exe" or ".dll" or "dll" or ".ps1" or "ps1" or ".bat" or "bat" or ".cmd" or "cmd" or ".scr" or "scr")
            return true;
        if (lower is "powershell.exe" or "cmd.exe" or "rundll32.exe" or "regsvr32.exe" or "wscript.exe" or "mshta.exe")
            return true;
        var dangerousFolders = new[]
        {
            @"\users", @"\appdata", @"\appdata\local", @"\appdata\roaming", @"\appdata\local\temp", @"\temp",
            @"\windows", @"\windows\system32", @"\windows\temp", @"\programdata", @"\program files",
            @"\program files (x86)", @"%temp%", @"%appdata%", @"%localappdata%", @"%userprofile%",
        };
        foreach (var f in dangerousFolders)
        {
            if (lower.EndsWith(f, StringComparison.Ordinal) || lower == f.TrimStart('\\'))
                return true;
        }
        // C:\Users\<name> (whole profile)
        var parts = lower.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts[1] == "users";
    }

    public static SecurityCheckResult EvaluateAntivirus(
        DefenderStatus? d, IReadOnlyList<SecurityProduct> products, IReadOnlyList<ThreatDetection> threats)
    {
        var lines = new List<string>();
        SecurityStatus status;
        string state;
        // True when Defender works and its databases are fresh: too broad exclusions then turn the state into a warning.
        var healthy = false;

        var third = products.Where(p => !p.IsMicrosoft && p.Enabled).ToList();
        var inactiveThird = InactiveThirdPartyText(products);
        var activeThreats = threats.Where(t => t.IsActive).ToList();
        var active = ActiveAntivirusNames(products, d);

        if (third.Count > 0)
        {
            var names = string.Join(", ", third.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase));
            var outdated = third.Where(p => !p.UpToDate).ToList();
            status = outdated.Count == 0 ? SecurityStatus.Ok : SecurityStatus.Warning;
            state = outdated.Count == 0 ? T.F("Защищает {0}", names) : T.F("{0}: базы устарели", outdated[0].Name);
            if (active.Count >= 2)
            {
                status = SecurityStatus.Warning;
                if (outdated.Count == 0)
                    state = T._("Работают сразу несколько антивирусов");
                lines.Add(T.F("Одновременно включены: {0}. Антивирусы мешают друг другу и замедляют компьютер — оставьте один, а лишние удалите в разделе «Программы».", string.Join(", ", active)));
            }
            else
            {
                lines.Add(d is null || !d.RealTimeEnabled
                    ? T._("Защитник Windows работает в пассивном режиме — так и должно быть, когда установлен другой антивирус.")
                    : T._("Защитник Windows тоже включён."));
            }
        }
        else if (d is null)
        {
            var disabled = products.Where(p => !p.Enabled).ToList();
            if (disabled.Count > 0)
            {
                status = SecurityStatus.Danger;
                state = T._("Антивирус выключен");
                lines.Add(T.F("Установлен {0}, но защита не работает. Включите её в настройках антивируса или в «Безопасности Windows».", string.Join(", ", disabled.Select(p => p.Name).Distinct())));
            }
            else
            {
                status = SecurityStatus.Unknown;
                state = T._("Не удалось узнать состояние антивируса");
                lines.Add(T._("Служба Защитника Windows не ответила. Откройте «Безопасность Windows», чтобы проверить защиту."));
            }
        }
        else
        {
            if (d.PolicyDisablesAntivirus)
            {
                status = SecurityStatus.Danger;
                state = T._("Защитник Windows отключён групповой политикой");
                lines.Add(T._("Так часто делают «активаторы» и вирусы. Если компьютер не рабочий, политику стоит удалить."));
            }
            else if (!d.ServiceEnabled || !d.AntivirusEnabled)
            {
                status = SecurityStatus.Danger;
                state = T._("Защитник Windows выключен");
                lines.Add(inactiveThird is null
                    ? T._("Другого антивируса не найдено — компьютер не защищён от вирусов.")
                    : T.F("{0} — компьютер не защищён от вирусов.", inactiveThird));
            }
            else if (!d.RealTimeEnabled)
            {
                status = SecurityStatus.Danger;
                state = T._("Защита в реальном времени выключена");
                lines.Add(d.PolicyDisablesRealtime
                    ? T._("Отключена групповой политикой — частый признак «активатора» или вируса.")
                    : T._("Файлы не проверяются при запуске. Включите защиту — это безопасно."));
                if (inactiveThird is not null)
                    lines.Add(inactiveThird + ".");
            }
            else if (d.SignatureAgeDays is > SignatureMaxAgeDays)
            {
                status = SecurityStatus.Warning;
                state = T.F("Антивирусные базы устарели ({0})", Days(d.SignatureAgeDays.Value));
                lines.Add(T._("Старые базы не знают новых вирусов. Обновите их — это займёт пару минут."));
            }
            else
            {
                status = SecurityStatus.Ok;
                state = T._("Защитник Windows включён, базы актуальны");
                healthy = true;
            }

            var facts = new List<string>
            {
                T.F("Защита в реальном времени: {0}", d.RealTimeEnabled ? T._("вкл.") : T._("выкл.")),
            };
            if (d.TamperProtected is not null)
                facts.Add(T.F("Защита от подделки: {0}", d.TamperProtected.Value ? T._("вкл.") : T._("выкл.")));
            lines.Add(string.Join(" · ", facts));
            var dates = new List<string>
            {
                T.F("Базы: {0}", d.SignatureUpdated is { } su ? Format.Date(su) : AgoText(d.SignatureAgeDays)),
                T.F("Быстрая проверка: {0}", AgoText(d.QuickScanAgeDays)),
                T.F("Полная: {0}", AgoText(d.FullScanAgeDays)),
            };
            lines.Add(string.Join(" · ", dates));

            var badExclusions = d.Exclusions.Where(IsDangerousExclusion).ToList();
            if (badExclusions.Count > 0)
            {
                status = status.Worst(SecurityStatus.Warning);
                if (status == SecurityStatus.Warning && healthy)
                    state = T._("Слишком широкие исключения антивируса");
                lines.Add(T.F("Защитник Windows не проверяет: {0}. Майнеры и вирусы добавляют себе такие исключения.", string.Join(", ", badExclusions.Take(3))));
            }
            else if (d.ExclusionsHidden && !Admin.IsElevated)
            {
                lines.Add(T._("Список исключений антивируса виден только администратору."));
            }
        }

        if (activeThreats.Count > 0)
        {
            status = SecurityStatus.Danger;
            state = T.F("Есть необезвреженные угрозы: {0}", activeThreats.Count);
            lines.Insert(0, T.F("Последняя: {0}.", activeThreats[0].Name) + " " +
                            (CanRemoveThreats(d, threats)
                                ? T._("Нажмите «Обезвредить угрозы» или выберите действие в «Безопасность Windows» → «Журнал защиты».")
                                : T._("Откройте «Безопасность Windows» → «Журнал защиты» и выберите действие.")));
        }
        else
        {
            var allowed = threats.Where(t => t.IsAllowed).ToList();
            if (allowed.Count > 0)
                lines.Add(allowed.Count > 1
                    ? T.F("Вы разрешили в Защитнике Windows: {0} и ещё {1}. Если не уверены в этом файле, отмените разрешение в «Безопасность Windows» → «Журнал защиты».", allowed[0].Name, allowed.Count - 1)
                    : T.F("Вы разрешили в Защитнике Windows: {0}. Если не уверены в этом файле, отмените разрешение в «Безопасность Windows» → «Журнал защиты».", allowed[0].Name));
        }

        return new SecurityCheckResult(SecurityCheckIds.Antivirus, status, state, string.Join("\n", lines));
    }

    // ===================== Firewall =====================

    /// <summary>MSFT_NetFirewallProfile.Enabled is a GpoBoolean: 0 = false, 1 = true, 2 = not configured (default on).</summary>
    public static bool? ParseGpoBoolean(object? value) => value switch
    {
        null => null,
        bool b => b,
        _ => long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var n) ? n != 0 : null,
    };

    /// <summary>
    /// Group Policy (HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall\&lt;profile&gt;\EnableFirewall) overrides the local
    /// setting that MSFT_NetFirewallProfile reports. <paramref name="policy"/> maps "Domain"/"Private"/"Public" to the
    /// policy value (null = not configured).
    /// </summary>
    public static List<FirewallProfileState> ApplyFirewallPolicy(IEnumerable<FirewallProfileState> profiles,
        IReadOnlyDictionary<string, int?> policy)
    {
        return profiles.Select(p =>
        {
            if (!policy.TryGetValue(p.Name, out var v) || v is null)
                return p;
            return p with { Enabled = v.Value != 0, ByPolicy = true };
        }).ToList();
    }

    /// <summary>Registry subkey of the firewall policy for a profile name.</summary>
    public static string FirewallPolicySubKey(string profile) => profile.ToLowerInvariant() switch
    {
        "domain" => "DomainProfile",
        "private" => "StandardProfile",
        "public" => "PublicProfile",
        _ => profile,
    };

    public static SecurityCheckResult EvaluateFirewall(IReadOnlyList<FirewallProfileState> profiles,
        IReadOnlyList<SecurityProduct> firewalls)
    {
        var why = T._("Без брандмауэра компьютер открыт для атак из сети — особенно в общественном Wi-Fi.");
        var third = firewalls.Where(f => !f.IsMicrosoft && f.Enabled).ToList();
        if (third.Count > 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Firewall, SecurityStatus.Ok,
                T.F("Работает {0}", string.Join(", ", third.Select(f => f.Name).Distinct())),
                T._("Сеть защищает сторонний брандмауэр. Брандмауэр Windows при этом может быть выключен — это нормально."));
        }
        if (profiles.Count == 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Firewall, SecurityStatus.Unknown,
                T._("Не удалось узнать состояние брандмауэра"),
                T._("Служба брандмауэра не ответила. Проверьте его в «Безопасности Windows» → «Брандмауэр и защита сети»."));
        }

        var off = profiles.Where(p => p.Enabled == false).ToList();
        var summary = string.Join(" · ", profiles.Select(p =>
        {
            var state = p.Enabled == false ? T._("выкл.") : p.Enabled == true ? T._("вкл.") : "?";
            return $"{Capitalize(p.ShortName)}: {state}" + (p.ByPolicy ? " " + T._("(политика)") : "");
        }));
        var policyNote = off.Any(p => p.ByPolicy)
            ? "\n" + T._("Выключение задано групповой политикой: на рабочем компьютере так настраивает администратор, на домашнем — «активаторы» и вирусы. Включить брандмауэр поверх политики нельзя.")
            : "";
        if (off.Any(p => !p.Name.Equals("Domain", StringComparison.OrdinalIgnoreCase)))
        {
            var state = off.Count >= 3 || off.Count == profiles.Count
                ? T._("Выключен для всех сетей")
                : off.Count == 1
                    ? T.F("Выключен для {0} сети", off[0].Genitive)
                    : T.F("Выключен для {0} и {1} сетей", string.Join(", ", off.Take(off.Count - 1).Select(p => p.Genitive)), off[^1].Genitive);
            return new SecurityCheckResult(SecurityCheckIds.Firewall, SecurityStatus.Danger, state,
                summary + "\n" + why + policyNote);
        }
        if (off.Count > 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Firewall, SecurityStatus.Warning,
                T._("Выключен для доменной сети"),
                summary + "\n" + T._("Доменный профиль работает только в корпоративных сетях, но лучше держать его включённым.") + policyNote);
        }
        return new SecurityCheckResult(SecurityCheckIds.Firewall, SecurityStatus.Ok,
            T._("Включён для всех типов сетей"), summary + "\n" + T._("Чужие подключения из сети блокируются."));
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], T.Culture) + s[1..];

    // ===================== UAC =====================

    public static SecurityCheckResult EvaluateUac(UacState uac)
    {
        if (!uac.Readable)
        {
            return new SecurityCheckResult(SecurityCheckIds.Uac, SecurityStatus.Unknown,
                T._("Не удалось прочитать настройки UAC"), T._("Нет доступа к разделу реестра с настройками."));
        }
        if (uac.EnableLua == 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Uac, SecurityStatus.Danger, T._("UAC отключён"),
                T._("Любая программа получает права администратора без вашего ведома. После включения нужна перезагрузка."));
        }
        if (uac.ConsentPromptBehaviorAdmin == 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Uac, SecurityStatus.Warning, T._("Запросы UAC отключены"),
                T._("Программы получают права администратора молча, без окна подтверждения. Рекомендуем вернуть запросы."));
        }
        var details = T._("Перед изменением системы Windows спрашивает разрешение.");
        if (uac.PromptOnSecureDesktop == 0)
            details += " " + T._("Запрос показывается без затемнения экрана — это немного менее надёжно.");
        return new SecurityCheckResult(SecurityCheckIds.Uac, SecurityStatus.Ok, T._("UAC включён"), details);
    }

    // ===================== Hosts =====================

    /// <summary>"Удалена 1 строка", "Удалены 3 строки", "Удалено 5 строк".</summary>
    public static string LinesRemovedText(int n)
        => string.Format(T.Culture, Format.Plural(n, "Удалена {0}", "Удалены {0}", "Удалено {0}"), Format.Count(n, "строка", "строки", "строк"));

    /// <summary>"будет удалена 1 строка", "будут удалены 3 строки", "будут удалены 5 строк".</summary>
    public static string LinesToRemoveText(int n)
        => string.Format(T.Culture, Format.Plural(n, "будет удалена {0}", "будут удалены {0}", "будут удалены {0}"), Format.Count(n, "строка", "строки", "строк"));

    /// <summary>"1 запись требует внимания", "3 записи требуют внимания", "5 записей требуют внимания": one sentence for each form.</summary>
    public static string EntriesNeedAttention(int n)
        => string.Format(T.Culture, Format.Plural(n, "{0} запись требует внимания", "{0} записи требуют внимания", "{0} записей требуют внимания"), Format.Number(n));

    public static SecurityCheckResult EvaluateHosts(HostsReport hosts)
    {
        if (hosts.Error is not null)
        {
            return new SecurityCheckResult(SecurityCheckIds.Hosts, SecurityStatus.Unknown,
                T._("Не удалось прочитать файл hosts"), hosts.Error);
        }
        if (!hosts.Exists)
        {
            return new SecurityCheckResult(SecurityCheckIds.Hosts, SecurityStatus.Ok, T._("Файла hosts нет"),
                T._("Без файла hosts Windows спрашивает адреса сайтов у DNS — это нормально."));
        }
        var entries = hosts.Entries;
        var dangers = entries.Where(e => e.Risk == SecurityStatus.Danger).ToList();
        var warnings = entries.Where(e => e.Risk == SecurityStatus.Warning).ToList();
        if (dangers.Count > 0)
        {
            var sites = dangers.SelectMany(e => e.Hostnames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new SecurityCheckResult(SecurityCheckIds.Hosts, SecurityStatus.Danger,
                T.F("Подменены адреса сайтов: {0}", sites.Count),
                T.F("Например, {0}. Так вирусы уводят на поддельные страницы. Проверьте строки в списке ниже.", sites[0]));
        }
        if (warnings.Count > 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Hosts, SecurityStatus.Warning,
                EntriesNeedAttention(warnings.Count),
                warnings[0].Reason);
        }
        if (entries.Count == 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Hosts, SecurityStatus.Ok, T._("Файл чистый"),
                T._("Активных записей нет — сайты открываются по настоящим адресам."));
        }
        return new SecurityCheckResult(SecurityCheckIds.Hosts, SecurityStatus.Ok,
            T.F("{0}, опасных нет", Format.Count(entries.Count, "запись", "записи", "записей")),
            T._("Только обычные записи — например, блокировка рекламы."));
    }

    // ===================== Winlogon =====================

    private static string NormalizeExe(string value, string windowsDir)
    {
        var v = value.Trim().Trim('"');
        v = v.Replace("%SystemRoot%", windowsDir, StringComparison.OrdinalIgnoreCase)
            .Replace("%windir%", windowsDir, StringComparison.OrdinalIgnoreCase);
        return v;
    }

    /// <summary>Shell must be just explorer.exe (optionally with the Windows folder path).</summary>
    public static bool IsShellDefault(string? value, string windowsDir)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 1)
            return false;
        var p = NormalizeExe(parts[0], windowsDir);
        return p.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) ||
               p.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
               p.Equals(Path.Combine(windowsDir, "explorer.exe"), StringComparison.OrdinalIgnoreCase) ||
               p.Equals(@"C:\Windows\explorer.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Userinit must be "C:\Windows\system32\userinit.exe," (case-insensitive, trailing comma optional).</summary>
    public static bool IsUserinitDefault(string? value, string windowsDir)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 1)
            return false;
        var p = NormalizeExe(parts[0], windowsDir);
        return p.Equals(Path.Combine(windowsDir, "system32", "userinit.exe"), StringComparison.OrdinalIgnoreCase) ||
               p.Equals(@"C:\Windows\system32\userinit.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static string DefaultUserinit(string windowsDir) => Path.Combine(windowsDir, "system32", "userinit.exe") + ",";

    public static SecurityCheckResult EvaluateWinlogon(WinlogonState w, string windowsDir)
    {
        if (!w.Readable)
        {
            return new SecurityCheckResult(SecurityCheckIds.Winlogon, SecurityStatus.Unknown,
                T._("Не удалось прочитать параметры входа"), T._("Нет доступа к разделу Winlogon в реестре."));
        }
        var problems = new List<string>();
        if (!IsShellDefault(w.Shell, windowsDir))
            problems.Add(T.F("Shell = «{0}» (должно быть explorer.exe)", Show(w.Shell)));
        if (!IsUserinitDefault(w.Userinit, windowsDir))
            problems.Add(T.F("Userinit = «{0}» (должно быть {1})", Show(w.Userinit), DefaultUserinit(windowsDir)));
        if (!string.IsNullOrWhiteSpace(w.UserShell) && !IsShellDefault(w.UserShell, windowsDir))
            problems.Add(T.F("для вашей учётной записи Shell = «{0}»", w.UserShell));
        if (problems.Count > 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Winlogon, SecurityStatus.Danger,
                T._("Подменён запуск при входе в Windows"),
                string.Join("\n", problems) + "\n" + T._("Так вирусы запускаются вместе с рабочим столом."));
        }
        return new SecurityCheckResult(SecurityCheckIds.Winlogon, SecurityStatus.Ok, T._("Параметры входа стандартные"),
            T._("Shell и Userinit указывают на программы Windows — посторонних программ при входе нет."));
    }

    private static string Show(string? s) => string.IsNullOrWhiteSpace(s) ? T._("пусто") : s.Trim();

    // ===================== IFEO =====================

    private static readonly HashSet<string> SensitiveImages = new(StringComparer.OrdinalIgnoreCase)
    {
        "taskmgr.exe", "msconfig.exe", "regedit.exe", "regedt32.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
        "powershell_ise.exe", "mmc.exe", "explorer.exe", "rstrui.exe", "systemreset.exe", "sethc.exe", "utilman.exe", "osk.exe",
        "magnify.exe", "narrator.exe", "displayswitch.exe", "atbroker.exe", "msmpeng.exe", "mpcmdrun.exe",
        "msascui.exe", "msseces.exe", "nissrv.exe", "securityhealthsystray.exe", "securityhealthservice.exe",
        "securityhealthhost.exe", "smartscreen.exe", "mrt.exe", "wuauclt.exe", "usoclient.exe", "control.exe",
        "systemsettings.exe", "procexp.exe", "procexp64.exe", "autoruns.exe", "autoruns64.exe", "perfmon.exe",
        "resmon.exe", "eventvwr.exe", "services.exe", "lsass.exe", "winlogon.exe", "svchost.exe", "userinit.exe",
        "chrome.exe", "msedge.exe", "firefox.exe", "browser.exe", "opera.exe",
    };

    private static readonly string[] SecurityImageKeywords =
    {
        "kaspersky", "avast", "ekrn", "egui", "nod32", "drweb", "cureit", "mbam", "malwarebytes", "adwcleaner",
        "hitman", "zemana", "spybot", "norton", "mcafee", "bdagent", "bitdefender", "avira", "sophos", "comodo",
        "tdsskiller", "rkill", "combofix", "hijackthis", "superantispyware", "emsisoft", "antivirus", "antimalware",
        "msmp", "defender", "avz",
    };

    // "eset" is a prefix, not a keyword: "systemreset.exe" or "reset.exe" are not ESET.
    private static readonly string[] SecurityImagePrefixes = { "avp", "kav", "kis", "avg", "a2", "360", "eset" };

    public static bool IsSecuritySensitiveImage(string imageName)
    {
        if (SensitiveImages.Contains(imageName))
            return true;
        var lower = imageName.ToLowerInvariant();
        return SecurityImageKeywords.Any(k => lower.Contains(k, StringComparison.Ordinal)) ||
               SecurityImagePrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal));
    }

    public static (SecurityStatus Risk, string Reason) ClassifyIfeo(string imageName, string debugger)
    {
        var dbg = debugger.Trim();
        var dbgLower = dbg.ToLowerInvariant();
        var dbgFile = Path.GetFileName(CommandLineHead(dbg));

        if (imageName.Equals("taskmgr.exe", StringComparison.OrdinalIgnoreCase) &&
            (dbgLower.Contains("procexp") || dbgLower.Contains("processhacker") || dbgLower.Contains("systeminformer")))
        {
            return (SecurityStatus.Warning,
                T.F("Диспетчер задач заменён на {0}. Так бывает, если вы сами включили замену в Process Explorer или Process Hacker.", dbgFile));
        }
        if (dbgLower.Contains("vsjitdebugger"))
        {
            return (SecurityStatus.Warning,
                T._("При запуске программы открывается отладчик Visual Studio. Так бывает у разработчиков; если вы не программист — удалите перехват."));
        }
        if (IsSecuritySensitiveImage(imageName))
        {
            return (SecurityStatus.Danger,
                T.F("Запуск {0} подменён на «{1}». Так вирусы не дают запустить антивирус, Диспетчер задач и средства восстановления.", imageName, dbg));
        }
        return (SecurityStatus.Warning,
            T.F("Вместо {0} запускается «{1}». Если вы не настраивали это сами — удалите перехват.", imageName, dbg));
    }

    private static string CommandLineHead(string cmd)
    {
        var c = cmd.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : c.Trim('"');
        }
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
            return c[..(exe + 4)];
        var space = c.IndexOf(' ');
        return space > 0 ? c[..space] : c;
    }

    public static SecurityCheckResult EvaluateIfeo(IReadOnlyList<IfeoEntry> entries, bool partial, bool readable)
    {
        var why = T._("Вирусы через этот раздел реестра блокируют запуск антивирусов и Диспетчера задач.");
        if (!readable)
        {
            return new SecurityCheckResult(SecurityCheckIds.Ifeo, SecurityStatus.Unknown,
                T._("Не удалось прочитать раздел IFEO"), T._("Нет доступа к реестру.") + " " + why);
        }
        var dangers = entries.Where(e => e.Risk == SecurityStatus.Danger).ToList();
        var warnings = entries.Where(e => e.Risk == SecurityStatus.Warning).ToList();
        var hint = partial ? " " + T._("Часть разделов видна только администратору.") : "";
        if (dangers.Count > 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Ifeo, SecurityStatus.Danger,
                T.F("Перехвачен запуск: {0}", string.Join(", ", dangers.Select(e => e.ImageName).Distinct().Take(3))),
                dangers[0].Reason + hint);
        }
        if (warnings.Count > 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Ifeo, SecurityStatus.Warning,
                T.F("Найден перехват: {0}", string.Join(", ", warnings.Select(e => e.ImageName).Distinct().Take(3))),
                warnings[0].Reason + hint);
        }
        return new SecurityCheckResult(SecurityCheckIds.Ifeo, SecurityStatus.Ok, T._("Перехватов нет"), why + hint);
    }

    // ===================== Proxy =====================

    public static SecurityCheckResult EvaluateProxy(ProxyState p)
    {
        var why = T._("Рекламные программы подменяют прокси, чтобы встраивать рекламу и следить за сайтами. Если прокси включили вы сами (VPN, работа, обход блокировок) — всё в порядке.");
        if (!p.Readable)
        {
            return new SecurityCheckResult(SecurityCheckIds.Proxy, SecurityStatus.Unknown,
                T._("Не удалось прочитать настройки прокси"), T._("Нет доступа к настройкам интернета в реестре."));
        }
        if (!string.IsNullOrWhiteSpace(p.AutoConfigUrl))
        {
            return new SecurityCheckResult(SecurityCheckIds.Proxy, SecurityStatus.Warning,
                T._("Используется скрипт настройки прокси"), T.F("Адрес скрипта: {0}", p.AutoConfigUrl) + "\n" + why);
        }
        if (p.Enabled && !string.IsNullOrWhiteSpace(p.Server))
        {
            return new SecurityCheckResult(SecurityCheckIds.Proxy, SecurityStatus.Warning,
                T.F("Включён прокси {0}", p.Server),
                (IsLocalProxy(p) ? T._("Прокси работает на этом компьютере — обычно это VPN или программа обхода блокировок.") + "\n" : "") + why);
        }
        return new SecurityCheckResult(SecurityCheckIds.Proxy, SecurityStatus.Ok, T._("Прокси не используется"),
            T._("Браузеры подключаются к сайтам напрямую, без посредников."));
    }

    /// <summary>
    /// An enabled proxy that runs on this computer and no setup script: usually a VPN or a program that bypasses blocks has
    /// set it. The server list is split like Windows splits it, at ";" and at whitespace ("http=127.0.0.1:8080 https=[::1]:8080"),
    /// and EVERY host must be "localhost" or a loopback address, compared as a whole: "localhost.evil.example", a list with one
    /// remote host or an entry that does not parse cleanly ("[::1]@evil.example:8080") is a remote proxy in disguise.
    /// A setup script is checked first by <see cref="EvaluateProxy"/> and is never "local".
    /// </summary>
    public static bool IsLocalProxy(ProxyState p)
    {
        if (!p.Enabled || !string.IsNullOrWhiteSpace(p.AutoConfigUrl) || string.IsNullOrWhiteSpace(p.Server))
            return false;
        var hosts = p.Server.Split(ProxyListSeparators, StringSplitOptions.RemoveEmptyEntries).Select(ProxyHost).ToList();
        return hosts.Count > 0 && hosts.All(h => h is not null && IsLoopbackHost(h));
    }

    private static readonly char[] ProxyListSeparators = { ';', ' ', '\t', '\r', '\n' };

    /// <summary>
    /// The host of one entry of ProxyServer: "host:port", "proto=host:port", "scheme://host:port", "[::1]:port" or a bare IPv6
    /// address. Null when the entry is anything else (a port that is not a number, text after "]" or after the port).
    /// </summary>
    private static string? ProxyHost(string entry)
    {
        var e = entry;
        var equals = e.IndexOf('=');
        if (equals >= 0)
        {
            if (!IsWord(e[..equals]))
                return null;
            e = e[(equals + 1)..];
        }
        var scheme = e.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            if (!IsWord(e[..scheme]))
                return null;
            e = e[(scheme + 3)..];
        }
        if (e.StartsWith('['))
        {
            var close = e.IndexOf(']');
            if (close < 0)
                return null;
            var rest = e[(close + 1)..];
            return rest.Length == 0 || IsPort(rest) ? e[1..close] : null;
        }
        // One colon is host:port; more than one is an IPv6 address without a port.
        var colon = e.IndexOf(':');
        if (colon < 0 || colon != e.LastIndexOf(':'))
            return e;
        return IsPort(e[colon..]) ? e[..colon] : null;
    }

    /// <summary>A protocol or scheme name: "http", "https", "socks5".</summary>
    private static bool IsWord(string s) => s.Length > 0 && char.IsAsciiLetter(s[0]) && s.All(char.IsAsciiLetterOrDigit);

    /// <summary>":8080".</summary>
    private static bool IsPort(string s) => s.Length > 1 && s[0] == ':' && s[1..].All(char.IsAsciiDigit);

    private static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
           (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>
    /// The proxy settings are still the ones the user confirmed (ProxyEnable, ProxyServer and AutoConfigURL, exact text).
    /// "Отключить прокси" changes nothing otherwise.
    /// </summary>
    public static bool SameProxySettings(ProxyState current, ProxyState confirmed)
        => current.Readable &&
           current.Enabled == confirmed.Enabled &&
           string.Equals(current.Server, confirmed.Server, StringComparison.Ordinal) &&
           string.Equals(current.AutoConfigUrl, confirmed.AutoConfigUrl, StringComparison.Ordinal);

    // ===================== DNS =====================

    private static readonly Dictionary<string, Func<string>> KnownDns = BuildKnownDns();

    private static Dictionary<string, Func<string>> BuildKnownDns()
    {
        var d = new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(Func<string> label, params string[] ips)
        {
            foreach (var ip in ips)
                d[IPAddress.Parse(ip).ToString()] = label;
        }

        Add(() => "Google Public DNS", "8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844");
        Add(() => "Cloudflare", "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001");
        Add(() => T._("Cloudflare (защита от вредоносных сайтов)"), "1.1.1.2", "1.0.0.2");
        Add(() => T._("Cloudflare (семейный)"), "1.1.1.3", "1.0.0.3");
        Add(() => "Quad9", "9.9.9.9", "149.112.112.112", "9.9.9.10", "9.9.9.11", "149.112.112.11", "2620:fe::fe", "2620:fe::9");
        Add(() => T._("Яндекс DNS"), "77.88.8.8", "77.88.8.1", "2a02:6b8::feed:0ff", "2a02:6b8:0:1::feed:0ff");
        Add(() => T._("Яндекс DNS (безопасный)"), "77.88.8.88", "77.88.8.2");
        Add(() => T._("Яндекс DNS (семейный)"), "77.88.8.7", "77.88.8.3");
        Add(() => "AdGuard DNS", "94.140.14.14", "94.140.15.15", "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff");
        Add(() => T._("AdGuard DNS (семейный)"), "94.140.14.15", "94.140.15.16");
        Add(() => T._("AdGuard DNS (без фильтрации)"), "94.140.14.140", "94.140.14.141");
        Add(() => "OpenDNS", "208.67.222.222", "208.67.220.220", "2620:119:35::35", "2620:119:53::53");
        Add(() => "OpenDNS FamilyShield", "208.67.222.123", "208.67.220.123");
        Add(() => "CleanBrowsing", "185.228.168.9", "185.228.169.9");
        Add(() => "Control D", "76.76.2.0", "76.76.10.0");
        Add(() => "Comodo Secure DNS", "8.26.56.26", "8.20.247.20");
        return d;
    }

    public static (string Label, DnsKind Kind) LabelDns(string address)
    {
        if (!IPAddress.TryParse(address.Trim(), out var ip))
            return (T._("Некорректный адрес"), DnsKind.Unknown);
        if (KnownDns.TryGetValue(ip.ToString(), out var label))
            return (label(), DnsKind.Known);
        if (IPAddress.IsLoopback(ip))
            return (T._("Этот компьютер (локальная программа)"), DnsKind.Private);
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            if (HostsRules.InCidr(ip, "10.0.0.0/8") || HostsRules.InCidr(ip, "172.16.0.0/12") ||
                HostsRules.InCidr(ip, "192.168.0.0/16"))
                return (T._("Роутер или локальная сеть"), DnsKind.Private);
            if (HostsRules.InCidr(ip, "100.64.0.0/10"))
                return (T._("Сеть провайдера"), DnsKind.Private);
            if (HostsRules.InCidr(ip, "169.254.0.0/16"))
                return (T._("Локальный адрес (нет связи с роутером)"), DnsKind.Private);
        }
        else if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal)
        {
            return (T._("Роутер или локальная сеть"), DnsKind.Private);
        }
        return (T._("Неизвестный DNS (возможно, провайдера)"), DnsKind.Unknown);
    }

    public static SecurityCheckResult EvaluateDns(IReadOnlyList<DnsServerInfo> servers, bool readable)
    {
        if (!readable)
        {
            return new SecurityCheckResult(SecurityCheckIds.Dns, SecurityStatus.Unknown,
                T._("Не удалось получить настройки сети"), T._("Служба WMI не ответила."));
        }
        if (servers.Count == 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Dns, SecurityStatus.Ok, T._("DNS не задан"),
                T._("Нет активных подключений с DNS-серверами — возможно, компьютер не в сети."));
        }
        var labels = servers.Select(s => s.Label).Distinct().ToList();
        var unknown = servers.Where(s => s.Kind == DnsKind.Unknown).ToList();
        if (unknown.Count > 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Dns, SecurityStatus.Info,
                T.F("Неизвестный DNS: {0}", string.Join(", ", unknown.Select(s => s.Address).Distinct().Take(2))),
                T._("Обычно это DNS вашего интернет-провайдера — это нормально. Если провайдер ни при чём, DNS мог подменить вирус."));
        }
        return new SecurityCheckResult(SecurityCheckIds.Dns, SecurityStatus.Ok, string.Join(", ", labels.Take(2)),
            T._("Адреса сайтов определяют известные DNS-серверы или ваш роутер — признаков подмены нет."));
    }

    // ===================== Windows Update =====================

    private static readonly string[] QfeFormats =
    {
        "M/d/yyyy", "MM/dd/yyyy", "M/d/yyyy h:mm:ss tt", "M/d/yyyy H:mm:ss", "yyyyMMdd", "yyyy-MM-dd", "dd.MM.yyyy",
    };

    /// <summary>Parses Win32_QuickFixEngineering.InstalledOn ("M/d/yyyy", "yyyyMMdd" or a hex FILETIME).</summary>
    public static DateTime? ParseQfeDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var s = text.Trim();
        if (DateTime.TryParseExact(s, QfeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
            return dt.Date;
        if (s.Length == 16 && long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ft))
        {
            try
            {
                var date = DateTime.FromFileTimeUtc(ft).ToLocalTime();
                return date.Year is > 1995 and < 2200 ? date.Date : null;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
        return null;
    }

    public static SecurityCheckResult EvaluateUpdates(UpdatesState u, DateTime now)
    {
        var count = u.Count > 0 ? " " + T.F("Установлено обновлений: {0}.", Format.Number(u.Count)) : "";
        if (u.ServiceDisabled == true)
        {
            return new SecurityCheckResult(SecurityCheckIds.Updates, SecurityStatus.Warning,
                T._("Служба обновлений отключена"),
                T._("Windows не получает исправления безопасности. Так часто делают «твикеры» и сборки Windows.") + count);
        }
        if (u.LastInstalled is null)
        {
            return new SecurityCheckResult(SecurityCheckIds.Updates, SecurityStatus.Unknown,
                T._("Не удалось узнать дату обновления"), T._("Откройте Центр обновления, чтобы проверить состояние.") + count);
        }
        var days = (int)Math.Floor((now.Date - u.LastInstalled.Value.Date).TotalDays);
        if (days > UpdatesMaxAgeDays)
        {
            return new SecurityCheckResult(SecurityCheckIds.Updates, SecurityStatus.Warning,
                T.F("Обновлений не было {0}", Days(days)),
                T.F("Последнее обновление установлено {0}. Обновления закрывают дыры, через которые заражают компьютеры.", Format.Date(u.LastInstalled)) + count);
        }
        return new SecurityCheckResult(SecurityCheckIds.Updates, SecurityStatus.Ok,
            T.F("Последнее обновление: {0}", AgoText(Math.Max(0, days))),
            T.F("Установлено {0}", Format.Date(u.LastInstalled)) + (u.LastHotFix is null ? "" : $" ({u.LastHotFix})") + "." + count);
    }

    // ===================== SmartScreen / RDP =====================

    public static SecurityCheckResult EvaluateSmartScreen(SmartScreenState s)
    {
        if (s.PolicyValue == 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.SmartScreen, SecurityStatus.Warning,
                T._("SmartScreen отключён политикой"),
                T._("Windows не предупреждает о подозрительных скачанных программах. Политику мог установить «твикер» или вирус."));
        }
        if (string.Equals(s.ExplorerValue, "Off", StringComparison.OrdinalIgnoreCase))
        {
            return new SecurityCheckResult(SecurityCheckIds.SmartScreen, SecurityStatus.Warning, T._("SmartScreen выключен"),
                T._("Windows не предупреждает перед запуском скачанных программ с плохой репутацией. Рекомендуем включить."));
        }
        return new SecurityCheckResult(SecurityCheckIds.SmartScreen, SecurityStatus.Ok, T._("SmartScreen включён"),
            T._("Перед запуском незнакомых скачанных программ Windows покажет предупреждение."));
    }

    public static SecurityCheckResult EvaluateRdp(RdpState r)
    {
        if (r.DenyConnections == 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Rdp, SecurityStatus.Warning, T._("Удалённый рабочий стол включён"),
                T._("К компьютеру можно подключиться по сети. Если вы этим не пользуетесь — отключите: злоумышленники подбирают пароли к RDP."));
        }
        return new SecurityCheckResult(SecurityCheckIds.Rdp, SecurityStatus.Ok, T._("Удалённый рабочий стол выключен"),
            T._("Подключиться к компьютеру по RDP нельзя — так безопаснее."));
    }

    // ===================== Browser extensions =====================

    // l10n:data-begin — words of extension names (Russian and English store names) that the check looks for, never shown
    private static readonly string[] AdwareKeywords =
    {
        "search", "coupon", "shopping", "deals", "discount", "cashback", "price compar", "new tab", "newtab",
        "start page", "startpage", "homepage", "home page", "pdf convert", "converter", "weather", "forecast",
        "maps & directions", "map search", "package track", "speedtest", "speed test", "video download",
        "web companion", "safe search", "secure search", "quick search", "my search",
        "поиск", "купон", "скидк", "кешбэк", "кэшбэк", "покупк", "сравнение цен", "новая вкладка", "стартовая",
        "домашняя страница", "погода", "конвертер", "скачать видео",
    };
    // l10n:data-end

    // l10n:data-begin — the Russian store names of extensions, matched and never shown
    private const string SovetnikName = "советник";
    private const string VisualBookmarksName = "визуальные закладки";
    // l10n:data-end

    /// <summary>
    /// Extensions that are commonly bundled with other software ("в нагрузку"): a part of the name (matched, never shown) and why it
    /// matters (shown, so every reason is a wrapped text outside the data regions).
    /// </summary>
    private static readonly (string Pattern, Func<string> Reason)[] KnownBundledExtensions =
    {
        (SovetnikName, () => T._("«Советник» показывает рекламу товаров поверх сайтов магазинов и часто ставится «в нагрузку» к другим программам.")),
        ("sovetnik", () => T._("«Советник» показывает рекламу товаров поверх сайтов магазинов и часто ставится «в нагрузку» к другим программам.")),
        (VisualBookmarksName, () => T._("Заменяет новую вкладку на страницу Яндекса и часто ставится «в нагрузку» к другим программам.")),
        ("visual bookmarks", () => T._("Заменяет новую вкладку и часто ставится «в нагрузку» к другим программам.")),
        ("mail.ru", () => T._("Расширения Mail.ru часто ставятся «в нагрузку» и меняют поиск и стартовую страницу.")),
        ("search manager", () => T._("Меняет поисковую систему браузера — типичное поведение рекламных программ.")),
    };

    /// <summary>Returns a reason if the extension name looks like adware/PUP, otherwise null.</summary>
    public static string? SuspiciousExtensionReason(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var pup = KnownPups.Match(name, null);
        if (pup is not null)
            return pup;
        foreach (var (pattern, reason) in KnownBundledExtensions)
        {
            if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return reason();
        }
        var lower = name.ToLowerInvariant();
        var kw = AdwareKeywords.FirstOrDefault(k => lower.Contains(k, StringComparison.Ordinal));
        return kw is null
            ? null
            : T.F("Название похоже на рекламное расширение («{0}»): такие часто подменяют поиск и стартовую страницу.", kw);
    }

    public static void ClassifyExtension(BrowserExtension ext)
    {
        var reason = SuspiciousExtensionReason(ext.Name);
        if (reason is not null)
        {
            ext.Risk = ext.Enabled == false ? SecurityStatus.Info : SecurityStatus.Warning;
            ext.Reason = ext.Enabled == false ? reason + " " + T._("Сейчас оно отключено, но лучше удалить.") : reason;
            if (ext.Source == ExtensionSource.OtherProgram)
                ext.Reason += " " + T._("Установлено другой программой, а не вами.");
            return;
        }
        switch (ext.Source)
        {
            case ExtensionSource.Policy:
                ext.Risk = SecurityStatus.Warning;
                ext.Reason = T._("Установлено принудительно через политику — удалить его можно только вместе с политикой. На домашнем компьютере это частый признак рекламной программы.");
                break;
            case ExtensionSource.OtherProgram:
                ext.Risk = SecurityStatus.Info;
                ext.Reason = T._("Установлено другой программой, а не вами из магазина расширений.");
                break;
            case ExtensionSource.Unpacked:
                ext.Risk = SecurityStatus.Info;
                ext.Reason = T._("Распакованное расширение (режим разработчика).");
                break;
            case ExtensionSource.Store:
                ext.Risk = SecurityStatus.Ok;
                ext.Reason = T._("Установлено из магазина расширений.");
                break;
            default:
                ext.Risk = SecurityStatus.Ok;
                ext.Reason = T._("Признаков рекламного расширения нет.");
                break;
        }
    }

    public static SecurityCheckResult EvaluateExtensions(IReadOnlyList<BrowserExtension> list, int browsers)
    {
        var why = T._("Удаляются расширения в самом браузере — нажмите «Как удалить».");
        if (browsers < 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Extensions, SecurityStatus.Unknown,
                T._("Не удалось прочитать расширения"), T._("Папки профилей браузеров недоступны. Проверьте расширения в самом браузере."));
        }
        if (browsers == 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Extensions, SecurityStatus.Ok, T._("Браузеры не найдены"),
                T._("Поддерживаются Chrome, Edge, Яндекс Браузер, Opera, Brave, Vivaldi и Firefox."));
        }
        var bad = list.Where(e => e.Risk == SecurityStatus.Warning).ToList();
        if (bad.Count > 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Extensions, SecurityStatus.Warning,
                T.F("Подозрительных: {0} из {1}", bad.Count, list.Count),
                T.F("Например, «{0}» ({1}).", bad[0].Name, bad[0].Browser) + " " + why);
        }
        if (list.Count == 0)
        {
            return new SecurityCheckResult(SecurityCheckIds.Extensions, SecurityStatus.Ok, T._("Расширений нет"),
                T._("В браузерах не установлено ни одного расширения."));
        }
        return new SecurityCheckResult(SecurityCheckIds.Extensions, SecurityStatus.Ok,
            T.F("{0}, подозрительных нет", Format.Count(list.Count, "расширение", "расширения", "расширений")),
            T._("Расширения видят открытые страницы — ставьте только те, которым доверяете."));
    }

    // ===================== Overall score =====================

    public static int Score(IEnumerable<SecurityCheckResult> checks)
    {
        var score = 100;
        foreach (var c in checks)
        {
            var critical = c.Id is SecurityCheckIds.Antivirus or SecurityCheckIds.Winlogon or SecurityCheckIds.Ifeo;
            score -= c.Status switch
            {
                SecurityStatus.Danger => critical ? 30 : 20,
                SecurityStatus.Warning => critical ? 10 : 6,
                _ => 0,
            };
        }
        return Math.Clamp(score, 0, 100);
    }
}
