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
    /// What is fully translated: all of src/PcCleaner ("/" is the whole tree; a list of files and folders — "Modules/Settings/",
    /// "MainWindow.xaml" — would do for a partly translated tree). There, the words of Format.Plural/Format.Count need en.json
    /// entries too, T._/T.F/T.Plural take only literals or consts (except the <see cref="PassThrough"/> files), no Cyrillic literal
    /// stays outside T in C# and no Russian text outside {loc:T '…'} in XAML. Wrapped texts (T._("…"), {loc:T '…'}) need their
    /// entry in every file, listed or not.
    /// </summary>
    internal static readonly string[] Enforced = { "/" };

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
            Assert.True(path.EndsWith('/') ? Directory.Exists(full) : File.Exists(full), $"{path} is not in src/PcCleaner"); // "/" is the root itself
        }
    }

    [Fact]
    public void TheWholeTree_IsEnforced()
        => Assert.Equal(new[] { "/" }, Enforced);

    [Fact]
    public void ThePassThroughFiles_PassWhenTheirFoldersAreEnforced()
    {
        var enforced = new[] { "Core/Localization/", "Core/Format.cs" };

        var problems = LocalizationCatalog.Check(SourceFiles(), File.ReadAllText(EnJsonPath()), enforced, PassThrough);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.NotEmpty(LocalizationCatalog.Check(SourceFiles(), File.ReadAllText(EnJsonPath()), enforced)); // the list is what lets them pass
    }

    [Fact]
    public void TheEnforcedFiles_HaveNoRussianTextOutsideT_InCSharpAndXaml_AndNoUnclosedDataRegion()
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
        var problems = Check(catalog, ("Pages/A.cs", "T.F(\"Найдено {0} из {1}\", a, b);"));

        Assert.Contains(problems, p => p.Contains("placeholder"));
        Assert.All(problems, p => Assert.True(p.Contains("placeholder") || p.Contains("passes 2"), p)); // {3} also has no argument
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

    // ----- the arguments of T.F / T.CtxF: string.Format throws at run time when a placeholder has none -----

    [Fact]
    public void AFormatCall_WithFewerArgumentsThanPlaceholders_IsReported_WithTheFileAndLine()
    {
        var problems = Check("""{ "Найдено {0} из {1}": "Found {0} of {1}" }""", ("Pages/A.cs", "var a = 1;\nvar b = T.F(\"Найдено {0} из {1}\", found);"));

        var problem = Assert.Single(problems);
        Assert.Contains("Pages/A.cs:2", problem);
        Assert.Contains("«Найдено {0} из {1}»", problem);
        Assert.Contains("uses 2 argument", problem);
        Assert.Contains("passes 1", problem);
    }

    [Fact]
    public void AFormatCall_WithoutAnyArgument_ButWithAPlaceholder_IsReported()
        => Assert.Contains("passes 0", Assert.Single(Check("""{ "Найдено {0}": "Found {0}" }""", ("Pages/A.cs", "T.F(\"Найдено {0}\");"))));

    [Fact]
    public void AFormatCall_WithFewerArgumentsThanTheEnglishValueUses_IsReported_AsWell()
    {
        // the Russian text needs one argument, the English value two: the English run would throw
        var problems = Check("""{ "Найдено {0}": "Found {0} of {1}" }""", ("Pages/A.cs", "T.F(\"Найдено {0}\", found);"));

        Assert.Contains(problems, p => p.Contains("placeholder"));
        Assert.Contains(problems, p => p.Contains("uses 2 argument") && p.Contains("passes 1"));
    }

    [Fact]
    public void AFormatCall_WithoutAnEntry_IsStillCheckedForItsArguments()
    {
        var problems = Check("{}", ("Pages/A.cs", "T.F(\"Найдено {0} из {1}\", found);"));

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("has no entry"));
        Assert.Contains(problems, p => p.Contains("passes 1"));
    }

    [Fact]
    public void AFormatCall_WithMoreArgumentsThanPlaceholders_IsReported()
    {
        var problems = Check("""{ "Найдено {0}": "Found {0}", "Привет": "Hello" }""",
            ("Pages/A.cs", "T.F(\"Найдено {0}\", found, total);\nT.F(\"Привет\", name);"));

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("Pages/A.cs:1:") && p.Contains("uses 1 argument") && p.Contains("passes 2"));
        Assert.Contains(problems, p => p.StartsWith("Pages/A.cs:2:") && p.Contains("uses 0 argument") && p.Contains("passes 1"));
    }

    [Fact]
    public void ACtxFCall_IsCheckedForItsArguments_NotCountingTheContext()
    {
        const string catalog = """{ "c\u0004Найдено {0} из {1}": "Found {0} of {1}" }""";

        Assert.Empty(Check(catalog, ("Pages/A.cs", "T.CtxF(\"c\", \"Найдено {0} из {1}\", a, b);")));
        var problem = Assert.Single(Check(catalog, ("Pages/A.cs", "T.CtxF(\"c\", \"Найдено {0} из {1}\", a);")));
        Assert.Contains("passes 1", problem);
    }

    [Fact]
    public void FormatCalls_WithTheRightNumberOfArguments_AreFine()
    {
        const string catalog = """
            {
              "Найдено {0} из {1}": "Found {0} of {1}",
              "Нет {0} и снова {0}": "No {0} and {0} again",
              "Порядок {1} {0}": "Order {0} {1}",
              "Привет": "Hello"
            }
            """;
        var cs = """
            T.F("Найдено {0} из {1}", Count(a, b), items.Where(i => i.Name == "x, y").Count());
            T.F("Нет {0} и снова {0}", name);
            T.F("Привет");
            T.F("Порядок {1} {0}", $"{a:N0}, {b}", c ? "x" : "y");
            """;

        Assert.Empty(Check(catalog, ("Pages/A.cs", cs)));
    }

    [Fact]
    public void AReadyArray_PassedToTheParamsOfTF_IsNotCounted()
    {
        var cs = """
            T.F("Найдено {0} из {1}", new object[] { a, b });
            T.F("Найдено {0} из {1}", new[] { a, b });
            T.F("Найдено {0} из {1}", values.ToArray());
            """;

        Assert.Empty(Check("""{ "Найдено {0} из {1}": "Found {0} of {1}" }""", ("Pages/A.cs", cs)));
    }

    [Fact]
    public void TheArgumentsOfOtherCalls_AreNotCounted()
    {
        const string catalog = """{ "c\u0004Один": "One", "Один": "One", "Пять": "Five", "Три": "Three" }""";

        Assert.Empty(Check(catalog, ("Pages/A.cs", "T._(\"Один\");\nT.Ctx(\"c\", \"Один\");\nT.Plural(n, \"Три\", \"Трёх\", \"Пять\");")));
    }

    // ----- texts with a context: T.Ctx / T.CtxF / T.CtxPlural / {loc:T '…', Context=…} -----

    [Fact]
    public void ATextWithAContext_NeedsTheEntryOfThatContext_NotThePlainOne()
    {
        var cs = "var a = T.Ctx(\"status\", \"Готово\");";

        var problem = Assert.Single(Check("""{ "Готово": "Done" }""", ("Pages/A.cs", cs), ("Pages/B.cs", "T._(\"Готово\");")));

        Assert.Contains("Pages/A.cs:1", problem);
        Assert.Contains("«Готово»", problem);
        Assert.Contains("status", problem);
        Assert.Empty(Check("{ \"status\\u0004Готово\": \"Ready\", \"Готово\": \"Done\" }", ("Pages/A.cs", cs), ("Pages/B.cs", "T._(\"Готово\");")));
    }

    [Fact]
    public void EachKindOfTextWithAContext_IsChecked()
    {
        var cs = """
            T.Ctx("status", "Один");
            T.CtxF("status", "Два {0}", x);
            T.CtxPlural("units", n, "три", "трёх", "трёх-много");
            """;

        var problems = Check("{}", ("Pages/A.cs", cs), ("Pages/B.xaml", "<TextBlock Text=\"{loc:T 'Шесть', Context=status}\" />"));

        foreach (var text in new[] { "Один", "Два {0}", "три", "трёх-много", "Шесть" })
            Assert.Contains(problems, p => p.Contains($"«{text}»"));
        Assert.DoesNotContain(problems, p => p.Contains("«трёх»")); // "few" is Russian only
        Assert.Equal(5, problems.Count);
    }

    [Fact]
    public void TextsWithAContext_AreChecked_InEveryFile()
    {
        var problems = Check("{}", ("Other/A.cs", "T.Ctx(\"status\", \"Один\");\nT.CtxF(\"status\", \"Два {0}\", x);\nT.CtxPlural(\"u\", n, \"три\", \"трёх\", \"много\");"));

        Assert.Equal(4, problems.Count);
    }

    [Fact]
    public void AllTranslatedWithContexts_IsFine()
    {
        const string catalog = """
            {
              "monitors\u0004монитор": "monitor",
              "monitors\u0004мониторов": "monitors",
              "status\u0004Готово": "Ready",
              "uninstall\u0004Удалено: {0} из {1}.": "Uninstalled {0} of {1}.",
              "Удалено: {0} из {1}.": "Deleted: {0} of {1}."
            }
            """;
        var cs = """
            var a = T.Ctx("status", "Готово");
            var b = T.CtxF("uninstall", "Удалено: {0} из {1}.", a, b);
            var c = T.F("Удалено: {0} из {1}.", a, b);
            var d = T.CtxPlural("monitors", n, "монитор", "монитора", "мониторов");
            """;

        Assert.Empty(Check(catalog, ("Pages/A.cs", cs)));
    }

    [Fact]
    public void AContextEntry_UsedNowhere_IsStale_AndSoIsAPlainEntryUsedOnlyWithContexts()
    {
        const string catalog = """{ "other\u0004Готово": "Finished", "status\u0004Готово": "Ready", "Готово": "Done" }""";

        var problems = Check(catalog, ("Pages/A.cs", "T.Ctx(\"status\", \"Готово\");"));

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("stale") && p.Contains("other") && p.Contains("Готово"));
        Assert.Contains(problems, p => p.Contains("stale") && p.Contains("«Готово»") && !p.Contains("other") && !p.Contains("status"));
    }

    [Theory]
    [InlineData("""{ "uninstall\u0004Удалено: {0} из {1}.": "Uninstalled {0}" }""")]
    [InlineData("""{ "uninstall\u0004Удалено: {0} из {1}.": "Uninstalled {0} of {2}" }""")]
    public void DifferentPlaceholders_OfAContextEntry_AreReported(string catalog)
    {
        var problems = Check(catalog, ("Pages/A.cs", "T.CtxF(\"uninstall\", \"Удалено: {0} из {1}.\", a, b);"));

        Assert.Contains(problems, p => p.Contains("placeholder"));
        Assert.All(problems, p => Assert.True(p.Contains("placeholder") || p.Contains("passes 2"), p)); // {2} also has no argument
    }

    [Fact]
    public void ABrokenFormatString_OfAContextText_IsReported()
    {
        Assert.Contains("format", Assert.Single(Check("{}", ("Other/A.cs", "T.CtxF(\"c\", \"Найдено {0\", n);"))));
        Assert.Contains("format", Assert.Single(Check("""{ "c\u0004Найдено {0}": "Found {0} {" }""", ("Pages/A.cs", "T.CtxF(\"c\", \"Найдено {0}\", a);"))));
    }

    [Fact]
    public void AContextText_InTheEnforcedFiles_MustBeALiteralOrAConst_AndSoMustTheContext()
    {
        var problems = Check("{}", ("Pages/A.cs", "T.Ctx(\"status\", name);\nT.CtxF(context, \"Один\", x);\nT.CtxPlural(\"u\", n, one, \"а\", many);"));

        Assert.Equal(4, problems.Count); // the text; the context; one and many
        Assert.All(problems, p => Assert.Contains("literal", p));
    }

    [Fact]
    public void AConstContext_AndAConstText_AreResolved()
    {
        var files = new[]
        {
            ("Pages/A.cs", "T.Ctx(Contexts.Status, Texts.Ready);"),
            ("Other/B.cs", "public const string Status = \"status\";\npublic const string Ready = \"Готово\";"),
        };

        Assert.Empty(LocalizationCatalog.Check(files, """{ "status\u0004Готово": "Ready" }""", new[] { "Pages/" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{0}")]
    [InlineData("статус")]
    [InlineData("a\u0004b")]
    [InlineData("status:")]
    public void AnInvalidContext_IsReported_InEveryFile(string context)
    {
        var escaped = context.Replace("\u0004", "\\u0004");
        var problems = LocalizationCatalog.Check(new[] { ("Other/A.cs", $"T.Ctx(\"{escaped}\", \"Готово\");") }, "{}", Array.Empty<string>());

        Assert.Contains(problems, p => p.Contains("context"));
    }

    [Fact]
    public void AnInvalidContext_InAKeyOfEnJson_IsReported()
    {
        var problems = Check("""{ "status:\u0004Пока": "Bye", "{0}\u0004Готово": "Ready" }""", ("Pages/A.cs", "var a = 1;"));

        Assert.Equal(2, problems.Count(p => p.StartsWith("en.json: the context") && p.Contains("short Latin name")));
    }

    [Fact]
    public void AValidContext_MayHaveWordsDigitsAndSeparators()
        => Assert.Empty(Check("""{ "main-window.title 2\u0004Готово": "Ready" }""", ("Pages/A.cs", "T.Ctx(\"main-window.title 2\", \"Готово\");")));

    [Fact]
    public void XamlLocT_WithAContext_IsFound_InEveryFile()
    {
        var xaml = "<TextBlock Text=\"{loc:T 'Готово', Context=status}\" ToolTip=\"{loc:T Text='Пока', Context=status}\" Tag=\"{loc:T 'Нажмите \\'Да\\'',Context = status }\" />";

        var problems = Check("{}", ("Other/A.xaml", xaml));

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("«Готово»") && p.Contains("status"));
        Assert.Contains(problems, p => p.Contains("«Пока»"));
        Assert.Contains(problems, p => p.Contains("«Нажмите 'Да'»"));
        Assert.Empty(Check("{ \"status\\u0004Готово\": \"Ready\", \"status\\u0004Нажмите 'Да'\": \"Press 'Yes'\", \"status\\u0004Пока\": \"Bye\", \"Готово\": \"Done\" }",
            ("Pages/A.xaml", xaml), ("Pages/B.cs", "T._(\"Готово\");")));
    }

    [Fact]
    public void XamlLocT_WithAnotherNamedProperty_IsReported()
        => Assert.Contains("{loc:T '", Assert.Single(Check("{}", ("Other/A.xaml", "<TextBlock Text=\"{loc:T 'Готово', Other=status}\" />"))));

    [Fact]
    public void XamlLocT_WithAContext_InsideAnotherMarkupExtension_IsFound()
    {
        var problem = Assert.Single(Check("{}", ("Pages/A.xaml", "<TextBlock Text=\"{Binding Name, FallbackValue={loc:T 'Без имени', Context=name}}\" />")));

        Assert.Contains("«Без имени»", problem);
    }

    [Fact]
    public void ACtxCall_InAComment_IsNotAText()
        => Assert.Empty(Check("{}", ("Pages/A.cs", "// T.Ctx(\"status\", \"Готово\");\n/// <summary>T.CtxF(\"c\", \"Один {0}\", x)</summary>")));

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
    public void ACyrillicLiteral_AsAnotherArgumentOfAWrappingCall_IsReported()
    {
        var cs = """
            var a = T.F("Найдено {0}", "файл");
            var b = T.CtxF("units", "Найдено {0}", "файл");
            var c = T.F("Найдено {0}", ok ? "да" : "нет");
            var d = T.F("Найдено {0}", Name("файл"));
            var e = T.F("Найдено {0}", new[] { "файл" });
            """;

        var problems = Unwrapped(cs);

        Assert.Equal(6, problems.Count); // "да" and "нет" are two
        Assert.All(new[] { 1, 2, 3, 3, 4, 5 }, line => Assert.Contains(problems, p => p.StartsWith($"Pages/A.cs:{line}:")));
    }

    [Fact]
    public void ATextPositionOfAWrappingCall_IsFine_AndSoIsAWrappedLiteralInAnotherArgument()
    {
        var cs = """
            var a = T.F("Найдено {0}", T._("файл"));
            var b = T.CtxF("units", "Найдено {0}", T.Ctx("units", "файл"));
            var c = T.Plural(n, "файл", "файла", "файлов");
            var d = T.CtxPlural("units", n, "файл", "файла", "файлов");
            var e = Format.Count(n, "файл", "файла", "файлов") + Format.Plural(n, "день", "дня", "дней");
            var f = Dialogs.Confirm(T._("Заголовок"), T.F("Вопрос {0}?", Name(T._("файл"))));
            var g = T.Ctx("status", "Готово");
            """;

        Assert.Empty(Unwrapped(cs));
    }

    [Fact]
    public void ACyrillicLiteral_InAContextOfAWrappingCall_IsReported()
        => Assert.Single(Unwrapped("var a = T.Ctx(\"статус\", \"Готово\");"));

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
    public void ARegionMarkedAsData_WithoutItsEnd_IsReported_WithTheLineOfItsBegin()
    {
        var cs = """
            var before = 1;
            // l10n:data-begin — Russian words that are matched, never shown
            var months = new[] { "января", "февраля" };
            var shown = "Привет";
            """;

        var problems = Unwrapped(cs);

        var problem = Assert.Single(problems); // the open region hides "Привет" too, so the one problem is the missing end
        Assert.StartsWith("Pages/A.cs:2:", problem);
        Assert.Contains("l10n:data-end", problem);
    }

    [Fact]
    public void ARegionMarkedAsData_StartedInsideAnotherOne_LeavesTheFirstUnclosed()
    {
        var cs = """
            // l10n:data-begin — first
            var a = new[] { "января" };
            // l10n:data-begin — second
            var b = new[] { "февраля" };
            // l10n:data-end
            var shown = "Привет";
            """;

        var problem = Assert.Single(Unwrapped(cs).Where(p => p.Contains("l10n:data-end")));

        Assert.StartsWith("Pages/A.cs:1:", problem);
    }

    [Fact]
    public void ClosedRegionsOneAfterAnother_AreFine_AndAnUncheckedFileMayLeaveOneOpen()
    {
        var cs = """
            // l10n:data-begin — one
            var a = new[] { "января" };
            // l10n:data-end
            // l10n:data-begin — two
            var b = new[] { "февраля" };
            // l10n:data-end
            """;

        Assert.Empty(Unwrapped(cs));
        Assert.Empty(Unwrapped("// l10n:data-begin — open\nvar a = 1;", "Other/A.cs"));
    }

    [Fact]
    public void AConst_PassedThroughT_InAnotherFile_IsFine()
        => Assert.Empty(Unwrapped("public const string Status = \"Проверяем…\";", "Pages/A.cs", ("Other/B.cs", "var a = T._(A.Status);")));

    [Fact]
    public void AFile_OutsideTheEnforcedList_IsNotChecked()
    {
        Assert.Empty(Unwrapped("var a = \"Привет\";", "Other/A.cs"));
        Assert.Empty(Unwrapped("<TextBlock Text=\"Привет\" />", "Other/A.xaml"));
    }

    [Fact]
    public void TheEnforcedRoot_CoversEveryFile()
    {
        var files = new[] { ("A.cs", "var a = \"Привет\";"), ("Deep/Er/B.cs", "var b = \"Пока\";"), ("Themes/T.xaml", "<TextBlock Text=\"Да\" />") };

        Assert.Equal(3, LocalizationCatalog.UnwrappedCyrillic(files, new[] { "/" }).Count);
        // and the words of Format.Count need their entries in any file, as in an enforced folder
        var count = ("Deep/Er/B.cs", "Format.Count(n, \"день\", \"дня\", \"дней\");");
        Assert.Equal(2, LocalizationCatalog.Check(new[] { count }, "{}", new[] { "/" }).Count);
        Assert.Empty(LocalizationCatalog.Check(new[] { count }, "{}", new[] { "Other/" }));
    }

    // ----- the guard in XAML: a Russian text must be {loc:T '…'} -----

    [Fact]
    public void ARussianTextInXaml_OutsideLocT_IsReported_WithTheFileAndLine()
    {
        var xaml = """
            <StackPanel>
              <TextBlock Text="Привет" />
              <Button Content="{Binding Name, StringFormat='Всего: {0}'}" ToolTip="Подсказка" />
              <TextBlock>Текст внутри</TextBlock>
            </StackPanel>
            """;

        var problems = Unwrapped(xaml, "Pages/A.xaml");

        Assert.Equal(3, problems.Count);
        Assert.Contains("Pages/A.xaml:2", problems[0]);
        Assert.Contains("Pages/A.xaml:3", problems[1]);
        Assert.Contains("Pages/A.xaml:4", problems[2]);
    }

    [Fact]
    public void WrappedTexts_Comments_AndTheLanguageName_AreFineInXaml()
    {
        var xaml = """
            <!-- «Привет» is the greeting
                 and «Пока» the farewell -->
            <StackPanel>
              <TextBlock Text="{loc:T 'Привет'}" />
              <Button Content="{loc:T 'Нажмите \'Да\'', Context=status}" ToolTip="{loc:T Text='Подсказка'}" />
              <Button Content="{Binding Name, FallbackValue={loc:T 'Без имени'}}" />
              <RadioButton ToolTip="Русский" AutomationProperties.Name="Русский" Content="Русский" />
              <TextBlock Text="{loc:T 'Первая
                строка'}" />
            </StackPanel>
            """;

        Assert.Empty(Unwrapped(xaml, "Pages/A.xaml"));
    }

    [Fact]
    public void ARussianTextInXaml_AfterAMultiLineComment_IsReportedOnItsOwnLine()
    {
        var xaml = "<!-- a\n b\n c -->\n<TextBlock Text=\"Привет\" />";

        Assert.Contains("Pages/A.xaml:4", Assert.Single(Unwrapped(xaml, "Pages/A.xaml")));
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
