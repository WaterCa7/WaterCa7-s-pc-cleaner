using System.IO;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Programs;
using Xunit;

namespace PcCleaner.Tests.Programs;

/// <summary>
/// The checkbox columns of «Программы» and «Удалить отмеченные» on both tabs: what the user sees as checked is exactly what gets
/// removed. Programs live in an HKCU sandbox; the uninstaller, the Store app removal and the restore point are faked through the
/// service's seams, so nothing real is ever started, removed or created. A faked uninstaller "removes" a program by deleting its
/// sandbox key, so the page's rescan sees the real result.
/// </summary>
[Collection("ProgramsRegistrySandbox")]
public sealed class ProgramsCheckedTests : IDisposable
{
    private const string SandboxRoot = @"Software\WaterCa7sPcCleanerTests\Programs";
    private const string RuntimeName = "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135";
    private const string RuntimeTooltip = "Компоненты .NET, Visual C++ и т. п. нужны другим программам — удаляйте их по одной";
    private const string QuietSentence =
        "Программы, которые умеют удаляться тихо, удалятся без окон; для остальных по очереди откроются их окна удаления.";

    private const string BingNews = "Microsoft.BingNews_1.0.0.0_x64__8wekyb3d8bbwe";
    private const string CandyCrush = "king.com.CandyCrushSaga_1.0.0.0_x86__kgqvnymyfvs32";
    private const string BingWeather = "Microsoft.BingWeather_1.0.0.0_x64__8wekyb3d8bbwe";
    private const string WindowsStore = "Microsoft.WindowsStore_1.0.0.0_x64__8wekyb3d8bbwe";
    private const string NotesApp = "Contoso.NotesApp_1.0.0.0_x64__abcdefghjkmnp";

    private readonly string _id = Guid.NewGuid().ToString("N")[..10];
    private readonly string _work;
    private readonly string _uninstallerPath;

    /// <summary>Everything the fakes were asked to do, in order ("restore:…", "uninstall:name:quiet|normal", "remove:full name").</summary>
    private readonly List<string> _calls = new();

    /// <summary>The page's progress text each time the faked uninstaller started.</summary>
    private readonly List<string> _progress = new();

    private readonly List<(string Title, string Message)> _questions = new();
    private readonly List<(string Title, string Message)> _infos = new();
    private readonly List<(string Title, string Message)> _errors = new();

    /// <summary>The Store apps Windows "has" (full names); the faked removal takes them out.</summary>
    private readonly List<StoreAppRecord> _store = new();

    private ProgramsViewModel? _vm;

    public ProgramsCheckedTests()
    {
        TestEnvironment.Init();
        _work = TestEnvironment.NewTempDir("progschk");
        _uninstallerPath = Path.Combine(_work, "App", "uninst.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_uninstallerPath)!);
        File.WriteAllText(_uninstallerPath, "fake - never executed");
    }

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

    // ---------------------------------------------------------------- sandbox

    private string MainPath => $@"{SandboxRoot}\{_id}\Main\Uninstall";

    private UninstallSource MainSource => new(RegistryHive.CurrentUser, RegistryView.Registry64, MainPath, ProgramScope.User);

    private void AddEntry(string keyName, Dictionary<string, string> values)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{MainPath}\{keyName}");
        foreach (var (name, value) in values)
            key.SetValue(name, value, RegistryValueKind.String);
    }

    /// <summary>A program with a working (never started) uninstaller; with <paramref name="quiet"/> its installer left a quiet command.</summary>
    private void AddProgram(string displayName, bool quiet = false, string publisher = "Vendor")
    {
        var values = new Dictionary<string, string>
        {
            ["DisplayName"] = displayName,
            ["Publisher"] = publisher,
            ["UninstallString"] = $"\"{_uninstallerPath}\" --uninstall",
        };
        if (quiet)
            values["QuietUninstallString"] = $"\"{_uninstallerPath}\" --uninstall --silent";
        AddEntry(KeyOf(displayName), values);
    }

    private void AddRuntime() => AddProgram(RuntimeName, quiet: true, publisher: "Microsoft Corporation");

    /// <summary>A "broken" entry: its uninstaller is gone (the folder exists), there is no install folder.</summary>
    private void AddBroken(string displayName)
        => AddEntry(KeyOf(displayName), new()
        {
            ["DisplayName"] = displayName,
            ["Publisher"] = "Vendor",
            ["UninstallString"] = $"\"{Path.Combine(_work, "uninstall-gone.exe")}\"",
        });

    private static string KeyOf(string displayName) => displayName.Replace(' ', '_');

    /// <summary>The program's sandbox key is deleted: as if its uninstaller had removed it.</summary>
    private void DeleteEntry(string displayName)
    {
        using var parent = Registry.CurrentUser.OpenSubKey(MainPath, writable: true);
        parent?.DeleteSubKeyTree(KeyOf(displayName), throwOnMissingSubKey: false);
    }

    /// <summary>What the faked uninstaller does (default: <see cref="RemoveIt"/>).</summary>
    private Func<InstalledProgram, CancellationToken, Task<UninstallOutcome>>? Uninstall { get; set; }

    private Task<UninstallOutcome> RemoveIt(InstalledProgram p, CancellationToken ct)
    {
        DeleteEntry(p.DisplayName);
        return Task.FromResult(new UninstallOutcome(UninstallStatus.Removed, "ok"));
    }

