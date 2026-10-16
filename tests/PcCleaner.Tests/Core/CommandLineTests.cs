using System.IO;
using PcCleaner.Core;
using Xunit;

namespace PcCleaner.Tests.Core;

public class CommandLineTests
{
    private static readonly string Sys = Environment.SystemDirectory;

    [Fact]
    public void Quoted_PathAndArgs()
    {
        var (exe, args) = CommandLine.Split("\"C:\\Program Files\\App\\app.exe\" --minimized /x");
        Assert.Equal(@"C:\Program Files\App\app.exe", exe);
        Assert.Equal("--minimized /x", args);
    }

    [Fact]
    public void Unquoted_ExistingPathWithArgs()
    {
        var (exe, args) = CommandLine.Split($"{Sys}\\cmd.exe /c echo hi");
        Assert.Equal(Path.Combine(Sys, "cmd.exe"), exe, ignoreCase: true);
        Assert.Equal("/c echo hi", args);
    }

    [Fact]
    public void Unquoted_PathWithSpaces_UsesExtension()
    {
        var (exe, args) = CommandLine.Split(@"C:\Program Files\Some App\tool.exe -silent");
        Assert.Equal(@"C:\Program Files\Some App\tool.exe", exe);
        Assert.Equal("-silent", args);
    }

    [Fact]
    public void EnvironmentVariables_AreExpanded()
    {
        var (exe, _) = CommandLine.Split("%windir%\\system32\\cmd.exe /k");
        Assert.Equal(Path.Combine(Sys, "cmd.exe"), exe, ignoreCase: true);
    }

    [Fact]
    public void BareName_ResolvesToSystemFolder()
    {
        var (exe, _) = CommandLine.Split("cmd.exe /c dir");
        Assert.Equal(Path.Combine(Sys, "cmd.exe"), exe, ignoreCase: true);
    }

    [Fact]
    public void Rundll32_TargetIsTheDll()
    {
        var target = CommandLine.TargetFile(@"rundll32.exe C:\Users\x\AppData\Roaming\evil.dll,Start");
        Assert.Equal(@"C:\Users\x\AppData\Roaming\evil.dll", target);
    }

    [Fact]
    public void ServiceImagePath_SystemRootPrefix()
    {
        var (exe, _) = CommandLine.Split(@"\SystemRoot\System32\svchost.exe -k netsvcs");
        Assert.Equal(Path.Combine(Sys, "svchost.exe"), exe, ignoreCase: true);
    }

    [Fact]
    public void Empty_ReturnsNull()
        => Assert.Null(CommandLine.Split("  ").Executable);
}

public class WmiTests
{
    [Fact]
    public void OperatingSystem_QueryReturnsCaption()
    {
        var rows = Wmi.Query("SELECT Caption, LastBootUpTime FROM Win32_OperatingSystem");
        Assert.Single(rows);
        Assert.Contains("Windows", rows[0].Str("Caption"));
        Assert.NotNull(rows[0].Date("LastBootUpTime"));
    }

    [Fact]
    public void InvalidNamespace_ReturnsEmpty()
    {
        PcCleaner.Tests.TestEnvironment.Init();
        Assert.Empty(Wmi.Query("SELECT * FROM Nothing", @"root\does\not\exist"));
    }
}
