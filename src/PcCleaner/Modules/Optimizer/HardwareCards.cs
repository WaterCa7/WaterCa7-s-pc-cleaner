using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using PcCleaner.Core;

namespace PcCleaner.Modules.Optimizer;

/// <summary>One compact card in the "Ваш компьютер" grid.</summary>
public sealed class HardwareCard
{
    public HardwareCard()
    {
        CopyCommand = new RelayCommand(() => ClipboardText.TrySet(CopyText));
        // Only cards that name a real device can be searched (not "Нет данных" or a power plan name).
        SearchCommand = new RelayCommand(() => FileTools.SearchWeb(SearchQuery!), () => HasSearch);
    }

    public required string Icon { get; init; }
    public required string Caption { get; init; }
    public required string Title { get; init; }
    public string? Primary { get; init; }
    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();
    public string? Badge { get; init; }
    public Tone BadgeTone { get; init; } = Tone.Neutral;
    public string? ToolTipText { get; init; }

    /// <summary>Text for "Найти в интернете" (full device name).</summary>
    public string? SearchQuery { get; init; }

    /// <summary>Optional action button at the bottom of the card, handled by the page (see <see cref="HardwareCardActions"/>).</summary>
    public string? ActionId { get; init; }

    public string? ActionText { get; init; }

    public string? ActionToolTip { get; init; }

    public bool HasAction => !string.IsNullOrEmpty(ActionId) && !string.IsNullOrEmpty(ActionText);

    public bool HasBadge => !string.IsNullOrEmpty(Badge);
    public bool HasPrimary => !string.IsNullOrEmpty(Primary);
    public bool HasToolTip => !string.IsNullOrWhiteSpace(ToolTipText);
    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchQuery);

    public RelayCommand CopyCommand { get; }
    public RelayCommand SearchCommand { get; }

    public string CopyText
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Caption).Append(": ").AppendLine(Title);
            if (HasPrimary)
                sb.AppendLine(Primary);
            foreach (var d in Details)
                sb.AppendLine(d);
            return sb.ToString().TrimEnd();
        }
    }
}

/// <summary>Ids of the card action buttons.</summary>
public static class HardwareCardActions
{
    public const string BatteryReport = "BatteryReport";
}

/// <summary>Clipboard access that never throws (the clipboard can be locked by another app).</summary>
internal static class ClipboardText
{
    public static bool TrySet(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            Log.Warn($"Не удалось скопировать в буфер обмена: {ex.Message}");
            return false;
        }
    }
}

/// <summary>Builds the hardware cards from the snapshots and the evaluated recommendations.</summary>
public static class HardwareCardBuilder
{
    public static List<HardwareCard> Build(HardwareSnapshot hw, SettingsSnapshot s, IReadOnlyCollection<Recommendation> items, DateTime now)
    {
        bool Has(string id) => items.Any(r => r.Id == id && r.Kind != RecommendationKind.Good);
        bool HasPrefix(string prefix) => items.Any(r => r.Id.StartsWith(prefix, StringComparison.Ordinal) && r.Kind != RecommendationKind.Good);

        var cards = new List<HardwareCard>
        {
            Cpu(hw, s),
            Gpu(hw, now),
            Memory(hw, items, Has),
            Disk(hw),
            Monitor(hw, HasPrefix("Refresh:")),
            Platform(hw, Has("BiosOld")),
            Windows(hw, s, Has("Uptime")),
            Power(hw, s),
        };
        if (hw.IsLaptop && hw.HasBattery)
            cards.Add(Battery(hw, s));
        return cards;
    }

    /// <summary>Why the temperature is missing (shown in the CPU card tooltip).</summary>
    internal static string TemperatureNote(SettingsSnapshot s) => s.TemperatureState switch
    {
        _ when s.TemperatureC is not null =>
            "Температура — с датчика ACPI материнской платы (обычно это зона процессора). Точную температуру каждого ядра " +
            "показывают программы HWiNFO или Core Temp.",
        SensorState.NeedsAdmin =>
            "Температура: нет данных — Windows сообщает показания датчика только программам, запущенным от имени администратора.",
        SensorState.NotSupported =>
            "Температура: нет данных — материнская плата не передаёт температуру в Windows. Её можно посмотреть в BIOS или " +
            "в программах HWiNFO и Core Temp.",
        _ => "Температура: нет данных — не удалось прочитать датчик температуры.",
    };

