using PcCleaner.Modules.Optimizer;
using Xunit;

namespace PcCleaner.Tests.Optimizer;

public class PowerCfgParsingTests
{
    [Fact]
    public void ParsesRussianActiveScheme()
    {
        var plan = PowerPlans.ParseActive("GUID схемы питания: 381b4222-f694-41f0-9685-ff5bb260df2e  (Сбалансированная)\r\n");
        Assert.NotNull(plan);
        Assert.Equal(PowerPlans.Balanced, plan!.Guid);
        Assert.Equal("Сбалансированная", plan.Name);
        Assert.True(plan.IsActive);
        Assert.Equal(PowerPlanKind.Balanced, plan.Kind);
    }

    [Fact]
    public void ParsesEnglishActiveScheme()
    {
        var plan = PowerPlans.ParseActive("Power Scheme GUID: 8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C  (High performance)");
        Assert.NotNull(plan);
        Assert.Equal(PowerPlans.HighPerformance, plan!.Guid); // normalised to lower case
        Assert.Equal("High performance", plan.Name);
        Assert.Equal(PowerPlanKind.HighPerformance, plan.Kind);
    }

    [Fact]
    public void ParsesRussianList_WithActiveMarkAndDuplicatedUltimate()
    {
        const string output =
            "\r\nСуществующие схемы управления питанием (* - активные)\r\n" +
            "-----------------------------------\r\n" +
            "GUID схемы питания: 38156909-5918-4777-864e-fbf99c75df8b  (Максимальная производительность)\r\n" +
            "GUID схемы питания: 381b4222-f694-41f0-9685-ff5bb260df2e  (Сбалансированная)\r\n" +
            "GUID схемы питания: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Высокая производительность) *\r\n" +
            "GUID схемы питания: a1841308-3541-4fab-bc81-f71556f20b4a  (Экономия энергии)\r\n";
        var plans = PowerPlans.ParseList(output);
        Assert.Equal(4, plans.Count);
        Assert.Equal(PowerPlanKind.UltimatePerformance, plans[0].Kind); // recognised by name, GUID is a copy
        Assert.Single(plans, p => p.IsActive);
        Assert.Equal(PowerPlans.HighPerformance, plans.Single(p => p.IsActive).Guid);
        Assert.Equal(PowerPlanKind.PowerSaver, plans[3].Kind);
    }

    [Fact]
    public void ParsesEnglishList_WithParenthesesInName()
    {
        const string output =
            "Existing Power Schemes (* Active)\n-----------------------------------\n" +
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *\n" +
            "Power Scheme GUID: 9897998c-92de-4669-853f-b7cd3ecb2790  (AMD Ryzen (TM) Balanced)\n" +
            "Power Scheme GUID: e9a42b02-d5df-448d-aa00-03f14749eb61  (Ultimate Performance)\n";
        var plans = PowerPlans.ParseList(output);
        Assert.Equal(3, plans.Count);
        Assert.True(plans[0].IsActive);
        Assert.Equal("AMD Ryzen (TM) Balanced", plans[1].Name);
        Assert.Equal(PowerPlanKind.Custom, plans[1].Kind);
        Assert.Equal(PowerPlanKind.UltimatePerformance, plans[2].Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Не удается выполнить операцию.")]
    public void GarbageOutput_GivesNothing(string? output)
    {
        Assert.Empty(PowerPlans.ParseList(output));
        Assert.Null(PowerPlans.ParseActive(output));
    }

    [Theory]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", true)]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e & del", false)]
    [InlineData("scheme_balanced", false)]
    [InlineData("", false)]
    public void IsGuid_RejectsAnythingButAGuid(string text, bool expected) => Assert.Equal(expected, PowerPlans.IsGuid(text));
}

public class HardwareTextTests
{
    [Theory]
    [InlineData("ChannelA-DIMM0", "BANK 0", "A")]
    [InlineData("Controller1-ChannelB-DIMM1", "BANK 3", "B")]
    [InlineData("DIMM 1", "P0 CHANNEL B", "B")]
    [InlineData("DIMM_A2", "BANK 1", "A")]
    [InlineData("DDR4_B1", null, "B")]
    [InlineData("DIMM 0", "BANK 0", null)]
    [InlineData(null, null, null)]
    public void ParsesMemoryChannel(string? locator, string? bank, string? expected)
        => Assert.Equal(expected, HardwareText.ParseChannel(locator, bank));

