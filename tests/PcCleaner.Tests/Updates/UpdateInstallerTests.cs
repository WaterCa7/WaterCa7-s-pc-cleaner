using System.Diagnostics;
using System.IO;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

public class UpdateInstallerTests
{
    public UpdateInstallerTests() => TestEnvironment.Init();

    private static (string Exe, StagedUpdate Staged, byte[] NewBytes) Setup(UpdateTestKit kit, byte[]? oldBytes = null)
    {
        var app = TestEnvironment.NewTempDir("upd-app");
        var exe = Path.Combine(app, "WaterCa7sPcCleaner.exe");
        File.WriteAllBytes(exe, oldBytes ?? UpdateTestKit.FakeExe(1));
        var store = new UpdateStore(TestEnvironment.NewTempDir("upd-store"));
        var newBytes = UpdateTestKit.FakeExe(2);
        var manifest = UpdateStoreTests.Stage(store, kit, "1.2.0", newBytes);
        return (exe, new StagedUpdate(manifest, store.ExePathFor(manifest)), newBytes);
    }

    [Fact]
    public async Task Install_SwapsTheExe_AndCleanupRemovesTheOldOne()
    {
        using var kit = new UpdateTestKit();
        var (exe, staged, newBytes) = Setup(kit);

        UpdateInstaller.Install(staged, exe);

        Assert.Equal(newBytes, File.ReadAllBytes(exe));
        Assert.True(File.Exists(exe + UpdateInstaller.OldSuffix));
        Assert.False(File.Exists(exe + UpdateInstaller.NewSuffix));

        await UpdateInstaller.CleanupAsync(exe, TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(exe + UpdateInstaller.OldSuffix));
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public void Install_RefusesAStagedFileChangedAfterTheCheck_AndKeepsTheOldExe()
    {
        using var kit = new UpdateTestKit();
        var old = UpdateTestKit.FakeExe(1);
        var (exe, staged, _) = Setup(kit, old);
        File.WriteAllBytes(staged.ExePath, UpdateTestKit.FakeExe(9)); // same size, other bytes

        Assert.Throws<UpdateException>(() => UpdateInstaller.Install(staged, exe));

        Assert.Equal(old, File.ReadAllBytes(exe));
        Assert.False(File.Exists(exe + UpdateInstaller.NewSuffix));
        Assert.False(File.Exists(exe + UpdateInstaller.OldSuffix));
    }

    [Fact]
    public void Install_WhenTheExeCannotBeMoved_LeavesEverythingAsItWas()
    {
        using var kit = new UpdateTestKit();
        var old = UpdateTestKit.FakeExe(1);
        var (exe, staged, _) = Setup(kit, old);
        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read)) // no FILE_SHARE_DELETE: rename fails
            Assert.ThrowsAny<IOException>(() => UpdateInstaller.Install(staged, exe));

        Assert.Equal(old, File.ReadAllBytes(exe));
        Assert.False(File.Exists(exe + UpdateInstaller.NewSuffix));
        Assert.False(File.Exists(exe + UpdateInstaller.OldSuffix));
    }

    [Fact]
    public async Task Install_ReplacesARunningExe()
    {
        // Windows lets a running exe be renamed: the updater relies on this. A copy of ping.exe stands in for the app.
        using var kit = new UpdateTestKit();
        var (exe, staged, newBytes) = Setup(kit);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe, overwrite: true);
        using var running = Process.Start(new ProcessStartInfo(exe, "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            UpdateInstaller.Install(staged, exe);
            Assert.Equal(newBytes, File.ReadAllBytes(exe));
        }
        finally
        {
            running.Kill();
            running.WaitForExit();
        }
        await UpdateInstaller.CleanupAsync(exe, TimeSpan.FromSeconds(10));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, "*" + UpdateInstaller.OldSuffix));
    }
}
