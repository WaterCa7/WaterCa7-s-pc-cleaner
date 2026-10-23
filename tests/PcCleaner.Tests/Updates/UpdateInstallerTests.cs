using System.IO;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

/// <summary>The verified copy of the downloaded setup and the folder it is started from (temp folders only).</summary>
public class UpdateInstallerTests
{
    public UpdateInstallerTests() => TestEnvironment.Init();

    private static StagedUpdate Staged(byte[] served, byte[] promised)
    {
        var dir = TestEnvironment.NewTempDir("inst-staged");
        var path = Path.Combine(dir, UpdateEnvironment.SetupName);
        File.WriteAllBytes(path, served);
        var manifest = UpdateManifest.Parse(UpdateTestKit.ManifestJson("1.2.0", promised.Length, UpdateTestKit.Sha256(promised)), out _)!;
        return new StagedUpdate(manifest, path);
    }

    [Fact]
    public void PrepareSetup_CopiesTheVerifiedBytesIntoTheSetupFolder()
    {
        var setup = UpdateTestKit.FakeExe(7);
        var setupDir = Path.Combine(TestEnvironment.NewTempDir("inst-app"), "Updates");

        using var prepared = UpdateInstaller.PrepareSetup(Staged(setup, setup), setupDir);

        Assert.Equal(Path.Combine(setupDir, UpdateEnvironment.SetupName), prepared.Path);
        Assert.Equal(setup, File.ReadAllBytes(prepared.Path));
    }

    [Fact]
    public void PrepareSetup_RefusesAFileThatDiffersFromUpdateJson_AndLeavesNothing()
    {
        var setupDir = Path.Combine(TestEnvironment.NewTempDir("inst-app"), "Updates");

        Assert.Throws<UpdateException>(() => UpdateInstaller.PrepareSetup(Staged(UpdateTestKit.FakeExe(8), UpdateTestKit.FakeExe(7)), setupDir));

        Assert.Empty(Directory.EnumerateFiles(setupDir));
    }

    [Fact]
    public void PrepareSetup_ReplacesAnOlderCopy()
    {
        var setupDir = Path.Combine(TestEnvironment.NewTempDir("inst-app"), "Updates");
        Directory.CreateDirectory(setupDir);
        File.WriteAllBytes(Path.Combine(setupDir, UpdateEnvironment.SetupName), new byte[] { 1, 2, 3 });
        var setup = UpdateTestKit.FakeExe(9);

        using var prepared = UpdateInstaller.PrepareSetup(Staged(setup, setup), setupDir);

        Assert.Equal(setup, File.ReadAllBytes(prepared.Path));
    }

    [Fact]
    public void PrepareSetup_KeepsTheCopyLockedAgainstWritesAndDeletes_UntilTheLeaseIsDisposed()
    {
        var setup = UpdateTestKit.FakeExe(4);
        var setupDir = Path.Combine(TestEnvironment.NewTempDir("inst-app"), "Updates");
        var prepared = UpdateInstaller.PrepareSetup(Staged(setup, setup), setupDir);
        try
        {
            // the verified bytes cannot be changed directly until the process has been created from the copy
            Assert.True(Refuses(() => File.WriteAllBytes(prepared.Path, new byte[] { 9 })));
            Assert.True(Refuses(() => File.Delete(prepared.Path)));
            Assert.True(Refuses(() => File.Move(prepared.Path, prepared.Path + ".old")));
            // ... while a process can still be created from it (it only needs to read the file)
            using var reader = new FileStream(prepared.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            Assert.Equal(setup.Length, reader.Length);
        }
        finally
        {
            prepared.Dispose();
        }

        File.WriteAllBytes(prepared.Path, new byte[] { 9 });
        File.Delete(prepared.Path);
        Assert.False(File.Exists(prepared.Path));
    }

    [Fact]
    public void TheVerifiedCopy_CanReallyBeStarted_WhileItIsLocked()
    {
        // The real hand-off: Windows must be able to create a process from the copy while PreparedSetup holds it against writes and
        // deletes. whoami.exe stands in for the setup (signed, part of every Windows, prints the user name and exits 0 without
        // arguments). It is under the 1 MB that update.json requires of a setup, so zeros are appended: the loader ignores data
        // past the last section, and the manifest names the padded bytes, so the whole check of PrepareSetup runs on them.
        var whoami = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
        var original = File.ReadAllBytes(whoami);
        var setup = new byte[Math.Max(original.Length, (int)UpdateManifest.MinSize) + 4096];
        original.CopyTo(setup, 0);
        var setupDir = Path.Combine(TestEnvironment.NewTempDir("inst-start"), "Updates");

        using var prepared = UpdateInstaller.PrepareSetup(Staged(setup, setup), setupDir);
        using var process = UpdateEnvironment.TryStartProcess(prepared.Path, Array.Empty<string>(), out var error);

        Assert.Null(error);
        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(10_000), "the started copy did not exit in 10 s");
        Assert.Equal(0, process.ExitCode);
    }

    private static bool Refuses(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    [Fact]
    public void SetupArgs_ShowOnlyTheProgress_AskNothing_AndNameTheVersionWeCameFrom()
        => Assert.Equal(new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/UPDATEDFROM=1.3.1" },
            UpdateInstaller.SetupArgs(new Version(1, 3, 1)));

    [Fact]
    public async Task CleanupSetup_RemovesTheFolder_OnceTheSetupHasExited()
    {
        var setupDir = Path.Combine(TestEnvironment.NewTempDir("inst-app"), "Updates");
        Directory.CreateDirectory(setupDir);
        var file = Path.Combine(setupDir, UpdateEnvironment.SetupName);
        File.WriteAllBytes(file, new byte[] { 1 });
        var running = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); // the setup is still running
        try
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(700);
                running.Dispose();
            });

            await UpdateInstaller.CleanupSetupAsync(setupDir, TimeSpan.FromSeconds(10));

            Assert.False(Directory.Exists(setupDir));
        }
        finally
        {
            running.Dispose();
        }
    }

    [Fact]
    public async Task CleanupSetup_WithoutAFolder_DoesNothing()
        => await UpdateInstaller.CleanupSetupAsync(Path.Combine(TestEnvironment.NewTempDir("inst-app"), "Updates"), TimeSpan.FromSeconds(1));
}
