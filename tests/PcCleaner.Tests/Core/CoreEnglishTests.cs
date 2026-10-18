using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Controls;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Network;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// The shared texts of Core in English (the real en.json): the explanation of an exception (<see cref="ErrorText"/>), the restart
/// results, the name of the module on the Home badges. Runs alone (the language is process-wide) and always switches back to Russian;
/// the Russian twins are checked outside the English block.
/// </summary>
[Collection(LanguageSequentialCollection.Name)]
public class CoreEnglishTests
{
    private static readonly Regex Cyrillic = new("[\u0400-\u04FF]");

    private const string RuPath = @"C:\Users\Иван\x.txt";

    public CoreEnglishTests() => TestEnvironment.Init();

    // ----- ErrorText -----

    [Fact]
    public void TheCommonErrors_AreToldByTheirType_InEnglish()
    {
        using (TestLanguage.English())
        {
            Assert.Equal("access denied — administrator rights are needed, or Windows protects the item",
                ErrorText.For(new UnauthorizedAccessException($"Access to the path '{RuPath}' is denied.")));
            Assert.Equal("file or folder not found", ErrorText.For(new FileNotFoundException($"Could not find file '{RuPath}'.", RuPath)));
            Assert.Equal("file or folder not found", ErrorText.For(new DirectoryNotFoundException("Could not find a part of the path 'C:\\x'.")));
            Assert.Equal("the file path is too long", ErrorText.For(new PathTooLongException("The path is too long.")));
            Assert.Equal("the file is in use by another program",
                ErrorText.For(new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020))));
            Assert.Equal("not enough space on the disk", ErrorText.For(new IOException("There is not enough space on the disk.", unchecked((int)0x80070070))));
            Assert.Equal("timed out", ErrorText.For(new TimeoutException("Timed out.")));
            Assert.Equal("not enough memory", ErrorText.For(new OutOfMemoryException()));
            Assert.Equal("the operation was canceled", ErrorText.For(new OperationCanceledException($"Copying '{RuPath}' was canceled.")));
            Assert.Equal("unknown error", ErrorText.For(null));
            Assert.Equal("access denied — administrator rights are needed, or Windows protects the item",
                ErrorText.For(new AggregateException(new UnauthorizedAccessException("Access denied"))));
        }

        Assert.StartsWith("нет доступа", ErrorText.For(new UnauthorizedAccessException("Access denied"))); // and Russian is back
        Assert.Equal("файл занят другой программой", ErrorText.For(new IOException("busy", unchecked((int)0x80070020))));
    }

    [Fact]
    public void ASentence_IsCapitalized_AndEndsWithAPeriod_InEnglish()
    {
        using (TestLanguage.English())
        {
            Assert.Equal("The file is in use by another program.", ErrorText.Sentence(new IOException("x", unchecked((int)0x80070020))));
            Assert.Equal("Unexpected error — details are in the log.", ErrorText.Sentence(new InvalidOperationException("Sequence contains no elements")));
        }

        Assert.Equal("Непредвиденная ошибка — подробности записаны в журнал.", ErrorText.Sentence(new InvalidOperationException("Sequence contains no elements")));
    }

    [Fact]
    public void Win32Codes_HaveEnglishPhrases_AndWindowsEnglishTextIsKeptOnlyWhenItIsEnglish()
    {
        using (TestLanguage.English())
        {
            Assert.StartsWith("access denied", ErrorText.For(new Win32Exception(5, "Access is denied")));
            Assert.Equal("the action was canceled by the user", ErrorText.For(new Win32Exception(1223, "The operation was canceled by the user")));
            Assert.Equal("administrator rights are needed", ErrorText.Win32Text(740, "x"));
            Assert.Equal("file or folder not found", ErrorText.Win32Text(2, "x"));
            // Windows' own English text for the code is understandable: kept (its first word lower-case, no final period)
            Assert.Equal("no service is operating at the destination network endpoint",
                ErrorText.Win32Text(1234, "No service is operating at the destination network endpoint."));
            // a Russian (or any non-Latin) Windows text is never shown in the English UI
            Assert.Equal("Windows error (code 1234)", ErrorText.Win32Text(1234, "Отсутствует служба, работающая с конечным сетевым адресом.\r\n"));
            Assert.Equal("Windows error (code 1234)", ErrorText.Win32Text(1234, "Der Dienst ist nicht verfügbar."));
            Assert.Equal("Windows error (code 1234)", ErrorText.Win32Text(1234, ""));
        }

        Assert.Equal("ошибка Windows (код 1234)", ErrorText.Win32Text(1234, "No service is operating at the destination network endpoint."));
        Assert.Equal("Отсутствует служба, работающая с конечным сетевым адресом", ErrorText.Win32Text(1234, "Отсутствует служба, работающая с конечным сетевым адресом.\r\n"));
    }

    [Fact]
    public void AnIoMessage_IsKept_WhenItIsEnglish_AndANonEnglishOneNever()
    {
        using (TestLanguage.English())
        {
            // .NET's English message of an I/O error; Cyrillic inside the quoted path is no reason to hide it
            Assert.Equal($"the file '{RuPath}' already exists",
                ErrorText.For(new IOException($"The file '{RuPath}' already exists.", unchecked((int)0x80070050))));
            Assert.Equal("Windows says: no", ErrorText.For(new IOException("Windows says: no")));
            // the Russian message of a Russian Windows, or of an app phrase nobody translated
            Assert.Equal("could not read or write the file", ErrorText.For(new IOException("Процесс не может получить доступ к файлу.")));
            Assert.Equal("could not read or write the file", ErrorText.For(new IOException("")));
            // an exception that is not about files or marked as the app's own gets the general phrase, never a developer's sentence
            Assert.Equal("unexpected error — details are in the log", ErrorText.For(new InvalidOperationException("Collection was modified.")));
            Assert.Equal("unexpected error — details are in the log", ErrorText.For(new InvalidOperationException("Не удалось удалить файл.")));
            // COM and other Windows errors: the code
            Assert.Equal("Windows error (code 0x80004005)", ErrorText.For(new COMException("Unspecified error", unchecked((int)0x80004005))));
            Assert.Equal("Windows error (code 0x80004005)", ErrorText.For(new COMException("Shell.Application недоступен", unchecked((int)0x80004005))));
        }

        // Russian UI: unchanged
        Assert.Equal("не удалось прочитать или записать файл", ErrorText.For(new IOException($"The file '{RuPath}' already exists.", unchecked((int)0x80070050))));
        Assert.Equal("Процесс не может получить доступ к файлу", ErrorText.For(new IOException("Процесс не может получить доступ к файлу.")));
    }

    [Fact]
    public void AMessageTheAppWroteItself_IsShownAsItIs_InBothLanguages()
    {
        var ru = ErrorText.For(ErrorText.Own(new InvalidOperationException(T._("Системные файлы Windows нельзя помещать в карантин."))));
        string en;
        using (TestLanguage.English())
            en = ErrorText.For(ErrorText.Own(new InvalidOperationException(T._("Системные файлы Windows нельзя помещать в карантин."))));

        Assert.Equal("Системные файлы Windows нельзя помещать в карантин", ru);
        Assert.Equal("Windows system files cannot be put in quarantine", en); // "Windows" keeps its capital
        using (TestLanguage.English())
        {
            Assert.Equal("the quarantine is broken", ErrorText.For(ErrorText.Own(new COMException("The quarantine is broken."))));
            Assert.Equal("Windows system files cannot be put in quarantine.",
                ErrorText.Sentence(ErrorText.Own(new InvalidOperationException(T._("Системные файлы Windows нельзя помещать в карантин.")))));
        }
    }

    [Fact]
    public void TheQuarantine_RefusesAWindowsFile_WithAnEnglishReason()
    {
        var windowsFile = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (!File.Exists(windowsFile))
            return;

        using (TestLanguage.English())
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Quarantine.Add(windowsFile, "test"));

            Assert.Equal("Windows system files cannot be put in quarantine.", ex.Message);
            Assert.Equal("Windows system files cannot be put in quarantine.", ErrorText.Sentence(ex));
        }
        var ru = Assert.Throws<InvalidOperationException>(() => Quarantine.Add(windowsFile, "test"));
        Assert.Equal("Системные файлы Windows нельзя помещать в карантин.", ru.Message);
    }

    // ----- the name of the module (the badge of an issue on Home, the section of the report) -----

    [Fact]
    public void TheModuleNames_AreEnglish_AndRussianIsUnchanged()
    {
        var names = new Func<string>[]
        {
            () => AppServices.Cleaner.ModuleName,
            () => AppServices.Processes.ModuleName,
            () => AppServices.Startup.ModuleName,
            () => AppServices.Programs.ModuleName,
            () => AppServices.Disk.ModuleName,
            () => AppServices.Optimizer.ModuleName,
            () => AppServices.Network.ModuleName,
            () => AppServices.Security.ModuleName,
        };
        var russian = names.Select(n => n()).ToList();
        List<string> english;
        string networkConstant;
        using (TestLanguage.English())
        {
            english = names.Select(n => n()).ToList();
            networkConstant = NetworkRules.ModuleName;
        }

        Assert.Equal(new[] { "Очистка", "Процессы", "Автозагрузка", "Программы", "Диски", "Ускорение", "Сеть", "Безопасность" }, russian);
        Assert.Equal(new[] { "Cleanup", "Processes", "Startup", "Programs", "Disks", "Speed up", "Network", "Security" }, english);
        Assert.Equal("Network", networkConstant);
        Assert.Equal("Сеть", NetworkRules.ModuleName);
    }

    [Fact]
    public void TheModuleNames_MatchTheTitlesOfTheirPages_InEnglish()
    {
        using (TestLanguage.English())
        {
            // the page of the module and its name on the Home badge say the same word
            Assert.Equal(new[] { "Cleanup", "Processes", "Startup", "Programs", "Disks", "Speed up", "Network", "Security" },
                new[]
                {
                    T._("Очистка"), T._("Процессы"), T._("Автозагрузка"), T._("Программы"), T._("Диски"), T._("Ускорение"), T._("Сеть"), T._("Безопасность"),
                });
            Assert.Equal(AppServices.Disk.ModuleName, T._("Диски"));
        }
    }

    [Fact]
    public void AnIssueOfAModule_KeepsTheBadgeText_OfTheLanguageItWasMadeIn()
    {
        HealthIssue issue;
        using (TestLanguage.English())
            issue = new HealthIssue { Module = AppServices.Disk.ModuleName, Title = "x" };

        Assert.Equal("Disks", issue.Module);
        Assert.Equal("Диски", new HealthIssue { Module = AppServices.Disk.ModuleName, Title = "x" }.Module);
    }

    [Fact]
    public void TheDefaultFixLabel_FollowsTheLanguage()
    {
        Assert.Equal("Исправить", HealthIssue.DefaultFixLabel);
        Assert.Equal("Исправить", new HealthIssue { Module = "m", Title = "t" }.FixLabel);
        using (TestLanguage.English())
        {
            Assert.Equal("Fix", HealthIssue.DefaultFixLabel);
            Assert.Equal("Fix", new HealthIssue { Module = "m", Title = "t" }.FixLabel);
        }
    }

    // ----- small texts -----

    [Fact]
    public void TheBulkBarText_FollowsTheLanguage()
    {
        Assert.Equal("Отмечено: 3", BulkActionBar.CheckedTextFor(3));
        using (TestLanguage.English())
            Assert.Equal("Checked: 3", BulkActionBar.CheckedTextFor(3));
    }

    [Fact]
    public void TheSignatureStatusTexts_AreEnglish()
    {
        using (TestLanguage.English())
        {
            Assert.Equal("Signed: Vendor Co", new SignatureInfo(SignatureStatus.Valid, "Vendor Co").StatusText);
            Assert.Equal("Unsigned", new SignatureInfo(SignatureStatus.Unsigned, null).StatusText);
            Assert.Equal("Untrusted signature", new SignatureInfo(SignatureStatus.Untrusted, null).StatusText);
            Assert.Equal("File not found", new SignatureInfo(SignatureStatus.NotFound, null).StatusText);
        }

        Assert.Equal("Подписан: Vendor Co", new SignatureInfo(SignatureStatus.Valid, "Vendor Co").StatusText);
    }

    [Fact]
    public void NoCoreTextOfTheseFiles_LeavesRussianBehind_InEnglish()
    {
        using (TestLanguage.English())
        {
            var texts = new[]
            {
                new SignatureInfo(SignatureStatus.Invalid, null).StatusText,
                new SignatureInfo(SignatureStatus.Error, null).StatusText,
                new SignatureInfo(SignatureStatus.Valid, "Vendor") { IsPackageSigned = true }.StatusText,
                ErrorText.For(new IOException("Процесс не может получить доступ к файлу.")),
                new SuspicionResult(70, Array.Empty<string>()).LevelText,
                HealthIssue.DefaultFixLabel,
            };

            Assert.All(texts, t => Assert.False(Cyrillic.IsMatch(t), t));
        }
    }
}

