using System.IO;
using PcCleaner.Core.Localization;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// en.json is complete: every wrapped text — T._("…"), T.F("…", …), T.Plural(n, "…", "…", "…"), {loc:T '…'} — of any file
/// has an entry with the same {0} placeholders; in the <see cref="Enforced"/> files also the words of Format.Plural and
/// Format.Count (they go through T.Plural) and every text argument is a literal or a const; and en.json has no entry that
/// nothing uses. See <see cref="LocalizationCatalog"/>.
/// </summary>
public class LocalizationCatalogTests
{
    /// <summary>
    /// Files and folders under src/PcCleaner that are fully translated: "/" separators, a folder ends with "/", e.g.
    /// "Modules/Settings/" or "MainWindow.xaml". There, the words of Format.Plural/Format.Count need en.json entries too and
    /// T._/T.F/T.Plural take only literals or consts (except the <see cref="PassThrough"/> files). Each translation task adds
    /// the files it wraps. Wrapped texts (T._("…"), {loc:T '…'}) need their entry in every file, listed or not.
    /// </summary>
    internal static readonly string[] Enforced =
    {
        "App.xaml.cs",
        "Core/",
        "MainViewModel.cs",
        "MainWindow.xaml",
        "MainWindow.xaml.cs",
        "Modules/Cleaner/",
        "Modules/Dashboard/",
        "Modules/Disk/",
        "Modules/Network/",
        "Modules/Optimizer/",
        "Modules/Processes/",
        "Modules/Programs/",
        "Modules/Security/",
        "Modules/Settings/",
        "Modules/Startup/",
    };

    /// <summary>
    /// Files that pass the caller's text on (T._(Text), T.Plural(n, one, few, many), Format.Count(n, forms[0], …)): they
    /// may give T and Format a variable even inside an enforced folder.
    /// </summary>
    internal static readonly string[] PassThrough =
    {
        "Core/Format.cs",
        "Core/Localization/LocExtension.cs",
    };

