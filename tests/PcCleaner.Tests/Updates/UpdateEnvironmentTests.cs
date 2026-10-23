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

    [Theory]
    [InlineData(@"C:\Program Files\WaterCa7s PC Cleaner\WaterCa7sPcCleaner.exe", @"C:\Program Files", true)]
    [InlineData(@"c:\program files\WaterCa7s PC Cleaner\x.exe", @"C:\Program Files\", true)]      // case and a trailing separator
    [InlineData(@"C:\Program Files\A\..\B\x.exe", @"C:\Program Files", true)]                 // full paths are compared
    [InlineData(@"C:\Users\me\AppData\Local\WaterCa7s PC Cleaner\x.exe", @"C:\Program Files", false)]
    [InlineData(@"D:\Tools\x.exe", @"C:\Program Files", false)]
    [InlineData(@"C:\Program Files (x86)\x.exe", @"C:\Program Files", false)]                   // the prefix trap
    [InlineData(@"C:\Program Files\..\Users\x.exe", @"C:\Program Files", false)]
    [InlineData(@"C:\Program Files", @"C:\Program Files", false)]                                // the folder itself is not inside it
    [InlineData(@"C:\Program Files\x.exe", "", false)]
    public void IsInside_ComparesWholeFolderNames(string path, string folder, bool expected)
        => Assert.Equal(expected, UpdateEnvironment.IsInside(path, folder));

    [Fact]
    public void TheProgramIsInProgramFiles_WhenTheExeLivesThere()
    {
        var env = new UpdateEnvironment
        {
            ExePath = @"C:\Program Files\WaterCa7s PC Cleaner\WaterCa7sPcCleaner.exe",
            UpdatesDir = "x",
            CurrentVersion = new Version(1, 0, 0),
            ProgramFiles = @"C:\Program Files",
        };

        Assert.True(env.IsInProgramFiles);
        Assert.False(new UpdateEnvironment { ExePath = @"D:\Tools\WaterCa7sPcCleaner.exe", UpdatesDir = "x", CurrentVersion = new Version(1, 0, 0), ProgramFiles = @"C:\Program Files" }.IsInProgramFiles);
    }

    [Fact]
    public void TheManifestNamesTheSetup()
        => Assert.Equal("win-x64-setup", UpdateManifest.Platform);
}
