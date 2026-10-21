using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Programs;
using PcCleaner.Tests.Core;
using Xunit;

namespace PcCleaner.Tests.Programs;

/// <summary>
/// The Programs page, its catalog and its dashboard findings in English (the real en.json). Registry entries live in an HKCU
/// sandbox; the uninstaller, the Store app removal and the restore point are faked. Runs alone (the language is process-wide) and
/// always switches back to Russian; the Russian twin of the main texts is checked outside the English block.
/// </summary>
[Collection(LanguageSequentialCollection.Name)]
public sealed class ProgramsEnglishTests : IDisposable
{
    private const string SandboxRoot = @"Software\WaterCa7sPcCleanerTests\Programs";
    private const string BingNews = "Microsoft.BingNews_1.0.0.0_x64__8wekyb3d8bbwe";
    private const string WindowsStore = "Microsoft.WindowsStore_1.0.0.0_x64__8wekyb3d8bbwe";

    private static readonly Regex Cyrillic = new("[Ѐ-ӿ]");

    private readonly string _id = Guid.NewGuid().ToString("N")[..10];
    private readonly string _work;
    private readonly string _uninstallerPath;

    /// <summary>What the fakes were asked to do, in order ("restore:…", "uninstall:name:quiet|normal", "remove:full name").</summary>
    private readonly List<string> _calls = new();

    private readonly List<(string Title, string Message)> _questions = new();
    private readonly List<(string Title, string Message)> _infos = new();

