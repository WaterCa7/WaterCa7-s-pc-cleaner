using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Processes;
using Xunit;

namespace PcCleaner.Tests.Processes;

public class CpuMathTests
{
    [Fact]
    public void OneSecondOfCpuOverTwoSecondsOnFourCores_Is12Point5Percent()
        => Assert.Equal(12.5, ProcessMath.CpuPercent(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 4), 6);

    [Fact]
    public void FullyLoadedSingleCoreMachine_Is100Percent()
        => Assert.Equal(100, ProcessMath.CpuPercent(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(2), 1), 6);

    [Theory]
    [InlineData(0, 0, 2.0, 4)] // no work
    [InlineData(5_000_000, 1_000_000, 2.0, 4)] // counter went backwards
    [InlineData(0, 10_000_000, 0.0, 4)] // no time elapsed
    [InlineData(0, 10_000_000, 2.0, 0)] // no CPUs reported
    public void DegenerateInputs_GiveZero(long previous, long current, double elapsed, int cpus)
        => Assert.Equal(0, ProcessMath.CpuPercent(previous, current, elapsed, cpus));

    [Fact]
    public void ImpossibleValues_AreClampedTo100()
        => Assert.Equal(100, ProcessMath.CpuPercent(0, 100 * TimeSpan.TicksPerSecond, 1.0, 2));

    [Theory]
    [InlineData(0.0, "0%")]
    [InlineData(0.04, "0%")]
    [InlineData(0.3, "0,3%")]
    [InlineData(12.3, "12,3%")]
    public void CpuText_IsRussianPercent(double value, string expected) => Assert.Equal(expected, ProcessMath.CpuText(value));

    [Fact]
    public void Sampler_ComputesPerProcessCpuBetweenSnapshots()
    {
        var sampler = new CpuSampler();
        var t0 = 1_000_000L;
        var first = new List<RawProcess>
        {
            new(10, 0, "a.exe", 111, 0, 1, 1, 1),
            new(11, 0, "b.exe", 222, 5 * TimeSpan.TicksPerSecond, 1, 1, 1),
        };
        var initial = sampler.Sample(first, t0, 2);
        Assert.All(initial, v => Assert.Equal(0, v)); // no baseline yet

        var t1 = t0 + 2 * Stopwatch.Frequency; // two seconds later
        var second = new List<RawProcess>
        {
            new(10, 0, "a.exe", 111, 2 * TimeSpan.TicksPerSecond, 1, 1, 1), // 2 s of CPU in 2 s on 2 cores = 50%
            new(11, 0, "b.exe", 222, 5 * TimeSpan.TicksPerSecond, 1, 1, 1), // idle
            new(12, 0, "new.exe", 333, TimeSpan.TicksPerSecond, 1, 1, 1), // new process: no baseline
        };
        var cpu = sampler.Sample(second, t1, 2);
        Assert.Equal(50, cpu[0], 6);
        Assert.Equal(0, cpu[1]);
        Assert.Equal(0, cpu[2]);
    }

    [Fact]
    public void Sampler_ReusedPidWithNewStartTime_IsANewProcess()
    {
        var sampler = new CpuSampler();
        sampler.Sample(new List<RawProcess> { new(10, 0, "a.exe", 111, 100 * TimeSpan.TicksPerSecond, 1, 1, 1) }, 1, 1);
        var cpu = sampler.Sample(new List<RawProcess> { new(10, 0, "b.exe", 999, TimeSpan.TicksPerSecond, 1, 1, 1) },
            1 + Stopwatch.Frequency, 1);
        Assert.Equal(0, cpu[0]);
    }

    [Fact]
    public void Sampler_BaselineGoesStale()
    {
        var sampler = new CpuSampler();
        Assert.False(sampler.HasFreshBaseline(100));
        sampler.Sample(new List<RawProcess>(), 100, 1);
        Assert.True(sampler.HasFreshBaseline(100 + Stopwatch.Frequency));
        Assert.False(sampler.HasFreshBaseline(100 + 60 * Stopwatch.Frequency));
    }
}

public class ProcessGuardTests
{
    private static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string Sys32 = Path.Combine(Win, "System32");

    [Theory]
    [InlineData("svchost.exe")]
    [InlineData("csrss.exe")]
    [InlineData("lsass.exe")]
    [InlineData("LsaIso.exe")]
    [InlineData("winlogon.exe")]
    [InlineData("wininit.exe")]
    [InlineData("smss.exe")]
    [InlineData("services.exe")]
    [InlineData("dwm.exe")]
    [InlineData("fontdrvhost.exe")]
    [InlineData("SecurityHealthService.exe")]
    public void CriticalWindowsProcesses_AreBlocked(string name)
    {
        Assert.Equal(KillVerdict.Blocked, ProcessGuard.Check(1234, name, Path.Combine(Sys32, name), selfPid: 1).Verdict);
        // Without admin rights the path is often unknown — still protected.
        Assert.Equal(KillVerdict.Blocked, ProcessGuard.Check(1234, name, null, selfPid: 1).Verdict);
    }

