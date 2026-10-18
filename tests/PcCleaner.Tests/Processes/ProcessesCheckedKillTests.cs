using System.IO;
using PcCleaner.Modules.Processes;
using Xunit;
using static PcCleaner.Tests.Processes.ProcessTestData;

namespace PcCleaner.Tests.Processes;

/// <summary>
/// The checkbox column of «Процессы» and «Завершить отмеченные». Nothing is really terminated: the page's kill goes to
/// <see cref="FakeKill"/>, which only records the targets (and, like a real kill, makes them vanish from the fake list).
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

        public KillReport Kill(IReadOnlyList<KillTarget> targets)
        {
            Calls.Add(targets.ToList());
            var report = new KillReport();
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

    [Fact]
    public async Task Checks_SurviveTheRefresh()
    {
        var (vm, source, clock, _) = await LoadAsync();
        var chrome = GroupOf(vm, "chrome.exe");
        chrome.IsChecked = true;
        RowOf(vm, 300).IsChecked = true;
        Assert.Equal(2, vm.CheckedCount);

        source.AddCpu(300, 5 * TimeSpan.TicksPerSecond); // reorders the list when sorted by CPU
        vm.SortBy("Cpu");
        await RefreshAsync(vm, clock);

        Assert.Same(chrome, GroupOf(vm, "chrome.exe"));
        Assert.True(chrome.IsChecked);
        Assert.True(RowOf(vm, 300).IsChecked);
        Assert.False(RowOf(vm, 100).IsChecked); // a checked program does not check its processes one by one
        Assert.Equal(2, vm.CheckedCount);
    }

    [Fact]
    public async Task ExitedProcesses_DropOutOfTheChecks()
    {
        var (vm, source, clock, _) = await LoadAsync();
        RowOf(vm, 100).IsChecked = true;
        RowOf(vm, 101).IsChecked = true;
        GroupOf(vm, "evilupdater.exe").IsChecked = true;
        Assert.Equal(3, vm.CheckedCount);

        source.Remove(101);
        source.Remove(300); // the program's only process: the program leaves the list too
        await RefreshAsync(vm, clock);

        Assert.Equal(1, vm.CheckedCount);
        Assert.True(RowOf(vm, 100).IsChecked);
    }

    [Fact]
    public async Task ProtectedProcesses_CannotBeChecked_AndCheckAllSkipsThem()
    {
        var (vm, _, _, _) = await LoadAsync();
        var svchost = GroupOf(vm, "svchost.exe");
        var system = GroupOf(vm, "System");
        var chrome = GroupOf(vm, "chrome.exe");
        Assert.False(svchost.CanCheck);
        Assert.False(system.CanCheck);
        Assert.Equal("Системный процесс Windows — его нельзя завершать", svchost.CheckTooltip);
        Assert.True(chrome.CanCheck);
        Assert.Null(chrome.CheckTooltip);

        svchost.IsChecked = true;
        Assert.False(svchost.IsChecked);
        Assert.Equal(0, vm.CheckedCount);
        Assert.False(vm.AllVisibleChecked);

        chrome.IsChecked = true;
        Assert.Null(vm.AllVisibleChecked); // some of the rows that can be checked

        vm.ToggleAllVisibleCommand.Execute(null);
        Assert.True(vm.AllVisibleChecked);
        Assert.Equal(2, vm.CheckedCount); // chrome and evilupdater
        Assert.False(svchost.IsChecked);
        Assert.False(system.IsChecked);

        vm.ToggleAllVisibleCommand.Execute(null);
        Assert.False(vm.AllVisibleChecked);
        Assert.Equal(0, vm.CheckedCount);
    }

    [Fact]
    public async Task TheCleanerItself_CannotBeChecked()
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
    }

    [Fact]
    public async Task CheckAll_TakesOnlyTheRowsTheSearchShows()
    {
        var (vm, _, _, _) = await LoadAsync();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SearchText = "chrome";
        Assert.Contains(nameof(ProcessesViewModel.AllVisibleChecked), raised);
        vm.ToggleAllVisibleCommand.Execute(null);
        Assert.True(vm.AllVisibleChecked);
        Assert.Equal(1, vm.CheckedCount);

        vm.SearchText = "";
        Assert.Null(vm.AllVisibleChecked);
        Assert.True(GroupOf(vm, "chrome.exe").IsChecked);
        Assert.False(GroupOf(vm, "evilupdater.exe").IsChecked);

        vm.ToggleAllVisibleCommand.Execute(null); // "some" checks the rest
        Assert.True(vm.AllVisibleChecked);
        Assert.Equal(2, vm.CheckedCount);
    }

    [Fact]
    public async Task ClearChecks_UnchecksEverything_AndTellsTheRows()
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
    }

    [Fact]
    public async Task KillChecked_Declined_EndsNothing()
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
        Assert.Equal(1, vm.CheckedCount);
        Assert.Contains(source.Enumerate(), p => p.Pid == 100);
    }

    [Fact]
    public async Task KillChecked_Confirmed_EndsEveryCheckedProcessOnce()
    {
        var (vm, _, _, kill) = await LoadAsync();
        GroupOf(vm, "chrome.exe").IsChecked = true; // 100, 101, 102
        RowOf(vm, 101).IsChecked = true; // also in the checked program
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
        Assert.Contains("Google Chrome", question);
        Assert.Contains("evilupdater.exe", question);
        Assert.Contains("Несохранённые данные", question);
        Assert.DoesNotContain(ProcessGuard.ExplorerText, question);
        Assert.EndsWith("Продолжить?", question);

        // Ended processes leave the checks, and the list is refreshed right away.
        Assert.Equal(0, vm.CheckedCount);
        Assert.DoesNotContain(vm.Rows, r => r.Pid is 100 or 101 or 102 or 300);
    }

    [Fact]
    public async Task KillChecked_KeepsTheChecksOfProcessesThatCouldNotBeEnded()
    {
        var (vm, _, _, kill) = await LoadAsync();
        kill.Source = null; // the rows stay in the list: only the report says what was ended
        kill.FailWith = t => t.Pid == 300 ? "нет доступа" : null;
        GroupOf(vm, "chrome.exe").IsChecked = true;
        GroupOf(vm, "evilupdater.exe").IsChecked = true;
        vm.Confirm = (_, _) => true;

        await vm.KillCheckedCommand.ExecuteAsync(null);

        Assert.Equal(4, Assert.Single(kill.Calls).Count);
        Assert.False(GroupOf(vm, "chrome.exe").IsChecked);
        Assert.True(GroupOf(vm, "evilupdater.exe").IsChecked);
        Assert.Equal(1, vm.CheckedCount);
    }

    [Fact]
    public async Task KillChecked_WarnsAboutExplorer()
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
    }

    [Fact]
    public async Task KillChecked_SkipsAProgramThatBecameProtectedAfterItWasChecked()
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

        // An svchost.exe whose file can't be read joins the only svchost.exe program and makes it protected.
        source.Add(701, "svchost.exe", null);
        await RefreshAsync(vm, clock);
        Assert.Same(svchost, GroupOf(vm, "svchost.exe"));
        Assert.Equal(2, svchost.Count);
        Assert.False(svchost.CanCheck);

        string? question = null;
        vm.Confirm = (_, message) =>
        {
            question = message;
            return true;
        };
        await vm.KillCheckedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { 710 }, Assert.Single(kill.Calls).Select(t => t.Pid));
        Assert.Contains("затронуты не будут", question);
        Assert.Contains("svchost.exe", question);
        Assert.Contains(source.Enumerate(), p => p.Pid == 700);
    }
}
