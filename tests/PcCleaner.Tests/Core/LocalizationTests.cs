using System.Globalization;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Markup;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Settings;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// Tests that switch the process-wide UI language (<see cref="T.Use"/>) run alone, after the parallel collections, so the
/// ~1 600 Russian assertions of the other tests never see English. They always switch back to Russian (<see cref="TestLanguage"/>).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageSequentialCollection
{
    public const string Name = "Language (sequential)";
}

/// <summary>
/// Switches <see cref="T"/> to English (optionally with its own catalog instead of en.json) until disposed; then Russian and
/// the real catalog again. Use only in <see cref="LanguageSequentialCollection"/>.
/// </summary>
internal sealed class TestLanguage : IDisposable
{
    private readonly IReadOnlyDictionary<string, string> _savedTexts = T.EnglishTexts;

    private TestLanguage(IReadOnlyDictionary<string, string>? englishTexts)
    {
        if (englishTexts is not null)
            T.EnglishTexts = englishTexts;
        T.Use(T.English);
    }

    public static TestLanguage English(IReadOnlyDictionary<string, string>? englishTexts = null) => new(englishTexts);

    public void Dispose()
    {
        T.Use(T.Russian);
        T.EnglishTexts = _savedTexts;
    }
}

/// <summary>What does not depend on the current language: these may run in parallel with everything else.</summary>
public class LocalizationRulesTests
{
    [Fact]
    public void TheDefaultLanguage_IsRussian()
    {
        Assert.Equal(T.Russian, T.Language);
        Assert.Equal("ru-RU", T.Culture.Name);
    }

    [Theory]
    [InlineData("ru", "en-US", "ru")]
    [InlineData("en", "ru-RU", "en")]
    [InlineData(null, "ru-RU", "ru")]
    [InlineData(null, "ru-UA", "ru")]
    [InlineData(null, "ru", "ru")]
    [InlineData(null, "en-US", "en")]
    [InlineData(null, "de-DE", "en")]
    [InlineData(null, "uk-UA", "en")]
    [InlineData(null, "", "en")] // invariant culture
    [InlineData("de", "ru-RU", "ru")]
    [InlineData("", "en-GB", "en")]
    [InlineData("EN", "ru-RU", "ru")] // only the exact codes that Sanitize keeps
    public void ResolveLanguage_TheSettingFirst_ThenTheSystemUiLanguage(string? setting, string system, string expected)
        => Assert.Equal(expected, T.ResolveLanguage(setting, CultureInfo.GetCultureInfo(system)));

    [Theory]
    [InlineData("en", "ru", "ru-RU", "en")]   // --lang beats Настройки
    [InlineData(null, "en", "ru-RU", "en")]   // Настройки beat the system
    [InlineData(null, null, "ru-RU", "ru")]   // the system
    [InlineData(null, null, "fr-FR", "en")]
    [InlineData("de", null, "ru-RU", "ru")]   // an unknown --lang is ignored
    public void StartupLanguage_IsTheArgument_ThenTheSetting_ThenTheSystem(string? argument, string? setting, string system, string expected)
        => Assert.Equal(expected, App.StartupLanguage(argument, setting, CultureInfo.GetCultureInfo(system)));

    [Fact]
    public void Use_RejectsAnUnknownLanguage()
    {
        Assert.Throws<ArgumentException>(() => T.Use("de"));
        Assert.Equal(T.Russian, T.Language);
    }

    [Theory]
    [InlineData(1, "файл")]
    [InlineData(2, "файла")]
    [InlineData(5, "файлов")]
    [InlineData(11, "файлов")]
    [InlineData(21, "файл")]
    [InlineData(112, "файлов")]
    [InlineData(1004, "файла")]
    [InlineData(-3, "файла")]
    public void Plural_InRussian_UsesTheRussianRules(long n, string expected)
        => Assert.Equal(expected, T.Plural(n, "файл", "файла", "файлов"));

    [Fact]
    public void Russian_ReturnsTheKeyItself()
    {
        Assert.Equal("Проверить снова", T._("Проверить снова"));
        Assert.Equal("Найдено 3 из 1,5", T.F("Найдено {0} из {1}", 3, 1.5));
        Assert.Equal("Проверить снова", new LocExtension("Проверить снова").ProvideValue(null!));
    }

