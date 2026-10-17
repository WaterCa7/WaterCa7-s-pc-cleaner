using System.Globalization;
using PcCleaner.Core;

namespace PcCleaner.Modules.Security;

/// <summary>
/// Device and network-protocol checks: SMBv1, Secure Boot, TPM, drive encryption, memory integrity, several antiviruses.
/// Pure logic (no system access).
/// </summary>
public static partial class SecurityRules
{
    private const string AdminForExactCheck = "Для точной проверки нужны права администратора.";

    // ===================== Windows features =====================

    /// <summary>Win32_OptionalFeature.InstallState: 1 = enabled, 2 = disabled, 3 = absent, anything else = unknown.</summary>
    public static FeatureState MapInstallState(long? installState) => installState switch
    {
        1 => FeatureState.Enabled,
        2 => FeatureState.Disabled,
        3 => FeatureState.Absent,
        _ => FeatureState.Unknown,
    };

    /// <summary>Get-WindowsOptionalFeature State ("Enabled", "EnablePending", "Disabled", "DisabledWithPayloadRemoved"...).</summary>
    public static FeatureState MapFeatureStateName(string? state)
    {
        var s = (state ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "enabled" or "enablepending" => FeatureState.Enabled,
            "disabled" or "disablepending" => FeatureState.Disabled,
            "disabledwithpayloadremoved" or "absent" => FeatureState.Absent,
            _ => FeatureState.Unknown,
        };
    }

