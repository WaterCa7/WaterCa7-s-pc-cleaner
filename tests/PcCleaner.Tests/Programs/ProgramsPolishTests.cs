using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Programs;
using Xunit;

namespace PcCleaner.Tests.Programs;

/// <summary>Install-folder heuristics through Core, the trusted list, runtimes and the page's trust refresh.</summary>
// Trust tests save an AppSettings instance to the temp test settings file, like the Settings tests.
[Collection(PcCleaner.Tests.Settings.SettingsFileCollection.Name)]
public sealed class ProgramsPolishTests
{
    public ProgramsPolishTests() => TestEnvironment.Init();

    // ---------------------------------------------------------------- install folder (Core's heuristics)

    [Fact]
    public void AnalyzeProgram_UsesCoreForTheInstallFolder()
    {
        var temp = Path.GetTempPath().TrimEnd('\\');

        // A program installed right into Temp (not only into a subfolder of it) is noted too.
        var inTempItself = ProgramRules.AnalyzeProgram("Odd Tool", "Odd Vendor", temp);
        Assert.Contains("Установлена во временную папку (Temp)", inTempItself.Reasons);

        // Quotes and a trailing slash from the registry do not hide the folder.
        var quoted = ProgramRules.AnalyzeProgram("Odd Tool", "Odd Vendor", $"\"{Path.Combine(temp, "odd")}\\\"");
        Assert.Contains("Установлена во временную папку (Temp)", quoted.Reasons);

        // Same verdict as Core for the same folder (the page has no wording of its own any more).
        var folder = Path.Combine(temp, "odd");
        var core = Suspicion.AnalyzeProgram("Odd Tool", null, folder + "\\");
        var page = ProgramRules.AnalyzeProgram("Odd Tool", null, folder);
        Assert.Equal(core.Score, page.Score);
        Assert.Equal(core.Reasons, page.Reasons);
        Assert.Contains("Издатель не указан", page.Reasons);

        // Network folders are not judged; per-user AppData folders and Program Files are normal.
        Assert.Equal(0, ProgramRules.AnalyzeProgram("Share App", "Vendor", @"\\server\share\Temp\app").Score);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.Equal(0, ProgramRules.AnalyzeProgram("Normal App", "Vendor", Path.Combine(programFiles, "Normal App")).Score);
        Assert.Null(ProgramRules.DescribeInstallLocationRisk(Path.Combine(programFiles, "Normal App")));

        // The longer explanation in the details panel follows Core's classification.
        Assert.Equal("Папка программы находится в корзине.",
            ProgramRules.DescribeInstallLocationRisk(@"C:\$Recycle.Bin\S-1-5-21-1\$R1A2B3C\app"));
        Assert.Contains("Temp", ProgramRules.DescribeInstallLocationRisk(temp));
    }

    // ---------------------------------------------------------------- trusted list

    [Fact]
    public void SetTrusted_ChangesTheListOnlyThroughAddRemoveTrusted()
    {
        var settings = new AppSettings();
        var before = settings.TrustedItems;

        Assert.True(ProgramsService.SetTrusted(settings, "Some Toolbar", trusted: true));
        Assert.Equal(new[] { "Some Toolbar" }, settings.TrustedItems);
        Assert.Empty(before); // replaced, never edited in place

        Assert.True(ProgramsService.SetTrusted(settings, "SOME TOOLBAR", trusted: true)); // no duplicates
        Assert.Single(settings.TrustedItems);

        var withOne = settings.TrustedItems;
        Assert.False(ProgramsService.SetTrusted(settings, "some toolbar", trusted: false));
        Assert.Empty(settings.TrustedItems);
        Assert.Single(withOne);
    }

    // ---------------------------------------------------------------- runtimes

