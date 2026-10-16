using System.IO;
using System.Text;
using PcCleaner.Core;
using PcCleaner.Modules.Settings;
using Xunit;

namespace PcCleaner.Tests.Settings;

public class BackupCatalogTests
{
    public BackupCatalogTests() => TestEnvironment.Init();

    [Theory]
    [InlineData("20240131-101500-123_HKCU_Software_Microsoft_Windows_CurrentVersion_Run.reg",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData("20240131-101500-123_HKLM_SYSTEM_CurrentControlSet_Services_SysMain.reg",
        @"HKLM\SYSTEM\CurrentControlSet\Services\SysMain")]
    [InlineData("20240131-101500-123_HKEY_LOCAL_MACHINE_SOFTWARE_Vendor.reg", @"HKLM\SOFTWARE\Vendor")]
    [InlineData("20240131-101500-123_HKEY_CURRENT_USER_Control_Panel.reg", @"HKCU\Control\Panel")]
    [InlineData("20240131-101500-123_HKCU.reg", "HKCU")]
    [InlineData("20240131-101500-123_Windows_CurrentVersion_Run.reg", @"…\Windows\CurrentVersion\Run")]
    public void KeyFromFileName_RestoresReadableKey(string fileName, string expected)
        => Assert.Equal(expected, BackupCatalog.KeyFromFileName(fileName));

    [Fact]
    public void TimeFromFileName_ParsesStamp()
    {
        Assert.Equal(new DateTime(2024, 1, 31, 10, 15, 0, 123),
            BackupCatalog.TimeFromFileName("20240131-101500-123_HKCU_Software.reg"));
        Assert.Equal(new DateTime(2024, 1, 31, 10, 15, 0),
            BackupCatalog.TimeFromFileName("20240131-101500_hosts.hosts"));
        Assert.Null(BackupCatalog.TimeFromFileName("manual-backup.reg"));
    }

    [Theory]
    [InlineData(@"HKEY_CURRENT_USER\Software\X", @"HKCU\Software\X", false)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\X", @"HKLM\SOFTWARE\X", true)]
    [InlineData(@"HKEY_CLASSES_ROOT\.txt", @"HKCR\.txt", true)]
    [InlineData(@"HKCU\Software", @"HKCU\Software", false)]
    [InlineData("HKEY_USERS", "HKU", true)]
    public void ShortHive_AndAdminRequirement(string key, string shortKey, bool needsAdmin)
    {
        Assert.Equal(shortKey, BackupCatalog.ShortHive(key));
        Assert.Equal(needsAdmin, BackupCatalog.NeedsAdmin(key));
    }

    [Theory]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Автозагрузка программ (Run) — текущий пользователь")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Автозагрузка программ (Run) — все пользователи")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce", "Однократный автозапуск (RunOnce) — текущий пользователь")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        "Включение и отключение автозагрузки — текущий пользователь")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\SysMain", "Параметры службы «SysMain»")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip", "Запись об установленной программе «7-Zip»")]
    [InlineData(@"HKCU\Software\Vendor\Some Tool", "Раздел «Some Tool»")]
    public void FriendlyKeyName_DescribesKnownKeys(string key, string expected)
        => Assert.Equal(expected, BackupCatalog.FriendlyKeyName(key));

