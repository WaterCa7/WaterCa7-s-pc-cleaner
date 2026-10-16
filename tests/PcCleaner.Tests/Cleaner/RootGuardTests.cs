using System.IO;
using PcCleaner.Modules.Cleaner;
using Xunit;

namespace PcCleaner.Tests.Cleaner;

/// <summary>Pure path checks only — nothing is deleted here.</summary>
public class RootGuardTests
{
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static IEnumerable<object[]> DangerousRoots()
    {
        var drive = Path.GetPathRoot(Windows)!;
        yield return new object[] { drive };
        yield return new object[] { drive.TrimEnd('\\') };
        yield return new object[] { Windows };
        yield return new object[] { Environment.SystemDirectory };
        yield return new object[] { Path.Combine(Environment.SystemDirectory, "drivers") };
        yield return new object[] { Path.Combine(Windows, "WinSxS") };
        yield return new object[] { Path.Combine(Windows, "Installer") };
        yield return new object[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        yield return new object[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files") };
        yield return new object[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        yield return new object[] { Profile };
        yield return new object[] { Path.GetDirectoryName(Profile)! };
        yield return new object[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        yield return new object[] { Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        yield return new object[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Projects") };
        yield return new object[] { Path.Combine(Profile, "Downloads") };
        yield return new object[] { Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) };
        yield return new object[] { Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
        yield return new object[] { Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) };
        yield return new object[] { Local };
        yield return new object[] { Roaming };
        yield return new object[] { Path.GetDirectoryName(Local)! }; // AppData itself contains Local and Roaming
        yield return new object[] { ProgramData };
        yield return new object[] { Path.Combine(Local, "Microsoft") };
        yield return new object[] { Path.Combine(Local, "Packages") };
        yield return new object[] { "relative\\path" };
        yield return new object[] { @"\\server\share\cache" };
        yield return new object[] { @"\\?\C:\Windows" };
        yield return new object[] { "" };
    }

    [Theory]
    [MemberData(nameof(DangerousRoots))]
    public void SystemGuard_RejectsDangerousRoots(string root)
    {
        var guard = RootGuard.ForEnvironment(CleanerEnvironment.FromSystem());
        Assert.False(guard.IsSafeFolder(root, out var reason), $"«{root}» must be refused");
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void SystemGuard_AcceptsUsualCacheFolders()
    {
        var guard = RootGuard.ForEnvironment(CleanerEnvironment.FromSystem());
        Assert.True(guard.IsSafeFolder(Path.GetTempPath(), out _));
        Assert.True(guard.IsSafeFolder(Path.Combine(Windows, "Temp"), out _));
        Assert.True(guard.IsSafeFolder(Path.Combine(Windows, @"SoftwareDistribution\Download"), out _));
        Assert.True(guard.IsSafeFolder(Path.Combine(Local, @"Google\Chrome\User Data\Default\Cache\Cache_Data"), out _));
        Assert.True(guard.IsSafeFolder(Path.Combine(Local, @"Microsoft\Windows\Explorer"), out _));
        Assert.True(guard.IsSafeFolder(Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"), out _));
        Assert.True(guard.IsSafeFile(Path.Combine(Windows, "MEMORY.DMP"), out _));
    }

    [Fact]
    public void SystemGuard_RejectsSingleFilesWithUnexpectedExtensionsOrInSystem32()
    {
        var guard = RootGuard.ForEnvironment(CleanerEnvironment.FromSystem());
        Assert.False(guard.IsSafeFile(Path.Combine(Windows, "explorer.exe"), out _));
        Assert.False(guard.IsSafeFile(Path.Combine(Environment.SystemDirectory, "crash.dmp"), out _));
        Assert.False(guard.IsSafeFile(Path.Combine(Path.GetPathRoot(Windows)!, "MEMORY.DMP"), out _));
    }

    [Fact]
    public void TestGuard_AllowsOnlyInsideItsBase()
    {
        using var t = new CleanerTestFolder("guard");
        var guard = t.Guard();
        Assert.True(guard.IsSafeFolder(t.Root, out _));
        Assert.True(guard.IsSafeFolder(t.Path("cache", "sub"), out _));
        Assert.False(guard.IsSafeFolder(Path.GetDirectoryName(t.Root)!, out _));
        Assert.False(guard.IsSafeFolder(t.Root + "-sibling", out _));
        Assert.False(guard.IsSafeFolder(Path.Combine(t.Root, "..", "escape"), out _));
    }

    [Fact]
    public void EveryCatalogRoot_OnThisPc_PassesTheGuard()
    {
        // Read-only consistency check: the catalog must only produce roots the guard accepts.
        var env = CleanerEnvironment.FromSystem();
        var guard = RootGuard.ForEnvironment(env);
        var categories = CleanerCatalog.Build(env, 24);
        foreach (var root in categories.SelectMany(c => c.Roots))
            Assert.True(guard.IsSafeRoot(root, out var reason), $"{root.Path}: {reason}");
    }

    [Fact]
    public void EveryCatalogRoot_InFakeEnvironment_PassesTheGuard()
    {
        using var t = new CleanerTestFolder("guard-env");
        var env = CleanerEnvironment.ForTests(t.Root);
        CatalogTests.CreateFakeInstallation(t, env);
        var guard = RootGuard.ForEnvironment(env);
        var categories = CleanerCatalog.Build(env, 24);
        Assert.NotEmpty(categories.SelectMany(c => c.Roots));
        foreach (var root in categories.SelectMany(c => c.Roots))
            Assert.True(guard.IsSafeRoot(root, out var reason), $"{root.Path}: {reason}");
    }
}