    [Fact]
    public void Settings_LanguageIsNullByDefault_AndIsSaved()
    {
        Assert.Null(new AppSettings().Language);

        var json = JsonSerializer.Serialize(new AppSettings { Language = "en" });
        Assert.Equal("en", JsonSerializer.Deserialize<AppSettings>(json)!.Language);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("EN")]
    [InlineData(" en")]
    [InlineData("")]
    [InlineData("ru-RU")]
    public void Sanitize_ResetsAnUnknownLanguageToTheSystemOne(string language)
    {
        var settings = new AppSettings { Language = language };

        Assert.Equal(1, SettingsLogic.Sanitize(settings));
        Assert.Null(settings.Language);
        Assert.Equal(0, SettingsLogic.Sanitize(settings));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    [InlineData(null)]
    public void Sanitize_KeepsAKnownLanguage(string? language)
    {
        var settings = new AppSettings { Language = language };

        Assert.Equal(0, SettingsLogic.Sanitize(settings));
        Assert.Equal(language, settings.Language);
    }
}

[Collection(LanguageSequentialCollection.Name)]
public class LocalizationSwitchTests
{
    private static readonly Dictionary<string, string> Texts = new()
    {
        ["Найдено {0} из {1}"] = "Found {0} of {1}",
        ["Проверить снова"] = "Check again",
        ["Пусто"] = "  ",
        ["файл"] = "file",
        ["файлов"] = "files",
    };

    [Fact]
    public void English_TakesTheTextFromTheCatalog()
    {
        using (TestLanguage.English(Texts))
        {
            Assert.Equal(T.English, T.Language);
            Assert.Equal("en-US", T.Culture.Name);
            Assert.Equal("Check again", T._("Проверить снова"));
        }
        Assert.Equal(T.Russian, T.Language);
        Assert.Equal("Проверить снова", T._("Проверить снова"));
    }

    [Fact]
    public void English_WithoutAnEntry_ShowsTheRussianText()
    {
        using (TestLanguage.English(Texts))
        {
            Assert.Equal("Нет такого текста", T._("Нет такого текста"));
            Assert.Equal("Пусто", T._("Пусто")); // a blank entry is no translation
        }
    }

    [Fact]
    public void F_FormatsTheTranslatedTextWithTheEnglishCulture()
    {
        using (TestLanguage.English(Texts))
        {
            Assert.Equal("Found 3 of 1.5", T.F("Найдено {0} из {1}", 3, 1.5));
            Assert.Equal("Осталось 2.5", T.F("Осталось {0}", 2.5)); // no entry: Russian, still formatted
        }
    }

    [Theory]
    [InlineData(1, "file")]
    [InlineData(-1, "file")]
    [InlineData(0, "files")]
    [InlineData(2, "files")]
    [InlineData(5, "files")]
    [InlineData(21, "files")]
    public void Plural_InEnglish_IsOneOrOther(long n, string expected)
    {
        using (TestLanguage.English(Texts))
            Assert.Equal(expected, T.Plural(n, "файл", "файла", "файлов"));
    }

    [Fact]
    public void Plural_InEnglish_WithoutEntries_ShowsTheRussianOneOrMany()
    {
        using (TestLanguage.English(new Dictionary<string, string>()))
        {
            Assert.Equal("файл", T.Plural(1, "файл", "файла", "файлов"));
            Assert.Equal("файлов", T.Plural(3, "файл", "файла", "файлов"));
        }
    }

    [Fact]
    public void LocExtension_ProvidesTheTextInTheUiLanguage()
    {
        using (TestLanguage.English(Texts))
        {
            Assert.Equal("Check again", new LocExtension("Проверить снова").ProvideValue(null!));
            Assert.Equal("Check again", new TExtension("Проверить снова").ProvideValue(null!));
        }
    }

    [Fact]
    public void Xaml_LocT_GivesTheTextInTheUiLanguage()
    {
        const string xaml = "<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                            "xmlns:loc='clr-namespace:PcCleaner.Core.Localization;assembly=WaterCa7sPcCleaner' " +
                            "Text=\"{loc:T 'Проверить снова'}\" />";
        string? russian = null, english = null;

        UiThread.Run(() =>
        {
            russian = ((TextBlock)XamlReader.Parse(xaml)).Text;
            using (TestLanguage.English(Texts))
                english = ((TextBlock)XamlReader.Parse(xaml)).Text;
            return Task.CompletedTask;
        });

        Assert.Equal("Проверить снова", russian);
        Assert.Equal("Check again", english);
    }