    private static HardwareCard Cpu(HardwareSnapshot hw, SettingsSnapshot s)
    {
        var cpu = hw.Cpu;
        if (cpu is null)
            return Empty("\uE950", "Процессор");
        var details = new List<string>();
        if (cpu.MaxClockMhz > 0)
            details.Add($"Базовая частота {HardwareText.Ghz(cpu.MaxClockMhz)}");
        details.Add("Температура: " + (s.TemperatureC is { } t ? HardwareText.Celsius(t) : "нет данных"));

        string? badge = null;
        var tone = Tone.Neutral;
        if (s.TemperatureC is { } hot && hot >= RecommendationEngine.HighTemperatureC)
            (badge, tone) = ($"Перегрев: {HardwareText.Celsius(hot)}", Tone.Danger);
        else if (s.CpuLoadPercent is { } load && load > RecommendationEngine.HighCpuLoadPercent)
            (badge, tone) = ($"Загружен на {Format.Percent(Math.Round(load))}", Tone.Warning);

        return new HardwareCard
        {
            Icon = "\uE950",
            Caption = "Процессор",
            Title = HardwareText.ShortCpuName(cpu.Name),
            Primary = cpu.Cores > 0
                ? $"{Format.Count(cpu.Cores, "ядро", "ядра", "ядер")} · {Format.Count(cpu.LogicalProcessors, "поток", "потока", "потоков")}"
                : Format.Count(cpu.LogicalProcessors, "поток", "потока", "потоков"),
            Details = details,
            Badge = badge,
            BadgeTone = tone,
            ToolTipText = cpu.Name + "\n" + TemperatureNote(s),
            SearchQuery = HardwareText.CleanName(cpu.Name),
        };
    }

    private static HardwareCard Gpu(HardwareSnapshot hw, DateTime now)
    {
        var gpus = hw.RealGpus.ToList();
        if (gpus.Count == 0)
            return Empty("\uE964", "Видеокарта");
        var main = gpus.FirstOrDefault(GpuClassifier.IsDiscrete) ?? gpus[0];
        var details = new List<string>();
        if (main.DriverDate is { } date || main.DriverVersion is not null)
        {
            var version = main.Vendor == GpuVendor.Nvidia && HardwareText.NvidiaDriverVersion(main.DriverVersion) is { } nv
                ? nv
                : main.DriverVersion;
            details.Add(main.DriverDate is null ? $"Драйвер {version}" : $"Драйвер {version} от {Format.Date(main.DriverDate)}");
        }
        var others = gpus.Where(g => !ReferenceEquals(g, main)).Select(g => HardwareText.CleanName(g.Name)).ToList();
        if (others.Count > 0)
            details.Add("Также: " + string.Join(", ", others));

        string? badge;
        Tone tone;
        if (main.IsBasicDriver)
            (badge, tone) = ("Нет драйвера", Tone.Danger);
        else if (main.DriverDate is { } d && now - d > RecommendationEngine.DriverMaxAge)
            (badge, tone) = ("Драйвер устарел", Tone.Warning);
        else if (main.DriverDate is not null)
            (badge, tone) = ("Драйвер свежий", Tone.Good);
        else
            (badge, tone) = (null, Tone.Neutral);

        return new HardwareCard
        {
            Icon = "\uE964",
            Caption = "Видеокарта",
            Title = HardwareText.CleanName(main.Name),
            Primary = main.IsIntegrated
                ? "Встроенная графика"
                : main.VramBytes > 0 ? $"{HardwareText.Gb(main.VramBytes)} видеопамяти" : GpuClassifier.VendorName(main.Vendor),
            Details = details,
            Badge = badge,
            BadgeTone = tone,
            ToolTipText = string.Join("\n", gpus.Select(g => $"{g.Name}: драйвер {g.DriverVersion ?? "—"} от {Format.Date(g.DriverDate)}")),
            SearchQuery = HardwareText.CleanName(main.Name),
        };
    }

