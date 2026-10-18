using System.IO;
using PcCleaner.Modules.Processes;
using Xunit;
using static PcCleaner.Tests.Processes.ProcessTestData;

namespace PcCleaner.Tests.Processes;

/// <summary>
/// The checkbox column of «Процессы» and «Завершить отмеченные»: what is checked is what gets ended. Nothing is really
/// terminated: the page's kill goes to <see cref="FakeKill"/>, which only records the targets (and, like a real kill,
/// makes them vanish from the fake list). Every test runs on one UI thread with a dispatcher (<see cref="UiThread.Run"/>), like
/// the app: the list updates and the checks are never changed from two threads at once.
/// </summary>
public class ProcessesCheckedKillTests
{
    public ProcessesCheckedKillTests() => TestEnvironment.Init();

    private sealed class FakeKill
    {
        public List<List<KillTarget>> Calls { get; } = new();

        /// <summary>Ended processes disappear from this list (null: the list is left alone, only the report tells).</summary>
        public FakeProcessSource? Source { get; set; }

        /// <summary>Error text for a process that "could not be ended", null for one that was.</summary>
        public Func<KillTarget, string?> FailWith { get; set; } = _ => null;

        public bool AccessDenied { get; set; }

        public KillReport Kill(IReadOnlyList<KillTarget> targets)
        {
            Calls.Add(targets.ToList());
            var report = new KillReport { AccessDenied = AccessDenied };
            foreach (var target in targets)
            {
                if (FailWith(target) is { } error)
                {
                    report.Failed.Add((target, error));
                    continue;
                }
                report.Killed.Add(target);
                Source?.Remove(target.Pid);
            }
            return report;
        }
    }

    private static async Task<(ProcessesViewModel Vm, FakeProcessSource Source, FakeClock Clock, FakeKill Kill)> LoadAsync(
        Action<FakeProcessSource>? addProcesses = null, int? selfPid = null)
    {
        var (service, source, _, clock) = CreateService(selfPid: selfPid);
        (addProcesses ?? AddTypicalProcesses)(source);
        var kill = new FakeKill { Source = source };
        var vm = new ProcessesViewModel(service) { BaselineDelay = TimeSpan.Zero, KillOverride = kill.Kill };
        await vm.RefreshAsync();
        await vm.WaitForBackgroundAsync(CancellationToken.None);
        return (vm, source, clock, kill);
    }

    private static async Task RefreshAsync(ProcessesViewModel vm, FakeClock clock)
    {
        clock.Advance(TimeSpan.FromSeconds(2));
        await vm.RefreshAsync();
        await vm.WaitForBackgroundAsync(CancellationToken.None);
    }

    private static ProcessGroup GroupOf(ProcessesViewModel vm, string exe)
        => vm.Groups.Single(g => string.Equals(g.ExeName, exe, StringComparison.OrdinalIgnoreCase));

    private static ProcessRow RowOf(ProcessesViewModel vm, int pid) => vm.Rows.Single(r => r.Pid == pid);

    /// <summary>Answers "Да" and keeps the question.</summary>
    private static Func<string?> AnswerYes(ProcessesViewModel vm)
    {
        string? question = null;
        vm.Confirm = (_, message) =>
        {
            question = message;
            return true;
        };
        return () => question;
    }

    private static int[] EndedPids(FakeKill kill) => Assert.Single(kill.Calls).Select(t => t.Pid).OrderBy(p => p).ToArray();

    // ---------------------------------------------------------------- checking

