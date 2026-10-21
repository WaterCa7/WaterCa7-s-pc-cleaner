using PcCleaner.Core.Localization;
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

    /// <summary>CPU load (2-second sample) above which the PC is called busy.</summary>
    public const double HighCpuLoadPercent = 90;

    /// <summary>Thermal zone temperature from which the PC is overheating.</summary>
    public const double HighTemperatureC = 85;

    /// <summary>Page files filled more than this are a sign of a RAM shortage.</summary>
    public const double PagefilePressurePercent = 50;

    /// <summary>Battery wear (lost capacity, %) above which a replacement is suggested: Low, then Medium.</summary>
    public const double BatteryWearLowPercent = 30;

    public const double BatteryWearMediumPercent = 40;

    /// <summary>Pseudo-URI for the "Отчёт о батарее" button (handled by the page, not by the shell).</summary>
    public const string BatteryReportUri = "waterca7s:battery-report";

    public const string MbrToGptGuideUri = "https://learn.microsoft.com/ru-ru/windows/deployment/mbr-to-gpt";

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
        AddCpuLoad(list, s);
        AddTemperature(list, hw, s);
        AddSystemDisk(list, hw);
        AddRefreshRate(list, hw);
        AddMonitorOnIntegratedGpu(list, hw);
        AddGpuDrivers(list, hw, now);
        AddBios(list, hw, now);
        AddBootMode(list, hw);
        AddVirtualization(list, hw);
        AddBattery(list, hw);
        AddPowerPlan(list, hw, s);
        AddGameMode(list, s);
        AddGameDvr(list, s);
        AddHags(list, hw, s);
        AddVisualEffects(list, hw, s);
        AddTransparency(list, hw, s);
        AddPagefile(list, hw, s);
        AddFastStartup(list, s);
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
            T._("Перезагрузите компьютер и сразу нажимайте Delete (или F2), чтобы открыть BIOS."),
            amd
                ? T._("Найдите пункт EXPO, DOCP или XMP — обычно он на главном экране BIOS или в разделе разгона (Ai Tweaker, OC, Extreme Tweaker).")
                : T._("Найдите пункт XMP (Extreme Memory Profile) — обычно он на главном экране BIOS или в разделе разгона (Ai Tweaker, OC, Tweaker)."),
            T._("Выберите «Profile 1» и сохраните настройки клавишей F10."),
            T._("Если после этого компьютер не запускается — подождите: многие платы сами сбрасывают неудачные настройки после 2–3 попыток. Иначе сбросьте BIOS (Clear CMOS) или выберите профиль с меньшей частотой."),
            T._("Если пункта XMP нет или частота после включения не выросла, её ограничивает процессор или материнская плата (например, платы Intel на чипсетах H и B до 500-й серии). Тогда менять ничего не нужно."),
        };
    }

    /// <summary>
    /// Memory speed as the engine sees it. <see cref="Rated"/> is the slowest module's rating: the higher of the SPD speed
    /// Windows reports and the speed encoded in the part number (Windows often reports only the JEDEC speed, e.g. 2133
    /// for a DDR4-3200 kit with XMP off). <see cref="PartRated"/> is set only when every module's part number was decoded.
    /// <see cref="JedecOnly"/>: every module is a standard JEDEC module without an XMP/EXPO profile (OEM Samsung, SK Hynix,
    /// Micron, Kingston ValueRAM, basic Crucial), so a lower speed is a processor/board limit, not a profile left off.
    /// </summary>
    public sealed record MemorySpeedInfo(int Rated, int Configured, string? Type, bool FromPartNumber, int? PartRated, string? PartNumber,
        bool JedecOnly = false);

    /// <summary>
    /// Rated (slowest module) and configured (MT/s) memory speed, or null when Windows does not report them.
    /// Some firmware reports the real clock instead of the transfer rate (1600 for DDR4-3200); that is corrected,
    /// but only when the reported value is below anything the memory type really runs at — so a DDR4-4400 kit
    /// stuck at its JEDEC 2133 is still recognised as slow.
    /// </summary>
    public static MemorySpeedInfo? MemorySpeed(HardwareSnapshot hw)
    {
        var mods = hw.RamModules
            .Select(m => (Module: m, Part: HardwareText.DecodeMemoryPart(m.PartNumber)))
            .Where(x => x.Module.ConfiguredMhz > 0 && (x.Module.RatedMhz > 0 || x.Part is not null))
            .ToList();
        if (mods.Count == 0)
            return null;
        static int RatingOf((RamModule Module, (int Speed, string? Type)? Part) x) => Math.Max(x.Module.RatedMhz, x.Part?.Speed ?? 0);
        var slowest = mods.MinBy(RatingOf);
        var rated = RatingOf(slowest);
        var fromPart = slowest.Part is { } sp && sp.Speed > slowest.Module.RatedMhz;
        int? partRated = mods.All(x => x.Part is not null) ? mods.Min(x => x.Part!.Value.Speed) : null;
        var configured = mods.Max(x => x.Module.ConfiguredMhz);
        var type = mods.Select(x => HardwareText.MemoryTypeName(x.Module.MemoryType)).FirstOrDefault(t => t is not null)
                   ?? mods.Select(x => x.Part?.Type).FirstOrDefault(t => t is not null);
        var lowestReal = type switch
        {
            "DDR3" or "LPDDR3" => 1066,
            "DDR4" or "LPDDR4" => 2133,
            "DDR5" or "LPDDR5" => 4400,
            _ => int.MaxValue, // unknown type: rely on the ratio alone
        };
        if (configured < lowestReal && configured * 2 >= rated * 0.95 && configured * 2 <= rated * 1.05)
            configured *= 2;
        var jedecOnly = mods.All(x => HardwareText.IsJedecOnlyPart(x.Module.PartNumber));
        return new MemorySpeedInfo(rated, configured, type, fromPart, partRated, slowest.Part is null ? null : slowest.Module.PartNumber,
            jedecOnly);
    }

    /// <summary>True when the memory runs noticeably (more than 10%) below its rated speed.</summary>
    public static bool IsMemoryBelowRated(int rated, int configured) => rated > configured * (1 + MemorySpeedTolerance);

    /// <summary>
    /// The standard (JEDEC) speed every module starts at without XMP/EXPO: DDR4 2133/2400/2666, DDR5 4800.
    /// Windows reports 2666 as 2667 on some boards, so a small tolerance is allowed.
    /// </summary>
    public static bool IsJedecSpeed(string? type, int configured)
    {
        static bool Near(int value, int target) => Math.Abs(value - target) <= 20;
        return type switch
        {
            "DDR4" => Near(configured, 2133) || Near(configured, 2400) || Near(configured, 2666),
            "DDR5" => Near(configured, 4800),
            _ => false,
        };
    }

    /// <summary>The memory runs without XMP/EXPO although the modules are rated faster (desktop only, see the RamSpeed rule).</summary>
    public static bool IsXmpOff(HardwareSnapshot hw)
        => !hw.IsLaptop && MemorySpeed(hw) is { JedecOnly: false } s && IsMemoryBelowRated(s.Rated, s.Configured) &&
           IsJedecSpeed(s.Type, s.Configured);

    /// <summary>
    /// One memory-speed item at most: "without XMP/EXPO" (JEDEC speed, modules rated faster — by SPD or part number),
    /// "below rated speed" (other slow speeds), "check the sticker" (JEDEC speed, rating unknown) or "fine".
    /// </summary>
    private static void AddMemorySpeed(List<Recommendation> list, HardwareSnapshot hw)
    {
        if (MemorySpeed(hw) is not { } speed)
            return;
        var (rated, configured, type) = (speed.Rated, speed.Configured, speed.Type);
        var current = T.F("{0} МГц из {1} МГц", configured, rated);

        if (IsMemoryBelowRated(rated, configured))
        {
            if (hw.IsLaptop)
            {
                list.Add(new Recommendation
                {
                    Id = "RamSpeed",
                    Title = T.F("Память работает на {0} МГц вместо {1} МГц", configured, rated),
                    Explanation = T.F("Модули поддерживают {0} МГц, но в ноутбуках частоту памяти обычно ограничивает процессор или производитель, а включить XMP в BIOS ноутбука, как правило, нельзя. Это нормально — менять ничего не нужно.", rated),
                    Category = RecommendationCategory.Hardware,
                    Kind = RecommendationKind.Info,
                    Impact = RecommendationImpact.Low,
                    Severity = IssueSeverity.Info,
                    CurrentValue = current,
                });
                return;
            }
            if (speed.JedecOnly)
            {
                // Standard modules have no XMP/EXPO profile: "switch on XMP" would send the user hunting for a BIOS option
                // that does nothing. The usual cause is the processor's memory limit (e.g. 2666 МГц for Core i5 10th gen).
                list.Add(new Recommendation
                {
                    Id = "RamSpeed",
                    Title = T.F("Память работает на {0} МГц вместо {1} МГц", configured, rated),
                    Explanation = (speed.PartNumber is { } part
                                      ? T.F("Модули ({0}) рассчитаны на {1} МГц, но это обычная память без профиля XMP/EXPO, поэтому включать в BIOS нечего.", part, rated)
                                      : T.F("Модули рассчитаны на {0} МГц, но это обычная память без профиля XMP/EXPO, поэтому включать в BIOS нечего.", rated)) +
                                  " " + T._("Скорее всего, частоту ограничивает процессор или материнская плата — например, Intel Core i3/i5 10-го поколения и платы на чипсетах H410/B460 поддерживают не больше 2666 МГц. Обычно менять ничего не нужно."),
                    Category = RecommendationCategory.Hardware,
                    Kind = RecommendationKind.Info,
                    Impact = RecommendationImpact.Low,
                    Severity = IssueSeverity.Info,
                    CurrentValue = current,
                    Steps = new[]
                    {
                        T._("Посмотрите в характеристиках процессора на сайте Intel или AMD, какую частоту памяти он поддерживает (строка «Типы памяти» или Max Memory Speed). Если там столько же, сколько сейчас, всё в порядке."),
                        T.F("Если процессор поддерживает больше, откройте BIOS (Delete или F2 при включении) и проверьте, не задана ли частота памяти (DRAM Frequency) вручную. Выберите Auto или {0} МГц и сохраните настройки клавишей F10.", rated),
                    },
                });
                return;
            }
            var source = speed.FromPartNumber
                ? T.F("Судя по маркировке модулей ({0}), они рассчитаны на {1} МГц", speed.PartNumber, rated)
                : T.F("Модули рассчитаны на {0} МГц", rated);
            if (IsJedecSpeed(type, configured))
            {
                list.Add(new Recommendation
                {
                    Id = "RamSpeed",
                    Title = T._("Память работает без XMP/EXPO"),
                    Explanation = T.F("{0}, но работают на стандартной частоте {1} — {2} МГц. Так бывает, когда в BIOS не включён профиль XMP (у плат для AMD он называется EXPO или DOCP): без него любая память запускается на базовой частоте. Включение профиля бесплатно ускоряет память, а с ней — игры и тяжёлые программы.", source, type, configured),
                    Category = RecommendationCategory.Hardware,
                    Kind = RecommendationKind.Manual,
                    Impact = RecommendationImpact.High,
                    Severity = IssueSeverity.High,
                    CurrentValue = current,
                    Gain = T._("Быстрее игры и тяжёлые программы"),
                    Steps = XmpSteps(hw),
                });
                return;
            }
            list.Add(new Recommendation
            {
                Id = "RamSpeed",
                Title = T.F("Память работает на {0} МГц вместо {1} МГц", configured, rated),
                Explanation = T.F("{0}, но сейчас работают медленнее. Обычно так бывает, когда в BIOS не включён профиль разгона памяти XMP (у плат для AMD — EXPO или DOCP) или выбрана частота вручную. Включение профиля бесплатно ускоряет память, а с ней — игры и тяжёлые программы.", source),
                Category = RecommendationCategory.Hardware,
                Kind = RecommendationKind.Manual,
                Impact = RecommendationImpact.High,
                Severity = IssueSeverity.High,
                CurrentValue = current,
                Gain = T._("Быстрее игры и тяжёлые программы"),
                Steps = XmpSteps(hw),
            });
            return;
        }

        // Windows often reports only the JEDEC base speed, so a module at 2133/2400 (DDR4) or 4800 (DDR5) may still have XMP off.
        // When every part number was decoded, the real rating is known and there is nothing to check; standard modules
        // without an XMP/EXPO profile have nothing to switch on either.
        var baseSpeed = !hw.IsLaptop && speed.PartRated is null && !speed.JedecOnly &&
                        ((type == "DDR4" && configured <= 2400) || (type == "DDR5" && configured <= 4800));
        if (baseSpeed)
        {
            list.Add(new Recommendation
            {
                Id = "RamBaseSpeed",
                Title = T.F("Память работает на базовой частоте {0} МГц", configured),
                Explanation = T.F("Это стандартная частота для {0}. Многие модули рассчитаны на большую (например, 3200–3600 МГц для DDR4 или 6000 МГц для DDR5) — она указана на наклейке модуля. Windows сообщает не всегда точно, поэтому проверьте: если модули быстрее, включите профиль XMP/EXPO в BIOS.", type),
                Category = RecommendationCategory.Hardware,
                Kind = RecommendationKind.Info,
                Impact = RecommendationImpact.Low,
                Severity = IssueSeverity.Info,
                CurrentValue = T.F("{0} МГц", configured),
                Steps = XmpSteps(hw),
            });
            return;
        }

        list.Add(Good("RamSpeed", T._("Память работает на паспортной частоте"), T.F("{0} МГц", configured), RecommendationCategory.Hardware));
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
            var slotCount = hw.RamSlots;
            list.Add(new Recommendation
            {
                Id = "RamSingle",
                Title = T._("Память работает в одноканальном режиме"),
                Explanation = T.F("Установлен всего один модуль памяти ({0}). С двумя одинаковыми модулями память работает в двухканальном режиме и её пропускная способность почти удваивается — это заметно в играх и особенно на встроенной графике.", HardwareText.Gb(mods[0].CapacityBytes)) +
                              (hw.IsLaptop ? " " + T._("В некоторых ноутбуках свободного слота нет — проверьте это в характеристиках модели.") : ""),
                Category = RecommendationCategory.Hardware,
                Kind = RecommendationKind.Manual,
                Impact = hw.IsLaptop ? RecommendationImpact.Low : RecommendationImpact.Medium,
                Severity = hw.IsLaptop ? IssueSeverity.Low : IssueSeverity.Medium,
                CurrentValue = slotCount > 1 ? T.F("1 модуль, слотов на плате: {0}", slotCount) : T._("1 модуль"),
                Gain = T._("Выше скорость памяти"),
                Steps = new[]
                {
                    T.F("Купите второй модуль такого же объёма и частоты ({0}, {1}, {2}).", HardwareText.Gb(mods[0].CapacityBytes),
                        HardwareText.MemoryTypeName(mods[0].MemoryType) ?? T._("того же типа"),
                        mods[0].RatedMhz > 0 ? HardwareText.Mhz(mods[0].RatedMhz) : T._("та же частота")) + " " + T._("Лучше всего — такой же модели."),
                    hw.IsLaptop
                        ? T._("Установите его в свободный слот (как это сделать, написано в инструкции к ноутбуку).")
                        : T._("Установите модули в слоты, рекомендованные в инструкции к материнской плате (обычно A2 и B2 — второй и четвёртый от процессора)."),
                    T._("Откройте этот раздел снова и убедитесь, что видны оба модуля."),
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
            var both = mods.Count == 2 ? T._("оба модуля") : T.F("все {0}", Format.Count(mods.Count, "модуль", "модуля", "модулей"));
            list.Add(new Recommendation
            {
                Id = "RamOneChannel",
                Title = T._("Все модули памяти стоят в одном канале"),
                Explanation = T.F("Похоже, {0} установлены в слоты канала {1}. Тогда память работает в одноканальном режиме и заметно медленнее, чем могла бы. Обычно модули ставят через один слот — так включается двухканальный режим.", both, channels[0].Key),
                Category = RecommendationCategory.Hardware,
                Kind = RecommendationKind.Manual,
                Impact = RecommendationImpact.Medium,
                Severity = IssueSeverity.Medium,
                CurrentValue = string.Join(", ", mods.Select(m => m.DeviceLocator ?? m.BankLabel ?? "?")),
                Gain = T._("Выше скорость памяти"),
                Steps = new[]
                {
                    T._("Выключите компьютер и отключите его от розетки."),
                    T._("Посмотрите в инструкции к материнской плате, в какие слоты ставить модули (для двух модулей обычно A2 и B2)."),
                    T._("Переставьте модули, включите компьютер и проверьте этот раздел ещё раз."),
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
                        Title = T._("Переставьте модули памяти — часть памяти работает в одноканальном режиме"),
                        Explanation = T.F("В каналах установлен разный объём памяти (канал {0}: {1}, канал {2}: {3}), поэтому {4} работают в быстром двухканальном режиме, а оставшиеся {5} — в одноканальном. Если поменять местами два модуля, в каждом канале будет по {6} ({7}) и вся память заработает в двухканальном режиме. Это бесплатно и занимает несколько минут.", channels[0].Key, HardwareText.Gb(a), channels[1].Key, HardwareText.Gb(b), HardwareText.Gb(dual), HardwareText.Gb(single), half, combo),
                        Category = RecommendationCategory.Hardware,
                        Kind = RecommendationKind.Manual,
                        Impact = RecommendationImpact.Low,
                        Severity = IssueSeverity.Low,
                        CurrentValue = T.F("Канал {0}: {1} · канал {2}: {3}", channels[0].Key, HardwareText.Gb(a), channels[1].Key, HardwareText.Gb(b)),
                        Gain = T._("Вся память в двухканальном режиме"),
                        Steps = new[]
                        {
                            T._("Выключите компьютер, отключите его от розетки и снимите боковую крышку корпуса."),
                            T.F("Поменяйте местами один модуль на {0} из канала {1} и один модуль на {2} из канала {3} (Windows называет их слоты «{4}» и «{5}»). Слоты одного канала обычно стоят рядом и подписаны на плате буквой канала (A1, A2, B1, B2); точная схема — в инструкции к материнской плате.", HardwareText.Gb(swap.FromA.CapacityBytes), channels[0].Key, HardwareText.Gb(swap.FromB.CapacityBytes), channels[1].Key, SlotName(swap.FromA), SlotName(swap.FromB)),
                            T.F("Включите компьютер и откройте этот раздел снова: в каналах должно быть по {0}.", half),
                        },
                    });
                    return;
                }
                list.Add(new Recommendation
                {
                    Id = "RamAsymmetric",
                    Title = T._("Часть памяти работает в одноканальном режиме"),
                    Explanation = T.F("В каналах установлен разный объём памяти (канал {0}: {1}, канал {2}: {3}). {4} работают в быстром двухканальном режиме, а оставшиеся {5} — в одноканальном. На практике разница невелика; для максимальной скорости ставьте одинаковые модули парами.", channels[0].Key, HardwareText.Gb(a), channels[1].Key, HardwareText.Gb(b), HardwareText.Gb(dual), HardwareText.Gb(single)),
                    Category = RecommendationCategory.Hardware,
                    Kind = RecommendationKind.Info,
                    Impact = RecommendationImpact.Low,
                    Severity = IssueSeverity.Info,
                    CurrentValue = T.F("Канал {0}: {1} · канал {2}: {3}", channels[0].Key, HardwareText.Gb(a), channels[1].Key, HardwareText.Gb(b)),
                });
                return;
            }
        }

        list.Add(Good("RamChannels",
            channels.Count == 2 ? T._("Память работает в двухканальном режиме") : T._("Память работает в многоканальном режиме"),
            T.F("{0} в {1}", Format.Count(mods.Count, "модуль", "модуля", "модулей"), Format.Count(channels.Count, "канале", "каналах", "каналах")),
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
                Title = T.F("Мало оперативной памяти: {0}", gb),
                Explanation = T._("Для Windows 10/11 с браузером и мессенджерами нужно хотя бы 8 ГБ, для игр — 16 ГБ. Когда памяти не хватает, Windows сбрасывает данные на диск, и компьютер начинает «тормозить». Добавить память — один из самых заметных и недорогих апгрейдов."),
                Category = RecommendationCategory.Hardware,
                Kind = RecommendationKind.Manual,
                Impact = RecommendationImpact.High,
                Severity = IssueSeverity.High,
                CurrentValue = gb,
                Gain = T._("Меньше «тормозов» при работе нескольких программ"),
                Steps = new[]
                {
                    T._("Посмотрите в карточке «Оперативная память» выше тип памяти (DDR4, DDR5…) и количество свободных слотов."),
                    T._("Купите модуль того же типа и частоты — лучше такого же объёма, как уже установленный, чтобы работал двухканальный режим."),
                    T._("Если свободных слотов нет, замените модули на более ёмкие (например, 2 × 8 ГБ)."),
                },
            });
        }
        else if (RamBelow(hw, 16))
        {
            list.Add(new Recommendation
            {
                Id = "RamGaming",
                Title = T.F("{0} памяти — маловато для современных игр", gb),
                Explanation = T._("Для офиса и браузера этого хватает, но современным играм желательно 16 ГБ: иначе возможны подтормаживания и долгие загрузки, особенно если параллельно открыт браузер или Discord."),
                Category = RecommendationCategory.Games,
                Kind = RecommendationKind.Info,
                Impact = RecommendationImpact.Low,
                Severity = IssueSeverity.Info,
                CurrentValue = gb,
            });
        }
        else
        {
            list.Add(Good("RamAmount", T._("Оперативной памяти достаточно"), gb, RecommendationCategory.Hardware));
        }
    }

    private static void AddMemoryLoad(List<Recommendation> list, SettingsSnapshot s)
    {
        if (s.MemoryTotalBytes <= 0 || s.MemoryLoadPercent < HighMemoryLoadPercent)
            return;
        list.Add(new Recommendation
        {
            Id = "RamLoad",
            Title = T.F("Оперативная память почти заполнена ({0}%)", s.MemoryLoadPercent),
            Explanation = T.F("Сейчас занято {0} из {1}. Когда свободной памяти мало, Windows начинает активно использовать диск, и всё замедляется. Посмотрите, какие программы занимают больше всего памяти, и закройте лишние.", Format.Bytes(s.MemoryUsedBytes), Format.Bytes(s.MemoryTotalBytes)),
            Category = RecommendationCategory.Maintenance,
            Kind = RecommendationKind.Manual,
            Impact = RecommendationImpact.Medium,
            Severity = IssueSeverity.Medium,
            CurrentValue = T.F("Занято {0}%", s.MemoryLoadPercent),
            Gain = T._("Освободится память"),
            NavigateKey = "Processes",
            NavigateLabel = T._("Открыть «Процессы»"),
        });
    }

    private static void AddCpuLoad(List<Recommendation> list, SettingsSnapshot s)
    {
        if (s.CpuLoadPercent is not { } load || load <= HighCpuLoadPercent)
            return;
        var text = Format.Percent(Math.Round(load));
        list.Add(new Recommendation
        {
            Id = "CpuLoad",
            Title = T._("Процессор загружен более чем на 90%"),
            Explanation = T.F("Пока шла проверка, процессор был занят на {0} (замер за 2 секунды, без учёта самой программы). Когда процессор загружен до предела, всё работает с задержками. Откройте «Процессы» и посмотрите, что его нагружает: часто это обновление Windows, проверка антивирусом или вкладки браузера, а бывает и скрытый майнер.", text),
            Category = RecommendationCategory.Maintenance,
            Kind = RecommendationKind.Manual,
            Impact = RecommendationImpact.Medium,
            Severity = IssueSeverity.Medium,
            CurrentValue = T.F("Загрузка {0}", text),
            Gain = T._("Быстрее отклик системы"),
            NavigateKey = "Processes",
            NavigateLabel = T._("Открыть «Процессы»"),
        });
    }

    private static void AddTemperature(List<Recommendation> list, HardwareSnapshot hw, SettingsSnapshot s)
    {
        if (s.TemperatureC is not { } t)
            return;
        var text = HardwareText.Celsius(t);
        if (t < HighTemperatureC)
        {
            list.Add(Good("Temperature", T._("Температура в норме"), text, RecommendationCategory.Hardware));
            return;
        }
        var steps = hw.IsLaptop
            ? new[]
            {
                T._("Почистите вентиляционные решётки ноутбука и не ставьте его на мягкие поверхности — кровать, диван, колени."),
                T._("Помогает охлаждающая подставка с вентиляторами."),
                T._("Если ноутбуку больше 2–3 лет, отдайте его в сервис на чистку системы охлаждения и замену термопасты."),
                T._("Точную температуру ядер под нагрузкой покажут бесплатные программы HWiNFO или Core Temp."),
            }
            : new[]
            {
                T._("Выключите компьютер, отключите его от розетки и почистите от пыли радиатор процессора, вентиляторы и пылевые фильтры корпуса — баллончиком со сжатым воздухом или мягкой кисточкой."),
                T._("Проверьте, что все вентиляторы крутятся, а корпус не стоит вплотную к стене или батарее отопления."),
                T._("Если компьютеру больше 2–3 лет, замените термопасту на процессоре (или попросите об этом сервис)."),
                T._("Точную температуру ядер под нагрузкой покажут бесплатные программы HWiNFO или Core Temp."),
            };
        list.Add(new Recommendation
        {
            Id = "Temperature",
            Title = T._("Высокая температура"),
            Explanation = T.F("Датчик температуры показывает {0}. При перегреве процессор сбрасывает частоту, чтобы не повредиться (это называется троттлингом), — компьютер начинает тормозить, а при сильном перегреве может внезапно выключаться. Чаще всего виноваты пыль в радиаторе и вентиляторах или высохшая термопаста.", text),
            Category = RecommendationCategory.Hardware,
            Kind = RecommendationKind.Manual,
            Impact = RecommendationImpact.High,
            Severity = IssueSeverity.High,
            CurrentValue = text,
            Gain = T._("Без троттлинга и внезапных выключений"),
            Steps = steps,
        });
    }

    // ===================== Disk / display / GPU =====================

    private static void AddSystemDisk(List<Recommendation> list, HardwareSnapshot hw)
    {
        var disk = hw.SystemDisk;
        if (disk is null)
            return;
        var model = string.IsNullOrWhiteSpace(disk.Model) ? T._("системный диск") : disk.Model;
        if (disk.MediaKind == DiskMediaKind.Hdd)
        {
            list.Add(new Recommendation
            {
                Id = "SystemHdd",
                Title = T._("Установите SSD: Windows работает с жёсткого диска (HDD)"),
                Explanation = T.F("Системный диск {0}: ({1}) — механический. Это самая частая причина медленного включения компьютера и долгого запуска программ. Переход на SSD ускоряет загрузку Windows в несколько раз — это самый заметный апгрейд для старого ПК.", disk.DriveLetter, model),
                Category = RecommendationCategory.Hardware,
                Kind = RecommendationKind.Manual,
                Impact = RecommendationImpact.High,
                Severity = IssueSeverity.High,
                CurrentValue = $"HDD · {model}",
                Gain = T._("Загрузка Windows и программ в разы быстрее"),
                Steps = new[]
                {
                    hw.IsLaptop
                        ? T._("Узнайте, какой SSD подходит вашему ноутбуку (2,5\" SATA или M.2 NVMe) — это есть в характеристиках модели.")
                        : T._("Купите SSD объёмом от 500 ГБ: M.2 NVMe, если на материнской плате есть слот M.2, иначе 2,5\" SATA."),
                    T._("Перенесите систему программой клонирования от производителя SSD (например, Samsung Data Migration, Acronis для WD и Crucial) или установите Windows на SSD заново."),
                    T._("В BIOS выберите SSD первым в порядке загрузки (Boot Priority). Старый HDD можно оставить для файлов."),
                },
            });
        }
        else if (disk.MediaKind == DiskMediaKind.Ssd)
        {
            list.Add(Good("SystemSsd", T._("Windows установлена на SSD"), model, RecommendationCategory.Hardware));
        }
    }

    private static string MonitorLabel(DisplayInfo d) => string.IsNullOrWhiteSpace(d.MonitorName) ? T._("Монитор") : d.MonitorName!;

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
                    Title = T.F("Монитор поддерживает {0} Гц, а работает на {1} Гц", max, d.CurrentHz),
                    Explanation = T.F("Сейчас изображение на мониторе «{0}» обновляется {1} в секунду, хотя он умеет {2}.", name, Format.Count(d.CurrentHz, "раз", "раза", "раз"), max) + " " +
                                  (big
                                      ? T._("Более высокая частота делает движение мыши, прокрутку и игры заметно плавнее.")
                                      : T._("Разница небольшая, но в динамичных играх движение станет чуть плавнее.")) + " " +
                                  T._("Переключение безопасно и делается в параметрах Windows за минуту.") +
                                  (hw.IsLaptop ? " " + T._("На ноутбуке высокая частота немного быстрее расходует батарею.") : ""),
                    Category = RecommendationCategory.Games,
                    Kind = RecommendationKind.Manual,
                    Impact = impact,
                    Severity = SeverityOf(impact),
                    CurrentValue = T.F("{0} Гц при {1}×{2}", d.CurrentHz, d.Width, d.Height),
                    Gain = T._("Плавнее изображение"),
                    SettingsUri = DisplaySettingsUri,
                    SettingsLabel = T._("Открыть параметры дисплея"),
                    Steps = new[]
                    {
                        T._("Нажмите «Открыть параметры дисплея» (Параметры → Система → Дисплей → Расширенные параметры дисплея)."),
                        hw.Displays.Count > 1 ? T.F("Вверху выберите монитор «{0}».", name) : T._("Убедитесь, что выбран нужный монитор."),
                        T.F("В списке «Выберите частоту обновления» выберите {0} Гц.", max),
                        T._("Если нужной частоты нет — проверьте кабель: для высоких частот нужен DisplayPort или HDMI 2.0 и новее. Также частоту можно выбрать в панели управления видеокарты."),
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
                fine.Count == 1 ? T._("Монитор работает на максимальной частоте") : T._("Мониторы работают на максимальной частоте"),
                string.Join(", ", fine.Select(d => fine.Count == 1 ? T.F("{0} Гц", d.CurrentHz) : T.F("{0}: {1} Гц", MonitorLabel(d), d.CurrentHz))),
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
            Title = T._("Монитор подключён к материнской плате, а не к видеокарте"),
            Explanation = T.F("Основной монитор работает через встроенную графику ({0}), хотя в компьютере есть видеокарта {1}. Игры при этом могут работать на слабой встроенной графике или терять производительность.", HardwareText.CleanName(adapter), dgpu),
            Category = RecommendationCategory.Games,
            Kind = RecommendationKind.Manual,
            Impact = RecommendationImpact.High,
            Severity = IssueSeverity.High,
            CurrentValue = T.F("Подключён к {0}", HardwareText.CleanName(adapter)),
            Gain = T._("Игры пойдут на видеокарте"),
            Steps = new[]
            {
                T._("Выключите компьютер."),
                T._("Сзади системного блока переключите кабель монитора из разъёма материнской платы (вверху, рядом с USB) в разъём видеокарты (ниже, в горизонтальном ряду)."),
                T._("Включите компьютер — изображение появится через видеокарту."),
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
                    Title = T._("Не установлен драйвер видеокарты"),
                    Explanation = T._("Windows использует стандартный «базовый видеоадаптер»: нет аппаратного ускорения, игры и видео работают очень медленно или не запускаются, может быть недоступна нужная частота монитора. Установите драйвер с официального сайта производителя видеокарты."),
                    Category = RecommendationCategory.Hardware,
                    Kind = RecommendationKind.Manual,
                    Impact = RecommendationImpact.High,
                    Severity = IssueSeverity.High,
                    CurrentValue = name,
                    Gain = T._("Заработает ускорение графики"),
                    SettingsUri = url ?? UpdateUri,
                    SettingsLabel = url is null ? T._("Открыть Центр обновления") : T.F("Сайт {0}", vendor),
                    Steps = new[]
                    {
                        url is null
                            ? T._("Откройте Центр обновления Windows и установите все обновления, включая необязательные (там бывают драйверы).")
                            : T.F("Откройте сайт {0} кнопкой ниже и скачайте драйвер для своей видеокарты и Windows.", vendor),
                        T._("Запустите установщик и следуйте подсказкам, затем перезагрузите компьютер."),
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
                    url is null ? T._("Найдите драйвер на сайте производителя видеокарты.") : T.F("Откройте сайт {0} кнопкой «Сайт {1}».", vendor, vendor),
                    T.F("Выберите свою модель ({0}) и версию Windows, скачайте и запустите установщик.", name),
                };
                if (gpu.Vendor == GpuVendor.Nvidia)
                    steps.Add(T._("Удобнее всего — приложение NVIDIA App: оно само следит за обновлениями драйвера."));
                else if (gpu.Vendor == GpuVendor.Amd)
                    steps.Add(T._("Удобнее всего — AMD Software: Adrenalin Edition: оно само следит за обновлениями драйвера."));
                if (hw.IsLaptop)
                    steps.Add(T._("Если универсальный драйвер не ставится, скачайте драйвер с сайта производителя ноутбука."));
                steps.Add(T._("После установки перезагрузите компьютер."));
                list.Add(new Recommendation
                {
                    Id = "GpuDriver:" + gpu.Name,
                    Title = T.F("Обновите драйвер видеокарты {0}", name),
                    Explanation = T.F("Установленному драйверу уже {0} (версия {1} от {2}). Новые драйверы исправляют ошибки и часто ускоряют свежие игры. Скачивайте драйвер только с официального сайта производителя.", age, version, Format.Date(date)),
                    Category = RecommendationCategory.Hardware,
                    Kind = RecommendationKind.Manual,
                    Impact = minor ? RecommendationImpact.Low : RecommendationImpact.Medium,
                    Severity = minor ? IssueSeverity.Low : IssueSeverity.Medium,
                    CurrentValue = T.F("{0} от {1}", version, Format.Date(date)),
                    Gain = T._("Исправления и ускорение в играх"),
                    SettingsUri = url ?? "https://www.google.com/search?q=" + Uri.EscapeDataString(T.F("{0} драйвер", name)),
                    SettingsLabel = url is null ? T._("Найти драйвер") : T.F("Сайт {0}", vendor),
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
                fresh.Count == 1 ? T._("Драйвер видеокарты свежий") : T._("Драйверы видеокарт свежие"),
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
            Title = T.F("Версия BIOS материнской платы вышла в {0} году", date.Year),
            Explanation = T._("Возможно, для вашей платы уже есть более новая версия. Обновления BIOS исправляют ошибки, улучшают совместимость с памятью и новыми процессорами и закрывают уязвимости. Но прошивка BIOS — рискованная операция: обновляйте его, только если есть проблемы или нужна поддержка нового железа, и строго по инструкции с сайта производителя платы."),
            Category = RecommendationCategory.Hardware,
            Kind = RecommendationKind.Info,
            Impact = RecommendationImpact.Low,
            Severity = IssueSeverity.Info,
            CurrentValue = T.F("Версия {0} от {1}", hw.BiosVersion ?? "—", Format.Date(date)),
            SettingsUri = board.Length > 0 ? "https://www.google.com/search?q=" + Uri.EscapeDataString(board + " BIOS") : null,
            SettingsLabel = board.Length > 0 ? T._("Найти обновление") : null,
            Steps = board.Length > 0
                ? new[]
                {
                    T.F("Найдите страницу своей платы ({0}) на официальном сайте производителя, раздел «Support» → «BIOS».", board),
                    T._("Сравните версии: если новая исправляет то, что вам мешает, скачайте её и прочитайте инструкцию."),
                    T._("Во время обновления не выключайте компьютер; для ноутбука подключите зарядку."),
                }
                : Array.Empty<string>(),
        });
    }

    private static void AddBootMode(List<Recommendation> list, HardwareSnapshot hw)
    {
        if (hw.BootMode == BootMode.Uefi)
        {
            list.Add(Good("BootMode", T._("Компьютер загружается в режиме UEFI"), "UEFI", RecommendationCategory.Hardware));
            return;
        }
        if (hw.BootMode != BootMode.LegacyBios)
            return;
        list.Add(new Recommendation
        {
            Id = "BootMode",
            Title = T._("Компьютер загружается в режиме Legacy BIOS"),
            Explanation = T._("Windows запускается в устаревшем режиме совместимости (Legacy BIOS, он же CSM), а не в современном UEFI. С UEFI компьютер включается быстрее, а ещё этот режим обязателен для безопасной загрузки (Secure Boot) и для Windows 11. Переход — сложная операция для опытных пользователей: системный диск нужно преобразовать из MBR в GPT, и ошибка может оставить компьютер без загружаемой Windows. Если всё работает и Windows 11 не нужна, можно ничего не менять."),
            Category = RecommendationCategory.Hardware,
            Kind = RecommendationKind.Manual,
            Impact = RecommendationImpact.Medium,
            Severity = IssueSeverity.Medium,
            CurrentValue = "Legacy BIOS (CSM)",
            Gain = T._("Быстрее загрузка, Secure Boot и Windows 11"),
            SettingsUri = MbrToGptGuideUri,
            SettingsLabel = T._("Инструкция Microsoft"),
            Steps = new[]
            {
                T._("Это сложная и рискованная операция. Сначала сохраните важные файлы на другой диск или в облако, а лучше сделайте полную резервную копию системы."),
                T._("Если диск зашифрован (BitLocker или «Шифрование устройства»), запишите ключ восстановления и приостановите защиту: Панель управления → «Шифрование диска BitLocker» → «Приостановить защиту». Иначе после переключения BIOS Windows потребует ключ восстановления."),
                T._("Убедитесь, что материнская плата умеет загружаться в режиме UEFI: такой пункт есть в BIOS почти всех плат, выпущенных после 2012 года."),
                T._("Запустите «Командную строку» от имени администратора и выполните mbr2gpt /validate /allowFullOS — эта команда только проверяет диск и ничего не меняет."),
                T._("Если проверка прошла без ошибок, выполните mbr2gpt /convert /allowFullOS. Если появилась ошибка — остановитесь: на этом диске преобразование небезопасно."),
                T._("Сразу после преобразования перезагрузите компьютер, откройте BIOS (Delete или F2) и переключите режим загрузки на UEFI: выключите CSM (Legacy Support) или выберите Boot Mode → UEFI. Сохраните настройки клавишей F10."),
                T._("Если Windows не загружается, проверьте в BIOS, что включён режим UEFI, а первым в порядке загрузки стоит Windows Boot Manager."),
            },
        });
    }

    private static void AddVirtualization(List<Recommendation> list, HardwareSnapshot hw)
    {
        switch (hw.VirtualizationEnabled)
        {
            case true:
                list.Add(Good("Virtualization", T._("Аппаратная виртуализация включена"),
                    hw.HypervisorPresent == true ? T._("Работает гипервизор Windows") : null, RecommendationCategory.Hardware));
                return;
            case null:
                return;
        }
        // An old or low-end processor without VT-x / AMD-V: there is no BIOS option to switch on.
        if (hw.VirtualizationUnsupported)
            return;
        var name = hw.Cpu?.Name ?? "";
        var intel = name.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        var amd = name.Contains("AMD", StringComparison.OrdinalIgnoreCase);
        var tech = intel ? "Intel VT-x" : amd ? "AMD SVM" : T._("Intel VT-x или AMD SVM");
        list.Add(new Recommendation
        {
            Id = "Virtualization",
            Title = T._("Аппаратная виртуализация выключена в BIOS"),
            Explanation = T.F("Виртуализация процессора ({0}) нужна для WSL, Docker, эмуляторов Android (BlueStacks, LDPlayer, эмулятор Android Studio) и виртуальных машин (VirtualBox, VMware, Hyper-V). Без неё они работают очень медленно или вовсе не запускаются. Если вы такими программами не пользуетесь, ничего менять не нужно.", tech),
            Category = RecommendationCategory.Hardware,
            Kind = RecommendationKind.Info,
            Impact = RecommendationImpact.Low,
            Severity = IssueSeverity.Info,
            CurrentValue = T._("Выключена"),
            Steps = new[]
            {
                T._("Перезагрузите компьютер и сразу нажимайте Delete (или F2), чтобы открыть BIOS. На ноутбуках клавиша бывает другой — F1, F10 или Esc."),
                intel
                    ? T._("Найдите пункт Intel Virtualization Technology (VT-x) — обычно он в разделе Advanced → CPU Configuration.")
                    : amd
                        ? T._("Найдите пункт SVM Mode (AMD-V) — обычно он в разделе Advanced → CPU Configuration или в разделе разгона (OC, Tweaker).")
                        : T._("Найдите пункт Intel Virtualization Technology (VT-x) или SVM Mode (AMD-V) — обычно он в разделе Advanced → CPU Configuration."),
                T._("Переключите его в Enabled и сохраните настройки клавишей F10."),
            },
        });
    }

    /// <summary>Lost battery capacity in whole percent (0..100), or null when a capacity is unknown.</summary>
    public static double? BatteryWearPercent(long designedMwh, long fullChargedMwh)
    {
        if (designedMwh <= 0 || fullChargedMwh <= 0)
            return null;
        return Math.Round(Math.Clamp(100.0 * (1 - (double)fullChargedMwh / designedMwh), 0, 100));
    }

    private static void AddBattery(List<Recommendation> list, HardwareSnapshot hw)
    {
        if (!hw.IsLaptop || hw.Battery is not { WearPercent: { } wear } battery)
            return;
        if (wear <= BatteryWearLowPercent)
        {
            list.Add(Good("Battery", T._("Батарея в хорошем состоянии"), T.F("Износ {0:0}%", wear), RecommendationCategory.Hardware));
            return;
        }
        var replace = wear > BatteryWearMediumPercent;
        var impact = replace ? RecommendationImpact.Medium : RecommendationImpact.Low;
        list.Add(new Recommendation
        {
            Id = "Battery",
            Title = replace ? T._("Батарею стоит заменить") : T.F("Батарея изношена на {0:0}%", wear),
            Explanation = T.F("Полностью заряженная батарея сейчас вмещает {0} из {1} — {2:0}% от ёмкости новой.", HardwareText.WattHours(battery.FullChargedCapacityMwh),
                              HardwareText.WattHours(battery.DesignedCapacityMwh), 100 - wear) + " " +
                          (replace
                              ? T._("Ноутбук работает от неё заметно меньше, чем раньше, а сильно изношенная батарея может внезапно отключаться под нагрузкой. Замена вернёт прежнее время работы.")
                              : T._("Это обычный износ за несколько лет работы. Менять её пока не обязательно, но если ноутбук держит заряд слишком мало, замена вернёт прежнее время работы.")),
            Category = RecommendationCategory.Hardware,
            Kind = RecommendationKind.Manual,
            Impact = impact,
            Severity = SeverityOf(impact),
            CurrentValue = T.F("Износ {0:0}%", wear),
            Gain = T._("Дольше работа без зарядки"),
            SettingsUri = BatteryReportUri,
            SettingsLabel = T._("Отчёт о батарее"),
            Steps = new[]
            {
                T._("Нажмите «Отчёт о батарее» — Windows покажет историю ёмкости и время работы от батареи."),
                T._("Найдите батарею для своей модели ноутбука (модель указана на наклейке снизу) — лучше оригинальную или от проверенного продавца."),
                T._("Если батарея встроенная и не снимается, замените её в сервисном центре."),
                T._("Чтобы новая батарея изнашивалась медленнее, включите в фирменной программе ноутбука ограничение заряда до 80%, если оно есть."),
            },
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
                list.Add(Good("PowerPlan", T._("Включён производительный план электропитания"), current, RecommendationCategory.Windows,
                    TweakIds.PowerPlan));
                return;
            }
            if (kind is not (PowerPlanKind.Balanced or PowerPlanKind.PowerSaver))
                return; // vendor plan (e.g. "AMD Ryzen Balanced") — tuned by the manufacturer, leave it alone

            var saver = kind == PowerPlanKind.PowerSaver;
            var impact = saver ? RecommendationImpact.High : RecommendationImpact.Medium;
            var why = saver
                ? T._("План «Экономия энергии» ограничивает частоту процессора — компьютер работает заметно медленнее, чем может. На настольном ПК экономия электричества копеечная, а потеря скорости ощутимая.")
                : T.F("Сейчас активен план «{0}»: в простое процессор снижает частоту и разгоняется с небольшой задержкой. Производительный план держит процессор наготове — система откликается быстрее, а частота кадров в играх стабильнее. Для настольного ПК это безопасно, расход электричества вырастет немного.", current);
            var target = Find(PowerPlanKind.UltimatePerformance) ?? Find(PowerPlanKind.HighPerformance);
            if (target is not null)
            {
                var targetName = PowerPlans.DisplayName(target);
                list.Add(new Recommendation
                {
                    Id = "PowerPlan",
                    Title = T.F("Включите план электропитания «{0}»", targetName),
                    Explanation = why,
                    Category = RecommendationCategory.Windows,
                    Kind = RecommendationKind.AutoFix,
                    Impact = impact,
                    Severity = SeverityOf(impact),
                    CurrentValue = current,
                    Gain = T._("Быстрее отклик и стабильнее частоты процессора"),
                    FixId = TweakIds.PowerPlan,
                    FixArgument = target.Guid,
                    ApplySummary = T.F("• Будет включён план электропитания «{0}» (сейчас «{1}»).\n• Команда: powercfg /setactive {2}", targetName, current, target.Guid),
                    IsSafe = true,
                    SettingsUri = "powercfg.cpl",
                    SettingsLabel = T._("Электропитание"),
                });
            }
            else
            {
                list.Add(new Recommendation
                {
                    Id = "PowerPlan",
                    Title = T._("Включите режим максимальной производительности"),
                    Explanation = why + " " + T._("Производительного плана в списке нет, но в Windows 11 можно выбрать режим питания."),
                    Category = RecommendationCategory.Windows,
                    Kind = RecommendationKind.Manual,
                    Impact = impact,
                    Severity = SeverityOf(impact),
                    CurrentValue = current,
                    Gain = T._("Быстрее отклик системы"),
                    SettingsUri = PowerUri,
                    SettingsLabel = T._("Открыть параметры питания"),
                    Steps = new[]
                    {
                        T._("Нажмите «Открыть параметры питания» (Параметры → Система → Питание)."),
                        T._("В пункте «Режим питания» выберите «Максимальная производительность»."),
                        T._("В Windows 10 откройте «Панель управления → Электропитание» и выберите «Высокая производительность»."),
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
                Title = T._("Включите сбалансированный план электропитания"),
                Explanation = T._("План «Экономия энергии» сильно ограничивает процессор — ноутбук работает медленнее. «Сбалансированный» план сам снижает частоты в простое и экономит батарею почти так же хорошо, но не мешает работе."),
                Category = RecommendationCategory.Windows,
                Kind = RecommendationKind.AutoFix,
                Impact = RecommendationImpact.Medium,
                Severity = IssueSeverity.Medium,
                CurrentValue = current,
                Gain = T._("Ноутбук работает быстрее"),
                FixId = TweakIds.PowerPlan,
                FixArgument = balanced.Guid,
                ApplySummary = T.F("• Будет включён план электропитания «{0}» (сейчас «{1}»).\n• Команда: powercfg /setactive {2}", PowerPlans.DisplayName(balanced), current, balanced.Guid),
                SettingsUri = "powercfg.cpl",
                SettingsLabel = T._("Электропитание"),
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
                Title = T._("Ноутбук работает от батареи в режиме максимальной производительности"),
                Explanation = T.F("План «{0}» не даёт процессору экономить энергию, поэтому батарея садится заметно быстрее. Для работы от батареи лучше «Сбалансированный» план, а производительный включайте, когда ноутбук на зарядке.", current),
                Category = RecommendationCategory.Windows,
                Kind = RecommendationKind.AutoFix,
                Impact = RecommendationImpact.Low,
                Severity = IssueSeverity.Low,
                CurrentValue = T.F("{0}, от батареи", current),
                Gain = T._("Дольше работа от батареи"),
                FixId = TweakIds.PowerPlan,
                FixArgument = balanced.Guid,
                ApplySummary = T.F("• Будет включён план электропитания «{0}» (сейчас «{1}»).\n• Команда: powercfg /setactive {2}", PowerPlans.DisplayName(balanced), current, balanced.Guid),
                SettingsUri = "powercfg.cpl",
                SettingsLabel = T._("Электропитание"),
            });
            return;
        }
        if (kind == PowerPlanKind.Balanced && s.OnAcPower == true)
        {
            list.Add(new Recommendation
            {
                Id = "PowerPlanLaptopAc",
                Title = T._("Для игр на зарядке можно включить максимальную производительность"),
                Explanation = T._("Сбалансированный план — правильный выбор для ноутбука. Но когда ноутбук подключён к сети и вы играете или работаете в тяжёлых программах, режим максимальной производительности может прибавить скорости. Не забудьте вернуть обычный режим при работе от батареи."),
                Category = RecommendationCategory.Windows,
                Kind = RecommendationKind.Info,
                Impact = RecommendationImpact.Low,
                Severity = IssueSeverity.Info,
                CurrentValue = T.F("{0}, от сети", current),
                SettingsUri = PowerUri,
                SettingsLabel = T._("Открыть параметры питания"),
                Steps = new[]
                {
                    T._("Нажмите «Открыть параметры питания» (Параметры → Система → Питание и батарея)."),
                    T._("В пункте «Режим питания» выберите «Максимальная производительность» — Windows запомнит его отдельно для работы от сети."),
                },
            });
            return;
        }
        if (kind is PowerPlanKind.Balanced or PowerPlanKind.HighPerformance or PowerPlanKind.UltimatePerformance)
            list.Add(Good("PowerPlan", T._("План электропитания подходит для ноутбука"), current, RecommendationCategory.Windows, TweakIds.PowerPlan));
    }

    private static void AddGameMode(List<Recommendation> list, SettingsSnapshot s)
    {
        if (s.GameModeEnabled)
        {
            list.Add(Good("GameMode", T._("Игровой режим Windows включён"), null, RecommendationCategory.Games, TweakIds.GameMode));
            return;
        }
        list.Add(new Recommendation
        {
            Id = "GameMode",
            Title = T._("Включите игровой режим Windows"),
            Explanation = T._("Игровой режим отдаёт приоритет запущенной игре: Windows не устанавливает обновления драйверов и не показывает уведомления о перезагрузке во время игры, а фоновые задачи получают меньше ресурсов. Это стандартная функция Windows — включать её безопасно."),
            Category = RecommendationCategory.Games,
            Kind = RecommendationKind.AutoFix,
            Impact = RecommendationImpact.Medium,
            Severity = IssueSeverity.Medium,
            CurrentValue = T._("Выключен"),
            Gain = T._("Больше ресурсов для игр"),
            FixId = TweakIds.GameMode,
            ApplySummary = T._("• Будет включён игровой режим Windows.") + "\n" + RegistryTweaks.Describe(RegistryTweaks.GameMode),
            IsSafe = true,
            SettingsUri = GameModeUri,
            SettingsLabel = T._("Открыть параметры"),
        });
    }

    private static void AddGameDvr(List<Recommendation> list, SettingsSnapshot s)
    {
        if (!s.GameDvrActive)
        {
            list.Add(Good("GameDvr", T._("Запись игр Xbox Game Bar отключена"), null, RecommendationCategory.Games, TweakIds.GameDvr));
            return;
        }
        // "Record what happened" (HistoricalCaptureEnabled, off by default) records the last minutes of every game all the time:
        // that is what costs FPS. With it off, Game DVR is only on standby, so the gain is small — say so honestly.
        var background = s.HistoricalCaptureValue is { } h && h != 0;
        var impact = background ? RecommendationImpact.Medium : RecommendationImpact.Low;
        list.Add(new Recommendation
        {
            Id = "GameDvr",
            Title = background ? T._("Отключите фоновую запись игр Xbox Game Bar") : T._("Отключите запись игр Xbox Game Bar, если не пользуетесь ей"),
            Explanation = background
                ? T._("Включена фоновая запись: Xbox Game Bar постоянно записывает последние минуты игры, чтобы их можно было сохранить. Это нагружает видеокарту и диск и может отнимать FPS, особенно на слабых видеокартах. Если вы не сохраняете клипы через Win+Alt+G, запись лучше выключить. Само меню Game Bar (Win+G) продолжит открываться.")
                : T._("Постоянная фоновая запись выключена, но функция записи игр (Game DVR) включена и запускается вместе с играми. Если вы не записываете клипы через Win+Alt+R, её можно выключить — на слабых компьютерах это иногда добавляет пару FPS. Само меню Game Bar (Win+G) продолжит открываться."),
            Category = RecommendationCategory.Games,
            Kind = RecommendationKind.AutoFix,
            Impact = impact,
            Severity = SeverityOf(impact),
            CurrentValue = background ? T._("Идёт фоновая запись") : T._("Запись клипов включена, фоновая запись выключена"),
            Gain = T._("Может прибавить FPS в играх"),
            FixId = TweakIds.GameDvr,
            ApplySummary = T._("• Будет отключена запись игр Xbox Game Bar (Game DVR).") + "\n" + RegistryTweaks.Describe(RegistryTweaks.GameDvr),
            AfterApplyNote = T._("Скриншоты и запись клипов через Xbox Game Bar будут недоступны, пока вы не вернёте настройку."),
            IsSafe = true,
            SettingsUri = CapturesUri,
            SettingsLabel = T._("Открыть параметры"),
        });
    }

    private static void AddHags(List<Recommendation> list, HardwareSnapshot hw, SettingsSnapshot s)
    {
        var supported = s.HagsSupported ?? hw.RealGpus.Any(GpuClassifier.LikelySupportsHags);
        if (!supported)
            return;
        if (s.HagsRegistryMode == 2)
        {
            list.Add(Good("Hags", T._("Планирование GPU с аппаратным ускорением включено"),
                s.HagsActive == false ? T._("Включится после перезагрузки") : null, RecommendationCategory.Games, TweakIds.Hags));
            return;
        }
        var off = s.HagsRegistryMode == 1 || (s.HagsRegistryMode is null && s.HagsActive == false);
        if (!off)
        {
            if (s.HagsActive == true)
                list.Add(Good("Hags", T._("Планирование GPU с аппаратным ускорением включено"), null, RecommendationCategory.Games, TweakIds.Hags));
            return;
        }
        var frameGen = hw.RealGpus.Any(GpuClassifier.IsFrameGenerationCapable);
        var impact = frameGen ? RecommendationImpact.Medium : RecommendationImpact.Low;
        list.Add(new Recommendation
        {
            Id = "Hags",
            Title = T._("Включите планирование GPU с аппаратным ускорением"),
            Explanation = T._("Видеокарта сама управляет очередью своих задач вместо процессора. Это немного снижает задержки и нагрузку на процессор, а для DLSS Frame Generation на видеокартах NVIDIA RTX 40 и новее — обязательно. Эффект зависит от игры: если появятся проблемы, верните настройку."),
            Category = RecommendationCategory.Games,
            Kind = RecommendationKind.AutoFix,
            Impact = impact,
            Severity = SeverityOf(impact),
            CurrentValue = T._("Выключено"),
            Gain = T._("Меньше задержек в играх"),
            FixId = TweakIds.Hags,
            ApplySummary = T._("• Будет включено планирование GPU с аппаратным ускорением.") + "\n" + RegistryTweaks.Describe(RegistryTweaks.Hags),
            AfterApplyNote = T._("Изменение вступит в силу после перезагрузки компьютера."),
            NeedsAdmin = true,
            NeedsRestart = true,
            SettingsUri = GraphicsUri,
            SettingsLabel = T._("Открыть параметры графики"),
            Steps = new[]
            {
                T._("Нажмите «Открыть параметры графики» (Параметры → Система → Дисплей → Графика)."),
                T._("Откройте «Изменить стандартные параметры графики» (в Windows 10 — пункт сразу на странице)."),
                T._("Включите «Планирование графического процессора с аппаратным ускорением» и перезагрузите компьютер."),
            },
        });
    }

    private static string WeakReason(HardwareSnapshot hw)
    {
        var parts = new List<string>();
        if (RamBelow(hw, 8))
            parts.Add(T.F("всего {0} памяти", HardwareText.Gb(hw.TotalRamBytes)));
        if (hw.Cpu is { LogicalProcessors: > 0 and <= 4 } cpu)
            parts.Add(T.F("процессор с {0} {1}", cpu.LogicalProcessors, Format.Plural(cpu.LogicalProcessors, "потоком", "потоками", "потоками")));
        return parts.Count == 2 ? T.F("{0} и {1}", parts[0], parts[1]) : string.Join("", parts);
    }

    private static string VisualFxText(int? value) => value switch
    {
        1 => T._("Наилучший вид"),
        2 => T._("Наилучшее быстродействие"),
        3 => T._("Особые эффекты"),
        _ => T._("Выбирается Windows автоматически"),
    };

    private static void AddVisualEffects(List<Recommendation> list, HardwareSnapshot hw, SettingsSnapshot s)
    {
        var weak = IsWeakPc(hw);
        if (s.VisualFxSetting == 2)
        {
            list.Add(Good("VisualFx", T._("Визуальные эффекты настроены на быстродействие"), VisualFxText(2), RecommendationCategory.Windows,
                TweakIds.VisualEffects));
            return;
        }
        if (!weak)
            return;
        list.Add(new Recommendation
        {
            Id = "VisualFx",
            Title = T._("Упростите визуальные эффекты Windows"),
            Explanation = T.F("На этом компьютере {0}. Анимации окон и другие визуальные эффекты отнимают ресурсы — без них окна сворачиваются и открываются быстрее. «Применить» выключит анимацию окон и панели задач (подействует после выхода из системы и повторного входа); остальные эффекты, например тени и плавное появление меню, можно выключить в «Параметрах быстродействия» — см. «Как сделать».", WeakReason(hw)),
            Category = RecommendationCategory.Windows,
            Kind = RecommendationKind.AutoFix,
            Impact = RecommendationImpact.Medium,
            Severity = IssueSeverity.Medium,
            CurrentValue = VisualFxText(s.VisualFxSetting),
            Gain = T._("Отзывчивее интерфейс"),
            FixId = TweakIds.VisualEffects,
            ApplySummary = T._("• Будет выключена анимация сворачивания и разворачивания окон и анимация панели задач, а в окне «Параметры быстродействия» будет отмечен режим «Обеспечить наилучшее быстродействие».") + "\n" +
                           RegistryTweaks.Describe(RegistryTweaks.VisualEffects),
            AfterApplyNote = T._("Изменения полностью вступят в силу после выхода из системы и повторного входа."),
            IsSafe = true,
            SettingsUri = PerformanceOptions,
            SettingsLabel = T._("Параметры быстродействия"),
            Steps = new[]
            {
                T._("Нажмите «Параметры быстродействия» — откроется окно «Визуальные эффекты»."),
                T._("Выберите «Обеспечить наилучшее быстродействие»."),
                T._("При желании отметьте «Сглаживание неровностей экранных шрифтов» и «Вывод эскизов вместо значков» — они почти не нагружают систему."),
                T._("Нажмите «ОК»."),
            },
        });
    }

    private static void AddTransparency(List<Recommendation> list, HardwareSnapshot hw, SettingsSnapshot s)
    {
        if (!IsWeakPc(hw))
            return;
        if (!s.TransparencyEnabled)
        {
            list.Add(Good("Transparency", T._("Эффекты прозрачности отключены"), null, RecommendationCategory.Windows, TweakIds.Transparency));
            return;
        }
        list.Add(new Recommendation
        {
            Id = "Transparency",
            Title = T._("Отключите эффекты прозрачности"),
            Explanation = T._("Полупрозрачные меню «Пуск», панель задач и окна дополнительно нагружают видеокарту. На слабом компьютере их отключение делает интерфейс чуть отзывчивее, а внешний вид почти не меняется."),
            Category = RecommendationCategory.Windows,
            Kind = RecommendationKind.AutoFix,
            Impact = RecommendationImpact.Low,
            Severity = IssueSeverity.Low,
            CurrentValue = T._("Включены"),
            Gain = T._("Чуть отзывчивее интерфейс"),
            FixId = TweakIds.Transparency,
            ApplySummary = T._("• Будут отключены эффекты прозрачности.") + "\n" + RegistryTweaks.Describe(RegistryTweaks.Transparency),
            IsSafe = true,
            SettingsUri = ColorsUri,
            SettingsLabel = T._("Открыть параметры"),
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
                Title = T._("Файл подкачки отключён"),
                Explanation = T._("Без файла подкачки при нехватке памяти программы и игры могут внезапно закрываться с ошибкой, даже если памяти вроде бы достаточно. Рекомендуется вернуть автоматический размер — Windows сама решит, сколько места на диске для этого нужно."),
                Category = RecommendationCategory.Windows,
                Kind = RecommendationKind.Manual,
                Impact = impact,
                Severity = SeverityOf(impact),
                CurrentValue = T._("Отключён"),
                Gain = T._("Меньше вылетов при нехватке памяти"),
                SettingsUri = PerformanceOptions,
                SettingsLabel = T._("Параметры быстродействия"),
                Steps = new[]
                {
                    T._("Нажмите «Параметры быстродействия»."),
                    T._("Перейдите на вкладку «Дополнительно» и в блоке «Виртуальная память» нажмите «Изменить»."),
                    T._("Отметьте «Автоматически выбирать объём файла подкачки» и нажмите «ОК»."),
                    T._("Перезагрузите компьютер."),
                },
            });
            return;
        }
        if (s.PagefileUsagePercent is { } usage && usage > PagefilePressurePercent)
        {
            var used = Format.Bytes(s.PagefileUsedMb * 1024 * 1024);
            var allocated = Format.Bytes(s.PagefileAllocatedMb * 1024 * 1024);
            var drive = s.PagefilePath is { Length: >= 2 } path && path[1] == ':' ? char.ToUpperInvariant(path[0]).ToString() : null;
            var onHdd = drive is not null && hw.SystemDisk is { MediaKind: DiskMediaKind.Hdd } disk &&
                        string.Equals(disk.DriveLetter, drive, StringComparison.OrdinalIgnoreCase);
            list.Add(new Recommendation
            {
                Id = "PagefileUsage",
                Title = T._("Активно используется файл подкачки"),
                Explanation = T.F("Файл подкачки заполнен на {0}: {1} из {2}. Windows переносит туда то, что не помещается в оперативную память, а диск намного медленнее памяти — отсюда подтормаживания. Это признак того, что оперативной памяти не хватает для ваших задач.", Format.Percent(Math.Round(usage)), used, allocated) +
                              (onHdd ? " " + T._("К тому же файл подкачки лежит на медленном жёстком диске (HDD) — это тормозит систему ещё сильнее.") : ""),
                Category = RecommendationCategory.Windows,
                Kind = RecommendationKind.Manual,
                Impact = RecommendationImpact.Low,
                Severity = IssueSeverity.Low,
                CurrentValue = T.F("Занято {0} из {1}", used, allocated),
                Gain = T._("Меньше подтормаживаний"),
                NavigateKey = "Processes",
                NavigateLabel = T._("Открыть «Процессы»"),
                SettingsUri = PerformanceOptions,
                SettingsLabel = T._("Параметры быстродействия"),
                Steps = new[]
                {
                    T._("Закройте программы, которые занимают много памяти, — их видно в разделе «Процессы»."),
                    T._("Если так бывает постоянно, добавьте оперативной памяти — это самое действенное решение."),
                    T._("Держите файл подкачки на SSD. Если он сейчас на жёстком диске, перенесите его: «Параметры быстродействия» → «Дополнительно» → «Виртуальная память» → «Изменить»."),
                },
            });
            return;
        }
        if (s.PagefileAutomatic == true || s.PagefileCount > 0)
        {
            list.Add(Good("Pagefile", T._("Файл подкачки включён"),
                s.PagefileAutomatic == true ? T._("Размер выбирается автоматически") : T._("Размер задан вручную"), RecommendationCategory.Windows));
        }
    }

    private static void AddFastStartup(List<Recommendation> list, SettingsSnapshot s)
    {
        if (!s.FastStartupEnabled)
            return;
        list.Add(new Recommendation
        {
            Id = "FastStartup",
            Title = T._("Включён быстрый запуск Windows"),
            Explanation = T._("Плюс: после «Завершения работы» компьютер включается быстрее. Минус: Windows выключается не полностью — ядро системы и драйверы сохраняются на диск, как при гибернации. Поэтому часть обновлений и новых драйверов применяется только после «Перезагрузки», после включения иногда сбоят устройства, а если на компьютере есть вторая система (Linux или другая Windows), диск с Windows может оказаться недоступным. Если всё работает нормально, оставьте как есть."),
            Category = RecommendationCategory.Windows,
            Kind = RecommendationKind.Info,
            Impact = RecommendationImpact.Low,
            Severity = IssueSeverity.Info,
            CurrentValue = T._("Включён"),
            SettingsUri = "powercfg.cpl",
            SettingsLabel = T._("Электропитание"),
            Steps = new[]
            {
                T._("Нажмите «Электропитание» (Панель управления → Электропитание)."),
                T._("Слева выберите «Действия кнопок питания»."),
                T._("Нажмите «Изменение параметров, которые сейчас недоступны» — для этого нужны права администратора."),
                T._("Снимите флажок «Включить быстрый запуск (рекомендуется)» и нажмите «Сохранить изменения»."),
            },
        });
    }

    private static void AddRestart(List<Recommendation> list, SettingsSnapshot s)
    {
        if (s.PendingReboot)
        {
            list.Add(new Recommendation
            {
                Id = "PendingReboot",
                Title = T._("Windows ждёт перезагрузки, чтобы завершить обновление"),
                Explanation = T._("Установленные обновления применятся только после перезагрузки. До этого часть системных файлов работает в промежуточном состоянии, а новые обновления могут не устанавливаться."),
                Category = RecommendationCategory.Maintenance,
                Kind = RecommendationKind.Manual,
                Impact = RecommendationImpact.Medium,
                Severity = IssueSeverity.Medium,
                CurrentValue = s.Uptime > TimeSpan.Zero ? T.F("Без перезагрузки: {0}", Format.Duration(s.Uptime)) : null,
                Gain = T._("Завершится установка обновлений"),
                OffersRestart = true,
                SettingsUri = UpdateUri,
                SettingsLabel = T._("Центр обновления"),
                Steps = new[]
                {
                    T._("Сохраните открытые документы."),
                    T._("Нажмите «Пуск» → «Выключение» → «Обновить и перезагрузить» (или просто «Перезагрузка»)."),
                },
            });
            return;
        }
        if (s.Uptime > UptimeLimit)
        {
            list.Add(new Recommendation
            {
                Id = "Uptime",
                Title = T.F("Компьютер не перезагружался {0}", HardwareText.DaysText(s.Uptime)),
                Explanation = T._("За долгую работу накапливаются утечки памяти и зависшие процессы, а часть обновлений ждёт перезагрузки. Перезагрузка раз в несколько дней освежает систему. Учтите: при включённом быстром запуске обычное «Завершение работы» не сбрасывает систему полностью — нужна именно «Перезагрузка»."),
                Category = RecommendationCategory.Maintenance,
                Kind = RecommendationKind.Manual,
                Impact = RecommendationImpact.Low,
                Severity = IssueSeverity.Low,
                CurrentValue = T.F("Без перезагрузки: {0}", Format.Duration(s.Uptime)),
                Gain = T._("Освежит систему"),
                OffersRestart = true,
                Steps = new[]
                {
                    T._("Сохраните открытые документы."),
                    T._("Нажмите «Пуск» → «Выключение» → «Перезагрузка»."),
                },
            });
            return;
        }
        if (s.Uptime > TimeSpan.Zero)
            list.Add(Good("Uptime", T._("Компьютер недавно перезагружался"), T.F("Без перезагрузки: {0}", Format.Duration(s.Uptime)),
                RecommendationCategory.Maintenance));
    }

    private static void AddMaintenanceLinks(List<Recommendation> list)
    {
        list.Add(new Recommendation
        {
            Id = "Link:Startup",
            Title = T._("Проверьте автозагрузку"),
            Explanation = T._("Программы, которые запускаются вместе с Windows, замедляют включение компьютера и занимают память в фоне. Лишние можно безопасно отключить — это обратимо."),
            Category = RecommendationCategory.Maintenance,
            Kind = RecommendationKind.Navigate,
            Impact = RecommendationImpact.Low,
            Severity = IssueSeverity.Info,
            NavigateKey = "Startup",
            NavigateLabel = T._("Открыть «Автозагрузку»"),
        });
        list.Add(new Recommendation
        {
            Id = "Link:Cleaner",
            Title = T._("Очистите временные файлы"),
            Explanation = T._("Кэш браузеров, временные файлы и остатки обновлений занимают место. Когда системный диск почти заполнен, Windows работает медленнее."),
            Category = RecommendationCategory.Maintenance,
            Kind = RecommendationKind.Navigate,
            Impact = RecommendationImpact.Low,
            Severity = IssueSeverity.Info,
            NavigateKey = "Cleaner",
            NavigateLabel = T._("Открыть «Очистку»"),
        });
        list.Add(new Recommendation
        {
            Id = "Link:Disk",
            Title = T._("Проверьте место на дисках"),
            Explanation = T._("Когда на системном диске остаётся меньше 10% свободного места, Windows и программы работают медленнее, а обновления могут не устанавливаться. В разделе «Диски» видно, что занимает место."),
            Category = RecommendationCategory.Maintenance,
            Kind = RecommendationKind.Navigate,
            Impact = RecommendationImpact.Low,
            Severity = IssueSeverity.Info,
            NavigateKey = "Disk",
            NavigateLabel = T._("Открыть «Диски»"),
        });
    }
}
