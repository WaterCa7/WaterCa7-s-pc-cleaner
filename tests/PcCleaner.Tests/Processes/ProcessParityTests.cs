using System.Diagnostics;
using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Processes;
using Xunit;
using static PcCleaner.Tests.Processes.ProcessTestData;

namespace PcCleaner.Tests.Processes;

/// <summary>Evaluated items for <see cref="ProcessIssues.Build"/> (pure, no system access).</summary>
internal static class IssueItems
{
    public static readonly MemoryStatus Ram16 = new(16UL << 30, 10UL << 30, 37);

    public static ProcessIssues.Evaluated Item(int pid, string name, string? path, double cpu = 0, long memory = 1,
        SuspicionResult? suspicion = null, int parent = 0, long? created = null, string? description = null)
    {
        var createTime = created ?? 1000 + pid;
        var info = new ProcessStaticInfo(new ProcessKey(pid, createTime), name)
        {
            Path = path,
            Meta = description is null ? null : new FileMeta(description, null, null),
        };
        var sample = new ProcessSample(new RawProcess(pid, parent, name, createTime, 0, memory, memory, 1), info, cpu);
        return new ProcessIssues.Evaluated(sample, suspicion ?? SuspicionResult.Clean);
    }
}

/// <summary>The cleaner never reports itself or the helpers it starts for its checks (powershell.exe, reg.exe, netsh.exe...).</summary>
public class OwnProcessTests
{
    public OwnProcessTests() => TestEnvironment.Init();

    private const int Self = 900;

    private static readonly string SelfPath = Path.Combine(Path.GetTempPath(), "unsigned-cleaner-test", "WaterCa7sPcCleaner.exe");

    private static readonly string PowerShellPath = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>A hidden-window launch is enough for the heuristic; no real script is involved.</summary>
    private const string HiddenLaunch = "powershell.exe -NoProfile -WindowStyle Hidden -File C:\\Checks\\check.ps1";

    private static readonly SuspicionResult Hidden = new(65, new[] { "Скрытый или закодированный запуск PowerShell" });

    [Fact]
    public void Dashboard_IgnoresTheCleanerAndEverythingItStarted()
    {
        var items = new[]
        {
            IssueItems.Item(Self, "WaterCa7sPcCleaner.exe", SelfPath, cpu: 30, suspicion: new SuspicionResult(50, new[] { "x" }), parent: 1),
            IssueItems.Item(901, "powershell.exe", PowerShellPath, cpu: 60, suspicion: Hidden, parent: Self),
            // the console window of our PowerShell: a grandchild
            IssueItems.Item(902, "conhost.exe", Path.Combine(Environment.SystemDirectory, "conhost.exe"), cpu: 55, suspicion: Hidden,
                parent: 901),
            // a big helper would otherwise be a memory hog
            IssueItems.Item(903, "reg.exe", Path.Combine(Environment.SystemDirectory, "reg.exe"), memory: 9L << 30, parent: Self),
        };
        Assert.Empty(ProcessIssues.Build(items, IssueItems.Ram16, Self));
    }

    [Fact]
    public void Dashboard_StillReportsTheSameProgramStartedBySomeoneElse()
    {
        var items = new[]
        {
            IssueItems.Item(Self, "WaterCa7sPcCleaner.exe", SelfPath, parent: 1),
            IssueItems.Item(901, "powershell.exe", PowerShellPath, suspicion: Hidden, parent: Self),
            IssueItems.Item(950, "powershell.exe", PowerShellPath, suspicion: Hidden, parent: 4000),
        };
        var issue = Assert.Single(ProcessIssues.Build(items, IssueItems.Ram16, Self));
        Assert.Equal("Подозрительный процесс: powershell.exe", issue.Title);
        Assert.Contains("Подозрительных процессов: 1 из 2", issue.Details);
    }

