using System.Diagnostics;
using System.IO;
using PcCleaner.Modules.Programs;
using Xunit;

namespace PcCleaner.Tests.Programs;

public class FolderSizeTests
{
    public FolderSizeTests() => TestEnvironment.Init();

    [Fact]
    public void Measure_SumsFilesRecursively_AndSkipsExcludedFolders()
    {
        var root = TestEnvironment.NewTempDir("size");
        try
        {
            File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[1000]);
            Directory.CreateDirectory(Path.Combine(root, "sub", "deeper"));
            File.WriteAllBytes(Path.Combine(root, "sub", "b.bin"), new byte[500]);
            File.WriteAllBytes(Path.Combine(root, "sub", "deeper", "c.bin"), new byte[250]);
            var other = Path.Combine(root, "OtherProgram");
            Directory.CreateDirectory(other);
            File.WriteAllBytes(Path.Combine(other, "big.bin"), new byte[10_000]);

            Assert.Equal(11_750, FolderSize.Measure(root, CancellationToken.None));
            var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { other };
            Assert.Equal(1_750, FolderSize.Measure(root, CancellationToken.None, exclude: exclude));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Measure_MissingFolder_ReturnsNull()
        => Assert.Null(FolderSize.Measure(Path.Combine(TestEnvironment.DataDir, "does-not-exist"), CancellationToken.None));

    [Fact]
    public void Measure_DoesNotFollowJunctions()
    {
        var root = TestEnvironment.NewTempDir("junction");
        var target = TestEnvironment.NewTempDir("junction-target");
        var link = Path.Combine(root, "link");
        try
        {
            File.WriteAllBytes(Path.Combine(root, "own.bin"), new byte[100]);
            File.WriteAllBytes(Path.Combine(target, "outside.bin"), new byte[5000]);
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                   {
                       UseShellExecute = false,
                       CreateNoWindow = true,
                       RedirectStandardOutput = true,
                       RedirectStandardError = true,
                   }))
            {
                p!.WaitForExit(10_000);
            }
            if (!Directory.Exists(link))
                return; // junctions unavailable here - nothing to verify

            Assert.Equal(100, FolderSize.Measure(root, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link); // removes only the junction itself
            Directory.Delete(root, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }
}
