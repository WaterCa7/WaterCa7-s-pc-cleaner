using System.IO;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Programs;
using Xunit;

namespace PcCleaner.Tests.Programs;

/// <summary>
/// Dashboard fixes of the Programs module: "Нежелательная программа" (uninstall) and "предустановленных приложений можно
/// удалить" (remove Store apps). Registry entries live in an HKCU sandbox; the uninstaller, the Store app removal and the
/// restore point are faked, so nothing real is ever started, removed or created.
/// </summary>
[Collection("ProgramsRegistrySandbox")]
public sealed class ProgramsDashboardFixTests : IDisposable
{
    private const string SandboxRoot = @"Software\WaterCa7sPcCleanerTests\Programs";
    private const string BingNews = "Microsoft.BingNews_1.0.0.0_x64__8wekyb3d8bbwe";
    private const string CandyCrush = "king.com.CandyCrushSaga_1.0.0.0_x86__kgqvnymyfvs32";
    private const string BingWeather = "Microsoft.BingWeather_1.0.0.0_x64__8wekyb3d8bbwe";
    private const string WindowsStore = "Microsoft.WindowsStore_1.0.0.0_x64__8wekyb3d8bbwe";

    private readonly string _id = Guid.NewGuid().ToString("N")[..10];
    private readonly string _work;
    private readonly string _uninstallerPath;
    private readonly List<string> _trustedToRemove = new();

    /// <summary>Everything the fakes were asked to do, in order ("restore:…", "uninstall:name:quiet|normal", "remove:name").</summary>
    private readonly List<string> _calls = new();