/// <summary>The restart results are English and still sorted into failure and success (the Home row decides from the text).</summary>
[Collection(LanguageSequentialCollection.Name)]
public sealed class RestartEnglishTests
{
    public RestartEnglishTests() => TestEnvironment.Init();

    [Fact]
    public async Task TheRestartResults_AreEnglish_AndStillSortedIntoFailureAndSuccess()
    {
        using var language = TestLanguage.English();

        string declined;
        using (new SystemRestartFake(answer: false))
            declined = await SystemRestart.RestartAsync(CancellationToken.None);
        string started;
        using (new SystemRestartFake(answer: true))
            started = await SystemRestart.RestartAsync(CancellationToken.None);
        string refused;
        using (new SystemRestartFake(answer: true, startSucceeds: false))
            refused = await SystemRestart.RestartAsync(CancellationToken.None);

        Assert.Equal("The restart was canceled — nothing was changed.", declined);
        Assert.Equal("The computer is restarting…", started);
        Assert.Equal("Could not restart the computer.", refused);
        Assert.True(HealthScoring.LooksLikeFailure(declined));
        Assert.False(HealthScoring.LooksLikeFailure(started));
        Assert.True(HealthScoring.LooksLikeFailure(refused));
    }

    [Fact]
    public async Task TheQuestion_IsEnglish()
    {
        using var language = TestLanguage.English();
        using var fake = new SystemRestartFake(answer: false);

        await SystemRestart.RestartAsync(CancellationToken.None);

        var (title, question) = Assert.Single(fake.Questions);
        Assert.Equal("Restart the computer", title);
        Assert.Equal("Save your work in all programs. The computer will restart in a few seconds. Continue?", question);
    }
}
