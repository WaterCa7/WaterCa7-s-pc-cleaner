using System.IO;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

/// <summary>Which copy of the program updates itself: only the one the setup installed (the marker next to the exe).</summary>
public class UpdateEnvironmentTests
{
    public UpdateEnvironmentTests() => TestEnvironment.Init();

    private static UpdateEnvironment In(string dir, string exeName = UpdateEnvironment.ExeName)
        => new() { ExePath = Path.Combine(dir, exeName), UpdatesDir = Path.Combine(dir, "data"), CurrentVersion = new Version(1, 0, 0) };

    [Fact]
    public void TheInstalledCopy_HasTheMarkerNextToTheExe()
    {
        var dir = TestEnvironment.NewTempDir("env-installed");
        File.WriteAllBytes(Path.Combine(dir, UpdateEnvironment.ExeName), new byte[] { 1 });
        File.WriteAllText(Path.Combine(dir, UpdateEnvironment.InstalledMarkerName), "");
        File.WriteAllBytes(Path.Combine(dir, "WaterCa7sPcCleaner.dll"), new byte[] { 1 }); // the folder build always has it

        Assert.True(In(dir).IsInstalledBuild);
    }

    [Fact]
    public void WithoutTheMarker_ItIsADevelopmentBuild()
    {
        var dir = TestEnvironment.NewTempDir("env-dev");
        File.WriteAllBytes(Path.Combine(dir, UpdateEnvironment.ExeName), new byte[] { 1 });

        Assert.False(In(dir).IsInstalledBuild);
    }

    [Fact]
    public void ARenamedExe_NeverUpdates()
    {
        var dir = TestEnvironment.NewTempDir("env-renamed");
        File.WriteAllBytes(Path.Combine(dir, "Cleaner (2).exe"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(dir, UpdateEnvironment.InstalledMarkerName), "");

        Assert.False(In(dir, "Cleaner (2).exe").IsInstalledBuild);
    }

    [Fact]
    public void TheSetupIsKeptInTheUpdatesFolderOfTheProgram()
        => Assert.Equal(@"C:\Program Files\WaterCa7s PC Cleaner\Updates",
            new UpdateEnvironment
            {
                ExePath = @"C:\Program Files\WaterCa7s PC Cleaner\WaterCa7sPcCleaner.exe",
                UpdatesDir = "x",
                CurrentVersion = new Version(1, 0, 0),
            }.SetupDir);

    [Fact]
    public void TheManifestNamesTheSetup()
        => Assert.Equal("win-x64-setup", UpdateManifest.Platform);
}