    public ProgramsDashboardFixTests()
    {
        TestEnvironment.Init();
        _work = TestEnvironment.NewTempDir("progsfix");
        _uninstallerPath = Path.Combine(_work, "App", "uninst.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_uninstallerPath)!);
        File.WriteAllText(_uninstallerPath, "fake - never executed");
    }

    private string MainPath => $@"{SandboxRoot}\{_id}\Main\Uninstall";

    private UninstallSource MainSource => new(RegistryHive.CurrentUser, RegistryView.Registry64, MainPath, ProgramScope.User);

    public void Dispose()
    {
        foreach (var name in _trustedToRemove)
            AppServices.Settings.RemoveTrusted(name);
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

    private void AddEntry(string keyName, Dictionary<string, object> values)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{MainPath}\{keyName}");
        foreach (var (name, value) in values)
            key.SetValue(name, value.ToString()!, RegistryValueKind.String);
    }

    /// <summary>A known unwanted program ("Амиго") with an ordinary uninstaller and no quiet command.</summary>
    private void AddAmigo()
        => AddEntry("Amigo", new()
        {
            ["DisplayName"] = "Амиго",
            ["Publisher"] = "Mail.Ru",
            ["InstallLocation"] = Path.GetDirectoryName(_uninstallerPath)!,
            ["UninstallString"] = $"\"{_uninstallerPath}\" --uninstall",
        });

    /// <summary>An unwanted program whose installer left a QuietUninstallString.</summary>
    private void AddAmigoWithQuietCommand(string displayName = "Amigo Browser")
        => AddEntry("AmigoQuiet", new()
        {
            ["DisplayName"] = displayName,
            ["Publisher"] = "Mail.Ru",
            ["InstallLocation"] = Path.GetDirectoryName(_uninstallerPath)!,
            ["UninstallString"] = $"\"{_uninstallerPath}\" --uninstall",
            ["QuietUninstallString"] = $"\"{_uninstallerPath}\" --uninstall --silent",
        });

    private ProgramsService CreateService(Func<IReadOnlyList<StoreAppRecord>>? store = null,
        UninstallOutcome? outcome = null, Func<StoreApp, (bool Success, string Message)>? remove = null)
    {
        var service = new ProgramsService(new[] { MainSource },
            _ => Task.FromResult(store?.Invoke() ?? Array.Empty<StoreAppRecord>()))
        {
            IsElevated = () => false,
            UninstallOverride = (p, quiet, _) =>
            {
                _calls.Add($"uninstall:{p.DisplayName}:{(quiet ? "quiet" : "normal")}");
                return Task.FromResult(outcome ?? new UninstallOutcome(UninstallStatus.Removed, "ok"));
            },
            RemoveStoreAppOverride = (app, _) =>
            {
                _calls.Add($"remove:{app.PackageFullName}");
                return Task.FromResult(remove?.Invoke(app) ?? (true, "Удалено."));
            },
            CreateRestorePointAsync = (description, _) =>
            {
                _calls.Add($"restore:{description}");
                return Task.FromResult(true);
            },
        };
        return service;
    }

    private static StoreAppRecord Record(string fullName, string signature = "Store")
    {
        var name = fullName[..fullName.IndexOf('_')];
        return new StoreAppRecord(name, fullName, null, null, "1.0.0.0", null, signature);
    }

    /// <summary>The preinstalled apps of the catalog, as installed packages.</summary>
    private static readonly string[] ManyBloat =
    {
        "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingFinance", "Microsoft.BingSports", "Microsoft.BingTravel",
        "Microsoft.BingFoodAndDrink", "Microsoft.BingHealthAndFitness", "Microsoft.BingSearch", "Microsoft.News",
        "Microsoft.GetHelp", "Microsoft.Getstarted", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.People",
        "Microsoft.WindowsFeedbackHub", "Microsoft.ZuneMusic", "Microsoft.ZuneVideo", "Microsoft.MixedReality.Portal",
    };

    private static string FullName(string name) => $"{name}_1.0.0.0_x64__8wekyb3d8bbwe";

    /// <summary>Removes made, in order (the restore point is not one of them).</summary>
    private List<string> Removes() => _calls.Where(c => c.StartsWith("remove:", StringComparison.Ordinal)).ToList();

    private static string NameOf(string fullName) => ProgramsService.CreateStoreApp(Record(fullName)).DisplayName;

    private static IReadOnlyList<StoreAppRecord> Records(params string[] fullNames) => fullNames.Select(n => Record(n)).ToList();

    private static async Task<HealthIssue> UnwantedIssueAsync(ProgramsService service, string name = "Амиго")
    {
        var issues = await service.AnalyzeAsync(CancellationToken.None);
        return Assert.Single(issues, i => i.Title == $"Нежелательная программа: {name}");
    }

    private static async Task<HealthIssue> BloatIssueAsync(ProgramsService service)
    {
        var issues = await service.AnalyzeAsync(CancellationToken.None);
        return Assert.Single(issues, i => i.Title.Contains("предустановленн"));
    }

    private static void AssertFailureNotAdmin(string text)
    {
        Assert.True(HealthScoring.LooksLikeFailure(text), $"should look like a failure: {text}");
        Assert.False(HealthScoring.LooksLikeAdminRequired(text), $"must not ask for administrator rights: {text}");
    }

    private static void AssertSuccess(string text)
    {
        Assert.False(HealthScoring.LooksLikeFailure(text), $"should not look like a failure: {text}");
        Assert.False(HealthScoring.LooksLikeAdminRequired(text));
    }

    // ================================================================ unwanted program

    [Fact]
    public async Task UnwantedProgram_HasFixWithOwnLabel_AndIsNotRunByFixAll()
    {
        AddAmigo();
        var service = CreateService();

        var issue = await UnwantedIssueAsync(service);

        Assert.NotNull(issue.FixAsync);
        Assert.True(issue.CanFix);
        Assert.Equal("Удалить программу", issue.FixLabel);
        Assert.False(issue.InFixAll);
        Assert.False(issue.AutoFixSafe);
        Assert.Empty(_calls); // the analysis changes nothing
    }

    [Fact]
    public async Task UnwantedProgram_WithoutQuietCommand_RunsNormalUninstallAfterRestorePoint()
    {
        AddAmigo();
        var service = CreateService();
        var issue = await UnwantedIssueAsync(service);

        await issue.FixAsync!(CancellationToken.None);

        Assert.Equal(new[] { "restore:Удаление Амиго", "uninstall:Амиго:normal" }, _calls);
    }

    [Fact]
    public async Task UnwantedProgram_WithQuietCommand_RunsQuietUninstall()
    {
        AddAmigoWithQuietCommand();
        var service = CreateService();
        var issue = await UnwantedIssueAsync(service, "Amigo Browser");

        await issue.FixAsync!(CancellationToken.None);

        Assert.Equal(new[] { "restore:Удаление Amigo Browser", "uninstall:Amigo Browser:quiet" }, _calls);
    }

    [Theory]
    [InlineData(UninstallStatus.Removed, "Программа «Амиго» удалена.", false)]
    [InlineData(UninstallStatus.StillInstalled, "Не удалось удалить «Амиго»: программа осталась в списке установленных.", true)]
    [InlineData(UninstallStatus.UacDeclined,
        "Не удалось удалить «Амиго»: вы отклонили запрос контроля учётных записей Windows.", true)]
    [InlineData(UninstallStatus.NotStarted, "Не удалось запустить удаление «Амиго».", true)]
    public async Task UnwantedProgram_OutcomeText_AndClassification(UninstallStatus status, string expected, bool failure)
    {
        AddAmigo();
        var service = CreateService(outcome: new UninstallOutcome(status, "message of the runner - never shown"));
        var issue = await UnwantedIssueAsync(service);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal(expected, text);
        if (failure)
            AssertFailureNotAdmin(text);
        else
            AssertSuccess(text);
    }

    [Fact]
    public async Task UnwantedProgram_GoneSinceTheScan_StartsNothing()
    {
        AddAmigo();
        var service = CreateService();
        var issue = await UnwantedIssueAsync(service);
        using (var parent = Registry.CurrentUser.OpenSubKey(MainPath, writable: true))
            parent!.DeleteSubKeyTree("Amigo");

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("Программа уже удалена — делать ничего не нужно.", text);
        AssertSuccess(text); // gone is what the fix wanted: the row is marked fixed
        Assert.Empty(_calls); // no restore point and no uninstaller either
    }

    [Fact]
    public async Task UnwantedProgram_WithoutAWorkingUninstaller_HasNoFixButton()
    {
        // Its uninstaller and install folder are gone: the entry is "broken", a "Удалить программу" button could never succeed.
        AddEntry("AmigoGone", new()
        {
            ["DisplayName"] = "Амиго",
            ["Publisher"] = "Mail.Ru",
            ["UninstallString"] = $"\"{Path.Combine(_work, "Gone", "uninstall.exe")}\"",
        });
        var service = CreateService();

        var issue = await UnwantedIssueAsync(service);

        Assert.Null(issue.FixAsync);
        Assert.False(issue.CanFix);
        Assert.Contains("Деинсталлятор программы не найден", issue.Details);
        Assert.Contains("Программы", issue.Details);
        Assert.Equal("Programs", issue.TargetPageKey); // the row stays and opens the page
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task UnwantedProgram_BrokenSinceTheScan_StartsNothingAndLogsTheReason()
    {
        AddAmigo();
        var service = CreateService();
        var issue = await UnwantedIssueAsync(service);
        Assert.NotNull(issue.FixAsync);
        Directory.Delete(Path.GetDirectoryName(_uninstallerPath)!, recursive: true); // uninstaller and install folder vanish

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("Не удалось запустить удаление «Амиго».", text);
        AssertFailureNotAdmin(text);
        Assert.Empty(_calls); // no restore point for nothing
        Assert.Contains(Log.Entries.ToList(),
            e => e.Level == LogLevel.Warning && e.Message.Contains("«Амиго»") && e.Message.Contains("Деинсталлятор этой программы не найден"));
    }

    [Fact]
    public async Task UnwantedProgram_NotStarted_LogsTheRunnersReason()
    {
        AddAmigo();
        var reason = "причина-" + _id;
        var service = CreateService(outcome: new UninstallOutcome(UninstallStatus.NotStarted, reason));
        var issue = await UnwantedIssueAsync(service);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("Не удалось запустить удаление «Амиго».", text);
        Assert.Contains(Log.Entries.ToList(), e => e.Level == LogLevel.Warning && e.Message.Contains("«Амиго»") && e.Message.Contains(reason));
    }

    [Fact]
    public async Task UnwantedProgram_TrustedSinceTheScan_ChangesNothing()
    {
        const string name = "Amigo Trust Test";
        AddAmigoWithQuietCommand(name);
        var service = CreateService();
        var issue = await UnwantedIssueAsync(service, name);
        AppServices.Settings.AddTrusted(name);
        _trustedToRemove.Add(name);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal($"«{name}» больше не считается нежелательной (возможно, вы добавили её в доверенные) — делать ничего не нужно.",
            text);
        AssertSuccess(text);
        Assert.Empty(_calls);
    }

    private const string WaitStoppedText =
        "Ожидание остановлено: удаление «Амиго» могло продолжиться в окне удаления программы.";

    [Fact]
    public async Task UnwantedProgram_CancelledWhileWaitingForTheUninstaller_SaysItMayStillRun()
    {
        AddAmigo();
        using var cts = new CancellationTokenSource();
        var service = CreateService();
        service.UninstallOverride = (p, quiet, _) =>
        {
            _calls.Add($"uninstall:{p.DisplayName}:{(quiet ? "quiet" : "normal")}");
            cts.Cancel(); // "Отмена" while waiting: RunUninstallerAsync stops waiting, the uninstaller keeps running
            return Task.FromCanceled<UninstallOutcome>(cts.Token);
        };
        var issue = await UnwantedIssueAsync(service);

        var text = await issue.FixAsync!(cts.Token);

        Assert.Equal(WaitStoppedText, text);
        Assert.Equal(new[] { "restore:Удаление Амиго", "uninstall:Амиго:normal" }, _calls);
    }

    [Fact]
    public async Task UnwantedProgram_QuietUninstallCancelledWhileWaiting_MentionsNoWindow()
    {
        AddAmigoWithQuietCommand();
        using var cts = new CancellationTokenSource();
        var service = CreateService();
        service.UninstallOverride = (_, _, _) =>
        {
            cts.Cancel();
            return Task.FromCanceled<UninstallOutcome>(cts.Token);
        };
        var issue = await UnwantedIssueAsync(service, "Amigo Browser");

        var text = await issue.FixAsync!(cts.Token);

        Assert.Equal("Ожидание остановлено: удаление «Amigo Browser» могло продолжиться в фоне, без окна.", text);
        // The Dashboard adds its own «Проверить снова» note to a stopped fix: the text does not repeat it.
        Assert.DoesNotContain("Проверить снова", text);
    }

    [Fact]
    public async Task UnwantedProgram_CancelledBeforeTheUninstallerStarts_StillCancels()
    {
        AddAmigo();
        using var cts = new CancellationTokenSource();
        var service = CreateService();
        service.CreateRestorePointAsync = (description, _) =>
        {
            _calls.Add($"restore:{description}");
            cts.Cancel(); // "Отмена" while the restore point is being made
            return Task.FromResult(true);
        };
        var issue = await UnwantedIssueAsync(service);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => issue.FixAsync!(cts.Token));

        Assert.Equal(new[] { "restore:Удаление Амиго" }, _calls); // nothing was started
    }

    [Fact]
    public async Task UnwantedProgram_CancelledWhileWaiting_DashboardKeepsTheTextAndDoesNotMarkItFixed()
    {
        AddAmigo();
        using var cts = new CancellationTokenSource();
        var service = CreateService();
        service.UninstallOverride = (_, _, _) =>
        {
            cts.Cancel();
            return Task.FromCanceled<UninstallOutcome>(cts.Token);
        };
        var vm = new DashboardViewModel(new IHealthContributor[] { service }, new AppSettings())
        {
            CreateRestorePointAsync = (_, _) => Task.FromResult(false),
            ReadStaticInfo = () => new StaticSystemInfo("Windows 11 Pro", "24H2", "TEST-PC", "Test CPU", 8, 16UL << 30),
        };
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var item = vm.Groups.Single().Items.Single();
        Assert.Equal("Удалить программу", item.Issue.FixLabel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.FixOneCoreAsync(item, cts.Token));

        Assert.StartsWith(WaitStoppedText, item.Issue.FixResult);
        Assert.Contains(DashboardViewModel.InterruptedNote, item.Issue.FixResult);
        Assert.False(item.Issue.IsFixed);
        Assert.True(item.Issue.CanFix);
        Assert.True(item.FixFailed);
    }

    // ---------------------------------------------------------------- quiet when possible (reused by the bulk uninstall)

    [Fact]
    public void ShouldUninstallQuietly_OnlyWhenQuietCommandExistsAndNotARuntime()
    {
        UninstallEntryValues Values(string name, string publisher, string? quiet) => new()
        {
            KeyName = "K",
            DisplayName = name,
            Publisher = publisher,
            InstallLocation = _work,
            UninstallString = $"\"{_uninstallerPath}\"",
            QuietUninstallString = quiet,
        };
        var quietCommand = $"\"{_uninstallerPath}\" /S";
        const string runtimeName = "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135";

        var withQuiet = ProgramsService.CreateProgram(Values("Some App", "Vendor", quietCommand), MainSource, DateTime.Now);
        var withoutQuiet = ProgramsService.CreateProgram(Values("Some App", "Vendor", null), MainSource, DateTime.Now);
        var runtimeWithQuiet = ProgramsService.CreateProgram(Values(runtimeName, "Microsoft Corporation", quietCommand), MainSource, DateTime.Now);

        Assert.NotNull(withQuiet.QuietLaunch);
        Assert.True(runtimeWithQuiet.IsRuntime);
        Assert.NotNull(runtimeWithQuiet.QuietLaunch);

        Assert.True(ProgramsService.ShouldUninstallQuietly(withQuiet));
        Assert.False(ProgramsService.ShouldUninstallQuietly(withoutQuiet));
        Assert.False(ProgramsService.ShouldUninstallQuietly(runtimeWithQuiet));
    }

    [Fact]
    public async Task UninstallQuietWhenPossible_PassesTheDecisionToTheRunner()
    {
        var service = CreateService();
        var runtime = ProgramsService.CreateProgram(new UninstallEntryValues
        {
            KeyName = "Rt",
            DisplayName = "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135",
            Publisher = "Microsoft Corporation",
            InstallLocation = _work,
            UninstallString = $"\"{_uninstallerPath}\"",
            QuietUninstallString = $"\"{_uninstallerPath}\" /S",
        }, MainSource, DateTime.Now);
        var plain = ProgramsService.CreateProgram(new UninstallEntryValues
        {
            KeyName = "Plain",
            DisplayName = "Plain App",
            InstallLocation = _work,
            UninstallString = $"\"{_uninstallerPath}\"",
            QuietUninstallString = $"\"{_uninstallerPath}\" /S",
        }, MainSource, DateTime.Now);

        await service.UninstallQuietWhenPossibleAsync(runtime, CancellationToken.None);
        var outcome = await service.UninstallQuietWhenPossibleAsync(plain, CancellationToken.None);

        Assert.Equal(new[] { $"uninstall:{runtime.DisplayName}:normal", "uninstall:Plain App:quiet" }, _calls);
        Assert.Equal(UninstallStatus.Removed, outcome.Status);
    }

    [Fact]
    public async Task UninstallQuietWhenPossible_WithoutOverride_UsesTheQuietLaunchOfTheProgram()
    {
        // No override: the real runner is used, but both uninstallers are missing, so it starts nothing and its message
        // names the file it would have started - which tells quiet from normal.
        var service = new ProgramsService(new[] { MainSource });
        var normalExe = Path.Combine(_work, "missing", "normal.exe");
        var quietExe = Path.Combine(_work, "missing", "quiet.exe");
        UninstallEntryValues Values(bool withQuiet) => new()
        {
            KeyName = withQuiet ? "WithQuiet" : "NoQuiet",
            DisplayName = "Missing Uninstaller",
            InstallLocation = _work, // folder exists, so the entry is not "broken" and Launch is built
            UninstallString = $"\"{normalExe}\"",
            QuietUninstallString = withQuiet ? $"\"{quietExe}\" /S" : null,
        };

        var quiet = await service.UninstallQuietWhenPossibleAsync(
            ProgramsService.CreateProgram(Values(true), MainSource, DateTime.Now), CancellationToken.None);
        var normal = await service.UninstallQuietWhenPossibleAsync(
            ProgramsService.CreateProgram(Values(false), MainSource, DateTime.Now), CancellationToken.None);

        Assert.Equal(UninstallStatus.NotStarted, quiet.Status);
        Assert.Contains(quietExe, quiet.Message);
        Assert.Equal(UninstallStatus.NotStarted, normal.Status);
        Assert.Contains(normalExe, normal.Message);
    }

    [Fact]
    public async Task UninstallQuietWhenPossible_ProgramThatCannotBeUninstalled_StartsNothing()
    {
        var service = CreateService();
        var gone = Path.Combine(_work, "Gone", "uninstall.exe");
        // Uninstaller and install folder are gone (broken entry), although the installer left a quiet command.
        var broken = ProgramsService.CreateProgram(new UninstallEntryValues
        {
            KeyName = "Broken",
            DisplayName = "Gone App",
            UninstallString = $"\"{gone}\"",
            QuietUninstallString = $"\"{gone}\" /S",
        }, MainSource, DateTime.Now);
        var noUninstaller = ProgramsService.CreateProgram(
            new UninstallEntryValues { KeyName = "None", DisplayName = "No Uninstaller", InstallLocation = _work }, MainSource, DateTime.Now);
        Assert.False(broken.CanUninstall);
        Assert.NotNull(broken.QuietLaunch);
        Assert.False(noUninstaller.CanUninstall);

        var first = await service.UninstallQuietWhenPossibleAsync(broken, CancellationToken.None);
        var second = await service.UninstallQuietWhenPossibleAsync(noUninstaller, CancellationToken.None);

        Assert.Equal(UninstallStatus.NotStarted, first.Status);
        Assert.Contains("не найден", first.Message);
        Assert.Equal(UninstallStatus.NotStarted, second.Status);
        Assert.Contains("не указан деинсталлятор", second.Message);
        Assert.Empty(_calls); // the runner (here the fake) was never reached
    }

    // ================================================================ preinstalled Store apps

    [Fact]
    public async Task Bloat_HasFixWithOwnLabel_AndIsNotRunByFixAll()
    {
        var service = CreateService(() => Records(BingNews, CandyCrush, WindowsStore));

        var issue = await BloatIssueAsync(service);

        Assert.Equal("2 предустановленных приложения можно удалить", issue.Title);
        Assert.NotNull(issue.FixAsync);
        Assert.True(issue.CanFix);
        Assert.Equal("Удалить лишние", issue.FixLabel);
        Assert.False(issue.InFixAll);
        Assert.Empty(_calls); // the analysis changes nothing
    }

    [Fact]
    public async Task Bloat_Fix_RemovesOnlyRemovableBloatApps()
    {
        var service = CreateService(() => Records(BingNews, CandyCrush, WindowsStore));
        var issue = await BloatIssueAsync(service);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("Удалено приложений: 2 из 2.", text);
        AssertSuccess(text);
        Assert.Equal(new[] { $"remove:{BingNews}", $"remove:{CandyCrush}" }.OrderBy(c => c),
            Removes().OrderBy(c => c)); // the protected Microsoft Store app is never passed on
    }

    [Fact]
    public async Task Bloat_Fix_CreatesOneRestorePointBeforeRemoving_LikeThePage()
    {
        var service = CreateService(() => Records(BingNews, CandyCrush));
        var issue = await BloatIssueAsync(service);

        await issue.FixAsync!(CancellationToken.None);

        Assert.Equal(3, _calls.Count);
        Assert.Equal("restore:Удаление предустановленных приложений", _calls[0]);
        Assert.Equal(2, Removes().Count);
    }

    [Fact]
    public async Task Bloat_Fix_CancelledWhileTheRestorePointIsMade_RemovesNothing()
    {
        using var cts = new CancellationTokenSource();
        var service = CreateService(() => Records(BingNews, CandyCrush));
        service.CreateRestorePointAsync = (description, _) =>
        {
            _calls.Add($"restore:{description}");
            cts.Cancel();
            return Task.FromResult(true);
        };
        var issue = await BloatIssueAsync(service);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => issue.FixAsync!(cts.Token));

        Assert.Empty(Removes());
    }

