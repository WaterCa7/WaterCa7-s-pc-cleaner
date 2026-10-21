using System.IO;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Processes;
using PcCleaner.Tests.Core;
using Xunit;
using static PcCleaner.Tests.Processes.ProcessTestData;

namespace PcCleaner.Tests.Processes;

/// <summary>
/// The Processes page and its dashboard findings in English (the real en.json): the issue texts and their module, the kill question
/// and report, the quarantine messages, the labels of a row. Runs alone (the language is process-wide) and always switches back to
/// Russian; the Russian twin of the main texts is checked outside the English block.
/// </summary>
[Collection(LanguageSequentialCollection.Name)]
public sealed class ProcessesEnglishTests
{
    private static readonly Regex Cyrillic = new("[Ѐ-ӿ]");

    private const long Mb = 1L << 20;

    public ProcessesEnglishTests() => TestEnvironment.Init();

    private static void AssertEnglish(params string?[] texts)
    {
        foreach (var text in texts)
            Assert.DoesNotMatch(Cyrillic, text ?? "");
    }

    private static KillTarget Target(int pid, string name) => new(pid, 1000 + pid, name, $@"C:\Apps\{name}");

    // ================================================================ dashboard findings

    [Fact]
    public void ASuspiciousProcess_HasAnEnglishTitleDetailsAndModule()
    {
        var suspicion = new SuspicionResult(70, new[] { "Запускается из временной папки" });
        var items = new[]
        {
            IssueItems.Item(10, "tool.exe", @"C:\Tools\tool.exe", suspicion: suspicion),
            IssueItems.Item(11, "tool.exe", @"C:\Tools\tool.exe"),
        };
        var service = CreateService().Service;

        using (TestLanguage.English())
        {
            var issue = Assert.Single(ProcessIssues.Build(items, IssueItems.Ram16, selfPid: 1));

            Assert.Equal("Processes", issue.Module);
            Assert.Equal("Processes", service.ModuleName);
            Assert.Equal("Suspicious process: tool.exe", issue.Title);
            Assert.Contains("Suspicious processes: 1 of 2.", issue.Details);
            Assert.EndsWith("This is a hint, not a verdict: on the Processes page you can check the file on VirusTotal or with Microsoft Defender.", issue.Details);
            Assert.Equal("Processes", issue.TargetPageKey);
        }

        var russian = Assert.Single(ProcessIssues.Build(items, IssueItems.Ram16, selfPid: 1));
        Assert.Equal("Процессы", russian.Module);
        Assert.Equal("Процессы", service.ModuleName);
        Assert.Equal("Подозрительный процесс: tool.exe", russian.Title);
        Assert.Contains("Подозрительных процессов: 1 из 2.", russian.Details);
    }

    [Fact]
    public void EveryKindOfFinding_IsEnglish()
    {
        var lowMemory = new MemoryStatus(16UL << 30, 1UL << 30, 94);
        using var _ = TestLanguage.English();

        var low = Assert.Single(ProcessIssues.Build(new[] { IssueItems.Item(10, "tool.exe", @"C:\Tools\tool.exe", memory: 100 * Mb) }, lowMemory, selfPid: 1));
        Assert.Equal("Low free memory", low.Title);
        Assert.StartsWith("15 GB of 16 GB is in use (94%). Because of this, Windows starts to use the slow page file.", low.Details);
        Assert.Contains("The most memory is used by:\n• tool.exe — 100 MB", low.Details);
        Assert.EndsWith("Close programs and browser tabs you do not need.", low.Details);

        var busy = Assert.Single(ProcessIssues.Build(new[] { IssueItems.Item(10, "tool.exe", @"C:\Tools\tool.exe", cpu: 60) }, IssueItems.Ram16, selfPid: 1));
        Assert.Equal("High processor load: tool.exe", busy.Title);
        Assert.StartsWith("Over 3 seconds, “tool.exe” took 60% of the processor power on average.", busy.Details);
        Assert.Contains("You can end it on the Processes page.", busy.Details);

        var system = Assert.Single(ProcessIssues.Build(new[] { IssueItems.Item(200, "svchost.exe", SvchostPath, cpu: 60) }, IssueItems.Ram16, selfPid: 1));
        Assert.EndsWith("if it lasts for hours, restart the computer.", system.Details);

        var hog = Assert.Single(ProcessIssues.Build(
            new[] { IssueItems.Item(10, "Photoshop.exe", @"C:\Apps\Photoshop.exe", memory: 5L << 30, description: "Adobe Photoshop") }, IssueItems.Ram16, selfPid: 1));
        Assert.Equal("Adobe Photoshop uses 5 GB of memory (31% of the total)", hog.Title);
        Assert.Contains("“Adobe Photoshop (Photoshop.exe)” uses 5 GB out of 16 GB of memory", hog.Details);

        var copies = Enumerable.Range(0, 30).Select(i => IssueItems.Item(1000 + i, "chrome.exe", ChromePath, memory: 150 * Mb, description: "Google Chrome")).ToList();
        var swarm = Assert.Single(ProcessIssues.Build(copies, IssueItems.Ram16, selfPid: 1));
        Assert.Equal("30 copies of chrome.exe use 4.39 GB", swarm.Title);
        Assert.StartsWith("30 copies of “Google Chrome (chrome.exe)” together use 4.39 GB out of 16 GB of memory (27%).", swarm.Details);
        Assert.Contains("A browser starts a separate process for every tab and extension.", swarm.Details);

        var single = Assert.Single(ProcessIssues.Build(
            Enumerable.Range(0, 31).Select(i => IssueItems.Item(1000 + i, "Discord.exe", @"C:\Apps\Discord.exe", memory: 120 * Mb)).ToList(), IssueItems.Ram16, selfPid: 1));
        Assert.StartsWith("31 copies of Discord.exe use 3.63 GB", single.Title);
        Assert.Contains("Many programs (for example, messengers", single.Details);

        AssertEnglish(low.Title, low.Details, busy.Title, busy.Details, system.Details, hog.Title, hog.Details, swarm.Title, swarm.Details,
            single.Title, single.Details);
    }

