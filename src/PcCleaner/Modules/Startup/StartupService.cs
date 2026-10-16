using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Startup;

/// <summary>Outcome of one change made to a startup entry.</summary>
public sealed record StartupActionResult(bool Success, string Message, bool NeedsAdmin = false, string? BackupPath = null)
{
    public static StartupActionResult Ok(string message, string? backupPath = null) => new(true, message, false, backupPath);

    public static StartupActionResult Fail(string message) => new(false, message);

    public static StartupActionResult Admin(string message) => new(false, message, true);
}

/// <summary>Outcome of a batch deletion.</summary>
public sealed record StartupBulkResult(IReadOnlyList<StartupItem> Done, IReadOnlyList<(StartupItem Item, string Error)> Failed)
{
    public bool NeedsAdmin { get; init; }

    public string Summary
    {
        get
        {
            var lines = new List<string>();
            lines.Add(Done.Count > 0
                ? $"Удалено из автозагрузки: {Format.Count(Done.Count, "запись", "записи", "записей")}."
                : "Ничего не удалено.");
            if (Done.Count > 0)
                lines.Add($"Резервные копии сохранены в папке {AppPaths.BackupsDir} (ярлыки — в Корзине).");
            if (Failed.Count > 0)
            {
                lines.Add("");
                lines.Add($"Не удалось удалить ({Failed.Count}):");
                lines.AddRange(Failed.Take(10).Select(f => $"• {f.Item.DisplayName} — {f.Error}"));
                if (Failed.Count > 10)
                    lines.Add($"• …и ещё {Failed.Count - 10}");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }
}

/// <summary>
/// Everything that starts with Windows: Run/RunOnce registry values, Startup folders, logon/boot scheduled tasks
/// and auto-start services. Reading is always safe; every change is reversible or backed up first
/// (StartupApproved flags like Task Manager, .reg exports, task XML exports, Recycle Bin).
/// </summary>
public sealed partial class StartupService : IHealthContributor
{
    internal const string ModuleKey = "Startup";

    private readonly StartupEnvironment _env;

    public StartupService() : this(StartupEnvironment.CreateDefault())
    {
    }

    internal StartupService(StartupEnvironment environment)
    {
        _env = environment;
    }

    public string ModuleName => "Автозагрузка";

    /// <summary>Raised when entries were changed outside the Startup page (e.g. a dashboard fix).</summary>
    public event Action? ChangedExternally;

    internal StartupEnvironment Env => _env;

    /// <summary>The settings whose trusted list this service checks (AppServices.Settings outside tests).</summary>
    internal AppSettings Settings => _env.Settings();

    // ===================== scanning =====================

    /// <summary>Full read-only enumeration (all sources + signatures + heuristics). Never changes the system.</summary>
    public Task<IReadOnlyList<StartupItem>> ScanAsync(Action<double, string>? progress, CancellationToken ct)
        => Task.Run<IReadOnlyList<StartupItem>>(() => Scan(progress, ct), ct);

    internal List<StartupItem> Scan(Action<double, string>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var items = new List<StartupItem>();

        progress?.Invoke(4, "Читаем разделы реестра Run…");
        items.AddRange(ScanRegistry(ct));

        progress?.Invoke(10, "Проверяем папки «Автозагрузка»…");
        items.AddRange(ScanFolders(ct));

        if (_env.IncludeTasks)
        {
            progress?.Invoke(16, "Просматриваем задания планировщика…");
            items.AddRange(ScanTasks(ct));
        }

        if (_env.IncludeServices)
        {
            progress?.Invoke(32, "Собираем службы с автозапуском…");
            items.AddRange(ScanServices(ct));
        }

        Enrich(items, ct, (done, total) =>
            progress?.Invoke(40 + 60.0 * done / Math.Max(1, total), $"Проверяем цифровые подписи ({done} из {total})…"));

        Log.Info($"Автозагрузка: найдено {items.Count} записей за {sw.Elapsed.TotalSeconds:0.0} с");
        return Sort(items);
    }

    internal static List<StartupItem> Sort(IEnumerable<StartupItem> items) => items
        .OrderByDescending(i => i.IsSuspicious)
        .ThenByDescending(i => i.RiskScore)
        .ThenByDescending(i => i.IsBroken)
        .ThenBy(i => i.SourceGroup)
        .ThenByDescending(i => i.IsEnabled)
        .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>Fills signature, publisher, file existence and the suspicion verdict of every item.</summary>
    internal void Enrich(IReadOnlyList<StartupItem> items, CancellationToken ct, Action<int, int>? progress = null)
    {
        var files = items
            .Select(i => i.TargetFile)
            .Where(f => !string.IsNullOrWhiteSpace(f) && Path.IsPathFullyQualified(f!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var info = new ConcurrentDictionary<string, (SignatureInfo Signature, string? Company, string? Description)>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, file =>
        {
            var signature = FileSignature.Get(file);
            string? company = null, description = null;
            if (signature.Status != SignatureStatus.NotFound)
            {
                try
                {
                    var version = FileVersionInfo.GetVersionInfo(file!);
                    company = Clean(version.CompanyName);
                    description = Clean(version.FileDescription);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // version info is optional
                }
            }
            info[file!] = (signature, company, description);
            var n = Interlocked.Increment(ref done);
            if (n % 8 == 0 || n == files.Count)
                progress?.Invoke(n, files.Count);
        });

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.TargetFile is not null && info.TryGetValue(item.TargetFile, out var fileInfo))
            {
                item.Signature = fileInfo.Signature;
                item.Publisher = fileInfo.Signature.Publisher ?? fileInfo.Company;
                item.FileDescription = fileInfo.Description;
            }
            var exists = StartupNative.TargetExists(item.TargetFile, item.Is32Bit);
            // Missing in this profile only: it may exist for the account the entry really runs for, so never call it broken.
            item.TargetExists = exists == false && item.TargetDependsOnUser ? null : exists;
            try
            {
                Reanalyze(item);
            }
            catch (InvalidOperationException)
            {
                // The trusted list was edited on another page at this very moment: read it again.
                Reanalyze(item);
            }
        }
    }

    /// <summary>Recomputes the trust flag and the heuristic verdict (after "Доверять" or a settings change).</summary>
    public void Reanalyze(StartupItem item)
    {
        item.IsTrusted = IsTrustedNow(item, _env.Settings());
        if (item.IsTrusted || item.TargetExists == false)
        {
            item.Suspicion = SuspicionResult.Clean;
            return;
        }

        // Core already treats a Microsoft-signed binary in a Microsoft install location (Defender's MsMpEng.exe in
        // ProgramData\Microsoft\Windows Defender\Platform) as genuine, and still flags a signed copy started from Temp.
        var result = Suspicion.Analyze(item.TargetFile, item.Signature, item.Command);
        // Known unwanted programs are often recognisable only by the entry name or publisher.
        if (!result.Reasons.Any(r => r.StartsWith("Потенциально нежелательная", StringComparison.Ordinal)))
        {
            var pup = KnownPups.Match(item.Name, item.Publisher) ?? KnownPups.Match(item.FileDescription, null);
            if (pup is not null && !item.IsMicrosoft)
                result = new SuspicionResult(Math.Min(100, result.Score + 35), result.Reasons.Append(pup).ToList());
        }
        item.Suspicion = result;
    }

    /// <summary>
    /// Whether the entry is trusted according to the shared list: the entry itself, or its program file.
    /// A trusted interpreter path (powershell.exe, rundll32.exe…) must not vouch for whatever script it is told to run.
    /// </summary>
    internal static bool IsTrustedNow(StartupItem item, AppSettings settings)
        => settings.IsTrusted(item.Id) ||
           (!StartupTargets.IsGenericHost(item.TargetFile) && settings.IsTrusted(item.TargetFile));

    /// <summary>
    /// "Доверять"/"Не доверять" for one entry. Changes the shared list only through AppSettings.AddTrusted/RemoveTrusted
    /// (thread-safe, saved and logged). Returns the new trust state. Does not re-analyze the item.
    /// </summary>
    internal static bool SetTrusted(StartupItem item, bool trusted, AppSettings settings)
    {
        if (trusted)
        {
            settings.AddTrusted(TrustKey(item));
        }
        else
        {
            if (settings.IsTrusted(item.Id))
                settings.RemoveTrusted(item.Id);
            // The program file is untrusted too, unless it is a generic host (then only the entry was ever trusted).
            if (!string.IsNullOrWhiteSpace(item.TargetFile) && !StartupTargets.IsGenericHost(item.TargetFile) &&
                settings.IsTrusted(item.TargetFile))
                settings.RemoveTrusted(item.TargetFile);
        }
        return IsTrustedNow(item, settings);
    }

    /// <summary>
    /// What "Доверять" stores in AppSettings.TrustedItems: the program file (shared with the Processes/Programs checks),
    /// or the entry itself when the file is unknown or a generic host such as powershell.exe / rundll32.exe —
    /// trusting the interpreter would silence warnings for every script started through it, everywhere in the app.
    /// </summary>
    internal static string TrustKey(StartupItem item)
        => string.IsNullOrWhiteSpace(item.TargetFile) || StartupTargets.IsGenericHost(item.TargetFile) ? item.Id : item.TargetFile!;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ===================== analysis for the dashboard =====================

    public async Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
    {
        var items = await ScanAsync(null, ct);
        return BuildIssues(items);
    }

    /// <summary>Turns a scan into dashboard findings. Pure: does not touch the system.</summary>
    internal IReadOnlyList<HealthIssue> BuildIssues(IReadOnlyList<StartupItem> items)
    {
        var issues = new List<HealthIssue>();

        // 1. Suspicious enabled entries, most dangerous first.
        foreach (var item in items.Where(i => i.IsEnabled && i.IsSuspicious && !i.IsBroken)
                     .OrderByDescending(i => i.RiskScore).Take(10))
        {
            issues.Add(new HealthIssue
            {
                Module = ModuleName,
                Title = $"Подозрительная запись в автозагрузке: {item.DisplayName}",
                Details = $"{string.Join("; ", item.Reasons)}. Источник: {item.SourceText}. Команда: {item.Command}\n" +
                          "Проверьте файл на VirusTotal или Защитником Windows; если программа вам незнакома — " +
                          (item.IsRunOnce ? "удалите эту запись на странице «Автозагрузка»." : "отключите её."),
                Severity = item.RiskLevel == SuspicionLevel.High ? IssueSeverity.High : IssueSeverity.Medium,
                TargetPageKey = ModuleKey,
            });
        }

        // 2. Too many programs starting with Windows.
        var programs = CountedPrograms(items);
        if (programs.Count > 8)
        {
            var names = programs.Select(p => p.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var list = string.Join(", ", names.Take(12)) + (names.Count > 12 ? $" и ещё {names.Count - 12}" : "");
            issues.Add(new HealthIssue
            {
                Module = ModuleName,
                Title = $"Много программ в автозагрузке ({programs.Count})",
                Details = $"Вместе с Windows запускаются: {list}. Отключите те, что не нужны сразу после включения, — " +
                          "компьютер будет загружаться быстрее, а в фоне освободится память. Отключение обратимо.",
                Severity = IssueSeverity.Medium,
                Gain = "Быстрее включение компьютера",
                TargetPageKey = ModuleKey,
            });
        }

        // 3. Broken entries (target file is gone).
        var broken = items.Where(i => i.IsBroken && i.CanDelete).ToList();
        if (broken.Count > 0)
        {
            var list = string.Join(", ", broken.Take(8).Select(b => b.DisplayName)) + (broken.Count > 8 ? $" и ещё {broken.Count - 8}" : "");
            issues.Add(new HealthIssue
            {
                Module = ModuleName,
                Title = $"Битые записи в автозагрузке ({broken.Count})",
                Details = $"Эти записи ссылаются на файлы, которых больше нет: {list}. Они ничего не запускают, только засоряют список. " +
                          "Исправление удалит их, предварительно сохранив резервные копии.",
                Severity = IssueSeverity.Low,
                Gain = "Порядок в автозагрузке",
                TargetPageKey = ModuleKey,
                AutoFixSafe = false,
                FixAsync = ct => FixBrokenAsync(broken, ct),
            });
        }

        return issues;
    }

    /// <summary>Enabled non-Microsoft programs that start at logon (Run, Startup folder, tasks). Hidden Windows items never count.</summary>
    internal static List<StartupItem> CountedPrograms(IEnumerable<StartupItem> items) => items
        .Where(i => i.IsEnabled && !i.IsMicrosoft && !i.IsBroken && !i.IsProtected &&
                    i.Kind is StartupKind.RegistryRun or StartupKind.StartupFolder or StartupKind.ScheduledTask)
        .ToList();

    /// <summary>Dashboard fix: deletes the broken entries with backups. The dashboard has already asked the user.</summary>
    internal async Task<string> FixBrokenAsync(IReadOnlyList<StartupItem> broken, CancellationToken ct)
    {
        var current = broken.Where(i => StartupNative.TargetExists(i.TargetFile, i.Is32Bit) == false).ToList();
        if (current.Count == 0)
            return "Битых записей больше нет — ничего не изменено.";
        var needAdmin = current.Where(i => i.RequiresAdmin).ToList();
        if (needAdmin.Count > 0 && !_env.IsElevated())
            return $"Нужны права администратора: {Format.Count(needAdmin.Count, "запись относится", "записи относятся", "записей относятся")} " +
                   "ко всем пользователям. Ничего не изменено — перезапустите программу от имени администратора и повторите.";

        var result = await DeleteManyAsync(current, ct);
        if (result.Done.Count > 0)
            ChangedExternally?.Invoke();
        return result.Summary;
    }

    // ===================== actions =====================

    /// <summary>Enables or disables an entry (reversible). The caller confirms with the user first.</summary>
    public async Task<StartupActionResult> SetEnabledAsync(StartupItem item, bool enable, CancellationToken ct)
    {
        if (item.IsProtected)
            return StartupActionResult.Fail(item.ToggleBlockedReason);
        if (item.IsRunOnce)
            return StartupActionResult.Fail(item.ToggleBlockedReason);
        if (item.IsEnabled == enable)
            return StartupActionResult.Ok(enable ? "Запись уже включена." : "Запись уже отключена.");

        try
        {
            return item.Kind switch
            {
                StartupKind.RegistryRun or StartupKind.StartupFolder => await SetApprovedAsync(item, enable, ct),
                StartupKind.ScheduledTask => await SetTaskEnabledAsync(item, enable, ct),
                StartupKind.Service => await SetServiceEnabledAsync(item, enable, ct),
                _ => StartupActionResult.Fail("Эту запись нельзя отключить."),
            };
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            Log.Warn($"Автозагрузка: нет прав на изменение «{item.DisplayName}»: {ex.Message}");
            return StartupActionResult.Admin("Не хватает прав для этого изменения. Запустите программу от имени администратора.");
        }
        catch (Exception ex) when (ex is IOException or COMException or Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Автозагрузка: не удалось изменить «{item.DisplayName}»: {ex.Message}");
            return StartupActionResult.Fail($"Не удалось изменить запись: {ex.Message}");
        }
    }

    /// <summary>Removes an entry from startup (with a backup). Services are never deleted. The caller confirms first.</summary>
    public async Task<StartupActionResult> DeleteAsync(StartupItem item, CancellationToken ct)
    {
        if (!item.CanDelete)
            return StartupActionResult.Fail(item.IsService
                ? "Службы не удаляются — их можно только перевести в ручной режим."
                : "Системные записи Windows программа не удаляет.");

        try
        {
            return item.Kind switch
            {
                StartupKind.RegistryRun or StartupKind.RegistryRunOnce => await DeleteRegistryValueAsync(item, ct),
                StartupKind.StartupFolder => await DeleteFolderItemAsync(item, ct),
                StartupKind.ScheduledTask => await DeleteTaskAsync(item, ct),
                _ => StartupActionResult.Fail("Эту запись нельзя удалить."),
            };
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            Log.Warn($"Автозагрузка: нет прав на удаление «{item.DisplayName}»: {ex.Message}");
            return StartupActionResult.Admin("Не хватает прав для удаления. Запустите программу от имени администратора.");
        }
        catch (Exception ex) when (ex is IOException or COMException or Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Автозагрузка: не удалось удалить «{item.DisplayName}»: {ex.Message}");
            return StartupActionResult.Fail($"Не удалось удалить запись: {ex.Message}");
        }
    }

    /// <summary>Deletes several entries one by one; each one is backed up first.</summary>
    public async Task<StartupBulkResult> DeleteManyAsync(IEnumerable<StartupItem> items, CancellationToken ct)
    {
        var done = new List<StartupItem>();
        var failed = new List<(StartupItem, string)>();
        var needsAdmin = false;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var result = await DeleteAsync(item, ct);
            if (result.Success)
                done.Add(item);
            else
            {
                failed.Add((item, result.Message));
                needsAdmin |= result.NeedsAdmin;
            }
        }
        return new StartupBulkResult(done, failed) { NeedsAdmin = needsAdmin };
    }

    internal static bool IsAccessDenied(Exception ex)
        => ex is UnauthorizedAccessException or SecurityException ||
           (ex is COMException com && (uint)com.HResult == 0x80070005) ||
           (ex is Win32Exception w32 && w32.NativeErrorCode == 5);

    // ===================== registry helpers =====================

    internal static RegistryKey? OpenKey(RegistryHive hive, string path, bool writable)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return root.OpenSubKey(path, writable);
    }

    internal static Dictionary<string, byte[]> ReadApproved(RegistryHive hive, string? keyPath)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (keyPath is null)
            return result;
        try
        {
            using var key = OpenKey(hive, keyPath, false);
            if (key is null)
                return result;
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is byte[] data)
                    result[name] = data;
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Автозагрузка: нет доступа к {StartupEnvironment.HiveName(hive)}\\{keyPath}: {ex.Message}");
        }
        return result;
    }

    internal static string Quote(string path) => path.Contains(' ') && !path.StartsWith('"') ? $"\"{path}\"" : path;
}