    [Fact]
    public void ListIn_DescribesRegistryAndOtherBackups()
    {
        var dir = TestEnvironment.NewTempDir("backups");
        try
        {
            // Real reg.exe exports are UTF-16 LE with BOM. The key contains a space and an underscore,
            // which the file name cannot represent — the content must win.
            var reg = Path.Combine(dir, "20240131-101500-123_HKCU_Software_My_App_Test.reg");
            File.WriteAllText(reg,
                "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\My App_Test]\r\n\"Value\"=\"1\"\r\n",
                Encoding.Unicode);
            File.WriteAllText(reg + ".txt", "Перед отключением автозагрузки «Updater»");

            var hosts = Path.Combine(dir, "20240201-090000_hosts.hosts");
            File.WriteAllText(hosts, "127.0.0.1 localhost");

            var task = Path.Combine(dir, "20240301-080000_Updater.xml");
            File.WriteAllText(task,
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<RegistrationInfo><URI>\\Vendor\\Updater Task</URI></RegistrationInfo><Actions /></Task>",
                Encoding.Unicode);

            var notes = Path.Combine(dir, "notes.txt");
            File.WriteAllText(notes, "anything");
            File.SetLastWriteTime(notes, new DateTime(2023, 5, 1));

            var list = BackupCatalog.ListIn(dir);

            Assert.Equal(4, list.Count);
            Assert.Equal(new[] { task, hosts, reg, notes }, list.Select(b => b.FullPath));

            var r = list.Single(b => b.Kind == BackupKind.Registry);
            Assert.Equal(@"HKCU\Software\My App_Test", r.KeyPath);
            Assert.Equal("Раздел «My App_Test»", r.Title);
            Assert.Equal("Перед отключением автозагрузки «Updater»", r.Description);
            Assert.Equal(reg + ".txt", r.DescriptionPath);
            Assert.False(r.NeedsAdmin);
            Assert.True(r.CanRestore);
            Assert.True(r.CanDelete);
            Assert.Equal(new DateTime(2024, 1, 31, 10, 15, 0, 123), r.Time);

            var h = list.Single(b => b.Kind == BackupKind.Hosts);
            Assert.False(h.CanRestore);
            Assert.False(h.CanDelete);

            var t = list.Single(b => b.Kind == BackupKind.ScheduledTask);
            Assert.Equal("Задание планировщика «Vendor\\Updater Task»", t.Title);

            Assert.Equal(BackupKind.Other, list.Single(b => b.FullPath == notes).Kind);
            Assert.DoesNotContain(list, b => b.FullPath.EndsWith(".reg.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ListIn_FallsBackToFileNameWhenContentIsUnreadable()
    {
        var dir = TestEnvironment.NewTempDir("backups");
        try
        {
            var reg = Path.Combine(dir, "20240131-101500-123_HKLM_SOFTWARE_Vendor.reg");
            File.WriteAllText(reg, "not a registry file");
            var entry = Assert.Single(BackupCatalog.ListIn(dir));
            Assert.Equal(@"HKLM\SOFTWARE\Vendor", entry.KeyPath);
            Assert.True(entry.NeedsAdmin);
            Assert.Equal("Реестр (система)", entry.KindText);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ListIn_MissingFolder_IsEmpty()
        => Assert.Empty(BackupCatalog.ListIn(Path.Combine(TestEnvironment.DataDir, "no-such-folder-" + Guid.NewGuid())));

    [Fact]
    public void List_UsesAppBackupsFolder()
    {
        Directory.CreateDirectory(AppPaths.BackupsDir);
        var reg = Path.Combine(AppPaths.BackupsDir, $"20250101-000000-000_HKCU_Software_SettingsTest{Guid.NewGuid():N}.reg");
        File.WriteAllText(reg, "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\SettingsTest]\r\n",
            Encoding.Unicode);
        try
        {
            var entry = Assert.Single(BackupCatalog.List(), b => b.FullPath == reg);
            Assert.Equal(@"HKCU\Software\SettingsTest", entry.KeyPath);
        }
        finally
        {
            File.Delete(reg);
        }
    }
}

public class BackupDeletionGuardTests
{
    public BackupDeletionGuardTests() => TestEnvironment.Init();

    [Fact]
    public void IsInside_AcceptsOnlyRealChildren()
    {
        var root = TestEnvironment.NewTempDir("guard");
        var backups = Path.Combine(root, "Backups");
        Directory.CreateDirectory(backups);
        try
        {
            Assert.True(PathGuard.IsInside(Path.Combine(backups, "a.reg"), backups));
            Assert.True(PathGuard.IsInside(Path.Combine(backups, "sub", "a.reg"), backups + "\\"));
            Assert.False(PathGuard.IsInside(backups, backups));
            Assert.False(PathGuard.IsInside(backups + "\\", backups));
            Assert.False(PathGuard.IsInside(Path.Combine(root, "BackupsEvil", "a.reg"), backups));
            Assert.False(PathGuard.IsInside(Path.Combine(backups, "..", "settings.json"), backups));
            Assert.False(PathGuard.IsInside(@"C:\Windows\System32\drivers\etc\hosts", backups));
            Assert.False(PathGuard.IsInside(null, backups));
            Assert.False(PathGuard.IsInside("", backups));
            Assert.False(PathGuard.IsInside(Path.Combine(backups, "a.reg"), null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteBackup_RefusesFilesOutsideBackupsFolder()
    {
        var root = TestEnvironment.NewTempDir("guard");
        var backups = Path.Combine(root, "Backups");
        Directory.CreateDirectory(backups);
        var outside = Path.Combine(root, "important.reg");
        File.WriteAllText(outside, "keep me");
        var sibling = Path.Combine(root, "BackupsOld", "x.reg");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllText(sibling, "keep me too");
        try
        {
            var (ok, message) = BackupCatalog.DeleteBackup(outside, backups);
            Assert.False(ok);
            Assert.Contains("вне папки", message);
            Assert.True(File.Exists(outside));

            (ok, _) = BackupCatalog.DeleteBackup(Path.Combine(backups, "..", "important.reg"), backups);
            Assert.False(ok);
            Assert.True(File.Exists(outside));

            (ok, _) = BackupCatalog.DeleteBackup(sibling, backups);
            Assert.False(ok);
            Assert.True(File.Exists(sibling));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteBackup_RefusesNonRegistryFiles()
    {
        var backups = TestEnvironment.NewTempDir("guard");
        var hosts = Path.Combine(backups, "20240101-000000_hosts.hosts");
        File.WriteAllText(hosts, "127.0.0.1 localhost");
        try
        {
            var (ok, _) = BackupCatalog.DeleteBackup(hosts, backups);
            Assert.False(ok);
            Assert.True(File.Exists(hosts));
        }
        finally
        {
            Directory.Delete(backups, recursive: true);
        }
    }

    [Fact]
    public void DeleteBackup_RemovesRegFileAndDescription()
    {
        var backups = TestEnvironment.NewTempDir("guard");
        var reg = Path.Combine(backups, "20240101-000000-000_HKCU_Software_X.reg");
        File.WriteAllText(reg, "Windows Registry Editor Version 5.00");
        File.WriteAllText(reg + ".txt", "описание");
        var neighbour = Path.Combine(backups, "20240101-000000-000_HKCU_Software_Y.reg");
        File.WriteAllText(neighbour, "Windows Registry Editor Version 5.00");
        try
        {
            var (ok, message) = BackupCatalog.DeleteBackup(reg, backups);
            Assert.True(ok, message);
            Assert.False(File.Exists(reg));
            Assert.False(File.Exists(reg + ".txt"));
            Assert.True(File.Exists(neighbour));
        }
        finally
        {
            Directory.Delete(backups, recursive: true);
        }
    }
}