    [Fact]
    public void TheMinerReason_IsEnglish_AndAddedOnce()
    {
        var path = @"C:\Users\Test\AppData\Local\Temp\miner.exe";
        var unsigned = new SignatureInfo(SignatureStatus.Unsigned, null);
        var clean = SuspicionResult.Clean;

        using (TestLanguage.English())
        {
            var once = MinerHeuristic.Apply(clean, sustainedHighCpu: true, path, unsigned);
            var twice = MinerHeuristic.Apply(once, sustainedHighCpu: true, path, unsigned);

            Assert.Equal("Very high processor load: possibly a hidden miner", MinerHeuristic.Reason);
            Assert.Equal(new[] { MinerHeuristic.Reason }, once.Reasons);
            Assert.Equal(once.Reasons, twice.Reasons); // decided by the flag, not by the words
            Assert.True(twice.Flags.HasFlag(SuspicionFlags.SustainedCpu));
            Assert.Equal(35, twice.Score);
        }

        var russian = MinerHeuristic.Apply(clean, sustainedHighCpu: true, path, unsigned);
        Assert.Equal("Очень высокая загрузка процессора — возможен скрытый майнер", Assert.Single(russian.Reasons));
        Assert.Equal(35, russian.Score);
    }

    // ================================================================ the page: kill question and report

    [Fact]
    public void KillChecked_AsksAndReportsInEnglish() => UiThread.Run(async () =>
    {
        using var _ = TestLanguage.English();
        var (service, source, _, _) = CreateService();
        AddTypicalProcesses(source);
        var kill = new Func<IReadOnlyList<KillTarget>, KillReport>(targets =>
        {
            var report = new KillReport();
            report.Killed.AddRange(targets);
            return report;
        });
        var vm = new ProcessesViewModel(service) { BaselineDelay = TimeSpan.Zero, KillOverride = kill };
        await vm.RefreshAsync();
        await vm.WaitForBackgroundAsync(CancellationToken.None);
        vm.Groups.Single(g => g.ExeName == "chrome.exe").IsChecked = true;
        vm.Rows.Single(r => r.Pid == 300).IsChecked = true;
        string? title = null, question = null;
        (string Title, string Message)? info = null;
        vm.Confirm = (t, m) =>
        {
            (title, question) = (t, m);
            return true;
        };
        vm.ShowInfo = (t, m) => info = (t, m);

        await vm.KillCheckedCommand.ExecuteAsync(null);

        Assert.Equal("End the checked processes?", title);
        Assert.Contains("4 processes will be force-ended", question);
        Assert.Contains("Unsaved data in these programs will be lost.", question);
        Assert.EndsWith("Continue?", question);
        Assert.Equal("Processes ended: 4.", info!.Value.Message);
        AssertEnglish(title, question, info.Value.Title, info.Value.Message);
    });