    private static HardwareCard Memory(HardwareSnapshot hw, IReadOnlyCollection<Recommendation> items, Func<string, bool> has)
    {
        var mods = hw.RamModules;
        var total = hw.TotalRamBytes;
        if (total <= 0)
            return Empty("\uEEA0", "Оперативная память");
        // Same numbers as the engine (incl. the half-clock correction), so the card never contradicts the advice.
        var speed = RecommendationEngine.MemorySpeed(hw);
        var type = mods.Select(m => HardwareText.MemoryTypeName(m.MemoryType)).FirstOrDefault(t => t is not null)
                   ?? speed?.Type
                   ?? mods.Select(m => HardwareText.DecodeMemoryPart(m.PartNumber)?.Type).FirstOrDefault(t => t is not null);
        var configured = speed?.Configured ?? (mods.Count > 0 ? mods.Max(m => m.ConfiguredMhz) : 0);
        var rated = speed?.Rated ?? 0;

        var details = mods
            .GroupBy(m => (m.CapacityBytes, Name: string.Join(" ", new[] { m.Manufacturer, m.PartNumber }.Where(x => !string.IsNullOrWhiteSpace(x)))))
            .Select(g => $"{g.Count()} × {HardwareText.Gb(g.Key.CapacityBytes)}" + (g.Key.Name.Length > 0 ? $" · {g.Key.Name}" : ""))
            .ToList();
        if (hw.RamSlots > 0 && mods.Count > 0)
            details.Add($"Занято слотов: {mods.Count} из {hw.RamSlots}");

        string? badge = null;
        var tone = Tone.Neutral;
        if (has("RamSpeed") && RecommendationEngine.IsXmpOff(hw))
            (badge, tone) = ("Без XMP/EXPO", Tone.Warning);
        else if (has("RamSpeed"))
            (badge, tone) = ("Ниже паспортной частоты", Tone.Warning);
        else if (has("RamSingle") || has("RamOneChannel"))
            (badge, tone) = ("Одноканальный режим", Tone.Warning);
        else if (has("RamAsymmetric"))
            (badge, tone) = ("Частично двухканальный", Tone.Info);
        else if (items.FirstOrDefault(r => r.Id == "Ok:RamChannels") is { } ok)
            (badge, tone) = (ok.Title.Contains("двухканальном", StringComparison.Ordinal) ? "Двухканальный режим" : "Многоканальный режим", Tone.Good);

        string? primary = null;
        if (configured > 0)
            primary = rated > 0 && RecommendationEngine.IsMemoryBelowRated(rated, configured)
                ? $"{configured} МГц из {rated} МГц"
                : HardwareText.Mhz(configured);

        return new HardwareCard
        {
            Icon = "\uEEA0",
            Caption = "Оперативная память",
            Title = (HardwareText.Gb(total) + " " + (type ?? "")).Trim(),
            Primary = primary,
            Details = details,
            Badge = badge,
            BadgeTone = tone,
            ToolTipText = mods.Count == 0
                ? null
                : string.Join("\n", mods.Select(m =>
                    $"{m.DeviceLocator ?? m.BankLabel ?? "Слот"}: {HardwareText.Gb(m.CapacityBytes)}, " +
                    $"{(m.ConfiguredMhz > 0 ? m.ConfiguredMhz : 0)} / {(m.RatedMhz > 0 ? m.RatedMhz : 0)} МГц (сейчас / паспорт)" +
                    (string.IsNullOrWhiteSpace(m.PartNumber) ? "" : $", {m.Manufacturer} {m.PartNumber}") +
                    (HardwareText.RatedSpeedFromPartNumber(m.PartNumber) is { } part && part != m.RatedMhz
                        ? $" — по маркировке {part} МГц"
                        : ""))),
        };
    }

