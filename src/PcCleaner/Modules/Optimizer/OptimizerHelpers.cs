using System.Globalization;
using System.Text.RegularExpressions;
using PcCleaner.Core;

namespace PcCleaner.Modules.Optimizer;

/// <summary>Pure text helpers for hardware values (unit-testable).</summary>
public static class HardwareText
{
    public const long GiB = 1024L * 1024 * 1024;

    private static readonly Regex ChannelRegex = new(@"channel\s*[-_]?\s*([A-H])(?![A-Z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DimmLetterRegex = new(@"(?:DIMM|DDR\d?)[\s_\-]?([A-H])\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Memory channel letter from Win32_PhysicalMemory slot names:
    /// "ChannelA-DIMM0", "Controller0-ChannelB-DIMM1", "P0 CHANNEL A", "DIMM_A1", "DDR4_B2". Null if unknown.
    /// </summary>
    public static string? ParseChannel(string? deviceLocator, string? bankLabel)
    {
        foreach (var text in new[] { deviceLocator, bankLabel })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var m = ChannelRegex.Match(text);
            if (m.Success)
                return m.Groups[1].Value.ToUpperInvariant();
        }
        foreach (var text in new[] { deviceLocator, bankLabel })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var m = DimmLetterRegex.Match(text);
            if (m.Success)
                return m.Groups[1].Value.ToUpperInvariant();
        }
        return null;
    }

    /// <summary>SMBIOS memory type -> "DDR4". Null if unknown.</summary>
    public static string? MemoryTypeName(int smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        22 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => null,
    };

    /// <summary>Whole gigabytes for memory sizes: 8589934592 -> "8 ГБ", 12.5 GiB -> "12,5 ГБ".</summary>
    public static string Gb(long bytes)
    {
        if (bytes <= 0)
            return "—";
        var gb = bytes / (double)GiB;
        return (gb >= 10 ? Math.Round(gb) : Math.Round(gb, 1)).ToString("0.#", Format.Ru) + " ГБ";
    }

    public static string Mhz(int mhz) => $"{mhz.ToString(CultureInfo.InvariantCulture)} МГц";

    /// <summary>3792 -> "3,8 ГГц" (WMI reports clocks with bus-clock jitter, so one decimal is honest enough).</summary>
    public static string Ghz(int mhz) => Math.Round(mhz / 1000.0, 1).ToString("0.0", Format.Ru) + " ГГц";

    /// <summary>"Intel(R) Core(TM) i7-10700K CPU @ 3.80GHz" -> "Intel Core i7-10700K".</summary>
    public static string ShortCpuName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Неизвестный процессор";
        var s = Regex.Replace(name, @"\((R|TM|tm|r)\)|®|™", "");
        s = Regex.Replace(s, @"\s+@\s+[\d.,]+\s*GHz", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(CPU|Processor|\d+-Core Processor|with Radeon Graphics)\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s.Length == 0 ? name.Trim() : s;
    }

    /// <summary>Removes (R)/(TM) marks from a device name.</summary>
    public static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var s = Regex.Replace(name, @"\((R|TM|tm|r)\)|®|™", "");
        return Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    /// <summary>WDDM major from a Windows driver version: "32.0.16.1714" -> 32 (27 = WDDM 2.7, 30 = WDDM 3.0).</summary>
    public static int DriverWddmMajor(string? driverVersion)
    {
        if (string.IsNullOrWhiteSpace(driverVersion))
            return 0;
        var first = driverVersion.Split('.')[0];
        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>NVIDIA marketing version from the Windows driver version: "32.0.16.1714" -> "617.14".</summary>
    public static string? NvidiaDriverVersion(string? driverVersion)
    {
        if (string.IsNullOrWhiteSpace(driverVersion))
            return null;
        var parts = driverVersion.Split('.');
        if (parts.Length != 4 || parts[3].Length > 4 || !parts[2].All(char.IsDigit) || !parts[3].All(char.IsDigit))
            return null;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5)
            return null;
        var last5 = digits[^5..];
        return last5[..3].TrimStart('0') + "." + last5[3..];
    }

    /// <summary>"5 месяцев", "1 год", "2 года и 3 месяца".</summary>
    public static string AgeText(DateTime from, DateTime now)
    {
        var months = (now.Year - from.Year) * 12 + now.Month - from.Month;
        if (now.Day < from.Day)
            months--;
        months = Math.Max(0, months);
        if (months < 1)
        {
            var days = Math.Max(0, (int)(now - from).TotalDays);
            return Format.Count(days, "день", "дня", "дней");
        }
        if (months < 12)
            return Format.Count(months, "месяц", "месяца", "месяцев");
        var years = months / 12;
        var rest = months % 12;
        var y = Format.Count(years, "год", "года", "лет");
        return rest == 0 ? y : $"{y} и {Format.Count(rest, "месяц", "месяца", "месяцев")}";
    }

    public static string DaysText(TimeSpan span) => Format.Count((long)span.TotalDays, "день", "дня", "дней");
}