    [Fact]
    public void StaleParentPid_IsNotTreatedAsOurs()
    {
        // Windows reuses PIDs: a process "started by" our PID before the cleaner even existed belongs to someone else.
        var items = new[]
        {
            IssueItems.Item(Self, "WaterCa7sPcCleaner.exe", SelfPath, parent: 1, created: 5000),
            IssueItems.Item(960, "tool.exe", @"C:\Tools\tool.exe", suspicion: new SuspicionResult(70, new[] { "x" }), parent: Self,
                created: 100),
        };
        var issue = Assert.Single(ProcessIssues.Build(items, IssueItems.Ram16, Self));
        Assert.Contains("tool.exe", issue.Title);
    }

    [Fact]
    public void Find_UsesDirectChildrenWhenTheCleanerIsNotListed()
    {
        var conhost = Path.Combine(Environment.SystemDirectory, "conhost.exe");
        var all = new List<(int Pid, int Parent, long Created, string Name, string? Path)>
        {
            (901, Self, 10, "powershell.exe", PowerShellPath), (902, 901, 11, "conhost.exe", conhost), (903, 1, 12, "conhost.exe", conhost),
        };
        var own = Find(all, Self);
        Assert.Equal(new[] { 901, 902 }, own.Select(p => p.Pid).OrderBy(p => p));
        Assert.Empty(Find(all, 12345));
    }

    private static HashSet<(int Pid, int Parent, long Created, string Name, string? Path)> Find(
        List<(int Pid, int Parent, long Created, string Name, string? Path)> all, int self)
        => OwnProcesses.Find(all, self, p => p.Pid, p => p.Parent, p => p.Created, p => p.Name, p => p.Path);