    [Theory]
    [InlineData(new[] { "--lang", "en" }, "en")]
    [InlineData(new[] { "--LANG", "EN" }, "en")]
    [InlineData(new[] { "--lang", "ru" }, "ru")]
    [InlineData(new[] { "--lang", "de" }, null)]
    public void ParseArgs_ReadsTheLanguage(string[] args, string? expected)
    {
        App.ParseArgs(args);

        Assert.Equal(expected, App.LanguageArg);
    }
}

/// <summary>Format follows the UI language; in Russian its output is exactly what it always was.</summary>
[Collection(LanguageSequentialCollection.Name)]
public class FormatLanguageTests
{
    private static readonly DateTime Moment = new(2026, 10, 18, 14, 5, 0);

    private static readonly Dictionary<string, string> Texts = new()
    {
        ["файл"] = "file",
        ["файлов"] = "files",
    };

    [Fact]
    public void Russian_StaysAsItWas()
    {
        Assert.Equal("0 Б", Format.Bytes(0));
        Assert.Equal("1,5 КБ", Format.Bytes(1536));
        Assert.Equal("-1,5 КБ", Format.Bytes(-1536));
        Assert.Equal("12,5%", Format.Percent(12.5));
        Assert.Equal("2 д 3 ч", Format.Duration(new TimeSpan(2, 3, 0, 0)));
        Assert.Equal("5 ч 12 мин", Format.Duration(new TimeSpan(5, 12, 0)));
        Assert.Equal("3 мин 5 с", Format.Duration(new TimeSpan(0, 3, 5)));
        Assert.Equal("42 с", Format.Duration(TimeSpan.FromSeconds(42)));
        Assert.Equal("18.10.2026", Format.Date(Moment));
        Assert.Equal("18.10.2026 14:05", Format.DateTime(Moment));
        Assert.Equal("—", Format.Date(null));
        Assert.Equal("21 файл", Format.Count(21, "файл", "файла", "файлов"));
        Assert.Equal("5 файлов", Format.Count(5, "файл", "файла", "файлов"));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(-1536, "-1.5 KB")]
    [InlineData(10L * 1024 * 1024, "10 MB")]
    [InlineData(3L * 1024 * 1024 * 1024 / 2, "1.5 GB")]
    [InlineData(5L * 1024 * 1024 * 1024 * 1024, "5 TB")]
    public void Bytes_InEnglish(long bytes, string expected)
    {
        using (TestLanguage.English(Texts))
            Assert.Equal(expected, Format.Bytes(bytes));
    }

    [Fact]
    public void NumbersDatesAndDurations_InEnglish()
    {
        using (TestLanguage.English(Texts))
        {
            Assert.Equal("12.5%", Format.Percent(12.5));
            Assert.Equal("1,234,567", Format.Number(1234567));
            Assert.Equal("2 d 3 h", Format.Duration(new TimeSpan(2, 3, 0, 0)));
            Assert.Equal("5 h 12 min", Format.Duration(new TimeSpan(5, 12, 0)));
            Assert.Equal("3 min 5 s", Format.Duration(new TimeSpan(0, 3, 5)));
            Assert.Equal("42 s", Format.Duration(TimeSpan.FromSeconds(42)));
            Assert.Equal("Oct 18, 2026", Format.Date(Moment));
            Assert.Equal("Oct 18, 2026 14:05", Format.DateTime(Moment));
            Assert.Equal("—", Format.DateTime(null));
        }
    }

    [Fact]
    public void PluralAndCount_InEnglish_UseTheCatalog()
    {
        using (TestLanguage.English(Texts))
        {
            Assert.Equal("file", Format.Plural(1, "файл", "файла", "файлов"));
            Assert.Equal("1 file", Format.Count(1, "файл", "файла", "файлов"));
            Assert.Equal("21 files", Format.Count(21, "файл", "файла", "файлов"));
            Assert.Equal("1,500 files", Format.Count(1500, "файл", "файла", "файлов"));
        }
    }
}
