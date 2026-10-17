using System.ComponentModel;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Settings;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>Core tests that swap global state (Dialogs.ConfirmHandler, AppPaths.DataDir): they run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CoreSequentialCollection
{
    public const string Name = "Core (sequential)";
}

internal sealed class CoreFakePage : PageViewModel
{
    private readonly string _title;

    public CoreFakePage(string title) => _title = title;

    public override string Title => _title;

    public override string Subtitle => "";

    public override string IconGlyph => "";

    public Task StartWork(string text, Task until) => RunBusyAsync(text, _ => until);
}

public class RestorePointScriptTests
{
    private const string Prefix = "WaterCa7s PC Cleaner: ";

    [Theory]
    // U+2019/U+2018/U+201C/U+201D are string delimiters for Windows PowerShell, like the ASCII apostrophe.
    [InlineData("Удаление X\u2019;Set-Content $env:TEMP\\pcc_pwned.txt OWNED;#")]
    [InlineData("Удаление X\u2018;saps calc;#")]
    [InlineData("Удаление X\u201C;saps calc;#\u201D")]
    [InlineData("Удаление X';saps calc;#")]
    [InlineData("Удаление X`$(saps calc)")]
    [InlineData("Удаление X\r\nsaps calc")]
    public void Description_IsCarriedAsData_NeverAsCode(string description)
    {
        foreach (var force in new[] { false, true })
        {
            var script = RestorePoint.BuildScript(description, force);
            Assert.DoesNotContain("saps", script);
            Assert.DoesNotContain("Set-Content", script);
            Assert.DoesNotContain("Удаление", script);
            foreach (var quote in "\u2018\u2019\u201A\u201B\u201C\u201D\u201E")
                Assert.DoesNotContain(quote, script);

            var b64 = Regex.Match(script, @"FromBase64String\('([A-Za-z0-9+/=]+)'\)").Groups[1].Value;
            Assert.Equal(Prefix + RestorePoint.Truncate(description, 60), Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            Assert.Contains("Checkpoint-Computer -Description $desc ", script);
        }
    }

    [Fact]
    public void AutomaticPoint_NeverTurnsSystemProtectionOn()
    {
        var script = RestorePoint.BuildScript("Удаление программы", force: false);
        Assert.DoesNotContain("Enable-ComputerRestore", script);
        Assert.Contains(RestorePoint.ProtectionOffMarker, script);
        Assert.DoesNotContain(RestorePoint.ProtectionEnabledMarker, script);
    }

    [Fact]
    public void ManualPoint_TurnsProtectionOn_AndReportsTheChange()
    {
        var script = RestorePoint.BuildScript("Создана вручную в настройках", force: true);
        Assert.Contains("Enable-ComputerRestore", script);
        Assert.Contains(RestorePoint.ProtectionEnabledMarker, script);
        // The state is read before enabling, so the change is reported only when protection really was off.
        Assert.True(script.IndexOf("$p0 = Prot", StringComparison.Ordinal) < script.IndexOf("Enable-ComputerRestore", StringComparison.Ordinal));
    }

    [Fact]
    public void Truncate_KeepsSurrogatePairsWhole()
    {
        var text = new string('a', 59) + "\U0001F600" + "tail";
        var cut = RestorePoint.Truncate(text, 60);
        Assert.Equal(59, cut.Length);
        Assert.False(char.IsHighSurrogate(cut[^1]));
        Assert.Equal("short", RestorePoint.Truncate("short", 60));
    }

    [Fact]
    public async Task GeneratedScript_ContainsOnlyTheExpectedCommands()
    {
        TestEnvironment.Init();
        // Parse (never run) the generated script with PowerShell's own parser.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Get-ItemProperty", "Enable-ComputerRestore", "Get-ComputerRestorePoint", "Sort-Object", "Select-Object",
            "Checkpoint-Computer", "Prot", "Last",
        };
        foreach (var force in new[] { false, true })
        {
            var generated = RestorePoint.BuildScript("Удаление X\u2019;saps calc;# \u201Cy\u201D; $(saps calc)", force);
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(generated));
            var probe =
                $"$s = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{b64}'))\r\n" +
                "$tokens = $null; $errors = $null\r\n" +
                "$ast = [System.Management.Automation.Language.Parser]::ParseInput($s, [ref]$tokens, [ref]$errors)\r\n" +
                "if ($errors.Count -gt 0) { 'PARSE-ERRORS' }\r\n" +
                "$ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { 'CMD:' + $_.GetCommandName() }\r\n" +
                "'DONE'\r\n";
            var result = await ProcessRunner.PowerShellAsync(probe, TimeSpan.FromSeconds(60));
            Assert.Contains("DONE", result.StdOut);
            Assert.DoesNotContain("PARSE-ERRORS", result.StdOut);
            var commands = result.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("CMD:")).Select(l => l[4..]).ToList();
            Assert.Contains("Checkpoint-Computer", commands);
            Assert.All(commands, c => Assert.Contains(c, allowed));
        }
    }
}

