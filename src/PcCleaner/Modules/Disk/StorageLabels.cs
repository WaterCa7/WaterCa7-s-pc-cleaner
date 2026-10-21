using PcCleaner.Core.Localization;
namespace PcCleaner.Modules.Disk;

/// <summary>Kind of physical storage device (inferred from MSFT_PhysicalDisk).</summary>
public enum DriveKind
{
    Unknown,
    Hdd,
    Ssd,
    NvmeSsd,
    Scm,
}

/// <summary>MSFT_PhysicalDisk.HealthStatus as an enum.</summary>
public enum DiskHealth
{
    Unknown,
    Healthy,
    Warning,
    Unhealthy,
}

/// <summary>Visual tone of a badge / status (mapped to theme brushes by <see cref="ToneBrushConverter"/>).</summary>
public enum Tone
{
    Neutral,
    Accent,
    Info,
    Warning,
    Danger,
}

/// <summary>
/// Maps raw values of the Storage WMI classes (MSFT_Disk / MSFT_PhysicalDisk) to Russian labels.
/// </summary>
internal static class StorageLabels
{
    public const int BusNvme = 17;
    public const int BusUsb = 7;

    /// <summary>
    /// MediaType: 3 = HDD, 4 = SSD, 5 = SCM, 0 = unspecified. When unspecified, NVMe bus means SSD,
    /// SpindleSpeed 0 means "non-rotational" and any real RPM value means HDD (uint.MaxValue = unknown).
    /// </summary>
    public static DriveKind InferKind(long? mediaType, long? busType, long? spindleSpeed)
    {
        var nvme = busType == BusNvme;
        switch (mediaType)
        {
            case 3:
                return DriveKind.Hdd;
            case 4:
                return nvme ? DriveKind.NvmeSsd : DriveKind.Ssd;
            case 5:
                return DriveKind.Scm;
        }
        if (nvme)
            return DriveKind.NvmeSsd;
        if (busType == 18)
            return DriveKind.Scm;
        if (spindleSpeed is > 0 and < uint.MaxValue)
            return DriveKind.Hdd;
        if (spindleSpeed == 0)
            return DriveKind.Ssd;
        return DriveKind.Unknown;
    }

    public static bool IsSolidState(DriveKind kind) => kind is DriveKind.Ssd or DriveKind.NvmeSsd or DriveKind.Scm;

    public static string KindLabel(DriveKind kind) => kind switch
    {
        DriveKind.Hdd => "HDD",
        DriveKind.Ssd => "SSD",
        DriveKind.NvmeSsd => "NVMe SSD",
        DriveKind.Scm => "SCM",
        _ => T._("Тип неизвестен"),
    };

    public static string KindDescription(DriveKind kind) => kind switch
    {
        DriveKind.Hdd => T._("Жёсткий диск (HDD) с вращающимися пластинами. Он медленнее SSD, и ему полезна периодическая дефрагментация — Windows делает её сама раз в неделю."),
        DriveKind.Ssd => T._("Твердотельный накопитель (SSD): быстрый, без движущихся частей. Дефрагментация ему не нужна — вместо неё Windows отправляет команду TRIM."),
        DriveKind.NvmeSsd => T._("Быстрый твердотельный накопитель, подключённый по шине NVMe (PCI Express). Дефрагментация ему не нужна — вместо неё Windows отправляет команду TRIM."),
        DriveKind.Scm => T._("Накопитель на энергонезависимой памяти (Storage Class Memory)."),
        _ => T._("Windows не сообщает тип этого накопителя (так бывает с виртуальными и некоторыми USB-дисками)."),
    };

    public static string BusLabel(long? busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => T._("SD-карта"),
        13 => "MMC",
        14 => T._("Виртуальный"),
        15 => T._("Виртуальный (файл)"),
        16 => T._("Дисковые пространства"),
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => T._("Неизвестно"),
    };

