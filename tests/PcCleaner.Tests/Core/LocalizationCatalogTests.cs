using System.IO;
using PcCleaner.Core.Localization;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// en.json is complete for the translated parts of the program: every T._("…"), T.F("…", …), T.Plural(n, "…", "…", "…")
/// (and Format.Plural/Format.Count, which go through T.Plural) and {loc:T '…'} of the <see cref="Enforced"/> files has an
/// entry with the same {0} placeholders, and en.json has no entry that nothing uses. See <see cref="LocalizationCatalog"/>.
/// </summary>
public class LocalizationCatalogTests
{
    /// <summary>
    /// Files and folders under src/PcCleaner whose texts must all be in en.json: "/" separators, a folder ends with "/",
    /// e.g. "Modules/Settings/" or "MainWindow.xaml". Each translation task adds the files it wraps. Texts of other files may
    /// stay untranslated (they show in Russian). Core/Localization/ and Core/Format.cs pass the caller's text on
    /// (T._(Text), T.Plural(n, one, few, many)) and are not listed: they have no texts of their own.
    /// </summary>
    internal static readonly string[] Enforced =
    {
    };

    [Fact]
    public void EveryTextOfTheEnforcedFiles_IsInEnJson_AndEnJsonHasNoStaleEntries()
    {
        var problems = LocalizationCatalog.Check(SourceFiles(), File.ReadAllText(EnJsonPath()), Enforced);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EnforcedPaths_Exist()
    {
        var root = SourceRoot();
        foreach (var path in Enforced)
        {
            var full = Path.Combine(root, path.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
            Assert.True(path.EndsWith('/') ? Directory.Exists(full) : File.Exists(full), $"{path} is not in src/PcCleaner");
        }
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
    public void TextsOutsideTheEnforcedFiles_MayStayUntranslated()
        => Assert.Empty(Check("{}", ("Other/A.cs", "T._(\"Проверить снова\");"), ("Other/A.xaml", "<Button Content=\"{loc:T 'Сохранить'}\" />")));

    [Fact]
    public void AnEnforcedFile_CanBeNamedExactly()
    {
        var files = new[] { ("Shell/Main.cs", "T._(\"Да\");"), ("Shell/Other.cs", "T._(\"Нет\");") };

        var problem = Assert.Single(LocalizationCatalog.Check(files, "{}", new[] { "Shell/Main.cs" }));
        Assert.Contains("«Да»", problem);
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