public class ErrorTextTests
{
    [Fact]
    public void EnglishAccessDenied_BecomesRussian()
    {
        var text = ErrorText.For(new UnauthorizedAccessException("Access to the path 'C:\\Windows\\x' is denied."));
        Assert.StartsWith("нет доступа", text);
        Assert.DoesNotContain("Access", text);
    }

    [Fact]
    public void RussianMessage_IsKept()
    {
        var ex = new InvalidOperationException("Системные файлы Windows нельзя помещать в карантин.");
        Assert.Equal("Системные файлы Windows нельзя помещать в карантин", ErrorText.For(ex));
        Assert.Equal("Системные файлы Windows нельзя помещать в карантин.", ErrorText.Sentence(ex));
    }

    [Fact]
    public void CommonIoErrors_HaveRussianText()
    {
        Assert.Equal("файл занят другой программой",
            ErrorText.For(new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020))));
        Assert.Equal("на диске недостаточно места", ErrorText.For(new IOException("There is not enough space on the disk.", unchecked((int)0x80070070))));
        Assert.Equal("файл или папка не найдены", ErrorText.For(new DirectoryNotFoundException("Could not find a part of the path 'C:\\x'.")));
        Assert.Equal("не удалось прочитать или записать файл", ErrorText.For(new IOException("Some I/O error.")));
    }

    [Fact]
    public void WindowsErrors_AreDescribedByCode()
    {
        Assert.StartsWith("нет доступа", ErrorText.For(new Win32Exception(5, "Access is denied")));
        Assert.Equal("действие отменено пользователем", ErrorText.For(new Win32Exception(1223, "The operation was canceled by the user")));
        Assert.Equal("ошибка Windows (код 1234)", ErrorText.For(new Win32Exception(1234, "No service is operating at the destination network endpoint")));
    }

    [Fact]
    public void UnexpectedErrors_AreGeneric_AndWrappersAreUnwrapped()
    {
        var unexpected = new NullReferenceException("Object reference not set to an instance of an object.");
        Assert.StartsWith("непредвиденная ошибка", ErrorText.For(unexpected));
        Assert.StartsWith("нет доступа", ErrorText.For(new AggregateException(new UnauthorizedAccessException("Access denied"))));
        Assert.Equal("Непредвиденная ошибка — подробности записаны в журнал.", ErrorText.Sentence(unexpected));
    }

    [Fact]
    public void LogError_ShowsRussianText_TheFileKeepsTheDetails()
    {
        TestEnvironment.Init();
        var marker = "Проверка текста ошибки " + Guid.NewGuid().ToString("N");
        Log.Error(marker, new UnauthorizedAccessException("Access to the path 'C:\\x' is denied."));

        var entry = Log.Entries.ToList().Last(e => e.Message.StartsWith(marker));
        Assert.StartsWith(marker + ": нет доступа", entry.Message);
        Assert.DoesNotContain("UnauthorizedAccessException", entry.Message);

        var file = Path.Combine(AppPaths.LogsDir, $"{entry.Time:yyyy-MM-dd}.log");
        var line = File.ReadAllLines(file).Last(l => l.Contains(marker));
        Assert.Contains("UnauthorizedAccessException: Access to the path", line);
    }
}