    [Fact]
    public void CheckingAProgram_ChecksItsProcesses_AndTheChecksSurviveTheRefresh() => UiThread.Run(async () =>
    {
        var (vm, source, clock, _) = await LoadAsync();
        var chrome = GroupOf(vm, "chrome.exe");
        chrome.IsChecked = true;
        RowOf(vm, 300).IsChecked = true;
        Assert.Equal(4, vm.CheckedCount); // chrome's 3 processes + evilupdater
        Assert.True(RowOf(vm, 101).IsChecked);

        source.AddCpu(300, 5 * TimeSpan.TicksPerSecond); // reorders the list when sorted by CPU
        vm.SortBy("Cpu");
        await RefreshAsync(vm, clock);

        Assert.Same(chrome, GroupOf(vm, "chrome.exe"));
        Assert.True(chrome.IsChecked);
        Assert.True(GroupOf(vm, "evilupdater.exe").IsChecked);
        Assert.Equal(4, vm.CheckedCount);
    });

    [Fact]
    public void ExitedProcesses_DropOutOfTheChecks() => UiThread.Run(async () =>
    {
        var (vm, source, clock, _) = await LoadAsync();
        GroupOf(vm, "chrome.exe").IsChecked = true;
        GroupOf(vm, "evilupdater.exe").IsChecked = true;
        Assert.Equal(4, vm.CheckedCount);

        source.Remove(101);
        source.Remove(300); // the program's only process: the program leaves the list too
        await RefreshAsync(vm, clock);

        Assert.Equal(2, vm.CheckedCount);
        Assert.True(GroupOf(vm, "chrome.exe").IsChecked);
    });

    [Fact]
    public void ProtectedProcesses_CannotBeChecked_AndCheckAllSkipsThem() => UiThread.Run(async () =>
    {
        var (vm, _, _, _) = await LoadAsync();
        var svchost = GroupOf(vm, "svchost.exe");
        var system = GroupOf(vm, "System");
        var chrome = GroupOf(vm, "chrome.exe");
        Assert.False(svchost.CanCheck);
        Assert.False(RowOf(vm, 200).CanCheck);
        Assert.False(system.CanCheck);
        Assert.Equal("Системный процесс Windows — его нельзя завершать", svchost.CheckTooltip);
        Assert.True(chrome.CanCheck);
        Assert.Null(chrome.CheckTooltip);

        svchost.IsChecked = true;
        RowOf(vm, 200).IsChecked = true;
        Assert.False(svchost.IsChecked);
        Assert.Equal(0, vm.CheckedCount);
        Assert.False(vm.AllVisibleChecked);

        chrome.IsChecked = true;
        Assert.Null(vm.AllVisibleChecked); // some of what can be checked

        vm.ToggleAllVisibleCommand.Execute(null);
        Assert.True(vm.AllVisibleChecked);
        Assert.Equal(4, vm.CheckedCount); // chrome's 3 and evilupdater
        Assert.False(svchost.IsChecked);
        Assert.False(system.IsChecked);

        vm.ToggleAllVisibleCommand.Execute(null);
        Assert.False(vm.AllVisibleChecked);
        Assert.Equal(0, vm.CheckedCount);
    });

    [Fact]
    public void TheCleanerItself_CannotBeChecked() => UiThread.Run(async () =>
    {
        var (vm, _, _, _) = await LoadAsync(source =>
        {
            source.Add(900, "PcCleaner.exe", @"C:\Apps\PcCleaner\PcCleaner.exe");
            source.Add(901, "notepad.exe", @"C:\Apps\notepad.exe");
        }, selfPid: 900);

        var self = RowOf(vm, 900);
        Assert.False(self.CanCheck);
        Assert.Equal(ProcessGuard.SelfText, self.CheckTooltip);
        Assert.True(RowOf(vm, 901).CanCheck);
    });

    [Fact]
    public void CheckAll_TakesTheProcessesBehindTheVisibleRows() => UiThread.Run(async () =>
    {
        var (vm, _, _, _) = await LoadAsync();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SearchText = "chrome";
        Assert.Contains(nameof(ProcessesViewModel.AllVisibleChecked), raised);
        vm.ToggleAllVisibleCommand.Execute(null);
        Assert.True(vm.AllVisibleChecked);
        Assert.Equal(3, vm.CheckedCount); // the collapsed program stands for its 3 processes

        vm.SearchText = "";
        Assert.Null(vm.AllVisibleChecked);
        Assert.True(GroupOf(vm, "chrome.exe").IsChecked);
        Assert.False(GroupOf(vm, "evilupdater.exe").IsChecked);

        vm.ToggleAllVisibleCommand.Execute(null); // "some" checks the rest
        Assert.True(vm.AllVisibleChecked);
        Assert.Equal(4, vm.CheckedCount);
    });

