using System.IO;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Programs;
using Xunit;

namespace PcCleaner.Tests.Programs;

/// <summary>
/// Creates fake Uninstall entries under HKCU\Software\WaterCa7sPcCleanerTests\Programs\&lt;id&gt; and deletes them afterwards.
/// Nothing outside that sandbox key and the temp test folder is touched; no uninstaller is ever started.
/// </summary>
[Collection("ProgramsRegistrySandbox")]
public sealed class ProgramsServiceTests : IDisposable
{
    private const string SandboxRoot = @"Software\WaterCa7sPcCleanerTests\Programs";
    private const string MsiGuid = "{11111111-2222-3333-4444-555555555555}";

    private readonly string _id = Guid.NewGuid().ToString("N")[..10];
    private readonly string _work;
    private readonly string _uninstallerPath;

    public ProgramsServiceTests()
    {
        TestEnvironment.Init();
        _work = TestEnvironment.NewTempDir("progs");
        _uninstallerPath = Path.Combine(_work, "Good App", "uninst.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_uninstallerPath)!);
        File.WriteAllText(_uninstallerPath, "fake - never executed");
    }

    private string MainPath => $@"{SandboxRoot}\{_id}\Main\Uninstall";

    private string SecondPath => $@"{SandboxRoot}\{_id}\Second\Uninstall";

    private UninstallSource MainSource => new(RegistryHive.CurrentUser, RegistryView.Registry64, MainPath, ProgramScope.User);

    private UninstallSource SecondSource => new(RegistryHive.CurrentUser, RegistryView.Registry64, SecondPath, ProgramScope.Machine32);

    public void Dispose()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SandboxRoot, writable: true);
            root?.DeleteSubKeyTree(_id, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // best effort; the shared parent keys are removed at exit by TestEnvironment
        }
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void AddEntry(string uninstallPath, string keyName, Dictionary<string, object> values)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{uninstallPath}\{keyName}");
        foreach (var (name, value) in values)
        {
            if (value is int i)
                key.SetValue(name, i, RegistryValueKind.DWord);
            else
                key.SetValue(name, value.ToString()!, RegistryValueKind.String);
        }
    }

    private void CreateStandardEntries()
    {
        AddEntry(MainPath, "GoodApp", new()
        {
            ["DisplayName"] = "Good App",
            ["Publisher"] = "Good Vendor",
            ["DisplayVersion"] = "1.2.3",
            ["InstallDate"] = DateTime.Now.AddDays(-3).ToString("yyyyMMdd"),
            ["EstimatedSize"] = 2048,
            ["InstallLocation"] = Path.GetDirectoryName(_uninstallerPath)!,
            ["UninstallString"] = $"\"{_uninstallerPath}\"",
            ["QuietUninstallString"] = $"\"{_uninstallerPath}\" /S",
        });
        AddEntry(MainPath, "BrokenApp", new()
        {
            ["DisplayName"] = "Broken App",
            ["Publisher"] = "Gone Vendor",
            ["DisplayVersion"] = "9.9",
            ["InstallDate"] = "20200101",
            ["EstimatedSize"] = 4096,
            ["UninstallString"] = $"\"{Path.Combine(_work, "Gone", "uninstall.exe")}\"",
        });
        AddEntry(MainPath, "SysComp", new() { ["DisplayName"] = "Hidden component", ["SystemComponent"] = 1 });
        AddEntry(MainPath, "Patch", new() { ["DisplayName"] = "Some patch", ["ParentKeyName"] = "GoodApp" });
        AddEntry(MainPath, "KB500100", new() { ["DisplayName"] = "Security Update KB500100", ["ReleaseType"] = "Security Update" });
        AddEntry(MainPath, "NoName", new() { ["UninstallString"] = "x.exe" });
        AddEntry(MainPath, MsiGuid, new()
        {
            ["DisplayName"] = "Msi Product",
            ["Publisher"] = "Msi Vendor",
            ["UninstallString"] = "MsiExec.exe /I" + MsiGuid,
            ["WindowsInstaller"] = 1,
        });
        AddEntry(MainPath, "AmigoBrowser", new()
        {
            ["DisplayName"] = "Амиго",
            ["Publisher"] = "Mail.Ru",
            ["UninstallString"] = $"\"{_uninstallerPath}\" --uninstall",
        });
        AddEntry(MainPath, "VcRedist", new()
        {
            ["DisplayName"] = "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135",
            ["Publisher"] = "Microsoft Corporation",
            ["UninstallString"] = $"\"{_uninstallerPath}\" /uninstall",
        });
        // Same program as GoodApp seen through another view -> must be deduplicated.
        AddEntry(SecondPath, "GoodApp", new()
        {
            ["DisplayName"] = "Good App",
            ["DisplayVersion"] = "1.2.3",
            ["UninstallString"] = $"\"{_uninstallerPath}\"",
        });
    }

    private ProgramsService CreateService(Func<CancellationToken, Task<IReadOnlyList<StoreAppRecord>>>? store = null)
        => new(new[] { MainSource, SecondSource }, store ?? (_ => Task.FromResult<IReadOnlyList<StoreAppRecord>>(Array.Empty<StoreAppRecord>())))
        {
            IsElevated = () => false,
        };

    [Fact]
    public void Scan_FiltersHiddenEntriesAndDeduplicates()
    {
        CreateStandardEntries();
        var programs = CreateService().ScanPrograms(CancellationToken.None).Programs;
        var names = programs.Select(p => p.DisplayName).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "Broken App", "Good App", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135", "Msi Product", "Амиго" }
            .OrderBy(n => n), names);
        Assert.Single(programs, p => p.DisplayName == "Good App");
    }

    [Fact]
    public void Scan_ReadsFieldsAndFlags()
    {
        CreateStandardEntries();
        var programs = CreateService().ScanPrograms(CancellationToken.None).Programs.ToDictionary(p => p.DisplayName);

        var good = programs["Good App"];
        Assert.Equal("Good Vendor", good.Publisher);
        Assert.Equal("1.2.3", good.Version);
        Assert.Equal(2048L * 1024, good.SizeBytes);
        Assert.True(good.IsRecent);
        Assert.False(good.IsBroken);
        Assert.False(good.IsPup);
        Assert.True(good.CanUninstall);
        Assert.True(good.CanQuietUninstall);
        Assert.True(good.InstallLocationExists);
        Assert.Equal($@"HKCU\{MainPath}\GoodApp", good.RegistryKeyPath);
        Assert.Contains(good.Badges, b => b.Text == "Недавно установлена");

        var broken = programs["Broken App"];
        Assert.True(broken.IsBroken);
        Assert.False(broken.CanUninstall);
        Assert.False(broken.IsRecent);
        Assert.Equal(0, broken.SizeBytes); // the program is gone - its old size is not counted
        Assert.Equal("Битая запись", broken.PrimaryBadge?.Text);

        var msi = programs["Msi Product"];
        Assert.True(msi.IsMsi);
        Assert.Equal(MsiGuid, msi.ProductCode);
        Assert.Equal("/x " + MsiGuid, msi.Launch!.Arguments);
        Assert.False(msi.IsBroken);

        var amigo = programs["Амиго"];
        Assert.True(amigo.IsPup);
        Assert.Equal(BadgeKind.Danger, amigo.PrimaryBadge?.Kind);

        var vc = programs["Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135"];
        Assert.True(vc.IsRuntime);
        Assert.Contains(vc.Badges, b => b.Text.Contains("не удалять"));
    }

    [Fact]
    public void Scan_UsesInjectedClockForRecentFlag()
    {
        CreateStandardEntries();
        var service = new ProgramsService(new[] { MainSource }, clock: () => DateTime.Now.AddYears(1));
        var good = service.ScanPrograms(CancellationToken.None).Programs.Single(p => p.DisplayName == "Good App");
        Assert.False(good.IsRecent);
    }

    [Fact]
    public void Scan_MissingRoot_ReturnsEmpty()
    {
        var service = new ProgramsService(new[] { MainSource });
        Assert.Empty(service.ScanPrograms(CancellationToken.None).Programs);
    }

    [Fact]
    public async Task DeleteEntry_BacksUpAndDeletesOnlyThatKey()
    {
        CreateStandardEntries();
        var service = CreateService();
        var broken = service.ScanPrograms(CancellationToken.None).Programs.Single(p => p.DisplayName == "Broken App");

        var result = await service.DeleteEntryAsync(broken, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.BackupFile);
        Assert.True(File.Exists(result.BackupFile));
        Assert.StartsWith(AppPaths.BackupsDir, result.BackupFile!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Broken App", File.ReadAllText(result.BackupFile!));
        Assert.False(service.EntryExists(broken));

        var remaining = service.ScanPrograms(CancellationToken.None).Programs.Select(p => p.DisplayName).ToList();
        Assert.DoesNotContain("Broken App", remaining);
        Assert.Contains("Good App", remaining);
    }

    [Fact]
    public async Task DeleteEntry_RefusesKeysOutsideKnownRoots()
    {
        CreateStandardEntries();
        var service = CreateService();
        var foreign = new UninstallSource(RegistryHive.CurrentUser, RegistryView.Registry64, $@"{SandboxRoot}\{_id}\Main\Uninstall", ProgramScope.Machine64);
        var program = ProgramsService.CreateProgram(
            new UninstallEntryValues { KeyName = "BrokenApp", DisplayName = "Broken App", UninstallString = @"""C:\nope\u.exe""" },
            foreign, DateTime.Now);

        var result = await service.DeleteEntryAsync(program, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(service.EntryExists(service.ScanPrograms(CancellationToken.None).Programs.Single(p => p.DisplayName == "Broken App")));
    }

    [Fact]
    public async Task DeleteEntry_KeyNameWithPathSeparators_IsRefused()
    {
        CreateStandardEntries();
        var service = CreateService();
        var program = ProgramsService.CreateProgram(
            new UninstallEntryValues { KeyName = @"..\..\Main", DisplayName = "Evil" }, MainSource, DateTime.Now);
        var result = await service.DeleteEntryAsync(program, CancellationToken.None);
        Assert.False(result.Success);
        using var stillThere = Registry.CurrentUser.OpenSubKey(MainPath);
        Assert.NotNull(stillThere);
    }

    [Fact]
    public async Task DeleteEntry_MachineWideWithoutAdmin_ChangesNothing()
    {
        // HKLM source that does not exist: the service must stop before any registry access for writing.
        var hklm = new UninstallSource(RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\WaterCa7sPcCleanerTests\Programs\NotThere\Uninstall", ProgramScope.Machine64);
        var service = new ProgramsService(new[] { hklm }) { IsElevated = () => false };
        var program = ProgramsService.CreateProgram(
            new UninstallEntryValues { KeyName = "X", DisplayName = "X", UninstallString = @"""C:\nope\u.exe""" }, hklm, DateTime.Now);

        var result = await service.DeleteEntryAsync(program, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("администратора", result.Message);
    }

    [Fact]
    public async Task RunUninstaller_MissingFile_StartsNothing()
    {
        CreateStandardEntries();
        var service = CreateService();
        var program = ProgramsService.CreateProgram(new UninstallEntryValues
        {
            KeyName = "GoneTwo",
            DisplayName = "Gone Two",
            UninstallString = $"\"{Path.Combine(_work, "nothing-here", "uninstall.exe")}\"",
            InstallLocation = _work, // folder exists, so the entry is not "broken" and Launch is built
        }, MainSource, DateTime.Now);
        Assert.True(program.CanUninstall);

        var outcome = await service.RunUninstallerAsync(program, quiet: false, null, CancellationToken.None);

        Assert.Equal(UninstallStatus.NotStarted, outcome.Status);
    }

    [Fact]
    public async Task Analyze_ReportsPupBrokenAndBloat_AndFixDeletesBrokenEntries()
    {
        CreateStandardEntries();
        var service = CreateService(_ => Task.FromResult<IReadOnlyList<StoreAppRecord>>(new[]
        {
            new StoreAppRecord("Microsoft.BingNews", "Microsoft.BingNews_1.0.0.0_x64__8wekyb3d8bbwe", null, null, "1.0.0.0", null, "Store"),
            new StoreAppRecord("king.com.CandyCrushSaga", "king.com.CandyCrushSaga_1.0.0.0_x86__kgqvnymyfvs32", null, null, "1.0.0.0", null, "Store"),
            new StoreAppRecord("Microsoft.WindowsStore", "Microsoft.WindowsStore_1.0.0.0_x64__8wekyb3d8bbwe", null, null, "1.0.0.0", null, "Store"),
        }));

        var issues = await service.AnalyzeAsync(CancellationToken.None);

        var pup = Assert.Single(issues, i => i.Severity == IssueSeverity.High);
        Assert.Contains("Амиго", pup.Title);
        Assert.NotNull(pup.FixAsync); // «Удалить программу» (its behaviour: ProgramsDashboardFixTests)
        Assert.False(pup.InFixAll);
        Assert.Equal("Programs", pup.TargetPageKey);

        var broken = Assert.Single(issues, i => i.Severity == IssueSeverity.Low);
        Assert.Equal("1 битая запись в списке программ", broken.Title);
        Assert.False(broken.AutoFixSafe);
        Assert.NotNull(broken.FixAsync);

        var bloat = Assert.Single(issues, i => i.Severity == IssueSeverity.Info);
        Assert.Equal("2 предустановленных приложения можно удалить", bloat.Title);
        Assert.All(issues, i => Assert.Equal("Программы", i.Module));

        var message = await broken.FixAsync!(CancellationToken.None);
        Assert.Contains("Удалено 1 запись", message);
        Assert.DoesNotContain(service.ScanPrograms(CancellationToken.None).Programs, p => p.IsBroken);
    }

    // ---------------------------------------------------------------- review regressions

    [Fact]
    public async Task DeleteEntry_ProgramReinstalledAfterScan_IsNotDeleted()
    {
        CreateStandardEntries();
        var service = CreateService();
        var broken = service.ScanPrograms(CancellationToken.None).Programs.Single(p => p.DisplayName == "Broken App");

        // The user reinstalls the program while the list is open: its uninstaller is back.
        var uninstaller = Path.Combine(_work, "Gone", "uninstall.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(uninstaller)!);
        File.WriteAllText(uninstaller, "fake - never executed");

        var result = await service.DeleteEntryAsync(broken, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.BackupFile);
        Assert.True(service.EntryExists(broken));
    }

    [Fact]
    public async Task DeleteEntry_KeyNameWithQuote_IsRefused()
    {
        var program = ProgramsService.CreateProgram(
            new UninstallEntryValues { KeyName = "A\" \"B", DisplayName = "Quote", UninstallString = @"""C:\nope\u.exe""" }, MainSource, DateTime.Now);
        var result = await CreateService().DeleteEntryAsync(program, CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task DeleteEntries_Cancelled_ReportsInsteadOfThrowing()
    {
        CreateStandardEntries();
        var service = CreateService();
        var broken = service.ScanPrograms(CancellationToken.None).Programs.Where(p => p.IsBroken).ToList();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (deleted, failures) = await service.DeleteEntriesAsync(broken, null, cts.Token);

        Assert.Equal(0, deleted);
        Assert.Empty(failures);
        Assert.All(broken, b => Assert.True(service.EntryExists(b)));
    }

    [Fact]
    public void Scan_BuildsQuietLaunchMainExeAndBundleFallbackOnce()
    {
        var cache = Path.Combine(_work, "Package Cache", "{B}", "bundle.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "fake - never executed");
        CreateStandardEntries();
        AddEntry(MainPath, "Bundle", new()
        {
            ["DisplayName"] = "Some Bundle",
            ["Publisher"] = "Bundle Vendor",
            ["UninstallString"] = $"\"{Path.Combine(_work, "gone-bundle", "setup.exe")}\" /uninstall",
            ["QuietUninstallString"] = $"\"{Path.Combine(_work, "gone-bundle", "setup.exe")}\" /uninstall /quiet",
            ["BundleCachePath"] = cache,
        });
        var programs = CreateService().ScanPrograms(CancellationToken.None).Programs.ToDictionary(p => p.DisplayName);

        var bundle = programs["Some Bundle"];
        Assert.False(bundle.IsBroken);
        Assert.Equal(cache, bundle.Launch!.FileName);
        Assert.Equal("/uninstall", bundle.Launch.Arguments);
        Assert.Equal("/uninstall /quiet", bundle.QuietLaunch!.Arguments);

        var good = programs["Good App"];
        Assert.NotNull(good.QuietLaunch);
        Assert.Equal(_uninstallerPath, good.MainExecutable);
        Assert.False(programs["Амиго"].HasQuietUninstall);
        Assert.False(programs["Амиго"].CanQuietUninstall);
    }

    [Fact]
    public async Task ScanStoreApps_ClassifiesAndSorts()
    {
        var service = CreateService(_ => Task.FromResult<IReadOnlyList<StoreAppRecord>>(new[]
        {
            new StoreAppRecord("Microsoft.WindowsStore", "Microsoft.WindowsStore_1_x64__8wekyb3d8bbwe", null, null, "1", null, "Store"),
            new StoreAppRecord("Microsoft.BingWeather", "Microsoft.BingWeather_1_x64__8wekyb3d8bbwe", null, null, "1", null, "Store"),
            new StoreAppRecord("Microsoft.BingWeather", "Microsoft.BingWeather_1_x64__8wekyb3d8bbwe", null, null, "1", null, "Store"),
        }));
        var result = await service.ScanStoreAppsAsync(CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Apps.Count);
        Assert.Contains(result.Apps, a => a.IsBloatware && a.DisplayName == "Погода");
        Assert.Contains(result.Apps, a => a.IsProtected && !a.CanRemove);
    }
}

[CollectionDefinition("ProgramsRegistrySandbox", DisableParallelization = true)]
public sealed class ProgramsRegistrySandboxCollection
{
}
