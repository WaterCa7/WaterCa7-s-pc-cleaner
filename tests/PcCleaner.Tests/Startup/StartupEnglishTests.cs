using System.IO;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Startup;
using PcCleaner.Tests.Core;
using Xunit;

namespace PcCleaner.Tests.Startup;

/// <summary>
/// The Startup page and its dashboard findings in English (the real en.json): the issue texts, the fix results (and that a failed
/// one is still a failure), the bulk summary, the labels of an entry, and the Defender verdict that no longer depends on words.
/// Registry roots and Startup folders live in the sandbox. Runs alone (the language is process-wide) and always switches back to
/// Russian; the Russian twin of the main texts is checked outside the English block.
/// </summary>
[Collection(LanguageSequentialCollection.Name)]
public sealed class StartupEnglishTests
{
    private const string ApprovedRun = @"HKCU\Explorer\StartupApproved\Run";

    private static readonly Regex Cyrillic = new("[Ѐ-ӿ]");

    private static void AssertEnglish(params string?[] texts)
    {
        foreach (var text in texts)
            Assert.DoesNotMatch(Cyrillic, text ?? "");
    }

    /// <summary>Registers an unsigned program started from a temp folder (the sandbox lives there), scans and returns its issue.</summary>
    private static (HealthIssue Issue, StartupItem Item) SuspiciousEntry(StartupSandbox sandbox, string relativeKey, string name)
    {
        var exe = sandbox.CreateFile($"{name}.exe");
        sandbox.SetValue(relativeKey, name, $"\"{exe}\" /silent");
        var items = sandbox.Service.Scan(null, CancellationToken.None);
        var item = Assert.Single(items, i => i.Name == name);
        Assert.True(item.IsSuspicious, "the test entry must look suspicious");
        var issue = Assert.Single(sandbox.Service.BuildIssues(items), i => i.Module == sandbox.Service.ModuleName && i.Title.Contains(name, StringComparison.Ordinal));
        return (issue, item);
    }

    private static StartupItem Entry(string name, StartupKind kind = StartupKind.RegistryRun, StartupScope scope = StartupScope.CurrentUser)
        => new() { Id = $"test|{name}", Kind = kind, Scope = scope, Name = name, Command = $@"C:\Apps\{name}.exe" };

    // ================================================================ suspicious entry: finding and fix

    [Fact]
    public void ASuspiciousEntry_HasAnEnglishTitleLabelAndExplanation()
    {
        using var sandbox = new StartupSandbox();
        var exe = sandbox.CreateFile("Evil.exe");
        sandbox.SetValue(@"HKCU\Run", "Evil", $"\"{exe}\" /silent");

        using (TestLanguage.English())
        {
            var (issue, item) = SuspiciousEntry(sandbox, @"HKCU\Run", "Evil");

            Assert.Equal("Startup", issue.Module);
            Assert.Equal("Startup", sandbox.Service.ModuleName);
            Assert.Equal("Suspicious startup entry: Evil", issue.Title);
            Assert.Equal("Disable", issue.FixLabel);
            Assert.Contains(". Source: Registry: Run. Command: ", issue.Details);
            Assert.EndsWith("the button will disable it; you can turn it back on on the Startup page.", issue.Details);
            Assert.Equal("Startup", issue.TargetPageKey);
            Assert.Equal("Registry", item.SourceShort);
            AssertEnglish(issue.Title, issue.Details, issue.FixLabel, item.RiskTooltip, item.Advice);
        }

        var (russian, _) = SuspiciousEntry(sandbox, @"HKCU\Run", "Evil");
        Assert.Equal("Автозагрузка", russian.Module);
        Assert.Equal("Подозрительная запись в автозагрузке: Evil", russian.Title);
        Assert.Equal("Отключить", russian.FixLabel);
    }

    [Fact]
    public async Task TheFix_SaysInEnglishWhatHappened_AndIsNotAFailure()
    {
        using var sandbox = new StartupSandbox();
        using var _ = TestLanguage.English();
        var (disable, _) = SuspiciousEntry(sandbox, @"HKCU\Run", "Evil");
        var (remove, _) = SuspiciousEntry(sandbox, @"HKCU\RunOnce", "Evil2");

        var disabled = await disable.FixAsync!(CancellationToken.None);
        var removed = await remove.FixAsync!(CancellationToken.None);

        Assert.Equal("The entry “Evil” was disabled; you can turn it back on on the Startup page.", disabled);
        Assert.Equal("The entry “Evil2” was deleted from startup; a backup was saved.", removed);
        Assert.False(HealthScoring.LooksLikeFailure(disabled));
        Assert.False(HealthScoring.LooksLikeFailure(removed));
        Assert.Equal("Delete entry", remove.FixLabel);
        Assert.Equal(0x03, Assert.IsType<byte[]>(sandbox.GetValue(ApprovedRun, "Evil"))[0]);
        Assert.Null(sandbox.GetValue(@"HKCU\RunOnce", "Evil2"));
    }