public class SecureTempTests
{
    [Fact]
    public void Create_WritesTheContent_WithAnAdminOnlyDacl()
    {
        TestEnvironment.Init();
        var dir = TestEnvironment.NewTempDir("securetemp");
        var content = Encoding.UTF8.GetBytes("Write-Output 'ok'");
        var path = SecureTemp.Create(dir, "WaterCa7sPcCleaner-ps", ".ps1", content);
        try
        {
            Assert.StartsWith(Path.Combine(dir, "WaterCa7sPcCleaner-ps-"), path);
            Assert.Equal(content, File.ReadAllBytes(path));

            var security = new FileInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            Assert.All(rules, r => Assert.Contains(r.IdentityReference.Value, new[] { "S-1-5-18", "S-1-5-32-544", "S-1-3-4" }));
            Assert.DoesNotContain(rules, r => r.IdentityReference.Value == "S-1-3-4" &&
                                              (r.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.ChangePermissions)) != 0);

            if (!Admin.IsElevated)
                Assert.Throws<UnauthorizedAccessException>(() => File.OpenWrite(path).Dispose());
        }
        finally
        {
            SecureTemp.TryDelete(path);
        }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ElevatedPowerShell_LoadsModulesOnlyFromMachineFolders()
    {
        Assert.Contains("GetEnvironmentVariable('PSModulePath','Machine')", ProcessRunner.BuildPowerShellScript("Get-Date", elevated: true));
        var normal = ProcessRunner.BuildPowerShellScript("Get-Date", elevated: false);
        Assert.DoesNotContain("PSModulePath", normal);
        Assert.EndsWith("Get-Date", normal);
    }
}

public class AdminTextTests
{
    [Fact]
    public void BusyWarning_IsTheSameTextAsTheSettingsHelper()
    {
        var busy = new[] { ("Программы", "Удаление «Test App»…"), ("Диски", "") };
        Assert.Equal(ElevatedRestart.BusyWarning(busy), Admin.BusyWarning(busy));
    }

    [Fact]
    public void ElevationQuestion_OnAStandardAccount_SaysWhoseProfileWillBeUsed()
    {
        var admin = Admin.ElevationQuestion("Включение брандмауэра", standardUser: false);
        Assert.Contains("«Включение брандмауэра»", admin);
        Assert.DoesNotContain("обычная", admin);

        var standard = Admin.ElevationQuestion("Включение брандмауэра", standardUser: true);
        Assert.Contains("обычная, без прав администратора", standard);
        Assert.Contains("к профилю администратора, а не к вашему", standard);
        Assert.EndsWith("Перезапустить программу от имени администратора?", standard);
    }

    [Fact]
    public void OtherAccount_IsDetected_OnlyWhenTheAccountsDiffer()
    {
        // The test process runs as the signed-in user.
        Assert.Null(Admin.SessionUserIfDifferent);
        Assert.Null(Admin.OtherAccountWarning);

        var text = Admin.OtherAccountText(@"PC\Admin", @"PC\Kid");
        Assert.Contains("от имени «Admin»", text);
        Assert.Contains("вошёл «Kid»", text);
    }

    [Fact]
    public void CurrentAccountType_MatchesTheToken()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var expectedAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ||
                            identity.Claims.Any(c => c.Value == "S-1-5-32-544" && c.Type.EndsWith("/denyonlysid", StringComparison.Ordinal));
        Assert.Equal(expectedAdmin, Admin.IsAdministratorAccount(identity));
        Assert.Equal(!Admin.IsElevated && !expectedAdmin, Admin.IsStandardUser);
    }

    [Theory]
    [InlineData(42, "42 с")]
    [InlineData(312, "5 мин 12 с")]
    [InlineData(3 * 3600 + 120, "3 ч 2 мин")]
    public void Duration_UsesTheStandardSecondsAbbreviation(int seconds, string expected)
        => Assert.Equal(expected, Format.Duration(TimeSpan.FromSeconds(seconds)));
}