    [Theory]
    [InlineData(0, "Idle")]
    [InlineData(4, "System")]
    [InlineData(150, "Registry")]
    [InlineData(2100, "Memory Compression")]
    [InlineData(160, "Secure System")]
    public void KernelPseudoProcesses_AreBlocked(int pid, string name)
        => Assert.Equal(KillVerdict.Blocked, ProcessGuard.Check(pid, name, null, selfPid: 1).Verdict);

    [Fact]
    public void DefenderInProgramData_IsBlocked()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Microsoft\Windows Defender\Platform\4.18.1\MsMpEng.exe");
        Assert.Equal(KillVerdict.Blocked, ProcessGuard.Check(5000, "MsMpEng.exe", path, selfPid: 1).Verdict);
        Assert.Equal(KillVerdict.Blocked, ProcessGuard.Check(5001, "NisSrv.exe", null, selfPid: 1).Verdict);
    }

    [Fact]
    public void FakeSvchostInAppData_CanBeKilled()
    {
        var fake = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "svchost.exe");
        Assert.Equal(KillVerdict.Allowed, ProcessGuard.Check(6000, "svchost.exe", fake, selfPid: 1).Verdict);
    }

    [Fact]
    public void Explorer_IsAllowedWithWarning()
    {
        var (verdict, reason) = ProcessGuard.Check(7000, "explorer.exe", Path.Combine(Win, "explorer.exe"), selfPid: 1);
        Assert.Equal(KillVerdict.Warn, verdict);
        Assert.Contains("панель задач", reason);
    }

    [Fact]
    public void TheCleanerItself_IsBlocked()
    {
        var (verdict, reason) = ProcessGuard.Check(Environment.ProcessId, "WaterCa7sPcCleaner.exe", Environment.ProcessPath);
        Assert.Equal(KillVerdict.Blocked, verdict);
        Assert.Equal(ProcessGuard.SelfText, reason);
    }

    [Fact]
    public void OrdinaryProgram_IsAllowed()
        => Assert.Equal(KillVerdict.Allowed, ProcessGuard.Check(8000, "notepad.exe", Path.Combine(Sys32, "notepad.exe"), 1).Verdict);

    [Fact]
    public void Worst_PicksBlockedOverWarn()
        => Assert.Equal(KillVerdict.Blocked, ProcessGuard.Worst(new[] { KillVerdict.Allowed, KillVerdict.Warn, KillVerdict.Blocked }));
}

public class ProcessGroupingTests
{
    public ProcessGroupingTests() => TestEnvironment.Init();

    private sealed record Item(string Name, string? Path);

    [Fact]
    public void SamePath_SameGroup_DifferentPaths_DifferentGroups()
    {
        var a = new Item("app.exe", @"C:\A\app.exe");
        var b = new Item("app.exe", @"c:\a\APP.exe");
        var c = new Item("app.exe", @"C:\B\app.exe");
        var keys = ProcessGrouping.AssignKeys(new[] { a, b, c }, i => i.Name, i => i.Path);
        Assert.Equal(keys[a], keys[b]);
        Assert.NotEqual(keys[a], keys[c]);
    }

    [Fact]
    public void InaccessibleProcess_JoinsTheOnlyGroupWithTheSameFileName()
    {
        var withPath = new Item("svchost.exe", ProcessTestData.SvchostPath);
        var noPath = new Item("svchost.exe", null);
        var keys = ProcessGrouping.AssignKeys(new[] { withPath, noPath }, i => i.Name, i => i.Path);
        Assert.Equal(keys[withPath], keys[noPath]);
    }

    [Fact]
    public void InaccessibleProcess_StaysAloneWhenTheFileNameIsAmbiguous()
    {
        var a = new Item("app.exe", @"C:\A\app.exe");
        var b = new Item("app.exe", @"C:\B\app.exe");
        var noPath = new Item("app.exe", null);
        var keys = ProcessGrouping.AssignKeys(new[] { a, b, noPath }, i => i.Name, i => i.Path);
        Assert.NotEqual(keys[a], keys[noPath]);
        Assert.NotEqual(keys[b], keys[noPath]);
    }

