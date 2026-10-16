using PcCleaner.Core;

namespace PcCleaner.Modules.Optimizer;

/// <summary>
/// Pure rules: (hardware, settings, now) -> recommendations. No I/O, no system access — fully unit-testable.
/// Items with <see cref="RecommendationKind.Good"/> are checks that passed (shown in the "already fine" list).
/// </summary>
public static class RecommendationEngine
{
    /// <summary>XMP rule fires only when the rated speed is more than 10% above the configured speed.</summary>
    public const double MemorySpeedTolerance = 0.10;

    public const int HighMemoryLoadPercent = 85;

    public static readonly TimeSpan DriverMaxAge = TimeSpan.FromDays(183);
    public static readonly TimeSpan BiosMaxAge = TimeSpan.FromDays(365 * 3);
    public static readonly TimeSpan UptimeLimit = TimeSpan.FromDays(7);

    private const string DisplaySettingsUri = "ms-settings:display-advanced";
    private const string GameModeUri = "ms-settings:gaming-gamemode";
    private const string CapturesUri = "ms-settings:gaming-gamedvr";
    private const string GraphicsUri = "ms-settings:display-advancedgraphics";
    private const string PowerUri = "ms-settings:powersleep";
    private const string UpdateUri = "ms-settings:windowsupdate";
    private const string ColorsUri = "ms-settings:colors";
    private const string PerformanceOptions = "SystemPropertiesPerformance.exe";

    public static List<Recommendation> Evaluate(HardwareSnapshot hw, SettingsSnapshot s, DateTime now)
    {
        var list = new List<Recommendation>();
        AddMemorySpeed(list, hw);
        AddMemoryChannels(list, hw);
        AddMemoryAmount(list, hw);
        AddMemoryLoad(list, s);
        AddSystemDisk(list, hw);
        AddRefreshRate(list, hw);
        AddMonitorOnIntegratedGpu(list, hw);
        AddGpuDrivers(list, hw, now);
        AddBios(list, hw, now);
        AddPowerPlan(list, hw, s);
        AddGameMode(list, s);
        AddGameDvr(list, s);
        AddHags(list, hw, s);
        AddVisualEffects(list, hw, s);
        AddTransparency(list, hw, s);
        AddPagefile(list, hw, s);
        AddRestart(list, s);
        AddMaintenanceLinks(list);
        return Sort(list);
    }

    /// <summary>Good items last; then by impact (Info after Low), auto-fixable first, then severity.</summary>
    public static List<Recommendation> Sort(IEnumerable<Recommendation> items) => items
        .OrderBy(r => r.Kind == RecommendationKind.Good ? 1 : 0)
        .ThenByDescending(r => r.Kind == RecommendationKind.Info ? 0 : (int)r.Impact)
        .ThenBy(r => r.Kind switch
        {
            RecommendationKind.AutoFix => 0,
            RecommendationKind.Manual => 1,
            RecommendationKind.Navigate => 2,
            RecommendationKind.Info => 3,
            _ => 4,
        })
        .ThenByDescending(r => r.Severity)
        .ToList();

    /// <summary>Weak PC: less than 8 GB RAM or 4 logical processors or fewer.</summary>
    public static bool IsWeakPc(HardwareSnapshot hw) => RamBelow(hw, 8) || hw.Cpu is { LogicalProcessors: > 0 and <= 4 };

    /// <summary>Installed RAM below N GB (0.5 GB tolerance for memory reserved by hardware).</summary>
    public static bool RamBelow(HardwareSnapshot hw, int gb) => hw.TotalRamBytes > 0 && hw.TotalRamBytes < (gb - 0.5) * HardwareText.GiB;

    private static IssueSeverity SeverityOf(RecommendationImpact impact) => impact switch
    {
        RecommendationImpact.High => IssueSeverity.High,
        RecommendationImpact.Medium => IssueSeverity.Medium,
        _ => IssueSeverity.Low,
    };

    private static Recommendation Good(string id, string title, string? current, RecommendationCategory category, string? fixId = null)
        => new()
        {
            Id = "Ok:" + id,
            Title = title,
            CurrentValue = current,
            Category = category,
            Kind = RecommendationKind.Good,
            Impact = RecommendationImpact.Low,
            Severity = IssueSeverity.Info,
            FixId = fixId,
        };

    // ===================== Memory =====================