    private bool Answer { get; set; } = true;

    private ProgramsService CreateService()
    {
        return new ProgramsService(new[] { MainSource }, _ => Task.FromResult<IReadOnlyList<StoreAppRecord>>(_store.ToList()))
        {
            IsElevated = () => false,
            UninstallOverride = (p, quiet, ct) =>
            {
                _calls.Add($"uninstall:{p.DisplayName}:{(quiet ? "quiet" : "normal")}");
                _progress.Add(_vm!.BusyText);
                return (Uninstall ?? RemoveIt)(p, ct);
            },
            RemoveStoreAppOverride = (app, _) =>
            {
                _calls.Add($"remove:{app.PackageFullName}");
                _store.RemoveAll(r => r.PackageFullName == app.PackageFullName);
                return Task.FromResult((true, "Удалено."));
            },
            CreateRestorePointAsync = (description, _) =>
            {
                _calls.Add($"restore:{description}");
                return Task.FromResult(true);
            },
        };
    }

    /// <summary>The page with the sandbox programs and the fake Store apps loaded, as after opening it.</summary>
    private async Task<ProgramsViewModel> LoadAsync(ProgramsService? service = null)
    {
        var vm = new ProgramsViewModel(service ?? CreateService())
        {
            Confirm = (title, message) =>
            {
                _questions.Add((title, message));
                return Answer;
            },
            ShowInfo = (title, message) => _infos.Add((title, message)),
            ShowError = (title, message) => _errors.Add((title, message)),
        };
        _vm = vm;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsLoaded);
        return vm;
    }

    private string Question => Assert.Single(_questions).Message;

    private string Info => Assert.Single(_infos).Message;

    private static InstalledProgram Program(ProgramsViewModel vm, string name) => vm.Programs.Single(p => p.DisplayName == name);

    private static void Check(ProgramsViewModel vm, params string[] names)
    {
        foreach (var name in names)
            Program(vm, name).IsChecked = true;
    }

    private static IEnumerable<StoreAppRecord> Records(params string[] fullNames) => fullNames.Select(Record);

    private static StoreAppRecord Record(string fullName)
    {
        var name = fullName[..fullName.IndexOf('_')];
        return new StoreAppRecord(name, fullName, null, null, "1.0.0.0", null, "Store");
    }

    private static StoreApp App(ProgramsViewModel vm, string fullName) => vm.StoreApps.Single(a => a.PackageFullName == fullName);

    private static void CheckApps(ProgramsViewModel vm, params string[] fullNames)
    {
        foreach (var fullName in fullNames)
            App(vm, fullName).IsChecked = true;
    }

    /// <summary>The lines of one section of a message: the lines after its heading, up to the next blank line.</summary>
    private static List<string> Section(string message, string heading)
    {
        var lines = message.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var start = lines.FindIndex(l => l.StartsWith(heading, StringComparison.Ordinal));
        Assert.True(start >= 0, $"no section «{heading}» in:\n{message}");
        return lines.Skip(start + 1).TakeWhile(l => l.Length > 0).ToList();
    }

    // ================================================================ Programs: checking

    [Fact]
    public void Runtimes_CannotBeChecked_ByAClickByCodeOrByTheHeader() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddRuntime();
        var vm = await LoadAsync();
        var alpha = Program(vm, "Alpha App");
        var runtime = Program(vm, RuntimeName);

        Assert.True(alpha.CanCheck);
        Assert.Null(alpha.CheckTooltip);
        Assert.True(runtime.IsRuntime);
        Assert.False(runtime.CanCheck);
        Assert.Equal(RuntimeTooltip, runtime.CheckTooltip);

        runtime.IsChecked = true;
        Assert.False(vm.ToggleProgramCheckCommand.CanExecute(runtime));
        vm.ToggleProgramCheckCommand.Execute(runtime);
        Assert.False(runtime.IsChecked);
        Assert.Equal(0, vm.CheckedProgramsCount);

        vm.ToggleAllVisibleProgramsCommand.Execute(null);
        Assert.True(alpha.IsChecked);
        Assert.False(runtime.IsChecked);
        Assert.Equal(1, vm.CheckedProgramsCount);
        Assert.True(vm.AllVisibleProgramsChecked); // every row that can be checked is

        vm.ToggleAllVisibleProgramsCommand.Execute(null);
        Assert.Equal(0, vm.CheckedProgramsCount);
        Assert.False(vm.AllVisibleProgramsChecked);
    });

    [Fact]
    public void ProgramsWithoutAnUninstaller_CannotBeChecked_AndTheHeaderUnderBrokenChecksNothing() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddBroken("Gone App");
        AddEntry("NoUninstaller", new() { ["DisplayName"] = "No Uninstaller App", ["Publisher"] = "Vendor" });
        var vm = await LoadAsync();
        var gone = Program(vm, "Gone App");
        var none = Program(vm, "No Uninstaller App");

        Assert.True(gone.IsBroken);
        Assert.False(gone.CanCheck);
        Assert.Equal("Деинсталлятор не найден — уберите запись кнопкой «Убрать запись из списка»", gone.CheckTooltip);
        Assert.False(none.IsBroken);
        Assert.False(none.CanUninstall);
        Assert.False(none.CanCheck);
        Assert.Equal("У программы не указан деинсталлятор. Попробуйте удалить её через «Параметры» → «Приложения».", none.CheckTooltip);
        gone.IsChecked = true;
        Assert.False(vm.ToggleProgramCheckCommand.CanExecute(none));
        Assert.Equal(0, vm.CheckedProgramsCount);

        vm.ShowFilterCommand.Execute("Broken"); // «Битые»: only the broken entry is listed
        Assert.Equal(new[] { gone }, vm.ProgramsView.Cast<InstalledProgram>());
        Assert.False(vm.ToggleAllVisibleProgramsCommand.CanExecute(null));
        vm.ToggleAllVisibleProgramsCommand.Execute(null);
        Assert.Equal(0, vm.CheckedProgramsCount);
        Assert.False(vm.AllVisibleProgramsChecked);
    });

    [Fact]
    public void UninstallChecked_ProgramsThatBrokeAfterTheyWereChecked_AreSkippedWithTheReason_WithoutARestorePoint() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddProgram("Beta App");
        var vm = await LoadAsync();
        Check(vm, "Alpha App", "Beta App");
        Directory.Delete(Path.GetDirectoryName(_uninstallerPath)!, recursive: true); // their uninstaller is gone since the list was read

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.Empty(_calls); // nothing was started, so no restore point either
        Assert.StartsWith("Удалено: 0 из 2.", Info);
        Assert.Equal(new[] { "• Alpha App — деинсталлятор не найден", "• Beta App — деинсталлятор не найден" }, Section(Info, "Пропущены (2):"));
        // The rescan shows them as broken entries, which can't be checked.
        Assert.True(Program(vm, "Alpha App").IsBroken);
        Assert.Equal(0, vm.CheckedProgramsCount);
    });

    [Fact]
    public void UninstallChecked_AProgramThatIsARuntimeByItsTurn_IsSkipped_AndTheOthersGoOn() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddProgram("Beta App");
        var vm = await LoadAsync();
        Check(vm, "Alpha App", "Beta App");
        // Since the list was read, the same registry key describes a Microsoft runtime.
        AddEntry(KeyOf("Alpha App"), new() { ["DisplayName"] = RuntimeName, ["Publisher"] = "Microsoft Corporation" });

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.Equal(2, _calls.Count);
        Assert.StartsWith("restore:", _calls[0]); // made before the first program that is really started
        Assert.Equal("uninstall:Beta App:normal", _calls[1]);
        Assert.StartsWith("Удалено: 1 из 2.", Info);
        Assert.Equal(new[] { "• Alpha App — компонент, который нужен другим программам: такие удаляют по одному" },
            Section(Info, "Пропущены (1):"));
    });

    [Fact]
    public void RowClickAndHeader_FollowTheChecks() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddProgram("Beta App");
        var vm = await LoadAsync();

        Assert.False(vm.AllVisibleProgramsChecked);
        Assert.False(vm.UninstallCheckedCommand.CanExecute(null));
        vm.ToggleProgramCheckCommand.Execute(Program(vm, "Alpha App"));
        Assert.True(Program(vm, "Alpha App").IsChecked);
        Assert.Null(vm.AllVisibleProgramsChecked); // some
        Assert.True(vm.UninstallCheckedCommand.CanExecute(null));

        vm.ToggleAllVisibleProgramsCommand.Execute(null);
        Assert.True(vm.AllVisibleProgramsChecked);
        Assert.Equal(2, vm.CheckedProgramsCount);

        vm.ClearProgramChecksCommand.Execute(null);
        Assert.Equal(0, vm.CheckedProgramsCount);
        Assert.False(Program(vm, "Beta App").IsChecked);
    });

    [Fact]
    public void Checks_SurviveARescan_ByRegistryKey_AndProgramsThatAreGoneLeaveThem() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddProgram("Beta App");
        AddProgram("Gamma App");
        var vm = await LoadAsync();
        var oldAlpha = Program(vm, "Alpha App");
        Check(vm, "Alpha App", "Beta App");

        await vm.RefreshCommand.ExecuteAsync(null); // a rescan builds new objects for the same entries

        Assert.NotSame(oldAlpha, Program(vm, "Alpha App"));
        Assert.True(Program(vm, "Alpha App").IsChecked);
        Assert.True(Program(vm, "Beta App").IsChecked);
        Assert.False(Program(vm, "Gamma App").IsChecked);
        Assert.Equal(2, vm.CheckedProgramsCount);

        DeleteEntry("Beta App");
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.CheckedProgramsCount);
        Assert.True(Program(vm, "Alpha App").IsChecked);
    });

    [Fact]
    public void AProgramThatTurnsOutToBeARuntime_LeavesTheChecks() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        var vm = await LoadAsync();
        Check(vm, "Alpha App");
        Assert.Equal(1, vm.CheckedProgramsCount);

        // The same registry key now describes a Microsoft runtime: its box is disabled, so it must not stay checked.
        AddEntry(KeyOf("Alpha App"), new() { ["DisplayName"] = RuntimeName, ["Publisher"] = "Microsoft Corporation" });
        await vm.RefreshCommand.ExecuteAsync(null);

        var runtime = Program(vm, RuntimeName);
        Assert.False(runtime.CanCheck);
        Assert.False(runtime.IsChecked);
        Assert.Equal(0, vm.CheckedProgramsCount);
    });

    // ================================================================ Programs: «Удалить отмеченные»

    [Fact]
    public void UninstallChecked_ConfirmNo_StartsNothing() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App", quiet: true);
        AddProgram("Beta App");
        var vm = await LoadAsync();
        Check(vm, "Alpha App", "Beta App");
        Answer = false;

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.Single(_questions);
        Assert.Empty(_calls); // no restore point, no uninstaller
        Assert.Empty(_infos);
        Assert.Equal(2, vm.CheckedProgramsCount);
        Assert.Equal(2, vm.Programs.Count);
    });

    [Fact]
    public void UninstallChecked_Confirmed_OneRestorePoint_ThenOneByOne_QuietOnlyWhereTheInstallerAllows() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App", quiet: true);
        AddProgram("Beta App");
        AddProgram("Gamma App", quiet: true);
        var vm = await LoadAsync();
        Check(vm, "Alpha App", "Beta App");

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        // One question, naming each program and how it will be removed.
        var (title, question) = Assert.Single(_questions);
        Assert.Equal("Удалить отмеченные программы?", title);
        Assert.Contains(QuietSentence, question);
        Assert.Equal(new[] { "• Alpha App — тихо", "• Beta App" }, Section(question, "Удалить 2 программы:"));
        Assert.DoesNotContain("Gamma App", question);

        // One restore point first, then each program in the order of the question, quietly only where a quiet command exists.
        Assert.Equal(3, _calls.Count);
        Assert.StartsWith("restore:", _calls[0]);
        Assert.Equal(new[] { "uninstall:Alpha App:quiet", "uninstall:Beta App:normal" }, _calls.Skip(1));
        Assert.Equal(new[]
        {
            "Удаляем (1 из 2): Alpha App\nЖдём завершения тихого удаления. «Отмена» остановит только ожидание, а не само удаление.",
            "Удаляем (2 из 2): Beta App\nЖдём завершения деинсталлятора. Если его окно уже закрыто, нажмите «Отмена».",
        }, _progress);

        // One message; the list is read again: removed programs are gone and leave the checks.
        var (infoTitle, info) = Assert.Single(_infos);
        Assert.Equal("Удаление отмеченных программ", infoTitle);
        Assert.StartsWith("Удалено: 2 из 2.", info);
        Assert.Equal(new[] { "• Alpha App", "• Beta App" }, Section(info, "Удалены (2):"));
        Assert.Equal(new[] { "Gamma App" }, vm.Programs.Select(p => p.DisplayName));
        Assert.Equal(0, vm.CheckedProgramsCount);
        Assert.False(vm.IsBusy);
    });

    [Fact]
    public void UninstallChecked_OneProgram_AsksAboutThatOne() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        var vm = await LoadAsync();
        Check(vm, "Alpha App");

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.Equal("Удалить отмеченную программу?", Assert.Single(_questions).Title);
        Assert.Equal(new[] { "• Alpha App" }, Section(Question, "Удалить 1 программу:"));
        Assert.Equal(2, _calls.Count);
        Assert.Equal("uninstall:Alpha App:normal", _calls[1]);
    });

    [Fact]
    public void UninstallChecked_CancelledWhileTheFirstRuns_StopsBeforeTheNext_AndNamesTheProgramsNotStarted() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddProgram("Beta App");
        AddProgram("Gamma App");
        var vm = await LoadAsync();
        Check(vm, "Alpha App", "Beta App", "Gamma App");
        Uninstall = (p, _) =>
        {
            vm.CancelCommand.Execute(null); // «Отмена» pressed while the first uninstaller runs; it still finishes
            DeleteEntry(p.DisplayName);
            return Task.FromResult(new UninstallOutcome(UninstallStatus.Removed, "ok"));
        };

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "uninstall:Alpha App:normal" }, _calls.Where(c => c.StartsWith("uninstall:")));
        Assert.StartsWith("Удалено: 1 из 3.", Info);
        Assert.Equal(new[] { "• Alpha App" }, Section(Info, "Удалены (1):"));
        Assert.Equal(new[] { "• Beta App", "• Gamma App" }, Section(Info, "Не начаты из-за отмены (2):"));
        Assert.DoesNotContain("могло продолжиться", Info);
        // The list was read again; the programs that were not started stay checked.
        Assert.Equal(new[] { "Beta App", "Gamma App" }, vm.Programs.Select(p => p.DisplayName));
        Assert.Equal(2, vm.CheckedProgramsCount);
        Assert.True(Program(vm, "Beta App").IsChecked);
    });

    [Fact]
    public void UninstallChecked_CancelledWhileWaitingForTheUninstaller_SaysItMayContinue() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddProgram("Beta App");
        var vm = await LoadAsync();
        Check(vm, "Alpha App", "Beta App");
        Uninstall = (_, ct) =>
        {
            vm.CancelCommand.Execute(null); // only the waiting stops: the uninstaller window may still be open
            return Task.FromCanceled<UninstallOutcome>(ct);
        };

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.Single(_calls, c => c.StartsWith("uninstall:"));
        Assert.StartsWith("Удалено: 0 из 2.", Info);
        Assert.Equal(new[] { "• Alpha App" }, Section(Info, "Ожидание остановлено — удаление могло продолжиться:"));
        Assert.Equal(new[] { "• Beta App" }, Section(Info, "Не начаты из-за отмены (1):"));
        // Both are still installed. The interrupted one leaves the checks: its uninstaller may still be running, and the button must
        // not start a second one; the one that was not started stays checked.
        Assert.Equal(2, vm.Programs.Count);
        Assert.False(Program(vm, "Alpha App").IsChecked);
        Assert.True(Program(vm, "Beta App").IsChecked);
        Assert.Equal(1, vm.CheckedProgramsCount);
    });

    [Fact]
    public void UninstallChecked_CancelledBetweenTwoPrograms_TheNextTurnNeverBegins() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddProgram("Beta App");
        var vm = await LoadAsync();
        Check(vm, "Alpha App", "Beta App");
        var texts = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.BusyText))
                texts.Add(vm.BusyText);
        };
        Uninstall = (p, ct) =>
        {
            vm.CancelCommand.Execute(null); // «Отмена» pressed just as the first uninstaller finishes
            return RemoveIt(p, ct);
        };

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.Contains(texts, t => t.StartsWith("Удаляем (1 из 2): Alpha App", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("(2 из 2)", StringComparison.Ordinal)); // the next turn did not even begin
        Assert.Equal(new[] { "uninstall:Alpha App:normal" }, _calls.Where(c => c.StartsWith("uninstall:")));
        Assert.Equal(new[] { "• Beta App" }, Section(Info, "Не начаты из-за отмены (1):"));
        Assert.True(Program(vm, "Beta App").IsChecked);
    });

    [Fact]
    public void ProgressWhileWaiting_NamesTheUninstaller_AndSpeaksOfAWindowOnlyWhenThereIsOne()
    {
        var normal = ProgramsViewModel.UninstallProgressText(2, 5, "Alpha App", quiet: false, "uninst.exe");
        var quiet = ProgramsViewModel.UninstallProgressText(2, 5, "Alpha App", quiet: true, "uninst.exe");

        Assert.Equal("Удаляем (2 из 5): Alpha App\nЖдём завершения деинсталлятора (uninst.exe). Если его окно уже закрыто, нажмите «Отмена».",
            normal);
        Assert.Equal("Удаляем (2 из 5): Alpha App\nЖдём завершения тихого удаления (uninst.exe). «Отмена» остановит только ожидание, " +
                     "а не само удаление.", quiet);
        Assert.DoesNotContain("окн", quiet);
    }

    [Fact]
    public void UninstallChecked_AnErrorHalfWay_ShowsWhatWasDoneAndTheError_InOneMessage() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddProgram("Beta App");
        AddProgram("Gamma App");
        var vm = await LoadAsync();
        Check(vm, "Alpha App", "Beta App", "Gamma App");
        Uninstall = (p, ct) => p.DisplayName == "Beta App" ? throw new InvalidOperationException("тестовая ошибка") : RemoveIt(p, ct);

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "uninstall:Alpha App:normal", "uninstall:Beta App:normal" }, _calls.Where(c => c.StartsWith("uninstall:")));
        Assert.Empty(_infos);
        var (title, message) = Assert.Single(_errors); // the one message, with the error in it
        Assert.Equal("Удаление отмеченных программ", title);
        Assert.StartsWith("Удалено: 1 из 3.", message);
        Assert.Equal(new[] { "• Alpha App" }, Section(message, "Удалены (1):"));
        Assert.Contains("Остановлено из-за ошибки на «Beta App»: тестовая ошибка.", message);
        Assert.Equal(new[] { "• Gamma App" }, Section(message, "Не начаты из-за ошибки (1):"));
        Assert.Contains(Log.Entries.ToList(), e => e.Level == LogLevel.Error && e.Message.Contains("Удаление отмеченных программ"));
        // The list was read again; the program whose uninstaller may have started leaves the checks, the rest stays checked.
        Assert.Equal(new[] { "Beta App", "Gamma App" }, vm.Programs.Select(p => p.DisplayName));
        Assert.False(Program(vm, "Beta App").IsChecked);
        Assert.True(Program(vm, "Gamma App").IsChecked);
        Assert.False(vm.IsBusy);
    });

    [Fact]
    public void UninstallChecked_CancelledWhileTheRestorePointIsMade_StartsNoProgram() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddProgram("Beta App");
        var service = CreateService();
        service.CreateRestorePointAsync = (description, ct) =>
        {
            _calls.Add($"restore:{description}");
            _vm!.CancelCommand.Execute(null);
            return Task.FromCanceled<bool>(ct);
        };
        var vm = await LoadAsync(service);
        Check(vm, "Alpha App", "Beta App");

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.DoesNotContain(_calls, c => c.StartsWith("uninstall:"));
        Assert.Equal(new[] { "• Alpha App", "• Beta App" }, Section(Info, "Не начаты из-за отмены (2):"));
        Assert.Equal(2, vm.CheckedProgramsCount);
    });

    [Fact]
    public void UninstallChecked_AProgramGoneBeforeItsTurn_CountsAsRemoved_AndIsNotLaunched() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddProgram("Beta App");
        var vm = await LoadAsync();
        Check(vm, "Alpha App", "Beta App");
        Uninstall = (p, _) =>
        {
            DeleteEntry(p.DisplayName);
            DeleteEntry("Beta App"); // e.g. Alpha's uninstaller took Beta with it
            return Task.FromResult(new UninstallOutcome(UninstallStatus.Removed, "ok"));
        };

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "uninstall:Alpha App:normal" }, _calls.Where(c => c.StartsWith("uninstall:")));
        Assert.StartsWith("Удалено: 2 из 2.", Info);
        Assert.Equal(new[] { "• Alpha App", "• Beta App" }, Section(Info, "Удалены (2):"));
        Assert.Empty(vm.Programs);
        Assert.Equal(0, vm.CheckedProgramsCount);
    });

    [Fact]
    public void UninstallChecked_UsesTheProgramAsItIsNow_NotAsItWasScanned() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        var vm = await LoadAsync();
        Check(vm, "Alpha App");
        Assert.False(ProgramsService.ShouldUninstallQuietly(Program(vm, "Alpha App")));
        // Since the list was read, an update of the program added a quiet command.
        AddProgram("Alpha App", quiet: true);

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.Equal("uninstall:Alpha App:quiet", _calls.Last());
    });

    [Fact]
    public void UninstallChecked_Summary_GroupsTheOutcomes_AndWhatIsLeftStaysChecked() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddProgram("Beta App");
        AddProgram("Gamma App");
        AddProgram("Delta App");
        var vm = await LoadAsync();
        Check(vm, "Alpha App", "Beta App", "Gamma App", "Delta App");
        Uninstall = (p, _) =>
        {
            switch (p.DisplayName)
            {
                case "Alpha App":
                    DeleteEntry(p.DisplayName);
                    return Task.FromResult(new UninstallOutcome(UninstallStatus.Removed, "ok"));
                case "Beta App":
                    return Task.FromResult(new UninstallOutcome(UninstallStatus.StillInstalled, "осталась"));
                case "Gamma App":
                    return Task.FromResult(new UninstallOutcome(UninstallStatus.UacDeclined, "отклонено"));
                default:
                    return Task.FromResult(new UninstallOutcome(UninstallStatus.NotStarted,
                        "Файл деинсталлятора не найден:\nC:\\Gone\\uninst.exe\n\nПопробуйте удалить программу через «Параметры»."));
            }
        };

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.Equal(4, _calls.Count(c => c.StartsWith("uninstall:")));
        var info = Info;
        Assert.StartsWith("Удалено: 1 из 4.", info);
        Assert.Equal(new[] { "• Alpha App" }, Section(info, "Удалены (1):"));
        Assert.Equal(new[] { "• Beta App" }, Section(info, "Остались установленными (1)"));
        Assert.Equal(new[] { "• Gamma App" }, Section(info, "Вы отклонили запрос контроля учётных записей (1):"));
        Assert.Equal(new[] { "• Delta App — Файл деинсталлятора не найден: C:\\Gone\\uninst.exe" },
            Section(info, "Не удалось запустить удаление (1):"));
        Assert.DoesNotContain("Не начаты", info);
        Assert.Equal(3, vm.CheckedProgramsCount);
        Assert.DoesNotContain(vm.Programs, p => p.DisplayName == "Alpha App");
        Assert.True(Program(vm, "Delta App").IsChecked);
    });

    [Fact]
    public void UninstallChecked_StillInstalledButGoneAfterTheRescan_IsReportedAsRemoved() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        var vm = await LoadAsync();
        Check(vm, "Alpha App");
        Uninstall = (p, _) =>
        {
            DeleteEntry(p.DisplayName); // the entry went away a moment after the uninstaller reported back
            return Task.FromResult(new UninstallOutcome(UninstallStatus.StillInstalled, "осталась"));
        };

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.StartsWith("Удалено: 1 из 1.", Info);
        Assert.DoesNotContain("Остались", Info);
    });

    [Fact]
    public void UninstallChecked_CheckedProgramsTheListDoesNotShow_AreNamedFirst_NeverFolded_AndRemovedFirst() => UiThread.Run(async () =>
    {
        var hidden = Enumerable.Range(1, 16).Select(i => $"Hidden Tool {i:00}").ToList();
        foreach (var name in hidden)
            AddProgram(name);
        AddProgram("Shown App");
        var vm = await LoadAsync();
        vm.ToggleAllVisibleProgramsCommand.Execute(null);
        Assert.Equal(17, vm.CheckedProgramsCount);
        vm.SearchText = "Shown"; // the search hides the 16 checked tools
        Assert.True(vm.AllVisibleProgramsChecked);

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        var question = Question;
        Assert.StartsWith("Удалить 17 программ.", question);
        Assert.Equal(hidden.Select(n => "• " + n), Section(question, "Не видны в списке сейчас:"));
        Assert.Equal(new[] { "• Shown App" }, Section(question, "В списке:"));
        Assert.True(question.IndexOf("Не видны в списке сейчас:", StringComparison.Ordinal) <
                    question.IndexOf("В списке:", StringComparison.Ordinal));
        Assert.DoesNotContain("…и ещё", question);
        // They are removed too, in the order of the question.
        Assert.Equal(hidden.Select(n => $"uninstall:{n}:normal").Append("uninstall:Shown App:normal"),
            _calls.Where(c => c.StartsWith("uninstall:")));
    });

    [Fact]
    public void UninstallChecked_ProgramsHiddenByAFilterChip_AreNamedAsHiddenToo() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddEntry("Amigo", new()
        {
            ["DisplayName"] = "Амиго",
            ["Publisher"] = "Mail.Ru",
            ["UninstallString"] = $"\"{_uninstallerPath}\" --uninstall",
        });
        var vm = await LoadAsync();
        Check(vm, "Alpha App", "Амиго");
        vm.ShowFilterCommand.Execute("Unwanted"); // «Нежелательные»: only Амиго is listed
        Answer = false;

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "• Alpha App" }, Section(Question, "Не видны в списке сейчас:"));
        Assert.Equal(new[] { "• Амиго" }, Section(Question, "В списке:"));
    });

    [Fact]
    public void UninstallChecked_NothingHidden_MeansNoHiddenHeading() => UiThread.Run(async () =>
    {
        AddProgram("Alpha App");
        AddProgram("Beta App");
        var vm = await LoadAsync();
        Check(vm, "Alpha App");
        vm.SearchText = "Alpha";
        Answer = false;

        await vm.UninstallCheckedCommand.ExecuteAsync(null);

        Assert.DoesNotContain("Не видны в списке сейчас:", Question);
        Assert.DoesNotContain("В списке:", Question);
    });

    // ================================================================ Store apps

    [Fact]
    public void StoreApps_ProtectedAppsCannotBeChecked() => UiThread.Run(async () =>
    {
        _store.AddRange(Records(BingNews, WindowsStore, NotesApp));
        var vm = await LoadAsync();
        var store = App(vm, WindowsStore);
        var news = App(vm, BingNews);

        Assert.False(store.CanRemove);
        Assert.False(store.CanCheck);
        Assert.Equal(store.ProtectReason, store.CheckTooltip);
        Assert.False(string.IsNullOrWhiteSpace(store.CheckTooltip));
        Assert.True(news.CanCheck);
        Assert.Null(news.CheckTooltip);

        store.IsChecked = true;
        vm.ToggleStoreAppCheckCommand.Execute(store);
        Assert.False(vm.ToggleStoreAppCheckCommand.CanExecute(store));
        Assert.False(store.IsChecked);
        Assert.Equal(0, vm.CheckedStoreAppsCount);

        vm.ToggleAllVisibleStoreAppsCommand.Execute(null);
        Assert.Equal(2, vm.CheckedStoreAppsCount);
        Assert.True(news.IsChecked && App(vm, NotesApp).IsChecked);
        Assert.False(store.IsChecked);
        Assert.True(vm.AllVisibleStoreAppsChecked);
    });

    [Fact]
    public void StoreApps_ChecksSurviveARescan_AndAppsThatBecameProtectedLeaveThem() => UiThread.Run(async () =>
    {
        _store.AddRange(Records(BingNews, CandyCrush, NotesApp));
        var vm = await LoadAsync();
        var oldNews = App(vm, BingNews);
        CheckApps(vm, BingNews, NotesApp);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.NotSame(oldNews, App(vm, BingNews));
        Assert.True(App(vm, BingNews).IsChecked);
        Assert.True(App(vm, NotesApp).IsChecked);
        Assert.Equal(2, vm.CheckedStoreAppsCount);

        // The same package is now classified as protected (its name is a protected one): it can't stay checked.
        _store[_store.FindIndex(r => r.PackageFullName == NotesApp)] =
            new StoreAppRecord("Microsoft.WindowsStore", NotesApp, null, null, "1.0.0.0", null, "Store");
        _store.RemoveAll(r => r.PackageFullName == BingNews);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(App(vm, NotesApp).CanCheck);
        Assert.Equal(0, vm.CheckedStoreAppsCount);
    });

    [Fact]
    public void StoreApps_RemoveChecked_ConfirmNo_RemovesNothing() => UiThread.Run(async () =>
    {
        _store.AddRange(Records(BingNews, CandyCrush));
        var vm = await LoadAsync();
        CheckApps(vm, BingNews, CandyCrush);
        Answer = false;

        await vm.RemoveCheckedStoreAppsCommand.ExecuteAsync(null);

        Assert.Single(_questions);
        Assert.Empty(_calls);
        Assert.Empty(_infos);
        Assert.Equal(2, vm.CheckedStoreAppsCount);
    });

    [Fact]
    public void StoreApps_RemoveChecked_Confirmed_OneRestorePoint_RemovesInTheOrderOfTheQuestion_AndReportsOnce() => UiThread.Run(async () =>
    {
        _store.AddRange(Records(BingNews, CandyCrush, BingWeather, WindowsStore));
        var vm = await LoadAsync();
        CheckApps(vm, BingNews, CandyCrush);
        var listed = vm.StoreAppsView.Cast<StoreApp>().Where(a => a.IsChecked).ToList();

        await vm.RemoveCheckedStoreAppsCommand.ExecuteAsync(null);

        var (title, question) = Assert.Single(_questions);
        Assert.Equal("Удалить отмеченные приложения?", title);
        Assert.Equal(listed.Select(a => "• " + a.DisplayName), Section(question, "Удалить 2 приложения:"));
        Assert.Equal(3, _calls.Count);
        Assert.StartsWith("restore:", _calls[0]);
        Assert.Equal(listed.Select(a => $"remove:{a.PackageFullName}"), _calls.Skip(1));
        var (infoTitle, info) = Assert.Single(_infos);
        Assert.Equal("Удаление отмеченных приложений", infoTitle);
        Assert.StartsWith("Удалено 2 приложения из 2.", info);
        // The list was read again: the removed apps are gone and leave the checks.
        Assert.Equal(new[] { BingWeather, WindowsStore }.OrderBy(n => n), vm.StoreApps.Select(a => a.PackageFullName).OrderBy(n => n));
        Assert.Equal(0, vm.CheckedStoreAppsCount);
    });

    [Fact]
    public void StoreApps_RemoveChecked_FailuresAreNamed_AndStayChecked() => UiThread.Run(async () =>
    {
        _store.AddRange(Records(BingNews, CandyCrush));
        var service = CreateService();
        service.RemoveStoreAppOverride = (app, _) =>
        {
            _calls.Add($"remove:{app.PackageFullName}");
            if (app.PackageFullName == BingNews)
                return Task.FromResult((false, "Windows отказала."));
            _store.RemoveAll(r => r.PackageFullName == app.PackageFullName);
            return Task.FromResult((true, "Удалено."));
        };
        var vm = await LoadAsync(service);
        CheckApps(vm, BingNews, CandyCrush);

        await vm.RemoveCheckedStoreAppsCommand.ExecuteAsync(null);

        Assert.StartsWith("Удалено 1 приложение из 2.", Info);
        Assert.Equal(new[] { $"• {App(vm, BingNews).DisplayName} — Windows отказала." }, Section(Info, "Не удалось удалить (1):"));
        Assert.Equal(1, vm.CheckedStoreAppsCount);
        Assert.True(App(vm, BingNews).IsChecked);
    });

    [Fact]
    public void StoreApps_RemoveChecked_Cancelled_StopsBeforeTheNext_AndNamesTheRest() => UiThread.Run(async () =>
    {
        _store.AddRange(Records(BingNews, CandyCrush, BingWeather));
        var service = CreateService();
        service.RemoveStoreAppOverride = (app, _) =>
        {
            _calls.Add($"remove:{app.PackageFullName}");
            _vm!.CancelCommand.Execute(null); // «Отмена» while the first app is being removed
            _store.RemoveAll(r => r.PackageFullName == app.PackageFullName);
            return Task.FromResult((true, "Удалено."));
        };
        var vm = await LoadAsync(service);
        CheckApps(vm, BingNews, CandyCrush, BingWeather);
        var listed = vm.StoreAppsView.Cast<StoreApp>().Where(a => a.IsChecked).ToList();

        await vm.RemoveCheckedStoreAppsCommand.ExecuteAsync(null);

        Assert.Equal(new[] { $"remove:{listed[0].PackageFullName}" }, _calls.Where(c => c.StartsWith("remove:")));
        Assert.StartsWith("Удалено 1 приложение из 3.", Info);
        Assert.Equal(listed.Skip(1).Select(a => "• " + a.DisplayName), Section(Info, "Не удалены из-за отмены (2):"));
        Assert.Equal(2, vm.CheckedStoreAppsCount);
    });

    [Fact]
    public void StoreApps_RemoveChecked_AnErrorHalfWay_ShowsWhatWasDoneAndTheError() => UiThread.Run(async () =>
    {
        _store.AddRange(Records(BingNews, CandyCrush, BingWeather));
        var service = CreateService();
        var remove = service.RemoveStoreAppOverride!;
        var vm = await LoadAsync(service);
        CheckApps(vm, BingNews, CandyCrush, BingWeather);
        var listed = vm.StoreAppsView.Cast<StoreApp>().Where(a => a.IsChecked).ToList();
        service.RemoveStoreAppOverride = (app, ct) => app.PackageFullName == listed[1].PackageFullName
            ? throw new InvalidOperationException("тестовая ошибка")
            : remove(app, ct);

        await vm.RemoveCheckedStoreAppsCommand.ExecuteAsync(null);

        Assert.Equal(new[] { $"remove:{listed[0].PackageFullName}" }, _calls.Where(c => c.StartsWith("remove:"))); // the second one threw
        Assert.Empty(_infos);
        var (title, message) = Assert.Single(_errors);
        Assert.Equal("Удаление отмеченных приложений", title);
        Assert.StartsWith("Удалено 1 приложение из 3.", message);
        Assert.Contains($"Остановлено из-за ошибки на «{listed[1].DisplayName}»: тестовая ошибка.", message);
        Assert.Equal(new[] { "• " + listed[2].DisplayName }, Section(message, "Не удалены из-за ошибки (1):"));
        Assert.Equal(2, vm.CheckedStoreAppsCount); // the list was read again: only the removed app left the checks
    });

    [Fact]
    public void StoreApps_CheckedAppsTheListDoesNotShow_AreNamedFirst() => UiThread.Run(async () =>
    {
        _store.AddRange(Records(BingNews, NotesApp));
        var vm = await LoadAsync();
        CheckApps(vm, BingNews, NotesApp);
        vm.ShowRemovableAppsCommand.Execute(null); // «Можно удалить»: the regular app is not listed
        Answer = false;

        await vm.RemoveCheckedStoreAppsCommand.ExecuteAsync(null);

        Assert.StartsWith("Удалить 2 приложения.", Question);
        Assert.Equal(new[] { "• " + App(vm, NotesApp).DisplayName }, Section(Question, "Не видны в списке сейчас:"));
        Assert.Equal(new[] { "• " + App(vm, BingNews).DisplayName }, Section(Question, "В списке:"));
    });
}
