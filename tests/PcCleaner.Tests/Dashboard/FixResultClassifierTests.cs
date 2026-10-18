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
            var russian = Sample(russianKey, T.Russian);
            var failure = FixResultClassifier.IsFailure(russian, T.Russian, null);
            var admin = FixResultClassifier.NeedsAdmin(russian, T.Russian, null);
            if ((!failure && !admin) || ExpectedSkipped.Contains(russianKey))
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