    /// <summary>HealthStatus: 0 = Healthy, 1 = Warning, 2 = Unhealthy, 5 = Unknown.</summary>
    public static DiskHealth ToHealth(long? healthStatus) => healthStatus switch
    {
        0 => DiskHealth.Healthy,
        1 => DiskHealth.Warning,
        2 => DiskHealth.Unhealthy,
        _ => DiskHealth.Unknown,
    };

    public static string HealthLabel(DiskHealth health) => health switch
    {
        DiskHealth.Healthy => T._("Исправен"),
        DiskHealth.Warning => T._("Предупреждение"),
        DiskHealth.Unhealthy => T._("Неисправен"),
        _ => T._("Нет данных"),
    };

    public static Tone HealthTone(DiskHealth health) => health switch
    {
        DiskHealth.Healthy => Tone.Accent,
        DiskHealth.Warning => Tone.Warning,
        DiskHealth.Unhealthy => Tone.Danger,
        _ => Tone.Neutral,
    };

    public static string HealthDescription(DiskHealth health) => health switch
    {
        DiskHealth.Healthy => T._("Накопитель сообщает, что с ним всё в порядке."),
        DiskHealth.Warning => T._("Накопитель сообщает о проблемах (например, растёт число ошибок или он изношен). Сделайте резервную копию важных файлов."),
        DiskHealth.Unhealthy => T._("Накопитель сообщает о серьёзной неисправности и может выйти из строя в любой момент. Срочно скопируйте важные файлы на другой диск или в облако."),
        _ => T._("Windows не получила от накопителя сведений о его состоянии."),
    };

    /// <summary>One value of MSFT_PhysicalDisk.OperationalStatus.</summary>
    public static string OperationalStatusLabel(long code) => code switch
    {
        0 => T._("Неизвестно"),
        1 => T._("Другое"),
        2 => T._("Работает нормально"),
        3 => T._("Работает с ошибками"),
        4 => T._("Перегружен"),
        5 => T._("Прогнозируется сбой"),
        6 => T._("Ошибка"),
        7 => T._("Неустранимая ошибка"),
        8 => T._("Запускается"),
        9 => T._("Останавливается"),
        10 => T._("Остановлен"),
        11 => T._("На обслуживании"),
        12 => T._("Нет связи"),
        13 => T._("Связь потеряна"),
        14 => T._("Прервано"),
        15 => T._("Неактивен"),
        16 => T._("Ошибка связанного устройства"),
        17 => T._("Завершено"),
        _ => T.F("Код состояния {0}", code),
    };

    /// <summary>Combined label for the OperationalStatus array (WMI returns ushort[]).</summary>
    public static string OperationalStatusLabel(object? raw)
    {
        var codes = ToCodes(raw);
        return codes.Count == 0 ? T._("Нет данных") : string.Join(", ", codes.Select(OperationalStatusLabel).Distinct());
    }

    public static bool IsOperationalOk(object? raw)
    {
        var codes = ToCodes(raw);
        return codes.Count == 0 || codes.All(c => c is 2 or 11 or 17);
    }

    internal static List<long> ToCodes(object? raw)
    {
        var list = new List<long>();
        switch (raw)
        {
            case null:
                break;
            case string s:
                if (long.TryParse(s, out var parsed))
                    list.Add(parsed);
                break;
            case System.Collections.IEnumerable items:
                foreach (var item in items)
                {
                    try
                    {
                        list.Add(Convert.ToInt64(item));
                    }
                    catch
                    {
                        // ignore odd values
                    }
                }
                break;
            default:
                try
                {
                    list.Add(Convert.ToInt64(raw));
                }
                catch
                {
                    // ignore
                }
                break;
        }
        return list;
    }

    /// <summary>SpindleSpeed: 0 = no spindle (SSD), uint.MaxValue = unknown, otherwise RPM.</summary>
    public static string? SpindleLabel(long? rpm) => rpm is > 0 and < uint.MaxValue ? T.F("{0} об/мин", rpm) : null;
}