[Collection(CoreSequentialCollection.Name)]
public class RestartGuardTests
{
    public RestartGuardTests() => TestEnvironment.Init();

    private static T WithAnswers<T>(Func<string, string, bool> handler, Func<T> body)
    {
        var previous = Dialogs.ConfirmHandler;
        Dialogs.ConfirmHandler = handler;
        try
        {
            Dialogs.TakeRecentYes("", TimeSpan.Zero); // forget answers of earlier tests
            return body();
        }
        finally
        {
            Dialogs.ConfirmHandler = previous;
        }
    }

    [Fact]
    public async Task Restart_AsksWhileAnyPageIsBusy()
    {
        var idle = new CoreFakePage("Безопасность");
        var busy = new CoreFakePage("Программы");
        var gate = new TaskCompletionSource();
        var work = busy.StartWork("Удаляем предустановленные приложения…", gate.Task);
        var pages = new PageViewModel[] { idle, busy };

        var questions = new List<string>();
        Assert.False(WithAnswers((_, m) => { questions.Add(m); return false; }, () => Admin.ConfirmRestart(pages, false)));
        Assert.Single(questions);
        Assert.Contains("• «Программы»: Удаляем предустановленные приложения", questions[0]);

        Assert.True(WithAnswers((_, _) => true, () => Admin.ConfirmRestart(pages, false)));

        gate.SetResult();
        await work;
        questions.Clear();
        Assert.True(WithAnswers((_, m) => { questions.Add(m); return false; }, () => Admin.ConfirmRestart(pages, false)));
        Assert.Empty(questions);
    }

    [Fact]
    public async Task Restart_DoesNotRepeatTheBusyQuestionJustAnswered()
    {
        var busy = new CoreFakePage("Очистка");
        var gate = new TaskCompletionSource();
        var work = busy.StartWork("Очистка…", gate.Task);
        try
        {
            var asked = 0;
            var ok = WithAnswers((_, _) => { asked++; return true; }, () =>
            {
                // Settings/ElevatedRestart asks first, then calls Admin.RestartElevated.
                Assert.True(Dialogs.Confirm(Admin.RestartTitle, "Сейчас ещё не закончена работа…"));
                return Admin.ConfirmRestart(new PageViewModel[] { busy }, false);
            });
            Assert.True(ok);
            Assert.Equal(1, asked);

            // A "Да" to another question does not count.
            asked = 0;
            Assert.False(WithAnswers((t, _) => { asked++; return t != Admin.RestartTitle; }, () =>
            {
                Dialogs.Confirm("Нужны права администратора", "…");
                return Admin.ConfirmRestart(new PageViewModel[] { busy }, false);
            }));
            Assert.Equal(2, asked);
        }
        finally
        {
            gate.SetResult();
            await work;
        }
    }

    [Fact]
    public void Restart_FromAStandardAccount_AsksAboutTheOtherAccount()
    {
        var questions = new List<string>();
        Assert.False(WithAnswers((_, m) => { questions.Add(m); return false; },
            () => Admin.ConfirmRestart(Array.Empty<PageViewModel>(), askAboutOtherAccount: true)));
        Assert.Single(questions);
        Assert.Contains("обычная, без прав администратора", questions[0]);
    }

    [Fact]
    public void RequireElevation_Declined_DoesNotRestart()
    {
        if (Admin.IsElevated)
            return;
        Assert.False(WithAnswers((_, _) => false, () => Admin.RequireElevation("Тестовое действие")));
    }

    [Fact]
    public void Ask_IsAnsweredLikeConfirm()
    {
        Assert.True(WithAnswers((_, _) => true, () => Dialogs.Ask("Отчёт сохранён", "Открыть отчёт в браузере?")));
        Assert.False(WithAnswers((_, _) => false, () => Dialogs.Ask("Отчёт сохранён", "Открыть отчёт в браузере?")));
    }
}

[Collection(CoreSequentialCollection.Name)]
public class QuarantineIndexTests : IDisposable
{
    private readonly string _previousDataDir;
    private readonly string _files;