    [Fact]
    public void Aggregate_SumsCpuAndMemory()
    {
        var rows = new[]
        {
            ProcessTestData.Row(1, "chrome.exe", ProcessTestData.ChromePath),
            ProcessTestData.Row(2, "chrome.exe", ProcessTestData.ChromePath),
            ProcessTestData.Row(3, "chrome.exe", ProcessTestData.ChromePath),
        };
        rows[0].Cpu = 10;
        rows[1].Cpu = 2.5;
        rows[2].Cpu = 0;
        rows[0].Memory = 100;
        rows[1].Memory = 200;
        rows[2].Memory = 300;
        var (cpu, memory, count) = ProcessGrouping.Aggregate(rows);
        Assert.Equal(12.5, cpu, 6);
        Assert.Equal(600, memory);
        Assert.Equal(3, count);
    }

    [Fact]
    public void Combine_TakesHighestScoreAndAllReasons()
    {
        var combined = ProcessGrouping.Combine(new SuspicionResult?[]
        {
            SuspicionResult.Clean,
            new SuspicionResult(40, new[] { "A" }),
            new SuspicionResult(70, new[] { "B", "A" }),
            null,
        });
        Assert.NotNull(combined);
        Assert.Equal(70, combined!.Score);
        Assert.Equal(new[] { "A", "B" }, combined.Reasons);
        Assert.Null(ProcessGrouping.Combine(new SuspicionResult?[] { null }));
        Assert.Same(SuspicionResult.Clean, ProcessGrouping.Combine(new SuspicionResult?[] { SuspicionResult.Clean }));
    }
}

public class CollectionSyncTests
{
    private sealed class Box
    {
        public Box(int value) => Value = value;

        public int Value { get; }

        public override string ToString() => Value.ToString();
    }

    private static (ObservableCollection<Box> Target, List<Box> Boxes, List<string> Log) Make(int count)
    {
        var boxes = Enumerable.Range(0, count).Select(i => new Box(i)).ToList();
        var target = new ObservableCollection<Box>(boxes);
        var log = new List<string>();
        target.CollectionChanged += (_, e) => log.Add(e.Action.ToString());
        return (target, boxes, log);
    }

    [Fact]
    public void MovingOneItemToTheTop_IsASingleMove()
    {
        var (target, boxes, log) = Make(10);
        var desired = new List<Box> { boxes[9] };
        desired.AddRange(boxes.Take(9));
        CollectionSync.SyncTo(target, desired);
        Assert.Equal(desired, target);
        Assert.Equal(new[] { "Move" }, log);
    }

    [Fact]
    public void MovingOneItemToTheBottom_IsASingleMove()
    {
        var (target, boxes, log) = Make(10);
        var desired = boxes.Skip(1).Append(boxes[0]).ToList();
        CollectionSync.SyncTo(target, desired);
        Assert.Equal(desired, target);
        Assert.Single(log);
    }

    [Fact]
    public void UnchangedList_RaisesNoEvents()
    {
        var (target, boxes, log) = Make(5);
        CollectionSync.SyncTo(target, boxes.ToList());
        Assert.Empty(log);
    }

    [Fact]
    public void AddsAndRemoves_KeepSameObjects()
    {
        var (target, boxes, _) = Make(5);
        var extra = new Box(99);
        var desired = new List<Box> { boxes[4], extra, boxes[0], boxes[2] };
        CollectionSync.SyncTo(target, desired);
        Assert.Equal(desired, target);
        Assert.Same(boxes[4], target[0]);
    }

    [Fact]
    public void RandomPermutations_AlwaysMatch()
    {
        var random = new Random(42);
        for (var round = 0; round < 200; round++)
        {
            var (target, boxes, _) = Make(random.Next(0, 30));
            var pool = boxes.Concat(Enumerable.Range(100, 10).Select(i => new Box(i))).ToList();
            var desired = pool.OrderBy(_ => random.Next()).Take(random.Next(0, pool.Count + 1)).ToList();
            CollectionSync.SyncTo(target, desired);
            Assert.Equal(desired, target);
        }
    }

    [Fact]
    public void LongestIncreasingSubsequence_FindsTheLongestRun()
    {
        var keep = CollectionSync.LongestIncreasingSubsequence(new[] { 3, 0, 1, 2, 9, 4 });
        Assert.Equal(new[] { false, true, true, true, false, true }, keep);
    }
}

public class SnapshotDiffTests
{
    private static ProcessSample Sample(int pid, long create, double cpu = 0, long memory = 1)
        => new(new RawProcess(pid, 0, $"p{pid}.exe", create, 0, memory, memory, 1),
            new ProcessStaticInfo(new ProcessKey(pid, create), $"p{pid}.exe"), cpu);

