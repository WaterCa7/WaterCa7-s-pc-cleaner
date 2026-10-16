using System.Diagnostics;
using System.IO;

namespace PcCleaner.Tests.Disk;

/// <summary>Temp folder helper: files of exact sizes, junctions, and safe cleanup (links are removed first).</summary>
internal sealed class DiskTestTree : IDisposable
{
    private readonly List<string> _junctions = new();

    public DiskTestTree(string name = "disk")
    {
        TestEnvironment.Init();
        Root = TestEnvironment.NewTempDir(name);
    }

    public string Root { get; }

    public string Dir(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relative, long size, DateTime? time = null)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            fs.SetLength(size);
        if (time is not null)
        {
            System.IO.File.SetCreationTime(path, time.Value);
            System.IO.File.SetLastWriteTime(path, time.Value);
        }
        return path;
    }

    /// <summary>Creates a directory junction (no admin rights needed) with "cmd /c mklink /J".</summary>
    public string Junction(string relativeLink, string target)
    {
        var link = Path.Combine(Root, relativeLink);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit(15000);
        if (!Directory.Exists(link) || !new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Не удалось создать junction: " + process.StandardError.ReadToEnd());
        _junctions.Add(link);
        return link;
    }

    public void Dispose()
    {
        // Remove links first so that nothing can ever be deleted through them.
        foreach (var link in _junctions)
        {
            try
            {
                if (Directory.Exists(link))
                    Directory.Delete(link, recursive: false);
            }
            catch (IOException)
            {
            }
        }
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