    public QuarantineIndexTests()
    {
        TestEnvironment.Init();
        _previousDataDir = AppPaths.DataDir;
        AppPaths.DataDir = TestEnvironment.NewTempDir("qdata");
        _files = TestEnvironment.NewTempDir("qfiles");
    }

    public void Dispose() => AppPaths.DataDir = _previousDataDir;

    private string IndexFile => Path.Combine(AppPaths.QuarantineDir, "index.json");

    private string NewFile(string name)
    {
        var path = Path.Combine(_files, name);
        File.WriteAllText(path, "test");
        return path;
    }

    [Fact]
    public void DamagedIndex_RefusesChanges_InsteadOfDroppingRecords()
    {
        var first = Quarantine.Add(NewFile("first.exe"), "Тест");
        var bytes = File.ReadAllBytes(IndexFile);
        File.WriteAllBytes(IndexFile, bytes[..(bytes.Length / 2)]); // torn write

        var second = NewFile("second.exe");
        var ex = Assert.Throws<InvalidOperationException>(() => Quarantine.Add(second, "Тест"));
        Assert.Contains("ничего не изменено", ex.Message);
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(first.StoredPath));
        Assert.Equal(bytes[..(bytes.Length / 2)], File.ReadAllBytes(IndexFile));
        Assert.Throws<InvalidOperationException>(() => Quarantine.Delete(first));
        Assert.True(File.Exists(first.StoredPath));
    }

    [Fact]
    public void DamagedIndex_ListShowsThePreviousCopy()
    {
        var a = Quarantine.Add(NewFile("a.exe"), "Тест");
        var b = Quarantine.Add(NewFile("b.exe"), "Тест");
        Assert.Equal(new[] { a.Id, b.Id }, Quarantine.List().Select(i => i.Id));

        File.WriteAllText(IndexFile, "[{\"Id\":");
        Assert.Equal(new[] { a.Id }, Quarantine.List().Select(i => i.Id));
    }

    [Fact]
    public void IndexWriteFailure_PutsTheFileBack()
    {
        var a = Quarantine.Add(NewFile("a.exe"), "Тест");
        // A folder where the new index is written makes the write fail right after the file was moved in.
        Directory.CreateDirectory(IndexFile + ".tmp");

        var b = NewFile("b.exe");
        var ex = Assert.Throws<InvalidOperationException>(() => Quarantine.Add(b, "Тест"));
        Assert.Contains("Файл оставлен на месте", ex.Message);
        Assert.True(File.Exists(b));
        Assert.Equal(new[] { Path.GetFileName(a.StoredPath) },
            Directory.EnumerateFiles(AppPaths.QuarantineDir, "*.quarantine").Select(Path.GetFileName));
        Assert.Equal(new[] { a.Id }, Quarantine.List().Select(i => i.Id));
    }

    [Fact]
    public void AddAndRestore_KeepAPreviousCopyOfTheIndex()
    {
        var original = NewFile("c.exe");
        var item = Quarantine.Add(original, "Тест");
        Assert.False(File.Exists(original));
        Quarantine.Restore(item);
        Assert.True(File.Exists(original));
        Assert.Empty(Quarantine.List());
        Assert.True(File.Exists(IndexFile + ".bak"));
        Assert.False(File.Exists(IndexFile + ".tmp"));
    }
}

[Collection(CoreSequentialCollection.Name)]
public class SettingsConcurrencyTests : IDisposable
{
    private readonly string _previousDataDir;

    public SettingsConcurrencyTests()
    {
        TestEnvironment.Init();
        _previousDataDir = AppPaths.DataDir;
        AppPaths.DataDir = TestEnvironment.NewTempDir("settings");
    }

    public void Dispose() => AppPaths.DataDir = _previousDataDir;