/// <summary>Classifies video adapters by name and PnP id.</summary>
public static class GpuClassifier
{
    public const string NvidiaDriverUrl = "https://www.nvidia.com/ru-ru/drivers/";
    public const string AmdDriverUrl = "https://www.amd.com/ru/support/download/drivers.html";
    public const string IntelDriverUrl = "https://www.intel.ru/content/www/ru/ru/support/detect.html";

    private static readonly string[] VirtualMarkers =
    {
        "remote", "virtual", "mirage", "parsec", "citrix", "vmware", "virtualbox", "hyper-v", "indirect display",
        "iddcx", "spacedesk", "displaylink", "sunshine", "usb mobile monitor", "dameware", "radmin", "teamviewer",
    };

    private static readonly string[] BasicDriverNames =
    {
        "microsoft basic display adapter", "базовый видеоадаптер (майкрософт)", "microsoft basic render driver",
        "standard vga graphics adapter", "стандартный vga графический адаптер",
    };

    public static GpuVendor Vendor(string? name, string? pnpDeviceId)
    {
        var pnp = pnpDeviceId?.ToUpperInvariant() ?? "";
        if (pnp.Contains("VEN_10DE"))
            return GpuVendor.Nvidia;
        if (pnp.Contains("VEN_1002") || pnp.Contains("VEN_1022"))
            return GpuVendor.Amd;
        if (pnp.Contains("VEN_8086"))
            return GpuVendor.Intel;
        var n = name?.ToLowerInvariant() ?? "";
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro"))
            return GpuVendor.Nvidia;
        if (n.Contains("amd") || n.Contains("radeon"))
            return GpuVendor.Amd;
        if (n.Contains("intel"))
            return GpuVendor.Intel;
        if (n.Contains("microsoft") || n.Contains("майкрософт"))
            return GpuVendor.Microsoft;
        return GpuVendor.Unknown;
    }

    public static bool IsBasicDriver(string? name)
    {
        var n = name?.Trim().ToLowerInvariant() ?? "";
        return BasicDriverNames.Any(b => n == b);
    }

