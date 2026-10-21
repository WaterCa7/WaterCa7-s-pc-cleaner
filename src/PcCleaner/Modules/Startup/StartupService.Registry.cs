using System.IO;
using System.Security;
using Microsoft.Win32;
using PcCleaner.Core.Localization;
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
                        // HKLM entries run for every user: "%APPDATA%\x.exe" may be missing only in this profile.
                        TargetDependsOnUser = source.Scope == StartupScope.AllUsers && StartupTargets.HasUserVariable(command),
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
                Log.Warn(T.F("Автозагрузка: нет доступа к {0}\\{1}: {2}", hiveName, source.KeyPath, ErrorText.For(ex)), ex);
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
                Log.Warn(T.F("Автозагрузка: нет доступа к папке {0}: {1}", folder, ErrorText.For(ex)), ex);
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
            return StartupActionResult.Fail(T._("Для этой записи Windows не поддерживает отключение."));
        if (item.Scope == StartupScope.AllUsers && !_env.IsElevated())
            return StartupActionResult.Admin(T._("Записи для всех пользователей можно менять только с правами администратора."));

        var fullKey = $@"{StartupEnvironment.HiveName(item.ApprovedHive)}\{item.ApprovedKeyPath}";
        string? backup = null;
        if (KeyExists(item.ApprovedHive, item.ApprovedKeyPath))
        {
            backup = await _env.BackupKey(fullKey, enable
                ? T.F("Автозагрузка: перед включением {0}", T.Quote(item.DisplayName))
                : T.F("Автозагрузка: перед отключением {0}", T.Quote(item.DisplayName)), ct);
            if (backup is null)
                return StartupActionResult.Fail(T._("Не удалось сохранить резервную копию реестра — изменение отменено."));
        }

        var nowUtc = DateTime.UtcNow;
        await Task.Run(() =>
        {
            using var root = RegistryKey.OpenBaseKey(item.ApprovedHive, RegistryView.Registry64);
            using var key = root.CreateSubKey(item.ApprovedKeyPath, writable: true)
                            ?? throw new IOException(T.F("Не удалось открыть {0}", fullKey));
            key.SetValue(item.ApprovedValueName, enable ? StartupApproved.CreateEnabled() : StartupApproved.CreateDisabled(nowUtc),
                RegistryValueKind.Binary);
        }, ct);

        item.IsEnabled = enable;
        item.DisabledTime = enable ? null : nowUtc.ToLocalTime();
        Log.Action(enable
            ? T.F("Автозагрузка: включена запись {0} ({1}, {2}). Флаг записан в {3}.", T.Quote(item.DisplayName), item.SourceText, item.ScopeText.ToLowerInvariant(), fullKey)
            : T.F("Автозагрузка: отключена запись {0} ({1}, {2}). Флаг записан в {3}.", T.Quote(item.DisplayName), item.SourceText, item.ScopeText.ToLowerInvariant(), fullKey));
        return StartupActionResult.Ok(enable
            ? T.F("{0} снова будет запускаться вместе с Windows.", T.Quote(item.DisplayName))
            : T.F("{0} больше не будет запускаться вместе с Windows. Включить обратно можно в любой момент.", T.Quote(item.DisplayName)), backup);
    }

    /// <summary>Whether the Run value still exists, and its command as the scan reads it (null when it is no longer a string).</summary>
    private static (bool Exists, string? Command) ReadRunValue(StartupItem item)
    {
        using var key = OpenKey(item.Hive, item.RegistryKeyPath!, false);
        return ReadRunValue(key, item.RegistryValueName ?? "");
    }

    private static (bool Exists, string? Command) ReadRunValue(RegistryKey? key, string valueName)
    {
        if (key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value)
            return (false, null);
        return (true, value is string raw ? raw.Trim() : null);
    }

    private async Task<StartupActionResult> DeleteRegistryValueAsync(StartupItem item, bool onlyIfStillBroken, CancellationToken ct)
    {
        if (item.RegistryKeyPath is null)
            return StartupActionResult.Fail(T._("Неизвестное расположение записи."));
        if (item.Scope == StartupScope.AllUsers && !_env.IsElevated())
            return StartupActionResult.Admin(T._("Записи для всех пользователей можно удалять только с правами администратора."));

        var valueName = item.RegistryValueName ?? "";
        var fullKey = $@"{StartupEnvironment.HiveName(item.Hive)}\{item.RegistryKeyPath}";
        var (exists, command) = await Task.Run(() => ReadRunValue(item), ct);
        if (!exists)
            return StartupActionResult.Ok(T.F("Записи {0} уже нет в реестре.", T.Quote(item.DisplayName)));
        if (onlyIfStillBroken && WhyNoLongerBroken(item, command, StartupTargets.Resolve(command)) is { } reason)
            return StartupActionResult.Skip(reason);

        var backup = await _env.BackupKey(fullKey, T.F("Автозагрузка: перед удалением {0}", T.Quote(item.DisplayName)), ct);
        if (backup is null)
            return StartupActionResult.Fail(T._("Не удалось сохранить резервную копию раздела реестра — удаление отменено."));

        var deleted = await Task.Run(() =>
        {
            using var key = OpenKey(item.Hive, item.RegistryKeyPath, true)
                            ?? throw new IOException(T.F("Раздел {0} не найден", fullKey));
            // The value may have been rewritten while the backup was being exported.
            if (onlyIfStillBroken && ReadRunValue(key, valueName) is (true, var now) && !string.Equals(now, item.Command, StringComparison.Ordinal))
                return false;
            key.DeleteValue(valueName, throwOnMissingValue: false);
            return true;
        }, ct);
        if (!deleted)
            return StartupActionResult.Skip(ChangedSinceScan);

        Log.Action(T.F("Автозагрузка: удалена запись {0} из {1} (команда: {2}). Резервная копия: {3}",
            T.Quote(item.DisplayName), fullKey, item.Command, Path.GetFileName(backup)));
        return StartupActionResult.Ok(
            T.F("Запись {0} удалена. Резервная копия: {1} — чтобы вернуть запись, дважды щёлкните этот файл.", T.Quote(item.DisplayName), backup), backup);
    }

    private async Task<StartupActionResult> DeleteFolderItemAsync(StartupItem item, bool onlyIfStillBroken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.FilePath))
            return StartupActionResult.Fail(T._("Неизвестное расположение ярлыка."));
        if (item.Scope == StartupScope.AllUsers && !_env.IsElevated())
            return StartupActionResult.Admin(T._("Общую папку автозагрузки можно менять только с правами администратора."));
        if (!File.Exists(item.FilePath))
            return StartupActionResult.Ok(T.F("Ярлыка {0} уже нет в папке автозагрузки.", T.Quote(item.DisplayName)));
        if (onlyIfStillBroken)
        {
            // An updater may have re-pointed the same shortcut to the new version of the program.
            var (command, target) = await Task.Run(() => DescribeFolderFile(item.FilePath), ct);
            if (WhyNoLongerBroken(item, command, target) is { } reason)
                return StartupActionResult.Skip(reason);
        }

        var ok = await Task.Run(() => _env.RecycleFile(item.FilePath), ct);
        if (!ok)
            return StartupActionResult.Fail(T._("Не удалось переместить файл в Корзину (возможно, он открыт другой программой)."));

        Log.Action(T.F("Автозагрузка: {0} убран из папки автозагрузки ({1}) — файл перемещён в Корзину.", T.Quote(item.DisplayName), item.FilePath));
        return StartupActionResult.Ok(T.F("{0} убран из автозагрузки. Ярлык лежит в Корзине — его можно восстановить оттуда.", T.Quote(item.DisplayName)));
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
