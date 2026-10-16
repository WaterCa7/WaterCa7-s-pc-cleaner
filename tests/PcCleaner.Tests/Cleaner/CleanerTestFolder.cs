using System.Diagnostics;
using System.IO;
using PcCleaner.Modules.Cleaner;
using Xunit;

namespace PcCleaner.Tests.Cleaner;

/// <summary>
/// A throw-away folder under the test temp root. Everything the cleaner tests create or delete lives here;
/// junctions are removed (as links, never followed) before the folder is deleted.
/// </summary>
internal sealed class CleanerTestFolder : IDisposable
{
    private readonly List<string> _junctions = new();

    public CleanerTestFolder(string name = "cleaner")
    {
        TestEnvironment.Init();
        // Not under TestEnvironment.DataDir: the app's data folder is always excluded from cleaning.
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WaterCa7sPcCleaner.CleanerTests",
            name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path(params string[] parts) => System.IO.Path.Combine(new[] { Root }.Concat(parts).ToArray());

    public string Dir(params string[] parts)
    {
        var dir = Path(parts);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Creates a file of the given size; by default it looks old (3 days) so age rules do not hide it.</summary>
    public string File(string relativePath, int size = 100, TimeSpan? age = null)
    {
        var path = Path(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        SetAge(path, age ?? TimeSpan.FromDays(3));
        return path;
    }

    public static void SetAge(string path, TimeSpan age)
    {
        var time = DateTime.UtcNow - age;
        System.IO.File.SetCreationTimeUtc(path, time);
        System.IO.File.SetLastWriteTimeUtc(path, time);
    }

    /// <summary>Creates a directory junction with "mklink /J" (no admin rights needed).</summary>
    public void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using (var p = Process.Start(psi)!)
        {
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
        }
        _junctions.Add(link);
        Assert.True(Directory.Exists(link), "junction was not created");
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), "link is not a reparse point");
    }

    public static CleanerCategory Category(string id, params CleanRoot[] roots) => new()
    {
        Id = id,
        Group = CleanerCategory.GroupWindows,
        Name = "Тест " + id,
        Description = "Тестовая категория.",
        Roots = roots,
    };

    public RootGuard Guard() => RootGuard.ForTests(Root);

    public static CleanerRules Rules(TimeSpan? minAge = null, IEnumerable<string>? exclusions = null, bool elevated = false)
        => new(minAge ?? TimeSpan.Zero, exclusions, elevated);

    public void Dispose()
    {
        foreach (var link in _junctions)
        {
            try
            {
                if (Directory.Exists(link))
                    Directory.Delete(link); // removes only the link, never the target's content
            }
            catch (IOException)
            {
                // ignore
            }
        }
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    System.IO.File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Root, recursive: true);
            }
            var parent = System.IO.Path.GetDirectoryName(Root)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort; the folder is inside the test temp root
        }
    }
}