    [Fact]
    public void ProgramsTheCleanerOpensForTheUser_AreStillJudged()
    {
        // Only the console tools of its checks are ours. A browser opened by "VirusTotal", an uninstaller started from
        // "Программы" (and the copy of itself it runs from Temp), Explorer or Notepad are the user's programs.
        var temp = Path.GetTempPath();
        var all = new List<(int Pid, int Parent, long Created, string Name, string? Path)>
        {
            (Self, 1, 5, "WaterCa7sPcCleaner.exe", SelfPath),
            (901, Self, 10, "powershell.exe", PowerShellPath),
            (902, 901, 11, "conhost.exe", Path.Combine(Environment.SystemDirectory, "conhost.exe")),
            (910, Self, 12, "chrome.exe", ChromePath),
            (911, 910, 13, "chrome.exe", ChromePath),
            (920, Self, 14, "uninstall.exe", @"C:\Program Files\Toolbar\uninstall.exe"),
            (921, 920, 15, "Au_.exe", Path.Combine(temp, @"~nsu.tmp\Au_.exe")),
            (922, 921, 16, "cmd.exe", Path.Combine(Environment.SystemDirectory, "cmd.exe")), // a tool, but not started by us
            (930, Self, 17, "explorer.exe", Path.Combine(WindowsDir, "explorer.exe")),
            (931, Self, 18, "notepad.exe", Path.Combine(Environment.SystemDirectory, "notepad.exe")),
            // a tool name is not enough: the file must be the real one from the Windows folders
            (940, Self, 19, "reg.exe", Path.Combine(temp, "reg.exe")),
            // Defender's command-line scanner lives outside C:\Windows
            (950, Self, 20, "MpCmdRun.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Windows Defender\Platform\4.18.1\MpCmdRun.exe")),
            // the helper has already exited / no access: started by us under a tool's name
            (960, Self, 21, "netsh.exe", null),
        };
        var own = Find(all, Self).Select(p => p.Pid).OrderBy(p => p);
        Assert.Equal(new[] { Self, 901, 902, 950, 960 }, own);
    }

    [Fact]
    public void Dashboard_StillReportsAnUninstallerStartedFromTheCleaner()
    {
        var uninstaller = Path.Combine(Path.GetTempPath(), @"~nsu.tmp\Au_.exe");
        var items = new[]
        {
            IssueItems.Item(Self, "WaterCa7sPcCleaner.exe", SelfPath, parent: 1),
            IssueItems.Item(920, "Au_.exe", uninstaller, suspicion: new SuspicionResult(50, new[] { "x" }), parent: Self),
            IssueItems.Item(930, "chrome.exe", ChromePath, memory: 6L << 30, parent: Self, description: "Google Chrome"),
        };
        var issues = ProcessIssues.Build(items, IssueItems.Ram16, Self);
        Assert.Contains(issues, i => i.Title == "Подозрительный процесс: Au_.exe");
        Assert.Contains(issues, i => i.Title.StartsWith("Google Chrome занимает", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Page_LabelsOwnProcessesAndDoesNotJudgeThem()
    {
        var (service, source, details, _) = CreateService(selfPid: Self);
        source.Add(Self, "WaterCa7sPcCleaner.exe", SelfPath, parent: 1);
        source.Add(901, "powershell.exe", PowerShellPath, parent: Self);
        source.Add(902, "powershell.exe", PowerShellPath, parent: 4000);
        // An uninstaller started from "Программы" is a child of the cleaner too, but it is not one of our helpers.
        source.Add(903, "evil-uninstall.exe", Path.Combine(Path.GetTempPath(), "evil-uninstall.exe"), parent: Self);
        details.Processes.Add(new WmiProcessRow(901, Self, HiddenLaunch, PowerShellPath, null));
        details.Processes.Add(new WmiProcessRow(902, 4000, HiddenLaunch, PowerShellPath, null));
        var vm = new ProcessesViewModel(service) { BaselineDelay = TimeSpan.Zero };
        await vm.RefreshAsync();
        await vm.WaitForBackgroundAsync(CancellationToken.None);

        var uninstaller = vm.Rows.Single(r => r.Pid == 903);
        Assert.Equal(ProcessOwnRole.None, uninstaller.OwnRole);
        Assert.True(uninstaller.IsSuspicious);
        Assert.Null(uninstaller.OwnNote);

        // The cleaner itself: unsigned and in Temp here, which would be suspicious for anything else.
        var self = vm.Rows.Single(r => r.Pid == Self);
        Assert.Equal(ProcessOwnRole.Self, self.OwnRole);
        Assert.Equal(ProcessStatusKind.Own, self.StatusKind);
        Assert.Equal("Эта программа", self.StatusText);
        Assert.False(self.IsSuspicious);
        Assert.False(self.CanKill);

        // Our PowerShell check with a hidden window.
        var helper = vm.Rows.Single(r => r.Pid == 901);
        Assert.Equal(ProcessOwnRole.Helper, helper.OwnRole);
        Assert.Equal("Служебный", helper.StatusText);
        Assert.Contains("служебный процесс WaterCa7s PC Cleaner", helper.StatusToolTip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("служебный процесс WaterCa7s PC Cleaner", helper.OwnNote);
        Assert.False(helper.IsSuspicious);
        Assert.False(helper.IsVerifiedClean); // labelled as ours, not "nothing found"

        // The same PowerShell started by someone else is still judged.
        var other = vm.Rows.Single(r => r.Pid == 902);
        Assert.Equal(ProcessOwnRole.None, other.OwnRole);
        Assert.True(other.IsSuspicious);
        Assert.Equal(2, vm.SuspiciousCount); // the other PowerShell and the uninstaller

        var powershell = vm.Groups.Single(g => g.ExeName == "powershell.exe");
        Assert.Equal(ProcessOwnRole.None, powershell.OwnRole);
        Assert.True(powershell.IsSuspicious);
        Assert.Equal(1, powershell.OwnCount);
        Assert.StartsWith("Один из процессов — служебный процесс WaterCa7s PC Cleaner", powershell.OwnNote);

        var cleaner = vm.Groups.Single(g => g.ExeName == "WaterCa7sPcCleaner.exe");
        Assert.Equal(ProcessStatusKind.Own, cleaner.StatusKind);
        Assert.Null(cleaner.OwnNote); // the protection note already says what it is

        vm.SearchText = "служебный";
        Assert.Contains(vm.Items, i => i.ExeName == "powershell.exe");
        Assert.Contains(vm.Items, i => i.ExeName == "WaterCa7sPcCleaner.exe");
    }

    [Fact]
    public void OwnHelper_CannotBeQuarantined()
    {
        var reason = QuarantineRules.BlockReason(EvilPath, new SignatureInfo(SignatureStatus.Unsigned, null),
            new SuspicionResult(60, new[] { "x" }), KillVerdict.Allowed, false, @"C:\self.exe", isOwnHelper: true);
        Assert.NotNull(reason);
        Assert.Contains("служебный процесс", reason);
    }
}

public class MinerHeuristicTests
{
    public MinerHeuristicTests() => TestEnvironment.Init();

    private static readonly SignatureInfo Unsigned = new(SignatureStatus.Unsigned, null);
    private static readonly SignatureInfo GameSigned = new(SignatureStatus.Valid, "Valve Corp.");
    private static readonly SignatureInfo MsSigned = new(SignatureStatus.Valid, "Microsoft Windows");
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string Downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    private static readonly string GamePath = Path.Combine(ProgramFiles, @"Steam\steamapps\common\Big Game\game.exe");

    [Fact]
    public void Candidates_AreOnlyUnusualFiles()
    {
        Assert.True(MinerHeuristic.IsCandidate(Path.Combine(ProgramFiles, @"Tool\worker.exe"), Unsigned));
        Assert.True(MinerHeuristic.IsCandidate(Path.Combine(Downloads, "game.exe"), GameSigned)); // risky folder
        Assert.True(MinerHeuristic.IsCandidate(Path.Combine(ProgramFiles, @"Miner\xmrig.exe"), GameSigned)); // known miner name
        Assert.False(MinerHeuristic.IsCandidate(GamePath, GameSigned));
        // Windows unpacks its own tools into Temp (DismHost.exe) and they may be busy for a while.
        Assert.False(MinerHeuristic.IsCandidate(Path.Combine(Path.GetTempPath(), @"0A1B2C3D\DismHost.exe"), MsSigned));
        Assert.False(MinerHeuristic.IsCandidate(null, Unsigned)); // no access to the file
    }

    [Fact]
    public void SustainedLoad_FlagsAnUnsignedProgram()
    {
        var path = Path.Combine(ProgramFiles, @"Tool\worker.exe");
        var calm = ProcessesService.Evaluate(path, Unsigned, null, null);
        Assert.DoesNotContain(MinerHeuristic.Reason, calm.Reasons);
        Assert.False(calm.IsSuspicious); // a missing signature alone is only "Внимание"

        var busy = ProcessesService.Evaluate(path, Unsigned, null, null, sustainedHighCpu: true);
        Assert.Contains(MinerHeuristic.Reason, busy.Reasons);
        Assert.True(busy.IsSuspicious);
    }

    [Fact]
    public void SignedGameUnderLoad_IsNotFlagged()
    {
        var result = ProcessesService.Evaluate(GamePath, GameSigned, null, null, sustainedHighCpu: true);
        Assert.Equal(0, result.Score);
        Assert.DoesNotContain(MinerHeuristic.Reason, result.Reasons);
    }

    [Fact]
    public void Streaks_NeedConsecutiveMeasurementsAndStayForTheLifeOfTheProcess()
    {
        var a = new ProcessKey(1, 10);
        var b = new ProcessKey(2, 20);
        var streaks = new CpuStreaks();

        streaks.Update(new[] { (a, 95.0), (b, 80.0) });
        Assert.False(streaks.IsSustained(a)); // one busy moment is not enough

        streaks.Update(new[] { (a, 90.0), (b, 40.0) });
        Assert.True(streaks.IsSustained(a));
        Assert.False(streaks.IsSustained(b)); // interrupted

        streaks.Update(new[] { (a, 3.0), (b, 85.0) });
        Assert.True(streaks.IsSustained(a)); // a miner pausing while someone looks keeps its mark
        Assert.False(streaks.IsSustained(b)); // b starts over: 80, 40, 85

        streaks.Update(new[] { (b, 85.0) }); // a has exited
        Assert.False(streaks.IsSustained(a));
        Assert.True(streaks.IsSustained(b)); // exactly 80% counts too
    }

    [Fact]
    public void BreakStreaks_StartsCountingOver()
    {
        var a = new ProcessKey(1, 10);
        var streaks = new CpuStreaks();
        streaks.Update(new[] { (a, 95.0) });
        streaks.BreakStreaks(); // long pause since the last measurement
        streaks.Update(new[] { (a, 95.0) });
        Assert.False(streaks.IsSustained(a));
        streaks.Update(new[] { (a, 95.0) });
        Assert.True(streaks.IsSustained(a));
    }

    [Fact]
    public async Task Page_FlagsSustainedLoadOfAnUnsignedProgram_ButNotASignedGame()
    {
        var (service, source, _, clock) = CreateService();
        source.Add(10, "worker.exe", @"C:\Apps\unsigned-worker\worker.exe");
        source.Add(11, "game.exe", @"C:\Games\Big Game\game.exe");
        var vm = new ProcessesViewModel(service) { BaselineDelay = TimeSpan.Zero };
        await vm.RefreshAsync();
        await vm.WaitForBackgroundAsync(CancellationToken.None);

        async Task BusyRefreshAsync()
        {
            // 90% of a 4-core machine for 2 s = 7,2 s of CPU time.
            clock.Advance(TimeSpan.FromSeconds(2));
            source.AddCpu(10, 72_000_000);
            source.AddCpu(11, 72_000_000);
            await vm.RefreshAsync();
            await vm.WaitForBackgroundAsync(CancellationToken.None);
        }

        await BusyRefreshAsync();
        var worker = vm.Rows.Single(r => r.Pid == 10);
        Assert.Equal(90, worker.Cpu, 3);
        Assert.DoesNotContain(MinerHeuristic.Reason, worker.Reasons);
        Assert.False(worker.IsSuspicious);

        await BusyRefreshAsync();
        Assert.Contains(MinerHeuristic.Reason, worker.Reasons);
        Assert.True(worker.IsSuspicious);
        Assert.Equal(1, vm.SuspiciousCount);

        var game = vm.Rows.Single(r => r.Pid == 11);
        Assert.Equal(90, game.Cpu, 3);
        Assert.False(game.IsSuspicious);
        Assert.Empty(game.Reasons);
    }

    [Fact]
    public async Task DashboardAnalysis_MeasuresTwiceAndReportsTheMiner()
    {
        var now = 1_000_000_000L;
        var step = (long)(1.5 * Stopwatch.Frequency);
        var (service, source, _, _) = CreateService(selfPid: 900, memory: () => IssueItems.Ram16,
            analysisInterval: TimeSpan.FromMilliseconds(1), clock: () => now += step);
        source.Add(900, "WaterCa7sPcCleaner.exe", @"C:\Apps\WaterCa7sPcCleaner.exe");
        source.Add(901, "powershell.exe", Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), parent: 900);
        source.Add(20, "worker.exe", @"C:\Apps\unsigned-worker\worker.exe");
        source.Add(21, "game.exe", @"C:\Games\Big Game\game.exe");
        // 95% of 4 cores over 1,5 s = 5,7 s of CPU time per measurement.
        foreach (var pid in new[] { 20, 21, 901 })
            source.CpuPerEnumerate[pid] = 57_000_000;

        var issues = await service.AnalyzeAsync(CancellationToken.None);

        var miner = Assert.Single(issues, i => i.Title == "Подозрительный процесс: worker.exe");
        Assert.Contains(MinerHeuristic.Reason, miner.Details);
        Assert.DoesNotContain(issues, i => i.Title == "Подозрительный процесс: game.exe");
        var gameLoad = Assert.Single(issues, i => i.Title == "Высокая нагрузка на процессор: game.exe");
        Assert.Contains("95%", gameLoad.Details);
        Assert.Equal(IssueSeverity.Low, gameLoad.Severity);
        // Our own PowerShell check is busy too, but it is not the user's problem.
        Assert.DoesNotContain(issues, i => i.Title.Contains("powershell.exe") || i.Details.Contains("powershell.exe"));
    }
}

public class MemoryHogTests
{
    public MemoryHogTests() => TestEnvironment.Init();

    private const long Mb = 1L << 20;

    private static List<ProcessIssues.Evaluated> Copies(int count, string name, string path, long memoryEach, string? description = null)
        => Enumerable.Range(0, count)
            .Select(i => IssueItems.Item(1000 + i, name, path, memory: memoryEach, description: description))
            .ToList();

    [Fact]
    public void OneHeavyProgram_IsALowIssueWithItsShareOfRam()
    {
        var items = new[]
        {
            IssueItems.Item(10, "Photoshop.exe", @"C:\Program Files\Adobe\Photoshop.exe", memory: 5L << 30, description: "Adobe Photoshop"),
            IssueItems.Item(11, "notepad.exe", @"C:\Windows\notepad.exe", memory: 100 * Mb),
        };
        var issue = Assert.Single(ProcessIssues.Build(items, IssueItems.Ram16, selfPid: 1));
        Assert.Equal("Adobe Photoshop занимает 5 ГБ памяти (31% от всей)", issue.Title);
        Assert.Equal(IssueSeverity.Low, issue.Severity);
        Assert.Contains("закройте", issue.Details);
        Assert.Contains("перезапустите", issue.Details);
        Assert.Equal("Processes", issue.TargetPageKey);
        Assert.Null(issue.FixAsync);
    }

    [Fact]
    public void QuarterOfRamOrLess_IsFine()
    {
        var items = new[] { IssueItems.Item(10, "editor.exe", @"C:\Apps\editor.exe", memory: 4L << 30) }; // exactly 25%
        Assert.Empty(ProcessIssues.Build(items, IssueItems.Ram16, selfPid: 1));
    }

    [Fact]
    public void ManyBrowserTabs_AreOneIssueAboutCopies()
    {
        var items = Copies(30, "chrome.exe", ChromePath, 150 * Mb, "Google Chrome"); // 4,39 ГБ = 27%
        var issue = Assert.Single(ProcessIssues.Build(items, IssueItems.Ram16, selfPid: 1));
        Assert.Equal("30 копий chrome.exe занимают 4,39 ГБ", issue.Title);
        Assert.Equal(IssueSeverity.Low, issue.Severity);
        Assert.Contains("Google Chrome", issue.Details);
        Assert.Contains("вкладки", issue.Details);
        Assert.Contains("экономию памяти", issue.Details);
    }

    [Fact]
    public void ManyCopies_UseCorrectRussianGrammar()
    {
        var items = Copies(31, "chrome.exe", ChromePath, 120 * Mb); // 3,63 ГБ = 23%
        var issue = Assert.Single(ProcessIssues.Build(items, IssueItems.Ram16, selfPid: 1));
        Assert.Equal("31 копия chrome.exe занимает 3,63 ГБ", issue.Title);
    }

    [Fact]
    public void ManyCopiesUsingLittleMemory_AreFine()
    {
        var items = Copies(40, "chrome.exe", ChromePath, 75 * Mb); // 2,93 ГБ = 18%
        Assert.Empty(ProcessIssues.Build(items, IssueItems.Ram16, selfPid: 1));
    }

    [Fact]
    public void NonBrowserCopies_GetAdviceForThatProgram()
    {
        var items = Copies(26, "Discord.exe", @"C:\Users\x\AppData\Local\Discord\app-1.0\Discord.exe", 150 * Mb);
        var issue = Assert.Single(ProcessIssues.Build(items, IssueItems.Ram16, selfPid: 1));
        Assert.StartsWith("26 копий Discord.exe занимают", issue.Title);
        Assert.Contains("Закройте лишние окна", issue.Details);
        Assert.DoesNotContain("Браузер запускает", issue.Details);
    }

    [Fact]
    public void WindowsItselfAndTheCleaner_AreNotMemoryHogs()
    {
        var items = Copies(80, "svchost.exe", SvchostPath, 60 * Mb); // 4,7 ГБ = 29%, nothing the user can close
        items.Add(IssueItems.Item(900, "WaterCa7sPcCleaner.exe", @"C:\Apps\WaterCa7sPcCleaner.exe", memory: 6L << 30));
        Assert.Empty(ProcessIssues.Build(items, IssueItems.Ram16, selfPid: 900));
    }
}

public class SuspiciousCardTests
{
    public SuspiciousCardTests() => TestEnvironment.Init();

    [Fact]
    public async Task Card_CountsProcessesAndTheProgramsTheyBelongTo()
    {
        var (service, source, _, clock) = CreateService();
        AddTypicalProcesses(source); // evilupdater.exe (PID 300) is suspicious
        source.Add(301, "evilupdater.exe", EvilPath, parent: 300); // a second process of the same program
        var vm = new ProcessesViewModel(service) { BaselineDelay = TimeSpan.Zero };
        await vm.RefreshAsync();
        await vm.WaitForBackgroundAsync(CancellationToken.None);

        Assert.Equal(2, vm.SuspiciousCount);
        Assert.Equal(1, vm.SuspiciousProgramCount);
        Assert.Equal("2", vm.SuspiciousCountText);
        Assert.Equal("в 1 программе", vm.SuspiciousHint);
        Assert.StartsWith("2 подозрительных процесса в 1 программе.", vm.SuspiciousSummary);

        // What the card promises is what "Показать" shows: one row per program when grouped, one per process when not.
        vm.ShowSuspiciousCommand.Execute(null);
        Assert.Equal(vm.SuspiciousProgramCount, vm.Items.Count);
        vm.GroupByProgram = false;
        Assert.Equal(vm.SuspiciousCount, vm.Items.Count);
        vm.GroupByProgram = true;

        source.Add(302, "unsigned-helper.exe", Path.Combine(Path.GetTempPath(), "unsigned-helper.exe"));
        clock.Advance(TimeSpan.FromSeconds(2));
        await vm.RefreshAsync();
        await vm.WaitForBackgroundAsync(CancellationToken.None);
        Assert.Equal(3, vm.SuspiciousCount);
        Assert.Equal(2, vm.SuspiciousProgramCount);
        Assert.Equal("в 2 программах", vm.SuspiciousHint);
        Assert.StartsWith("3 подозрительных процесса в 2 программах.", vm.SuspiciousSummary);
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public async Task Card_SaysAllIsWellWhenNothingIsSuspicious()
    {
        var (service, source, _, _) = CreateService();
        source.Add(100, "chrome.exe", ChromePath);
        var vm = new ProcessesViewModel(service) { BaselineDelay = TimeSpan.Zero };
        await vm.RefreshAsync();
        await vm.WaitForBackgroundAsync(CancellationToken.None);
        Assert.Equal(0, vm.SuspiciousCount);
        Assert.Equal(0, vm.SuspiciousProgramCount);
        Assert.Equal("ничего подозрительного", vm.SuspiciousHint);
        Assert.False(vm.HasSuspicious);
    }
}