    [Fact]
    public void AFailedKill_IsReportedInEnglish() => UiThread.Run(async () =>
    {
        using var _ = TestLanguage.English();
        var (service, source, _, _) = CreateService();
        AddTypicalProcesses(source);
        var kill = new Func<IReadOnlyList<KillTarget>, KillReport>(targets =>
        {
            var report = new KillReport();
            report.Killed.Add(targets[0]);
            foreach (var t in targets.Skip(1))
                report.Failed.Add((t, "access denied"));
            return report;
        });
        var vm = new ProcessesViewModel(service) { BaselineDelay = TimeSpan.Zero, KillOverride = kill };
        await vm.RefreshAsync();
        await vm.WaitForBackgroundAsync(CancellationToken.None);
        vm.Groups.Single(g => g.ExeName == "chrome.exe").IsChecked = true;
        vm.Confirm = (_, _) => true;
        var previous = Dialogs.ErrorHandler;
        string? shown = null;
        Dialogs.ErrorHandler = (_, m) => shown = m;
        try
        {
            await vm.KillCheckedCommand.ExecuteAsync(null);
        }
        finally
        {
            Dialogs.ErrorHandler = previous;
        }

        Assert.StartsWith("Processes ended: 1 of 3.\nCould not end:\n", shown!.Replace("\r\n", "\n"));
        Assert.Contains("chrome.exe", shown);
        Assert.Contains("— access denied", shown);
        Assert.True(HealthScoring.LooksLikeFailure(shown));
        AssertEnglish(shown);
    });

    [Fact]
    public void TheSingleKillQuestion_IsEnglish()
    {
        var chrome = Row(100, "chrome.exe", ChromePath, description: "Google Chrome");
        var other = Row(101, "chrome.exe", ChromePath, description: "Google Chrome");
        var group = new ProcessGroup("path:" + ChromePath.ToLowerInvariant(), "chrome.exe");
        group.SetInstances(new List<ProcessRow> { chrome, other });

        using var _ = TestLanguage.English();
        var program = ProcessesViewModel.BuildKillMessage(group, new List<ProcessRow> { chrome, other }, new List<ProcessRow> { chrome, other },
            new List<ProcessRow>(), tree: false);
        var tree = ProcessesViewModel.BuildKillMessage(chrome, new List<ProcessRow> { chrome }, new List<ProcessRow> { chrome, other },
            new List<ProcessRow>(), tree: true);

        Assert.StartsWith("All processes of the program “chrome.exe” will be force-ended — 2 in total (PID 100, 101).", program);
        Assert.Contains("Unsaved data in the program will be lost.", program);
        Assert.EndsWith("Continue?", program);
        Assert.StartsWith("“Google Chrome” (chrome.exe), PID 100, and all the processes it started will be force-ended — 2 in total:", tree);
        AssertEnglish(program, tree);
    }

    // ================================================================ quarantine and kill messages

    [Fact]
    public void TheQuarantineMessages_AreEnglish()
    {
        var path = @"C:\Apps\tool.exe";
        var denied = new KillReport { AccessDenied = true };
        denied.Failed.Add((Target(1, "tool.exe"), "no access"));
        var failed = new KillReport();
        failed.Failed.Add((Target(1, "tool.exe"), "protected system process."));
        var killed = new KillReport();
        killed.Killed.Add(Target(1, "tool.exe"));

        using var _ = TestLanguage.English();

        Assert.Equal("Windows did not allow ending the process: administrator rights are needed. The file was left where it was.",
            ProcessesService.KillFailedMessage(denied));
        Assert.Equal("Could not end the process: protected system process. The file was left where it was.", ProcessesService.KillFailedMessage(failed));
        Assert.True(HealthScoring.LooksLikeFailure(ProcessesService.KillFailedMessage(failed)));

        var locked = ProcessesService.MoveFailedMessage(path, new UnauthorizedAccessException(), elevated: false, killed: true);
        Assert.StartsWith("The program was ended, but its file could not be moved to quarantine: access denied", locked);
        Assert.Contains($"The file stayed where it was:\n{path}", locked);
        Assert.EndsWith("Restart the program as administrator and try again.", locked);
        Assert.StartsWith("The file could not be moved to quarantine:", ProcessesService.MoveFailedMessage(path, new IOException("x"), elevated: true, killed: false));

        var refused = ProcessesService.QuarantineRefusedMessage(path,
            ErrorText.Own(new InvalidOperationException(T._("Системные файлы Windows нельзя помещать в карантин."))), killed: false);
        Assert.Equal($"The file was not put in quarantine: Windows system files cannot be put in quarantine.\n\n{path}", refused);
        var broken = ProcessesService.QuarantineRefusedMessage(path, new InvalidOperationException("list", new IOException("x")), killed: true);
        Assert.StartsWith("The program was ended, but its file was not put in quarantine: could not read or write the quarantine list", broken);
        Assert.EndsWith("Open Settings → Quarantine to check the quarantine list, and try again. Details are in the log.", broken);

        var cancelled = ProcessesService.CancelledAfterKillMessage(path, killed);
        Assert.StartsWith("Operation canceled. The program was already ended (1 process), but its file was not moved to quarantine and stayed in place:\n" + path, cancelled);
        Assert.EndsWith("If the program starts again, you can put it in quarantine from this page.", cancelled);
        AssertEnglish(locked, refused, broken, cancelled);
    }