    private static InstalledProgram Program(string name, string? publisher, string? quiet = null)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "cmd.exe"); // exists; never started by these tests
        return ProgramsService.CreateProgram(new UninstallEntryValues
        {
            KeyName = name.Replace(' ', '_'),
            DisplayName = name,
            Publisher = publisher,
            DisplayVersion = "14.40.33810.0",
            UninstallString = $"\"{exe}\" /uninstall",
            QuietUninstallString = quiet,
        }, UninstallSource.Defaults[2], DateTime.Now);
    }

    [Fact]
    public void Runtime_IsUninstallableOnlyAsASecondaryActionWithAStrongWarning()
    {
        var exe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var runtime = Program("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "Microsoft Corporation",
            quiet: $"\"{exe}\" /uninstall /quiet");
        Assert.True(runtime.IsRuntime);
        Assert.True(runtime.CanUninstall);
        Assert.False(runtime.ShowPrimaryUninstall);   // no red "Удалить программу"
        Assert.True(runtime.UninstallNotRecommended); // a muted "Всё равно удалить" instead
        Assert.True(runtime.CanQuietUninstall);
        Assert.False(runtime.ShowQuietUninstall);     // never a silent removal of a runtime
        Assert.Contains(runtime.Badges, b => b.Text == "Нужна другим программам — не удалять");

        var warning = ProgramsViewModel.BuildUninstallConfirmation(runtime, quiet: false);
        Assert.StartsWith("Внимание!", warning);
        Assert.Contains("перестать запускаться", warning);
        Assert.Contains("Всё равно удалить", warning);
        Assert.True(warning.IndexOf("Внимание!", StringComparison.Ordinal) < warning.IndexOf("Всё равно удалить", StringComparison.Ordinal));

        var app = Program("Some Editor", "Some Vendor", quiet: $"\"{exe}\" /S");
        Assert.True(app.ShowPrimaryUninstall);
        Assert.False(app.UninstallNotRecommended);
        Assert.True(app.ShowQuietUninstall);
        var normal = ProgramsViewModel.BuildUninstallConfirmation(app, quiet: false);
        Assert.StartsWith("Удалить программу «Some Editor»?", normal);
        Assert.DoesNotContain("Внимание!", normal);
    }

    [Fact]
    public void Page_RuntimeMenuText_DetailsPanelToggle()
    {
        var vm = new ProgramsViewModel(new ProgramsService(Array.Empty<UninstallSource>(),
            _ => Task.FromResult<IReadOnlyList<StoreAppRecord>>(Array.Empty<StoreAppRecord>())));

        Assert.True(vm.IsDetailsOpen);
        vm.ToggleDetailsCommand.Execute(null);
        Assert.False(vm.IsDetailsOpen);
        vm.ToggleDetailsCommand.Execute(null);
        Assert.True(vm.IsDetailsOpen);

        vm.SelectedProgram = Program("Microsoft .NET Runtime - 8.0.10 (x64)", "Microsoft Corporation");
        Assert.Equal("Удалить компонент… (не рекомендуется)", vm.UninstallMenuText);
        Assert.False(vm.QuietUninstallCommand.CanExecute(null));
        vm.SelectedProgram = Program("Some Editor", "Some Vendor");
        Assert.Equal("Удалить программу…", vm.UninstallMenuText);
    }

    // ---------------------------------------------------------------- trust changed on another page

    [Fact]
    public async Task TrustChangedElsewhere_IsAppliedWhenThePageOpensAgain()
    {
        var name = "Amigo " + Guid.NewGuid().ToString("N")[..6]; // a known unwanted program
        var program = Program(name, "Mail.Ru");
        var other = Program("Some Editor", "Some Vendor");
        var settings = new AppSettings();
        var vm = new ProgramsViewModel(new ProgramsService(Array.Empty<UninstallSource>(),
            _ => Task.FromResult<IReadOnlyList<StoreAppRecord>>(Array.Empty<StoreAppRecord>())))
        {
            Settings = () => settings,
        };
        vm.ShowProgramsForTests(new[] { program, other });
        Assert.True(program.IsPup);
        Assert.Equal(1, vm.PupCount);

        settings.AddTrusted(name); // e.g. "Доверять" on the Processes page
        await vm.OnNavigatedToAsync();

        Assert.True(program.IsTrusted);
        Assert.False(program.IsPup);
        Assert.Equal(0, vm.PupCount);
        Assert.Contains(program.Badges, b => b.Text == "Доверенная");
        Assert.False(other.IsTrusted);

        settings.RemoveTrusted(name); // e.g. removed in «Настройки» → «Доверенные»
        await vm.OnNavigatedToAsync();

        Assert.False(program.IsTrusted);
        Assert.True(program.IsPup);
        Assert.Equal(1, vm.PupCount);

        // "Доверять" on this page goes through the same list.
        vm.SelectedProgram = program;
        vm.ToggleTrustCommand.Execute(null);
        Assert.True(settings.IsTrusted(name));
        Assert.True(program.IsTrusted);
        Assert.Equal("Убрать из доверенных", vm.TrustButtonText);
        vm.ToggleTrustCommand.Execute(null);
        Assert.False(settings.IsTrusted(name));
        Assert.Equal("Доверять", vm.TrustButtonText);
    }
}
