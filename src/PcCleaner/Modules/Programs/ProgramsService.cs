using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Programs;

public sealed record ProgramScanResult(IReadOnlyList<InstalledProgram> Programs, int UnreadableEntries);

public sealed record StoreAppsResult(IReadOnlyList<StoreApp> Apps, string? Error);

public enum UninstallStatus
{
    /// <summary>The uninstaller ran and the program is no longer in the list.</summary>
    Removed,
    /// <summary>The uninstaller finished, but the entry is still there (cancelled, or the removal continues elsewhere).</summary>
    StillInstalled,
    /// <summary>The user declined the UAC prompt.</summary>
    UacDeclined,
    /// <summary>Nothing was started (no uninstaller, file missing, start error).</summary>
    NotStarted,
}

public sealed record UninstallOutcome(UninstallStatus Status, string Message, int? ExitCode = null);

public sealed record EntryDeletionResult(bool Success, string Message, string? BackupFile = null);

/// <summary>
/// Installed programs (Uninstall registry keys) and Microsoft Store apps: enumeration, flags, uninstall,
/// removal of broken entries (with .reg backups) and the dashboard analysis.
/// </summary>
public sealed class ProgramsService : IHealthContributor
{
    private const string AppxScript =
        "$ErrorActionPreference='Stop';" +
        "$p = @(Get-AppxPackage | Where-Object { -not $_.IsFramework -and $_.SignatureKind -ne 'System' -and -not $_.NonRemovable } | " +
        "Select-Object Name, PackageFullName, PackageFamilyName, Publisher, @{n='Version';e={[string]$_.Version}}, InstallLocation, " +
        "@{n='SignatureKind';e={[string]$_.SignatureKind}});" +
        "ConvertTo-Json -InputObject $p -Depth 3 -Compress";

    private readonly IReadOnlyList<UninstallSource> _sources;
    private readonly Func<CancellationToken, Task<IReadOnlyList<StoreAppRecord>>>? _storeAppsProvider;
    private readonly Func<DateTime> _clock;

    public ProgramsService()
        : this(UninstallSource.Defaults)
    {
    }

    /// <summary>Test constructor: custom registry roots (sandbox key), fake Store apps and clock.</summary>
    internal ProgramsService(
        IReadOnlyList<UninstallSource> sources,
        Func<CancellationToken, Task<IReadOnlyList<StoreAppRecord>>>? storeAppsProvider = null,
        Func<DateTime>? clock = null)
    {
        _sources = sources;
        _storeAppsProvider = storeAppsProvider;
        _clock = clock ?? (() => DateTime.Now);
    }

    public string ModuleName => "Программы";

    /// <summary>Overridable for tests (sandbox keys live in HKCU and never need elevation).</summary>
    internal Func<bool> IsElevated { get; set; } = () => Admin.IsElevated;

    // ===================================================================== enumeration

    public Task<ProgramScanResult> ScanProgramsAsync(CancellationToken ct) => Task.Run(() => ScanPrograms(ct), ct);

    internal ProgramScanResult ScanPrograms(CancellationToken ct)
    {
        var now = _clock();
        var result = new List<InstalledProgram>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var unreadable = 0;

        foreach (var source in _sources)
        {
            ct.ThrowIfCancellationRequested();
            RegistryKey? root = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(source.Hive, source.View);
                root = baseKey.OpenSubKey(source.SubKeyPath);
                if (root is null)
                    continue;

                foreach (var name in root.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!seenKeys.Add($@"{source.HiveShortName}\{source.SubKeyPath}\{name}"))
                        continue; // the same key seen through another registry view

                    UninstallEntryValues? values;
                    try
                    {
                        values = ReadValues(root, name);
                    }
                    catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                    {
                        unreadable++;
                        continue;
                    }
                    if (values is null || !ProgramRules.ShouldInclude(values))
                        continue;

                    var program = CreateProgram(values, source, now);
                    var key = ProgramRules.DedupKey(program.DisplayName, program.Version);
                    if (byName.TryGetValue(key, out var index))
                    {
                        // Same program seen in two views: keep the healthier entry.
                        if (result[index].IsBroken && !program.IsBroken)
                            result[index] = program;
                        continue;
                    }
                    byName[key] = result.Count;
                    result.Add(program);
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                Log.Warn($"Программы: нет доступа к {source.HiveShortName}\\{source.SubKeyPath}: {ex.Message}");
            }
            finally
            {
                root?.Dispose();
            }
        }

