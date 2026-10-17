using System.IO;
using System.Text;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Startup;
using Xunit;

namespace PcCleaner.Tests.Startup;

/// <summary>Run/RunOnce enumeration and changes against the sandbox key HKCU\Software\WaterCa7sPcCleanerTests\Startup.</summary>
public class StartupRegistryTests
{
    private static StartupItem Find(IEnumerable<StartupItem> items, string name)
        => items.Single(i => i.Name == name);

    private static List<StartupItem> Scan(StartupSandbox sandbox)
    {
        var items = sandbox.Service.ScanRegistry(CancellationToken.None);
        sandbox.Service.Enrich(items, CancellationToken.None);
        return items;
    }

    [Fact]
    public void Scan_ReadsAllRunKeysWithScopesAndStates()
    {
        using var sandbox = new StartupSandbox();
        var good = sandbox.CreateFile("good.exe");
        sandbox.SetValue("HKCU\\Run", "GoodApp", $"\"{good}\" --tray");
        sandbox.SetValue("HKCU\\Run", "OffApp", $"\"{good}\" --off");
        sandbox.SetValue("HKCU\\RunOnce", "FinishSetup", $"\"{good}\" /finish");
        sandbox.SetValue("HKLM\\Run", "MachineApp", $"\"{good}\"");
        sandbox.SetValue("HKLM32\\Run", "App32", $"\"{good}\" -32");
        sandbox.SetValue("HKCU\\Run", "NotAString", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
        var disabledAt = new DateTime(2025, 5, 1, 8, 0, 0, DateTimeKind.Utc);
        sandbox.SetValue(@"HKCU\Explorer\StartupApproved\Run", "OffApp", StartupApproved.CreateDisabled(disabledAt), RegistryValueKind.Binary);
        sandbox.SetValue(@"HKLM\Explorer\StartupApproved\Run32", "App32", new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);

        var items = Scan(sandbox);

        Assert.Equal(5, items.Count);
        var goodItem = Find(items, "GoodApp");
        Assert.Equal(StartupKind.RegistryRun, goodItem.Kind);
        Assert.Equal(StartupScope.CurrentUser, goodItem.Scope);
        Assert.True(goodItem.IsEnabled);
        Assert.Equal(Path.GetFullPath(good), goodItem.TargetFile, ignoreCase: true);
        Assert.True(goodItem.TargetExists);

        var off = Find(items, "OffApp");
        Assert.False(off.IsEnabled);
        Assert.Equal(disabledAt.ToLocalTime(), off.DisabledTime);

        var once = Find(items, "FinishSetup");
        Assert.Equal(StartupKind.RegistryRunOnce, once.Kind);
        Assert.True(once.IsEnabled);
        Assert.False(once.CanToggle);
        Assert.True(once.CanDelete);

        var machine = Find(items, "MachineApp");
        Assert.Equal(StartupScope.AllUsers, machine.Scope);
        Assert.True(machine.RequiresAdmin);

        var app32 = Find(items, "App32");
        Assert.True(app32.Is32Bit);
        Assert.False(app32.IsEnabled);
        Assert.Contains("Run32", app32.ApprovedKeyPath);
    }

    [Fact]
    public void BrokenEntries_AreDetectedOnlyWhenTheFileIsReallyMissing()
    {
        using var sandbox = new StartupSandbox();
        var good = sandbox.CreateFile("ok.exe");
        sandbox.SetValue("HKCU\\Run", "Good", $"\"{good}\"");
        sandbox.SetValue("HKCU\\Run", "Broken", $"\"{sandbox.MissingFile("gone.exe")}\" -silent");
        sandbox.SetValue("HKCU\\Run", "BrokenUnquoted", $"{sandbox.MissingFile("Old App.exe")} /min");
        sandbox.SetValue("HKCU\\Run", "BareName", "definitely-not-on-path-12345.exe -x");
        sandbox.SetValue("HKCU\\Run", "EnvVar", "%WINDIR%\\explorer.exe");

        var items = Scan(sandbox);

        Assert.False(Find(items, "Good").IsBroken);
        Assert.True(Find(items, "Broken").IsBroken);
        Assert.Equal(StartupState.Broken, Find(items, "Broken").State);
        Assert.True(Find(items, "BrokenUnquoted").IsBroken);
        Assert.Null(Find(items, "BareName").TargetExists); // could be found via PATH: never reported as broken
        Assert.False(Find(items, "BareName").IsBroken);
        Assert.False(Find(items, "EnvVar").IsBroken);
    }

    [Fact]
    public void MachineEntryWithPerUserPath_IsNeverReportedBroken()
    {
        using var sandbox = new StartupSandbox();
        var missing = $@"%LOCALAPPDATA%\WaterCa7sTests-{sandbox.Id}\gone.exe";
        sandbox.SetValue("HKLM\\Run", "PerUserMachine", $"\"{missing}\" /tray", RegistryValueKind.ExpandString);
        sandbox.SetValue("HKCU\\Run", "PerUserMine", $"\"{missing}\" /tray", RegistryValueKind.ExpandString);

        var items = Scan(sandbox);

        // HKLM entries run for every account: the file may exist in another user's profile.
        var machine = Find(items, "PerUserMachine");
        Assert.True(machine.TargetDependsOnUser);
        Assert.Null(machine.TargetExists);
        Assert.False(machine.IsBroken);
        // The current user's own entry really is broken.
        Assert.True(Find(items, "PerUserMine").IsBroken);
        var issue = Assert.Single(sandbox.Service.BuildIssues(items));
        Assert.Contains("(1)", issue.Title);
        Assert.DoesNotContain("PerUserMachine", issue.Details);
    }

    [Fact]
    public async Task DisableAndEnable_WriteTaskManagerCompatibleFlags()
    {
        using var sandbox = new StartupSandbox();
        var good = sandbox.CreateFile("app.exe");
        sandbox.SetValue("HKCU\\Run", "App", $"\"{good}\"");
        var item = Find(Scan(sandbox), "App");
        Assert.True(item.IsEnabled);
        Assert.False(sandbox.KeyExists(@"HKCU\Explorer\StartupApproved\Run"));

        var before = DateTime.UtcNow.AddSeconds(-5);
        var disabled = await sandbox.Service.SetEnabledAsync(item, false, CancellationToken.None);
        Assert.True(disabled.Success, disabled.Message);
        Assert.False(item.IsEnabled);
        var data = Assert.IsType<byte[]>(sandbox.GetValue(@"HKCU\Explorer\StartupApproved\Run", "App"));
        Assert.Equal(12, data.Length);
        Assert.Equal(0x03, data[0]);
        Assert.InRange(StartupApproved.DisabledAt(data)!.Value.ToUniversalTime(), before, DateTime.UtcNow.AddSeconds(5));
        // The Run value itself is untouched: disabling is reversible.
        Assert.Equal($"\"{good}\"", sandbox.GetValue("HKCU\\Run", "App"));

        var enabled = await sandbox.Service.SetEnabledAsync(item, true, CancellationToken.None);
        Assert.True(enabled.Success, enabled.Message);
        Assert.True(item.IsEnabled);
        data = Assert.IsType<byte[]>(sandbox.GetValue(@"HKCU\Explorer\StartupApproved\Run", "App"));
        Assert.Equal(StartupApproved.CreateEnabled(), data);
        // The approved key existed before re-enabling, so a .reg backup was made first.
        Assert.NotNull(enabled.BackupPath);
        Assert.True(File.Exists(enabled.BackupPath));

        // A rescan reads the same state back.
        Assert.True(Find(Scan(sandbox), "App").IsEnabled);
    }

    [Fact]
    public async Task MachineEntries_NeedAdmin_AndAreNotChangedWithoutIt()
    {
        using var sandbox = new StartupSandbox(elevated: false);
        var good = sandbox.CreateFile("m.exe");
        sandbox.SetValue("HKLM\\Run", "MachineApp", $"\"{good}\"");
        var item = Find(Scan(sandbox), "MachineApp");

        var toggle = await sandbox.Service.SetEnabledAsync(item, false, CancellationToken.None);
        var delete = await sandbox.Service.DeleteAsync(item, CancellationToken.None);

        Assert.False(toggle.Success);
        Assert.True(toggle.NeedsAdmin);
        Assert.False(delete.Success);
        Assert.True(delete.NeedsAdmin);
        Assert.True(item.IsEnabled);
        Assert.False(sandbox.KeyExists(@"HKLM\Explorer\StartupApproved\Run"));
        Assert.NotNull(sandbox.GetValue("HKLM\\Run", "MachineApp"));
    }

    [Fact]
    public async Task Delete_BacksUpTheKeyFirst_ThenRemovesOnlyThatValue()
    {
        using var sandbox = new StartupSandbox();
        var keep = sandbox.CreateFile("keep.exe");
        var marker = "Broken-" + sandbox.Id;
        sandbox.SetValue("HKCU\\Run", marker, $"\"{sandbox.MissingFile("x.exe")}\"");
        sandbox.SetValue("HKCU\\Run", "Keep", $"\"{keep}\"");
        var item = Find(Scan(sandbox), marker);

        var result = await sandbox.Service.DeleteAsync(item, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Null(sandbox.GetValue("HKCU\\Run", marker));
        Assert.NotNull(sandbox.GetValue("HKCU\\Run", "Keep"));
        Assert.NotNull(result.BackupPath);
        var backup = File.ReadAllText(result.BackupPath!, Encoding.Unicode);
        Assert.Contains(marker, backup);
    }

    [Fact]
    public async Task Delete_RunOnceEntry()
    {
        using var sandbox = new StartupSandbox();
        sandbox.SetValue("HKCU\\RunOnce", "Once", "cmd.exe /c echo hi");
        var item = Find(Scan(sandbox), "Once");

        var toggle = await sandbox.Service.SetEnabledAsync(item, false, CancellationToken.None);
        Assert.False(toggle.Success); // RunOnce cannot be disabled, only deleted

        var result = await sandbox.Service.DeleteAsync(item, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Null(sandbox.GetValue("HKCU\\RunOnce", "Once"));
    }

    [Fact]
    public async Task Delete_AbortsWhenBackupFails()
    {
        using var sandbox = new StartupSandbox();
        var env = new StartupEnvironment
        {
            UserHive = sandbox.Environment.UserHive,
            UserSoftware = sandbox.Environment.UserSoftware,
            MachineHive = sandbox.Environment.MachineHive,
            MachineSoftware = sandbox.Environment.MachineSoftware,
            MachineSoftware32 = sandbox.Environment.MachineSoftware32,
            IncludeServices = false,
            IncludeTasks = false,
            IsElevated = () => true,
            BackupKey = (_, _, _) => Task.FromResult<string?>(null),
        };
        var service = new StartupService(env);
        sandbox.SetValue("HKCU\\Run", "App", "\"C:\\nothing\\app.exe\"");
        var item = service.ScanRegistry(CancellationToken.None).Single();

        var result = await service.DeleteAsync(item, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(sandbox.GetValue("HKCU\\Run", "App"));
    }

    [Fact]
    public async Task BrokenIssue_FixDeletesOnlyBrokenEntries()
    {
        using var sandbox = new StartupSandbox();
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        sandbox.SetValue("HKCU\\Run", "Fine", $"\"{explorer}\"");
        sandbox.SetValue("HKCU\\Run", "Dead1", $"\"{sandbox.MissingFile("a.exe")}\"");
        sandbox.SetValue("HKCU\\RunOnce", "Dead2", $"\"{sandbox.MissingFile("b.exe")}\" /x");
        var changed = false;
        sandbox.Service.ChangedExternally += () => changed = true;

        var issues = sandbox.Service.BuildIssues(Scan(sandbox));
        var issue = Assert.Single(issues);
        Assert.Equal(IssueSeverity.Low, issue.Severity);
        Assert.Contains("(2)", issue.Title);
        Assert.False(issue.AutoFixSafe);
        Assert.Equal("Startup", issue.TargetPageKey);
        Assert.NotNull(issue.FixAsync);

        var message = await issue.FixAsync!(CancellationToken.None);

        Assert.Contains("Удалено", message);
        Assert.True(changed);
        Assert.Null(sandbox.GetValue("HKCU\\Run", "Dead1"));
        Assert.Null(sandbox.GetValue("HKCU\\RunOnce", "Dead2"));
        Assert.NotNull(sandbox.GetValue("HKCU\\Run", "Fine"));
    }

    [Fact]
    public async Task BrokenIssue_WithoutAdmin_ChangesNothingForMachineEntries()
    {
        using var sandbox = new StartupSandbox(elevated: false);
        sandbox.SetValue("HKCU\\Run", "DeadUser", $"\"{sandbox.MissingFile("u.exe")}\"");
        sandbox.SetValue("HKLM\\Run", "DeadMachine", $"\"{sandbox.MissingFile("m.exe")}\"");

        var issue = Assert.Single(sandbox.Service.BuildIssues(Scan(sandbox)));
        var message = await issue.FixAsync!(CancellationToken.None);

        Assert.Contains("администратора", message);
        Assert.NotNull(sandbox.GetValue("HKCU\\Run", "DeadUser"));
        Assert.NotNull(sandbox.GetValue("HKLM\\Run", "DeadMachine"));
    }

    [Fact]
    public async Task BrokenIssue_FixLeavesAnEntryRewrittenToANewVersionFolder()
    {
        using var sandbox = new StartupSandbox();
        // An updater moves the program to a new version folder and rewrites the same Run value.
        var oldCommand = $"\"{sandbox.MissingFile(@"app-1.0\app.exe")}\" --background";
        sandbox.SetValue("HKCU\\Run", "VersionedApp", oldCommand);
        var issue = Assert.Single(sandbox.Service.BuildIssues(Scan(sandbox)));
        var newCommand = $"\"{sandbox.CreateFile(@"app-2.0\app.exe")}\" --background";
        sandbox.SetValue("HKCU\\Run", "VersionedApp", newCommand);
        var changed = false;
        sandbox.Service.ChangedExternally += () => changed = true;

        var message = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal(newCommand, sandbox.GetValue("HKCU\\Run", "VersionedApp"));
        Assert.Equal("Битых записей больше нет — исправлять нечего.", message);
        Assert.False(PcCleaner.Modules.Dashboard.HealthScoring.LooksLikeFailure(message)); // already resolved = fixed
        Assert.True(changed); // the Startup page list is out of date too
    }

    [Fact]
    public async Task BrokenIssue_EntriesAlreadyRemoved_CountAsFixed()
    {
        using var sandbox = new StartupSandbox();
        sandbox.SetValue("HKCU\\Run", "Dead", $"\"{sandbox.MissingFile("d.exe")}\"");
        var issue = Assert.Single(sandbox.Service.BuildIssues(Scan(sandbox)));
        // Removed on the Startup page (or by an uninstaller) before the dashboard fix is pressed.
        using (var key = Registry.CurrentUser.OpenSubKey($@"{sandbox.RegistryRoot}\HKCU\Run", writable: true))
            key!.DeleteValue("Dead");

        var message = await issue.FixAsync!(CancellationToken.None);

        Assert.Contains("Битых записей больше нет", message);
        Assert.False(PcCleaner.Modules.Dashboard.HealthScoring.LooksLikeFailure(message));
    }

    [Fact]
    public async Task BrokenIssue_FixStillDeletesUnchangedBrokenEntries_AndKeepsRewrittenOnes()
    {
        using var sandbox = new StartupSandbox();
        sandbox.SetValue("HKCU\\Run", "Dead", $"\"{sandbox.MissingFile("dead.exe")}\"");
        sandbox.SetValue("HKCU\\Run", "Updated", $"\"{sandbox.MissingFile(@"v1\upd.exe")}\"");
        var issue = Assert.Single(sandbox.Service.BuildIssues(Scan(sandbox)));
        Assert.Contains("(2)", issue.Title);
        var updated = $"\"{sandbox.CreateFile(@"v2\upd.exe")}\"";
        sandbox.SetValue("HKCU\\Run", "Updated", updated);

        var message = await issue.FixAsync!(CancellationToken.None);

        Assert.Contains("Удалено из автозагрузки: 1 запись", message);
        Assert.Null(sandbox.GetValue("HKCU\\Run", "Dead"));
        Assert.Equal(updated, sandbox.GetValue("HKCU\\Run", "Updated"));
        Assert.False(PcCleaner.Modules.Dashboard.HealthScoring.LooksLikeFailure(message));
    }

    [Theory]
    [InlineData(true)]  // rewritten to the new version of the program
    [InlineData(false)] // rewritten to another path that is missing too: still not what the user confirmed
    public async Task DeleteBroken_SkipsAValueRewrittenAfterTheScan(bool newTargetExists)
    {
        using var sandbox = new StartupSandbox();
        sandbox.SetValue("HKCU\\Run", "App", $"\"{sandbox.MissingFile(@"1.0\app.exe")}\"");
        var item = Find(Scan(sandbox), "App");
        Assert.True(item.IsBroken);
        var target = newTargetExists ? sandbox.CreateFile(@"2.0\app.exe") : sandbox.MissingFile(@"2.0\app.exe");
        sandbox.SetValue("HKCU\\Run", "App", $"\"{target}\"");

        var result = await sandbox.Service.DeleteBrokenAsync(new[] { item }, CancellationToken.None);

        Assert.Empty(result.Done);
        Assert.Empty(result.Failed);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(StartupService.ChangedSinceScan, skipped.Reason);
        Assert.Equal($"\"{target}\"", sandbox.GetValue("HKCU\\Run", "App"));
        Assert.StartsWith("Удалять нечего.", result.Summary);
        Assert.Contains("обновили", result.Summary);
        Assert.False(PcCleaner.Modules.Dashboard.HealthScoring.LooksLikeFailure(result.Summary));
    }

    [Fact]
    public async Task DeleteBroken_SkipsAnEntryWhoseFileIsBack()
    {
        using var sandbox = new StartupSandbox();
        var path = sandbox.MissingFile("back.exe");
        sandbox.SetValue("HKCU\\Run", "Back", $"\"{path}\"");
        var item = Find(Scan(sandbox), "Back");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "reinstalled");

        var result = await sandbox.Service.DeleteBrokenAsync(new[] { item }, CancellationToken.None);

        Assert.Equal(StartupService.TargetIsBack, Assert.Single(result.Skipped).Reason);
        Assert.NotNull(sandbox.GetValue("HKCU\\Run", "Back"));
    }

    [Fact]
    public async Task DeleteBroken_DeletesAnUnchangedBrokenValue_WithBackup()
    {
        using var sandbox = new StartupSandbox();
        var marker = "StillDead-" + sandbox.Id;
        sandbox.SetValue("HKCU\\Run", marker, $"\"{sandbox.MissingFile("x.exe")}\" /min");
        sandbox.SetValue("HKCU\\RunOnce", "OnceDead", $"\"{sandbox.MissingFile("y.exe")}\"");
        var items = Scan(sandbox);

        var result = await sandbox.Service.DeleteBrokenAsync(items, CancellationToken.None);

        Assert.Equal(2, result.Done.Count);
        Assert.Empty(result.Skipped);
        Assert.Null(sandbox.GetValue("HKCU\\Run", marker));
        Assert.Null(sandbox.GetValue("HKCU\\RunOnce", "OnceDead"));
        Assert.Contains(SandboxBackups(sandbox), b => File.ReadAllText(b.FullName, Encoding.Unicode).Contains(marker));
    }

    [Fact]
    public async Task DeleteBroken_ValueRewrittenWhileTheBackupIsMade_IsKept()
    {
        using var sandbox = new StartupSandbox();
        var newCommand = $"\"{sandbox.CreateFile(@"9.1\app.exe")}\"";
        var env = new StartupEnvironment
        {
            UserHive = sandbox.Environment.UserHive,
            UserSoftware = sandbox.Environment.UserSoftware,
            MachineHive = sandbox.Environment.MachineHive,
            MachineSoftware = sandbox.Environment.MachineSoftware,
            MachineSoftware32 = sandbox.Environment.MachineSoftware32,
            IncludeServices = false,
            IncludeTasks = false,
            IsElevated = () => true,
            Settings = () => sandbox.Settings,
            BackupKey = (_, _, _) =>
            {
                sandbox.SetValue("HKCU\\Run", "App", newCommand); // the updater wins the race
                return Task.FromResult<string?>(Path.Combine(sandbox.Dir, "backup.reg"));
            },
        };
        var service = new StartupService(env);
        sandbox.SetValue("HKCU\\Run", "App", $"\"{sandbox.MissingFile(@"9.0\app.exe")}\"");
        var items = service.ScanRegistry(CancellationToken.None);
        service.Enrich(items, CancellationToken.None);

        var result = await service.DeleteBrokenAsync(items, CancellationToken.None);

        Assert.Single(result.Skipped);
        Assert.Empty(result.Done);
        Assert.Equal(newCommand, sandbox.GetValue("HKCU\\Run", "App"));
    }

    [Fact]
    public async Task PlainDelete_IsUnchanged_DeletesTheNamedEntryTheUserConfirmed()
    {
        using var sandbox = new StartupSandbox();
        sandbox.SetValue("HKCU\\Run", "App", $"\"{sandbox.MissingFile(@"1\app.exe")}\"");
        var item = Find(Scan(sandbox), "App");
        sandbox.SetValue("HKCU\\Run", "App", $"\"{sandbox.MissingFile(@"2\app.exe")}\"");

        var result = await sandbox.Service.DeleteAsync(item, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.False(result.Skipped);
        Assert.Null(sandbox.GetValue("HKCU\\Run", "App"));
    }

    private static IEnumerable<FileInfo> SandboxBackups(StartupSandbox sandbox) => StartupSandbox.Backups($"*{sandbox.Id}*");

    [Fact]
    public async Task Analyze_IsReadOnly()
    {
        using var sandbox = new StartupSandbox();
        sandbox.SetValue("HKCU\\Run", "Dead", $"\"{sandbox.MissingFile("d.exe")}\"");

        var issues = await sandbox.Service.AnalyzeAsync(CancellationToken.None);

        Assert.Single(issues);
        Assert.NotNull(sandbox.GetValue("HKCU\\Run", "Dead"));
        Assert.False(sandbox.KeyExists(@"HKCU\Explorer\StartupApproved\Run"));
    }
}
