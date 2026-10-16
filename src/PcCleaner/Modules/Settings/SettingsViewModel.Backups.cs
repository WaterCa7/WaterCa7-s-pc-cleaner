using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Settings;

// Tab "Резервные копии": registry exports (.reg) plus hosts/task backups stored by other modules.
public sealed partial class SettingsViewModel
{
    private ObservableCollection<BackupEntry> _backups = new();
    private BackupEntry? _selectedBackup;
    private string _backupsSummary = "";

    public ObservableCollection<BackupEntry> Backups
    {
        get => _backups;
        private set
        {
            if (Set(ref _backups, value))
                OnPropertyChanged(nameof(BackupsTileLabel));
        }
    }

    public BackupEntry? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (Set(ref _selectedBackup, value))
                RelayCommand.Refresh();
        }
    }

    public string BackupsSummary
    {
        get => _backupsSummary;
        private set => Set(ref _backupsSummary, value);
    }

    public string BackupsDir => AppPaths.BackupsDir;

    public AsyncRelayCommand RestoreBackupCommand { get; private set; } = null!;
    public RelayCommand ShowBackupCommand { get; private set; } = null!;
    public AsyncRelayCommand DeleteBackupCommand { get; private set; } = null!;
    public RelayCommand OpenBackupsFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshBackupsCommand { get; private set; } = null!;

    private void InitBackupCommands()
    {
        RestoreBackupCommand = new AsyncRelayCommand(p => RestoreBackupAsync(BackupFrom(p)),
            p => !IsBusy && BackupFrom(p)?.CanRestore == true);
        ShowBackupCommand = new RelayCommand(p =>
        {
            if (BackupFrom(p) is { } b)
                ProcessRunner.ShowInExplorer(b.FullPath);
        }, p => BackupFrom(p) is not null);
        DeleteBackupCommand = new AsyncRelayCommand(p => DeleteBackupAsync(BackupFrom(p)),
            p => !IsBusy && BackupFrom(p)?.CanDelete == true);
        OpenBackupsFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.BackupsDir));
        RefreshBackupsCommand = new AsyncRelayCommand(() => LoadBackupsAsync(CancellationToken.None));
    }

    private BackupEntry? BackupFrom(object? parameter) => parameter as BackupEntry ?? SelectedBackup;

    internal async Task LoadBackupsAsync(CancellationToken ct)
    {
        var selectedPath = SelectedBackup?.FullPath;
        var list = await Task.Run(BackupCatalog.List, ct);
        Backups = new ObservableCollection<BackupEntry>(list);
        SelectedBackup = list.FirstOrDefault(b => string.Equals(b.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));

        var registry = list.Count(b => b.Kind == BackupKind.Registry);
        var other = list.Count - registry;
        var size = list.Sum(b => b.SizeBytes);
        BackupsSummary = list.Count == 0
            ? "Резервных копий пока нет"
            : $"{Format.Count(registry, "копия реестра", "копии реестра", "копий реестра")}" +
              (other > 0 ? $" и {Format.Count(other, "другой файл", "других файла", "других файлов")}" : "") +
              $" · {Format.Bytes(size)}";
    }

    private async Task RestoreBackupAsync(BackupEntry? backup)
    {
        if (backup is null || !backup.CanRestore)
            return;
        if (!PathGuard.IsDirectChild(backup.FullPath, AppPaths.BackupsDir) || !File.Exists(backup.FullPath))
        {
            Dialogs.Info("Восстановление", "Файл резервной копии не найден. Обновите список.");
            await LoadBackupsAsync(CancellationToken.None);
            return;
        }

        // Decide from the file itself (not from the list, which may fall back to the file name) what the import will
        // write: the current state of exactly that key is exported first, so the restore can be undone.
        var reg = await Task.Run(() => BackupCatalog.InspectRegFile(backup.FullPath));
        if (RestoreRefusal(reg) is { } refusal)
        {
            Log.Warn($"Восстановление из {backup.FileName} отменено: {refusal}");
            Dialogs.Info("Восстановление из резервной копии", refusal + "\n\nРеестр не изменён.");
            return;
        }

        var key = reg.RootKey!;
        var title = BackupCatalog.FriendlyKeyName(key);
        var needsAdmin = BackupCatalog.NeedsAdmin(key);
        if (needsAdmin && !Admin.RequireElevation("Восстановить системный раздел реестра"))
            return;

        var exists = await Task.Run(() => KeyExists(key));
        var message =
            $"Восстановить раздел реестра из резервной копии от {backup.TimeText}?\n\n" +
            $"{title}\n{key}\n\n" +
            (backup.HasDescription ? $"Копия сделана перед действием: {backup.Description}\n\n" : "") +
            "Что произойдёт:\n" +
            "• значения, сохранённые в копии, будут заново записаны в реестр (то, что было изменено или удалено, вернётся);\n" +
            "• значения, появившиеся позже, останутся как есть;\n" +
            (exists
                ? "• перед этим текущее состояние раздела сохранится в новую резервную копию — так восстановление можно отменить."
                : "• сейчас этого раздела в реестре нет — он будет создан заново из копии.");
        if (!Dialogs.Confirm("Восстановление из резервной копии", message))
            return;

        string? resultMessage = null;
        await RunBusyAsync("Восстановление раздела реестра…", async ct =>
        {
            // Back up the current state first (if the key exists at all — after a deletion it usually doesn't).
            if (exists)
            {
                var current = await RegistryBackup.ExportAsync(key, $"Перед восстановлением из копии {backup.FileName}", ct);
                if (current is null)
                {
                    resultMessage = "Не удалось сохранить текущее состояние раздела, поэтому восстановление отменено — " +
                                    "реестр не изменён. Подробности во вкладке «Журнал».";
                    return;
                }
            }

            var ok = await RegistryBackup.ImportAsync(backup.FullPath, ct);
            if (ok)
            {
                // RegistryBackup.ImportAsync has already logged the action with the file name; add the readable key.
                Log.Info($"Раздел реестра восстановлен из резервной копии от {backup.TimeText}: {key}");
                resultMessage = $"Готово: раздел «{title}» восстановлен из копии от {backup.TimeText}.\n\n" +
                                "Некоторые изменения вступят в силу после перезапуска программ или перезагрузки компьютера.";
            }
            else
            {
                resultMessage = "Windows не смогла импортировать резервную копию. Подробности — во вкладке «Журнал»." +
                                (needsAdmin && !Admin.IsElevated ? "\n\nДля системных разделов нужны права администратора." : "");
            }
        });

        await LoadBackupsAsync(CancellationToken.None);
        if (resultMessage is not null)
            Dialogs.Info("Восстановление из резервной копии", resultMessage);
    }

    /// <summary>
    /// Why a .reg file must not be imported from this page (null = it is a plain export of one key and can be restored).
    /// </summary>
    internal static string? RestoreRefusal(RegFileInfo reg)
    {
        if (reg.RootKey is null)
            return "Не удалось прочитать раздел реестра из файла копии — возможно, файл повреждён.";
        if (!reg.SingleRoot)
            return "Файл копии затрагивает сразу несколько разных разделов реестра — похоже, его изменили вручную. " +
                   "Программа восстанавливает только собственные копии одного раздела.";
        if (reg.DeletesKeys)
            return "Файл копии удаляет разделы реестра — похоже, его изменили вручную. " +
                   "Программа восстанавливает только собственные копии.";
        return null;
    }

    /// <summary>
    /// True if the key exists or cannot be checked (then the export decides and aborts the restore if it fails).
    /// </summary>
    private static bool KeyExists(string keyPath)
    {
        var idx = keyPath.IndexOf('\\');
        var hiveName = idx < 0 ? keyPath : keyPath[..idx];
        var sub = idx < 0 ? "" : keyPath[(idx + 1)..];
        RegistryHive? hive = hiveName.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            "HKCC" or "HKEY_CURRENT_CONFIG" => RegistryHive.CurrentConfig,
            _ => null,
        };
        if (hive is null || sub.Length == 0)
            return true;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
            using var key = root.OpenSubKey(sub, writable: false);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    private async Task DeleteBackupAsync(BackupEntry? backup)
    {
        if (backup is null || !backup.CanDelete)
            return;

        var users = BackupCatalog.ModulesReferencing(Settings, backup.FullPath)
            .Select(BackupCatalog.ModuleDisplayName)
            .Distinct()
            .ToList();
        var message =
            $"Удалить резервную копию от {backup.TimeText}?\n\n{backup.Title}\n{backup.KeyPath}\n\n" +
            "Будет удалён только файл копии в папке программы — реестр Windows не изменится. " +
            "Но после удаления вернуть этот раздел к сохранённому состоянию будет нельзя." +
            (users.Count > 0
                ? $"\n\nВнимание: эта копия нужна {(users.Count == 1 ? "разделу" : "разделам")} " +
                  $"{string.Join(", ", users.Select(u => $"«{u}»"))}, " +
                  "чтобы отменить сделанные там изменения. После удаления копии отменить их будет нельзя."
                : "");
        if (!Dialogs.Confirm("Удаление резервной копии", message))
            return;

        var (ok, result) = BackupCatalog.DeleteBackup(backup.FullPath, AppPaths.BackupsDir);
        await LoadBackupsAsync(CancellationToken.None);
        if (ok)
            Dialogs.Info("Удаление резервной копии", result);
        else
            Dialogs.Error("Удаление резервной копии", result);
    }
}