    private static HardwareCard Disk(HardwareSnapshot hw)
    {
        var disk = hw.SystemDisk;
        if (disk is null)
            return Empty("\uEDA2", "Системный диск");
        var kind = disk.MediaKind switch
        {
            DiskMediaKind.Ssd => "SSD",
            DiskMediaKind.Hdd => "HDD",
            _ => null,
        };
        var parts = new[] { kind, disk.BusType, disk.SizeBytes > 0 ? Format.Bytes(disk.SizeBytes) : null }
            .Where(p => !string.IsNullOrEmpty(p));
        return new HardwareCard
        {
            Icon = "\uEDA2",
            Caption = "Системный диск",
            Title = string.IsNullOrWhiteSpace(disk.Model) ? $"Диск {disk.DriveLetter}:" : disk.Model!,
            Primary = string.Join(" · ", parts),
            Details = new[] { $"На нём установлена Windows ({disk.DriveLetter}:)" },
            Badge = disk.MediaKind switch
            {
                DiskMediaKind.Ssd => "Быстрый SSD",
                DiskMediaKind.Hdd => "Медленный HDD",
                _ => null,
            },
            BadgeTone = disk.MediaKind == DiskMediaKind.Hdd ? Tone.Danger : Tone.Good,
            SearchQuery = disk.Model,
            ToolTipText = disk.MediaKind switch
            {
                DiskMediaKind.Ssd => "SSD — твердотельный накопитель без движущихся частей: Windows и программы запускаются с него быстро.",
                DiskMediaKind.Hdd => "HDD — механический жёсткий диск с вращающимися пластинами. Для системы он медленный — лучше заменить на SSD.",
                _ => "Windows не сообщила тип накопителя.",
            },
        };
    }

    private static HardwareCard Monitor(HardwareSnapshot hw, bool belowMax)
    {
        var d = hw.Displays.FirstOrDefault(x => x.IsPrimary) ?? hw.Displays.FirstOrDefault();
        if (d is null)
            return Empty("\uE7F4", "Монитор");
        var details = new List<string>();
        // "Below max" uses exactly the engine's rule, so the badge and the recommendation always agree.
        var primaryBelow = RecommendationEngine.IsRefreshBelowMax(d);
        var atMax = d.MaxHzAtCurrentResolution <= d.CurrentHz;
        // 0 or 1 Hz means "hardware default" (remote/virtual displays): the real rate is unknown.
        var hzKnown = d.CurrentHz > 1;
        details.Add(!hzKnown ? "Частота обновления не сообщается" :
            atMax ? "Работает на максимальной частоте" : $"Поддерживает до {d.MaxHzAtCurrentResolution} Гц");
        if (hw.Displays.Count > 1)
            details.Add($"Ещё {Format.Count(hw.Displays.Count - 1, "монитор", "монитора", "мониторов")}");
        string? badge;
        Tone tone;
        if (!hzKnown)
            (badge, tone) = (belowMax ? "Проверьте второй монитор" : null, belowMax ? Tone.Warning : Tone.Neutral);
        else if (primaryBelow)
            (badge, tone) = ($"{d.CurrentHz} из {d.MaxHzAtCurrentResolution} Гц", Tone.Warning);
        else if (belowMax)
            (badge, tone) = ("Проверьте второй монитор", Tone.Warning);
        else if (atMax)
            (badge, tone) = ($"{d.CurrentHz} Гц — максимум", Tone.Good);
        else
            (badge, tone) = ($"{d.CurrentHz} из {d.MaxHzAtCurrentResolution} Гц", Tone.Neutral); // e.g. 59 of 60 Hz: not worth advice
        return new HardwareCard
        {
            Icon = "\uE7F4",
            Caption = hw.Displays.Count > 1 ? "Основной монитор" : "Монитор",
            Title = string.IsNullOrWhiteSpace(d.MonitorName) ? "Монитор" : d.MonitorName!,
            Primary = hzKnown ? $"{d.Width}×{d.Height} · {d.CurrentHz} Гц" : $"{d.Width}×{d.Height}",
            Details = details,
            Badge = badge,
            BadgeTone = tone,
            SearchQuery = string.IsNullOrWhiteSpace(d.MonitorName) || d.MonitorName.StartsWith("Монитор", StringComparison.Ordinal)
                ? null
                : $"{d.MonitorName} монитор",
            ToolTipText = string.Join("\n", hw.Displays.Select(x =>
                $"{x.MonitorName ?? x.DeviceName}: {x.Width}×{x.Height}, {x.CurrentHz} Гц (макс. {x.MaxHzAtCurrentResolution} Гц), видеокарта: {x.AdapterName}")),
        };
    }

