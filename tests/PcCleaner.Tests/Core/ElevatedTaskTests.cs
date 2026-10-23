using System.IO;
using System.Xml.Linq;
using PcCleaner.Core;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// The task that starts the installed program elevated without the UAC prompt. The store is a fake: no real task.
/// The tests swap the static <see cref="ElevatedTask.Store"/>, so they run alone.
/// </summary>
[Collection(CoreSequentialCollection.Name)]
public class ElevatedTaskTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string TestSid2 = "S-1-5-21-1-2-3-1002";
    private readonly FakeTaskStore _store = new();
    private readonly string _programFiles;
    private readonly string _exe;

    public ElevatedTaskTests()
    {
        TestEnvironment.Init();
        ElevatedTask.Store = _store;
        _programFiles = TestEnvironment.NewTempDir("pf");
        var app = Directory.CreateDirectory(Path.Combine(_programFiles, "WaterCa7s PC Cleaner")).FullName;
        _exe = Path.Combine(app, UpdateEnvironment.ExeName);
        File.WriteAllBytes(_exe, new byte[] { 1 });
        File.WriteAllText(Path.Combine(app, UpdateEnvironment.InstalledMarkerName), "");
    }

    private string Name => ElevatedTask.NameFor(Sid);

    // ----- who may have a task -----

    [Fact]
    public void TheInstalledExeInProgramFiles_MayHaveATask()
        => Assert.True(ElevatedTask.MayRegister(_exe, _programFiles));

    [Fact]
    public void AnExeOutsideProgramFiles_NeverGetsATask()
    {
        var elsewhere = TestEnvironment.NewTempDir("downloads");
        var exe = Path.Combine(elsewhere, UpdateEnvironment.ExeName);
        File.WriteAllBytes(exe, new byte[] { 1 });
        File.WriteAllText(Path.Combine(elsewhere, UpdateEnvironment.InstalledMarkerName), "");

        Assert.False(ElevatedTask.MayRegister(exe, _programFiles));
    }

    [Fact]
    public void AFolderThatOnlyStartsLikeProgramFiles_IsNotInside()
        // "...\WaterCa7s PC Cleaner\..." starts with the text "...\WaterCa7s" but is not inside the folder "...\WaterCa7s\".
        => Assert.False(ElevatedTask.MayRegister(_exe, Path.Combine(_programFiles, "WaterCa7s")));

    [Fact]
    public void WithoutTheMarker_NoTask()
    {
        File.Delete(Path.Combine(Path.GetDirectoryName(_exe)!, UpdateEnvironment.InstalledMarkerName));

        Assert.False(ElevatedTask.MayRegister(_exe, _programFiles));
    }

    [Fact]
    public void AnExeWithAnotherName_NeverGetsATask()
    {
        var other = Path.Combine(Path.GetDirectoryName(_exe)!, "Other.exe");
        File.WriteAllBytes(other, new byte[] { 1 });

        Assert.False(ElevatedTask.MayRegister(other, _programFiles));
    }

    [Fact]
    public void AMissingExeFile_NeverGetsATask()
    {
        File.Delete(_exe);

        Assert.False(ElevatedTask.MayRegister(_exe, _programFiles));
    }

    // ----- the task itself -----

    [Fact]
    public void TheTask_RunsThisExeElevated_ForThisUser_AtNormalPriority_WithoutLimits()
    {
        var xml = XDocument.Parse(ElevatedTask.TaskXml(_exe, Sid));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Value(string name) => xml.Descendants(ns + name).Single().Value;

        Assert.Equal(_exe, Value("Command"));
        Assert.Equal(ElevatedTask.FromTaskArg, Value("Arguments"));
        Assert.Equal(Path.GetDirectoryName(_exe), Value("WorkingDirectory"));
        Assert.Equal(Sid, Value("UserId"));
        Assert.Equal("InteractiveToken", Value("LogonType"));
        Assert.Equal("HighestAvailable", Value("RunLevel"));
        Assert.Equal("4", Value("Priority"));
        Assert.Equal("false", Value("DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value("StopIfGoingOnBatteries"));
        Assert.Equal("PT0S", Value("ExecutionTimeLimit"));
        Assert.Equal("Parallel", Value("MultipleInstancesPolicy"));
    }

    [Fact]
    public void APathWithAnAmpersand_IsEscapedInTheXml()
    {
        var xml = ElevatedTask.TaskXml(@"C:\Program Files\A & B\WaterCa7sPcCleaner.exe", Sid);

        Assert.Contains(@"A &amp; B", xml);
        XDocument.Parse(xml);
    }

    // ----- the task follows «Запускать с правами администратора» -----

    [Fact]
    public void Sync_Elevated_On_Installed_RegistersTheTask()
    {
        Assert.True(ElevatedTask.Sync(runAsAdmin: true, isElevated: true, _exe, _programFiles, Sid));

        Assert.True(_store.Exists(Name));
        Assert.Equal(Sid, _store.Tasks[Name].Sid);
    }

    [Fact]
    public void Sync_Off_RemovesTheTask()
    {
        _store.Register(Name, "<Task/>", Sid);

        Assert.True(ElevatedTask.Sync(runAsAdmin: false, isElevated: true, _exe, _programFiles, Sid));

        Assert.False(_store.Exists(Name));
    }

    [Fact]
    public void Sync_WithoutTheRights_ChangesNothing()
    {
        _store.Register(Name, "<Task/>", Sid);

        Assert.True(ElevatedTask.Sync(runAsAdmin: false, isElevated: false, _exe, _programFiles, Sid));
        Assert.True(ElevatedTask.Sync(runAsAdmin: true, isElevated: false, _exe, _programFiles, TestSid2));

        Assert.True(_store.Exists(Name));
        Assert.False(_store.Exists(ElevatedTask.NameFor(TestSid2)));
    }

    [Fact]
    public void Sync_ADevelopmentBuild_RegistersNothing()
    {
        var dev = Path.Combine(TestEnvironment.NewTempDir("dev"), UpdateEnvironment.ExeName);

        ElevatedTask.Sync(runAsAdmin: true, isElevated: true, dev, _programFiles, Sid);

        Assert.Empty(_store.Tasks);
    }

    [Fact]
    public void Sync_On_ForACopyThatIsNotInstalled_LeavesTheExistingTaskUntouched()
    {
        _store.Register(Name, "<Task/>", Sid);
        var dev = Path.Combine(TestEnvironment.NewTempDir("dev"), UpdateEnvironment.ExeName);

        Assert.True(ElevatedTask.Sync(runAsAdmin: true, isElevated: true, dev, _programFiles, Sid));

        Assert.Equal("<Task/>", _store.Tasks[Name].Xml);
    }

    [Fact]
    public void Sync_WhenTheSchedulerRefuses_IsFalse_AndDoesNotThrow()
    {
        _store.Failure = new UnauthorizedAccessException("denied");

        Assert.False(ElevatedTask.Sync(runAsAdmin: true, isElevated: true, _exe, _programFiles, Sid));
    }

    // ----- the start by the shortcut -----

    [Fact]
    public void TryStart_RunsTheTaskOfThisUser()
    {
        _store.Register(Name, "<Task/>", Sid);

        Assert.True(ElevatedTask.TryStart(_exe, _programFiles, Sid));

        Assert.Equal(new[] { Name }, _store.Runs);
    }

    [Fact]
    public void TryStart_WithoutATask_IsFalse_SoTheUacPromptIsTheFallback()
        => Assert.False(ElevatedTask.TryStart(_exe, _programFiles, Sid));

    [Fact]
    public void TryStart_WhenTheTaskDoesNotRun_IsFalse()
    {
        _store.Register(Name, "<Task/>", Sid);
        _store.FailRun = true;

        Assert.False(ElevatedTask.TryStart(_exe, _programFiles, Sid));
    }

    [Fact]
    public void TryStart_WhenTheSchedulerSaysNotFound_IsFalse_AndDoesNotThrow()
    {
        _store.Failure = new FileNotFoundException("no such task");

        Assert.False(ElevatedTask.TryStart(_exe, _programFiles, Sid));
    }

    [Fact]
    public void TryStart_WhenRunRefusesItsArgument_IsFalse_AndDoesNotThrow()
    {
        _store.Register(Name, "<Task/>", Sid);
        _store.RunFailure = new ArgumentException("bad argument");

        Assert.False(ElevatedTask.TryStart(_exe, _programFiles, Sid));
    }

    [Fact]
    public void TryStart_WhenTheLateBoundCallFails_IsFalse_AndDoesNotThrow()
    {
        _store.Register(Name, "<Task/>", Sid);
        _store.RunFailure = new Microsoft.CSharp.RuntimeBinder.RuntimeBinderException("no such member");

        Assert.False(ElevatedTask.TryStart(_exe, _programFiles, Sid));
    }

    [Fact]
    public void TryStart_WithAnUnusablePath_IsFalse_AndDoesNotThrow()
        => Assert.False(ElevatedTask.TryStart(@"C:\bad\path" + (char)0 + UpdateEnvironment.ExeName, _programFiles, Sid));

    [Fact]
    public void TryStart_FromADevelopmentBuild_NeverStartsTheInstalledCopy()
    {
        _store.Register(Name, "<Task/>", Sid);
        var dev = Path.Combine(TestEnvironment.NewTempDir("dev"), UpdateEnvironment.ExeName);

        Assert.False(ElevatedTask.TryStart(dev, _programFiles, Sid));
        Assert.Empty(_store.Runs);
    }

    // ----- the setup's commands -----

    [Theory]
    [InlineData("--register-task", "--register-task")]
    [InlineData("--UNREGISTER-TASKS", "--unregister-tasks")]
    [InlineData("--page Cleaner", null)]
    [InlineData("", null)]
    public void CommandIn_FindsTheSetupsCommand(string args, string? expected)
        => Assert.Equal(expected, ElevatedTask.CommandIn(args.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void RegisterCommand_RegistersForThisUser_AndSucceeds()
    {
        Assert.Equal(0, ElevatedTask.RunCommand(ElevatedTask.RegisterArg, runAsAdmin: true, isElevated: true, _exe, _programFiles, Sid));

        Assert.True(_store.Exists(Name));
    }

    [Fact]
    public void RegisterCommand_WithTheSettingOff_RegistersNothing_AndStillSucceeds()
    {
        Assert.Equal(0, ElevatedTask.RunCommand(ElevatedTask.RegisterArg, runAsAdmin: false, isElevated: true, _exe, _programFiles, Sid));

        Assert.Empty(_store.Tasks);
    }

    [Fact]
    public void Commands_WithoutTheRights_Fail()
    {
        Assert.Equal(1, ElevatedTask.RunCommand(ElevatedTask.RegisterArg, true, isElevated: false, _exe, _programFiles, Sid));
        Assert.Equal(1, ElevatedTask.RunCommand(ElevatedTask.UnregisterArg, true, isElevated: false, _exe, _programFiles, Sid));
        Assert.False(_store.FolderDeleted);
    }

    [Fact]
    public void RegisterCommand_WhenTheSchedulerRefuses_Fails_AndDoesNotThrow()
    {
        _store.Failure = new UnauthorizedAccessException("denied");

        Assert.Equal(1, ElevatedTask.RunCommand(ElevatedTask.RegisterArg, true, isElevated: true, _exe, _programFiles, Sid));
    }

    [Fact]
    public void UnregisterCommand_WhenTheFolderIsGone_Fails_AndDoesNotThrow()
    {
        _store.Failure = new DirectoryNotFoundException("no such folder");

        Assert.Equal(1, ElevatedTask.RunCommand(ElevatedTask.UnregisterArg, true, isElevated: true, _exe, _programFiles, Sid));
    }

    [Fact]
    public void AnUnknownCommand_Fails_AndNeverDeletesTheTasks()
    {
        _store.Register(Name, "<Task/>", Sid);

        Assert.Equal(1, ElevatedTask.RunCommand("--something-else", true, isElevated: true, _exe, _programFiles, Sid));

        Assert.False(_store.FolderDeleted);
        Assert.True(_store.Exists(Name));
    }

    [Fact]
    public void UnregisterCommand_RemovesEveryUsersTask()
    {
        _store.Register(Name, "<Task/>", Sid);
        _store.Register(ElevatedTask.NameFor(TestSid2), "<Task/>", TestSid2);

        Assert.Equal(0, ElevatedTask.RunCommand(ElevatedTask.UnregisterArg, true, isElevated: true, _exe, _programFiles, Sid));

        Assert.True(_store.FolderDeleted);
        Assert.Empty(_store.Tasks);
    }
}