    [Fact]
    public async Task Bloat_Question_NamesUpTo15Apps_AndSaysHowTheyComeBack()
    {
        var records = ManyBloat.Select(n => Record(FullName(n))).ToList();
        records[^1] = Record(FullName(ManyBloat[^1]), signature: "Developer"); // installed by another program, not from the Store
        var service = CreateService(() => records);
        var issue = await BloatIssueAsync(service);
        var question = DashboardViewModel.FixConfirmText(new IssueItem(issue));

        // The text of the Programs page's «Удалить лишние»: 15 names (in the list's order), then the rest counted.
        var apps = (await service.ScanStoreAppsAsync(CancellationToken.None)).Apps.Where(a => a.IsBloatware).ToList();
        Assert.Equal(17, apps.Count);
        Assert.Contains(ProgramsService.PreinstalledAppsText(apps), issue.Details);
        foreach (var app in apps.Take(15))
            Assert.Contains("• " + app.DisplayName + "\n", issue.Details);
        Assert.DoesNotContain("• " + apps[15].DisplayName, issue.Details);
        Assert.Contains("• … и ещё 2", issue.Details);
        Assert.Contains("остальные — только переустановив программу, которая их поставила", issue.Details);
        Assert.Contains("точка восстановления", issue.Details);
        // The whole of it fits into the question: nothing is cut.
        Assert.DoesNotContain("(полное описание", question);
        Assert.Contains("• … и ещё 2", question);
        Assert.Contains("переустановив программу", question);
    }