        if (unreadable > 0)
            Log.Warn($"Программы: {Format.Count(unreadable, "запись", "записи", "записей")} в реестре не удалось прочитать (нет доступа)");
        return new ProgramScanResult(result, unreadable);
    }

    private static UninstallEntryValues? ReadValues(RegistryKey root, string name)
    {
        using var key = root.OpenSubKey(name);
        if (key is null)
            return null;

        string? S(string value) => key.GetValue(value) switch
        {
            string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
            string[] arr => arr.Length == 0 ? null : string.Join(" ", arr).Trim(),
            int i => i.ToString(),
            long l => l.ToString(),
            _ => null,
        };
        bool B(string value) => key.GetValue(value) switch
        {
            int i => i != 0,
            long l => l != 0,
            string s => s.Trim() == "1",
            _ => false,
        };
        long? size = key.GetValue("EstimatedSize") switch
        {
            int i => (uint)i,
            long l => l,
            string s when long.TryParse(s.Trim(), out var parsed) => parsed,
            _ => null,
        };

        var displayName = S("DisplayName");
        if (displayName is not null && displayName.StartsWith('@'))
            displayName = ProgramIcons.ResolveIndirect(displayName) ?? displayName;

        return new UninstallEntryValues
        {
            KeyName = name,
            DisplayName = displayName,
            Publisher = S("Publisher"),
            DisplayVersion = S("DisplayVersion"),
            InstallDate = S("InstallDate"),
            EstimatedSizeKb = size,
            InstallLocation = S("InstallLocation"),
            UninstallString = S("UninstallString"),
            QuietUninstallString = S("QuietUninstallString"),
            DisplayIcon = S("DisplayIcon"),
            WindowsInstaller = B("WindowsInstaller"),
            SystemComponent = B("SystemComponent"),
            ParentKeyName = S("ParentKeyName"),
            ReleaseType = S("ReleaseType"),
            BundleCachePath = S("BundleCachePath"),
        };
    }

    internal static InstalledProgram CreateProgram(UninstallEntryValues v, UninstallSource source, DateTime now)
    {
        var location = ProgramRules.NormalizeFolder(v.InstallLocation);
        var locationExists = false;
        DateTime? folderCreated = null;
        if (location is not null && !ProgramRules.IsNetworkPath(location))
        {
            try
            {
                locationExists = Directory.Exists(location);
                if (locationExists && !ProgramRules.IsTooBroadToMeasure(location))
                    folderCreated = Directory.GetCreationTime(location).Date;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                locationExists = false;
            }
        }

        var date = ProgramRules.ParseInstallDate(v.InstallDate);
        var dateFromFolder = false;
        if (date is null && folderCreated is not null)
        {
            date = folderCreated;
            dateFromFolder = true;
        }

        UninstallLaunch? launch = null, quietLaunch = null;
        string? mainExecutable = null;
        try
        {
            launch = ProgramRules.WithBundleFallback(
                ProgramRules.BuildLaunch(v.UninstallString, v.WindowsInstaller, v.KeyName), v.BundleCachePath, quiet: false);
            if (!string.IsNullOrWhiteSpace(v.QuietUninstallString))
                quietLaunch = ProgramRules.WithBundleFallback(
                    ProgramRules.BuildQuietLaunch(v.QuietUninstallString), v.BundleCachePath, quiet: true);
            mainExecutable = ProgramRules.FindMainExecutable(v.DisplayIcon, launch);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            // malformed command lines: the entry is shown, but without uninstall actions
        }

        var program = new InstalledProgram
        {
            DisplayName = v.DisplayName!.Trim(),
            Publisher = v.Publisher,
            Version = v.DisplayVersion,
            InstallDate = date,
            InstallDateFromFolder = dateFromFolder,
            EstimatedSizeBytes = ProgramRules.EstimatedSizeToBytes(v.EstimatedSizeKb),
            InstallLocation = location,
            InstallLocationExists = locationExists,
            UninstallString = v.UninstallString,
            QuietUninstallString = v.QuietUninstallString,
            DisplayIcon = v.DisplayIcon,
            IsWindowsInstaller = v.WindowsInstaller,
            ProductCode = launch?.ProductCode ?? (v.WindowsInstaller && ProgramRules.IsGuid(v.KeyName) ? v.KeyName.ToUpperInvariant() : null),
            KeyName = v.KeyName,
            Source = source,
            IsBroken = ProgramRules.IsBroken(v),
            IsRecent = ProgramRules.IsRecent(date, now),
            IsRuntime = ProgramRules.IsRuntime(v.DisplayName, v.Publisher),
            IsDriver = ProgramRules.IsDriverPackage(v.DisplayName),
            Launch = launch,
            QuietLaunch = quietLaunch,
            MainExecutable = mainExecutable,
        };
        ApplyTrust(program);
        return program;
    }

    /// <summary>Re-evaluates the PUP heuristics and trust flag (after the user trusts/untrusts a program).</summary>
    public static void ApplyTrust(InstalledProgram p)
    {
        p.IsTrusted = AppServices.Settings.IsTrusted(p.DisplayName);
        p.Suspicion = p.IsTrusted
            ? SuspicionResult.Clean
            : ProgramRules.AnalyzeProgram(p.DisplayName, p.Publisher, p.InstallLocation);
        p.RefreshBadges();
    }

    /// <summary>Adds or removes the program name in AppSettings.TrustedItems. Returns the new trust state.</summary>
    public static bool SetTrusted(string displayName, bool trusted)
    {
        var settings = AppServices.Settings;
        settings.TrustedItems.RemoveAll(t => string.Equals(t, displayName, StringComparison.OrdinalIgnoreCase));
        if (trusted)
            settings.TrustedItems.Add(displayName);
        settings.Save();
        Log.Info(trusted
            ? $"Программа «{displayName}» добавлена в доверенные"
            : $"Программа «{displayName}» убрана из доверенных");
        return trusted;
    }

    /// <summary>True if the program's registry key still exists.</summary>
    public bool EntryExists(InstalledProgram p)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(p.Source.Hive, p.Source.View);
            using var key = baseKey.OpenSubKey($@"{p.Source.SubKeyPath}\{p.KeyName}");
            return key is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return true; // cannot tell - assume it is still there
        }
    }

    // ===================================================================== Store apps

    public async Task<StoreAppsResult> ScanStoreAppsAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        IReadOnlyList<StoreAppRecord> records;
        string? error = null;
        if (_storeAppsProvider is not null)
        {
            records = await _storeAppsProvider(ct);
        }
        else
        {
            try
            {
                var result = await ProcessRunner.PowerShellAsync(AppxScript, timeout ?? TimeSpan.FromSeconds(60), ct);
                if (result.Success)
                {
                    records = StoreAppCatalog.ParseJson(result.StdOut);
                }
                else
                {
                    records = Array.Empty<StoreAppRecord>();
                    error = result.TimedOut
                        ? "Windows слишком долго отвечала на запрос списка приложений."
                        : "Windows не выдала список приложений Microsoft Store.";
                    Log.Warn($"Программы: Get-AppxPackage не сработал ({(result.TimedOut ? "тайм-аут" : "код " + result.ExitCode)}): {FirstLine(result.StdErr)}");
                }
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                records = Array.Empty<StoreAppRecord>();
                error = "Не удалось запустить PowerShell для получения списка приложений.";
                Log.Warn($"Программы: не удалось запустить PowerShell: {ex.Message}");
            }
        }

        var apps = await Task.Run(() => records
            .GroupBy(r => r.PackageFullName, StringComparer.OrdinalIgnoreCase)
            .Select(g => CreateStoreApp(g.First()))
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList(), ct);
        return new StoreAppsResult(apps, error);
    }

    internal static StoreApp CreateStoreApp(StoreAppRecord r)
    {
        var manifest = ProgramIcons.ReadManifest(r.InstallLocation);
        var friendly = StoreAppCatalog.FriendlyName(r.Name);
        string? manifestName = null, manifestPublisher = null;
        if (manifest is not null)
        {
            manifestName = friendly is null ? ProgramIcons.ResolvePackageString(r.PackageFullName, r.Name, manifest.DisplayName) : null;
            manifestPublisher = ProgramIcons.ResolvePackageString(r.PackageFullName, r.Name, manifest.PublisherDisplayName);
        }

        var name = friendly ?? manifestName ?? StoreAppCatalog.DeriveName(r.Name);
        if (r.Name.StartsWith("Microsoft.LanguageExperiencePack", StringComparison.OrdinalIgnoreCase) && friendly is null && manifestName is null)
            name = "Языковой пакет " + r.Name["Microsoft.LanguageExperiencePack".Length..];

        return new StoreApp
        {
            Name = r.Name,
            PackageFullName = r.PackageFullName,
            PackageFamilyName = r.PackageFamilyName,
            Publisher = r.Publisher,
            Version = r.Version,
            InstallLocation = r.InstallLocation,
            SignatureKind = r.SignatureKind,
            Kind = StoreAppCatalog.Classify(r.Name),
            Description = StoreAppCatalog.Describe(r.Name),
            ProtectReason = StoreAppCatalog.ProtectReason(r.Name),
            LogoPath = manifest?.LogoPath,
            DisplayName = name,
            PublisherDisplay = manifestPublisher ?? StoreAppCatalog.PublisherFromDistinguishedName(r.Publisher) ?? "Не указан",
        };
    }

    // ===================================================================== enrichment (icons, sizes)

    /// <summary>
    /// Loads icons and measures install folders of programs without EstimatedSize (read-only, background).
    /// Properties are updated on the UI thread. Stops when <paramref name="budget"/> is spent.
    /// </summary>
    public Task EnrichAsync(IReadOnlyList<InstalledProgram> programs, IReadOnlyList<StoreApp> apps, TimeSpan budget, CancellationToken ct,
        Action? sizeMeasured = null)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            foreach (var p in programs)
            {
                ct.ThrowIfCancellationRequested();
                var icon = ProgramIcons.FromDisplayIcon(p.DisplayIcon);
                if (icon is not null)
                    Ui.Post(() => p.Icon = icon);
            }
            foreach (var a in apps)
            {
                ct.ThrowIfCancellationRequested();
                var icon = ProgramIcons.FromLogo(a.LogoPath);
                if (icon is not null)
                    Ui.Post(() => a.Icon = icon);
            }

            // Install folders of all programs: a launcher folder (e.g. a game store) must not count the games inside it.
            var allLocations = programs
                .Where(p => p.InstallLocationExists && p.InstallLocation is not null)
                .Select(p => FullPathOrNull(p.InstallLocation!))
                .Where(l => l is not null)
                .Select(l => l!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var measured = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in programs.Where(p => !p.IsBroken && p.EstimatedSizeBytes == 0 && p.InstallLocationExists && p.InstallLocation is not null))
            {
                ct.ThrowIfCancellationRequested();
                if (sw.Elapsed > budget)
                    return;
                var location = FullPathOrNull(p.InstallLocation!);
                if (location is null || ProgramRules.IsTooBroadToMeasure(location))
                    continue;
                if (!measured.TryGetValue(location, out var size))
                {
                    var nested = allLocations.Where(l => Suspicion.IsUnder(l, location)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    measured[location] = size = FolderSize.Measure(location, ct, exclude: nested);
                }
                if (size is > 0)
                {
                    Ui.Post(() => p.ComputedSizeBytes = size.Value);
                    sizeMeasured?.Invoke();
                }
            }

            foreach (var a in apps.Where(a => !string.IsNullOrWhiteSpace(a.InstallLocation)))
            {
                ct.ThrowIfCancellationRequested();
                if (sw.Elapsed > budget)
                    return;
                var size = FolderSize.Measure(a.InstallLocation!, ct, 50_000);
                if (size is > 0)
                    Ui.Post(() => a.SizeBytes = size.Value);
            }
        }, ct);

    private static string? FullPathOrNull(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    // ===================================================================== uninstall

    /// <summary>
    /// Runs the program's own uninstaller (UAC may prompt) and waits for it and the processes it spawns.
    /// Cancellation only stops waiting; the uninstaller keeps running. Caller must have confirmed with the user.
    /// </summary>
    public async Task<UninstallOutcome> RunUninstallerAsync(InstalledProgram p, bool quiet, Action<string>? waitingFor, CancellationToken ct)
    {
        var launch = quiet ? p.QuietLaunch : p.Launch;
        if (launch is null)
            return new UninstallOutcome(UninstallStatus.NotStarted, "У программы не указан деинсталлятор.");
        if (!launch.IsMsi && Path.IsPathRooted(launch.FileName) && !File.Exists(launch.FileName))
            return new UninstallOutcome(UninstallStatus.NotStarted,
                $"Файл деинсталлятора не найден:\n{launch.FileName}\n\nПопробуйте удалить программу через «Параметры» → «Приложения». " +
                "Если не получится, установите программу заново поверх старой и затем удалите.");

        var workingDir = launch.WorkingDirectory is not null && Directory.Exists(launch.WorkingDirectory) ? launch.WorkingDirectory : null;
        var psi = new ProcessStartInfo(launch.FileName, launch.Arguments)
        {
            UseShellExecute = true, // lets Windows show the UAC prompt if the uninstaller needs it
            WorkingDirectory = workingDir ?? "",
        };

        var launchTime = DateTime.Now;
        Process? process;
        try
        {
            // ShellExecute waits for the UAC prompt: keep the window responsive meanwhile.
            process = await Task.Run(() => Process.Start(psi));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info($"Удаление «{p.DisplayName}»: запуск отклонён в окне контроля учётных записей");
            return new UninstallOutcome(UninstallStatus.UacDeclined, "Запуск деинсталлятора отменён в окне контроля учётных записей.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"Не удалось запустить деинсталлятор «{p.DisplayName}»: {ex.Message}");
            return new UninstallOutcome(UninstallStatus.NotStarted, $"Не удалось запустить деинсталлятор: {ex.Message}");
        }

        Log.Action($"Запущен деинсталлятор «{p.DisplayName}»{(quiet ? " (тихое удаление)" : "")}: {launch.DisplayText}");

        int? exitCode = null;
        if (process is not null)
        {
            using (process)
            {
                await ProcessTree.WaitForTreeAsync(process, launchTime, waitingFor, ct);
                try
                {
                    exitCode = process.ExitCode;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    exitCode = null;
                }
            }
        }

        // Some uninstallers remove their registry entry a moment after the window closes.
        for (var i = 0; i < 12 && EntryExists(p); i++)
            await Task.Delay(500, ct);

        if (!EntryExists(p))
        {
            Log.Action($"Программа «{p.DisplayName}» удалена");
            return new UninstallOutcome(UninstallStatus.Removed, $"Программа «{p.DisplayName}» удалена.", exitCode);
        }
        Log.Info($"Удаление «{p.DisplayName}»: деинсталлятор завершился (код {exitCode?.ToString() ?? "—"}), но программа осталась в списке");
        return new UninstallOutcome(UninstallStatus.StillInstalled,
            $"Деинсталлятор завершил работу, но «{p.DisplayName}» всё ещё в списке. Возможно, удаление отменили " +
            "или оно продолжается в отдельном окне (например, в Steam). Когда закончите, нажмите «Обновить».", exitCode);
    }

    // ===================================================================== broken entries

    /// <summary>Path separators would leave the Uninstall root; a quote would break the reg.exe backup command line.</summary>
    private static readonly char[] ForbiddenKeyNameChars = { '\\', '/', '"' };

    /// <summary>True if the entry belongs to one of the Uninstall roots this service reads (never delete anything else).</summary>
    internal bool IsAllowedKey(InstalledProgram p)
        => _sources.Contains(p.Source) &&
           !string.IsNullOrWhiteSpace(p.KeyName) &&
           p.KeyName.IndexOfAny(ForbiddenKeyNameChars) < 0 &&
           p.Source.SubKeyPath.EndsWith(@"\Uninstall", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes the registry entry of a broken program after exporting a .reg backup (aborts if the backup fails).
    /// HKLM entries need administrator rights. Caller must have confirmed with the user.
    /// </summary>
    public async Task<EntryDeletionResult> DeleteEntryAsync(InstalledProgram p, CancellationToken ct)
    {
        if (!IsAllowedKey(p))
            return new EntryDeletionResult(false, "Запись находится вне списка установленных программ — удаление запрещено.");
        if (p.IsMachineWide && !IsElevated())
            return new EntryDeletionResult(false, "Нужны права администратора: запись относится ко всем пользователям.");

        // Re-check right before deleting: the program may have been reinstalled (or repaired) since the list was read.
        var check = await Task.Run(() => RecheckBrokenEntry(p), ct);
        if (check is not null)
            return new EntryDeletionResult(false, check);

        var backup = await RegistryBackup.ExportAsync(p.RegistryKeyPath, $"Запись «{p.DisplayName}» из списка установленных программ", ct);
        if (backup is null)
            return new EntryDeletionResult(false, "Не удалось сохранить резервную копию — запись не удалена.");

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(p.Source.Hive, p.Source.View);
            using var parent = baseKey.OpenSubKey(p.Source.SubKeyPath, writable: true);
            if (parent is null)
                return new EntryDeletionResult(false, "Раздел реестра со списком программ не найден.");
            parent.DeleteSubKeyTree(p.KeyName, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Не удалось удалить запись «{p.DisplayName}» ({p.RegistryKeyPath}): {ex.Message}");
            return new EntryDeletionResult(false, $"Нет доступа к записи: {ex.Message}", backup);
        }

        Log.Action($"Удалена запись «{p.DisplayName}» из списка установленных программ ({p.RegistryKeyPath}). " +
                   $"Резервная копия: {Path.GetFileName(backup)}");
        return new EntryDeletionResult(true, "Запись удалена.", backup);
    }

    /// <summary>
    /// Reads the entry again from the registry. Returns null if it still exists and is still broken,
    /// otherwise the reason why it must not be deleted.
    /// </summary>
    private static string? RecheckBrokenEntry(InstalledProgram p)
    {
        UninstallEntryValues? current;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(p.Source.Hive, p.Source.View);
            using var parent = baseKey.OpenSubKey(p.Source.SubKeyPath);
            current = parent is null ? null : ReadValues(parent, p.KeyName);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return $"Не удалось прочитать запись: {ex.Message}";
        }
        if (current is null)
            return "Этой записи уже нет в реестре — ничего не изменено.";
        if (!string.Equals(current.DisplayName?.Trim(), p.DisplayName, StringComparison.OrdinalIgnoreCase) || !ProgramRules.IsBroken(current))
            return "Запись изменилась: похоже, программу установили заново. Ничего не удалено — обновите список.";
        return null;
    }

    /// <summary>
    /// Deletes several broken entries (each with its own backup). Returns (deleted, failed messages).
    /// Cancellation stops after the current entry and still reports what was done.
    /// </summary>
    public async Task<(int Deleted, IReadOnlyList<string> Failures)> DeleteEntriesAsync(
        IReadOnlyList<InstalledProgram> entries, IProgress<double>? progress, CancellationToken ct)
    {
        var deleted = 0;
        var failures = new List<string>();
        for (var i = 0; i < entries.Count && !ct.IsCancellationRequested; i++)
        {
            EntryDeletionResult r;
            try
            {
                r = await DeleteEntryAsync(entries[i], ct);
            }
            catch (OperationCanceledException)
            {
                break; // the backup export was interrupted: this entry was not deleted
            }
            if (r.Success)
                deleted++;
            else
                failures.Add($"{entries[i].DisplayName}: {r.Message}");
            progress?.Report(100.0 * (i + 1) / entries.Count);
        }
        return (deleted, failures);
    }

    // ===================================================================== Store app removal

    /// <summary>Removes a Store app for the current user (Remove-AppxPackage). Caller must have confirmed with the user.</summary>
    public async Task<(bool Success, string Message)> RemoveStoreAppAsync(StoreApp app, CancellationToken ct)
    {
        if (app.IsProtected)
            return (false, "Это приложение нужно Windows или другим программам — удалять его нельзя.");
        if (!StoreAppCatalog.IsValidPackageFullName(app.PackageFullName))
            return (false, "Некорректное имя пакета.");

        ProcessResult result;
        try
        {
            result = await ProcessRunner.PowerShellAsync(
                $"Remove-AppxPackage -Package '{app.PackageFullName}' -ErrorAction Stop; 'OK'", TimeSpan.FromMinutes(3), ct);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            Log.Warn($"Не удалось запустить PowerShell для удаления «{app.DisplayName}»: {ex.Message}");
            return (false, ex.Message);
        }

        if (result.Success && result.StdOut.Contains("OK", StringComparison.Ordinal))
        {
            Log.Action($"Удалено приложение Microsoft Store «{app.DisplayName}» ({app.PackageFullName}) для текущего пользователя");
            return (true, "Удалено.");
        }
        var reason = result.TimedOut ? "Windows не ответила за 3 минуты." : FirstLine(result.StdErr);
        Log.Warn($"Не удалось удалить приложение «{app.DisplayName}»: {reason}");
        return (false, reason);
    }

    // ===================================================================== dashboard analysis

    public async Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
    {
        var issues = new List<HealthIssue>();
        var scan = await ScanProgramsAsync(ct);

        foreach (var pup in scan.Programs.Where(p => p.IsPup).OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            issues.Add(new HealthIssue
            {
                Module = ModuleName,
                Title = $"Нежелательная программа: {pup.DisplayName}",
                Details = $"{pup.PupReason}. Если вы не устанавливали её сознательно, удалите её в разделе «Программы» " +
                          "(откроется её собственный деинсталлятор).",
                Severity = IssueSeverity.High,
                Gain = pup.SizeBytes > 0 ? $"Освободит ≈ {Format.Bytes(pup.SizeBytes)}" : null,
                ReclaimableBytes = 0, // removal needs the user's review in the program's own uninstaller
                TargetPageKey = "Programs",
            });
        }

        var broken = scan.Programs.Where(p => p.IsBroken).ToList();
        if (broken.Count > 0)
        {
            var names = string.Join(", ", broken.Take(5).Select(b => $"«{b.DisplayName}»")) + (broken.Count > 5 ? " и другие" : "");
            var needsAdmin = broken.Any(b => b.IsMachineWide);
            issues.Add(new HealthIssue
            {
                Module = ModuleName,
                Title = $"{Format.Count(broken.Count, "битая запись", "битые записи", "битых записей")} в списке программ",
                Details = $"Эти программы уже удалены, но всё ещё видны в списке установленных: {names}. " +
                          "Записи можно убрать — перед удалением сохраняется резервная копия реестра." +
                          (needsAdmin ? " Нужны права администратора." : ""),
                Severity = IssueSeverity.Low,
                Gain = "Чистый список программ",
                TargetPageKey = "Programs",
                AutoFixSafe = false,
                FixAsync = c => FixBrokenEntriesAsync(broken, c),
            });
        }

        try
        {
            using var storeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            storeCts.CancelAfter(TimeSpan.FromSeconds(15));
            var store = await ScanStoreAppsAsync(storeCts.Token, TimeSpan.FromSeconds(14));
            var bloat = store.Apps.Where(a => a.IsBloatware).ToList();
            if (bloat.Count > 0)
            {
                var examples = string.Join(", ", bloat.Take(4).Select(a => a.DisplayName)) + (bloat.Count > 4 ? " и другие" : "");
                issues.Add(new HealthIssue
                {
                    Module = ModuleName,
                    Title = $"{Format.Count(bloat.Count, "предустановленное приложение", "предустановленных приложения", "предустановленных приложений")} можно удалить",
                    Details = $"Например: {examples}. Они занимают место и иногда работают в фоне. Удалить ненужные можно на вкладке " +
                              "«Приложения Microsoft Store» — при необходимости их легко вернуть из Microsoft Store.",
                    Severity = IssueSeverity.Info,
                    TargetPageKey = "Programs",
                });
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warn("Программы: список приложений Microsoft Store не получен вовремя — пропущено");
        }

        return issues;
    }

    /// <summary>Fix for the dashboard (already confirmed there): deletes the broken entries with backups.</summary>
    private async Task<string> FixBrokenEntriesAsync(IReadOnlyList<InstalledProgram> broken, CancellationToken ct)
    {
        // Re-check: only entries that still exist and are still broken.
        var fresh = (await ScanProgramsAsync(ct)).Programs.Where(p => p.IsBroken).ToList();
        var targets = fresh.Where(f => broken.Any(b => string.Equals(b.RegistryKeyPath, f.RegistryKeyPath, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (targets.Count == 0)
            return "Битых записей больше нет.";
        if (targets.Any(t => t.IsMachineWide) && !IsElevated())
            return "Ничего не изменено: для удаления этих записей нужны права администратора. " +
                   "Перезапустите программу от имени администратора и повторите.";

        var (deleted, failures) = await DeleteEntriesAsync(targets, null, ct);
        if (deleted == 0)
            return "Ничего не удалено. " + (failures.Count > 0 ? failures[0] : "");
        var text = $"Удалено {Format.Count(deleted, "запись", "записи", "записей")}, резервные копии сохранены.";
        if (failures.Count > 0)
            text += $" Не удалось: {failures.Count}.";
        return text;
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith("+", StringComparison.Ordinal) && !l.StartsWith("At line", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(line) ? "неизвестная причина" : line;
    }
}
