using System.IO;
using System.Security;
using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Startup;

// Run / RunOnce registry values and Startup folders.
public sealed partial class StartupService
{
    private sealed record RunSource(StartupScope Scope, RegistryHive Hive, string KeyPath, bool RunOnce, bool Is32Bit, string? ApprovedKey);

    private IEnumerable<RunSource> RunSources()
    {
        yield return new(StartupScope.CurrentUser, _env.UserHive, _env.UserRunKey, false, false, _env.UserApprovedRun);
        yield return new(StartupScope.CurrentUser, _env.UserHive, _env.UserRunOnceKey, true, false, null);
        yield return new(StartupScope.AllUsers, _env.MachineHive, _env.MachineRunKey, false, false, _env.MachineApprovedRun);
        yield return new(StartupScope.AllUsers, _env.MachineHive, _env.MachineRunOnceKey, true, false, null);
        yield return new(StartupScope.AllUsers, _env.MachineHive, _env.MachineRun32Key, false, true, _env.MachineApprovedRun32);
        yield return new(StartupScope.AllUsers, _env.MachineHive, _env.MachineRunOnce32Key, true, true, null);
    }

    /// <summary>Reads the Run/RunOnce values of HKCU, HKLM and HKLM\WOW6432Node (read-only).</summary>
    internal List<StartupItem> ScanRegistry(CancellationToken ct)
    {
        var list = new List<StartupItem>();
        foreach (var source in RunSources())
        {
            ct.ThrowIfCancellationRequested();
            var hiveName = StartupEnvironment.HiveName(source.Hive);
            var approved = ReadApproved(source.Hive, source.ApprovedKey);
            try
            {
                using var key = OpenKey(source.Hive, source.KeyPath, false);
                if (key is null)
                    continue;
                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValueKind(valueName) is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                        continue;
                    if (key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw ||
                        string.IsNullOrWhiteSpace(raw))
                        continue;

                    var command = raw.Trim();
                    approved.TryGetValue(valueName, out var flag);
                    var item = new StartupItem
                    {
                        Id = $"reg|{hiveName}|{source.KeyPath}|{valueName}",
                        Kind = source.RunOnce ? StartupKind.RegistryRunOnce : StartupKind.RegistryRun,
                        Scope = source.Scope,
                        Name = valueName,
                        Command = command,
                        TargetFile = StartupTargets.Resolve(command),
                        Is32Bit = source.Is32Bit,
                        LocationText = $@"{hiveName}\{source.KeyPath}",
                        Hive = source.Hive,
                        RegistryKeyPath = source.KeyPath,
                        RegistryValueName = valueName,
                        ApprovedHive = source.Hive,
                        ApprovedKeyPath = source.ApprovedKey,
                        ApprovedValueName = source.ApprovedKey is null ? null : valueName,
                    };
                    item.IsEnabled = source.RunOnce || StartupApproved.IsEnabled(flag);
                    item.DisabledTime = item.IsEnabled ? null : StartupApproved.DisabledAt(flag);
                    list.Add(item);
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                Log.Warn($"Автозагрузка: нет доступа к {hiveName}\\{source.KeyPath}: {ex.Message}");
            }
        }
        return list;
    }