    // ================================================================ labels of a row

    [Fact]
    public void TheLabelsOfARow_AreEnglish()
    {
        var unsigned = new SignatureInfo(SignatureStatus.Unsigned, null);
        var row = Row(300, "evilupdater.exe", EvilPath, signature: unsigned, suspicion: new SuspicionResult(70, new[] { "x" }));
        var system = Row(4, "System", null);
        var svchost = Row(200, "svchost.exe", SvchostPath);
        svchost.SetProtection(KillVerdict.Blocked, ProcessGuard.CriticalText);

        using (TestLanguage.English())
        {
            Assert.Equal("High risk", row.StatusText);
            Assert.Equal("Unsigned", row.SignatureBadgeText);
            Assert.Equal("Trust", row.TrustButtonText);
            Assert.StartsWith("Stop treating this program as suspicious", row.TrustToolTip);
            Assert.StartsWith("Force-end the process.", row.KillToolTip);
            Assert.Equal("Windows kernel", KnownWindowsProcesses.Describe("System"));
            Assert.Equal("Host process for Windows services", KnownWindowsProcesses.Describe("svchost.exe"));
            Assert.Equal("This is an important Windows system process. It cannot be ended: the computer may freeze, restart or be left without protection.",
                ProcessGuard.CriticalText);
            Assert.Equal("Windows system process; it cannot be ended", svchost.CheckTooltip);
            var self = Row(900, "WaterCa7sPcCleaner.exe", @"C:\Apps\WaterCa7sPcCleaner.exe");
            self.SetProtection(KillVerdict.Blocked, ProcessGuard.SelfText, isSelf: true);
            Assert.Equal("This is WaterCa7s PC Cleaner itself. To close it, just close the program window.", self.CheckTooltip);
            Assert.Equal("No access to the process file.", QuarantineRules.BlockReason(null, null, null, KillVerdict.Allowed, trusted: false));
            Assert.StartsWith("This process has no file on disk", system.PathText);
            AssertEnglish(row.StatusText, row.StatusToolTip, row.TrustToolTip, row.KillToolTip, row.KillTreeToolTip, row.QuarantineToolTip,
                row.SignatureText, row.PathText, system.PathText, svchost.CheckTooltip, ProcessGuard.SelfText, ProcessGuard.ExplorerText);
        }

        Assert.Equal("Высокий риск", row.StatusText);
        Assert.Equal("Доверять", row.TrustButtonText);
        Assert.Equal("Ядро Windows", KnownWindowsProcesses.Describe("System"));
    }

    [Theory]
    [InlineData(2, 3, "2 of 3 processes are WaterCa7s PC Cleaner helper processes:")]
    [InlineData(1, 3, "One of the processes is a WaterCa7s PC Cleaner helper process:")]
    public void TheNoteAboutOwnHelpers_IsEnglish(int helpers, int count, string expected)
    {
        var rows = Enumerable.Range(1, count)
            .Select(i => new ProcessRow(new ProcessStaticInfo(new ProcessKey(i, 1000 + i), "powershell.exe")))
            .ToList();
        for (var i = 0; i < helpers; i++)
            rows[i].OwnRole = ProcessOwnRole.Helper;
        var group = new ProcessGroup("powershell.exe", "powershell.exe");
        group.SetInstances(rows);

        using var _ = TestLanguage.English();

        Assert.StartsWith(expected, group.OwnNote);
        AssertEnglish(group.OwnNote, group.CountToolTip, group.HeaderSubtitle, group.InstancesText);
    }

    [Fact]
    public void ThePageTexts_AreEnglish() => UiThread.Run(async () =>
    {
        using var _ = TestLanguage.English();
        var (service, source, _, _) = CreateService();
        AddTypicalProcesses(source);
        var vm = new ProcessesViewModel(service) { BaselineDelay = TimeSpan.Zero };
        Assert.Equal("loading…", vm.ProgramCountText);
        await vm.RefreshAsync();
        await vm.WaitForBackgroundAsync(CancellationToken.None);

        Assert.Equal("Processes", vm.Title);
        Assert.Matches(@"^in \d+ programs$", vm.ProgramCountText);
        Assert.StartsWith("1 suspicious process in 1 program.", vm.SuspiciousSummary);
        AssertEnglish(vm.Title, vm.Subtitle, vm.ProgramCountText, vm.SuspiciousHint, vm.SuspiciousSummary, vm.EmptyTitle, vm.EmptyText, vm.PauseToolTip,
            vm.AdminHintText);
    });
}
