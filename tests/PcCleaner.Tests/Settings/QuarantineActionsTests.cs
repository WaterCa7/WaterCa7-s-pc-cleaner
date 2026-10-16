using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Settings;
using Xunit;

namespace PcCleaner.Tests.Settings;

public class QuarantineActionsTests
{
    public QuarantineActionsTests() => TestEnvironment.Init();

    [Fact]
    public void AlternativePath_AddsRussianSuffixAndCounts()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Apps\tool (восстановлен).exe",
            @"C:\Apps\tool (восстановлен 2).exe",
        };
        Assert.Equal(@"C:\Apps\tool (восстановлен 3).exe", QuarantineActions.AlternativePath(@"C:\Apps\tool.exe", taken.Contains));
        Assert.Equal(@"C:\Apps\readme (восстановлен)", QuarantineActions.AlternativePath(@"C:\Apps\readme", _ => false));
    }

    [Fact]
    public void Restore_WhenOriginalPathIsTaken_ReportsConflictThenRestoresUnderNewName()
    {
        var dir = TestEnvironment.NewTempDir("quarantine");
        var original = Path.Combine(dir, "updater.exe");
        File.WriteAllText(original, "quarantined content");
        var item = Quarantine.Add(original, "Тест: конфликт имён");
        try
        {
            Assert.False(File.Exists(original));
            Assert.True(PathGuard.IsInside(item.StoredPath, AppPaths.QuarantineDir));

            // Meanwhile another file appeared at the original location.
            File.WriteAllText(original, "new file that must stay untouched");

            var conflict = QuarantineActions.Restore(item, renameIfExists: false);
            Assert.Equal(QuarantineRestoreStatus.Conflict, conflict.Status);
            Assert.False(conflict.Success);
            Assert.Contains("уже есть файл", conflict.Message);
            Assert.Equal("new file that must stay untouched", File.ReadAllText(original));
            Assert.True(File.Exists(item.StoredPath));
            Assert.Contains(Quarantine.List(), i => i.Id == item.Id);

            var renamed = QuarantineActions.Restore(item, renameIfExists: true);
            Assert.Equal(QuarantineRestoreStatus.RestoredAs, renamed.Status);
            Assert.Equal(Path.Combine(dir, "updater (восстановлен).exe"), renamed.TargetPath);
            Assert.Equal("quarantined content", File.ReadAllText(renamed.TargetPath!));
            Assert.Equal("new file that must stay untouched", File.ReadAllText(original));
            Assert.False(File.Exists(item.StoredPath));
            Assert.DoesNotContain(Quarantine.List(), i => i.Id == item.Id);
        }
        finally
        {
            Cleanup(item, dir);
        }
    }

    [Fact]
    public void Restore_WithoutConflict_ReturnsFileToOriginalPath()
    {
        var dir = TestEnvironment.NewTempDir("quarantine");
        var original = Path.Combine(dir, "sub", "script.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllText(original, "Write-Host hi");
        var item = Quarantine.Add(original, "Тест");
        try
        {
            Directory.Delete(Path.GetDirectoryName(original)!); // folder vanished meanwhile — it is recreated
            var result = QuarantineActions.Restore(item, renameIfExists: false);
            Assert.Equal(QuarantineRestoreStatus.Restored, result.Status);
            Assert.Equal("Write-Host hi", File.ReadAllText(original));
            Assert.DoesNotContain(Quarantine.List(), i => i.Id == item.Id);
        }
        finally
        {
            Cleanup(item, dir);
        }
    }

    [Fact]
    public void Restore_WhenStoredFileIsMissing_ReportsIt()
    {
        var dir = TestEnvironment.NewTempDir("quarantine");
        var original = Path.Combine(dir, "gone.dll");
        File.WriteAllText(original, "x");
        var item = Quarantine.Add(original, "Тест");
        try
        {
            File.Delete(item.StoredPath);
            var result = QuarantineActions.Restore(item, renameIfExists: true);
            Assert.Equal(QuarantineRestoreStatus.MissingStoredFile, result.Status);
            Assert.False(File.Exists(original));

            // Removing the dangling record is safe.
            var (ok, _) = QuarantineActions.Delete(item);
            Assert.True(ok);
            Assert.DoesNotContain(Quarantine.List(), i => i.Id == item.Id);
        }
        finally
        {
            Cleanup(item, dir);
        }
    }

    [Fact]
    public void ForgedRecordPointingOutsideQuarantine_IsNeverMovedOrDeleted()
    {
        var dir = TestEnvironment.NewTempDir("quarantine");
        var victim = Path.Combine(dir, "victim.txt");
        File.WriteAllText(victim, "must survive");
        var forged = new QuarantineItem(Guid.NewGuid().ToString("N"), Path.Combine(dir, "restored.txt"), victim,
            DateTime.Now, "forged");
        try
        {
            var restore = QuarantineActions.Restore(forged, renameIfExists: true);
            Assert.Equal(QuarantineRestoreStatus.Failed, restore.Status);
            Assert.True(File.Exists(victim));
            Assert.False(File.Exists(forged.OriginalPath));

            var (ok, _) = QuarantineActions.Delete(forged);
            Assert.False(ok);
            Assert.True(File.Exists(victim));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ForgedRecordPointingIntoSubfolderOrIndex_IsNeverMovedOrDeleted()
    {
        // A subfolder inside the quarantine folder could be a junction to anywhere; Quarantine.Add never stores files there.
        Directory.CreateDirectory(AppPaths.QuarantineDir);
        var sub = Path.Combine(AppPaths.QuarantineDir, "linked-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sub);
        var victim = Path.Combine(sub, "victim.quarantine");
        File.WriteAllText(victim, "must survive");
        var dir = TestEnvironment.NewTempDir("quarantine");
        try
        {
            var nested = new QuarantineItem(Guid.NewGuid().ToString("N"), Path.Combine(dir, "x.exe"), victim, DateTime.Now, "forged");
            Assert.Equal(QuarantineRestoreStatus.Failed, QuarantineActions.Restore(nested, renameIfExists: true).Status);
            Assert.False(QuarantineActions.Delete(nested).Ok);
            Assert.True(File.Exists(victim));
            Assert.False(File.Exists(nested.OriginalPath));

            var index = new QuarantineItem(Guid.NewGuid().ToString("N"), Path.Combine(dir, "y.exe"),
                Path.Combine(AppPaths.QuarantineDir, "index.json"), DateTime.Now, "forged");
            Assert.False(QuarantineActions.IsOwnStoredFile(index));
            Assert.False(QuarantineActions.Delete(index).Ok);
        }
        finally
        {
            Directory.Delete(sub, recursive: true);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Restore_WithRelativeOriginalPath_IsRefusedAndKeepsFile()
    {
        var dir = TestEnvironment.NewTempDir("quarantine");
        var original = Path.Combine(dir, "tool.exe");
        File.WriteAllText(original, "content");
        var item = Quarantine.Add(original, "Тест");
        try
        {
            // Without the check the file would land relative to the current directory (".\restored-…\tool.exe").
            var relativeDir = "restored-" + Guid.NewGuid().ToString("N")[..8];
            var damaged = item with { OriginalPath = Path.Combine(relativeDir, "tool.exe") };
            var result = QuarantineActions.Restore(damaged, renameIfExists: true);
            Assert.Equal(QuarantineRestoreStatus.Failed, result.Status);
            Assert.True(File.Exists(item.StoredPath));
            Assert.False(Directory.Exists(Path.Combine(Environment.CurrentDirectory, relativeDir)));
        }
        finally
        {
            Cleanup(item, dir);
        }
    }

    [Fact]
    public void LoadRows_IgnoresNullEntriesInDamagedIndex()
    {
        var index = Path.Combine(AppPaths.QuarantineDir, "index.json");
        Directory.CreateDirectory(AppPaths.QuarantineDir);
        var before = File.Exists(index) ? File.ReadAllText(index) : null;
        try
        {
            File.WriteAllText(index, "[null]");
            Assert.Empty(QuarantineActions.LoadRows());
        }
        finally
        {
            if (before is null)
                File.Delete(index);
            else
                File.WriteAllText(index, before);
        }
    }

    [Fact]
    public void LoadRows_DescribesItems()
    {
        var dir = TestEnvironment.NewTempDir("quarantine");
        var original = Path.Combine(dir, "invoice.pdf.exe");
        File.WriteAllText(original, new string('x', 2048));
        var item = Quarantine.Add(original, "Двойное расширение");
        try
        {
            var row = Assert.Single(QuarantineActions.LoadRows(), r => r.Item.Id == item.Id);
            Assert.Equal("invoice.pdf.exe", row.FileName);
            Assert.Equal(dir, row.Folder);
            Assert.True(row.StoredExists);
            Assert.Equal(2048, row.SizeBytes);
            Assert.Equal("2 КБ", row.SizeText);
            Assert.Equal("Двойное расширение", row.Reason);
        }
        finally
        {
            Cleanup(item, dir);
        }
    }

    private static void Cleanup(QuarantineItem item, string dir)
    {
        if (Quarantine.List().Any(i => i.Id == item.Id))
            Quarantine.Delete(item);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