    [Fact]
    public void ClearChecks_UnchecksEverything_AndTellsTheRows() => UiThread.Run(async () =>
    {
        var (vm, _, _, _) = await LoadAsync();
        var chrome = GroupOf(vm, "chrome.exe");
        chrome.IsChecked = true;
        var rowChanges = new List<string?>();
        var vmChanges = new List<string?>();
        chrome.PropertyChanged += (_, e) => rowChanges.Add(e.PropertyName);
        vm.PropertyChanged += (_, e) => vmChanges.Add(e.PropertyName);

        vm.ClearChecksCommand.Execute(null);

        Assert.False(chrome.IsChecked);
        Assert.Equal(0, vm.CheckedCount);
        Assert.Contains(nameof(ProcessItem.IsChecked), rowChanges);
        Assert.Contains(nameof(ProcessesViewModel.CheckedCount), vmChanges);
        Assert.Contains(nameof(ProcessesViewModel.AllVisibleChecked), vmChanges);
    });

    [Fact]
    public void CheckedProgram_ShowsItsProcessesChecked_WhenGroupingIsOff() => UiThread.Run(async () =>
    {
        var (vm, _, _, kill) = await LoadAsync();
        GroupOf(vm, "chrome.exe").IsChecked = true;

        vm.GroupByProgram = false;
        Assert.All(vm.Items.Where(i => i.ExeName == "chrome.exe"), i => Assert.True(i.IsChecked));
        Assert.All(vm.Items.Where(i => i.ExeName != "chrome.exe"), i => Assert.False(i.IsChecked));
        Assert.True(vm.AllVisibleChecked is null);

        var question = AnswerYes(vm);
        await vm.KillCheckedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { 100, 101, 102 }, EndedPids(kill));
        Assert.DoesNotContain("Не видны в списке сейчас", question());
    });

    [Fact]
    public void CollapsedProgram_WithOneCheckedProcess_IsIndeterminate_AndOnlyThatProcessIsEnded() => UiThread.Run(async () =>
    {
        var (vm, _, _, kill) = await LoadAsync();
        var chrome = GroupOf(vm, "chrome.exe");
        RowOf(vm, 101).IsChecked = true;
        Assert.False(chrome.IsExpanded);
        Assert.Null(chrome.IsChecked);

        var question = AnswerYes(vm);
        await vm.KillCheckedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { 101 }, EndedPids(kill));
        // Inside a collapsed program the process is not in the list: it is named on its own, with its PID.
        Assert.Contains("Не видны в списке сейчас:", question());
        Assert.Contains("PID 101", question());
    });

    [Fact]
    public void ExpandedCheckedProgram_ShowsItsProcessesChecked_AndUncheckingOneLeavesItOut() => UiThread.Run(async () =>
    {
        var (vm, _, _, kill) = await LoadAsync();
        var chrome = GroupOf(vm, "chrome.exe");
        chrome.IsChecked = true;
        vm.ToggleExpand(chrome);
        var children = vm.Items.Where(i => i.IsChild).Cast<ProcessRow>().ToList();
        Assert.Equal(3, children.Count);
        Assert.All(children, c => Assert.True(c.IsChecked));

        var changes = new List<string?>();
        chrome.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        vm.ToggleCheckCommand.Execute(RowOf(vm, 101)); // the row's own checkbox
        Assert.False(RowOf(vm, 101).IsChecked);
        Assert.Null(chrome.IsChecked);
        Assert.Contains(nameof(ProcessItem.IsChecked), changes);

        // Clicking the indeterminate program box checks all of it again, a second click clears it.
        vm.ToggleCheckCommand.Execute(chrome);
        Assert.True(chrome.IsChecked);
        vm.ToggleCheckCommand.Execute(chrome);
        Assert.False(chrome.IsChecked);
        Assert.Equal(0, vm.CheckedCount);

        chrome.IsChecked = true;
        RowOf(vm, 101).IsChecked = false;
        var question = AnswerYes(vm);
        await vm.KillCheckedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { 100, 102 }, EndedPids(kill));
        Assert.DoesNotContain("Не видны в списке сейчас", question());
    });

    [Fact]
    public void AProcessThatJoinsACheckedProgramLater_IsNotEnded() => UiThread.Run(async () =>
    {
        var (vm, source, clock, kill) = await LoadAsync();
        var chrome = GroupOf(vm, "chrome.exe");
        chrome.IsChecked = true;

        source.Add(103, "chrome.exe", ChromePath, parent: 100);
        await RefreshAsync(vm, clock);
        Assert.Equal(4, chrome.Count);
        Assert.Null(chrome.IsChecked);
        Assert.Equal(3, vm.CheckedCount);

        AnswerYes(vm);
        await vm.KillCheckedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { 100, 101, 102 }, EndedPids(kill));
    });

    [Fact]
    public void AProgramThatBecomesProtected_LosesItsChecks() => UiThread.Run(async () =>
    {
        var fake = Path.Combine(Path.GetTempPath(), "svchost.exe"); // not the Windows one: may be ended
        var (vm, source, clock, kill) = await LoadAsync(s =>
        {
            s.Add(700, "svchost.exe", fake);
            s.Add(710, "notepad.exe", @"C:\Apps\notepad.exe");
        });
        var svchost = GroupOf(vm, "svchost.exe");
        Assert.True(svchost.CanCheck);
        svchost.IsChecked = true;
        GroupOf(vm, "notepad.exe").IsChecked = true;
        Assert.Equal(2, vm.CheckedCount);

        // An svchost.exe whose file can't be read joins the only svchost.exe program and makes it protected.
        source.Add(701, "svchost.exe", null);
        await RefreshAsync(vm, clock);
        Assert.Same(svchost, GroupOf(vm, "svchost.exe"));
        Assert.Equal(2, svchost.Count);
        Assert.False(svchost.CanCheck);
        Assert.False(RowOf(vm, 700).CanCheck); // no check can hide behind the program's disabled box
        Assert.Equal("Входит в программу с защищённым процессом — отметить нельзя; завершить его можно кнопкой «Завершить»",
            RowOf(vm, 700).CheckTooltip);
        Assert.False(svchost.IsChecked);
        Assert.False(RowOf(vm, 700).IsChecked);
        Assert.Equal(1, vm.CheckedCount);

        AnswerYes(vm);
        await vm.KillCheckedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { 710 }, EndedPids(kill));
        Assert.Contains(source.Enumerate(), p => p.Pid == 700);
    });

    // ---------------------------------------------------------------- "Завершить отмеченные"

    [Fact]
    public void KillChecked_Declined_EndsNothing() => UiThread.Run(async () =>
    {
        var (vm, source, _, kill) = await LoadAsync();
        GroupOf(vm, "chrome.exe").IsChecked = true;
        string? question = null;
        vm.Confirm = (_, message) =>
        {
            question = message;
            return false;
        };

        await vm.KillCheckedCommand.ExecuteAsync(null);

        Assert.NotNull(question);
        Assert.Empty(kill.Calls);
        Assert.Equal(3, vm.CheckedCount);
        Assert.Contains(source.Enumerate(), p => p.Pid == 100);
    });

    [Fact]
    public void KillChecked_Confirmed_EndsEveryCheckedProcessOnce() => UiThread.Run(async () =>
    {
        var (vm, _, _, kill) = await LoadAsync();
        GroupOf(vm, "chrome.exe").IsChecked = true; // 100, 101, 102
        RowOf(vm, 101).IsChecked = true; // already checked with its program
        RowOf(vm, 300).IsChecked = true;
        var expected = vm.Rows.Where(r => r.Pid is 100 or 101 or 102 or 300)
            .Select(r => (r.Pid, r.CreateTime)).OrderBy(k => k.Pid).ToList();
        string? title = null;
        string? question = null;
        vm.Confirm = (t, message) =>
        {
            title = t;
            question = message;
            return true;
        };

        await vm.KillCheckedCommand.ExecuteAsync(null);

        var targets = Assert.Single(kill.Calls);
        Assert.Equal(expected, targets.Select(t => (t.Pid, t.CreateTime)).OrderBy(k => k.Pid));
        Assert.Equal("Завершить отмеченные процессы?", title);
        Assert.Contains("Google Chrome (chrome.exe) — 3 процесса (PID 100, 101, 102)", question);
        Assert.Contains("evilupdater.exe, PID 300", question);
        Assert.DoesNotContain("Не видны в списке сейчас", question);
        Assert.Contains("Несохранённые данные", question);
        Assert.DoesNotContain(ProcessGuard.ExplorerText, question);
        Assert.EndsWith("Продолжить?", question);

        // Ended processes leave the checks, and the list is refreshed right away.
        Assert.Equal(0, vm.CheckedCount);
        Assert.DoesNotContain(vm.Rows, r => r.Pid is 100 or 101 or 102 or 300);
    });

    [Fact]
    public void KillChecked_KeepsTheChecksOfProcessesThatCouldNotBeEnded() => UiThread.Run(async () =>
    {
        var (vm, _, _, kill) = await LoadAsync();
        kill.Source = null; // the rows stay in the list: only the report says what was ended
        kill.FailWith = t => t.Pid == 300 ? "нет доступа" : null;
        GroupOf(vm, "chrome.exe").IsChecked = true;
        GroupOf(vm, "evilupdater.exe").IsChecked = true;
        AnswerYes(vm);

        await vm.KillCheckedCommand.ExecuteAsync(null);

        Assert.Equal(4, Assert.Single(kill.Calls).Count);
        Assert.False(GroupOf(vm, "chrome.exe").IsChecked);
        Assert.True(GroupOf(vm, "evilupdater.exe").IsChecked);
        Assert.Equal(1, vm.CheckedCount);
    });

    [Fact]
    public void KillChecked_WarnsAboutExplorer() => UiThread.Run(async () =>
    {
        var (vm, _, _, kill) = await LoadAsync(source =>
        {
            source.Add(800, "explorer.exe", Path.Combine(WindowsDir, "explorer.exe"));
            source.Add(801, "notepad.exe", @"C:\Apps\notepad.exe");
        });
        var explorer = RowOf(vm, 800);
        Assert.True(explorer.CanCheck);
        explorer.IsChecked = true;
        RowOf(vm, 801).IsChecked = true;
        string? question = null;
        vm.Confirm = (_, message) =>
        {
            question = message;
            return false;
        };

        await vm.KillCheckedCommand.ExecuteAsync(null);

        Assert.Contains(ProcessGuard.ExplorerText, question);
        Assert.Empty(kill.Calls);
    });

    [Fact]
    public void KillChecked_ListsHiddenChecksFirst_AndNeverFoldsThem() => UiThread.Run(async () =>
    {
        var (vm, _, _, kill) = await LoadAsync(source =>
        {
            for (var i = 1; i <= 15; i++)
            {
                source.Add(900 + i, $"tool{i:00}.exe", $@"C:\Apps\tool{i:00}.exe");
                source.Add(950 + i, $"other{i:00}.exe", $@"C:\Apps\other{i:00}.exe");
            }
        });
        vm.ToggleAllVisibleCommand.Execute(null);
        Assert.Equal(30, vm.CheckedCount);
        vm.SearchText = "tool"; // the "other" ones are checked but not in the list any more

        var question = AnswerYes(vm);
        await vm.KillCheckedCommand.ExecuteAsync(null);

        Assert.Equal(30, Assert.Single(kill.Calls).Count);
        var text = question()!;
        var hidden = text.IndexOf("Не видны в списке сейчас:", StringComparison.Ordinal);
        Assert.True(hidden >= 0, text);
        for (var i = 1; i <= 15; i++)
            Assert.Contains($"other{i:00}.exe, PID {950 + i}", text);
        Assert.True(hidden < text.IndexOf("tool01.exe", StringComparison.Ordinal), "the hidden part comes first");
        Assert.True(text.IndexOf("other15.exe", StringComparison.Ordinal) < text.IndexOf("tool01.exe", StringComparison.Ordinal));
        Assert.Contains("…и ещё 1", text); // only the visible part (15 lines) is folded
        Assert.Single(text.Split('\n'), l => l.StartsWith("…и ещё", StringComparison.Ordinal));
    });

    [Fact]
    public void KillChecked_EndsTheProcessesInTheOrderTheQuestionNamesThem_HiddenOnesFirst() => UiThread.Run(async () =>
    {
        var (vm, _, _, kill) = await LoadAsync(source =>
        {
            source.Add(911, "tool1.exe", @"C:\Apps\tool1.exe");
            source.Add(912, "tool2.exe", @"C:\Apps\tool2.exe");
            source.Add(962, "zeta2.exe", @"C:\Apps\zeta2.exe");
            source.Add(961, "zeta1.exe", @"C:\Apps\zeta1.exe");
        });
        vm.ToggleAllVisibleCommand.Execute(null);
        vm.SearchText = "tool";

        var question = AnswerYes(vm);
        await vm.KillCheckedCommand.ExecuteAsync(null);

        var pids = Assert.Single(kill.Calls).Select(t => t.Pid).ToList();
        Assert.Equal(new[] { 961, 962 }, pids.Take(2)); // the hidden ones, as the question names them
        Assert.Equal(new[] { 911, 912 }.OrderBy(p => p), pids.Skip(2).OrderBy(p => p));
        var text = question()!;
        var named = pids.Select(pid => text.IndexOf($"PID {pid}", StringComparison.Ordinal)).ToList();
        Assert.All(named, i => Assert.True(i >= 0, text));
        Assert.Equal(named.OrderBy(i => i), named); // the same order as in the question
    });

    [Fact]
    public void KillChecked_NothingEndedForLackOfRights_OffersTheAdminRestart() => UiThread.Run(async () =>
    {
        var (vm, _, _, kill) = await LoadAsync();
        kill.FailWith = _ => "нет доступа";
        kill.AccessDenied = true;
        string? elevation = null;
        vm.IsElevated = () => false;
        vm.RequireElevation = reason =>
        {
            elevation = reason;
            return false;
        };
        GroupOf(vm, "evilupdater.exe").IsChecked = true;
        AnswerYes(vm);

        await vm.KillCheckedCommand.ExecuteAsync(null);

        Assert.Equal("Завершение отмеченных процессов", elevation);
        Assert.Equal(1, vm.CheckedCount); // still checked: nothing was ended
    });

    [Fact]
    public void KillChecked_OnlyProtectedChecks_ShowsWhy_AndEndsNothing() => UiThread.Run(async () =>
    {
        // Protection changed after the last list update (the next one would drop the check by itself).
        var (vm, _, _, kill) = await LoadAsync();
        vm.Checked.Set(RowOf(vm, 200).Key, true);
        var asked = false;
        vm.Confirm = (_, _) => asked = true;
        (string Title, string Text)? info = null;
        vm.ShowInfo = (title, text) => info = (title, text);

        await vm.KillCheckedCommand.ExecuteAsync(null);

        Assert.False(asked);
        Assert.Empty(kill.Calls);
        Assert.NotNull(info);
        Assert.Contains("svchost.exe", info!.Value.Text);
        Assert.Equal(0, vm.CheckedCount);
    });
}