    public ProgramsEnglishTests()
    {
        TestEnvironment.Init();
        _work = TestEnvironment.NewTempDir("progsen");
        _uninstallerPath = Path.Combine(_work, "App", "uninst.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_uninstallerPath)!);
        File.WriteAllText(_uninstallerPath, "fake - never executed");
    }

    private string MainPath => $@"{SandboxRoot}\{_id}\Main\Uninstall";

    private UninstallSource MainSource => new(RegistryHive.CurrentUser, RegistryView.Registry64, MainPath, ProgramScope.User);

    public void Dispose()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SandboxRoot, writable: true);
            root?.DeleteSubKeyTree(_id, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // best effort; the shared parent keys are removed at exit by TestEnvironment
        }
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void AddEntry(string keyName, Dictionary<string, string> values)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{MainPath}\{keyName}");
        foreach (var (name, value) in values)
            key.SetValue(name, value, RegistryValueKind.String);
    }

    /// <summary>An ordinary program with an uninstaller (and a silent command when asked).</summary>
    private void AddProgram(string displayName, bool quiet = false, string publisher = "Vendor")
    {
        var values = new Dictionary<string, string>
        {
            ["DisplayName"] = displayName,
            ["Publisher"] = publisher,
            ["InstallLocation"] = Path.GetDirectoryName(_uninstallerPath)!,
            ["UninstallString"] = $"\"{_uninstallerPath}\" --uninstall",
        };
        if (quiet)
            values["QuietUninstallString"] = $"\"{_uninstallerPath}\" --uninstall --silent";
        AddEntry(displayName.Replace(' ', '_'), values);
    }

    private void DeleteEntry(string displayName)
    {
        using var parent = Registry.CurrentUser.OpenSubKey(MainPath, writable: true);
        parent?.DeleteSubKeyTree(displayName.Replace(' ', '_'), throwOnMissingSubKey: false);
    }

    private ProgramsService CreateService(Func<InstalledProgram, bool, UninstallOutcome>? outcome = null,
        IReadOnlyList<StoreAppRecord>? store = null, Func<StoreApp, (bool Success, string Message)>? remove = null)
        => new(new[] { MainSource }, _ => Task.FromResult(store ?? Array.Empty<StoreAppRecord>()))
        {
            IsElevated = () => false,
            UninstallOverride = (p, quiet, _) =>
            {
                _calls.Add($"uninstall:{p.DisplayName}:{(quiet ? "quiet" : "normal")}");
                var result = outcome?.Invoke(p, quiet);
                if (result is null || result.Status == UninstallStatus.Removed)
                {
                    DeleteEntry(p.DisplayName);
                    result ??= new UninstallOutcome(UninstallStatus.Removed, "ok");
                }
                return Task.FromResult(result);
            },
            RemoveStoreAppOverride = (app, _) =>
            {
                _calls.Add($"remove:{app.PackageFullName}");
                return Task.FromResult(remove?.Invoke(app) ?? (true, "ok"));
            },
            CreateRestorePointAsync = (description, _) =>
            {
                _calls.Add($"restore:{description}");
                return Task.FromResult(true);
            },
        };

    private static StoreAppRecord Record(string fullName)
        => new(fullName[..fullName.IndexOf('_')], fullName, null, null, "1.0.0.0", null, "Store");

    private async Task<ProgramsViewModel> LoadAsync(ProgramsService service)
    {
        var vm = new ProgramsViewModel(service)
        {
            Confirm = (title, message) =>
            {
                _questions.Add((title, message));
                return true;
            },
            ShowInfo = (title, message) => _infos.Add((title, message)),
            ShowError = (title, message) => _infos.Add((title, message)),
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsLoaded);
        return vm;
    }

    private static async Task<HealthIssue> UnwantedIssueAsync(ProgramsService service, string name)
        => Assert.Single(await service.AnalyzeAsync(CancellationToken.None), i => i.Title == $"Unwanted program: {name}");

    private static void AssertFailureNotAdmin(string text)
    {
        Assert.True(HealthScoring.LooksLikeFailure(text), $"should look like a failure: {text}");
        Assert.False(HealthScoring.LooksLikeAdminRequired(text), $"must not ask for administrator rights: {text}");
    }

    // ================================================================ dashboard findings and fixes

    [Fact]
    public async Task AnUnwantedProgram_HasAnEnglishTitleLabelAndExplanation()
    {
        AddProgram("Amigo Browser", quiet: true, publisher: "Mail.Ru");
        var service = CreateService();

        using (TestLanguage.English())
        {
            var issue = await UnwantedIssueAsync(service, "Amigo Browser");

            Assert.Equal("Programs", issue.Module);
            Assert.Equal("Uninstall program", issue.FixLabel);
            Assert.Contains("If you did not install it on purpose, uninstall it.", issue.Details);
            Assert.EndsWith("Details about the program are on the Programs page.", issue.Details);
            Assert.Equal("Programs", service.ModuleName);
            Assert.Empty(_calls);
        }

        Assert.Equal("Программы", service.ModuleName);
        Assert.Equal("Нежелательная программа: Amigo Browser", Assert.Single(await service.AnalyzeAsync(CancellationToken.None), i => i.Module == "Программы").Title);
    }

    [Fact]
    public async Task TheUnwantedProgramFix_SaysInEnglishWhatHappened_AndFailuresAreRecognised()
    {
        AddProgram("Amigo Browser", quiet: true, publisher: "Mail.Ru");
        UninstallStatus status = UninstallStatus.Removed;
        var service = CreateService((p, _) => new UninstallOutcome(status, "message"));

        using var _ = TestLanguage.English();
        var issue = await UnwantedIssueAsync(service, "Amigo Browser");

        var removed = await issue.FixAsync!(CancellationToken.None);
        Assert.Equal("The program “Amigo Browser” was uninstalled.", removed);
        Assert.False(HealthScoring.LooksLikeFailure(removed));
        Assert.Equal(new[] { "restore:Uninstall Amigo Browser", "uninstall:Amigo Browser:quiet" }, _calls);

        AddProgram("Amigo Browser", quiet: true, publisher: "Mail.Ru");
        foreach (var (s, expected) in new[]
                 {
                     (UninstallStatus.StillInstalled, "Could not uninstall “Amigo Browser”: the program is still in the list of installed programs."),
                     (UninstallStatus.UacDeclined, "Could not uninstall “Amigo Browser”: you declined the Windows User Account Control prompt."),
                     (UninstallStatus.NotStarted, "Could not start the uninstall of “Amigo Browser”."),
                 })
        {
            status = s;
            var text = await (await UnwantedIssueAsync(service, "Amigo Browser")).FixAsync!(CancellationToken.None);

            Assert.Equal(expected, text);
            AssertFailureNotAdmin(text);
        }
    }

    [Fact]
    public async Task ABrokenEntriesFinding_AndItsFixResults_AreEnglish()
    {
        AddEntry("Gone_App", new()
        {
            ["DisplayName"] = "Gone App",
            ["Publisher"] = "Vendor",
            ["UninstallString"] = $"\"{Path.Combine(_work, "uninstall-gone.exe")}\"",
        });
        var service = CreateService();

        using var _ = TestLanguage.English();
        var issue = Assert.Single(await service.AnalyzeAsync(CancellationToken.None), i => i.Title.EndsWith(" in the program list", StringComparison.Ordinal));

        Assert.Equal("1 broken entry in the program list", issue.Title);
        Assert.Equal("A clean program list", issue.Gain);
        Assert.StartsWith("These programs are already uninstalled, but are still visible in the list of installed programs: “Gone App”.", issue.Details);
        Assert.DoesNotMatch(Cyrillic, issue.Title + issue.Details + issue.Gain);
        Assert.Equal("Nothing was deleted.", ProgramsService.BrokenEntriesFixText(0, 0, Array.Empty<string>()).TrimEnd());
        var partial = ProgramsService.BrokenEntriesFixText(3, 1, new[] { "A: failed" });
        Assert.Equal("Deleted 1 entry, backups saved. Could not delete: 1. Deleting was stopped — not processed: 1 entry.", partial);
        Assert.True(HealthScoring.LooksLikeFailure(partial));
        Assert.False(HealthScoring.LooksLikeFailure(ProgramsService.BrokenEntriesFixText(2, 2, Array.Empty<string>())));
    }

    [Fact]
    public async Task APreinstalledAppsFinding_AndItsFixResults_AreEnglish()
    {
        var store = new[] { Record(BingNews) };
        var service = CreateService(store: store);

        using (TestLanguage.English())
        {
            var issue = Assert.Single(await service.AnalyzeAsync(CancellationToken.None), i => i.Title.EndsWith("can be removed", StringComparison.Ordinal));

            Assert.Equal("1 preinstalled app can be removed", issue.Title);
            Assert.Equal("Remove extras", issue.FixLabel);
            Assert.StartsWith("They take up space and sometimes run in the background. The button will remove:\n• News", issue.Details);
            Assert.EndsWith("on the “Microsoft Store apps” tab.", issue.Details);

            var text = await issue.FixAsync!(CancellationToken.None);

            Assert.Equal("Apps removed: 1 of 1.", text);
            Assert.False(HealthScoring.LooksLikeFailure(text));
            Assert.Contains("restore:Remove preinstalled apps", _calls);
        }

        var russian = Assert.Single(await service.AnalyzeAsync(CancellationToken.None), i => i.FixLabel == "Удалить лишние");
        Assert.StartsWith("Они занимают место и иногда работают в фоне. Кнопка удалит:\n• Новости", russian.Details);
        var failing = CreateService(store: store, remove: _ => (false, "no"));
        using (TestLanguage.English())
        {
            var text = await (await failing.AnalyzeAsync(CancellationToken.None)).Single(i => i.FixLabel == "Remove extras").FixAsync!(CancellationToken.None);

            Assert.Equal("Apps removed: 0 of 1. Could not remove (1): News.", text);
            AssertFailureNotAdmin(text);
        }
    }

    // ================================================================ uninstall outcomes

    [Fact]
    public async Task TheUninstallOutcomes_AreEnglish()
    {
        AddProgram("Contoso Editor");
        AddEntry("Missing_Uninstaller", new()
        {
            ["DisplayName"] = "Missing Uninstaller",
            ["Publisher"] = "Vendor",
            ["InstallLocation"] = Path.GetDirectoryName(_uninstallerPath)!,
            ["UninstallString"] = $"\"{Path.Combine(_work, "App", "not-there.exe")}\" /x",
        });
        AddEntry("No_Uninstaller", new()
        {
            ["DisplayName"] = "No Uninstaller",
            ["Publisher"] = "Vendor",
            ["InstallLocation"] = Path.GetDirectoryName(_uninstallerPath)!,
        });
        var service = new ProgramsService(new[] { MainSource }, _ => Task.FromResult<IReadOnlyList<StoreAppRecord>>(Array.Empty<StoreAppRecord>()));
        var programs = (await service.ScanProgramsAsync(CancellationToken.None)).Programs;

        using var _ = TestLanguage.English();
        var missing = await service.RunUninstallerAsync(programs.Single(p => p.DisplayName == "Missing Uninstaller"), quiet: false, null, CancellationToken.None);
        var none = await service.UninstallQuietWhenPossibleAsync(programs.Single(p => p.DisplayName == "No Uninstaller"), CancellationToken.None);

        Assert.Equal(UninstallStatus.NotStarted, missing.Status);
        Assert.StartsWith("The uninstaller file was not found:\n", missing.Message);
        Assert.EndsWith("If that does not work, install the program again over the old one and then uninstall it.", missing.Message);
        Assert.Equal(UninstallStatus.NotStarted, none.Status);
        Assert.Equal("The program has no uninstaller specified.", none.Message);
        AssertFailureNotAdmin(missing.Message);
        Assert.DoesNotMatch(Cyrillic, missing.Message + none.Message);
    }

    // ================================================================ the page

    [Fact]
    public void TheBulkUninstallQuestion_AndTheSummary_AreEnglish() => UiThread.Run(async () =>
    {
        AddProgram("Contoso Editor", quiet: true);
        AddProgram("Fabrikam Viewer");
        var service = CreateService();

        using (TestLanguage.English())
        {
            var vm = await LoadAsync(service);
            Assert.Equal("Programs", vm.Title);
            Assert.Equal("Programs · 2", vm.ProgramsTabHeader);
            Assert.Equal(new[] { "All", "Unwanted", "Recent", "Large", "Broken" }, vm.Filters.Select(f => f.Title));
            Assert.Equal("All · 2", vm.Filters[0].Label);
            foreach (var name in new[] { "Contoso Editor", "Fabrikam Viewer" })
                vm.Programs.Single(p => p.DisplayName == name).IsChecked = true;

            await vm.UninstallCheckedCommand.ExecuteAsync(null);

            var (title, question) = Assert.Single(_questions);
            Assert.Equal("Uninstall the checked programs?", title);
            Assert.StartsWith("Uninstall 2 programs:", question);
            Assert.Contains("• Contoso Editor — silent", question);
            Assert.Contains("• Fabrikam Viewer", question);
            Assert.Contains("Programs that can uninstall silently will be uninstalled without windows;", question);
            Assert.EndsWith("Continue?", question);
            Assert.DoesNotMatch(Cyrillic, question);

            var (infoTitle, summary) = Assert.Single(_infos);
            Assert.Equal("Uninstalling checked programs", infoTitle);
            Assert.StartsWith("Uninstalled 2 of 2.", summary);
            Assert.Contains("Uninstalled (2):", summary);
            Assert.DoesNotMatch(Cyrillic, summary);
            Assert.Equal(new[] { "restore:Uninstall checked programs (2)", "uninstall:Contoso Editor:quiet", "uninstall:Fabrikam Viewer:normal" }, _calls);
        }
    });

    [Fact]
    public void TheStoreAppsSummary_SaysUninstalled_AndNamesWhatCouldNotBeUninstalled() => UiThread.Run(async () =>
    {
        const string bingWeather = "Microsoft.BingWeather_1.0.0.0_x64__8wekyb3d8bbwe";
        var service = CreateService(store: new[] { Record(BingNews), Record(bingWeather) },
            remove: app => app.PackageFullName == BingNews ? (false, "Windows refused.") : (true, "ok"));

        using var _ = TestLanguage.English();
        var vm = await LoadAsync(service);
        foreach (var app in vm.StoreApps)
            app.IsChecked = true;

        await vm.RemoveCheckedStoreAppsCommand.ExecuteAsync(null);

        var (title, summary) = Assert.Single(_infos);
        Assert.Equal("Uninstalling checked apps", title);
        Assert.StartsWith("Uninstalled 1 app out of 2.", summary);
        Assert.Contains("Could not uninstall (1):", summary);
        Assert.Contains("• News — Windows refused.", summary);
        Assert.DoesNotMatch(Cyrillic, summary);
    });

    [Fact]
    public void AProgramThatCouldNotBeStarted_IsNamedInTheEnglishSummary() => UiThread.Run(async () =>
    {
        AddProgram("Fabrikam Viewer");
        var service = CreateService((_, _) => new UninstallOutcome(UninstallStatus.NotStarted, "The uninstaller is locked.\n\nSecond paragraph."));

        using var _ = TestLanguage.English();
        var vm = await LoadAsync(service);
        vm.Programs.Single().IsChecked = true;

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        var (title, summary) = Assert.Single(_infos);
        Assert.Equal("Uninstalling checked programs", title);
        Assert.Contains("Could not start the uninstall (1):", summary);
        Assert.Contains("• Fabrikam Viewer — The uninstaller is locked.", summary);
        Assert.DoesNotContain("Second paragraph", summary);
    });

    [Fact]
    public void ThePageTextsOfAProgram_AreEnglish_AndTheRussianOnesAreUnchanged()
    {
        var program = new InstalledProgram
        {
            DisplayName = "Old App",
            KeyName = "Old_App",
            Source = MainSource,
            Version = "1.0",
            IsBroken = true,
        };
        program.RefreshBadges();

        using (TestLanguage.English())
        {
            program.RefreshBadges();

            Assert.Equal("Not specified", program.PublisherText);
            Assert.Equal("Current user only", program.ScopeText);
            Assert.Equal("No uninstaller specified", program.InstallerTypeText);
            Assert.Equal("Unknown", program.DateDetailsText);
            Assert.Equal("Not specified", program.InstallLocationText);
            Assert.Equal("Broken entry", program.PrimaryBadge!.Text);
            Assert.Contains("Only the line in the list is left.", program.PrimaryBadge.ToolTip);
            Assert.Equal("The uninstaller was not found — remove the entry with the “Remove entry from the list” button", program.CheckTooltip);

            Assert.Equal("“Old App” was removed from the trusted list: the checks are on again.", ProgramsViewModel.BuildTrustMessage("Old App", Array.Empty<string>(), trusted: false));
            Assert.Equal("“Old App” was marked as trusted: it will no longer be marked as unwanted. You can remove it from the trusted list here or in “Settings”.",
                ProgramsViewModel.BuildTrustMessage("Old App", Array.Empty<string>(), trusted: true));
            Assert.Contains("and its file old.exe will no longer be marked as suspicious on the “Processes” and “Startup” pages.",
                ProgramsViewModel.BuildTrustMessage("Old App", new[] { @"C:\Old\old.exe" }, trusted: true));
        }

        program.RefreshBadges();
        Assert.Equal("Не указан", program.PublisherText);
        Assert.Equal("Битая запись", program.PrimaryBadge!.Text);
        Assert.Equal("«Old App» убрана из доверенных: проверки снова включены.", ProgramsViewModel.BuildTrustMessage("Old App", Array.Empty<string>(), trusted: false));
        Assert.Contains("а её файл old.exe — как подозрительный в разделах «Процессы» и «Автозагрузка».",
            ProgramsViewModel.BuildTrustMessage("Old App", new[] { @"C:\Old\old.exe" }, trusted: true));
    }

    [Fact]
    public void TheEmptyStates_OfBothTabs_AreEnglish() => UiThread.Run(async () =>
    {
        var service = CreateService();

        using var _ = TestLanguage.English();
        var vm = new ProgramsViewModel(service);

        Assert.Equal("Loading the list of programs…", vm.ProgramsEmptyTitle);
        Assert.Equal("This will take a few seconds.", vm.ProgramsEmptyText);
        Assert.Equal("Loading apps…", vm.StoreEmptyTitle);
        Assert.Equal("Microsoft Store apps", vm.StoreTabHeader);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("No programs found", vm.ProgramsEmptyTitle);
        Assert.Equal("Could not read the list of installed programs from the registry.", vm.ProgramsEmptyText);
        Assert.Equal("No Microsoft Store apps found", vm.StoreEmptyTitle);
        Assert.Equal("calculating the size…", vm.TotalSizeText);

        AddProgram("Contoso Editor");
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SearchText = " zzz ";
        Assert.Equal("Nothing found for “zzz”", vm.ProgramsEmptyTitle);
        Assert.Equal("Check the spelling or choose the “All” filter.", vm.ProgramsEmptyText);
        vm.SearchText = "";
        vm.ShowFilterCommand.Execute(ProgramFilter.Unwanted);
        Assert.Equal("No unwanted programs found", vm.ProgramsEmptyTitle);
        Assert.Equal("No adware, “optimizers” or bundled browsers found. Great!", vm.ProgramsEmptyText);
        Assert.Equal("none found — great", vm.PupHint);
    });

    // ================================================================ the Store catalog

    [Fact]
    public void AStoreApp_HasAnEnglishNameDescriptionAndBadge_AndTheRussianOnesAreUnchanged()
    {
        Assert.Equal("Новости", ProgramsService.CreateStoreApp(Record(BingNews)).DisplayName);

        using (TestLanguage.English())
        {
            var news = ProgramsService.CreateStoreApp(Record(BingNews));
            var store = ProgramsService.CreateStoreApp(Record(WindowsStore));

            Assert.Equal("News", news.DisplayName);
            Assert.Equal("The Microsoft Start news feed.", news.Description);
            Assert.Equal(StoreAppKind.Bloatware, news.Kind);
            Assert.Equal("Preinstalled, can be removed", news.Badge!.Text);
            Assert.Equal("Can be removed", news.Badge.ShortText);
            Assert.Equal("Microsoft Store", store.DisplayName);
            Assert.Equal("Needed to install and update apps from the Microsoft Store.", store.ProtectReason);
            Assert.Equal("Do not remove", store.Badge!.ShortText);
            Assert.Equal(store.ProtectReason, store.CheckTooltip);
            Assert.Equal("You can get the app back at any time — install it again from the Microsoft Store.", news.UndoText);
            Assert.Equal("Microsoft Store", news.SourceText);
        }

        var russian = ProgramsService.CreateStoreApp(Record(BingNews));
        Assert.Equal("Новости", russian.DisplayName);
        Assert.Equal("Лента новостей Microsoft Start.", russian.Description);
        Assert.Equal("Предустановленная, можно удалить", russian.Badge!.Text);
    }

    [Fact]
    public void NoTextOfTheStoreCatalog_StaysRussian_InEnglish()
    {
        using var _ = TestLanguage.English();
        var entries = (Array)typeof(StoreAppCatalog).GetProperty("Entries", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.True(entries.Length > 100);
        foreach (var entry in entries)
        {
            var type = entry.GetType();
            foreach (var property in new[] { "FriendlyName", "Description", "ProtectReason" })
            {
                var text = (string?)type.GetProperty(property)!.GetValue(entry);
                Assert.True(text is null || !Cyrillic.IsMatch(text), $"{type.GetProperty("Pattern")!.GetValue(entry)} {property}: {text}");
            }
        }
        Assert.Equal("Language pack ru-RU", ProgramsService.CreateStoreApp(Record("Microsoft.LanguageExperiencePackru-RU_1.0.0.0_neutral__8wekyb3d8bbwe")).DisplayName);
    }
}