    [Fact]
    public async Task Bloat_Question_OnlyStoreApps_SaysAnyCanBeInstalledAgain()
    {
        var service = CreateService(() => Records(BingNews, CandyCrush));
        var issue = await BloatIssueAsync(service);

        Assert.Contains("• " + NameOf(BingNews), issue.Details);
        Assert.Contains("• " + NameOf(CandyCrush), issue.Details);
        Assert.Contains("Любое из них можно вернуть, установив заново из Microsoft Store.", issue.Details);
        Assert.DoesNotContain("и ещё", issue.Details);
    }

    [Fact]
    public async Task Bloat_Fix_SkipsAppsGoneSinceTheScan_AndAppsInstalledAfterIt()
    {
        var installed = new List<string> { BingNews, CandyCrush };
        var service = CreateService(() => installed.Select(n => Record(n)).ToList());
        var issue = await BloatIssueAsync(service);
        installed.Remove(CandyCrush); // the user removed it meanwhile
        installed.Add(BingWeather);   // and a new preinstalled app appeared: it was not in the list the user confirmed

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("Удалено приложений: 1 из 1.", text);
        Assert.Equal(new[] { $"remove:{BingNews}" }, Removes());
    }

    [Fact]
    public async Task Bloat_Fix_NothingLeft_HasNothingToDo()
    {
        var installed = new List<string> { BingNews, CandyCrush };
        var service = CreateService(() => installed.Select(n => Record(n)).ToList());
        var issue = await BloatIssueAsync(service);
        installed.Clear();

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("Этих предустановленных приложений уже нет — делать ничего не нужно.", text);
        AssertSuccess(text);
        Assert.Empty(_calls); // not even a restore point
    }