    /// <summary>Reads the per-user and common Startup folders (read-only).</summary>
    internal List<StartupItem> ScanFolders(CancellationToken ct)
    {
        var list = new List<StartupItem>();
        var folders = new[]
        {
            (Folder: _env.UserStartupFolder, Scope: StartupScope.CurrentUser, Hive: _env.UserHive, Approved: _env.UserApprovedFolder),
            (Folder: _env.CommonStartupFolder, Scope: StartupScope.AllUsers, Hive: _env.MachineHive, Approved: _env.MachineApprovedFolder),
        };
        foreach (var (folder, scope, hive, approvedKey) in folders)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                continue;
            string[] files;
            try
            {
                files = Directory.GetFiles(folder);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
            {
                Log.Warn($"Автозагрузка: нет доступа к папке {folder}: {ex.Message}");
                continue;
            }

            var approved = ReadApproved(hive, approvedKey);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;

                var (command, target) = DescribeFolderFile(file);
                approved.TryGetValue(fileName, out var flag);
                var item = new StartupItem
                {
                    Id = $"lnk|{file}",
                    Kind = StartupKind.StartupFolder,
                    Scope = scope,
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = command,
                    TargetFile = target,
                    LocationText = folder,
                    FilePath = file,
                    ApprovedHive = hive,
                    ApprovedKeyPath = approvedKey,
                    ApprovedValueName = fileName,
                };
                item.IsEnabled = StartupApproved.IsEnabled(flag);
                item.DisabledTime = item.IsEnabled ? null : StartupApproved.DisabledAt(flag);
                list.Add(item);
            }
        }
        return list;
    }

    /// <summary>Command line and target of a file inside a Startup folder (shortcuts are resolved).</summary>
    internal static (string Command, string? Target) DescribeFolderFile(string file)
    {
        if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return (Quote(file), file);

        var shortcut = StartupNative.ReadShortcut(file);
        if (shortcut is not { } s || string.IsNullOrWhiteSpace(s.Target))
            return (file, null); // advertised (MSI) shortcut or unreadable: target unknown, never "broken"

        var target = System.Environment.ExpandEnvironmentVariables(s.Target.Trim());
        var command = string.IsNullOrWhiteSpace(s.Arguments) ? Quote(target) : $"{Quote(target)} {s.Arguments.Trim()}";
        // "rundll32.exe some.dll,Entry" -> the dll is what matters.
        return (command, StartupTargets.Resolve(command) ?? target);
    }

    // ---------- changes ----------

    /// <summary>Writes the Task-Manager-compatible StartupApproved flag (Run and Startup-folder entries).</summary>
    private async Task<StartupActionResult> SetApprovedAsync(StartupItem item, bool enable, CancellationToken ct)
    {
        if (item.ApprovedKeyPath is null || item.ApprovedValueName is null)
            return StartupActionResult.Fail("Для этой записи Windows не поддерживает отключение.");
        if (item.Scope == StartupScope.AllUsers && !_env.IsElevated())
            return StartupActionResult.Admin("Записи для всех пользователей можно менять только с правами администратора.");

        var fullKey = $@"{StartupEnvironment.HiveName(item.ApprovedHive)}\{item.ApprovedKeyPath}";
        string? backup = null;
        if (KeyExists(item.ApprovedHive, item.ApprovedKeyPath))
        {
            backup = await _env.BackupKey(fullKey, $"Автозагрузка: перед {(enable ? "включением" : "отключением")} «{item.DisplayName}»", ct);
            if (backup is null)
                return StartupActionResult.Fail("Не удалось сохранить резервную копию реестра — изменение отменено.");
        }

        var nowUtc = DateTime.UtcNow;
        await Task.Run(() =>
        {
            using var root = RegistryKey.OpenBaseKey(item.ApprovedHive, RegistryView.Registry64);
            using var key = root.CreateSubKey(item.ApprovedKeyPath, writable: true)
                            ?? throw new IOException($"Не удалось открыть {fullKey}");
            key.SetValue(item.ApprovedValueName, enable ? StartupApproved.CreateEnabled() : StartupApproved.CreateDisabled(nowUtc),
                RegistryValueKind.Binary);
        }, ct);

        item.IsEnabled = enable;
        item.DisabledTime = enable ? null : nowUtc.ToLocalTime();
        Log.Action($"Автозагрузка: {(enable ? "включена" : "отключена")} запись «{item.DisplayName}» ({item.SourceText}, {item.ScopeText.ToLowerInvariant()}). " +
                   $"Флаг записан в {fullKey}.");
        return StartupActionResult.Ok(enable
            ? $"«{item.DisplayName}» снова будет запускаться вместе с Windows."
            : $"«{item.DisplayName}» больше не будет запускаться вместе с Windows. Включить обратно можно в любой момент.", backup);
    }

    private async Task<StartupActionResult> DeleteRegistryValueAsync(StartupItem item, CancellationToken ct)
    {
        if (item.RegistryKeyPath is null)
            return StartupActionResult.Fail("Неизвестное расположение записи.");
        if (item.Scope == StartupScope.AllUsers && !_env.IsElevated())
            return StartupActionResult.Admin("Записи для всех пользователей можно удалять только с правами администратора.");

        var valueName = item.RegistryValueName ?? "";
        var fullKey = $@"{StartupEnvironment.HiveName(item.Hive)}\{item.RegistryKeyPath}";
        var exists = await Task.Run(() =>
        {
            using var key = OpenKey(item.Hive, item.RegistryKeyPath, false);
            return key?.GetValue(valueName) is not null;
        }, ct);
        if (!exists)
            return StartupActionResult.Ok($"Записи «{item.DisplayName}» уже нет в реестре.");

        var backup = await _env.BackupKey(fullKey, $"Автозагрузка: перед удалением «{item.DisplayName}»", ct);
        if (backup is null)
            return StartupActionResult.Fail("Не удалось сохранить резервную копию раздела реестра — удаление отменено.");

        await Task.Run(() =>
        {
            using var key = OpenKey(item.Hive, item.RegistryKeyPath, true)
                            ?? throw new IOException($"Раздел {fullKey} не найден");
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }, ct);

        Log.Action($"Автозагрузка: удалена запись «{item.DisplayName}» из {fullKey} (команда: {item.Command}). " +
                   $"Резервная копия: {Path.GetFileName(backup)}");
        return StartupActionResult.Ok(
            $"Запись «{item.DisplayName}» удалена. Резервная копия: {backup} — чтобы вернуть запись, дважды щёлкните этот файл.", backup);
    }

    private async Task<StartupActionResult> DeleteFolderItemAsync(StartupItem item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.FilePath))
            return StartupActionResult.Fail("Неизвестное расположение ярлыка.");
        if (item.Scope == StartupScope.AllUsers && !_env.IsElevated())
            return StartupActionResult.Admin("Общую папку автозагрузки можно менять только с правами администратора.");
        if (!File.Exists(item.FilePath))
            return StartupActionResult.Ok($"Ярлыка «{item.DisplayName}» уже нет в папке автозагрузки.");

        var ok = await Task.Run(() => _env.RecycleFile(item.FilePath), ct);
        if (!ok)
            return StartupActionResult.Fail("Не удалось переместить файл в Корзину (возможно, он открыт другой программой).");

        Log.Action($"Автозагрузка: «{item.DisplayName}» убран из папки автозагрузки ({item.FilePath}) — файл перемещён в Корзину.");
        return StartupActionResult.Ok($"«{item.DisplayName}» убран из автозагрузки. Ярлык лежит в Корзине — его можно восстановить оттуда.");
    }

    private static bool KeyExists(RegistryHive hive, string path)
    {
        try
        {
            using var key = OpenKey(hive, path, false);
            return key is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return true; // let the backup attempt decide
        }
    }
}