    /// <summary>Parses "SMB1Protocol=Enabled" lines (one feature per line) into a name → state map.</summary>
    public static Dictionary<string, FeatureState> ParseFeatureList(string output)
    {
        var result = new Dictionary<string, FeatureState>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            result[line[..eq].Trim()] = MapFeatureStateName(line[(eq + 1)..]);
        }
        return result;
    }

    // ===================== SMBv1 =====================

    /// <summary>
    /// Which SMBv1 parts are really on. The Windows feature list is exact; the registry alone is a best guess.
    /// A part that is installed but switched off in the registry (driver Start = 4, server SMB1 = 0) counts as off.
    /// </summary>
    public static (bool? Client, bool? Server, bool Exact) ResolveSmb1(Smb1State s)
    {
        var r = s.Registry;
        if (s.Feature != FeatureState.Unknown)
        {
            if (s.Feature != FeatureState.Enabled)
                return (false, false, true);
            // Parent feature on: the sub-features tell which part is installed (older Windows has no sub-features).
            var clientInstalled = s.Client is FeatureState.Enabled or FeatureState.Unknown;
            var serverInstalled = s.Server is FeatureState.Enabled or FeatureState.Unknown;
            var client = clientInstalled && !(r.Readable && r.ClientDriverStart == 4);
            var server = serverInstalled && !(r.Readable && (r.ServerSmb1 == 0 || r.ServerDriverStart == 4));
            return (client, server, true);
        }
        if (!r.Readable)
            return (null, null, false);
        var clientOn = r.ClientDriverStart is not null and not 4;
        var serverOn = r.ServerDriverStart is not null and not 4 && r.ServerSmb1 != 0;
        return (clientOn, serverOn, false);
    }

    /// <param name="rebootPending">SMBv1 was disabled by this app in this session and Windows waits for a reboot.</param>
    public static SecurityCheckResult EvaluateSmb1(Smb1State s, bool isAdmin, bool rebootPending = false)
    {
        if (rebootPending)
        {
            // Whatever the feature list says right now, the change is made and only the reboot finishes it.
            return new SecurityCheckResult(SecurityCheckIds.Smb1, SecurityStatus.Info, "SMBv1 отключится после перезагрузки",
                "Компонент уже отключён, но старый драйвер работает до перезагрузки. Перезагрузите компьютер, когда будет удобно.");
        }
        var (client, server, exact) = ResolveSmb1(s);
        if (client is null && server is null)
        {
            return new SecurityCheckResult(SecurityCheckIds.Smb1, SecurityStatus.Unknown, "Не удалось проверить SMBv1",
                "Нет доступа к списку компонентов Windows и к реестру." + (isAdmin ? "" : " " + AdminForExactCheck));
        }
        var hint = exact ? "" : isAdmin ? "\nСостояние определено по реестру." : "\n" + AdminForExactCheck;
        if (server == true)
        {
            return new SecurityCheckResult(SecurityCheckIds.Smb1, SecurityStatus.Danger,
                client == true ? "Включён устаревший протокол SMBv1" : "Включён сервер SMBv1",
                "Через уязвимости SMBv1 в 2017 году распространялся вирус-вымогатель WannaCry: он сам заражал компьютеры " +
                "по сети. Современным программам и устройствам SMBv1 не нужен — его стоит отключить." + hint);
        }
        if (client == true)
        {
            return new SecurityCheckResult(SecurityCheckIds.Smb1, SecurityStatus.Warning, "Включён клиент SMBv1",
                "Компьютер может подключаться к сетевым папкам по устаревшему протоколу, через который распространялся " +
                "WannaCry. Он нужен разве что очень старым NAS и роутерам с USB-диском — если таких нет, отключите его." + hint);
        }
        return new SecurityCheckResult(SecurityCheckIds.Smb1, SecurityStatus.Ok, "SMBv1 выключен",
            "Устаревший сетевой протокол, через который распространялся WannaCry, отключён — так и должно быть." + hint);
    }

    // ===================== Secure Boot =====================

    public static SecurityCheckResult EvaluateSecureBoot(SecureBootState s)
    {
        if (!s.Readable)
        {
            return new SecurityCheckResult(SecurityCheckIds.SecureBoot, SecurityStatus.Unknown,
                "Не удалось проверить Secure Boot", "Нет доступа к разделу реестра с состоянием безопасной загрузки.");
        }
        return s.Enabled switch
        {
            1 => new SecurityCheckResult(SecurityCheckIds.SecureBoot, SecurityStatus.Ok, "Secure Boot включён",
                "Безопасная загрузка не даёт буткитам — вирусам, которые стартуют раньше Windows, — встроиться в загрузку."),
            0 => new SecurityCheckResult(SecurityCheckIds.SecureBoot, SecurityStatus.Warning, "Secure Boot выключен",
                "Включается в BIOS/UEFI: раздел Boot или Security → Secure Boot. Защищает от буткитов, а ещё его требуют " +
                "Windows 11 и античиты — например, Riot Vanguard (Valorant) и FACEIT."),
            _ when s.IsUefi == false => new SecurityCheckResult(SecurityCheckIds.SecureBoot, SecurityStatus.Info,
                "Загрузка в режиме Legacy BIOS",
                "В этом режиме Secure Boot недоступен. Чтобы его включить, загрузку нужно перевести в режим UEFI " +
                "(с преобразованием диска в GPT) — это сложная операция, её лучше доверить специалисту."),
            _ => new SecurityCheckResult(SecurityCheckIds.SecureBoot, SecurityStatus.Info, "Secure Boot не поддерживается",
                "Прошивка компьютера не сообщает о безопасной загрузке. Так бывает на старых компьютерах и в режиме " +
                "совместимости (CSM / Legacy BIOS)."),
        };
    }

    /// <summary>Plain-language steps for the "Как включить" button of the Secure Boot card.</summary>
    public const string SecureBootHowTo =
        "Secure Boot включается в настройках BIOS/UEFI:\n\n" +
        "1. Перезагрузите компьютер и сразу нажимайте Delete или F2 (на ноутбуках бывает F1, F10, F12 или Esc).\n" +
        "2. Откройте раздел Boot или Security и найдите пункт Secure Boot.\n" +
        "3. Выберите Enabled. Если пункт недоступен, сначала выключите CSM (Compatibility Support Module) и выберите загрузку в режиме UEFI.\n" +
        "4. Сохраните настройки (обычно F10) и перезагрузитесь.\n\n" +
        "Важно: если Windows установлена в режиме Legacy BIOS (диск MBR), после выключения CSM она не загрузится. " +
        "Тогда верните CSM обратно — диск сначала нужно преобразовать в GPT. Если не уверены, обратитесь к специалисту.\n\n" +
        "После перезагрузки нажмите «Проверить снова».";

    // ===================== TPM =====================

    /// <summary>"2.0, 0, 1.38" (Win32_Tpm.SpecVersion) or "2.0" → "2.0"; null for empty or unexpected text.</summary>
    public static string? NormalizeTpmVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var first = text.Split(',')[0].Trim();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? first : null;
    }

    /// <summary>"True"/"False", "Да"/"Нет", "Истина"/"Ложь", "Yes"/"No"; null for anything else.</summary>
    public static bool? ParseYesNo(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "да" or "истина" => true,
        "false" or "no" or "нет" or "ложь" => false,
        _ => null,
    };

    /// <summary>
    /// Parses "tpmtool getdeviceinformation" (English or Russian Windows):
    /// "-TPM Present: True" / "-Наличие TPM: Да", "-TPM Version: 2.0" / "-Версия TPM: 2.0".
    /// </summary>
    public static TpmState ParseTpmTool(string? output)
    {
        bool? present = null;
        string? version = null;
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '–', ' ', '\t');
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (present is null && key.Contains("tpm") &&
                (key.Contains("present") || key.Contains("наличие") || key.Contains("присутств")))
            {
                present = ParseYesNo(value);
            }
            else if (version is null && (key.Contains("tpm version") || key.Contains("версия tpm")))
            {
                version = NormalizeTpmVersion(value);
            }
        }
        return new TpmState(present, null, null, version);
    }

    /// <summary>TBS_E_TPM_NOT_FOUND: Windows sees no TPM (absent or switched off in the firmware).</summary>
    public const uint TbsTpmNotFound = 0x8028400F;

    /// <summary>
    /// Tbsi_GetDeviceInfo result: 0 = found (tpmVersion 1 = TPM 1.2, 2 = TPM 2.0), TBS_E_TPM_NOT_FOUND = no TPM,
    /// anything else (service stopped or disabled) = unknown.
    /// </summary>
    public static TpmState TpmFromTbs(uint result, uint tpmVersion) => result switch
    {
        0 => new TpmState(true, null, null, tpmVersion switch { 1 => "1.2", 2 => "2.0", _ => null }),
        TbsTpmNotFound => new TpmState(false, null, null, null),
        _ => TpmState.Unknown,
    };

    public static SecurityCheckResult EvaluateTpm(TpmState t, bool isAdmin)
    {
        const string why = "Он нужен для Windows 11, BitLocker и Windows Hello.";
        if (t.Present is null)
        {
            return new SecurityCheckResult(SecurityCheckIds.Tpm, SecurityStatus.Unknown, "Не удалось проверить TPM",
                isAdmin
                    ? "Windows не ответила на запрос о модуле TPM. Посмотрите «Безопасность Windows» → «Безопасность устройства»."
                    : "Для проверки нужны права администратора. Состояние модуля видно и в «Безопасность Windows» → «Безопасность устройства».");
        }
        if (t.Present == false)
        {
            return new SecurityCheckResult(SecurityCheckIds.Tpm, SecurityStatus.Warning, "TPM не найден",
                "Модуля нет или он выключен в BIOS/UEFI. Обычно его можно включить: на процессорах AMD пункт называется " +
                "fTPM, на Intel — PTT. " + why);
        }
        if (t.Enabled == false || t.Activated == false)
        {
            return new SecurityCheckResult(SecurityCheckIds.Tpm, SecurityStatus.Warning, "TPM выключен",
                "Модуль есть, но отключён. Включите его в BIOS/UEFI (fTPM на AMD, PTT на Intel). " + why);
        }
        var major = t.Version is not null && double.TryParse(t.Version, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : (double?)null;
        if (major is < 2)
        {
            return new SecurityCheckResult(SecurityCheckIds.Tpm, SecurityStatus.Info, $"TPM {t.Version} — старая версия",
                "Модуль работает, но Windows 11 требует TPM 2.0. Иногда версию можно обновить прошивкой с сайта производителя.");
        }
        return new SecurityCheckResult(SecurityCheckIds.Tpm, SecurityStatus.Ok,
            t.Version is null ? "TPM работает" : $"TPM {t.Version} работает",
            "Модуль безопасности хранит ключи шифрования и данные Windows Hello. " + why);
    }

    /// <summary>Plain-language steps for the "Как включить" button of the TPM card.</summary>
    public const string TpmHowTo =
        "Модуль TPM включается в настройках BIOS/UEFI (войти — клавишей Delete или F2 сразу после включения компьютера):\n\n" +
        "• Процессоры AMD — пункт AMD fTPM (Firmware TPM), обычно в разделе Advanced → CPU Configuration или Security.\n" +
        "• Процессоры Intel — пункт Intel PTT (Platform Trust Technology), обычно в разделе Security или Advanced.\n\n" +
        "Выберите Enabled, сохраните настройки (обычно F10) и перезагрузитесь. Затем нажмите «Проверить снова».\n\n" +
        "Если такого пункта нет, на материнской плате может быть разъём для отдельного модуля TPM — это уточняют в инструкции к плате.";

    // ===================== Drive encryption =====================

    /// <summary>
    /// Shell property System.Volume.BitLockerProtection: 1 = on, 2 = off, 3 = encrypting, 4 = decrypting,
    /// 5 = suspended, 6 = on (locked), 8 = waiting for activation, 0 / empty = not supported.
    /// </summary>
    public static DriveEncryption MapShellBitLocker(object? value)
    {
        if (value is null || value is DBNull)
            return DriveEncryption.NotSupported;
        int n;
        try
        {
            n = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return string.IsNullOrWhiteSpace(value.ToString()) ? DriveEncryption.NotSupported : DriveEncryption.Unknown;
        }
        return n switch
        {
            0 => DriveEncryption.NotSupported,
            1 => DriveEncryption.On,
            2 => DriveEncryption.Off,
            3 => DriveEncryption.Encrypting,
            4 => DriveEncryption.Decrypting,
            5 => DriveEncryption.Suspended,
            6 => DriveEncryption.Locked,
            8 => DriveEncryption.Pending,
            _ => DriveEncryption.Unknown,
        };
    }

    /// <summary>
    /// Win32_EncryptableVolume: ProtectionStatus 0 = unprotected, 1 = protected, 2 = unknown (locked);
    /// ConversionStatus 0 = decrypted, 1 = encrypted, 2/4 = encryption in progress/paused, 3/5 = decryption.
    /// </summary>
    public static DriveEncryption MapWmiBitLocker(long? protection, long? conversion)
    {
        if (protection is null)
            return DriveEncryption.Unknown;
        if (protection == 2)
            return DriveEncryption.Locked;
        if (conversion is 2 or 4)
            return DriveEncryption.Encrypting;
        if (conversion is 3 or 5)
            return DriveEncryption.Decrypting;
        if (protection == 1)
            return DriveEncryption.On;
        // Unprotected: fully encrypted with the key in the clear = suspended; otherwise simply off.
        return conversion == 1 ? DriveEncryption.Suspended : DriveEncryption.Off;
    }

    private static readonly HashSet<int> PortableChassis = new() { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };
    private static readonly HashSet<int> DesktopChassis = new() { 3, 4, 5, 6, 7, 13, 15, 16, 17, 23, 24, 35, 36 };

    /// <summary>
    /// A laptop/tablet: a portable chassis type, or a battery in a computer that does not say it is a desktop
    /// (a desktop with a USB UPS also reports a battery).
    /// </summary>
    public static bool IsLaptop(int batteryCount, IReadOnlyCollection<int> chassisTypes)
    {
        if (chassisTypes.Any(PortableChassis.Contains))
            return true;
        return batteryCount > 0 && !chassisTypes.Any(DesktopChassis.Contains);
    }

    /// <summary>EditionID "Core", "CoreSingleLanguage", "CoreN", "CoreCountrySpecific" = Windows Home.</summary>
    public static bool IsHomeEdition(string? editionId)
        => editionId is not null && editionId.StartsWith("Core", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when an unprotected laptop drive should be reported on the dashboard.</summary>
    public static bool LaptopDriveUnprotected(BitLockerState b)
        => b.IsLaptop && b.Encryption is DriveEncryption.Off or DriveEncryption.Suspended or DriveEncryption.Pending;

    public static SecurityCheckResult EvaluateBitLocker(BitLockerState b)
    {
        var laptop = b.IsLaptop;
        var notProtected = laptop ? SecurityStatus.Warning : SecurityStatus.Info;
        return b.Encryption switch
        {
            DriveEncryption.On or DriveEncryption.Locked => new SecurityCheckResult(SecurityCheckIds.BitLocker, SecurityStatus.Ok,
                "Системный диск зашифрован",
                $"Диск {b.Drive} защищён шифрованием: без пароля или ключа восстановления данные не прочитать, даже если " +
                (laptop ? "ноутбук украдут." : "диск вынут из компьютера.")),
            DriveEncryption.Encrypting => new SecurityCheckResult(SecurityCheckIds.BitLocker, SecurityStatus.Ok,
                "Идёт шифрование системного диска",
                "Пользоваться компьютером можно как обычно — шифрование закончится само."),
            DriveEncryption.Decrypting => new SecurityCheckResult(SecurityCheckIds.BitLocker, SecurityStatus.Info,
                "Идёт расшифровка системного диска",
                "Когда она закончится, данные на диске больше не будут защищены шифрованием."),
            DriveEncryption.Suspended => new SecurityCheckResult(SecurityCheckIds.BitLocker, notProtected,
                "Защита BitLocker приостановлена",
                "Диск зашифрован, но ключ сейчас хранится открыто — так бывает во время обновления BIOS или Windows. " +
                "Если защита не возобновилась сама после перезагрузки, включите её в параметрах шифрования."),
            DriveEncryption.Pending => new SecurityCheckResult(SecurityCheckIds.BitLocker, notProtected,
                "Шифрование устройства не завершено",
                "Диск подготовлен к шифрованию, но ключ ещё не защищён. Войдите в Windows с учётной записью Microsoft " +
                "или включите шифрование в параметрах — тогда ключ восстановления сохранится в вашей учётной записи."),
            DriveEncryption.Off when laptop => new SecurityCheckResult(SecurityCheckIds.BitLocker, SecurityStatus.Warning,
                "Диск ноутбука не зашифрован",
                "Если ноутбук потеряют или украдут, файлы можно прочитать, просто подключив диск к другому компьютеру. " +
                "Шифрование (BitLocker или «Шифрование устройства») это предотвращает."),
            DriveEncryption.Off => new SecurityCheckResult(SecurityCheckIds.BitLocker, SecurityStatus.Info,
                "Системный диск не зашифрован",
                "Для настольного компьютера это не обязательно. Шифрование защищает данные, если компьютер или диск украдут."),
            DriveEncryption.NotSupported => new SecurityCheckResult(SecurityCheckIds.BitLocker, SecurityStatus.Info,
                "Шифрование диска недоступно",
                b.IsHomeEdition
                    ? "В домашней редакции Windows нет BitLocker, а «Шифрование устройства» этот компьютер не поддерживает."
                    : "Windows не сообщает о поддержке шифрования для системного диска."),
            _ => new SecurityCheckResult(SecurityCheckIds.BitLocker, SecurityStatus.Unknown,
                "Не удалось проверить шифрование диска",
                "Windows не ответила на запрос. Состояние шифрования видно в «Параметрах» → «Конфиденциальность и защита»."),
        };
    }

    // ===================== Memory integrity (HVCI) =====================

    public static SecurityCheckResult EvaluateMemoryIntegrity(DeviceGuardState d)
    {
        if (d.HvciRunning)
        {
            return new SecurityCheckResult(SecurityCheckIds.MemoryIntegrity, SecurityStatus.Ok, "Целостность памяти включена",
                "Изоляция ядра не даёт вредоносным программам и уязвимым драйверам внедриться в ядро Windows.");
        }
        if (!d.Available && d.HvciRegistryEnabled is null)
        {
            return new SecurityCheckResult(SecurityCheckIds.MemoryIntegrity, SecurityStatus.Unknown,
                "Не удалось проверить изоляцию ядра",
                "Windows не ответила на запрос. Состояние видно в «Безопасность Windows» → «Безопасность устройства».");
        }
        if (d.HvciConfigured)
        {
            return new SecurityCheckResult(SecurityCheckIds.MemoryIntegrity, SecurityStatus.Info,
                "Целостность памяти включится после перезагрузки",
                "Защита уже включена в настройках и заработает после перезагрузки компьютера. Если и после перезагрузки " +
                "она не работает, проверьте, включена ли в BIOS/UEFI виртуализация (Intel VT-x или AMD SVM).");
        }
        return new SecurityCheckResult(SecurityCheckIds.MemoryIntegrity, SecurityStatus.Info, "Целостность памяти выключена",
            "Защищает ядро Windows от вредоносных и уязвимых драйверов. Включается в «Безопасности Windows» → " +
            "«Безопасность устройства» → «Изоляция ядра». В отдельных играх может немного снизить FPS.");
    }

    // ===================== Several antiviruses / Defender threats =====================

    /// <summary>
    /// Antiviruses that protect in real time right now: every enabled third-party product, plus Microsoft Defender
    /// if it is still active next to them (normally it switches to passive mode by itself).
    /// </summary>
    public static IReadOnlyList<string> ActiveAntivirusNames(IReadOnlyList<SecurityProduct> products, DefenderStatus? defender)
    {
        var names = products
            .Where(p => !p.IsMicrosoft && p.Enabled && !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => p.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var defenderActive = products.Any(p => p.IsMicrosoft && p.Enabled) &&
                             defender is { RealTimeEnabled: true } && !defender.IsPassive;
        if (names.Count > 0 && defenderActive)
            names.Insert(0, "Защитник Windows");
        return names;
    }

    public static bool HasMultipleAntiviruses(IReadOnlyList<SecurityProduct> products, DefenderStatus? defender)
        => ActiveAntivirusNames(products, defender).Count >= 2;

    /// <summary>"Обезвредить угрозы" is offered only when Defender works and has detections it did not neutralize.</summary>
    public static bool CanRemoveThreats(DefenderStatus? defender, IReadOnlyList<ThreatDetection> threats)
        => defender is { ServiceEnabled: true } && threats.Any(t => t.IsActive);

    /// <summary>
    /// Result of "Обезвредить угрозы" from the active-threat counts before and after Remove-MpThreat.
    /// Only "all gone" is a success: when some threats remain, the dashboard must not mark the issue as fixed.
    /// </summary>
    public static SecurityActionResult ThreatRemovalOutcome(int before, int after, string? error)
    {
        var removed = Math.Max(0, before - after);
        if (after <= 0)
        {
            return SecurityActionResult.Ok(
                $"Угрозы обезврежены: {before}. Защитник Windows поместил их в карантин или удалил.\n\n" +
                "Если какой-то файл нужен и вы уверены в нём, восстановите его в «Безопасность Windows» → «Журнал защиты».");
        }
        if (removed > 0)
        {
            return SecurityActionResult.Fail(
                $"Не удалось обезвредить все угрозы: обезврежено {removed} из {before}. С остальными Защитник Windows не справился " +
                "сам — откройте «Безопасность Windows» → «Журнал защиты» и выберите действие вручную, а затем запустите полную проверку.");
        }
        return SecurityActionResult.Fail(
            "Не удалось обезвредить угрозы" + (string.IsNullOrWhiteSpace(error) ? "" : $": {error.Trim().TrimEnd('.')}") +
            ". Откройте «Безопасность Windows» → «Журнал защиты» и выберите действие вручную — или запустите полную проверку.");
    }

    // ===================== Misc =====================

    /// <summary>WinINet per-connection flags for "no proxy": direct, keeping "automatic detection" as the user had it.</summary>
    public static int DirectProxyFlags(int currentFlags) => 0x1 | (currentFlags & 0x8);

    /// <summary>Result text of a successful "Отключить SMBv1" (on the page and on the dashboard).</summary>
    public static string Smb1DisabledText(bool restartNeeded, RestorePointResult restorePoint)
        => (restartNeeded
               ? "SMBv1 отключён. Перезагрузите компьютер, чтобы изменение вступило в силу."
               : "SMBv1 отключён.") +
           "\n\n" + RestorePointText(restorePoint) +
           "\n\nЕсли перестанет открываться старое сетевое хранилище (NAS), включить SMBv1 обратно можно в «Включение или " +
           "отключение компонентов Windows» → «Поддержка общего доступа к файлам SMB 1.0/CIFS».";

    /// <summary>One sentence about the restore point made before a change.</summary>
    public static string RestorePointText(RestorePointResult result) => result switch
    {
        RestorePointResult.Created => "Перед изменением создана точка восстановления.",
        RestorePointResult.RecentPointExists => "Свежая точка восстановления уже есть — Windows создала её меньше суток назад.",
        RestorePointResult.DisabledInSettings => "Точка восстановления не создавалась: это отключено в настройках программы.",
        RestorePointResult.NotElevated => "Точка восстановления не создана: нужны права администратора.",
        _ => "Точку восстановления создать не удалось (возможно, защита системы выключена).",
    };
}