    [Fact]
    public async Task Bloat_Fix_StoreAppListNotReceived_ChangesNothing()
    {
        var service = CreateService(() => Records(BingNews, CandyCrush));
        var scanned = (await service.ScanStoreAppsAsync(CancellationToken.None)).Apps;
        // Windows did not give the list (PowerShell failed): an empty list with an error, or even a partial one.
        var failedScan = new StoreAppsResult(scanned, "Windows не выдала список приложений Microsoft Store.");

        var text = await service.RemovePreinstalledAppsAsync(scanned, failedScan, CancellationToken.None);

        Assert.Equal("Не удалось получить список приложений Microsoft Store — ничего не изменено.", text);
        AssertFailureNotAdmin(text);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Bloat_Fix_PartialFailure_NamesTheAppsAndLooksLikeAFailure()
    {
        var service = CreateService(() => Records(BingNews, CandyCrush),
            remove: app => app.PackageFullName == BingNews ? (false, "Windows отказала.") : (true, "Удалено."));
        var issue = await BloatIssueAsync(service);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.StartsWith("Удалено приложений: 1 из 2.", text);
        Assert.Contains("Не удалось удалить (1): " + NameOf(BingNews) + ".", text);
        Assert.DoesNotContain(NameOf(CandyCrush), text);
        AssertFailureNotAdmin(text);
    }

    [Fact]
    public async Task Bloat_Fix_EverythingFails_LooksLikeAFailure()
    {
        var service = CreateService(() => Records(BingNews, CandyCrush), remove: _ => (false, "Windows отказала."));
        var issue = await BloatIssueAsync(service);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.StartsWith("Удалено приложений: 0 из 2.", text);
        Assert.Contains("Не удалось удалить (2):", text);
        Assert.Contains(NameOf(BingNews), text);
        Assert.Contains(NameOf(CandyCrush), text);
        AssertFailureNotAdmin(text);
    }

    [Fact]
    public async Task Bloat_Fix_Cancelled_StopsAndReportsWhatWasNotProcessed()
    {
        using var cts = new CancellationTokenSource();
        var service = CreateService(() => Records(BingNews, CandyCrush, BingWeather));
        service.RemoveStoreAppOverride = (app, _) =>
        {
            _calls.Add($"remove:{app.PackageFullName}");
            cts.Cancel(); // "Отмена" pressed while the first app is being removed
            return Task.FromResult((true, "Удалено."));
        };
        var issue = await BloatIssueAsync(service);

        var text = await issue.FixAsync!(cts.Token);

        Assert.Single(Removes()); // the loop stopped after the first app
        Assert.StartsWith("Удалено приложений: 1 из 3.", text);
        Assert.Contains("прервано", text);
        AssertFailureNotAdmin(text); // not marked fixed: two apps are left
    }
}
