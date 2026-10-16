using System.IO;
using PcCleaner.Modules.Programs;
using Xunit;

namespace PcCleaner.Tests.Programs;

public class ProgramRulesTests
{
    private const string Guid1 = "{0158093D-F809-455B-9429-6D16A4B5D118}";

    public ProgramRulesTests() => TestEnvironment.Init();

    // ---------------------------------------------------------------- InstallDate

    [Theory]
    [InlineData("20240115", 2024, 1, 15)]
    [InlineData(" 20231231 ", 2023, 12, 31)]
    [InlineData("2024-03-05", 2024, 3, 5)]
    [InlineData("05.03.2024", 2024, 3, 5)]
    [InlineData("3/5/2024", 2024, 3, 5)]
    public void ParseInstallDate_ParsesKnownFormats(string value, int y, int m, int d)
        => Assert.Equal(new DateTime(y, m, d), ProgramRules.ParseInstallDate(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("20241345")]
    [InlineData("19000101")]
    [InlineData("0")]
    public void ParseInstallDate_InvalidValues_ReturnNull(string? value)
        => Assert.Null(ProgramRules.ParseInstallDate(value));

    [Fact]
    public void EstimatedSize_IsKilobytes()
    {
        Assert.Equal(2048L * 1024, ProgramRules.EstimatedSizeToBytes(2048));
        Assert.Equal(0, ProgramRules.EstimatedSizeToBytes(null));
        Assert.Equal(0, ProgramRules.EstimatedSizeToBytes(0));
        Assert.Equal(0, ProgramRules.EstimatedSizeToBytes(-5));
    }

    // ---------------------------------------------------------------- filtering

    [Fact]
    public void ShouldInclude_NormalProgram()
        => Assert.True(ProgramRules.ShouldInclude(new UninstallEntryValues { KeyName = "App", DisplayName = "App" }));

    [Theory]
    [InlineData(null, false, null, null)]
    [InlineData("  ", false, null, null)]
    [InlineData("Hidden runtime part", true, null, null)]
    [InlineData("Office patch", false, "Office16", null)]
    [InlineData("KB5001", false, null, "Update")]
    [InlineData("KB5002", false, null, "Hotfix")]
    [InlineData("KB5003", false, null, "Security Update")]
    [InlineData("KB5004", false, null, "security update")]
    public void ShouldInclude_SkipsHiddenAndUpdates(string? name, bool systemComponent, string? parent, string? releaseType)
        => Assert.False(ProgramRules.ShouldInclude(new UninstallEntryValues
        {
            KeyName = "K",
            DisplayName = name,
            SystemComponent = systemComponent,
            ParentKeyName = parent,
            ReleaseType = releaseType,
        }));

    // ---------------------------------------------------------------- uninstall strings

    [Theory]
    [InlineData("MsiExec.exe /I" + Guid1)]
    [InlineData("MsiExec.exe /X" + Guid1)]
    [InlineData("msiexec /x " + Guid1 + " /qn")]
    [InlineData("\"C:\\Windows\\System32\\msiexec.exe\" /I " + Guid1)]
    [InlineData("MsiExec.exe /i{0158093d-f809-455b-9429-6d16a4b5d118}")]
    public void BuildLaunch_Msi_AlwaysUninstallsWithSlashX(string uninstallString)
    {
        var launch = ProgramRules.BuildLaunch(uninstallString, windowsInstaller: false, keyName: "Whatever");
        Assert.NotNull(launch);
        Assert.True(launch!.IsMsi);
        Assert.Equal(Guid1, launch.ProductCode);
        Assert.Equal("/x " + Guid1, launch.Arguments);
        Assert.Equal(ProgramRules.MsiExecPath, launch.FileName);
    }

    [Fact]
    public void BuildLaunch_WindowsInstallerFlag_UsesKeyNameAsProductCode()
    {
        var launch = ProgramRules.BuildLaunch(@"C:\ProgramData\Package Cache\x\setup.exe /uninstall", windowsInstaller: true, keyName: Guid1);
        Assert.NotNull(launch);
        Assert.True(launch!.IsMsi);
        Assert.Equal("/x " + Guid1, launch.Arguments);
    }

    [Fact]
    public void ExtractProductCode_OnlyForMsiexec()
    {
        Assert.Equal(Guid1, ProgramRules.ExtractProductCode("MsiExec.exe /I" + Guid1));
        Assert.Null(ProgramRules.ExtractProductCode(@"""C:\Program Files\App\setup.exe"" /x " + Guid1));
        Assert.Null(ProgramRules.ExtractProductCode(null));
    }

    [Fact]
    public void BuildLaunch_QuotedPathWithArguments()
    {
        var launch = ProgramRules.BuildLaunch(@"""C:\Program Files\Some App\unins000.exe"" /SILENT", false, "Some App_is1");
        Assert.NotNull(launch);
        Assert.False(launch!.IsMsi);
        Assert.Equal(@"C:\Program Files\Some App\unins000.exe", launch.FileName);
        Assert.Equal("/SILENT", launch.Arguments);
        Assert.Equal(@"C:\Program Files\Some App", launch.WorkingDirectory);
    }

    [Fact]
    public void BuildLaunch_UnquotedPathWithSpaces_FindsExistingFile()
    {
        var dir = Path.Combine(TestEnvironment.NewTempDir("unq"), "My App Folder");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "uninst.exe");
        File.WriteAllText(exe, "not a real program");
        try
        {
            var launch = ProgramRules.BuildLaunch(exe + " /S /allusers", false, "MyApp");
            Assert.NotNull(launch);
            Assert.Equal(exe, launch!.FileName, ignoreCase: true);
            Assert.Equal("/S /allusers", launch.Arguments);
            Assert.Equal(dir, launch.WorkingDirectory, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    [Fact]
    public void BuildLaunch_Rundll32Style()
    {
        var launch = ProgramRules.BuildLaunch(
            @"RunDll32 C:\PROGRA~2\COMMON~1\INSTAL~1\PROFES~1\RunTime\10\01\Intel32\Ctor.dll,LaunchSetup ""C:\Program Files (x86)\InstallShield Installation Information\{X}\setup.exe"" -l0x9",
            false, "Old App");
        Assert.NotNull(launch);
        Assert.False(launch!.IsMsi);
        Assert.Equal("rundll32.exe", Path.GetFileName(launch.FileName), ignoreCase: true);
        Assert.StartsWith(@"C:\PROGRA~2\COMMON~1", launch.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildLaunch_Empty_ReturnsNull(string? value)
        => Assert.Null(ProgramRules.BuildLaunch(value, false, "X"));

    [Fact]
    public void BuildQuietLaunch_KeepsInstallerArguments()
    {
        var msi = ProgramRules.BuildQuietLaunch("MsiExec.exe /X" + Guid1 + " /qn");
        Assert.NotNull(msi);
        Assert.True(msi!.IsMsi);
        Assert.Equal(ProgramRules.MsiExecPath, msi.FileName);
        Assert.Contains("/qn", msi.Arguments);

        var exe = ProgramRules.BuildQuietLaunch(@"""C:\Program Files\App\uninstall.exe"" /S");
        Assert.NotNull(exe);
        Assert.Equal("/S", exe!.Arguments);
        Assert.Null(ProgramRules.BuildQuietLaunch(null));
    }

    // ---------------------------------------------------------------- broken entries

    [Fact]
    public void IsBroken_UninstallerAndFolderMissing()
    {
        var root = TestEnvironment.NewTempDir("broken");
        var v = new UninstallEntryValues
        {
            KeyName = "Gone",
            DisplayName = "Gone",
            UninstallString = $"\"{Path.Combine(root, "Gone", "uninstall.exe")}\" /S",
            InstallLocation = Path.Combine(root, "Gone"),
        };
        Assert.True(ProgramRules.IsBroken(v));
        Assert.True(ProgramRules.IsBroken(new UninstallEntryValues { KeyName = "G2", DisplayName = "G2", UninstallString = v.UninstallString }));
    }

    [Fact]
    public void IsBroken_FolderStillExists_IsNotBroken()
    {
        var root = TestEnvironment.NewTempDir("folder");
        var v = new UninstallEntryValues
        {
            KeyName = "App",
            DisplayName = "App",
            UninstallString = $"\"{Path.Combine(root, "missing-uninstall.exe")}\"",
            InstallLocation = root,
        };
        Assert.False(ProgramRules.IsBroken(v));
    }

    [Fact]
    public void IsBroken_UninstallerExists_IsNotBroken()
    {
        var root = TestEnvironment.NewTempDir("exists");
        var exe = Path.Combine(root, "uninst.exe");
        File.WriteAllText(exe, "x");
        Assert.False(ProgramRules.IsBroken(new UninstallEntryValues { KeyName = "A", DisplayName = "A", UninstallString = $"\"{exe}\" /S" }));
    }

    [Fact]
    public void IsBroken_MsiAndEmptyAndMissingDrive_AreNotBroken()
    {
        Assert.False(ProgramRules.IsBroken(new UninstallEntryValues { KeyName = Guid1, DisplayName = "M", UninstallString = "MsiExec.exe /X" + Guid1 }));
        Assert.False(ProgramRules.IsBroken(new UninstallEntryValues { KeyName = Guid1, DisplayName = "M", WindowsInstaller = true, UninstallString = @"C:\nope\setup.exe" }));
        Assert.False(ProgramRules.IsBroken(new UninstallEntryValues { KeyName = "E", DisplayName = "E" }));

        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = Enumerable.Range('D', 23).Select(c => (char)c).Reverse().FirstOrDefault(c => !used.Contains(c));
        if (free != default)
        {
            Assert.False(ProgramRules.IsBroken(new UninstallEntryValues
            {
                KeyName = "Ext", DisplayName = "Game on unplugged disk", UninstallString = $@"""{free}:\Games\X\unins000.exe""",
            }));
        }
    }

    [Fact]
    public void IsBroken_Rundll32WithMissingDll()
    {
        var root = TestEnvironment.NewTempDir("dll");
        var v = new UninstallEntryValues
        {
            KeyName = "Old",
            DisplayName = "Old",
            UninstallString = $"rundll32.exe {Path.Combine(root, "gone.dll")},Uninstall",
        };
        Assert.True(ProgramRules.IsBroken(v));
    }

    [Fact]
    public void IsBroken_UsesInjectedFileSystem()
    {
        var v = new UninstallEntryValues
        {
            KeyName = "Fake", DisplayName = "Fake", UninstallString = @"""C:\Fake Folder\uninstall.exe""", InstallLocation = @"C:\Fake Folder",
        };
        Assert.True(ProgramRules.IsBroken(v, _ => false, d => d.Length <= 3));
        Assert.False(ProgramRules.IsBroken(v, _ => true, _ => true));
        Assert.False(ProgramRules.IsBroken(v, _ => false, _ => true));
    }

    // ---------------------------------------------------------------- flags

    [Fact]
    public void IsRecent_Within14Days()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0);
        Assert.True(ProgramRules.IsRecent(new DateTime(2026, 10, 16), now));
        Assert.True(ProgramRules.IsRecent(new DateTime(2026, 10, 2), now));
        Assert.False(ProgramRules.IsRecent(new DateTime(2026, 10, 1), now));
        Assert.False(ProgramRules.IsRecent(new DateTime(2027, 1, 1), now));
        Assert.False(ProgramRules.IsRecent(null, now));
    }

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135", "Microsoft Corporation", true)]
    [InlineData("Microsoft Visual C++ 2010  x64 Redistributable - 10.0.40219", "Microsoft Corporation", true)]
    [InlineData("Microsoft Windows Desktop Runtime - 7.0.0 (x64)", "Microsoft Corporation", true)]
    [InlineData("Microsoft .NET Runtime - 8.0.1 (x64)", "Microsoft Corporation", true)]
    [InlineData("Microsoft ASP.NET Core 7.0.0 Shared Framework (x64)", "Microsoft Corporation", true)]
    [InlineData("Microsoft Edge WebView2 Runtime", "Корпорация Майкрософт", true)]
    [InlineData("DirectX 9.0c Extra Files", null, true)]
    [InlineData("Microsoft Visual C++ 2015 Redistributable", "Some Repacker", false)]
    [InlineData("Google Chrome", "Google LLC", false)]
    [InlineData("7-Zip 23.01 (x64)", "Igor Pavlov", false)]
    public void IsRuntime_DetectsMicrosoftRuntimes(string name, string? publisher, bool expected)
        => Assert.Equal(expected, ProgramRules.IsRuntime(name, publisher));

    [Fact]
    public void IsDriverPackage_RecognizesWindowsDriverPackages()
    {
        Assert.True(ProgramRules.IsDriverPackage("Пакет драйверов Windows - FTDI CDM Driver Package (07/12/2010 2.08.02)"));
        Assert.True(ProgramRules.IsDriverPackage("Windows Driver Package - Intel (e1dexpress) Net"));
        Assert.False(ProgramRules.IsDriverPackage("NVIDIA Graphics Driver 551.23"));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"",0", @"C:\Program Files\App\app.exe", 0)]
    [InlineData(@"C:\Program Files\App\app.exe,-101", @"C:\Program Files\App\app.exe", -101)]
    [InlineData(@"C:\App\icon.ico", @"C:\App\icon.ico", 0)]
    [InlineData(@"C:\Weird,Folder\app.exe", @"C:\Weird,Folder\app.exe", 0)]
    public void ParseIconPath_SplitsIndex(string value, string path, int index)
    {
        var (p, i) = ProgramRules.ParseIconPath(value);
        Assert.Equal(path, p);
        Assert.Equal(index, i);
    }

    [Fact]
    public void IsTooBroadToMeasure_ProtectsRootsAndSystemFolders()
    {
        Assert.True(ProgramRules.IsTooBroadToMeasure(@"C:\"));
        Assert.True(ProgramRules.IsTooBroadToMeasure(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.True(ProgramRules.IsTooBroadToMeasure(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.True(ProgramRules.IsTooBroadToMeasure(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.True(ProgramRules.IsTooBroadToMeasure(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!));
        Assert.False(ProgramRules.IsTooBroadToMeasure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip")));
        Assert.False(ProgramRules.IsTooBroadToMeasure(@"C:\Games\Heroes3"));
    }

    [Fact]
    public void DescribeInstallLocationRisk_FlagsTempAndDownloadsOnly()
    {
        Assert.Contains("Temp", ProgramRules.DescribeInstallLocationRisk(Path.Combine(Path.GetTempPath(), "weird-app")) ?? "");
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "tool");
        Assert.Contains("Загрузки", ProgramRules.DescribeInstallLocationRisk(downloads) ?? "");
        Assert.Null(ProgramRules.DescribeInstallLocationRisk(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "App")));
        Assert.Null(ProgramRules.DescribeInstallLocationRisk(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord")));
        Assert.Null(ProgramRules.DescribeInstallLocationRisk(null));
    }

    [Fact]
    public void DedupKey_IgnoresCaseAndSpaces()
        => Assert.Equal(ProgramRules.DedupKey(" Some App ", "1.0"), ProgramRules.DedupKey("SOME APP", "1.0 "));

    [Fact]
    public void NormalizeFolder_TrimsQuotesAndSlash()
    {
        Assert.Equal(@"C:\Program Files\App", ProgramRules.NormalizeFolder(@"""C:\Program Files\App\"""));
        Assert.Equal(@"C:\", ProgramRules.NormalizeFolder(@"C:\"));
        Assert.Null(ProgramRules.NormalizeFolder("  "));
    }

    // ---------------------------------------------------------------- review regressions

    [Theory]
    [InlineData("MsiExec.exe /I" + Guid1 + " /qn")]
    [InlineData("MsiExec.exe /i " + Guid1 + " /quiet")]
    [InlineData("MsiExec.exe /fa" + Guid1 + " /qn")]
    [InlineData("MsiExec.exe /package " + Guid1 + " /qn")]
    public void BuildQuietLaunch_Msi_NeverInstallsOrRepairs(string quiet)
    {
        var launch = ProgramRules.BuildQuietLaunch(quiet);
        Assert.NotNull(launch);
        Assert.True(launch!.IsMsi);
        Assert.Equal(ProgramRules.MsiExecPath, launch.FileName);
        Assert.StartsWith("/x " + Guid1, launch.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"(?i)(^|\s)[/-](i|f[a-z]*|package)(\s|\{|$)", launch.Arguments);
    }

    [Fact]
    public void BuildQuietLaunch_Msi_KeepsRestartOptions_RefusesAmbiguousCommands()
    {
        Assert.Equal("/x " + Guid1 + " /qn /forcerestart", ProgramRules.BuildQuietLaunch("MsiExec.exe /X" + Guid1 + " /qn /forcerestart")!.Arguments);
        Assert.Equal("/x " + Guid1 + " /quiet /norestart", ProgramRules.BuildQuietLaunch("msiexec.exe /x \"" + Guid1 + "\" /quiet /norestart")!.Arguments);
        Assert.Null(ProgramRules.BuildQuietLaunch(@"msiexec /i C:\Setup\product.msi /qn"));
        Assert.Null(ProgramRules.BuildQuietLaunch("MsiExec.exe /X" + Guid1 + @" /i C:\other.msi /qn"));
        Assert.Equal("/x " + Guid1, ProgramRules.BuildLaunch("MsiExec.exe /fa" + Guid1, false, "K")!.Arguments);
    }

    [Fact]
    public void IsBroken_ProgramExeFromDisplayIconStillThere_IsNotBroken()
    {
        // EA app: the uninstaller and InstallLocation are gone, but the program itself is installed.
        var root = TestEnvironment.NewTempDir("icon");
        var exe = Path.Combine(root, "EADesktop.exe");
        File.WriteAllText(exe, "x");
        var v = new UninstallEntryValues
        {
            KeyName = "EA", DisplayName = "EA app",
            UninstallString = $"\"{Path.Combine(root, "gone", "EAInstaller.exe")}\" /uninstall",
            DisplayIcon = $"\"{exe}\",0",
        };
        Assert.False(ProgramRules.IsBroken(v));
        Assert.True(ProgramRules.IsBroken(new UninstallEntryValues
        {
            KeyName = "EA", DisplayName = "EA app", UninstallString = v.UninstallString,
            DisplayIcon = Path.Combine(root, "gone", "EADesktop.exe"),
        }));
    }

    [Fact]
    public void IsBroken_ConservativeForBundlesPackageCacheAndRuntimes()
    {
        var root = TestEnvironment.NewTempDir("cache");
        var missing = $"\"{Path.Combine(root, "missing", "setup.exe")}\" /uninstall";
        Assert.False(ProgramRules.IsBroken(new UninstallEntryValues
        {
            KeyName = "Bundle", DisplayName = "Some Bundle", UninstallString = missing,
            BundleCachePath = Path.Combine(root, "missing", "setup.exe"),
        }));
        Assert.False(ProgramRules.IsBroken(new UninstallEntryValues
        {
            KeyName = "Cache", DisplayName = "Cached", UninstallString = $"\"{Path.Combine(root, "Package Cache", "{X}", "setup.exe")}\" /uninstall",
        }));
        Assert.False(ProgramRules.IsBroken(new UninstallEntryValues
        {
            KeyName = "Vc", DisplayName = "Microsoft Visual C++ 2015-2022 Redistributable (x64)", Publisher = "Microsoft Corporation",
            UninstallString = missing,
        }));
    }

    [Fact]
    public void WithBundleFallback_UsesCachedSetupOnlyWhenTheUninstallerIsGone()
    {
        var root = TestEnvironment.NewTempDir("bundle");
        var cache = Path.Combine(root, "Package Cache", "{B}", "bundle.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "x");
        var present = Path.Combine(root, "uninst.exe");
        File.WriteAllText(present, "x");

        var gone = ProgramRules.BuildLaunch($"\"{Path.Combine(root, "nope", "u.exe")}\"", false, "B");
        var fallback = ProgramRules.WithBundleFallback(gone, cache, quiet: false);
        Assert.Equal(cache, fallback!.FileName);
        Assert.Equal("/uninstall", fallback.Arguments);
        Assert.Equal("/uninstall /quiet", ProgramRules.WithBundleFallback(gone, cache, quiet: true)!.Arguments);

        var ok = ProgramRules.BuildLaunch($"\"{present}\" /S", false, "B");
        Assert.Same(ok, ProgramRules.WithBundleFallback(ok, cache, quiet: false));
        var msi = ProgramRules.BuildLaunch("MsiExec.exe /X" + Guid1, false, "B");
        Assert.Same(msi, ProgramRules.WithBundleFallback(msi, cache, quiet: false));
        Assert.Same(gone, ProgramRules.WithBundleFallback(gone, Path.Combine(root, "missing.exe"), quiet: false));
        Assert.Same(gone, ProgramRules.WithBundleFallback(gone, @"\\server\share\bundle.exe", quiet: false));
    }

    [Fact]
    public void FindMainExecutable_PrefersExistingLocalProgramExe()
    {
        var root = TestEnvironment.NewTempDir("main");
        var app = Path.Combine(root, "app.exe");
        var uninst = Path.Combine(root, "unins000.exe");
        File.WriteAllText(app, "x");
        File.WriteAllText(uninst, "x");
        var launch = ProgramRules.BuildLaunch($"\"{uninst}\"", false, "A");

        Assert.Equal(app, ProgramRules.FindMainExecutable($"\"{app}\",0", launch));
        Assert.Equal(uninst, ProgramRules.FindMainExecutable(Path.Combine(root, "icon.ico"), launch));
        Assert.Null(ProgramRules.FindMainExecutable(@"\\server\share\app.exe", null, _ => true));
        var rundll = Path.Combine(Environment.SystemDirectory, "rundll32.exe");
        Assert.Null(ProgramRules.FindMainExecutable(rundll, null));
        Assert.Null(ProgramRules.FindMainExecutable(null, ProgramRules.BuildLaunch("MsiExec.exe /X" + Guid1, false, "A")));
    }

    [Fact]
    public void AnalyzeProgram_PerUserAppDataFolderIsNormal_TempIsNoted()
    {
        var discord = ProgramRules.AnalyzeProgram("Discord", "Discord Inc.",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord"));
        Assert.Equal(0, discord.Score);

        var temp = ProgramRules.AnalyzeProgram("Odd Tool", "Odd Vendor", Path.Combine(Path.GetTempPath(), "oddtool"));
        Assert.Contains(temp.Reasons, r => r.StartsWith("Установлена во временную папку"));
        Assert.DoesNotContain(temp.Reasons, r => r.Contains("Запускается") || r.Contains("Установлена из"));

        var amigo = ProgramRules.AnalyzeProgram("Амиго", "Mail.Ru", null);
        Assert.True(amigo.IsSuspicious);
    }

    [Fact]
    public void IsTooBroadToMeasure_DownloadsAndPublic()
    {
        Assert.True(ProgramRules.IsTooBroadToMeasure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")));
        var publicDir = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrEmpty(publicDir))
            Assert.True(ProgramRules.IsTooBroadToMeasure(publicDir));
        Assert.False(ProgramRules.IsTooBroadToMeasure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PortableTool")));
    }

    [Theory]
    [InlineData("chrome", "Google Chrome Google LLC 120.0", true)]
    [InlineData("google llc", "Google Chrome Google LLC 120.0", true)]
    [InlineData("chrome firefox", "Google Chrome Google LLC 120.0", false)]
    [InlineData("", "anything", true)]
    [InlineData("АМИГО", "Амиго Mail.Ru", true)]
    public void SearchMatchesAllTerms(string query, string text, bool expected)
        => Assert.Equal(expected, ProgramsViewModel.MatchesSearch(text, query));
}