    [Fact]
    public void EveryTextOfTheEnforcedFiles_IsInEnJson_AndEnJsonHasNoStaleEntries()
    {
        var problems = LocalizationCatalog.Check(SourceFiles(), File.ReadAllText(EnJsonPath()), Enforced, PassThrough);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EnforcedAndPassThroughPaths_Exist()
    {
        var root = SourceRoot();
        foreach (var path in Enforced.Concat(PassThrough))
        {
            var full = Path.Combine(root, path.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
            Assert.True(path.EndsWith('/') ? Directory.Exists(full) : File.Exists(full), $"{path} is not in src/PcCleaner");
        }
    }

    [Fact]
    public void ThePassThroughFiles_PassWhenTheirFoldersAreEnforced()
    {
        var enforced = new[] { "Core/Localization/", "Core/Format.cs" };

        var problems = LocalizationCatalog.Check(SourceFiles(), File.ReadAllText(EnJsonPath()), enforced, PassThrough);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.NotEmpty(LocalizationCatalog.Check(SourceFiles(), File.ReadAllText(EnJsonPath()), enforced)); // the list is what lets them pass
    }

    [Fact]
    public void TheEnforcedCsFiles_HaveNoCyrillicLiteralOutsideT()
    {
        var problems = LocalizationCatalog.UnwrappedCyrillic(SourceFiles(), Enforced);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheEnglishTexts_UseAmericanSpelling()
    {
        var british = new System.Text.RegularExpressions.Regex(
            @"\b(?:cancell\w*|colour\w*|behaviour\w*|favour\w*|neighbour\w*|centre\w*|organis\w*|recognis\w*|analys(?:e|ed|es|ing)|" +
            @"programme\w*|grey|licence|catalogue\w*|defence|travell\w*|labell\w*|modell\w*|optimis\w*|initialis\w*|synchronis\w*)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var found = T.ParseCatalog(File.ReadAllText(EnJsonPath()))
            .Where(e => british.IsMatch(e.Value))
            .Select(e => $"«{e.Value}»")
            .ToList();

        Assert.True(found.Count == 0, "British spelling in en.json (use the American one):" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    [Fact]
    public void TheEmbeddedCatalog_IsEnJsonFromTheSource()
    {
        Assert.Equal(File.ReadAllText(EnJsonPath()), T.ReadEmbeddedCatalog());
        Assert.Equal(T.ParseCatalog(File.ReadAllText(EnJsonPath())), T.EnglishTexts);
    }

    // ----- the checker itself -----

    private static List<string> Check(string catalog, params (string Path, string Content)[] files)
        => LocalizationCatalog.Check(files, catalog, new[] { "Pages/" });

    [Fact]
    public void AnEmptyCatalog_WithNoTexts_IsFine()
        => Assert.Empty(Check("{}", ("Pages/A.cs", "var x = 1;"), ("Pages/A.xaml", "<Grid />")));

    [Fact]
    public void AllTranslated_IsFine()
    {
        const string catalog = """
            {
              "Найдено {0} из {1}": "Found {0} of {1}",
              "Проверить снова": "Check again",
              "Сохранить": "Save",
              "файл": "file",
              "файлов": "files"
            }
            """;
        var cs = """
            var a = T._("Проверить снова");
            var b = T.F("Найдено {0} из {1}", found, total);
            var c = T.Plural(items.Count(i => i.IsChecked), "файл", "файла", "файлов");
            """;

        Assert.Empty(Check(catalog, ("Pages/A.cs", cs), ("Pages/A.xaml", "<Button Content=\"{loc:T 'Сохранить'}\" />")));
    }

    [Fact]
    public void ATextWithoutAnEntry_IsReported_WithTheFileAndLine()
    {
        var problems = Check("{}", ("Pages/A.cs", "var a = 1;\nvar b = T._(\"Проверить снова\");"));

        var problem = Assert.Single(problems);
        Assert.Contains("Pages/A.cs:2", problem);
        Assert.Contains("Проверить снова", problem);
    }

    [Fact]
    public void EachKindOfText_IsChecked()
    {
        var cs = """
            T._("Один");
            T.F("Два {0}", x);
            T.Plural(n, "три", "трёх", "трёх-много");
            Format.Plural(n, "четыре", "четырёх", "четырёх-много");
            Format.Count(n, "пять", "пяти", "пяти-много");
            """;

        var problems = Check("{}", ("Pages/A.cs", cs), ("Pages/B.xaml", "<TextBlock Text=\"{loc:T 'Шесть'}\" />"));

        foreach (var text in new[] { "Один", "Два {0}", "три", "трёх-много", "четыре", "четырёх-много", "пять", "пяти-много", "Шесть" })
            Assert.Contains(problems, p => p.Contains($"«{text}»"));
        Assert.DoesNotContain(problems, p => p.Contains("«трёх»") || p.Contains("«четырёх»") || p.Contains("«пяти»")); // "few" is Russian only
        Assert.Equal(9, problems.Count);
    }

    [Fact]
    public void WrappedTexts_NeedAnEntry_InEveryFile()
    {
        var cs = """
            T._("Один");
            T.F("Два {0}", x);
            T.Plural(n, "три", "трёх", "трёх-много");
            """;

        var problems = Check("{}", ("Other/A.cs", cs), ("Other/B.xaml", "<TextBlock Text=\"{loc:T 'Шесть'}\" />"));

        foreach (var text in new[] { "Один", "Два {0}", "три", "трёх-много", "Шесть" })
            Assert.Contains(problems, p => p.Contains($"«{text}»"));
        Assert.Equal(5, problems.Count);
    }

    [Fact]
    public void AWrappedFormatString_IsChecked_InEveryFile()
    {
        Assert.Contains("format", Assert.Single(Check("{}", ("Other/A.cs", "T.F(\"Найдено {0\", n);"))));
        Assert.Contains("placeholder", Assert.Single(Check("""{ "Найдено {0}": "Found" }""", ("Other/A.cs", "T.F(\"Найдено {0}\", n);"))));
    }

    [Fact]
    public void AnUnquotedLocT_IsReported_InEveryFile()
        => Assert.Contains("{loc:T '", Assert.Single(Check("{}", ("Other/A.xaml", "<TextBlock Text=\"{loc:T Сохранить}\" />"))));

    [Fact]
    public void FormatCountWords_OutsideTheEnforcedFiles_MayStayUntranslated()
        => Assert.Empty(Check("{}", ("Other/A.cs", "Format.Count(n, \"файл\", \"файла\", \"файлов\");\nFormat.Plural(n, \"день\", \"дня\", \"дней\");")));

    [Fact]
    public void ANonLiteralText_OutsideTheEnforcedFiles_IsAllowed()
        => Assert.Empty(Check("{}", ("Other/A.cs", "T._(name);\nT.Plural(n, one, few, many);\nFormat.Count(n, forms[0], forms[1], forms[2]);")));

    [Fact]
    public void AnEnforcedFile_CanBeNamedExactly()
    {
        var files = new[] { ("Shell/Main.cs", "Format.Count(n, \"день\", \"дня\", \"дней\");"), ("Shell/Other.cs", "Format.Count(n, \"год\", \"года\", \"лет\");") };

        var problems = LocalizationCatalog.Check(files, "{}", new[] { "Shell/Main.cs" });

        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.Contains("Shell/Main.cs", p));
    }

    [Fact]
    public void APassThroughFile_MayUseVariables_InsideAnEnforcedFolder()
    {
        var files = new[]
        {
            ("Core/Format.cs", "public static string Plural(long n, string one, string few, string many) => T.Plural(n, one, few, many);"),
            ("Core/Ui.cs", "Status = T._(text);"),
            ("Core/Words.cs", "Format.Count(n, \"день\", \"дня\", \"дней\");"),
        };

        var problems = LocalizationCatalog.Check(files, """{ "день": "day", "дней": "days" }""", new[] { "Core/" }, new[] { "Core/Format.cs" });

        var problem = Assert.Single(problems); // Format.cs passes, Words.cs is translated, Ui.cs is not a pass-through
        Assert.Contains("Core/Ui.cs:1", problem);
        Assert.Contains("literal", problem);
    }

    [Fact]
    public void APassThroughFile_StillNeedsItsWrappedTexts_AndItsFormatCountWords()
    {
        var files = new[] { ("Core/Format.cs", "T.Plural(n, one, few, many);\nT._(\"Да\");\nFormat.Count(n, \"день\", \"дня\", \"дней\");") };

        var problems = LocalizationCatalog.Check(files, "{}", new[] { "Core/" }, new[] { "Core/Format.cs" });

        Assert.Equal(3, problems.Count); // «Да», «день», «дней»
        Assert.DoesNotContain(problems, p => p.Contains("literal"));
    }

    [Fact]
    public void AStaleEntry_IsReported()
    {
        var problem = Assert.Single(Check("""{ "Больше не нужно": "Not needed" }""", ("Pages/A.cs", "var a = 1;")));

        Assert.Contains("«Больше не нужно»", problem);
    }

    [Fact]
    public void AnEntryUsedOnlyOutsideTheEnforcedFiles_IsNotStale()
        => Assert.Empty(Check("""{ "Да": "Yes" }""", ("Other/A.cs", "T._(\"Да\");")));

    [Fact]
    public void ARussianFewForm_IsNoUseOfAnEntry()
    {
        var problem = Assert.Single(Check("""{ "файл": "file", "файла": "files", "файлов": "files" }""",
            ("Pages/A.cs", "T.Plural(n, \"файл\", \"файла\", \"файлов\");")));

        Assert.Contains("«файла»", problem);
    }

    [Theory]
    [InlineData("""{ "Найдено {0} из {1}": "Found {0}" }""")]
    [InlineData("""{ "Найдено {0} из {1}": "Found {0} of {2}" }""")]
    [InlineData("""{ "Найдено {0} из {1}": "Found {0} of {1} in {3}" }""")]
    public void DifferentPlaceholders_AreReported(string catalog)
    {
        var problem = Assert.Single(Check(catalog, ("Pages/A.cs", "T.F(\"Найдено {0} из {1}\", a, b);")));

        Assert.Contains("placeholder", problem);
    }

    [Fact]
    public void PlaceholderFormatsAndOrder_MayDiffer()
        => Assert.Empty(Check("""{ "Найдено {0:N0} из {1}": "{1}: found {0}" }""", ("Pages/A.cs", "T.F(\"Найдено {0:N0} из {1}\", a, b);")));

    [Fact]
    public void ABrokenFormatString_IsReported()
    {
        var problem = Assert.Single(Check("""{ "Найдено {0}": "Found {0} {" }""", ("Pages/A.cs", "T.F(\"Найдено {0}\", a);")));

        Assert.Contains("format", problem);
    }

    [Fact]
    public void EscapesInLiterals_AreTheTextItself()
    {
        const string catalog = """
            {
              "C:\\Temp": "C:\\Temp",
              "Нажмите \"Да\"": "Press \"Yes\"",
              "Нажмите 'Нет'": "Press 'No'",
              "Строка\nдве": "Line\ntwo"
            }
            """;
        var cs = """
            T._("Нажмите \"Да\"");
            T._(@"C:\Temp");
            T._("Строка\nдве");
            """;

        Assert.Empty(Check(catalog, ("Pages/A.cs", cs), ("Pages/A.xaml", "<TextBlock Text=\"{loc:T 'Нажмите \\'Нет\\''}\" />")));
    }

    [Fact]
    public void XamlEntities_AreDecoded()
        => Assert.Empty(Check("""{ "Да & нет": "Yes & no", "Нажмите \"Да\"": "Press \"Yes\"" }""",
            ("Pages/A.xaml", "<TextBlock Text=\"{loc:T 'Да &amp; нет'}\" ToolTip=\"{loc:T 'Нажмите &quot;Да&quot;'}\" />")));

    [Fact]
    public void XamlLocT_InsideAnotherMarkupExtension_IsFound()
    {
        var problem = Assert.Single(Check("{}", ("Pages/A.xaml", "<TextBlock Text=\"{Binding Name, FallbackValue={loc:T 'Без имени'}}\" />")));

        Assert.Contains("«Без имени»", problem);
    }

    [Fact]
    public void XamlLocT_WithoutQuotes_IsReported()
    {
        var problem = Assert.Single(Check("{}", ("Pages/A.xaml", "<TextBlock Text=\"{loc:T Сохранить}\" />")));

        Assert.Contains("{loc:T '", problem);
    }

    [Fact]
    public void Comments_AreNotTexts()
    {
        var cs = """
            /// <summary>Example: T._("Проверить снова").</summary>
            // T.F("Найдено {0}", n);
            var a = 1;
            """;

        Assert.Empty(Check("{}", ("Pages/A.cs", cs), ("Pages/A.xaml", "<!-- {loc:T 'Сохранить'} --><Grid />")));
    }

    [Fact]
    public void ANonLiteralText_IsReported_InAnEnforcedFile()
    {
        var problems = Check("{}", ("Pages/A.cs", "T._(name);\nT._(\"Да \" + name);\nT.F($\"Найдено {n}\");\nT.Plural(n, one, \"а\", many);"));

        Assert.Equal(5, problems.Count); // one and many of the plural are both texts

        Assert.All(problems, p => Assert.Contains("literal", p));
    }

    [Fact]
    public void AConstText_IsResolved_EvenFromAnotherFile()
    {
        var files = new[]
        {
            ("Pages/A.cs", "T._(Texts.Save);\nT._(Title);"),
            ("Other/Texts.cs", "public const string Save = \"Сохранить\";"),
            ("Pages/B.cs", "private const string Title = @\"Заголовок\";"),
        };

        Assert.Empty(LocalizationCatalog.Check(files, """{ "Заголовок": "Title", "Сохранить": "Save" }""", new[] { "Pages/" }));
        Assert.Equal(2, LocalizationCatalog.Check(files, "{}", new[] { "Pages/" }).Count);
    }

    [Fact]
    public void AComplexFirstArgumentOfPlural_IsSkipped()
    {
        var cs = """
            var s = T.Plural(Count(x => x.Name == "a,b" ? f(1, 2) : g[3, 4], $"{n:0}, {(m > 0 ? "x" : "y")}"), "файл", "файла", "файлов");
            var t = Format.Count(list.Where(i => i.Text != ')').Count(), "день", "дня", "дней");
            """;

        Assert.Empty(Check("""{ "день": "day", "дней": "days", "файл": "file", "файлов": "files" }""", ("Pages/A.cs", cs)));
    }

    [Theory]
    [InlineData("""{ "Б": "B", "А": "A" }""")]                 // not sorted
    [InlineData("""{ "А": "A", "А": "A" }""")]                 // twice
    [InlineData("""{ "А": "A", "Б": 2 }""")]                   // not a string
    [InlineData("""{ "А": "A", "Б": " " }""")]                 // blank
    [InlineData("""{ "А": "A", "Б": "Б" }""")]                 // Russian left
    [InlineData("""{ "А": "A", "Б": "B", }""")]                // not strict JSON
    [InlineData("""{ "А": "A", /* x */ "Б": "B" }""")]
    [InlineData("""[ "А" ]""")]
    public void ABadCatalog_IsReported(string catalog)
        => Assert.NotEmpty(Check(catalog, ("Pages/A.cs", "T._(\"А\");\nT._(\"Б\");")));

    [Fact]
    public void AGoodCatalog_WithTheSameTexts_IsFine()
        => Assert.Empty(Check("""{ "А": "A", "Б": "B" }""", ("Pages/A.cs", "T._(\"А\");\nT._(\"Б\");")));

    // ----- the guard against a forgotten text -----

    private static List<string> Unwrapped(string cs, string path = "Pages/A.cs", params (string Path, string Content)[] more)
        => LocalizationCatalog.UnwrappedCyrillic(new[] { (path, cs) }.Concat(more), new[] { "Pages/" });

    [Fact]
    public void ACyrillicLiteral_OutsideT_IsReported_WithTheFileAndLine()
    {
        var cs = """
            var a = 1;
            var b = "Привет";
            var c = $"Найдено {n}";
            """;

        var problems = Unwrapped(cs);

        Assert.Equal(2, problems.Count);
        Assert.Contains("Pages/A.cs:2", problems[0]);
        Assert.Contains("Pages/A.cs:3", problems[1]);
    }

    [Fact]
    public void WrappedLiterals_AndPluralWords_AreFine()
    {
        var cs = """
            var a = T._("Привет");
            var b = T.F("Найдено {0}", Format.Count(n, "файл", "файла", "файлов"));
            var c = Dialogs.Confirm(T._("Заголовок"), T.F("Вопрос {0}?", name));
            var d = T.Plural(n, "день", "дня", "дней");
            """;

        Assert.Empty(Unwrapped(cs));
    }

    [Fact]
    public void OnlyTheLiteralOutsideT_IsReported_InACallThatAlsoHasWrappedOnes()
    {
        var problem = Assert.Single(Unwrapped("Dialogs.Info(T._(\"Заголовок\"), \"Текст\");"));

        Assert.Contains("\"Текст\"", problem);
        Assert.DoesNotContain("Заголовок", problem);
    }

    [Fact]
    public void CommentsCharsTheLanguageNameAndNonCyrillicText_AreFine()
    {
        var cs = """
            /// <summary>"Привет" in the docs</summary>
            // var a = "Привет";
            var b = 1; // "Привет"
            /* "Привет" */
            var c = 'я';
            var d = "Русский";
            var e = "Hello";
            """;

        Assert.Empty(Unwrapped(cs));
    }

    [Fact]
    public void AConst_PassedThroughT_IsFine_ButAConstUsedOtherwiseIsReported()
    {
        var cs = """
            public const string Status = "Проверяем…";
            public const string Key = "Ключ";
            var a = T._(Status);
            var b = Key;
            """;

        var problem = Assert.Single(Unwrapped(cs));

        Assert.Contains("Pages/A.cs:2", problem);
    }

    [Fact]
    public void AConst_WithItsValueOnTheNextLine_IsFine_WhenPassedThroughT()
    {
        var cs = """
            internal const string Note =
                "Остановлено — возможно, сделано не всё.";
            var a = T._(Note);
            """;

        Assert.Empty(Unwrapped(cs));
    }

    [Fact]
    public void PluralWordsInAnInterpolatedString_AreFine_ButItsOwnText_IsReported()
    {
        var cs = """
            var a = $"{Format.Count(n, "файл", "файла", "файлов")} · {size}";
            var b = $"Найдено {Format.Count(n, "файл", "файла", "файлов")}";
            """;

        var problem = Assert.Single(Unwrapped(cs));

        Assert.Contains("Pages/A.cs:2", problem);
    }

    [Fact]
    public void ARegionMarkedAsData_IsNotChecked_ButTheLinesAroundItAre()
    {
        var cs = """
            var before = "Привет";
            // l10n:data-begin — Russian words that are matched, never shown
            var months = new[] { "января", "февраля" };
            var rx = new Regex(@"\bне\s+удалось\b");
            // l10n:data-end
            var after = "Пока";
            """;

        var problems = Unwrapped(cs);

        Assert.Equal(2, problems.Count);
        Assert.Contains("Pages/A.cs:1", problems[0]);
        Assert.Contains("Pages/A.cs:6", problems[1]);
    }

    [Fact]
    public void AConst_PassedThroughT_InAnotherFile_IsFine()
        => Assert.Empty(Unwrapped("public const string Status = \"Проверяем…\";", "Pages/A.cs", ("Other/B.cs", "var a = T._(A.Status);")));

    [Fact]
    public void AFile_OutsideTheEnforcedList_AndXaml_AreNotChecked()
    {
        Assert.Empty(Unwrapped("var a = \"Привет\";", "Other/A.cs"));
        Assert.Empty(Unwrapped("<TextBlock Text=\"Привет\" />", "Pages/A.xaml"));
    }

    // ----- the repository -----

    internal static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PcCleaner");
            if (File.Exists(Path.Combine(candidate, "PcCleaner.csproj")))
                return candidate;
        }
        throw new DirectoryNotFoundException($"src/PcCleaner not found above {AppContext.BaseDirectory}");
    }

    private static string EnJsonPath() => Path.Combine(SourceRoot(), "Core", "Localization", "en.json");

    /// <summary>Every .cs and .xaml file of src/PcCleaner (not bin/obj), with its path relative to it ("/" separators).</summary>
    private static IEnumerable<(string Path, string Content)> SourceFiles()
    {
        var root = SourceRoot();
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                continue;
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return (relative, File.ReadAllText(file));
        }
    }
}