    [Fact]
    public async Task Save_WhileModuleDataChanges_NeverFails()
    {
        var settings = new AppSettings();
        var errors = new List<string>();
        void OnEntry(LogEntry e)
        {
            if (e.Level == LogLevel.Error)
            {
                lock (errors)
                    errors.Add(e.Message);
            }
        }

        Log.EntryAdded += OnEntry;
        try
        {
            // Like RegistryTweakEngine.StoreValues on a worker thread...
            var writer = Task.Run(() =>
            {
                for (var i = 0; i < 30000; i++)
                {
                    lock (settings)
                    {
                        if (!settings.ModuleData.TryGetValue("Optimizer", out var d))
                            settings.ModuleData["Optimizer"] = d = new Dictionary<string, string>();
                        d[$"Tweak.{i}"] = i.ToString();
                        if (i % 3 == 0)
                            d.Remove($"Tweak.{i - 1}");
                    }
                    if (i % 1000 == 0)
                        settings.Save();
                }
            });
            // ...while the UI saves other changes and other modules use Get/SetModuleValue.
            var saver = Task.Run(() =>
            {
                for (var i = 0; i < 300; i++)
                {
                    settings.SetModuleValue("Dashboard", "LastScan", i.ToString());
                    _ = settings.GetModuleValue("Optimizer", "Tweak.1");
                }
            });
            await Task.WhenAll(writer, saver);
        }
        finally
        {
            Log.EntryAdded -= OnEntry;
        }

        Assert.Empty(errors.Distinct()); // before the fix: "Не удалось сохранить настройки" (Collection was modified)
        settings.Save();
        var saved = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile))!;
        Assert.Equal("299", saved.GetModuleValue("Dashboard", "LastScan"));
        Assert.Equal("29999", saved.GetModuleValue("Optimizer", "Tweak.29999"));
    }
}

[Collection(CoreSequentialCollection.Name)]
public class RegistryImportSnapshotTests
{
    public RegistryImportSnapshotTests() => TestEnvironment.Init();

    private static string RegText(string subKey) =>
        "Windows Registry Editor Version 5.00\r\n\r\n" +
        $"[HKEY_CURRENT_USER\\Software\\WaterCa7sPcCleanerTests\\{subKey}]\r\n" +
        "\"Value\"=\"restored\"\r\n";

    [Fact]
    public async Task TheCheckedSnapshot_IsWhatGetsImported_EvenIfTheFileChangesLater()
    {
        var dir = TestEnvironment.NewTempDir("regsnap");
        var reg = Path.Combine(dir, "backup.reg");
        var subKey = "Snapshot-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(reg, RegText(subKey), Encoding.Unicode);

        string? snapshot = null;
        string? checkedText = null;
        var ok = await RegistryBackup.ImportCheckedAsync(reg, path =>
        {
            snapshot = path;
            File.WriteAllText(reg, RegText(subKey + "-swapped"), Encoding.Unicode); // swapped after the snapshot
            checkedText = File.ReadAllText(path, Encoding.Unicode);
            return checkedText.Contains($"\\{subKey}]");
        });
        try
        {
            Assert.True(ok);
            Assert.NotNull(snapshot);
            Assert.NotEqual(reg, snapshot);
            Assert.False(File.Exists(snapshot));
            using (var key = Registry.CurrentUser.OpenSubKey($@"Software\WaterCa7sPcCleanerTests\{subKey}"))
                Assert.Equal("restored", key?.GetValue("Value"));
            Assert.Null(Registry.CurrentUser.OpenSubKey($@"Software\WaterCa7sPcCleanerTests\{subKey}-swapped"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\WaterCa7sPcCleanerTests\{subKey}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\WaterCa7sPcCleanerTests\{subKey}-swapped", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task RefusedSnapshot_IsNotImported()
    {
        var dir = TestEnvironment.NewTempDir("regsnap");
        var reg = Path.Combine(dir, "backup.reg");
        var subKey = "Refused-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(reg, RegText(subKey), Encoding.Unicode);

        string? snapshot = null;
        Assert.False(await RegistryBackup.ImportCheckedAsync(reg, path => { snapshot = path; return false; }));
        Assert.NotNull(snapshot);
        Assert.False(File.Exists(snapshot));
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"Software\WaterCa7sPcCleanerTests\{subKey}"));
    }
}
