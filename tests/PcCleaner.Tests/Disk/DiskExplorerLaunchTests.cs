using System.IO;
using System.Text.RegularExpressions;
using PcCleaner.Modules.Disk;
using Xunit;

namespace PcCleaner.Tests.Disk;

/// <summary>
/// «Показать в проводнике» and the other launches of the Disk page start programs by their full path: CreateProcess
/// looks in the application's own folder first, so a bare "explorer.exe" would run a planted explorer.exe from next
/// to our exe (e.g. a portable copy in Downloads) with our — possibly administrator — rights. Nothing is started here.
/// </summary>
public class DiskExplorerLaunchTests
{
    public DiskExplorerLaunchTests() => TestEnvironment.Init();

    [Fact]
    public void ExplorerPath_IsTheFullPathInTheWindowsFolder()
    {
        var path = SystemTools.ExplorerPath;

        Assert.True(Path.IsPathFullyQualified(path), path);
        Assert.Equal("explorer.exe", Path.GetFileName(path), ignoreCase: true);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Path.GetDirectoryName(path),
            ignoreCase: true);
        Assert.True(File.Exists(path), path);
    }

    [Fact]
    public void ExplorerArguments_SelectAFile_OpenAFolder_NothingForAMissingPath()
    {
        var dir = TestEnvironment.NewTempDir("explorer args");
        var file = Path.Combine(dir, "отчёт, 2024.txt");
        File.WriteAllText(file, "x");

        Assert.Equal($"/select,\"{file}\"", SystemTools.ExplorerArguments(file));
        Assert.Equal($"\"{dir}\"", SystemTools.ExplorerArguments(dir));
        Assert.Null(SystemTools.ExplorerArguments(Path.Combine(dir, "gone.txt")));
        Assert.Null(SystemTools.ExplorerArguments(Path.Combine(dir, "gone")));
    }

    /// <summary>
    /// Guards every future copy: a string literal without a folder as the program of Process.Start,
    /// ProcessStartInfo, SystemTools.Launch / RunStreamingAsync or a FileName initializer is a search-order hijack.
    /// (ProcessRunner.RunAsync resolves bare names itself, and OpenToolAsync wraps its name in SystemTools.Exe.)
    /// </summary>
    [Fact]
    public void DiskModule_StartsNoProgramByBareName()
    {
        var bareProgram = new Regex(
            @"(?:Process\.Start|new\s+ProcessStartInfo|\bLaunch|RunStreamingAsync)\s*\(\s*""(?<name>[^""\\/]*)""" +
            @"|\bFileName\s*=\s*""(?<name>[^""\\/]*)""",
            RegexOptions.CultureInvariant);

        var sources = Directory.GetFiles(DiskSourceFolder(), "*.cs", SearchOption.AllDirectories);
        Assert.Contains(sources, s => Path.GetFileName(s) == "SystemTools.cs");

        var offenders = new List<string>();
        foreach (var source in sources)
        {
            var lines = File.ReadAllLines(source);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in bareProgram.Matches(lines[i]))
                    offenders.Add($"{Path.GetFileName(source)}:{i + 1}: \"{m.Groups["name"].Value}\"");
            }
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void BareNameGuard_CatchesTheOldExplorerLaunch()
    {
        // The guard itself must recognise the defect it protects against.
        var bareProgram = new Regex(@"(?:Process\.Start|new\s+ProcessStartInfo)\s*\(\s*""(?<name>[^""\\/]*)""");
        Assert.Matches(bareProgram, "Process.Start(\"explorer.exe\", $\"/select,\\\"{path}\\\"\")?.Dispose();");
        Assert.DoesNotMatch(bareProgram, "Process.Start(ExplorerPath, $\"/select,\\\"{path}\\\"\")?.Dispose();");
    }

    private static string DiskSourceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PcCleaner", "Modules", "Disk");
            if (File.Exists(Path.Combine(candidate, "SystemTools.cs")))
                return candidate;
        }
        throw new DirectoryNotFoundException($"src/PcCleaner/Modules/Disk not found above {AppContext.BaseDirectory}");
    }
}