    private static string[] XmpSteps(HardwareSnapshot hw)
    {
        var amd = hw.Cpu?.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) == true;
        return new[]
        {
            "Перезагрузите компьютер и сразу нажимайте Delete (или F2), чтобы открыть BIOS.",
            amd
                ? "Найдите пункт EXPO, DOCP или XMP — обычно он на главном экране BIOS или в разделе разгона (Ai Tweaker, OC, Extreme Tweaker)."
                : "Найдите пункт XMP (Extreme Memory Profile) — обычно он на главном экране BIOS или в разделе разгона (Ai Tweaker, OC, Tweaker).",
            "Выберите «Profile 1» и сохраните настройки клавишей F10.",
            "Если после этого компьютер не запускается — подождите: многие платы сами сбрасывают неудачные настройки после 2–3 попыток. " +
            "Иначе сбросьте BIOS (Clear CMOS) или выберите профиль с меньшей частотой.",
        };
    }

    /// <summary>
    /// Rated (slowest module) and configured (MT/s) memory speed, or null when Windows does not report them.
    /// Some firmware reports the real clock instead of the transfer rate (1600 for DDR4-3200); that is corrected,
    /// but only when the reported value is below anything the memory type really runs at — so a DDR4-4400 kit
    /// stuck at its JEDEC 2133 is still recognised as slow.
    /// </summary>
    public static (int Rated, int Configured, string? Type)? MemorySpeed(HardwareSnapshot hw)
    {
        var mods = hw.RamModules.Where(m => m.RatedMhz > 0 && m.ConfiguredMhz > 0).ToList();
        if (mods.Count == 0)
            return null;
        var rated = mods.Min(m => m.RatedMhz);
        var configured = mods.Max(m => m.ConfiguredMhz);
        var type = mods.Select(m => HardwareText.MemoryTypeName(m.MemoryType)).FirstOrDefault(t => t is not null);
        var lowestReal = type switch
        {
            "DDR3" or "LPDDR3" => 1066,
            "DDR4" or "LPDDR4" => 2133,
            "DDR5" or "LPDDR5" => 4400,
            _ => int.MaxValue, // unknown type: rely on the ratio alone
        };
        if (configured < lowestReal && configured * 2 >= rated * 0.95 && configured * 2 <= rated * 1.05)
            configured *= 2;
        return (rated, configured, type);
    }

    /// <summary>True when the memory runs noticeably (more than 10%) below its rated speed.</summary>
    public static bool IsMemoryBelowRated(int rated, int configured) => rated > configured * (1 + MemorySpeedTolerance);

    private static void AddMemorySpeed(List<Recommendation> list, HardwareSnapshot hw)
    {
        if (MemorySpeed(hw) is not { } speed)
            return;
        var (rated, configured, type) = speed;

        if (IsMemoryBelowRated(rated, configured))
        {
            if (hw.IsLaptop)
            {
                list.Add(new Recommendation
                {
                    Id = "RamSpeed",
                    Title = $"Память работает на {configured} МГц вместо {rated} МГц",
                    Explanation = $"Модули поддерживают {rated} МГц, но в ноутбуках частоту памяти обычно ограничивает процессор " +
                                  "или производитель, а включить XMP в BIOS ноутбука, как правило, нельзя. Это нормально — менять ничего не нужно.",
                    Category = RecommendationCategory.Hardware,
                    Kind = RecommendationKind.Info,
                    Impact = RecommendationImpact.Low,
                    Severity = IssueSeverity.Info,
                    CurrentValue = $"{configured} МГц из {rated} МГц",
                });
                return;
            }
            list.Add(new Recommendation
            {
                Id = "RamSpeed",
                Title = $"Память работает на {configured} МГц вместо {rated} МГц — включите XMP/EXPO/DOCP в BIOS",
                Explanation = $"Модули рассчитаны на {rated} МГц, но сейчас работают медленнее. Обычно так бывает, когда в BIOS не включён " +
                              "профиль разгона памяти XMP (у плат для AMD — EXPO или DOCP). Включение профиля бесплатно ускоряет память, " +
                              "а с ней — игры и тяжёлые программы.",
                Category = RecommendationCategory.Hardware,
                Kind = RecommendationKind.Manual,
                Impact = RecommendationImpact.High,
                Severity = IssueSeverity.High,
                CurrentValue = $"{configured} МГц из {rated} МГц",
                Gain = "Быстрее игры и тяжёлые программы",
                Steps = XmpSteps(hw),
            });
            return;
        }

        // Windows often reports only the JEDEC base speed, so a module at 2133/2400 (DDR4) or 4800 (DDR5) may still have XMP off.
        var baseSpeed = !hw.IsLaptop && ((type == "DDR4" && configured <= 2400) || (type == "DDR5" && configured <= 4800));
        if (baseSpeed)
        {
            list.Add(new Recommendation
            {
                Id = "RamBaseSpeed",
                Title = $"Память работает на базовой частоте {configured} МГц",
                Explanation = $"Это стандартная частота для {type}. Многие модули рассчитаны на большую (например, 3200–3600 МГц для DDR4 " +
                              "или 6000 МГц для DDR5) — она указана на наклейке модуля. Windows сообщает не всегда точно, поэтому " +
                              "проверьте: если модули быстрее, включите профиль XMP/EXPO в BIOS.",
                Category = RecommendationCategory.Hardware,
                Kind = RecommendationKind.Info,
                Impact = RecommendationImpact.Low,
                Severity = IssueSeverity.Info,
                CurrentValue = $"{configured} МГц",
                Steps = XmpSteps(hw),
            });
            return;
        }

        list.Add(Good("RamSpeed", "Память работает на паспортной частоте", $"{configured} МГц", RecommendationCategory.Hardware));
    }

    private static void AddMemoryChannels(List<Recommendation> list, HardwareSnapshot hw)
    {
        var mods = hw.RamModules.Where(m => m.CapacityBytes > 0).ToList();
        if (mods.Count == 0)
            return;

        if (mods.Count == 1)
        {
            if (hw.RamSlots == 1 || HardwareText.IsSolderedLowPowerMemory(mods[0].MemoryType))
                return; // nothing can be added / soldered LPDDR is multi-channel by design
            var slots = hw.RamSlots > 1 ? $", слотов на плате: {hw.RamSlots}" : "";
            list.Add(new Recommendation
            {
                Id = "RamSingle",
                Title = "Память работает в одноканальном режиме",
                Explanation = $"Установлен всего один модуль памяти ({HardwareText.Gb(mods[0].CapacityBytes)}). С двумя одинаковыми модулями " +
                              "память работает в двухканальном режиме и её пропускная способность почти удваивается — это заметно " +
                              "в играх и особенно на встроенной графике." +
                              (hw.IsLaptop ? " В некоторых ноутбуках свободного слота нет — проверьте это в характеристиках модели." : ""),
                Category = RecommendationCategory.Hardware,
                Kind = RecommendationKind.Manual,
                Impact = hw.IsLaptop ? RecommendationImpact.Low : RecommendationImpact.Medium,
                Severity = hw.IsLaptop ? IssueSeverity.Low : IssueSeverity.Medium,
                CurrentValue = $"1 модуль{slots}",
                Gain = "Выше скорость памяти",
                Steps = new[]
                {
                    $"Купите второй модуль такого же объёма и частоты ({HardwareText.Gb(mods[0].CapacityBytes)}, {HardwareText.MemoryTypeName(mods[0].MemoryType) ?? "того же типа"}, " +
                    $"{(mods[0].RatedMhz > 0 ? HardwareText.Mhz(mods[0].RatedMhz) : "та же частота")}). Лучше всего — такой же модели.",
                    hw.IsLaptop
                        ? "Установите его в свободный слот (как это сделать, написано в инструкции к ноутбуку)."
                        : "Установите модули в слоты, рекомендованные в инструкции к материнской плате (обычно A2 и B2 — второй и четвёртый от процессора).",
                    "Откройте этот раздел снова и убедитесь, что видны оба модуля.",
                },
            });
            return;
        }

        if (mods.Any(m => m.Channel is null))
            return; // slot names do not tell the channel
        var channels = mods.GroupBy(m => m.Channel!).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();

        if (channels.Count == 1)
        {
            if (mods.Any(m => HardwareText.IsSolderedLowPowerMemory(m.MemoryType)))
                return; // soldered memory cannot be moved to other slots
            var both = mods.Count == 2 ? "оба модуля" : $"все {Format.Count(mods.Count, "модуль", "модуля", "модулей")}";
            list.Add(new Recommendation
            {
                Id = "RamOneChannel",
                Title = "Все модули памяти стоят в одном канале",
                Explanation = $"Похоже, {both} установлены в слоты канала {channels[0].Key}. Тогда память работает в одноканальном режиме " +
                              "и заметно медленнее, чем могла бы. Обычно модули ставят через один слот — так включается двухканальный режим.",
                Category = RecommendationCategory.Hardware,
                Kind = RecommendationKind.Manual,
                Impact = RecommendationImpact.Medium,
                Severity = IssueSeverity.Medium,
                CurrentValue = string.Join(", ", mods.Select(m => m.DeviceLocator ?? m.BankLabel ?? "?")),
                Gain = "Выше скорость памяти",
                Steps = new[]
                {
                    "Выключите компьютер и отключите его от розетки.",
                    "Посмотрите в инструкции к материнской плате, в какие слоты ставить модули (для двух модулей обычно A2 и B2).",
                    "Переставьте модули, включите компьютер и проверьте этот раздел ещё раз.",
                },
            });
            return;
        }

        if (channels.Count == 2)
        {
            var a = channels[0].Sum(m => m.CapacityBytes);
            var b = channels[1].Sum(m => m.CapacityBytes);
            if (a != b)
            {
                var dual = 2 * Math.Min(a, b);
                var single = a + b - dual;
                if (BalancingSwap(channels[0].ToList(), channels[1].ToList()) is { } swap &&
                    !mods.Any(m => HardwareText.IsSolderedLowPowerMemory(m.MemoryType)))
                {
                    // E.g. 8 + 8 GB in channel A and 16 + 16 GB in channel B: swapping one pair gives 24 GB per channel for free.
                    var half = HardwareText.Gb((a + b) / 2);
                    var combo = string.Join(" + ", channels[0].Where(m => !ReferenceEquals(m, swap.FromA)).Append(swap.FromB)
                        .Select(m => m.CapacityBytes).OrderBy(c => c).Select(HardwareText.Gb));
                    list.Add(new Recommendation
                    {
                        Id = "RamAsymmetric",
                        Title = "Переставьте модули памяти — часть памяти работает в одноканальном режиме",
                        Explanation = $"В каналах установлен разный объём памяти (канал {channels[0].Key}: {HardwareText.Gb(a)}, " +
                                      $"канал {channels[1].Key}: {HardwareText.Gb(b)}), поэтому {HardwareText.Gb(dual)} работают в быстром " +
                                      $"двухканальном режиме, а оставшиеся {HardwareText.Gb(single)} — в одноканальном. Если поменять местами " +
                                      $"два модуля, в каждом канале будет по {half} ({combo}) и вся память заработает в двухканальном режиме. " +
                                      "Это бесплатно и занимает несколько минут.",
                        Category = RecommendationCategory.Hardware,
                        Kind = RecommendationKind.Manual,
                        Impact = RecommendationImpact.Low,
                        Severity = IssueSeverity.Low,
                        CurrentValue = $"Канал {channels[0].Key}: {HardwareText.Gb(a)} · канал {channels[1].Key}: {HardwareText.Gb(b)}",
                        Gain = "Вся память в двухканальном режиме",
                        Steps = new[]
                        {
                            "Выключите компьютер, отключите его от розетки и снимите боковую крышку корпуса.",
                            $"Поменяйте местами один модуль на {HardwareText.Gb(swap.FromA.CapacityBytes)} из канала {channels[0].Key} " +
                            $"и один модуль на {HardwareText.Gb(swap.FromB.CapacityBytes)} из канала {channels[1].Key} (Windows называет " +
                            $"их слоты «{SlotName(swap.FromA)}» и «{SlotName(swap.FromB)}»). Слоты одного канала обычно стоят рядом и " +
                            "подписаны на плате буквой канала (A1, A2, B1, B2); точная схема — в инструкции к материнской плате.",
                            $"Включите компьютер и откройте этот раздел снова: в каналах должно быть по {half}.",
                        },
                    });
                    return;
                }
                list.Add(new Recommendation
                {
                    Id = "RamAsymmetric",
                    Title = "Часть памяти работает в одноканальном режиме",
                    Explanation = $"В каналах установлен разный объём памяти (канал {channels[0].Key}: {HardwareText.Gb(a)}, " +
                                  $"канал {channels[1].Key}: {HardwareText.Gb(b)}). {HardwareText.Gb(dual)} работают в быстром двухканальном режиме, " +
                                  $"а оставшиеся {HardwareText.Gb(single)} — в одноканальном. На практике разница невелика; " +
                                  "для максимальной скорости ставьте одинаковые модули парами.",
                    Category = RecommendationCategory.Hardware,
                    Kind = RecommendationKind.Info,
                    Impact = RecommendationImpact.Low,
                    Severity = IssueSeverity.Info,
                    CurrentValue = $"Канал {channels[0].Key}: {HardwareText.Gb(a)} · канал {channels[1].Key}: {HardwareText.Gb(b)}",
                });
                return;
            }
        }

        list.Add(Good("RamChannels",
            channels.Count == 2 ? "Память работает в двухканальном режиме" : "Память работает в многоканальном режиме",
            Format.Count(mods.Count, "модуль", "модуля", "модулей") + " в " + Format.Count(channels.Count, "канале", "каналах", "каналах"),
            RecommendationCategory.Hardware));
    }

    private static string SlotName(RamModule m) => m.DeviceLocator ?? m.BankLabel ?? "?";

    /// <summary>
    /// One module from channel A and one from channel B whose swap makes both channels hold the same amount of memory
    /// (module counts per channel stay the same), or null when a single swap cannot balance them.
    /// </summary>
    internal static (RamModule FromA, RamModule FromB)? BalancingSwap(IReadOnlyList<RamModule> channelA, IReadOnlyList<RamModule> channelB)
    {
        var diff = channelA.Sum(m => m.CapacityBytes) - channelB.Sum(m => m.CapacityBytes);
        if (diff == 0 || diff % 2 != 0)
            return null;
        foreach (var x in channelA)
        {
            foreach (var y in channelB)
            {
                if (x.CapacityBytes - y.CapacityBytes == diff / 2)
                    return (x, y);
            }
        }
        return null;
    }

    private static void AddMemoryAmount(List<Recommendation> list, HardwareSnapshot hw)
    {
        var total = hw.TotalRamBytes;
        if (total <= 0)
            return;
        var gb = HardwareText.Gb(total);
        if (RamBelow(hw, 8))
        {
            list.Add(new Recommendation
            {
                Id = "RamLow",
                Title = $"Мало оперативной памяти: {gb}",
                Explanation = "Для Windows 10/11 с браузером и мессенджерами нужно хотя бы 8 ГБ, для игр — 16 ГБ. Когда памяти не хватает, " +
                              "Windows сбрасывает данные на диск, и компьютер начинает «тормозить». Добавить память — один из самых заметных " +
                              "и недорогих апгрейдов.",
                Category = RecommendationCategory.Hardware,
                Kind = RecommendationKind.Manual,
                Impact = RecommendationImpact.High,
                Severity = IssueSeverity.High,
                CurrentValue = gb,
                Gain = "Меньше «тормозов» при работе нескольких программ",
                Steps = new[]
                {
                    "Посмотрите в карточке «Оперативная память» выше тип памяти (DDR4, DDR5…) и количество свободных слотов.",
                    "Купите модуль того же типа и частоты — лучше такого же объёма, как уже установленный, чтобы работал двухканальный режим.",
                    "Если свободных слотов нет, замените модули на более ёмкие (например, 2 × 8 ГБ).",
                },
            });
        }
        else if (RamBelow(hw, 16))
        {
            list.Add(new Recommendation
            {
                Id = "RamGaming",
                Title = $"{gb} памяти — маловато для современных игр",
                Explanation = "Для офиса и браузера этого хватает, но современным играм желательно 16 ГБ: иначе возможны подтормаживания " +
                              "и долгие загрузки, особенно если параллельно открыт браузер или Discord.",
                Category = RecommendationCategory.Games,
                Kind = RecommendationKind.Info,
                Impact = RecommendationImpact.Low,
                Severity = IssueSeverity.Info,
                CurrentValue = gb,
            });
        }
        else
        {
            list.Add(Good("RamAmount", "Оперативной памяти достаточно", gb, RecommendationCategory.Hardware));
        }
    }

    private static void AddMemoryLoad(List<Recommendation> list, SettingsSnapshot s)
    {
        if (s.MemoryTotalBytes <= 0 || s.MemoryLoadPercent < HighMemoryLoadPercent)
            return;
        list.Add(new Recommendation
        {
            Id = "RamLoad",
            Title = $"Оперативная память почти заполнена ({s.MemoryLoadPercent}%)",
            Explanation = $"Сейчас занято {Format.Bytes(s.MemoryUsedBytes)} из {Format.Bytes(s.MemoryTotalBytes)}. Когда свободной памяти мало, " +
                          "Windows начинает активно использовать диск, и всё замедляется. Посмотрите, какие программы занимают больше всего " +
                          "памяти, и закройте лишние.",
            Category = RecommendationCategory.Maintenance,
            Kind = RecommendationKind.Manual,
            Impact = RecommendationImpact.Medium,
            Severity = IssueSeverity.Medium,
            CurrentValue = $"Занято {s.MemoryLoadPercent}%",
            Gain = "Освободится память",
            NavigateKey = "Processes",
            NavigateLabel = "Открыть «Процессы»",
        });
    }

    // ===================== Disk / display / GPU =====================

    private static void AddSystemDisk(List<Recommendation> list, HardwareSnapshot hw)
    {
        var disk = hw.SystemDisk;
        if (disk is null)
            return;
        var model = string.IsNullOrWhiteSpace(disk.Model) ? "системный диск" : disk.Model;
        if (disk.MediaKind == DiskMediaKind.Hdd)
        {
            list.Add(new Recommendation
            {
                Id = "SystemHdd",
                Title = "Установите SSD: Windows работает с жёсткого диска (HDD)",
                Explanation = $"Системный диск {disk.DriveLetter}: ({model}) — механический. Это самая частая причина медленного включения " +
                              "компьютера и долгого запуска программ. Переход на SSD ускоряет загрузку Windows в несколько раз — " +
                              "это самый заметный апгрейд для старого ПК.",
                Category = RecommendationCategory.Hardware,
                Kind = RecommendationKind.Manual,
                Impact = RecommendationImpact.High,
                Severity = IssueSeverity.High,
                CurrentValue = $"HDD · {model}",
                Gain = "Загрузка Windows и программ в разы быстрее",
                Steps = new[]
                {
                    hw.IsLaptop
                        ? "Узнайте, какой SSD подходит вашему ноутбуку (2,5\" SATA или M.2 NVMe) — это есть в характеристиках модели."
                        : "Купите SSD объёмом от 500 ГБ: M.2 NVMe, если на материнской плате есть слот M.2, иначе 2,5\" SATA.",
                    "Перенесите систему программой клонирования от производителя SSD (например, Samsung Data Migration, Acronis для WD и Crucial) " +
                    "или установите Windows на SSD заново.",
                    "В BIOS выберите SSD первым в порядке загрузки (Boot Priority). Старый HDD можно оставить для файлов.",
                },
            });
        }
        else if (disk.MediaKind == DiskMediaKind.Ssd)
        {
            list.Add(Good("SystemSsd", "Windows установлена на SSD", model, RecommendationCategory.Hardware));
        }
    }

    private static string MonitorLabel(DisplayInfo d) => string.IsNullOrWhiteSpace(d.MonitorName) ? "Монитор" : d.MonitorName!;

    /// <summary>
    /// The monitor offers a noticeably higher refresh rate at the current resolution (at least +10 Hz and +10%:
    /// 60 -> 75, 144 -> 165; but not 59 -> 60 or 165 -> 175).
    /// </summary>
    public static bool IsRefreshBelowMax(DisplayInfo d)
        => d.CurrentHz > 1 && d.MaxHzAtCurrentResolution >= d.CurrentHz + 10 && d.MaxHzAtCurrentResolution >= d.CurrentHz * 1.10;

    private static void AddRefreshRate(List<Recommendation> list, HardwareSnapshot hw)
    {
        var fine = new List<DisplayInfo>();
        foreach (var d in hw.Displays.Where(d => d.CurrentHz > 1))
        {
            var max = d.MaxHzAtCurrentResolution;
            if (IsRefreshBelowMax(d))
            {
                var name = MonitorLabel(d);
                // 60 -> 144 is a night-and-day difference; 144 -> 165 is nice to have.
                var big = max >= d.CurrentHz * 1.25;
                var impact = !big ? RecommendationImpact.Low : hw.IsLaptop ? RecommendationImpact.Medium : RecommendationImpact.High;
                list.Add(new Recommendation
                {
                    Id = "Refresh:" + d.DeviceName,
                    Title = $"Монитор поддерживает {max} Гц, а работает на {d.CurrentHz} Гц",
                    Explanation = $"Сейчас изображение на мониторе «{name}» обновляется {d.CurrentHz} раз в секунду, хотя он умеет {max}. " +
                                  (big
                                      ? "Более высокая частота делает движение мыши, прокрутку и игры заметно плавнее. "
                                      : "Разница небольшая, но в динамичных играх движение станет чуть плавнее. ") +
                                  "Переключение безопасно и делается в параметрах Windows за минуту." +
                                  (hw.IsLaptop ? " На ноутбуке высокая частота немного быстрее расходует батарею." : ""),
                    Category = RecommendationCategory.Games,
                    Kind = RecommendationKind.Manual,
                    Impact = impact,
                    Severity = SeverityOf(impact),
                    CurrentValue = $"{d.CurrentHz} Гц при {d.Width}×{d.Height}",
                    Gain = "Плавнее изображение",
                    SettingsUri = DisplaySettingsUri,
                    SettingsLabel = "Открыть параметры дисплея",
                    Steps = new[]
                    {
                        "Нажмите «Открыть параметры дисплея» (Параметры → Система → Дисплей → Расширенные параметры дисплея).",
                        hw.Displays.Count > 1 ? $"Вверху выберите монитор «{name}»." : "Убедитесь, что выбран нужный монитор.",
                        $"В списке «Выберите частоту обновления» выберите {max} Гц.",
                        "Если нужной частоты нет — проверьте кабель: для высоких частот нужен DisplayPort или HDMI 2.0 и новее. " +
                        "Также частоту можно выбрать в панели управления видеокарты.",
                    },
                });
            }
            else
            {
                fine.Add(d);
            }
        }
        if (fine.Count > 0)
        {
            list.Add(Good("Refresh",
                fine.Count == 1 ? "Монитор работает на максимальной частоте" : "Мониторы работают на максимальной частоте",
                string.Join(", ", fine.Select(d => fine.Count == 1 ? $"{d.CurrentHz} Гц" : $"{MonitorLabel(d)}: {d.CurrentHz} Гц")),
                RecommendationCategory.Games));
        }
    }

    private static void AddMonitorOnIntegratedGpu(List<Recommendation> list, HardwareSnapshot hw)
    {
        if (hw.IsLaptop)
            return; // laptops route the picture through the integrated GPU by design
        var discrete = hw.RealGpus.Where(GpuClassifier.IsDiscrete).ToList();
        if (discrete.Count == 0)
            return;
        var primary = hw.Displays.FirstOrDefault(d => d.IsPrimary) ?? hw.Displays.FirstOrDefault();
        if (primary?.AdapterName is not { Length: > 0 } adapter || !GpuClassifier.IsIntegrated(adapter))
            return;
        if (discrete.Any(g => string.Equals(g.Name, adapter, StringComparison.OrdinalIgnoreCase)))
            return;
        var dgpu = HardwareText.CleanName(discrete[0].Name);
        list.Add(new Recommendation
        {
            Id = "MonitorOnIgpu",
            Title = "Монитор подключён к материнской плате, а не к видеокарте",
            Explanation = $"Основной монитор работает через встроенную графику ({HardwareText.CleanName(adapter)}), хотя в компьютере есть " +
                          $"видеокарта {dgpu}. Игры при этом могут работать на слабой встроенной графике или терять производительность.",
            Category = RecommendationCategory.Games,
            Kind = RecommendationKind.Manual,
            Impact = RecommendationImpact.High,
            Severity = IssueSeverity.High,
            CurrentValue = $"Подключён к {HardwareText.CleanName(adapter)}",
            Gain = "Игры пойдут на видеокарте",
            Steps = new[]
            {
                "Выключите компьютер.",
                "Сзади системного блока переключите кабель монитора из разъёма материнской платы (вверху, рядом с USB) " +
                "в разъём видеокарты (ниже, в горизонтальном ряду).",
                "Включите компьютер — изображение появится через видеокарту.",
            },
        });
    }

    private static void AddGpuDrivers(List<Recommendation> list, HardwareSnapshot hw, DateTime now)
    {
        var fresh = new List<GpuInfo>();
        var hasDiscrete = hw.RealGpus.Any(GpuClassifier.IsDiscrete);
        foreach (var gpu in hw.RealGpus)
        {
            var name = HardwareText.CleanName(gpu.Name);
            var url = GpuClassifier.DriverUrl(gpu.Vendor);
            var vendor = GpuClassifier.VendorName(gpu.Vendor);
            if (gpu.IsBasicDriver)
            {
                list.Add(new Recommendation
                {
                    Id = "GpuNoDriver:" + (gpu.PnpDeviceId ?? gpu.Name),
                    Title = "Не установлен драйвер видеокарты",
                    Explanation = "Windows использует стандартный «базовый видеоадаптер»: нет аппаратного ускорения, игры и видео " +
                                  "работают очень медленно или не запускаются, может быть недоступна нужная частота монитора. " +
                                  "Установите драйвер с официального сайта производителя видеокарты.",
                    Category = RecommendationCategory.Hardware,
                    Kind = RecommendationKind.Manual,
                    Impact = RecommendationImpact.High,
                    Severity = IssueSeverity.High,
                    CurrentValue = name,
                    Gain = "Заработает ускорение графики",
                    SettingsUri = url ?? UpdateUri,
                    SettingsLabel = url is null ? "Открыть Центр обновления" : $"Сайт {vendor}",
                    Steps = new[]
                    {
                        url is null
                            ? "Откройте Центр обновления Windows и установите все обновления, включая необязательные (там бывают драйверы)."
                            : $"Откройте сайт {vendor} кнопкой ниже и скачайте драйвер для своей видеокарты и Windows.",
                        "Запустите установщик и следуйте подсказкам, затем перезагрузите компьютер.",
                    },
                });
                continue;
            }
            if (gpu.DriverDate is not { } date)
                continue;
            if (now - date > DriverMaxAge)
            {
                var minor = gpu.IsIntegrated && hasDiscrete;
                var age = HardwareText.AgeText(date, now);
                var version = gpu.Vendor == GpuVendor.Nvidia && HardwareText.NvidiaDriverVersion(gpu.DriverVersion) is { } nv
                    ? nv
                    : gpu.DriverVersion;
                var steps = new List<string>
                {
                    url is null ? "Найдите драйвер на сайте производителя видеокарты." : $"Откройте сайт {vendor} кнопкой «Сайт {vendor}».",
                    $"Выберите свою модель ({name}) и версию Windows, скачайте и запустите установщик.",
                };
                if (gpu.Vendor == GpuVendor.Nvidia)
                    steps.Add("Удобнее всего — приложение NVIDIA App: оно само следит за обновлениями драйвера.");
                else if (gpu.Vendor == GpuVendor.Amd)
                    steps.Add("Удобнее всего — AMD Software: Adrenalin Edition: оно само следит за обновлениями драйвера.");
                if (hw.IsLaptop)
                    steps.Add("Если универсальный драйвер не ставится, скачайте драйвер с сайта производителя ноутбука.");
                steps.Add("После установки перезагрузите компьютер.");
                list.Add(new Recommendation
                {
                    Id = "GpuDriver:" + gpu.Name,
                    Title = $"Обновите драйвер видеокарты {name}",
                    Explanation = $"Установленному драйверу уже {age} (версия {version} от {Format.Date(date)}). Новые драйверы исправляют " +
                                  "ошибки и часто ускоряют свежие игры. Скачивайте драйвер только с официального сайта производителя.",
                    Category = RecommendationCategory.Hardware,
                    Kind = RecommendationKind.Manual,
                    Impact = minor ? RecommendationImpact.Low : RecommendationImpact.Medium,
                    Severity = minor ? IssueSeverity.Low : IssueSeverity.Medium,
                    CurrentValue = $"{version} от {Format.Date(date)}",
                    Gain = "Исправления и ускорение в играх",
                    SettingsUri = url ?? "https://www.google.com/search?q=" + Uri.EscapeDataString(name + " драйвер"),
                    SettingsLabel = url is null ? "Найти драйвер" : $"Сайт {vendor}",
                    Steps = steps,
                });
            }
            else
            {
                fresh.Add(gpu);
            }
        }
        if (fresh.Count > 0)
        {
            list.Add(Good("GpuDriver",
                fresh.Count == 1 ? "Драйвер видеокарты свежий" : "Драйверы видеокарт свежие",
                string.Join(", ", fresh.Select(g => $"{HardwareText.CleanName(g.Name)}: {Format.Date(g.DriverDate)}")),
                RecommendationCategory.Hardware));
        }
    }

    private static void AddBios(List<Recommendation> list, HardwareSnapshot hw, DateTime now)
    {
        if (hw.BiosDate is not { } date || now - date <= BiosMaxAge)
            return;
        var board = string.Join(" ", new[] { hw.BoardManufacturer, hw.BoardProduct }.Where(x => !string.IsNullOrWhiteSpace(x)));
        list.Add(new Recommendation
        {
            Id = "BiosOld",
            Title = $"Версия BIOS материнской платы вышла в {date.Year} году",
            Explanation = "Возможно, для вашей платы уже есть более новая версия. Обновления BIOS исправляют ошибки, улучшают " +
                          "совместимость с памятью и новыми процессорами и закрывают уязвимости. " +
                          "Но прошивка BIOS — рискованная операция: обновляйте его, только если есть проблемы или нужна поддержка нового " +
                          "железа, и строго по инструкции с сайта производителя платы.",
            Category = RecommendationCategory.Hardware,
            Kind = RecommendationKind.Info,
            Impact = RecommendationImpact.Low,
            Severity = IssueSeverity.Info,
            CurrentValue = $"Версия {hw.BiosVersion ?? "—"} от {Format.Date(date)}",
            SettingsUri = board.Length > 0 ? "https://www.google.com/search?q=" + Uri.EscapeDataString(board + " BIOS") : null,
            SettingsLabel = board.Length > 0 ? "Найти обновление" : null,
            Steps = board.Length > 0
                ? new[]
                {
                    $"Найдите страницу своей платы ({board}) на официальном сайте производителя, раздел «Support» → «BIOS».",
                    "Сравните версии: если новая исправляет то, что вам мешает, скачайте её и прочитайте инструкцию.",
                    "Во время обновления не выключайте компьютер; для ноутбука подключите зарядку.",
                }
                : Array.Empty<string>(),
        });
    }

    // ===================== Windows settings =====================

    private static void AddPowerPlan(List<Recommendation> list, HardwareSnapshot hw, SettingsSnapshot s)
    {
        var active = s.ActivePlan;
        if (active is null)
            return;
        var kind = active.Kind;
        var current = PowerPlans.DisplayName(active);
        PowerPlan? Find(PowerPlanKind k) => s.Plans.FirstOrDefault(p => p.Kind == k);

        if (!hw.IsLaptop)
        {
            if (kind is PowerPlanKind.HighPerformance or PowerPlanKind.UltimatePerformance)
            {
                list.Add(Good("PowerPlan", "Включён производительный план электропитания", current, RecommendationCategory.Windows,
                    TweakIds.PowerPlan));
                return;
            }
            if (kind is not (PowerPlanKind.Balanced or PowerPlanKind.PowerSaver))
                return; // vendor plan (e.g. "AMD Ryzen Balanced") — tuned by the manufacturer, leave it alone

            var saver = kind == PowerPlanKind.PowerSaver;
            var impact = saver ? RecommendationImpact.High : RecommendationImpact.Medium;
            var why = saver
                ? "План «Экономия энергии» ограничивает частоту процессора — компьютер работает заметно медленнее, чем может. " +
                  "На настольном ПК экономия электричества копеечная, а потеря скорости ощутимая."
                : $"Сейчас активен план «{current}»: в простое процессор снижает частоту и разгоняется с небольшой задержкой. " +
                  "Производительный план держит процессор наготове — система откликается быстрее, а частота кадров в играх стабильнее. " +
                  "Для настольного ПК это безопасно, расход электричества вырастет немного.";
            var target = Find(PowerPlanKind.UltimatePerformance) ?? Find(PowerPlanKind.HighPerformance);
            if (target is not null)
            {
                var targetName = PowerPlans.DisplayName(target);
                list.Add(new Recommendation
                {
                    Id = "PowerPlan",
                    Title = $"Включите план электропитания «{targetName}»",
                    Explanation = why,
                    Category = RecommendationCategory.Windows,
                    Kind = RecommendationKind.AutoFix,
                    Impact = impact,
                    Severity = SeverityOf(impact),
                    CurrentValue = current,
                    Gain = "Быстрее отклик и стабильнее частоты процессора",
                    FixId = TweakIds.PowerPlan,
                    FixArgument = target.Guid,
                    ApplySummary = $"• Будет включён план электропитания «{targetName}» (сейчас «{current}»).\n" +
                                   $"• Команда: powercfg /setactive {target.Guid}",
                    IsSafe = true,
                    SettingsUri = "powercfg.cpl",
                    SettingsLabel = "Электропитание",
                });
            }
            else
            {
                list.Add(new Recommendation
                {
                    Id = "PowerPlan",
                    Title = "Включите режим максимальной производительности",
                    Explanation = why + " Производительного плана в списке нет, но в Windows 11 можно выбрать режим питания.",
                    Category = RecommendationCategory.Windows,
                    Kind = RecommendationKind.Manual,
                    Impact = impact,
                    Severity = SeverityOf(impact),
                    CurrentValue = current,
                    Gain = "Быстрее отклик системы",
                    SettingsUri = PowerUri,
                    SettingsLabel = "Открыть параметры питания",
                    Steps = new[]
                    {
                        "Нажмите «Открыть параметры питания» (Параметры → Система → Питание).",
                        "В пункте «Режим питания» выберите «Максимальная производительность».",
                        "В Windows 10 откройте «Панель управления → Электропитание» и выберите «Высокая производительность».",
                    },
                });
            }
            return;
        }

        // Laptops: Balanced is the right default; High performance only makes sense on AC power.
        if (kind == PowerPlanKind.PowerSaver)
        {
            var balanced = Find(PowerPlanKind.Balanced);
            if (balanced is null)
                return;
            list.Add(new Recommendation
            {
                Id = "PowerPlan",
                Title = "Включите сбалансированный план электропитания",
                Explanation = "План «Экономия энергии» сильно ограничивает процессор — ноутбук работает медленнее. " +
                              "«Сбалансированный» план сам снижает частоты в простое и экономит батарею почти так же хорошо, " +
                              "но не мешает работе.",
                Category = RecommendationCategory.Windows,
                Kind = RecommendationKind.AutoFix,
                Impact = RecommendationImpact.Medium,
                Severity = IssueSeverity.Medium,
                CurrentValue = current,
                Gain = "Ноутбук работает быстрее",
                FixId = TweakIds.PowerPlan,
                FixArgument = balanced.Guid,
                ApplySummary = $"• Будет включён план электропитания «{PowerPlans.DisplayName(balanced)}» (сейчас «{current}»).\n" +
                               $"• Команда: powercfg /setactive {balanced.Guid}",
                SettingsUri = "powercfg.cpl",
                SettingsLabel = "Электропитание",
            });
            return;
        }
        if ((kind is PowerPlanKind.HighPerformance or PowerPlanKind.UltimatePerformance) && s.OnAcPower == false)
        {
            var balanced = Find(PowerPlanKind.Balanced);
            if (balanced is null)
                return;
            list.Add(new Recommendation
            {
                Id = "PowerPlan",
                Title = "Ноутбук работает от батареи в режиме максимальной производительности",
                Explanation = $"План «{current}» не даёт процессору экономить энергию, поэтому батарея садится заметно быстрее. " +
                              "Для работы от батареи лучше «Сбалансированный» план, а производительный включайте, когда ноутбук на зарядке.",
                Category = RecommendationCategory.Windows,
                Kind = RecommendationKind.AutoFix,
                Impact = RecommendationImpact.Low,
                Severity = IssueSeverity.Low,
                CurrentValue = current + ", от батареи",
                Gain = "Дольше работа от батареи",
                FixId = TweakIds.PowerPlan,
                FixArgument = balanced.Guid,
                ApplySummary = $"• Будет включён план электропитания «{PowerPlans.DisplayName(balanced)}» (сейчас «{current}»).\n" +
                               $"• Команда: powercfg /setactive {balanced.Guid}",
                SettingsUri = "powercfg.cpl",
                SettingsLabel = "Электропитание",
            });
            return;
        }
        if (kind == PowerPlanKind.Balanced && s.OnAcPower == true)
        {
            list.Add(new Recommendation
            {
                Id = "PowerPlanLaptopAc",
                Title = "Для игр на зарядке можно включить максимальную производительность",
                Explanation = "Сбалансированный план — правильный выбор для ноутбука. Но когда ноутбук подключён к сети и вы играете " +
                              "или работаете в тяжёлых программах, режим максимальной производительности может прибавить скорости. " +
                              "Не забудьте вернуть обычный режим при работе от батареи.",
                Category = RecommendationCategory.Windows,
                Kind = RecommendationKind.Info,
                Impact = RecommendationImpact.Low,
                Severity = IssueSeverity.Info,
                CurrentValue = current + ", от сети",
                SettingsUri = PowerUri,
                SettingsLabel = "Открыть параметры питания",
                Steps = new[]
                {
                    "Нажмите «Открыть параметры питания» (Параметры → Система → Питание и батарея).",
                    "В пункте «Режим питания» выберите «Максимальная производительность» — Windows запомнит его отдельно для работы от сети.",
                },
            });
            return;
        }
        if (kind is PowerPlanKind.Balanced or PowerPlanKind.HighPerformance or PowerPlanKind.UltimatePerformance)
            list.Add(Good("PowerPlan", "План электропитания подходит для ноутбука", current, RecommendationCategory.Windows, TweakIds.PowerPlan));
    }

    private static void AddGameMode(List<Recommendation> list, SettingsSnapshot s)
    {
        if (s.GameModeEnabled)
        {
            list.Add(Good("GameMode", "Игровой режим Windows включён", null, RecommendationCategory.Games, TweakIds.GameMode));
            return;
        }
        list.Add(new Recommendation
        {
            Id = "GameMode",
            Title = "Включите игровой режим Windows",
            Explanation = "Игровой режим отдаёт приоритет запущенной игре: Windows не устанавливает обновления драйверов и не показывает " +
                          "уведомления о перезагрузке во время игры, а фоновые задачи получают меньше ресурсов. Это стандартная " +
                          "функция Windows — включать её безопасно.",
            Category = RecommendationCategory.Games,
            Kind = RecommendationKind.AutoFix,
            Impact = RecommendationImpact.Medium,
            Severity = IssueSeverity.Medium,
            CurrentValue = "Выключен",
            Gain = "Больше ресурсов для игр",
            FixId = TweakIds.GameMode,
            ApplySummary = "• Будет включён игровой режим Windows.\n" + RegistryTweaks.Describe(RegistryTweaks.GameMode),
            IsSafe = true,
            SettingsUri = GameModeUri,
            SettingsLabel = "Открыть параметры",
        });
    }

    private static void AddGameDvr(List<Recommendation> list, SettingsSnapshot s)
    {
        if (!s.GameDvrActive)
        {
            list.Add(Good("GameDvr", "Запись игр Xbox Game Bar отключена", null, RecommendationCategory.Games, TweakIds.GameDvr));
            return;
        }
        // "Record what happened" (HistoricalCaptureEnabled, off by default) records the last minutes of every game all the time:
        // that is what costs FPS. With it off, Game DVR is only on standby, so the gain is small — say so honestly.
        var background = s.HistoricalCaptureValue is { } h && h != 0;
        var impact = background ? RecommendationImpact.Medium : RecommendationImpact.Low;
        list.Add(new Recommendation
        {
            Id = "GameDvr",
            Title = background ? "Отключите фоновую запись игр Xbox Game Bar" : "Отключите запись игр Xbox Game Bar, если не пользуетесь ей",
            Explanation = background
                ? "Включена фоновая запись: Xbox Game Bar постоянно записывает последние минуты игры, чтобы их можно было сохранить. " +
                  "Это нагружает видеокарту и диск и может отнимать FPS, особенно на слабых видеокартах. Если вы не сохраняете клипы " +
                  "через Win+Alt+G, запись лучше выключить. Само меню Game Bar (Win+G) продолжит открываться."
                : "Постоянная фоновая запись выключена, но функция записи игр (Game DVR) включена и запускается вместе с играми. " +
                  "Если вы не записываете клипы через Win+Alt+R, её можно выключить — на слабых компьютерах это иногда добавляет " +
                  "пару FPS. Само меню Game Bar (Win+G) продолжит открываться.",
            Category = RecommendationCategory.Games,
            Kind = RecommendationKind.AutoFix,
            Impact = impact,
            Severity = SeverityOf(impact),
            CurrentValue = background ? "Идёт фоновая запись" : "Запись клипов включена, фоновая запись выключена",
            Gain = "Может прибавить FPS в играх",
            FixId = TweakIds.GameDvr,
            ApplySummary = "• Будет отключена запись игр Xbox Game Bar (Game DVR).\n" + RegistryTweaks.Describe(RegistryTweaks.GameDvr),
            AfterApplyNote = "Скриншоты и запись клипов через Xbox Game Bar будут недоступны, пока вы не вернёте настройку.",
            IsSafe = true,
            SettingsUri = CapturesUri,
            SettingsLabel = "Открыть параметры",
        });
    }

    private static void AddHags(List<Recommendation> list, HardwareSnapshot hw, SettingsSnapshot s)
    {
        var supported = s.HagsSupported ?? hw.RealGpus.Any(GpuClassifier.LikelySupportsHags);
        if (!supported)
            return;
        if (s.HagsRegistryMode == 2)
        {
            list.Add(Good("Hags", "Планирование GPU с аппаратным ускорением включено",
                s.HagsActive == false ? "Включится после перезагрузки" : null, RecommendationCategory.Games, TweakIds.Hags));
            return;
        }
        var off = s.HagsRegistryMode == 1 || (s.HagsRegistryMode is null && s.HagsActive == false);
        if (!off)
        {
            if (s.HagsActive == true)
                list.Add(Good("Hags", "Планирование GPU с аппаратным ускорением включено", null, RecommendationCategory.Games, TweakIds.Hags));
            return;
        }
        var frameGen = hw.RealGpus.Any(GpuClassifier.IsFrameGenerationCapable);
        var impact = frameGen ? RecommendationImpact.Medium : RecommendationImpact.Low;
        list.Add(new Recommendation
        {
            Id = "Hags",
            Title = "Включите планирование GPU с аппаратным ускорением",
            Explanation = "Видеокарта сама управляет очередью своих задач вместо процессора. Это немного снижает задержки и нагрузку " +
                          "на процессор, а для DLSS Frame Generation на видеокартах NVIDIA RTX 40 и новее — обязательно. " +
                          "Эффект зависит от игры: если появятся проблемы, верните настройку.",
            Category = RecommendationCategory.Games,
            Kind = RecommendationKind.AutoFix,
            Impact = impact,
            Severity = SeverityOf(impact),
            CurrentValue = "Выключено",
            Gain = "Меньше задержек в играх",
            FixId = TweakIds.Hags,
            ApplySummary = "• Будет включено планирование GPU с аппаратным ускорением.\n" + RegistryTweaks.Describe(RegistryTweaks.Hags),
            AfterApplyNote = "Изменение вступит в силу после перезагрузки компьютера.",
            NeedsAdmin = true,
            NeedsRestart = true,
            SettingsUri = GraphicsUri,
            SettingsLabel = "Открыть параметры графики",
            Steps = new[]
            {
                "Нажмите «Открыть параметры графики» (Параметры → Система → Дисплей → Графика).",
                "Откройте «Изменить стандартные параметры графики» (в Windows 10 — пункт сразу на странице).",
                "Включите «Планирование графического процессора с аппаратным ускорением» и перезагрузите компьютер.",
            },
        });
    }

    private static string WeakReason(HardwareSnapshot hw)
    {
        var parts = new List<string>();
        if (RamBelow(hw, 8))
            parts.Add($"всего {HardwareText.Gb(hw.TotalRamBytes)} памяти");
        if (hw.Cpu is { LogicalProcessors: > 0 and <= 4 } cpu)
            parts.Add($"процессор с {cpu.LogicalProcessors} {Format.Plural(cpu.LogicalProcessors, "потоком", "потоками", "потоками")}");
        return string.Join(" и ", parts);
    }

    private static string VisualFxText(int? value) => value switch
    {
        1 => "Наилучший вид",
        2 => "Наилучшее быстродействие",
        3 => "Особые эффекты",
        _ => "Выбирается Windows автоматически",
    };

    private static void AddVisualEffects(List<Recommendation> list, HardwareSnapshot hw, SettingsSnapshot s)
    {
        var weak = IsWeakPc(hw);
        if (s.VisualFxSetting == 2)
        {
            list.Add(Good("VisualFx", "Визуальные эффекты настроены на быстродействие", VisualFxText(2), RecommendationCategory.Windows,
                TweakIds.VisualEffects));
            return;
        }
        if (!weak)
            return;
        list.Add(new Recommendation
        {
            Id = "VisualFx",
            Title = "Упростите визуальные эффекты Windows",
            Explanation = $"На этом компьютере {WeakReason(hw)}. Анимации окон и другие визуальные эффекты отнимают ресурсы — без них " +
                          "окна сворачиваются и открываются быстрее. «Применить» выключит анимацию окон и панели задач (подействует после " +
                          "выхода из системы и повторного входа); остальные эффекты, например тени и плавное появление меню, можно " +
                          "выключить в «Параметрах быстродействия» — см. «Как сделать».",
            Category = RecommendationCategory.Windows,
            Kind = RecommendationKind.AutoFix,
            Impact = RecommendationImpact.Medium,
            Severity = IssueSeverity.Medium,
            CurrentValue = VisualFxText(s.VisualFxSetting),
            Gain = "Отзывчивее интерфейс",
            FixId = TweakIds.VisualEffects,
            ApplySummary = "• Будет выключена анимация сворачивания и разворачивания окон и анимация панели задач, а в окне " +
                           "«Параметры быстродействия» будет отмечен режим «Обеспечить наилучшее быстродействие».\n" +
                           RegistryTweaks.Describe(RegistryTweaks.VisualEffects),
            AfterApplyNote = "Изменения полностью вступят в силу после выхода из системы и повторного входа.",
            IsSafe = true,
            SettingsUri = PerformanceOptions,
            SettingsLabel = "Параметры быстродействия",
            Steps = new[]
            {
                "Нажмите «Параметры быстродействия» — откроется окно «Визуальные эффекты».",
                "Выберите «Обеспечить наилучшее быстродействие».",
                "При желании отметьте «Сглаживание неровностей экранных шрифтов» и «Вывод эскизов вместо значков» — они почти не нагружают систему.",
                "Нажмите «ОК».",
            },
        });
    }

    private static void AddTransparency(List<Recommendation> list, HardwareSnapshot hw, SettingsSnapshot s)
    {
        if (!IsWeakPc(hw))
            return;
        if (!s.TransparencyEnabled)
        {
            list.Add(Good("Transparency", "Эффекты прозрачности отключены", null, RecommendationCategory.Windows, TweakIds.Transparency));
            return;
        }
        list.Add(new Recommendation
        {
            Id = "Transparency",
            Title = "Отключите эффекты прозрачности",
            Explanation = "Полупрозрачные меню «Пуск», панель задач и окна дополнительно нагружают видеокарту. На слабом компьютере " +
                          "их отключение делает интерфейс чуть отзывчивее, а внешний вид почти не меняется.",
            Category = RecommendationCategory.Windows,
            Kind = RecommendationKind.AutoFix,
            Impact = RecommendationImpact.Low,
            Severity = IssueSeverity.Low,
            CurrentValue = "Включены",
            Gain = "Чуть отзывчивее интерфейс",
            FixId = TweakIds.Transparency,
            ApplySummary = "• Будут отключены эффекты прозрачности.\n" + RegistryTweaks.Describe(RegistryTweaks.Transparency),
            IsSafe = true,
            SettingsUri = ColorsUri,
            SettingsLabel = "Открыть параметры",
        });
    }

    private static void AddPagefile(List<Recommendation> list, HardwareSnapshot hw, SettingsSnapshot s)
    {
        if (s.PagefileDisabled)
        {
            var impact = RamBelow(hw, 16) ? RecommendationImpact.Medium : RecommendationImpact.Low;
            list.Add(new Recommendation
            {
                Id = "Pagefile",
                Title = "Файл подкачки отключён",
                Explanation = "Без файла подкачки при нехватке памяти программы и игры могут внезапно закрываться с ошибкой, даже если " +
                              "памяти вроде бы достаточно. Рекомендуется вернуть автоматический размер — Windows сама решит, сколько " +
                              "места на диске для этого нужно.",
                Category = RecommendationCategory.Windows,
                Kind = RecommendationKind.Manual,
                Impact = impact,
                Severity = SeverityOf(impact),
                CurrentValue = "Отключён",
                Gain = "Меньше вылетов при нехватке памяти",
                SettingsUri = PerformanceOptions,
                SettingsLabel = "Параметры быстродействия",
                Steps = new[]
                {
                    "Нажмите «Параметры быстродействия».",
                    "Перейдите на вкладку «Дополнительно» и в блоке «Виртуальная память» нажмите «Изменить».",
                    "Отметьте «Автоматически выбирать объём файла подкачки» и нажмите «ОК».",
                    "Перезагрузите компьютер.",
                },
            });
            return;
        }
        if (s.PagefileAutomatic == true || s.PagefileCount > 0)
        {
            list.Add(Good("Pagefile", "Файл подкачки включён",
                s.PagefileAutomatic == true ? "Размер выбирается автоматически" : "Размер задан вручную", RecommendationCategory.Windows));
        }
    }

    private static void AddRestart(List<Recommendation> list, SettingsSnapshot s)
    {
        if (s.PendingReboot)
        {
            list.Add(new Recommendation
            {
                Id = "PendingReboot",
                Title = "Windows ждёт перезагрузки, чтобы завершить обновление",
                Explanation = "Установленные обновления применятся только после перезагрузки. До этого часть системных файлов работает " +
                              "в промежуточном состоянии, а новые обновления могут не устанавливаться.",
                Category = RecommendationCategory.Maintenance,
                Kind = RecommendationKind.Manual,
                Impact = RecommendationImpact.Medium,
                Severity = IssueSeverity.Medium,
                CurrentValue = s.Uptime > TimeSpan.Zero ? $"Без перезагрузки: {Format.Duration(s.Uptime)}" : null,
                Gain = "Завершится установка обновлений",
                SettingsUri = UpdateUri,
                SettingsLabel = "Центр обновления",
                Steps = new[]
                {
                    "Сохраните открытые документы.",
                    "Нажмите «Пуск» → «Выключение» → «Обновить и перезагрузить» (или просто «Перезагрузка»).",
                },
            });
            return;
        }
        if (s.Uptime > UptimeLimit)
        {
            list.Add(new Recommendation
            {
                Id = "Uptime",
                Title = $"Компьютер не перезагружался {HardwareText.DaysText(s.Uptime)}",
                Explanation = "За долгую работу накапливаются утечки памяти и зависшие процессы, а часть обновлений ждёт перезагрузки. " +
                              "Перезагрузка раз в несколько дней освежает систему. Учтите: при включённом быстром запуске обычное " +
                              "«Завершение работы» не сбрасывает систему полностью — нужна именно «Перезагрузка».",
                Category = RecommendationCategory.Maintenance,
                Kind = RecommendationKind.Manual,
                Impact = RecommendationImpact.Low,
                Severity = IssueSeverity.Low,
                CurrentValue = $"Без перезагрузки: {Format.Duration(s.Uptime)}",
                Gain = "Освежит систему",
                Steps = new[]
                {
                    "Сохраните открытые документы.",
                    "Нажмите «Пуск» → «Выключение» → «Перезагрузка».",
                },
            });
            return;
        }
        if (s.Uptime > TimeSpan.Zero)
            list.Add(Good("Uptime", "Компьютер недавно перезагружался", $"Без перезагрузки: {Format.Duration(s.Uptime)}",
                RecommendationCategory.Maintenance));
    }

    private static void AddMaintenanceLinks(List<Recommendation> list)
    {
        list.Add(new Recommendation
        {
            Id = "Link:Startup",
            Title = "Проверьте автозагрузку",
            Explanation = "Программы, которые запускаются вместе с Windows, замедляют включение компьютера и занимают память в фоне. " +
                          "Лишние можно безопасно отключить — это обратимо.",
            Category = RecommendationCategory.Maintenance,
            Kind = RecommendationKind.Navigate,
            Impact = RecommendationImpact.Low,
            Severity = IssueSeverity.Info,
            NavigateKey = "Startup",
            NavigateLabel = "Открыть «Автозагрузку»",
        });
        list.Add(new Recommendation
        {
            Id = "Link:Cleaner",
            Title = "Очистите временные файлы",
            Explanation = "Кэш браузеров, временные файлы и остатки обновлений занимают место. Когда системный диск почти заполнен, " +
                          "Windows работает медленнее.",
            Category = RecommendationCategory.Maintenance,
            Kind = RecommendationKind.Navigate,
            Impact = RecommendationImpact.Low,
            Severity = IssueSeverity.Info,
            NavigateKey = "Cleaner",
            NavigateLabel = "Открыть «Очистку»",
        });
        list.Add(new Recommendation
        {
            Id = "Link:Disk",
            Title = "Проверьте место на дисках",
            Explanation = "Когда на системном диске остаётся меньше 10% свободного места, Windows и программы работают медленнее, " +
                          "а обновления могут не устанавливаться. В разделе «Диски» видно, что занимает место.",
            Category = RecommendationCategory.Maintenance,
            Kind = RecommendationKind.Navigate,
            Impact = RecommendationImpact.Low,
            Severity = IssueSeverity.Info,
            NavigateKey = "Disk",
            NavigateLabel = "Открыть «Диски»",
        });
    }
}
