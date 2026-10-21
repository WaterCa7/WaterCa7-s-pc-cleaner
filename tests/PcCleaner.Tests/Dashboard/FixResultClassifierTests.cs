using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Tests.Core;
using Xunit;

namespace PcCleaner.Tests.Dashboard;

/// <summary>
/// A fix result in English is a failure / needs administrator rights when it contains the English text of a catalog entry whose
/// Russian text the Russian rules call a failure / admin-only (see <see cref="FixResultClassifier"/>). Language-sequential: the
/// wiring tests switch <see cref="T"/> to English (TestLanguage switches back to Russian in its Dispose, i.e. in a finally).
/// </summary>
[Collection(LanguageSequentialCollection.Name)]
public class FixResultClassifierTests
{
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly Regex Placeholder = new(@"\{(?<index>\d+)[^{}]*\}");

    /// <summary>
    /// Failure / admin entries of en.json whose English value is too short to be searched for in a text (see
    /// <see cref="FixResultClassifier"/>): a fix that returns one of them in English is NOT recognised as failed. When this test
    /// fails after a translation, give the failure text a whole English sentence ("Could not fix: {0}.", not "Error: {0}") — or,
    /// if the entry is never a fix result (a label, a status word), add its Russian key here.
    /// </summary>
    private static readonly string[] ExpectedSkipped =
    {
        "Ошибка", // "Error": a word of the log levels / status texts, not a fix result
        "Прервано", // "Aborted": a status label of a physical disk (Disks page), never a fix result
        "не удалось: {0}", // ", failed: 1" in the banner of a bulk action on the Startup page: a piece of a status line, never a fix result
        "отменено", // "canceled": the state of a module scan; as a word it also ends successful texts ("… updates canceled.")
    };

    /// <summary>The catalog the app ships (a new instance: its matchers are built for this test).</summary>
    private static IReadOnlyDictionary<string, string> RealCatalog() => T.ParseCatalog(T.ReadEmbeddedCatalog()!);

    private static object[] SampleArgs(string template)
    {
        var count = Placeholder.Matches(template).Select(m => int.Parse(m.Groups["index"].Value) + 1).DefaultIfEmpty(0).Max();
        return Enumerable.Repeat<object>(3, count).ToArray();
    }

    /// <summary>The text as T._/T.F would give it: placeholders filled with the number 3 in the culture of the language.</summary>
    private static string Sample(string template, string language)
    {
        var args = SampleArgs(template);
        if (args.Length == 0)
            return template;
        try
        {
            return string.Format(language == T.English ? EnglishCulture : RussianCulture, template, args);
        }
        catch (FormatException)
        {
            return template; // a text with braces that is only used with T._
        }
    }

