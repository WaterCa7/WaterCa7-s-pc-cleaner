using PcCleaner.Modules.Disk;
using Xunit;

namespace PcCleaner.Tests.Disk;

public class ToolOutputParsersTests
{
    [Fact]
    public void Fsutil_english_modern_output_trim_enabled()
    {
        const string output =
            "NTFS DisableDeleteNotify = 0  (Allows TRIM operations to be sent to the storage device)\r\n" +
            "ReFS DisableDeleteNotify = 1  (Disables TRIM operations to be sent to the storage device)\r\n";
        Assert.Equal(TrimState.Enabled, ToolOutputParsers.ParseTrimState(output));
    }

    [Fact]
    public void Fsutil_russian_output_trim_disabled()
    {
        const string output =
            "NTFS DisableDeleteNotify = 1  (Отключено: операции TRIM не отправляются на устройство хранения)\r\n" +
            "ReFS DisableDeleteNotify в данный момент не задан\r\n";
        Assert.Equal(TrimState.Disabled, ToolOutputParsers.ParseTrimState(output));
    }

    [Fact]
    public void Fsutil_legacy_output_without_file_system_prefix()
    {
        Assert.Equal(TrimState.Enabled, ToolOutputParsers.ParseTrimState("DisableDeleteNotify = 0\r\n"));
        Assert.Equal(TrimState.Disabled, ToolOutputParsers.ParseTrimState("DisableDeleteNotify = 1\r\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Ошибка: запрошенная операция требует повышения прав.")]
    public void Fsutil_unknown_output(string? output)
        => Assert.Equal(TrimState.Unknown, ToolOutputParsers.ParseTrimState(output));

    private const string VssEnglish = """
        vssadmin 1.1 - Volume Shadow Copy Service administrative command-line tool
        (C) Copyright 2001-2013 Microsoft Corp.

        Shadow Copy Storage association
           For volume: (C:)\\?\Volume{1b2c3d4e-0000-0000-0000-100000000000}\
           Shadow Copy Storage volume: (C:)\\?\Volume{1b2c3d4e-0000-0000-0000-100000000000}\
           Used Shadow Copy Storage space: 3.52 GB (0%)
           Allocated Shadow Copy Storage space: 4.11 GB (0%)
           Maximum Shadow Copy Storage space: 46.5 GB (5%)

        Shadow Copy Storage association
           For volume: (D:)\\?\Volume{2b2c3d4e-0000-0000-0000-100000000000}\
           Shadow Copy Storage volume: (D:)\\?\Volume{2b2c3d4e-0000-0000-0000-100000000000}\
           Used Shadow Copy Storage space: 0 B (0%)
           Allocated Shadow Copy Storage space: 0 B (0%)
           Maximum Shadow Copy Storage space: UNBOUNDED (100%)
        """;

    private const string VssRussian = """
        vssadmin 1.1 - Средство командной строки администрирования службы теневого копирования тома
        (C) Корпорация Майкрософт (Microsoft Corp.), 2001-2013.

        Сопоставление хранилища теневой копии
           Для тома: (C:)\\?\Volume{1b2c3d4e-0000-0000-0000-100000000000}\
           Том хранилища теневой копии: (C:)\\?\Volume{1b2c3d4e-0000-0000-0000-100000000000}\
           Используемое пространство хранилища теневой копии: 1,25 ГБ (0%)
           Выделенное пространство хранилища теневой копии: 2 ГБ (0%)
           Максимальное пространство хранилища теневой копии: 9,31 ГБ (1%)
        """;

    [Fact]
    public void Vssadmin_english_two_volumes()
    {
        var items = ToolOutputParsers.ParseShadowStorage(VssEnglish);

        Assert.Equal(2, items.Count);
        var c = items[0];
        Assert.Equal("C:", c.Volume);
        Assert.Equal((long)Math.Round(3.52 * (1L << 30)), c.UsedBytes);
        Assert.Equal((long)Math.Round(4.11 * (1L << 30)), c.AllocatedBytes);
        Assert.Equal((long)Math.Round(46.5 * (1L << 30)), c.MaxBytes);
        Assert.Equal(5, c.MaxPercent);

        var d = items[1];
        Assert.Equal("D:", d.Volume);
        Assert.Equal(0, d.UsedBytes);
        Assert.Null(d.MaxBytes);
        Assert.Equal(100, d.MaxPercent);
    }

    [Fact]
    public void Vssadmin_russian_with_comma_decimals()
    {
        var item = Assert.Single(ToolOutputParsers.ParseShadowStorage(VssRussian));

        Assert.Equal("C:", item.Volume);
        Assert.Equal((long)Math.Round(1.25 * (1L << 30)), item.UsedBytes);
        Assert.Equal(2L << 30, item.AllocatedBytes);
        Assert.Equal((long)Math.Round(9.31 * (1L << 30)), item.MaxBytes);
    }

    [Theory]
    [InlineData("No items found that satisfy the query.")]
    [InlineData("Не найдены элементы, удовлетворяющие запросу.")]
    [InlineData("Error: You don't have the correct permissions to run this command.")]
    [InlineData("")]
    public void Vssadmin_without_associations_returns_empty(string output)
        => Assert.Empty(ToolOutputParsers.ParseShadowStorage(output));

    [Theory]
    [InlineData("1.5 GB", 1610612736L)]
    [InlineData("1,5 ГБ", 1610612736L)]
    [InlineData("512 MB (3%)", 536870912L)]
    [InlineData("0 B (0%)", 0L)]
    [InlineData("100 bytes", 100L)]
    [InlineData("2 TB", 2199023255552L)]
    public void Parses_sizes_with_units(string text, long expected)
        => Assert.Equal(expected, ToolOutputParsers.ParseSize(text));

    [Theory]
    [InlineData("UNBOUNDED (100%)")]
    [InlineData("нет данных")]
    public void Size_without_unit_is_null(string text)
        => Assert.Null(ToolOutputParsers.ParseSize(text));

    [Fact]
    public void Reliability_json_array_and_single_object()
    {
        const string array = """
            [{"DeviceId":"0","Temperature":34,"TemperatureMax":70,"Wear":3,"PowerOnHours":12345,"ReadErrorsUncorrected":0,"WriteErrorsUncorrected":null},
             {"DeviceId":"1","Temperature":0,"TemperatureMax":null,"Wear":null,"PowerOnHours":null,"ReadErrorsUncorrected":null,"WriteErrorsUncorrected":null}]
            """;
        var map = ToolOutputParsers.ParseReliabilityJson(array);

        Assert.Equal(34, map["0"].TemperatureC);
        Assert.Equal(3, map["0"].WearPercent);
        Assert.Equal(12345, map["0"].PowerOnHours);
        Assert.Equal(0, map["0"].ReadErrorsUncorrected);
        Assert.True(map["0"].HasAnyValue);
        Assert.Null(map["1"].TemperatureC); // 0 °C means "not reported"
        Assert.False(map["1"].HasAnyValue);

        var single = ToolOutputParsers.ParseReliabilityJson("""{"DeviceId":"2","Temperature":41}""");
        Assert.Equal(41, single["2"].TemperatureC);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ broken")]
    public void Reliability_bad_json_is_empty(string json)
        => Assert.Empty(ToolOutputParsers.ParseReliabilityJson(json));

    [Theory]
    [InlineData("Retrim:  42% complete...", 42)]
    [InlineData("Оптимизация: выполнено 7%", 7)]
    [InlineData("Pass 1: 100.0% defragmented", 100)]
    [InlineData("Nothing here", null)]
    public void Defrag_progress_percent(string line, int? expected)
        => Assert.Equal(expected, (int?)ToolOutputParsers.ParseProgressPercent(line));

    [Fact]
    public void Defrag_summary_keeps_only_last_progress_line_and_drops_banner()
    {
        const string output = "Microsoft Drive Optimizer\nCopyright (c) Microsoft Corp.\n\nInvoking retrim on (C:)...\n" +
                              "Retrim:  10% complete...\nRetrim:  55% complete...\nRetrim: 100% complete.\n\n" +
                              "The operation completed successfully.\n";

        var summary = DiskService.SummarizeDefragOutput(output);

        Assert.DoesNotContain("Copyright", summary);
        Assert.DoesNotContain("10%", summary);
        Assert.Contains("100% complete", summary);
        Assert.Contains("The operation completed successfully.", summary);
    }

    [Fact]
    public void Defrag_summary_drops_the_russian_banner()
    {
        const string output = "Оптимизация дисков (Майкрософт)\r\n(c) Корпорация Майкрософт (Microsoft Corporation).\r\n\r\n" +
                              "Вызов повторной оптимизации на (C:)...\r\nПовторная оптимизация: выполнено 100%.\r\n\r\n" +
                              "Операция успешно завершена.\r\n";

        var summary = DiskService.SummarizeDefragOutput(output);

        Assert.DoesNotContain("Майкрософт", summary);
        Assert.StartsWith("Вызов повторной оптимизации", summary);
        Assert.Contains("Операция успешно завершена.", summary);
    }
}
