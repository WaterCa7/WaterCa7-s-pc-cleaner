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

        var path = UpdateInstaller.PrepareSetup(Staged(setup, setup), setupDir);

        Assert.Equal(Path.Combine(setupDir, UpdateEnvironment.SetupName), path);
        Assert.Equal(setup, File.ReadAllBytes(path));
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

        var path = UpdateInstaller.PrepareSetup(Staged(setup, setup), setupDir);

        Assert.Equal(setup, File.ReadAllBytes(path));
    }

    [Fact]
    public void SetupArgs_AreSilentAndNameTheVersionWeCameFrom()
        => Assert.Equal(new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/UPDATEDFROM=1.3.1" },
            UpdateInstaller.SetupArgs(new Version(1, 3, 1)));

    [Fact]
    public async Task CleanupSetup_RemovesTheFolder_OnceTheSetupHasExited()
    {
        var setupDir = Path.Combine(TestEnvironment.NewTempDir("inst-app"), "Updates");
        Directory.CreateDirectory(setupDir);
        var file = Path.Combine(setupDir, UpdateEnvironment.SetupName);
        File.WriteAllBytes(file, new byte[] { 1 });
        var running = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); // the setup is still running
        _ = Task.Run(async () =>
        {
            await Task.Delay(700);
            running.Dispose();
        });

        await UpdateInstaller.CleanupSetupAsync(setupDir, TimeSpan.FromSeconds(10));

        Assert.False(Directory.Exists(setupDir));
    }

    [Fact]
    public async Task CleanupSetup_WithoutAFolder_DoesNothing()
        => await UpdateInstaller.CleanupSetupAsync(Path.Combine(TestEnvironment.NewTempDir("inst-app"), "Updates"), TimeSpan.FromSeconds(1));
}