    internal static string BootModeText(BootMode mode) => mode switch
    {
        BootMode.Uefi => "UEFI",
        BootMode.LegacyBios => "Legacy BIOS",
        _ => "режим загрузки не определён",
    };

    internal static string VirtualizationText(bool? enabled) => enabled switch
    {
        true => "виртуализация включена",
        false => "виртуализация выключена",
        _ => "виртуализация: нет данных",
    };

    /// <summary>"Прошивка и платформа": the board, BIOS version/date, boot mode and hardware virtualization.</summary>
    private static HardwareCard Platform(HardwareSnapshot hw, bool biosOld)
    {
        var details = new List<string>();
        if (hw.BiosVersion is not null || hw.BiosDate is not null)
            details.Add($"BIOS {hw.BiosVersion ?? "—"} от {Format.Date(hw.BiosDate)}");
        if (hw.BoardProduct is not null && hw.BoardManufacturer is not null)
            details.Add(hw.BoardManufacturer);
        var name = string.Join(" ", new[] { hw.BoardManufacturer, hw.BoardProduct }.Where(x => !string.IsNullOrWhiteSpace(x)));

        string? badge = null;
        var tone = Tone.Neutral;
        if (hw.BootMode == BootMode.LegacyBios)
            (badge, tone) = ("Legacy BIOS", Tone.Warning);
        else if (biosOld && hw.BiosDate is { } date)
            (badge, tone) = ($"BIOS {date.Year} года", Tone.Neutral);
        else if (hw.BootMode == BootMode.Uefi)
            (badge, tone) = ("Загрузка UEFI", Tone.Good);

        var virtualization = hw.VirtualizationEnabled switch
        {
            true when hw.HypervisorPresent == true =>
                "Виртуализация процессора включена: сейчас работает гипервизор Windows (Hyper-V, WSL 2 или похожие функции).",
            true => "Виртуализация процессора (VT-x / SVM) включена — работают WSL, Docker, эмуляторы Android и виртуальные машины.",
            false => "Виртуализация процессора (VT-x / SVM) выключена в BIOS. Она нужна только для WSL, Docker, эмуляторов Android " +
                     "и виртуальных машин.",
            _ => "Windows не сообщила, включена ли виртуализация процессора.",
        };
        return new HardwareCard
        {
            Icon = "\uEEA1",
            Caption = "Прошивка и платформа",
            Title = hw.BoardProduct ?? hw.BoardManufacturer ?? "Материнская плата",
            Primary = $"{BootModeText(hw.BootMode)} · {VirtualizationText(hw.VirtualizationEnabled)}",
            Details = details,
            Badge = badge,
            BadgeTone = tone,
            SearchQuery = name.Length > 0 ? name : null,
            ToolTipText = "BIOS (UEFI) — встроенная программа материнской платы. В нём включаются XMP/EXPO для памяти, " +
                          "виртуализация и порядок загрузки дисков.\n" +
                          (hw.BootMode switch
                          {
                              BootMode.Uefi => "Режим загрузки UEFI — современный: быстрый старт, Secure Boot, поддержка Windows 11.",
                              BootMode.LegacyBios => "Режим загрузки Legacy BIOS (CSM) — устаревший: загрузка медленнее, нет Secure Boot, " +
                                                     "Windows 11 официально не поддерживается.",
                              _ => "Windows не сообщила режим загрузки.",
                          }) + "\n" + virtualization,
        };
    }