    [Fact]
    public void AddsUpdatesAndRemovesRows()
    {
        var rows = new Dictionary<ProcessKey, string>();
        var updates = new List<string>();

        var (added, removed) = SnapshotDiff.Apply(rows, new[] { Sample(1, 10), Sample(2, 20) },
            s => $"row{s.Pid}", (row, s) => updates.Add(row));
        Assert.Equal(new[] { "row1", "row2" }, added);
        Assert.Empty(removed);

        updates.Clear();
        (added, removed) = SnapshotDiff.Apply(rows, new[] { Sample(2, 20), Sample(3, 30) },
            s => $"row{s.Pid}", (row, s) => updates.Add(row));
        Assert.Equal(new[] { "row3" }, added);
        Assert.Equal(new[] { "row1" }, removed);
        Assert.Equal(new[] { "row2", "row3" }, updates); // existing row updated in place
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ReusedPid_ReplacesTheRow()
    {
        var rows = new Dictionary<ProcessKey, string>();
        SnapshotDiff.Apply(rows, new[] { Sample(5, 100) }, s => "old", (_, _) => { });
        var (added, removed) = SnapshotDiff.Apply(rows, new[] { Sample(5, 200) }, s => "new", (_, _) => { });
        Assert.Equal(new[] { "new" }, added);
        Assert.Equal(new[] { "old" }, removed);
    }
}

public class ProcessTreeTests
{
    private sealed record P(int Pid, int Parent, long Created);

    [Fact]
    public void CollectsDescendantsBreadthFirst()
    {
        var all = new List<P>
        {
            new(1, 0, 10), new(2, 1, 20), new(3, 1, 30), new(4, 2, 40), new(5, 99, 50),
        };
        var tree = ProcessTree.Collect(all, new[] { all[0] }, p => p.Pid, p => p.Parent, p => p.Created);
        Assert.Equal(new[] { 1, 2, 3, 4 }, tree.Select(p => p.Pid));
    }

    [Fact]
    public void StaleParentPid_IsIgnored()
    {
        // PID 7 was reused: the "child" started before its supposed parent, so it is not related.
        var parent = new P(7, 0, 500);
        var olderOrphan = new P(8, 7, 100);
        var tree = ProcessTree.Collect(new[] { parent, olderOrphan }, new[] { parent }, p => p.Pid, p => p.Parent, p => p.Created);
        Assert.Equal(new[] { 7 }, tree.Select(p => p.Pid));
    }
}

public class QuarantineRulesTests
{
    private static readonly SignatureInfo Unsigned = new(SignatureStatus.Unsigned, null);
    private static readonly SuspicionResult Risky = new(60, new[] { "x" });

    [Fact]
    public void SuspiciousUnsignedFileOutsideWindows_CanBeQuarantined()
        => Assert.Null(QuarantineRules.BlockReason(ProcessTestData.EvilPath, Unsigned, Risky, KillVerdict.Allowed, false, @"C:\self.exe"));

    [Fact]
    public void WindowsFolder_IsNeverQuarantined()
        => Assert.NotNull(QuarantineRules.BlockReason(ProcessTestData.SvchostPath, Unsigned, Risky, KillVerdict.Allowed, false, @"C:\self.exe"));

    [Fact]
    public void MicrosoftSigned_IsNeverQuarantined()
        => Assert.NotNull(QuarantineRules.BlockReason(@"C:\Apps\x.exe", new SignatureInfo(SignatureStatus.Valid, "Microsoft Corporation"),
            Risky, KillVerdict.Allowed, false, @"C:\self.exe"));

    [Fact]
    public void LowSuspicion_TrustedOrProtected_IsRefused()
    {
        Assert.NotNull(QuarantineRules.BlockReason(ProcessTestData.EvilPath, Unsigned, new SuspicionResult(20, new[] { "x" }),
            KillVerdict.Allowed, false, @"C:\self.exe"));
        Assert.NotNull(QuarantineRules.BlockReason(ProcessTestData.EvilPath, Unsigned, Risky, KillVerdict.Allowed, true, @"C:\self.exe"));
        Assert.NotNull(QuarantineRules.BlockReason(ProcessTestData.EvilPath, Unsigned, Risky, KillVerdict.Blocked, false, @"C:\self.exe"));
        Assert.NotNull(QuarantineRules.BlockReason(null, Unsigned, Risky, KillVerdict.Allowed, false, @"C:\self.exe"));
        Assert.NotNull(QuarantineRules.BlockReason(ProcessTestData.EvilPath, null, Risky, KillVerdict.Allowed, false, @"C:\self.exe"));
    }

    [Fact]
    public void TheCleanerItself_IsNeverQuarantined()
        => Assert.NotNull(QuarantineRules.BlockReason(ProcessTestData.EvilPath, Unsigned, Risky, KillVerdict.Allowed, false,
            ProcessTestData.EvilPath));
}
