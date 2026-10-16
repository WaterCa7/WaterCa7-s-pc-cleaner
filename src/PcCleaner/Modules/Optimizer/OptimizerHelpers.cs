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

    // "Controller1-ChannelA-DIMM0" (Intel 12th gen+: one memory controller per channel) and "P1 CHANNEL A" (second CPU socket).
    private static readonly Regex ControllerRegex = new(@"(controller|\bP)\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Memory channel from Win32_PhysicalMemory slot names:
    /// "ChannelA-DIMM0" -> "A", "P0 CHANNEL A" -> "A", "DIMM_A1" -> "A", "DDR4_B2" -> "B". Null if unknown.
    /// A memory controller / socket number other than 0 is part of the channel: Intel 12th gen and newer report
    /// dual channel as "Controller0-ChannelA-DIMM1" + "Controller1-ChannelA-DIMM1" -> "A" and "A (контроллер 1)",
    /// i.e. two different channels, not one.
    /// </summary>
    public static string? ParseChannel(string? deviceLocator, string? bankLabel)
    {
        foreach (var text in new[] { deviceLocator, bankLabel })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var m = ChannelRegex.Match(text);
            if (!m.Success)
                continue;
            var letter = m.Groups[1].Value.ToUpperInvariant();
            var owner = ControllerRegex.Match(text[..m.Index]);
            if (!owner.Success || !int.TryParse(owner.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n == 0)
                return letter;
            return owner.Groups[1].Value.Equals("P", StringComparison.OrdinalIgnoreCase)
                ? $"{letter} (процессор {n + 1})"
                : $"{letter} (контроллер {n})";
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

    /// <summary>LPDDR is soldered and multi-channel by design: one reported module does not mean single channel.</summary>
    public static bool IsSolderedLowPowerMemory(int smbiosType) => smbiosType is 27 or 28 or 29 or 30 or 35;

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

    // ===================== Memory part numbers =====================

    private const RegexOptions PartOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Vendor part-number patterns that encode the rated speed. Group "s2" = two-digit speed code (32 = 3200, 26 = 2666),
    /// "s3" = three-digit code (320 = 3200, 266 = 2666), "s4" = full MT/s. Group "g" = DDR generation (3/4/5) when encoded.
    /// All patterns are anchored to a vendor prefix so an unrelated number is never mistaken for a speed.
    /// </summary>
    private static readonly Regex[] PartPatterns =
    {
        // Kingston HyperX / FURY / ValueRAM / server: HX432C16FB3/8, KF560C40BBK2-32, KCP426NS8/8, KVR26N19S8/8, KSM32ES8/8ME
        new(@"^(?:HX|KF|KCP)(?<g>[345])(?<s2>\d{2})[A-Z]", PartOptions),
        new(@"^(?:KVR|KSM)(?<s2>\d{2})[A-Z]", PartOptions),
        // Kingston HyperX / FURY (older naming): KHX3200C16D4/16GX, KHX1600C9D3/8G, KF3600C17D4/8GX
        new(@"^K(?:HX|F)(?<s4>\d{4})C\d+(?:D(?<g>[345]))?", PartOptions),
        // Corsair: CMK16GX4M2B3200C16, CMK32GX5M2B5600C36, CMSX16GX4M2A2666C18
        new(@"^CM[A-Z0-9]*?X(?<g>[345])M\d+[A-Z](?<s4>\d{4})C\d", PartOptions),
        // G.Skill: F4-3600C16D-16GTZNC, F5-6000J3038F16GX2-TZ5RK
        new(@"^F(?<g>[345])-(?<s4>\d{4})[A-Z]", PartOptions),
        // Crucial DDR4: CT8G4DFS832A, CT16G4DFRA266, CP16G4DFRA32A, CT16G4SFRA32A, CT2K8G4DFS832A (kit of two)
        new(@"^C[TP](?:\d+K)?\d+G(?<g>4)[A-Z]{3}[A-Z0-9](?<s2>\d{2})(?:A|\d)", PartOptions),
        // Crucial DDR5: CT16G48C40U5, CP16G60C36U5B, CT8G48C40S5
        new(@"^C[TP](?:\d+K)?\d+G(?<s2>\d{2})C\d{2}[US](?<g>5)", PartOptions),
        // Crucial DDR3: CT102464BA160B
        new(@"^CT\d{5,6}B[A-Z](?<s3>\d{3})", PartOptions),
        // Crucial Ballistix: BL8G32C16U4B, BL2K8G36C16U4B, BLM2K8G44C19U4B, BLS8G4D240FSB, BLE8G4D30AEEA
        new(@"^BL[A-Z]?\d*K?\d+G(?<s2>\d{2})C\d{2}[US](?<g>4)", PartOptions),
        new(@"^BL[A-Z]?\d*K?\d+G(?<g>4)D(?<s2>\d{2})", PartOptions),
        // Samsung OEM DDR4/DDR5: M378A1K43CB2-CTD, M471A1K43DB1-CWE, M323R2GA3BB0-CQKOD
        new(@"^M\d{3}[AR][A-Z0-9]+-C(?<sam>PB|RC|TD|VF|WE|QK|WB)", PartOptions),
        // SK Hynix OEM DDR4: HMA81GU6CJR8N-VK, HMA851U6DJR6N-XN
        new(@"^HMA[A-Z0-9]+-(?<hyn>TF|UH|VK|WM|XN)", PartOptions),
        // Micron OEM: MTA8ATF1G64AZ-3G2E1, MTA16ATF2G64HZ-2G6E1
        new(@"^MTA[A-Z0-9]+-(?<mic>\dG\d)", PartOptions),
        // Team Group: TF3D416G3200HC16FDC01, FF3D532G6000HC38ADC01
        new(@"^(?:T|FF)[A-Z0-9]*?D(?<g>[345])\d+G(?<s4>\d{4})HC\d", PartOptions),
        // ADATA / XPG: AX4U320038G16A-ST10, AD4U320038G22-SGN, AD4S320038G22 (SO-DIMM), AX5U6000C3016G
        new(@"^A[DX](?<g>[45])[US](?<s4>\d{4})", PartOptions),
        // Patriot: PVS416G320C6K, PVE2432G360C0K, PSD48G266681
        new(@"^PV[A-Z0-9]*?(?<g>[345])\d{1,2}G(?<s3>\d{3})C\d", PartOptions),
        new(@"^PSD(?<g>[345])\d+G(?<s4>\d{4})", PartOptions),
    };

    private static readonly Dictionary<string, int> SamsungSpeeds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PB"] = 2133, ["RC"] = 2400, ["TD"] = 2666, ["VF"] = 2933, ["WE"] = 3200, ["QK"] = 4800, ["WB"] = 5600,
    };

    private static readonly Dictionary<string, int> HynixSpeeds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TF"] = 2133, ["UH"] = 2400, ["VK"] = 2666, ["WM"] = 2933, ["XN"] = 3200,
    };

    private static readonly Dictionary<string, int> MicronSpeeds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["2G1"] = 2133, ["2G3"] = 2400, ["2G4"] = 2400, ["2G6"] = 2666, ["2G9"] = 2933, ["3G2"] = 3200, ["4G8"] = 4800, ["5G6"] = 5600,
    };

    /// <summary>
    /// Rated speed (MT/s) and DDR generation encoded in a memory module part number, e.g. Kingston "HX432C16FB3/8" -> 3200 DDR4,
    /// Corsair "CMK16GX4M2B3200C16" -> 3200, G.Skill "F5-6000J3038F16GX2" -> 6000 DDR5, Crucial "CT8G4DFS832A" -> 3200.
    /// Null when the vendor scheme is unknown. Windows often reports only the slow JEDEC speed in Win32_PhysicalMemory.Speed,
    /// so the part number is the reliable way to see that a kit is rated for XMP/EXPO speeds.
    /// </summary>
    public static (int Speed, string? Type)? DecodeMemoryPart(string? partNumber)
    {
        if (string.IsNullOrWhiteSpace(partNumber))
            return null;
        var part = partNumber.Trim().Replace(" ", "", StringComparison.Ordinal);
        foreach (var pattern in PartPatterns)
        {
            var m = pattern.Match(part);
            if (!m.Success)
                continue;
            int? speed = null;
            if (m.Groups["s4"].Success)
                speed = int.Parse(m.Groups["s4"].Value, CultureInfo.InvariantCulture);
            else if (m.Groups["s3"].Success)
                speed = FromThreeDigits(int.Parse(m.Groups["s3"].Value, CultureInfo.InvariantCulture));
            else if (m.Groups["s2"].Success)
                speed = FromTwoDigits(int.Parse(m.Groups["s2"].Value, CultureInfo.InvariantCulture));
            else if (m.Groups["sam"].Success && SamsungSpeeds.TryGetValue(m.Groups["sam"].Value, out var sam))
                speed = sam;
            else if (m.Groups["hyn"].Success && HynixSpeeds.TryGetValue(m.Groups["hyn"].Value, out var hyn))
                speed = hyn;
            else if (m.Groups["mic"].Success && MicronSpeeds.TryGetValue(m.Groups["mic"].Value, out var mic))
                speed = mic;
            if (speed is not (>= 800 and <= 12000))
                continue;
            var type = m.Groups["g"].Success ? "DDR" + m.Groups["g"].Value : GuessDdrType(speed.Value);
            return (speed.Value, type);
        }
        return null;
    }

    /// <summary>Rated speed from a part number, or null (see <see cref="DecodeMemoryPart"/>).</summary>
    public static int? RatedSpeedFromPartNumber(string? partNumber) => DecodeMemoryPart(partNumber)?.Speed;

    /// <summary>Two-digit speed codes: 32 -> 3200, 26 -> 2666, 21 -> 2133, 48 -> 4800.</summary>
    private static int FromTwoDigits(int code) => code switch
    {
        10 => 1066,
        13 => 1333,
        18 => 1866,
        21 => 2133,
        26 => 2666,
        29 => 2933,
        34 => 3466,
        37 => 3733,
        42 => 4266,
        53 => 5333,
        _ => code * 100,
    };

    /// <summary>Three-digit speed codes: 320 -> 3200, 266 -> 2666, 160 -> 1600.</summary>
    private static int FromThreeDigits(int code) => code switch
    {
        106 => 1066,
        133 => 1333,
        186 => 1866,
        213 => 2133,
        266 => 2666,
        293 => 2933,
        346 => 3466,
        373 => 3733,
        426 => 4266,
        _ => code * 10,
    };

    /// <summary>Only used when the part number does not name the generation; speeds of DDR3/4/5 barely overlap.</summary>
    private static string? GuessDdrType(int speed) => speed switch
    {
        <= 2133 => null, // DDR3-2133 and DDR4-2133 both exist
        < 4400 => "DDR4",
        _ => "DDR5",
    };

    // ===================== Firmware / sensors =====================

    /// <summary>GetFirmwareType / PEFirmwareType value: 1 = legacy BIOS, 2 = UEFI, anything else = unknown.</summary>
    public static BootMode BootModeFrom(int? firmwareType) => firmwareType switch
    {
        1 => BootMode.LegacyBios,
        2 => BootMode.Uefi,
        _ => BootMode.Unknown,
    };

    /// <summary>
    /// MSAcpi_ThermalZoneTemperature.CurrentTemperature (tenths of a kelvin) -> °C. Null for values no real sensor
    /// reports (many boards return 0 or a placeholder): only 5..125 °C is accepted.
    /// </summary>
    public static double? KelvinTenthsToCelsius(long? tenthsOfKelvin)
    {
        if (tenthsOfKelvin is not > 0)
            return null;
        var celsius = Math.Round(tenthsOfKelvin.Value / 10.0 - 273.15, 1);
        return celsius is >= 5 and <= 125 ? celsius : null;
    }

    /// <summary>"52 °C".</summary>
    public static string Celsius(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + " °C";

    /// <summary>
    /// CPU load from GetSystemTimes deltas (100 ns units; kernel time includes idle), excluding <paramref name="ownBusy"/> —
    /// the CPU time this app used meanwhile, so our own scanning never looks like a busy PC. 0..100, null if no time passed.
    /// </summary>
    public static double? CpuLoadPercent(ulong idleDelta, ulong kernelDelta, ulong userDelta, long ownBusy = 0)
    {
        var total = (double)kernelDelta + userDelta;
        if (total <= 0)
            return null;
        var busy = total - idleDelta - Math.Max(0, ownBusy);
        return Math.Clamp(100.0 * busy / total, 0, 100);
    }

    /// <summary>"45,2 Вт·ч" from milliwatt-hours.</summary>
    public static string WattHours(long mwh) => (mwh / 1000.0).ToString(mwh >= 100_000 ? "0" : "0.#", Format.Ru) + " Вт·ч";
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
