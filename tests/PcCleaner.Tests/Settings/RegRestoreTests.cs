using System.IO;
using System.Text;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Settings;
using Xunit;

namespace PcCleaner.Tests.Settings;

/// <summary>
/// Restoring a registry backup: the strict .reg parser, the list of changes shown before the import,
/// and the private locked copy that is imported instead of the file in the user-writable backups folder.
/// </summary>
public class RegFileParserTests
{
    private const string Header = "Windows Registry Editor Version 5.00\r\n\r\n";
    private const string Root = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Evil";

    public RegFileParserTests() => TestEnvironment.Init();

    private static byte[] Utf16(string text) => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();

    [Fact]
    public void Parse_ReadsEveryValueOfAPlainExport()
    {
        var info = RegFileParser.Parse(Utf16(Header +
            $"[{Root}]\r\n" +
            "@=\"default\"\r\n" +
            "\"Type\"=dword:00000010\r\n" +
            "\"Quote\\\"Back\\\\\"=\"a\\\"b\\\\c\"\r\n" +
            "\"ImagePath\"=hex(2):43,00,3a,00,5c,00,78,00,2e,00,65,00,78,00,65,00,00,00\r\n" +
            "\"Multi\"=hex(7):61,00,00,00,62,00,00,00,00,00\r\n" +
            "\"Big\"=hex(b):01,00,00,00,00,00,00,00\r\n" +
            "\"Bin\"=hex:01,02,\\\r\n  03,04\r\n" +
            "\"None\"=hex(0):\r\n" +
            "\"Type\"=dword:00000020\r\n\r\n" +
            $"[{Root}\\Parameters]\r\n" +
            "\"ServiceDll\"=\"C:\\\\evil.dll\"\r\n\r\n"));

        Assert.Null(info.Problem);
        Assert.Equal(@"HKLM\SYSTEM\CurrentControlSet\Services\Evil", info.RootKey);
        Assert.True(info.SingleRoot);
        Assert.False(info.DeletesKeys);
        Assert.False(info.DeletesValues);
        Assert.Null(SettingsViewModel.RestoreRefusal(info));
        Assert.Equal(new[] { @"HKLM\SYSTEM\CurrentControlSet\Services\Evil", @"HKLM\SYSTEM\CurrentControlSet\Services\Evil\Parameters" },
            info.Keys);

        object Value(string name) => info.Values.Single(v => v.Name == name).Data;
        Assert.Equal("default", Value(""));
        Assert.Equal(0x20u, Value("Type")); // a later line for the same value wins, as in reg.exe
        Assert.Equal("a\"b\\c", Value("Quote\"Back\\"));
        Assert.Equal(@"C:\x.exe", Value("ImagePath"));
        Assert.Equal(2, info.Values.Single(v => v.Name == "ImagePath").Type);
        Assert.Equal(new[] { "a", "b" }, (string[])Value("Multi"));
        Assert.Equal(1ul, Value("Big"));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, (byte[])Value("Bin"));
        Assert.Empty((byte[])Value("None"));
        Assert.Equal(@"C:\evil.dll", Value("ServiceDll"));
        Assert.Equal(9, info.Values.Count);
    }

    // Each of these is imported by reg.exe differently from what a naive reader shows (checked against the real
    // reg.exe: an unclosed string is dropped and the next line is honoured, text after a string drops the value, …),
    // so a file containing them is never imported.
    [Theory]
    [InlineData("\"A\"=\"x\r\n\"Evil\"=\"C:\\\\evil.exe\"\r\n")] // string spanning lines: reg.exe writes only "Evil"
    [InlineData("\"V\"=\"1\" junk\r\n")]
    [InlineData("\"V\" = \"1\"\r\n")]
    [InlineData("  \"V\"=\"1\"\r\n")]
    [InlineData(";\"C\"=\"1\"\r\n")]
    [InlineData("\"H\"=hex:01,02,\\\r\n03,04\r\n")]
    [InlineData("\"H\"=hex:01,02,\\\r\n")]
    [InlineData("\"V\"=Dword:00000001\r\n")]
    [InlineData("\"V\"=dword:1\r\n")]
    [InlineData("\"V\"=\"a\\nb\"\r\n")]
    [InlineData("\"V\"=\"a\nb\"\r\n")]
    [InlineData("[HKCU\\Software\\X]\r\n")]
    [InlineData("[HKEY_LOCAL_MACHINE\\SYSTEM\\\\X]\r\n")]
    public void Parse_RefusesAnythingThatIsNotAPlainExport(string body)
    {
        var info = RegFileParser.Parse(Utf16(Header + $"[{Root}]\r\n" + body));
        Assert.NotNull(info.Problem);
        Assert.NotNull(SettingsViewModel.RestoreRefusal(info));
    }

    [Fact]
    public void Parse_RefusesOtherEncodingsHeadersAndBrokenCharacters()
    {
        var text = Header + $"[{Root}]\r\n\"V\"=\"1\"\r\n";
        Assert.NotNull(RegFileParser.Parse(Encoding.UTF8.GetBytes(text)).Problem);
        Assert.NotNull(RegFileParser.Parse(Utf16("REGEDIT4\r\n\r\n" + $"[{Root}]\r\n")).Problem);
        Assert.NotNull(RegFileParser.Parse(Utf16(text.Replace("\"1\"", "\"a\0b\""))).Problem); // NUL inside the text

        // A lone surrogate (Encoding.Unicode would turn it into U+FFFD, so the bytes are patched by hand).
        var bytes = Utf16(text.Replace("\"1\"", "\"\u0001\""));
        var at = Enumerable.Range(0, bytes.Length - 1).First(i => i % 2 == 0 && bytes[i] == 1 && bytes[i + 1] == 0);
        bytes[at] = 0x00;
        bytes[at + 1] = 0xD8;
        Assert.NotNull(RegFileParser.Parse(bytes).Problem);
        Assert.NotNull(RegFileParser.Parse(Utf16("\"V\"=\"1\"\r\n")).Problem);
        Assert.Null(RegFileParser.Parse(Utf16(text)).Problem);
    }

    [Fact]
    public void RestoreRefusal_RejectsValueDeletions()
    {
        var info = RegFileParser.Parse(Utf16(Header + $"[{Root}]\r\n\"Start\"=-\r\n"));
        Assert.Null(info.Problem);
        Assert.True(info.DeletesValues);
        Assert.Contains("удаляет значения", SettingsViewModel.RestoreRefusal(info));
    }

    [Fact]
    public void ReadForRestore_TooBigOrMissingFile_IsRefused()
    {
        var dir = TestEnvironment.NewTempDir("reg");
        try
        {
            var big = Path.Combine(dir, "big.reg");
            using (var f = new FileStream(big, FileMode.CreateNew))
                f.SetLength(RegFileParser.MaxBytes + 2L);
            var (bytes, info) = BackupCatalog.ReadForRestore(big);
            Assert.Null(bytes);
            Assert.NotNull(info.Problem);
            Assert.NotNull(SettingsViewModel.RestoreRefusal(info));

            (bytes, info) = BackupCatalog.ReadForRestore(Path.Combine(dir, "missing.reg"));
            Assert.Null(bytes);
            Assert.NotNull(SettingsViewModel.RestoreRefusal(info));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class RegRestorePlanTests
{
    private const string Service = @"HKLM\SYSTEM\CurrentControlSet\Services\Evil";

    public RegRestorePlanTests() => TestEnvironment.Init();

    private static RegFileInfo Plant() => RegFileParser.ParseText(
        "Windows Registry Editor Version 5.00\r\n\r\n" +
        $"[HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\Evil]\r\n" +
        "\"Type\"=dword:00000010\r\n" +
        "\"Start\"=dword:00000002\r\n" +
        "\"ImagePath\"=\"C:\\\\Users\\\\Public\\\\updater.exe --silent\"\r\n" +
        "\"DisplayName\"=\"Windows Update Helper\"\r\n");

    [Fact]
    public void PlantedServiceBackup_ConfirmationShowsTheProgramItStarts()
    {
        // The scenario from the security review: a single-root file with additions only passes every structural check,
        // so the confirmation itself must show what would be written — above all the command line.
        var reg = Plant();
        Assert.Null(SettingsViewModel.RestoreRefusal(reg));

        var plan = RegRestorePlan.Build(reg, _ => CurrentRegKey.Missing);
        Assert.Equal(4, plan.Changes.Count);
        var launch = Assert.Single(plan.LaunchChanges);
        Assert.Equal("ImagePath", launch.Value.Name);

        var message = SettingsViewModel.RestoreConfirmation("01.02.2024 10:15", "Перед отключением службы", Service,
            BackupCatalog.FriendlyKeyName(Service), exists: false, plan);
        Assert.Contains("Внимание: эти значения запускают программы", message);
        Assert.Contains(@"ImagePath = C:\Users\Public\updater.exe --silent (сейчас нет)", message);
        Assert.Contains("Start = 2 (сейчас нет)", message);
        Assert.Contains("Type = 16 (0x10) (сейчас нет)", message);
        Assert.Contains("он будет создан заново", message);
        Assert.True(message.IndexOf("ImagePath", StringComparison.Ordinal) < message.IndexOf("Start =", StringComparison.Ordinal),
            "values that start programs are listed first");
    }

    [Fact]
    public void Build_ComparesWithTheCurrentRegistry()
    {
        var reg = Plant();
        var current = new CurrentRegKey(true, true, new Dictionary<string, CurrentRegValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = new(4, 0x10u),                                      // same (names are case-insensitive)
            ["Start"] = new(4, 4u),                                        // changed: disabled → automatic
            ["ImagePath"] = new(2, @"C:\Users\Public\updater.exe --silent"), // same text, different type → change
            ["DisplayName"] = new(1, "Windows Update Helper"),
        });

        var plan = RegRestorePlan.Build(reg, k => string.Equals(k, Service, StringComparison.OrdinalIgnoreCase)
            ? current
            : CurrentRegKey.Missing);

        Assert.Equal(2, plan.Unchanged);
        Assert.Equal(new[] { "Start", "ImagePath" }, plan.Changes.Select(c => c.Value.Name));
        Assert.Contains("Start: 4 → 2", plan.DescribeChanges());
        Assert.False(plan.ChangesNothing);

        var same = RegRestorePlan.Build(reg, _ => new CurrentRegKey(true, true,
            reg.Values.ToDictionary(v => v.Name, v => new CurrentRegValue(v.Type, v.Data), StringComparer.OrdinalIgnoreCase)));
        Assert.True(same.ChangesNothing);
        Assert.Equal(4, same.Unchanged);
    }

    [Fact]
    public void Build_UnreadableKeyShowsEveryValueAndNewSubkeysAreListed()
    {
        var reg = RegFileParser.ParseText(
            "Windows Registry Editor Version 5.00\r\n\r\n" +
            "[HKEY_CURRENT_USER\\Software\\Vendor]\r\n\"A\"=\"1\"\r\n\r\n" +
            "[HKEY_CURRENT_USER\\Software\\Vendor\\Sub]\r\n\"B\"=\"2\"\r\n");
        var plan = RegRestorePlan.Build(reg, k => k.EndsWith(@"\Sub", StringComparison.Ordinal)
            ? CurrentRegKey.Missing
            : CurrentRegKey.Unreadable);

        Assert.Equal(2, plan.Changes.Count);
        Assert.False(plan.Changes[0].CurrentKnown);
        Assert.Equal(new[] { @"HKCU\Software\Vendor\Sub" }, plan.NewKeys);
        var text = plan.DescribeChanges();
        Assert.Contains("A = 1", text);
        Assert.Contains(@"Sub\B = 2 (сейчас нет)", text);
        Assert.Contains("Будут созданы разделы:\n• Sub", text);
    }

    [Theory]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Updater", "cmd /c start x", true)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\sethc.exe", "Debugger", "cmd", true)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Userinit", "x", true)]
    [InlineData(@"HKCU\Software\Classes\txtfile\shell\open\command", "", "notepad %1", true)]
    [InlineData(@"HKCU\Software\Vendor", "Path", @"C:\Tools\tool.exe", true)]
    [InlineData(@"HKCU\Software\Vendor", "Theme", "dark", false)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\SysMain", "Start", "2", false)]
    public void IsLaunchValue_FindsValuesThatStartPrograms(string key, string name, string data, bool expected)
        => Assert.Equal(expected, RegRestorePlan.IsLaunchValue(new RegFileValue(key, name, 1, data)));

    [Fact]
    public void Visible_EscapesLineBreaksAndInvisibleCharacters()
    {
        Assert.Equal(@"a\r\nb", RegRestorePlan.Visible("a\r\nb", 50));
        Assert.Equal(@"evil\u202Eexe.txt", RegRestorePlan.Visible("evil\u202Eexe.txt", 50)); // right-to-left override
        var shortened = RegRestorePlan.Visible(new string('a', 30) + "END", 20);
        Assert.Equal(20, shortened.Length);
        Assert.EndsWith("END", shortened);
        Assert.Equal("a b c", RegRestorePlan.OneLine(" a\r\nb\tc ", 50));
    }

    [Fact]
    public void RestoreConfirmation_EscapesDescriptionAndMentionsTheJournalWhenShortened()
    {
        var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\Vendor]\r\n");
        for (var i = 0; i < RegRestorePlan.MaxShownChanges + 3; i++)
            text.Append($"\"V{i}\"=\"{i}\"\r\n");
        var reg = RegFileParser.ParseText(text.ToString());
        var plan = RegRestorePlan.Build(reg, _ => CurrentRegKey.Missing);
        Assert.True(plan.IsTruncated);

        var message = SettingsViewModel.RestoreConfirmation("01.02.2024", "Безопасно\n\nЧто изменится: ничего", @"HKCU\Software\Vendor",
            "Раздел «Vendor»", exists: true, plan);
        Assert.Contains("Копия сделана перед действием: Безопасно Что изменится: ничего\n", message);
        Assert.Contains("…и ещё 3", message);
        Assert.Contains("вкладку «Журнал»", message);
        Assert.Contains("текущее состояние раздела сохранится", message);
        Assert.DoesNotContain("Внимание", message);
    }
}