    internal static string BatteryStateText(BatteryChargeState state) => state switch
    {
        BatteryChargeState.Charging => "Заряжается",
        BatteryChargeState.Discharging => "Работает от батареи",
        BatteryChargeState.Full => "Заряжена, ноутбук от сети",
        BatteryChargeState.PluggedIn => "От сети, не заряжается",
        _ => "Состояние неизвестно",
    };

    /// <summary>Laptop battery: charge, state, wear (capacity) and the "Отчёт о батарее" action.</summary>
    private static HardwareCard Battery(HardwareSnapshot hw, SettingsSnapshot s)
    {
        var battery = hw.Battery;
        var wear = battery?.WearPercent;
        var details = new List<string>();
        if (wear is { } w && battery is not null)
        {
            details.Add($"Износ: {w:0}%");
            details.Add($"Ёмкость: {HardwareText.WattHours(battery.FullChargedCapacityMwh)} из {HardwareText.WattHours(battery.DesignedCapacityMwh)}");
        }
        else
        {
            details.Add("Износ: нет данных");
        }

        string? badge = null;
        var tone = Tone.Neutral;
        if (wear > RecommendationEngine.BatteryWearMediumPercent)
            (badge, tone) = ("Пора заменить", Tone.Warning);
        else if (wear > RecommendationEngine.BatteryWearLowPercent)
            (badge, tone) = ($"Износ {wear:0}%", Tone.Info);
        else if (wear is not null)
            (badge, tone) = ("В хорошем состоянии", Tone.Good);

        var wearNote = (wear, battery?.CapacityState) switch
        {
            ({ }, _) => "Износ — насколько ёмкость батареи уменьшилась по сравнению с новой. До 30% — нормально для батареи возрастом в пару лет.",
            (null, SensorState.NeedsAdmin) => "Износ: нет данных — Windows сообщает ёмкость батареи только программам, запущенным от имени администратора.",
            _ => "Износ: нет данных — ноутбук не сообщает ёмкость батареи. Её можно увидеть в «Отчёте о батарее».",
        };
        return new HardwareCard
        {
            Icon = "\uE83F",
            Caption = "Батарея",
            Title = s.BatteryChargePercent is { } charge ? $"Заряд {charge}%" : "Заряд неизвестен",
            Primary = BatteryStateText(s.BatteryState),
            Details = details,
            Badge = badge,
            BadgeTone = tone,
            ToolTipText = string.Join("\n", new[] { battery?.Name is { } n ? $"Батарея: {n}" : null, wearNote }.Where(x => x is not null)),
            ActionId = HardwareCardActions.BatteryReport,
            ActionText = "Отчёт о батарее",
            ActionToolTip = "Windows создаст подробный отчёт (история ёмкости, время работы) в папке «Документы» и откроет его в браузере. " +
                            "Ничего в системе не меняется.",
        };
    }