    [Fact]
    public void EveryFailureAndAdminText_OfTheCatalog_IsRecognisedInEnglish_AsTheRussianOneInRussian()
    {
        var catalog = RealCatalog();
        var problems = new List<string>();
        var checkedFailures = 0;
        var checkedAdmin = 0;
        foreach (var (russianKey, englishValue) in catalog)
        {
            var russian = Sample(T.TextOf(russianKey), T.Russian); // an entry with a context is judged by its Russian text
            var failure = FixResultClassifier.IsFailure(russian, T.Russian, null);
            var admin = FixResultClassifier.NeedsAdmin(russian, T.Russian, null);
            if ((!failure && !admin) || ExpectedSkipped.Contains(russianKey) || FixResultClassifier.SideNoteKeys.Contains(T.TextOf(russianKey)))
                continue;
            var english = Sample(englishValue, T.English);
            var englishFailure = FixResultClassifier.IsFailure(english, T.English, catalog);
            var englishAdmin = FixResultClassifier.NeedsAdmin(english, T.English, catalog);
            if (englishFailure != failure || englishAdmin != admin)
                problems.Add($"«{russian}» (failure {failure}, admin {admin}) → «{english}» (failure {englishFailure}, admin {englishAdmin})");
            checkedFailures += failure ? 1 : 0;
            checkedAdmin += admin ? 1 : 0;
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(checkedFailures >= 50, $"only {checkedFailures} failure texts were checked");
        Assert.True(checkedAdmin >= 20, $"only {checkedAdmin} admin texts were checked");
    }

    /// <summary>
    /// Russian texts that begin with a failure phrase but are only appended to the end of a result that stays "fixed": the Russian
    /// rules read «Не удалось» at the start of a result only, so they give no English matcher (an English matcher fires anywhere).
    /// </summary>
    private static readonly string[] ExpectedSideNotes =
    {
        "Не удалось очистить: {0}.", // Cleaner: "Удалено … системного мусора. Не удалось очистить: …"
    };

    [Fact]
    public void TheSideNoteKeys_AreExactlyTheListedOnes_AreInTheCatalog_AndAreFailurePhrasesInRussian()
    {
        var catalog = RealCatalog();

        Assert.Equal(ExpectedSideNotes.OrderBy(k => k, StringComparer.Ordinal), FixResultClassifier.SideNoteKeys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var key in FixResultClassifier.SideNoteKeys)
        {
            Assert.True(catalog.ContainsKey(key), $"«{key}» is not in en.json (stale side note)");
            Assert.True(FixResultClassifier.IsFailure(Sample(key, T.Russian), T.Russian, null), $"«{key}» is not a failure phrase: it needs no entry here");
        }
    }

    [Fact]
    public void ASideNote_AfterASuccess_IsNotAFailure_InEnglish()
    {
        var catalog = RealCatalog();
        foreach (var key in FixResultClassifier.SideNoteKeys)
        {
            var english = "Deleted 3 MB of system junk. " + Sample(catalog[key], T.English);

            Assert.False(FixResultClassifier.IsFailure(english, T.English, catalog), english);
            Assert.False(FixResultClassifier.IsFailure("Удалено 3 МБ системного мусора. " + Sample(key, T.Russian), T.Russian, null));
        }
    }

    /// <summary>
    /// Entries the Russian rules do NOT call a failure whose English text the English matchers do (the English text contains a phrase
    /// that the matcher of a failure entry looks for; the comment of each key names the matcher that fires): log lines and the like,
    /// never a fix result. A new divergence shows up here, so it is looked at: a fix result among them would be judged differently
    /// in the two languages (give it its own wording, or add its key to
    /// <see cref="FixResultClassifier.SideNoteKeys"/> when it is a side note of a successful result).
    /// </summary>
    private static readonly string[] ExpectedEnglishOnlyFailures =
    {
        "Автозагрузка: не удалось запустить проверку Защитником: {0}", // log: matched by "Could not start …:"
        "Автозагрузка: не удалось прочитать ярлык {0}: {1}", // log: matched by "Could not read …:"
        "Автозагрузка: не удалось удалить {0}: {1}", // log: matched by "Could not delete …:"
        "Безопасность: проверка «{0}» не удалась: {1}: {2}", // log of the Security scan: matched by "check failed"
        "Главная: не удалось восстановить состояние после ошибки проверки: {0}", // log: matched by "Could not restore …:"
        "Главная: не удалось запустить удаление «{0}»: {1}", // log: matched by "Could not start …: / Could not start the uninstall of “…”"
        "Главная: не удалось исправить «{0}» ({1}): {2}", // log: matched by "Could not fix"
        "Главная: не удалось открыть {0}: {1}", // log: matched by "Could not open / Could not open …:"
        "Главная: не удалось прочитать результат прошлой проверки: {0}", // log: matched by "Could not read …:"
        "Главная: не удалось прочитать сведения о компьютере: {0}", // log: matched by "Could not read …:"
        "Главная: не удалось прочитать сведения о системе из реестра: {0}", // log: matched by "Could not read …:"
        "Главная: не удалось прочитать сведения о системном диске: {0}", // log: matched by "Could not read …:"
        "Главная: не удалось скопировать отчёт в буфер обмена: {0}", // log: matched by "Could not copy the report"
        "Главная: не удалось сохранить отчёт в «{0}»: {1}", // log: matched by "Could not save the report"
        "Диски: не удалось проверить {0}: {1}", // log: matched by "Could not check"
        "Карантин: не удалось восстановить {0}: {1}", // log of the quarantine tab: matched by "Could not restore …:"
        "Карантин: не удалось прочитать {0}: {1}", // log of the quarantine tab: matched by "Could not read …:"
        "Карантин: не удалось удалить {0}: {1}", // log of the quarantine tab: matched by "Could not delete …:"
        "Обновление: не удалось запустить {0}: {1}", // log: matched by "Could not start …:"
        "Обновление: не удалось удалить {0}: {1}", // log: matched by "Could not delete …:"
        "Остальные файлы не обработаны — операция была отменена.", // header of the quarantine tab: matched by "The operation was canceled / the operation was canceled"
        "Очистка: не удалось оценить «{0}»: {1}", // log: matched by "Could not check"
        "Очистка: не удалось проверить запущенные программы: {0}", // log: matched by "Could not check"
        "Очистка: не удалось узнать размер кэша оптимизации доставки: {0}", // log: matched by "Could not get the size"
        "Перезагрузка: не удалось запустить shutdown.exe: {0}", // log: matched by "Could not start …:"
        "Перезапуск: не удалось запустить {0}: {1}", // log of the restart: matched by "Could not start …:"
        "Перезапустить как администратор", // a button label: matched by "Restart as administrator"
        "Программы: не удалось запустить PowerShell: {0}", // log: matched by "Could not start PowerShell: / Could not start …:"
        "Программы: не удалось прочитать название приложения: {0}", // log: matched by "Could not read …:"
        "Процессы: не удалось проверить подпись {0}: {1}", // log: matched by "Could not check"
        "Сеть: не удалось запустить netsh: {0}", // log: matched by "Could not start netsh: / Could not start …:"
        "Сеть: не удалось очистить кэш DNS: {0}", // log: matched by "Could not clear the DNS cache:"
        "Сеть: не удалось прочитать адаптер {0}: {1}", // log: matched by "Could not read …:"
        "Сеть: не удалось прочитать политику брандмауэра: {0}", // log: matched by "Could not read …:"
        "Сеть: не удалось прочитать счётчики трафика: {0}", // log: matched by "Could not read …:"
        "Сеть: не удалось разобрать таблицу соединений: {0}", // log: matched by "Could not read …: / Could not read the connection table"
        "Сеть: проверка «{0}» не удалась: {1}", // log: matched by "check failed"
        "Ускорение: не удалось вернуть «{0}»: {1}", // log: matched by "Could not revert “…”:"
        "Ускорение: не удалось вернуть план «{0}»: {1}", // log: matched by "Could not restore …:"
        "Ускорение: не удалось запустить powercfg {0}: {1}", // log: matched by "Could not start …:"
        "Ускорение: не удалось прочитать {0}\\{1}: {2}", // log: matched by "Could not read …:"
        "Ускорение: не удалось прочитать {0}\\{1}\\{2}: {3}", // log: matched by "Could not read …:"
        "Ускорение: не удалось прочитать сведения о батарее: {0}", // log: matched by "Could not read …:"
    };

    [Fact]
    public void TheEntries_ThatOnlyEnglishCallsAFailure_AreExactlyTheListedOnes()
    {
        var catalog = RealCatalog();
        var found = new List<string>();
        foreach (var (russianKey, englishValue) in catalog)
        {
            var russian = Sample(T.TextOf(russianKey), T.Russian); // an entry with a context is judged by its Russian text
            if (FixResultClassifier.IsFailure(russian, T.Russian, null) || FixResultClassifier.NeedsAdmin(russian, T.Russian, null))
                continue;
            var english = Sample(englishValue, T.English);
            if (FixResultClassifier.IsFailure(english, T.English, catalog) || FixResultClassifier.NeedsAdmin(english, T.English, catalog))
                found.Add(russianKey);
        }

        var expected = ExpectedEnglishOnlyFailures.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var actual = found.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(expected.SequenceEqual(actual),
            "Entries English calls a failure and Russian does not (the list ExpectedEnglishOnlyFailures should be exactly this):" +
            Environment.NewLine + string.Join(Environment.NewLine, actual.Select(k => $"        \"{k}\",")));
    }

    [Fact]
    public void TheSkippedCatalogEntries_AreExactlyTheListedOnes()
        => Assert.Equal(ExpectedSkipped.OrderBy(k => k, StringComparer.Ordinal),
            FixResultClassifier.MatchersFor(RealCatalog()).Skipped);

    [Theory]
    [InlineData("Done.")]
    [InlineData("Removed 3 files (1.2 GB).")]
    [InlineData("Game mode is on.")]
    [InlineData("Automatic driver updates canceled.")]
    [InlineData("")]
    [InlineData(null)]
    public void SuccessTexts_AreNotFailures_InEnglish(string? text)
    {
        var catalog = RealCatalog();
        Assert.False(FixResultClassifier.IsFailure(text, T.English, catalog));
        Assert.False(FixResultClassifier.NeedsAdmin(text, T.English, catalog));
    }

    [Theory]
    [InlineData("Done. Could not fix: access denied.")]
    [InlineData("Removed 3 files (1.2 GB).\nCould not delete the file: it is in use.")]
    [InlineData("Done. The fix was canceled.")]
    public void ACombinedText_WithAFailureSentence_IsAFailure_InEnglish(string text)
        => Assert.True(FixResultClassifier.IsFailure(text, T.English, RealCatalog()));

    [Theory]
    [InlineData("Done. Administrator rights are needed for the firewall.")]
    [InlineData("The action “Turn on” needs administrator rights.")]
    [InlineData("RESTORING NEEDS ADMINISTRATOR RIGHTS")]
    public void AnAdminOnlySentence_NeedsAdmin_AndIsAFailure_InEnglish(string text)
    {
        var catalog = RealCatalog();
        Assert.True(FixResultClassifier.NeedsAdmin(text, T.English, catalog));
        Assert.True(FixResultClassifier.IsFailure(text, T.English, catalog));
    }

    [Fact]
    public void AnUntranslatedRussianText_IsStillRecognised_InEnglish()
    {
        var catalog = RealCatalog();
        Assert.True(FixResultClassifier.IsFailure("Не удалось включить защиту.", T.English, catalog));
        Assert.True(FixResultClassifier.NeedsAdmin("Нужны права администратора.", T.English, catalog));
    }

    [Fact]
    public void InRussian_OnlyTheRussianRulesCount()
    {
        var catalog = RealCatalog();
        Assert.False(FixResultClassifier.IsFailure("Could not fix: access denied.", T.Russian, catalog));
        Assert.False(FixResultClassifier.NeedsAdmin("Administrator rights needed", T.Russian, catalog));
        Assert.True(FixResultClassifier.IsFailure("Не удалось обновить сигнатуры: нет сети", T.Russian, catalog));
        Assert.False(FixResultClassifier.IsFailure("Удалено 300 МБ. Не удалось удалить 3 занятых файла.", T.Russian, catalog));
    }

    [Fact]
    public void Placeholders_MatchAnything_AndTheLiteralPartsAreLiteral()
    {
        var catalog = new Dictionary<string, string>
        {
            ["Не удалось починить {0} (код {1:X8})."] = "Could not repair {0} (code {1:X8}).",
        };
        Assert.True(FixResultClassifier.IsFailure("Could not repair the disk C: (code 0000001F).", T.English, catalog));
        Assert.True(FixResultClassifier.IsFailure("could not repair  the disk (code 1)", T.English, catalog)); // case, spaces, no period
        Assert.False(FixResultClassifier.IsFailure("Could not repair the disk code 1.", T.English, catalog)); // "(" is a bracket, not a group
        Assert.False(FixResultClassifier.IsFailure("Could not repaint the disk (code 1).", T.English, catalog));
    }

    [Fact]
    public void AnEntryWithAContext_IsJudgedByItsRussianText_NotByTheKeyWithTheContext()
    {
        var catalog = new Dictionary<string, string>
        {
            ["uninstall\u0004Не удалось удалить ({0}):"] = "Could not uninstall ({0}):",
            ["status\u0004Готово"] = "Ready",
            ["admin\u0004Нужны права администратора для {0}."] = "Administrator rights are needed for {0}.",
        };

        var matchers = FixResultClassifier.MatchersFor(catalog);

        Assert.Equal(2, matchers.Failure.Count); // the failure and the admin text; «Готово» is neither
        Assert.Single(matchers.AdminRequired);
        Assert.Empty(matchers.Skipped);
        Assert.True(FixResultClassifier.IsFailure("Done. Could not uninstall (2): Chrome.", T.English, catalog));
        Assert.True(FixResultClassifier.NeedsAdmin("Administrator rights are needed for the firewall.", T.English, catalog));
        Assert.False(FixResultClassifier.IsFailure("Ready", T.English, catalog));
    }

    [Fact]
    public void ASideNote_WithAContext_GivesNoMatcherEither()
    {
        var catalog = new Dictionary<string, string> { ["cleanup\u0004Не удалось очистить: {0}."] = "Could not clean up: {0}." };

        Assert.Empty(FixResultClassifier.MatchersFor(catalog).Failure);
        Assert.False(FixResultClassifier.IsFailure("Deleted 3 MB. Could not clean up: Windows logs.", T.English, catalog));
    }

    [Fact]
    public void AShortEntryWithAContext_IsListedAsSkipped_WithItsContext()
    {
        var catalog = new Dictionary<string, string> { ["status\u0004Ошибка"] = "Error" };

        Assert.Equal(new[] { "status\u0004Ошибка" }, FixResultClassifier.MatchersFor(catalog).Skipped);
    }

    [Fact]
    public void AMatcher_StartsAndEndsAtWordBoundaries()
    {
        var catalog = new Dictionary<string, string> { ["Файл не найден"] = "File not found" };
        Assert.True(FixResultClassifier.IsFailure("Done. File not found.", T.English, catalog));
        Assert.False(FixResultClassifier.IsFailure("Profile not founded.", T.English, catalog));
    }

    [Fact]
    public void ShortAndPlaceholderOnlyTemplates_AreSkipped()
    {
        var catalog = new Dictionary<string, string>
        {
            ["Ошибка: {0}"] = "Error: {0}",
            ["Не удалось {0}: {1}"] = "{0}: {1}",
            ["Отменено."] = "Canceled.",
            ["Не удалось открыть"] = "Could not open",
            ["Не удалось включить {0}"] = " ",
            ["Готово."] = "Done.",
        };
        var matchers = FixResultClassifier.MatchersFor(catalog);
        Assert.Equal(new[] { "Не удалось {0}: {1}", "Ошибка: {0}", "Отменено." }.OrderBy(k => k, StringComparer.Ordinal), matchers.Skipped);
        Assert.Single(matchers.Failure); // "Could not open"; a blank value is no translation (the Russian text is shown)
        Assert.False(FixResultClassifier.IsFailure("Error: x", T.English, catalog));
        Assert.False(FixResultClassifier.IsFailure("Done. Canceled.", T.English, catalog));
        Assert.True(FixResultClassifier.IsFailure("Could not open the folder.", T.English, catalog));
    }

    [Fact]
    public void TheMatchers_AreBuiltOncePerCatalog_AndAgainForAnotherCatalog()
    {
        var first = new Dictionary<string, string> { ["Не удалось починить диск."] = "Could not repair the disk." };
        var second = new Dictionary<string, string> { ["Готово."] = "Done." };
        var built = new ConcurrentBag<FixResultClassifier.CatalogMatchers>();
        Parallel.For(0, 64, _ => built.Add(FixResultClassifier.MatchersFor(first)));
        Assert.Single(built.Distinct());
        Assert.Same(built.First(), FixResultClassifier.MatchersFor(first));
        Assert.NotSame(built.First(), FixResultClassifier.MatchersFor(second));
        Assert.True(FixResultClassifier.IsFailure("Could not repair the disk.", T.English, first));
        Assert.False(FixResultClassifier.IsFailure("Could not repair the disk.", T.English, second));
    }

    [Fact]
    public void HealthScoring_UsesTheCatalogOfTheCurrentLanguage()
    {
        using (TestLanguage.English())
        {
            Assert.True(HealthScoring.LooksLikeFailure("Done. Could not fix: access denied."));
            Assert.True(HealthScoring.LooksLikeAdminRequired("The action “Turn on” needs administrator rights."));
            Assert.False(HealthScoring.LooksLikeFailure("Done."));
        }
        // Back in Russian: the English sentences mean nothing, the Russian rules are as before.
        Assert.False(HealthScoring.LooksLikeFailure("Done. Could not fix: access denied."));
        Assert.True(HealthScoring.LooksLikeFailure("Не удалось обновить сигнатуры: нет сети"));
    }

    [Fact]
    public void HealthScoring_RebuildsTheMatchers_WhenTheEnglishCatalogIsReplaced()
    {
        using (TestLanguage.English(new Dictionary<string, string> { ["Не удалось починить диск."] = "Could not repair the disk." }))
            Assert.True(HealthScoring.LooksLikeFailure("Could not repair the disk."));
        using (TestLanguage.English(new Dictionary<string, string> { ["Готово."] = "Done." }))
            Assert.False(HealthScoring.LooksLikeFailure("Could not repair the disk."));
    }
}