public class LockedRegCopyTests
{
    public LockedRegCopyTests() => TestEnvironment.Init();

    [Fact]
    public void Copy_HoldsExactBytesAndCannotBeChangedUntilDisposed()
    {
        var parent = TestEnvironment.NewTempDir("copy");
        try
        {
            var bytes = Encoding.Unicode.GetBytes("\ufeffWindows Registry Editor Version 5.00\r\n");
            string path;
            using (var copy = LockedRegCopy.Create(bytes, @"..\..\20240101-000000-000_HKCU_Software_X.reg", parent))
            {
                Assert.NotNull(copy);
                path = copy!.FilePath;
                Assert.Equal("20240101-000000-000_HKCU_Software_X.reg", Path.GetFileName(path));
                Assert.StartsWith(parent, path, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(bytes, File.ReadAllBytes(path)); // reg.exe can read it

                Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "evil"));
                Assert.ThrowsAny<IOException>(() => File.Delete(path));
                Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".old"));
            }
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Create_UsesSafeNameForOddFileNames()
    {
        var parent = TestEnvironment.NewTempDir("copy");
        try
        {
            using var copy = LockedRegCopy.Create(new byte[] { 1, 2 }, "not-a-reg.txt", parent);
            Assert.Equal("backup.reg", Path.GetFileName(copy!.FilePath));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}

/// <summary>
/// Round trip with the real reg.exe in the sandbox key HKCU\Software\WaterCa7sPcCleanerTests\Settings\&lt;id&gt;:
/// what the parser reads from an export must be exactly what is in the registry, and the import must use the
/// bytes that were checked even if the backup file is rewritten afterwards.
/// </summary>
public sealed class RegRestoreRoundTripTests : IDisposable
{
    private const string SandboxRoot = @"Software\WaterCa7sPcCleanerTests\Settings";
    private readonly string _sandbox = $@"{SandboxRoot}\{Guid.NewGuid():N}";
    private readonly List<string> _files = new();

    public RegRestoreRoundTripTests() => TestEnvironment.Init();

    private string FullKey => $@"HKCU\{_sandbox}";

    public void Dispose()
    {
        // The shared parent keys are removed at exit by TestEnvironment.
        Registry.CurrentUser.DeleteSubKeyTree(_sandbox, throwOnMissingSubKey: false);
        foreach (var f in _files)
        {
            try
            {
                File.Delete(f);
                File.Delete(f + ".txt");
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task<string> ExportAsync()
    {
        var file = await RegistryBackup.ExportAsync(FullKey, "test");
        Assert.NotNull(file);
        _files.Add(file!);
        return file!;
    }

    [Fact]
    public async Task ExportParsesToExactlyTheRegistryValues_AndImportUsesTheCheckedBytes()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_sandbox))
        {
            key.SetValue("", "default");
            key.SetValue("Text", "plain");
            key.SetValue("Quote\"Back\\", "a\"b\\c=d");
            key.SetValue("Empty", "");
            key.SetValue("Expand", @"%SystemRoot%\system32\notepad.exe", RegistryValueKind.ExpandString);
            key.SetValue("Multi", new[] { "one", "two words" }, RegistryValueKind.MultiString);
            key.SetValue("Dword", unchecked((int)0xFFFFFFFE), RegistryValueKind.DWord);
            key.SetValue("Qword", 0x1122334455667788L, RegistryValueKind.QWord);
            key.SetValue("Binary", Enumerable.Range(0, 100).Select(i => (byte)i).ToArray(), RegistryValueKind.Binary);
            key.SetValue("None", Array.Empty<byte>(), RegistryValueKind.None);
            key.SetValue("Юникод", "значение");
            using var sub = key.CreateSubKey("Sub Key");
            sub.SetValue("Inner", 1, RegistryValueKind.DWord);
        }

        var file = await ExportAsync();
        var (bytes, reg) = BackupCatalog.ReadForRestore(file);
        Assert.NotNull(bytes);
        Assert.Null(reg.Problem);
        Assert.Null(SettingsViewModel.RestoreRefusal(reg));
        Assert.Equal(FullKey, reg.RootKey);
        Assert.Equal(12, reg.Values.Count);

        // Parsed values equal the live registry exactly (types and data).
        var plan = RegRestorePlan.Build(reg, RegRestorePlan.ReadCurrent);
        Assert.True(plan.ChangesNothing, plan.DescribeAll());
        Assert.Equal(12, plan.Unchanged);

        // Change the registry: the plan lists exactly what the restore would put back.
        using (var key = Registry.CurrentUser.OpenSubKey(_sandbox, writable: true)!)
        {
            key.SetValue("Text", "changed");
            key.DeleteValue("Dword");
            key.DeleteSubKeyTree("Sub Key");
        }
        plan = RegRestorePlan.Build(reg, RegRestorePlan.ReadCurrent);
        Assert.Equal(new[] { "Text", "Dword", "Inner" }, plan.Changes.Select(c => c.Value.Name));
        Assert.Equal(new[] { $@"{FullKey}\Sub Key" }, plan.NewKeys);

        // Someone rewrites the backup file after it was checked: the import still writes the checked bytes.
        File.WriteAllText(file, "Windows Registry Editor Version 5.00\r\n\r\n" +
                                $"[HKEY_CURRENT_USER\\{_sandbox}]\r\n\"Text\"=\"tampered\"\r\n", Encoding.Unicode);
        using (var copy = LockedRegCopy.Create(bytes!, Path.GetFileName(file)))
        {
            Assert.NotNull(copy);
            Assert.True(await RegistryBackup.ImportAsync(copy!.FilePath));
        }

        using (var key = Registry.CurrentUser.OpenSubKey(_sandbox)!)
        {
            Assert.Equal("plain", key.GetValue("Text"));
            Assert.Equal(unchecked((int)0xFFFFFFFE), key.GetValue("Dword"));
            using var sub = key.OpenSubKey("Sub Key");
            Assert.Equal(1, sub?.GetValue("Inner"));
        }
        Assert.True(RegRestorePlan.Build(reg, RegRestorePlan.ReadCurrent).ChangesNothing);
    }

    [Fact]
    public async Task MultiLineStringInExport_IsRefusedInsteadOfImportedWrongly()
    {
        // reg.exe writes such a string across lines but cannot import it back (it drops the value and reads the rest
        // as separate lines), so the parser refuses the whole file instead of showing something reg.exe would not do.
        using (var key = Registry.CurrentUser.CreateSubKey(_sandbox))
            key.SetValue("Notes", "line1\r\nline2");
        var file = await ExportAsync();

        var (_, reg) = BackupCatalog.ReadForRestore(file);
        Assert.NotNull(reg.Problem);
        Assert.NotNull(SettingsViewModel.RestoreRefusal(reg));
    }
}

public class TrustedItemTextTests
{
    [Theory]
    [InlineData(@"reg|HKCU|Software\Microsoft\Windows\CurrentVersion\Run|Updater", "Автозагрузка: Updater (реестр, текущий пользователь)")]
    [InlineData(@"reg|HKLM|SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce|A|B",
        "Автозагрузка: A|B (реестр, все пользователи, однократный запуск, 32-разрядные программы)")]
    [InlineData(@"task|\Vendor\Updater Task", @"Задание планировщика: Vendor\Updater Task")]
    [InlineData("svc|VendorSvc", "Служба: VendorSvc")]
    [InlineData(@"lnk|C:\Users\u\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\Helper.lnk",
        "Автозагрузка: Helper (папка «Автозагрузка»)")]
    [InlineData("RegistryRun|HKCU|Updater", "Запись автозагрузки: Updater")]
    [InlineData(@"C:\Program Files\Tools\tool.exe", @"C:\Program Files\Tools\tool.exe")]
    [InlineData(@"\\server\share\tool.exe", @"\\server\share\tool.exe")]
    [InlineData("7-Zip 23.01 (x64)", "Программа: 7-Zip 23.01 (x64)")]
    [InlineData("", "")]
    public void Label_IsReadable(string item, string expected)
        => Assert.Equal(expected, TrustedItemText.Label(item));

    [Fact]
    public void ToolTip_KeepsTheStoredKey()
    {
        Assert.Equal("Служба: VendorSvc\nsvc|VendorSvc", TrustedItemText.ToolTip("svc|VendorSvc"));
        Assert.Equal(@"C:\x.exe", TrustedItemText.ToolTip(@"C:\x.exe"));
        Assert.Equal("Служба: VendorSvc", new TrustedItemLabelConverter().Convert("svc|VendorSvc", typeof(string), null,
            System.Globalization.CultureInfo.InvariantCulture));
    }
}