    [Fact]
    public async Task AnEntryThatIsGone_IsNothingToDo_AndCountsAsFixed_InEnglish()
    {
        using var sandbox = new StartupSandbox();
        var (issue, _) = SuspiciousEntry(sandbox, @"HKCU\Run", "Evil");
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{sandbox.RegistryRoot}\HKCU\Run", writable: true)!)
            key.DeleteValue("Evil");

        using var _ = TestLanguage.English();
        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("The entry has already been deleted from startup, so there is nothing to do.", text);
        Assert.False(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public async Task AFailedFix_IsStillAFailure_InEnglish()
    {
        using var sandbox = new StartupSandbox();
        var (issue, item) = SuspiciousEntry(sandbox, @"HKCU\RunOnce", "Evil");
        sandbox.BackupHook = (_, _, _) => Task.FromResult<string?>(null); // the .reg export fails: nothing is deleted
        var elevated = new StartupSandbox(elevated: false);
        var (needsAdmin, _) = SuspiciousEntry(elevated, @"HKLM\Run", "Evil3");

        using var _ = TestLanguage.English();
        var failed = await issue.FixAsync!(CancellationToken.None);
        var admin = await needsAdmin.FixAsync!(CancellationToken.None);

        Assert.Equal("Could not save a backup of the registry key, so the deletion was canceled.", failed);
        Assert.True(HealthScoring.LooksLikeFailure(failed));
        Assert.False(HealthScoring.LooksLikeAdminRequired(failed));
        Assert.NotNull(sandbox.GetValue(@"HKCU\RunOnce", "Evil"));
        Assert.StartsWith("Administrator rights are needed: ", admin);
        Assert.True(HealthScoring.LooksLikeAdminRequired(admin));
        Assert.True(HealthScoring.LooksLikeFailure(admin));

        // a reason that does not say it yet gets the sentence; one that does is not prefixed twice
        var plain = StartupService.SuspiciousFixText(item, StartupActionResult.Fail("The registry is not available."), removed: false);
        var removal = StartupService.SuspiciousFixText(item, StartupActionResult.Fail("The registry is not available."), removed: true);
        var stated = StartupService.SuspiciousFixText(item, StartupActionResult.Fail("Could not change the entry: access denied."), removed: false);
        Assert.Equal("Could not disable “Evil”: The registry is not available.", plain);
        Assert.Equal("Could not delete “Evil”: The registry is not available.", removal);
        Assert.Equal("Could not change the entry: access denied.", stated);
        Assert.True(HealthScoring.LooksLikeFailure(plain) && HealthScoring.LooksLikeFailure(removal) && HealthScoring.LooksLikeFailure(stated));
        AssertEnglish(failed, admin, plain, removal, stated);
        elevated.Dispose();
    }

    [Fact]
    public void TheRussianFailureMessages_OfTheService_AreReadAsBefore()
    {
        // SuspiciousFixText asks the dashboard's reading of a result whether the reason already says "not done": for the reasons the
        // service itself makes that is exactly "begins with «Не удалось»", as it was before the check became language-independent.
        var item = Entry("Evil");
        foreach (var (message, says) in new[]
                 {
                     ("Не удалось изменить запись: доступ запрещён.", true),
                     ("Не удалось удалить запись: файл занят.", true),
                     ("Не удалось сохранить резервную копию реестра — изменение отменено.", true),
                     ("Не удалось сохранить резервную копию раздела реестра — удаление отменено.", true),
                     ("Не удалось переместить файл в Корзину (возможно, он открыт другой программой).", true),
                     ("Не удалось изменить тип запуска службы (код 5).", true),
                     ("Для этой записи Windows не поддерживает отключение.", false),
                     ("Неизвестное расположение записи.", false),
                     ("Неизвестное расположение ярлыка.", false),
                     ("Неизвестный путь задания.", false),
                     ("Некорректное имя службы.", false),
                     ("Эту запись нельзя отключить.", false),
                     ("Эту запись нельзя удалить.", false),
                     ("Службы не удаляются — их можно только перевести в ручной режим.", false),
                     ("Системные записи Windows программа не удаляет.", false),
                     ("Однократный запуск нельзя отключить — Windows сама удалит запись после следующего входа. Если она не нужна, её можно удалить.", false),
                     ("Это системное задание Windows — программа его не меняет.", false),
                     ("Это системная служба Microsoft — программа её не меняет.", false),
                     ("Реестр недоступен.", false),
                 })
        {
            var text = StartupService.SuspiciousFixText(item, StartupActionResult.Fail(message), removed: false);

            Assert.Equal(says ? message : $"Не удалось отключить «Evil»: {message}", text);
        }
    }

    // ================================================================ broken entries and the bulk summary

    [Fact]
    public async Task TheBrokenEntriesFinding_AndItsFix_AreEnglish()
    {
        using var sandbox = new StartupSandbox();
        sandbox.SetValue(@"HKCU\Run", "Gone", $"\"{sandbox.MissingFile("gone.exe")}\"");

        using var _ = TestLanguage.English();
        var items = sandbox.Service.Scan(null, CancellationToken.None);
        var issue = Assert.Single(sandbox.Service.BuildIssues(items), i => i.Title.StartsWith("Broken startup entries", StringComparison.Ordinal));

        Assert.Equal("Broken startup entries (1)", issue.Title);
        Assert.Equal("A tidy startup list", issue.Gain);
        Assert.StartsWith("These entries point to files that no longer exist: Gone.", issue.Details);
        AssertEnglish(issue.Title, issue.Details, issue.Gain);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.StartsWith("Deleted from startup: 1 entry.", text);
        Assert.Contains("(shortcuts go to the Recycle Bin).", text);
        Assert.False(HealthScoring.LooksLikeFailure(text));
        Assert.Null(sandbox.GetValue(@"HKCU\Run", "Gone"));
    }

    [Fact]
    public void TheBulkSummary_IsEnglish_AndReadAsFailureOnlyWhenSomethingFailed()
    {
        var done = new[] { Entry("A"), Entry("B") };
        var failed = new[] { (Entry("Broken"), "no access") };

        using (TestLanguage.English())
        {
            var skipped = new[] { (Entry("Kept"), StartupService.TargetIsBack) };
            var ok = new StartupBulkResult(done, Array.Empty<(StartupItem, string)>()).Summary;
            var partial = new StartupBulkResult(done, failed) { Skipped = skipped }.Summary;
            var nothing = new StartupBulkResult(Array.Empty<StartupItem>(), Array.Empty<(StartupItem, string)>()) { Skipped = skipped }.Summary;
            var nl = Environment.NewLine;

            Assert.Equal($"Deleted from startup: 2 entries.{nl}Backups are saved in the folder {AppPaths.BackupsDir} (shortcuts go to the Recycle Bin).", ok);
            Assert.False(HealthScoring.LooksLikeFailure(ok));
            Assert.Equal(string.Join(nl, new[]
            {
                "Deleted from startup: 2 entries.",
                $"Backups are saved in the folder {AppPaths.BackupsDir} (shortcuts go to the Recycle Bin).",
                "",
                "Left unchanged: the entries changed after the check (1):",
                "• Kept — the program file is back in place",
                "Refresh the startup list to see their current state.",
                "",
                "Could not remove (1):",
                "• Broken — no access",
            }), partial);
            Assert.True(HealthScoring.LooksLikeFailure(partial));
            Assert.StartsWith("Nothing to delete.", nothing);
            AssertEnglish(ok, partial, nothing);
        }

        var russian = new StartupBulkResult(done, failed).Summary;
        Assert.StartsWith("Удалено из автозагрузки: 2 записи.", russian);
        Assert.Contains("Не удалось удалить (1):", russian);
    }

    [Fact]
    public void TheBannerAfterDeletingBrokenEntries_IsEnglish()
    {
        var done = new[] { Entry("A") };
        using var _ = TestLanguage.English();

        Assert.Equal("Broken entries deleted: 1.", StartupViewModel.BrokenResultMessage(new StartupBulkResult(done, Array.Empty<(StartupItem, string)>())));
        Assert.Equal("Deleted 1, failed: 1.", StartupViewModel.BrokenResultMessage(new StartupBulkResult(done, new[] { (Entry("B"), "x") })));
        Assert.Equal("Nothing to delete. Left unchanged (the entry changed after the check): 1. The list has been refreshed.",
            StartupViewModel.BrokenResultMessage(new StartupBulkResult(Array.Empty<StartupItem>(), Array.Empty<(StartupItem, string)>())
            {
                Skipped = new[] { (Entry("C"), "x") },
            }));
    }

    // ================================================================ labels of an entry

    [Fact]
    public void TheStateAndSourceLabels_AreEnglish()
    {
        var run = Entry("Run");
        var runOnce = Entry("Once", StartupKind.RegistryRunOnce);
        var folder = Entry("Folder", StartupKind.StartupFolder, StartupScope.AllUsers);
        var task = new StartupItem { Id = "task|\\X", Kind = StartupKind.ScheduledTask, Scope = StartupScope.CurrentUser, Name = "Task", TriggerText = "at sign-in" };
        var service = new StartupItem { Id = "svc|S", Kind = StartupKind.Service, Scope = StartupScope.AllUsers, Name = "Svc", ServiceStartMode = "delayed-auto" };
        var disabled = Entry("Off");
        disabled.IsEnabled = false;
        disabled.DisabledTime = new DateTime(2026, 10, 18, 14, 5, 0);
        var broken = Entry("Broken");
        broken.TargetExists = false;
        var risky = Entry("Risky");
        risky.Suspicion = new SuspicionResult(80, new[] { "reason" });

        using (TestLanguage.English())
        {
            Assert.Equal(("Registry: Run", "Registry", "Enabled", "For you", "for you"), (run.SourceText, run.SourceShort, run.StateText, run.ScopeText, run.SourceDetail));
            Assert.Equal(("Registry: RunOnce", "Registry, once", "Once"), (runOnce.SourceText, runOnce.SourceShort, runOnce.StateText));
            Assert.Equal(("Startup folder", "Folder", "For all users"), (folder.SourceText, folder.SourceShort, folder.ScopeText));
            Assert.Equal(("Task Scheduler", "Scheduler", "at sign-in"), (task.SourceText, task.SourceShort, task.SourceDetail));
            Assert.Equal(("Windows service", "Service", "delayed start"), (service.SourceText, service.SourceShort, service.SourceDetail));
            Assert.Equal("Disabled", disabled.StateText);
            Assert.Equal("Disabled Oct 18, 2026 14:05: Windows does not run this entry.", disabled.StateTooltip);
            Assert.Equal(("File not found", "File not found", "Publisher unknown"), (broken.StateText, broken.SignatureText, run.PublisherText));
            Assert.Equal(("High risk", "Disable", "Trust"), (risky.RiskText, risky.ToggleActionText, risky.TrustActionText));
            Assert.Equal("Enable", disabled.ToggleActionText);
            Assert.Equal("Starts automatically with Windows.", run.StateTooltip);
            Assert.StartsWith("A run-once entry cannot be disabled", runOnce.ToggleBlockedReason);
            Assert.Equal("A Windows Task Scheduler task that runs at sign-in. Update programs often work this way.", task.Explanation);
            Assert.Equal("(no name)", Entry("").DisplayName);
            Assert.Equal("Run startup what is this", run.SearchQuery);
            risky.IsTrusted = true;
            Assert.Equal(("Trusted", "Stop trusting"), (risky.RiskText, risky.TrustActionText));
            AssertEnglish(run.Explanation, runOnce.Explanation, folder.Explanation, service.Explanation, run.Advice, runOnce.Advice, service.Advice,
                broken.Advice, broken.StateTooltip, runOnce.StateTooltip, disabled.ToggleTooltip, run.ToggleTooltip, run.RiskTooltip, risky.RiskTooltip);
        }

        Assert.Equal(("Включено", "Реестр", "Для вас"), (run.StateText, run.SourceShort, run.ScopeText));
        Assert.Equal("Планировщик заданий", task.SourceText);
    }

    [Fact]
    public void ThePageTexts_AreEnglish() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        sandbox.SetValue(@"HKCU\Run", "Gone", $"\"{sandbox.MissingFile("gone.exe")}\"");
        using var _ = TestLanguage.English();
        var vm = new StartupViewModel(sandbox.Service);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Startup", vm.Title);
        Assert.Equal(new[] { "All", "Registry and folders", "Scheduler", "Services" }, vm.SourceFilters.Select(f => f.Title));
        Assert.Equal("Delete broken entries (1)", vm.DeleteBrokenText);
        Assert.Equal("Entries: 1", vm.ShownText);
        Assert.StartsWith("Show Windows tasks", vm.WindowsTasksText);
        Assert.Equal("Show Microsoft services", vm.MicrosoftServicesText);
        AssertEnglish(vm.Title, vm.Subtitle, vm.ShownText, vm.DeleteBrokenText, vm.WindowsTasksText, vm.MicrosoftServicesText, vm.EmptyTitle, vm.EmptyText);

        vm.ApplyItems(Array.Empty<StartupItem>());
        Assert.Equal("Startup is empty", vm.EmptyTitle);
        Assert.Equal("No third-party programs start with Windows. Keep it up!", vm.EmptyText);
    });

