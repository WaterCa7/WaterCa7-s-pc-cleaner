using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PcCleaner.Core;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// The «always run as administrator» restart at startup. Nothing here starts a process or shows a UAC prompt:
/// the start is a recording fake.
/// </summary>
public class StartupElevationTests
{
    public StartupElevationTests() => TestEnvironment.Init();

    // ----- ShouldElevate -----

    [Theory]
    [InlineData(false, true, false, "", true)]                                 // the normal start: ask for the rights
    [InlineData(false, true, false, "--page Cleaner", true)]                   // other arguments do not matter
    [InlineData(true, true, false, "", false)]                                 // already elevated
    [InlineData(false, false, false, "", false)]                               // switched off in Настройки
    [InlineData(false, true, true, "", false)]                                 // --screenshots verification runs
    [InlineData(false, true, false, "--elevation-declined", false)]            // the user already said no
    [InlineData(false, true, false, "--ELEVATION-DECLINED", false)]            // arguments are case-insensitive
    [InlineData(false, true, false, "--page Cleaner --elevation-declined", false)]
    [InlineData(false, true, false, "--updated-from 1.1.0", false)]            // the updater restarted us: no second question
    [InlineData(false, true, false, "--page Cleaner --Updated-From 1.1.0", false)]
    [InlineData(true, false, true, "--updated-from 1.1.0", false)]
    [InlineData(false, true, false, "--elevated-restart", false)]              // our own restart did not give the rights: no loop
    [InlineData(false, true, false, "--page Cleaner --ELEVATED-RESTART", false)]
    [InlineData(true, true, false, "--elevated-restart", false)]               // the restart worked
    [InlineData(false, true, false, "--from-task", false)]                     // the task did not give the rights: no loop
    [InlineData(true, true, false, "--from-task", false)]                      // the task worked
    public void ShouldElevate(bool isElevated, bool runAsAdmin, bool screenshotMode, string args, bool expected)
    {
        var list = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(expected, StartupElevation.ShouldElevate(isElevated, runAsAdmin, screenshotMode, list));
    }

    [Fact]
    public void ShouldElevate_AValueThatLooksLikeTheFlagIsStillAnArgument()
    {
        // "--page --elevation-declined" is odd, but the flag is looked for as a whole argument, nothing fancier.
        Assert.False(StartupElevation.ShouldElevate(false, true, false, new[] { "--page", "--elevation-declined" }));
        Assert.True(StartupElevation.ShouldElevate(false, true, false, new[] { "--page", "elevation-declined" }));
    }

    private const string LoopGuardWarning = "Перезапуск с правами администратора не дал прав — работаем без них";

    private static int LoopGuardWarnings()
        => Log.Entries.ToList().Count(e => e.Level == LogLevel.Warning && e.Message == LoopGuardWarning);

    [Fact]
    public void ShouldElevate_TheRestartStillWithoutRights_StopsAndSaysSoInTheJournal()
    {
        var before = LoopGuardWarnings();

        Assert.False(StartupElevation.ShouldElevate(false, true, false, new[] { "--page", "Cleaner", "--elevated-restart" }));

        Assert.Equal(before + 1, LoopGuardWarnings());
    }

    private const string TaskGuardWarning = "Копия, запущенная задачей Планировщика, не получила права — работаем без них";

    private static int TaskGuardWarnings()
        => Log.Entries.ToList().Count(e => e.Level == LogLevel.Warning && e.Message == TaskGuardWarning);

    [Fact]
    public void ShouldElevate_TheTasksCopyWithoutRights_SaysSoInTheJournal_NotAsARestart()
    {
        var restart = LoopGuardWarnings();
        var task = TaskGuardWarnings();

        Assert.False(StartupElevation.ShouldElevate(false, true, false, new[] { "--from-task" }));

        Assert.Equal(task + 1, TaskGuardWarnings());
        Assert.Equal(restart, LoopGuardWarnings());
    }

    [Fact]
    public void ShouldElevate_TheRestartStillWithoutRights_IsNotReportedAsTheTask()
    {
        var task = TaskGuardWarnings();

        Assert.False(StartupElevation.ShouldElevate(false, true, false, new[] { "--elevated-restart" }));

        Assert.Equal(task, TaskGuardWarnings());
    }

    [Theory]
    [InlineData(true, true, false)]   // the restart worked: nothing to complain about
    [InlineData(false, false, false)] // the user switched it off meanwhile
    [InlineData(false, true, true)]   // a screenshot run
    public void ShouldElevate_TheMarkerIsOnlyAProblemWhenElevationWasWanted(bool isElevated, bool runAsAdmin, bool screenshotMode)
    {
        var before = LoopGuardWarnings();

        Assert.False(StartupElevation.ShouldElevate(isElevated, runAsAdmin, screenshotMode, new[] { "--elevated-restart" }));

        Assert.Equal(before, LoopGuardWarnings());
    }

    [Fact]
    public void ShouldElevate_NoMarker_NoWarning()
    {
        var before = LoopGuardWarnings();

        Assert.True(StartupElevation.ShouldElevate(false, true, false, new[] { "--page", "Cleaner" }));

        Assert.Equal(before, LoopGuardWarnings());
    }

    // ----- TryRelaunchElevated -----

    private sealed class Starts
    {
        public List<ProcessStartInfo> Infos { get; } = new();

        public Func<ProcessStartInfo, bool> Behaviour { get; set; } = _ => true;