    private static HardwareCard Windows(HardwareSnapshot hw, SettingsSnapshot s, bool longUptime)
    {
        var caption = hw.OsCaption?
            .Replace("Microsoft ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Майкрософт ", "", StringComparison.OrdinalIgnoreCase)
            .Trim() ?? "Windows";
        var version = new List<string>();
        if (!string.IsNullOrWhiteSpace(hw.OsDisplayVersion))
            version.Add($"Версия {hw.OsDisplayVersion}");
        if (!string.IsNullOrWhiteSpace(hw.OsBuild))
            version.Add($"сборка {hw.OsBuild}");
        var details = new List<string>();
        if (hw.OsInstallDate is not null)
            details.Add($"Установлена {Format.Date(hw.OsInstallDate)}");
        if (s.Uptime > TimeSpan.Zero)
            details.Add($"Без перезагрузки: {Format.Duration(s.Uptime)}");
        string? badge = null;
        var tone = Tone.Neutral;
        if (s.PendingReboot)
            (badge, tone) = ("Ждёт перезагрузки", Tone.Warning);
        else if (longUptime)
            (badge, tone) = ("Давно не перезагружался", Tone.Info);
        return new HardwareCard
        {
            Icon = hw.IsLaptop ? "\uE7F8" : "\uE977",
            Caption = "Система",
            Title = caption,
            Primary = version.Count > 0 ? string.Join(" · ", version) : null,
            Details = details,
            Badge = badge,
            BadgeTone = tone,
            ToolTipText = string.Join("\n", new[]
            {
                hw.OsCaption,
                hw.OsVersion is null ? null : $"Версия ядра: {hw.OsVersion}",
                hw.LastBoot is null ? null : $"Последняя загрузка: {Format.DateTime(hw.LastBoot)}",
            }.Where(x => !string.IsNullOrWhiteSpace(x))),
        };
    }

    private static HardwareCard Power(HardwareSnapshot hw, SettingsSnapshot s)
    {
        var plan = s.ActivePlan;
        var details = new List<string>
        {
            "Игровой режим: " + (s.GameModeEnabled ? "включён" : "выключен"),
        };
        if (s.HagsSupported == true || s.HagsRegistryMode is not null)
            details.Add("Планирование GPU: " + (s.HagsRegistryMode == 2 || (s.HagsRegistryMode is null && s.HagsActive == true) ? "включено" : "выключено"));
        var (badge, tone) = plan?.Kind switch
        {
            PowerPlanKind.HighPerformance or PowerPlanKind.UltimatePerformance => ("Производительный", Tone.Good),
            PowerPlanKind.Balanced => ("Сбалансированный", hw.IsLaptop ? Tone.Good : Tone.Neutral),
            PowerPlanKind.PowerSaver => ("Экономия энергии", Tone.Warning),
            PowerPlanKind.Custom => ("План производителя", Tone.Neutral),
            _ => ((string?)null, Tone.Neutral),
        };
        return new HardwareCard
        {
            Icon = "\uE945",
            Caption = "Электропитание",
            Title = plan is null ? "План не определён" : PowerPlans.DisplayName(plan),
            Primary = hw.IsLaptop
                ? s.OnAcPower switch
                {
                    true => "Ноутбук · от сети",
                    false => "Ноутбук · от батареи",
                    _ => "Ноутбук",
                }
                : "Настольный компьютер",
            Details = details,
            Badge = badge,
            BadgeTone = tone,
            ToolTipText = s.Plans.Count == 0
                ? null
                : "Планы электропитания на этом компьютере:\n" +
                  string.Join("\n", s.Plans.Select(p => (p.IsActive ? "● " : "○ ") + PowerPlans.DisplayName(p))),
        };
    }

    private static HardwareCard Empty(string icon, string caption) => new()
    {
        Icon = icon,
        Caption = caption,
        Title = "Нет данных",
        Primary = "Windows не сообщила сведения об устройстве",
    };

    /// <summary>Plain-text report of the PC for forums / support.</summary>
    public static string BuildReport(IEnumerable<HardwareCard> cards, IEnumerable<Recommendation> recommendations)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"WaterCa7s PC Cleaner {AppInfo.Version} — сведения о компьютере ({Format.DateTime(DateTime.Now)})");
        sb.AppendLine();
        foreach (var c in cards)
        {
            sb.AppendLine(c.CopyText);
            sb.AppendLine();
        }
        var recs = recommendations.Where(r => r.Kind is not (RecommendationKind.Good or RecommendationKind.Navigate)).ToList();
        if (recs.Count > 0)
        {
            sb.AppendLine("Рекомендации:");
            foreach (var r in recs)
                sb.AppendLine($"• {r.Title}" + (r.HasCurrentValue ? $" (сейчас: {r.CurrentValue})" : ""));
        }
        return sb.ToString().TrimEnd();
    }
}