    public static bool IsVirtual(string? name, string? pnpDeviceId)
    {
        var pnp = pnpDeviceId?.ToUpperInvariant() ?? "";
        if (pnp.StartsWith(@"ROOT\", StringComparison.Ordinal) || pnp.StartsWith(@"SWD\", StringComparison.Ordinal))
            return true;
        var n = name?.ToLowerInvariant() ?? "";
        if (n.Length == 0)
            return true;
        return VirtualMarkers.Any(n.Contains);
    }

    /// <summary>Integrated graphics (Intel UHD/Iris, AMD "Radeon Graphics" APUs, Intel Arc iGPU without a model number).</summary>
    public static bool IsIntegrated(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var n = name.ToLowerInvariant();
        if (n.Contains("intel"))
        {
            if (n.Contains("arc") && Regex.IsMatch(n, @"\b[ab]\d{3}"))
                return false;
            return n.Contains("uhd") || n.Contains("hd graphics") || n.Contains("iris") || n.Contains("graphics");
        }
        if (n.Contains("amd") || n.Contains("radeon"))
        {
            if (Regex.IsMatch(n, @"\brx\s?\d") || n.Contains("radeon pro") || n.Contains("firepro") || n.Contains("instinct"))
                return false;
            return n.Contains("graphics") || Regex.IsMatch(n, @"\b\d{3}m\b");
        }
        return false;
    }

    public static bool IsDiscrete(GpuInfo gpu)
        => !gpu.IsVirtual && !gpu.IsBasicDriver && !gpu.IsIntegrated
           && gpu.Vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel;

    /// <summary>
    /// Fallback when the driver could not be asked directly: WDDM 2.7+ driver and a GPU generation that supports
    /// hardware-accelerated GPU scheduling (NVIDIA GTX 10xx and newer, AMD RX 5000+, Intel Arc).
    /// </summary>
    public static bool LikelySupportsHags(GpuInfo gpu)
    {
        if (HardwareText.DriverWddmMajor(gpu.DriverVersion) < 27 || !IsDiscrete(gpu))
            return false;
        var n = gpu.Name.ToLowerInvariant();
        return gpu.Vendor switch
        {
            GpuVendor.Nvidia => Regex.IsMatch(n, @"\b(rtx|titan)\b") || Regex.IsMatch(n, @"gtx\s?1[06]\d{2}"),
            GpuVendor.Amd => Regex.IsMatch(n, @"\brx\s?(5|6|7|9)\d{3}"),
            GpuVendor.Intel => n.Contains("arc"),
            _ => false,
        };
    }

    /// <summary>NVIDIA RTX 40/50 series (DLSS Frame Generation needs HAGS).</summary>
    public static bool IsFrameGenerationCapable(GpuInfo gpu)
        => gpu.Vendor == GpuVendor.Nvidia && Regex.IsMatch(gpu.Name, @"RTX\s?[45]0\d{2}", RegexOptions.IgnoreCase);

    public static string? DriverUrl(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => NvidiaDriverUrl,
        GpuVendor.Amd => AmdDriverUrl,
        GpuVendor.Intel => IntelDriverUrl,
        _ => null,
    };

    public static string VendorName(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        GpuVendor.Microsoft => "Microsoft",
        _ => "производителя",
    };
}

/// <summary>Well-known power plans and powercfg output parsing (Russian and English Windows).</summary>
public static class PowerPlans
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";

    private static readonly Regex GuidLine = new(
        @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*(?:\((.*)\))?\s*(\*)?\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex GuidOnly = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, PowerPlanKind> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Максимальная производительность"] = PowerPlanKind.UltimatePerformance,
        ["Ultimate Performance"] = PowerPlanKind.UltimatePerformance,
        ["Высокая производительность"] = PowerPlanKind.HighPerformance,
        ["High performance"] = PowerPlanKind.HighPerformance,
        ["Сбалансированная"] = PowerPlanKind.Balanced,
        ["Сбалансированный"] = PowerPlanKind.Balanced,
        ["Balanced"] = PowerPlanKind.Balanced,
        ["Экономия энергии"] = PowerPlanKind.PowerSaver,
        ["Экономия электроэнергии"] = PowerPlanKind.PowerSaver,
        ["Power saver"] = PowerPlanKind.PowerSaver,
    };

    public static bool IsGuid(string? text) => text is not null && GuidOnly.IsMatch(text.Trim());

    public static PowerPlanKind Classify(string? guid, string? name)
    {
        switch (guid?.Trim().ToLowerInvariant())
        {
            case Balanced:
                return PowerPlanKind.Balanced;
            case HighPerformance:
                return PowerPlanKind.HighPerformance;
            case UltimatePerformance:
                return PowerPlanKind.UltimatePerformance;
            case PowerSaver:
                return PowerPlanKind.PowerSaver;
        }
        // Copies made with "powercfg -duplicatescheme" keep the standard name but get a new GUID.
        return name is not null && KnownNames.TryGetValue(name.Trim(), out var kind) ? kind : PowerPlanKind.Custom;
    }

    /// <summary>Parses "powercfg /list" output. The active plan is marked with "*".</summary>
    public static List<PowerPlan> ParseList(string? output)
    {
        var plans = new List<PowerPlan>();
        if (string.IsNullOrEmpty(output))
            return plans;
        foreach (var raw in output.Split('\n'))
        {
            var m = GuidLine.Match(raw.TrimEnd('\r', ' '));
            if (!m.Success)
                continue;
            var guid = m.Groups[1].Value.ToLowerInvariant();
            if (plans.Any(p => p.Guid == guid))
                continue;
            plans.Add(new PowerPlan(guid, m.Groups[2].Value.Trim(), m.Groups[3].Success));
        }
        return plans;
    }

    /// <summary>Parses "powercfg /getactivescheme": "GUID схемы питания: 381b... (Сбалансированная)".</summary>
    public static PowerPlan? ParseActive(string? output)
    {
        var plan = ParseList(output).FirstOrDefault();
        return plan is null ? null : plan with { IsActive = true };
    }

    /// <summary>Russian display name for a plan kind (used when a plan has no readable name).</summary>
    public static string DisplayName(PowerPlan plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.Name))
            return plan.Name;
        return plan.Kind switch
        {
            PowerPlanKind.UltimatePerformance => "Максимальная производительность",
            PowerPlanKind.HighPerformance => "Высокая производительность",
            PowerPlanKind.Balanced => "Сбалансированная",
            PowerPlanKind.PowerSaver => "Экономия энергии",
            _ => plan.Guid,
        };
    }
}