        public bool Start(ProcessStartInfo info)
        {
            Infos.Add(info);
            return Behaviour(info);
        }
    }

    private static string UniqueExe() => $@"C:\Tests\{Guid.NewGuid():N}\WaterCa7sPcCleaner.exe";

    private static List<LogEntry> LogMentioning(string marker)
        => Log.Entries.ToList().Where(e => e.Message.Contains(marker)).ToList();

    [Fact]
    public void Relaunch_StartsTheSameExeWithTheRunasVerbAndTheSameArguments()
    {
        var starts = new Starts();
        var exe = UniqueExe();
        var args = new[] { "--page", "Cleaner", "--no-scan", "C:\\Program Files\\x y.txt" };

        var result = StartupElevation.TryRelaunchElevated(exe, args, starts.Start);

        Assert.True(result);
        var info = Assert.Single(starts.Infos);
        Assert.Equal(exe, info.FileName);
        Assert.Equal("runas", info.Verb);
        Assert.True(info.UseShellExecute); // a verb needs the shell
        Assert.Equal(args.Append("--elevated-restart"), info.ArgumentList); // the same arguments, then the loop guard's marker
        Assert.Equal(AppContext.BaseDirectory, info.WorkingDirectory);
    }

    [Fact]
    public void Relaunch_WithoutArguments_StartsWithOnlyTheMarker()
    {
        var starts = new Starts();

        Assert.True(StartupElevation.TryRelaunchElevated(UniqueExe(), Array.Empty<string>(), starts.Start));

        Assert.Equal(new[] { "--elevated-restart" }, Assert.Single(starts.Infos).ArgumentList);
    }

    [Fact]
    public void Relaunch_TheRestartedCopyDoesNotRelaunchAgain()
    {
        var starts = new Starts();
        var args = new[] { "--page", "Cleaner" };

        StartupElevation.TryRelaunchElevated(UniqueExe(), args, starts.Start);

        // What the restarted copy sees if Windows gave it no rights after all: it must stay as it is.
        Assert.False(StartupElevation.ShouldElevate(false, true, false, Assert.Single(starts.Infos).ArgumentList.ToList()));
    }

    // ParseArgs ignoring the marker is tested in ParseArgsTests (its tests share one sequential collection).

    [Fact]
    public void AdminRestartButtons_NeverCarryTheMarker()
    {
        // Admin.RestartElevated (the buttons, RequireElevation) starts the exe with no arguments at all.
        Assert.Empty(Admin.ElevatedStartInfo(UniqueExe()).ArgumentList);
    }

    [Fact]
    public void Relaunch_TheDefaultOverloadStartsTheRunningExe()
    {
        var starts = new Starts();

        StartupElevation.TryRelaunchElevated(new[] { "--page", "Cleaner" }, starts.Start);

        var info = Assert.Single(starts.Infos);
        Assert.Equal(Environment.ProcessPath, info.FileName);
        Assert.Equal("runas", info.Verb);
        Assert.Equal(new[] { "--page", "Cleaner", "--elevated-restart" }, info.ArgumentList);
    }

    [Fact]
    public void Relaunch_TheUserSaysNoInUac_ReturnsFalseWithoutRetrying()
    {
        const int ErrorCancelled = 1223;
        var starts = new Starts { Behaviour = _ => throw new Win32Exception(ErrorCancelled) };

        var result = StartupElevation.TryRelaunchElevated(UniqueExe(), new[] { "--page", "Cleaner" }, starts.Start);

        Assert.False(result);
        Assert.Single(starts.Infos);
    }

    [Fact]
    public void Relaunch_TheUserSaysNoInUac_IsNotLoggedAsAProblem()
    {
        var exe = UniqueExe();
        var starts = new Starts { Behaviour = _ => throw new Win32Exception(1223) };

        StartupElevation.TryRelaunchElevated(exe, Array.Empty<string>(), starts.Start);

        Assert.DoesNotContain(LogMentioning(exe), e => e.Level is LogLevel.Warning or LogLevel.Error);
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]            // some other Windows error, e.g. access denied
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(FileNotFoundException))]
    public void Relaunch_AnyOtherError_ReturnsFalseAndIsLogged(Type exceptionType)
    {
        var exe = UniqueExe();
        var exception = exceptionType == typeof(Win32Exception)
            ? new Win32Exception(5)
            : (Exception)Activator.CreateInstance(exceptionType, "boom")!;
        var starts = new Starts { Behaviour = _ => throw exception };

        var result = StartupElevation.TryRelaunchElevated(exe, new[] { "--page", "Cleaner" }, starts.Start);

        Assert.False(result);
        Assert.Single(starts.Infos);
        Assert.Contains(LogMentioning(exe), e => e.Level is LogLevel.Warning or LogLevel.Error);
    }

    [Fact]
    public void Relaunch_TheProcessDoesNotStart_ReturnsFalse()
    {
        var exe = UniqueExe();
        var starts = new Starts { Behaviour = _ => false };

        Assert.False(StartupElevation.TryRelaunchElevated(exe, Array.Empty<string>(), starts.Start));

        Assert.Contains(LogMentioning(exe), e => e.Level is LogLevel.Warning or LogLevel.Error);
    }

    [Fact]
    public void Relaunch_UnknownExePath_ReturnsFalseAndStartsNothing()
    {
        var starts = new Starts();

        Assert.False(StartupElevation.TryRelaunchElevated(null, new[] { "--page", "Cleaner" }, starts.Start));

        Assert.Empty(starts.Infos);
    }
}