    [Theory]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("32.0.16.1714", "617.14")]
    [InlineData("31.0.15.3623", "536.23")]
    [InlineData("27.21.14.5671", "456.71")]
    [InlineData("garbage", null)]
    public void ConvertsNvidiaDriverVersion(string version, string? expected)
        => Assert.Equal(expected, HardwareText.NvidiaDriverVersion(version));

    [Theory]
    [InlineData("32.0.16.1714", 32)]
    [InlineData("26.20.100.7262", 26)]
    [InlineData(null, 0)]
    public void ReadsWddmFromDriverVersion(string? version, int expected)
        => Assert.Equal(expected, HardwareText.DriverWddmMajor(version));

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-10700K CPU @ 3.80GHz", "Intel Core i7-10700K")]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor", "AMD Ryzen 7 5800X")]
    [InlineData("13th Gen Intel(R) Core(TM) i5-13400F", "13th Gen Intel Core i5-13400F")]
    public void ShortensCpuName(string name, string expected) => Assert.Equal(expected, HardwareText.ShortCpuName(name));

    [Fact]
    public void AgeText_IsRussian()
    {
        var now = new DateTime(2026, 10, 16);
        Assert.Equal("7 месяцев", HardwareText.AgeText(new DateTime(2026, 3, 1), now));
        Assert.Equal("1 год", HardwareText.AgeText(new DateTime(2025, 10, 1), now));
        Assert.Equal("2 года и 3 месяца", HardwareText.AgeText(new DateTime(2024, 7, 10), now));
        Assert.Equal("5 дней", HardwareText.AgeText(new DateTime(2026, 10, 11), now));
    }

    [Theory]
    [InlineData("Intel(R) UHD Graphics 630", true)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", true)]
    [InlineData("Intel(R) Arc(TM) Graphics", true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("AMD Radeon 780M Graphics", true)]
    [InlineData("AMD Radeon RX Vega 10 Graphics", true)]
    [InlineData("AMD Radeon RX 6700 XT", false)]
    [InlineData("NVIDIA GeForce RTX 4070", false)]
    public void DetectsIntegratedGraphics(string name, bool expected) => Assert.Equal(expected, GpuClassifier.IsIntegrated(name));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 2070 SUPER", @"PCI\VEN_10DE&DEV_1E84", GpuVendor.Nvidia)]
    [InlineData("Microsoft Basic Display Adapter", @"PCI\VEN_1002&DEV_73DF", GpuVendor.Amd)]
    [InlineData("Intel(R) UHD Graphics", null, GpuVendor.Intel)]
    public void DetectsGpuVendor(string name, string? pnp, GpuVendor expected) => Assert.Equal(expected, GpuClassifier.Vendor(name, pnp));

    [Theory]
    [InlineData("Microsoft Remote Display Adapter", @"SWD\RemoteDisplayEnum\1", true)]
    [InlineData("Parsec Virtual Display Adapter", @"ROOT\DISPLAY\0000", true)]
    [InlineData("NVIDIA GeForce RTX 3060", @"PCI\VEN_10DE&DEV_2504", false)]
    public void DetectsVirtualAdapters(string name, string pnp, bool expected) => Assert.Equal(expected, GpuClassifier.IsVirtual(name, pnp));

    [Fact]
    public void MatchesWmiMonitorToDisplayDevice()
    {
        Assert.True(HardwareProbe.MonitorIdsMatch(@"DISPLAY\TRG2711\5&108ccec1&0&UID4354_0",
            @"\\?\DISPLAY#TRG2711#5&108ccec1&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"));
        Assert.False(HardwareProbe.MonitorIdsMatch(@"DISPLAY\TRG2711\5&108ccec1&0&UID4354_0",
            @"\\?\DISPLAY#XXX0000#5&108ccec1&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"));
    }

    [Fact]
    public void DecodesWmiUInt16String()
    {
        var raw = new ushort[] { 'D', 'F', '2', '7', 0, 0 };
        Assert.Equal("DF27", HardwareProbe.DecodeWmiString(raw));
    }

    [Theory]
    [InlineData(3, 11, null, DiskMediaKind.Hdd)]
    [InlineData(4, 11, null, DiskMediaKind.Ssd)]
    [InlineData(0, 17, null, DiskMediaKind.Ssd)]
    [InlineData(0, 11, 0L, DiskMediaKind.Ssd)]
    [InlineData(0, 11, 7200L, DiskMediaKind.Hdd)]
    [InlineData(0, 11, 4294967295L, DiskMediaKind.Unknown)]
    public void ClassifiesDiskMedia(int mediaType, int busType, long? spindle, DiskMediaKind expected)
        => Assert.Equal(expected, HardwareProbe.MediaKindOf(mediaType, busType, spindle));

    [Theory]
    [InlineData(0, 0, 0, "Всё настроено отлично — рекомендаций нет")]
    [InlineData(1, 1, 0, "Найдена 1 рекомендация, из них 1 можно применить автоматически")]
    [InlineData(3, 2, 0, "Найдено 3 рекомендации, из них 2 можно применить автоматически")]
    [InlineData(5, 0, 1, "Найдено 5 рекомендаций — подскажем, что сделать вручную")]
    [InlineData(2, 0, 2, "Серьёзных проблем нет — 2 совета для сведения")]
    [InlineData(21, 3, 0, "Найдена 21 рекомендация, из них 3 можно применить автоматически")]
    public void SummaryTitle_UsesRussianPlurals(int total, int auto, int info, string expected)
        => Assert.Equal(expected, OptimizerViewModel.BuildSummaryTitle(total, auto, info));
}
