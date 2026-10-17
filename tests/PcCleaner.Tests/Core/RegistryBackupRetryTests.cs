using System.IO;
using System.Text;
using Microsoft.Win32;
using PcCleaner.Core;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// Under load reg.exe now and then fails to write its file ("Не удается выполнить запись в файл"). A backup that fails cancels
/// the change it protects, so <see cref="RegistryBackup.ExportAsync(string, string?, CancellationToken)"/> retries a failed export —
/// but still returns null (no backup → no change) when every attempt fails.
/// The key lives under HKCU\Software\WaterCa7sPcCleanerTests\Backup\&lt;guid&gt; and is removed afterwards.
/// </summary>
public class RegistryBackupRetryTests : IDisposable
{
    private const string TestsRoot = @"Software\WaterCa7sPcCleanerTests";
    private static readonly ProcessResult WriteError = new(1, "", "Ошибка: Не удается выполнить запись в файл.", TimedOut: false);
    private readonly string _id = Guid.NewGuid().ToString("N")[..12];

    public RegistryBackupRetryTests()
    {
        TestEnvironment.Init();
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue("App", @"""C:\Program Files\App\app.exe"" /tray");
    }

    private string KeyPath => $@"{TestsRoot}\Backup\{_id}";

    private string FullKey => $@"HKCU\{KeyPath}";

    private static ProcessResult WriteFile(string file)
    {
        File.WriteAllText(file, "Windows Registry Editor Version 5.00\r\n", Encoding.Unicode);
        return new ProcessResult(0, "Операция успешно завершена.", "", TimedOut: false);
    }

    [Fact]
    public async Task ExportThatFailsOnce_IsRetried()
    {
        var calls = 0;
        var file = await RegistryBackup.ExportAsync(FullKey, "test",
            (path, _) => Task.FromResult(++calls == 1 ? WriteError : WriteFile(path)), CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.NotNull(file);
        Assert.True(File.Exists(file));
        Assert.Equal("test", File.ReadAllText(file + ".txt"));
    }

    [Fact]
    public async Task ExportThatAlwaysFails_GivesNoBackup()
    {
        var calls = 0;
        var file = await RegistryBackup.ExportAsync(FullKey, "test",
            (_, _) => { calls++; return Task.FromResult(WriteError); }, CancellationToken.None);

        Assert.Null(file);
        Assert.Equal(RegistryBackup.ExportAttempts, calls);
        Assert.Empty(OwnBackups());
    }

    [Fact]
    public async Task ExportThatReportsSuccessButWritesNothing_IsRetried()
    {
        var calls = 0;
        var file = await RegistryBackup.ExportAsync(FullKey, "test",
            (path, _) => Task.FromResult(++calls == 1 ? new ProcessResult(0, "", "", TimedOut: false) : WriteFile(path)),
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task TimedOutExport_IsNotRetried()
    {
        var calls = 0;
        var file = await RegistryBackup.ExportAsync(FullKey, "test",
            (_, _) => { calls++; return Task.FromResult(new ProcessResult(-1, "", "", TimedOut: true)); }, CancellationToken.None);

        Assert.Null(file);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RealExport_StillWorks()
    {
        var file = await RegistryBackup.ExportAsync(FullKey, "test");

        Assert.NotNull(file);
        Assert.Contains(_id, File.ReadAllText(file!));
    }

    private IEnumerable<FileInfo> OwnBackups()
    {
        var backups = new DirectoryInfo(AppPaths.BackupsDir);
        return backups.Exists ? backups.EnumerateFiles($"*{_id}*").ToList() : Enumerable.Empty<FileInfo>();
    }

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // best effort
        }
        // Remove the shared parents only when nothing else lives there (other tests may use them).
        foreach (var parent in new[] { TestsRoot + @"\Backup", TestsRoot })
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(parent))
                {
                    if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0)
                        break;
                }
                Registry.CurrentUser.DeleteSubKey(parent, throwOnMissingSubKey: false);
            }
            catch (Exception)
            {
                break;
            }
        }
        foreach (var file in OwnBackups())
        {
            try
            {
                file.Delete();
            }
            catch (Exception)
            {
                // best effort
            }
        }
    }
}