    [Fact]
    public void TheBulkBar_AsksAndReportsInEnglish() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        var exe = sandbox.CreateFile("Tool.exe");
        sandbox.SetValue(@"HKCU\Run", "Tool", $"\"{exe}\"");
        using var _ = TestLanguage.English();
        var vm = new StartupViewModel(sandbox.Service);
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.ItemsView.Cast<StartupItem>().Single(i => i.Name == "Tool");
        item.IsChecked = true;
        string? title = null, question = null, banner = null;
        vm.Confirm = (t, m) =>
        {
            (title, question) = (t, m);
            return true;
        };
        vm.ShowInfo = (_, _) => { };

        await vm.DisableCheckedCommand.ExecuteAsync(null);
        banner = vm.Message;

        Assert.Equal("Disable the checked entry?", title);
        Assert.StartsWith("Turn off startup (1 entry)", question);
        Assert.Contains("The entries are not deleted: Windows just stops running them at sign-in", question);
        Assert.EndsWith("Continue?", question);
        Assert.Equal("Disabled: 1.", banner);
        AssertEnglish(title, question, banner);
    });

    // ================================================================ Defender: the verdict is a flag, not a word

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    public void ADefenderScan_SaysTheThreatByFlag_InBothLanguages(int exitCode, bool threat)
    {
        var russian = FileTools.DescribeDefenderScan(exitCode, " out ");
        DefenderScanResult english;
        using (TestLanguage.English())
            english = FileTools.DescribeDefenderScan(exitCode, " out ");

        Assert.Equal(threat, russian.ThreatFound);
        Assert.Equal(threat, english.ThreatFound);
        Assert.DoesNotMatch(Cyrillic, english.Text);
        Assert.Equal(threat, english.Text.Contains("found a threat", StringComparison.Ordinal));
        Assert.Equal(threat, russian.Text.Contains("обнаружил", StringComparison.Ordinal));
        if (exitCode == 2)
            Assert.Equal("Defender found a threat! Open “Windows Security” → “Protection history”.", english.Text);
        if (exitCode == 1)
            Assert.Equal("Проверка завершилась с кодом 1. out", russian.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADetection_IsShownAsAnError_InBothLanguages(bool english) => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        var exe = sandbox.CreateFile("Tool.exe");
        sandbox.SetValue(@"HKCU\Run", "Tool", $"\"{exe}\"");
        var previous = Dialogs.InfoHandler;
        string? dialog = null;
        Dialogs.InfoHandler = (_, m) => dialog = m;
        using var language = english ? TestLanguage.English() : null;
        try
        {
            var vm = new StartupViewModel(sandbox.Service);
            await vm.RefreshCommand.ExecuteAsync(null);
            var item = vm.ItemsView.Cast<StartupItem>().Single(i => i.Name == "Tool");
            Assert.True(item.TargetExists);

            vm.DefenderScan = (_, _) => Task.FromResult(FileTools.DescribeDefenderScan(2, ""));
            await vm.DefenderCommand.ExecuteAsync(item);
            Assert.True(vm.MessageIsError, vm.Message);
            Assert.Contains(english ? "found a threat" : "обнаружил угрозу", vm.Message);
            Assert.Contains(english ? "found a threat" : "обнаружил угрозу", dialog);

            // the words do not decide: a clean result whose text happens to say "обнаружил" is not an error
            vm.DefenderScan = (_, _) => Task.FromResult(new DefenderScanResult("обнаружил — но это только слово", ThreatFound: false));
            await vm.DefenderCommand.ExecuteAsync(item);
            Assert.False(vm.MessageIsError, vm.Message);

            vm.DefenderScan = (_, _) => Task.FromResult(FileTools.DescribeDefenderScan(0, ""));
            await vm.DefenderCommand.ExecuteAsync(item);
            Assert.False(vm.MessageIsError, vm.Message);
            Assert.Contains(english ? "No threats found." : "Угроз не найдено.", vm.Message);
        }
        finally
        {
            Dialogs.InfoHandler = previous;
        }
    });
}
