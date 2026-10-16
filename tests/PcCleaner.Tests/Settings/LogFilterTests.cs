using PcCleaner.Core;
using PcCleaner.Modules.Settings;
using Xunit;

namespace PcCleaner.Tests.Settings;

public class LogFilterTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 0, 0);

    private static List<LogEntry> Sample() => new()
    {
        new LogEntry(T0, LogLevel.Info, "Запуск программы"),
        new LogEntry(T0.AddSeconds(1), LogLevel.Action, "Файл помещён в карантин: C:\\Temp\\bad.exe"),
        new LogEntry(T0.AddSeconds(2), LogLevel.Warning, "Нет доступа к HKLM\\SOFTWARE\\Test"),
        new LogEntry(T0.AddSeconds(3), LogLevel.Error, "Не удалось сохранить настройки"),
        new LogEntry(T0.AddSeconds(4), LogLevel.Action, "Удалена резервная копия реестра: x.reg"),
    };

    [Theory]
    [InlineData(LogLevelFilter.All, 5)]
    [InlineData(LogLevelFilter.Actions, 2)]
    [InlineData(LogLevelFilter.Warnings, 1)]
    [InlineData(LogLevelFilter.Errors, 1)]
    public void Apply_FiltersByLevel(LogLevelFilter filter, int expected)
        => Assert.Equal(expected, LogFilter.Apply(Sample(), filter, null).Count);

    [Fact]
    public void Apply_ReturnsNewestFirst()
    {
        var result = LogFilter.Apply(Sample(), LogLevelFilter.All, "");
        Assert.Equal(Sample().Select(e => e.Time).Reverse(), result.Select(e => e.Time));
    }

    [Fact]
    public void Search_IsCaseInsensitiveAndRequiresAllWords()
    {
        Assert.Single(LogFilter.Apply(Sample(), LogLevelFilter.All, "КАРАНТИН"));
        Assert.Single(LogFilter.Apply(Sample(), LogLevelFilter.All, "карантин bad.exe"));
        Assert.Empty(LogFilter.Apply(Sample(), LogLevelFilter.All, "карантин реестр"));
        Assert.Equal(2, LogFilter.Apply(Sample(), LogLevelFilter.All, "  удал  ").Count);
    }

    [Fact]
    public void Search_MatchesLevelTextAndTime()
    {
        Assert.Single(LogFilter.Apply(Sample(), LogLevelFilter.All, "ошибка"));
        Assert.Single(LogFilter.Apply(Sample(), LogLevelFilter.All, "12:00:03"));
    }

    [Fact]
    public void Search_CombinesWithLevelFilter()
    {
        Assert.Single(LogFilter.Apply(Sample(), LogLevelFilter.Actions, "реестр"));
        Assert.Empty(LogFilter.Apply(Sample(), LogLevelFilter.Errors, "карантин"));
    }

    [Fact]
    public void Options_HaveRussianLabels()
        => Assert.Equal(new[] { "Все", "Действия", "Предупреждения", "Ошибки" }, LogFilter.Options.Select(o => o.Label));

    [Fact]
    public void ClipboardText_IsChronologicalWithDateAndLevel()
    {
        var newestFirst = LogFilter.Apply(Sample(), LogLevelFilter.Actions, null);
        var lines = LogFilter.ToClipboardText(newestFirst).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("16.10.2026 12:00:01 [Действие] Файл помещён в карантин: C:\\Temp\\bad.exe", lines[0]);
        Assert.StartsWith("16.10.2026 12:00:04 [Действие]", lines[1]);
    }

    [Fact]
    public void Count_ReportsPerLevel()
    {
        var (total, actions, warnings, errors) = LogFilter.Count(Sample());
        Assert.Equal((5, 2, 1, 1), (total, actions, warnings, errors));
    }
}
