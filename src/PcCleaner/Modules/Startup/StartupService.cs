using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Startup;

/// <summary>Outcome of one change made to a startup entry.</summary>
public sealed record StartupActionResult(bool Success, string Message, bool NeedsAdmin = false, string? BackupPath = null)
{
    /// <summary>
    /// A "broken" entry was left alone because it is no longer what the scan saw (rewritten by an update, file back).
    /// Not an error: nothing was changed. Message holds the reason.
    /// </summary>
    public bool Skipped { get; init; }

    public static StartupActionResult Ok(string message, string? backupPath = null) => new(true, message, false, backupPath);

    public static StartupActionResult Fail(string message) => new(false, message);

    public static StartupActionResult Admin(string message) => new(false, message, true);

    public static StartupActionResult Skip(string reason) => new(false, reason) { Skipped = true };
}

/// <summary>Outcome of a batch deletion.</summary>
public sealed record StartupBulkResult(IReadOnlyList<StartupItem> Done, IReadOnlyList<(StartupItem Item, string Error)> Failed)
{
    public bool NeedsAdmin { get; init; }

    /// <summary>Broken entries left alone because they changed after the scan (see <see cref="StartupActionResult.Skipped"/>).</summary>
    public IReadOnlyList<(StartupItem Item, string Reason)> Skipped { get; init; } = Array.Empty<(StartupItem, string)>();

    public string Summary
    {
        get
        {
            var lines = new List<string>();
            lines.Add(Done.Count > 0
                ? T.F("Удалено из автозагрузки: {0}.", Format.Count(Done.Count, "запись", "записи", "записей"))
                : Failed.Count == 0 && Skipped.Count > 0
                    ? T._("Удалять нечего.")
                    : T._("Ничего не удалено."));
            if (Done.Count > 0)
                lines.Add(T.F("Резервные копии сохранены в папке {0} (ярлыки — в Корзине).", AppPaths.BackupsDir));
            if (Skipped.Count > 0)
            {
                lines.Add("");
                lines.Add(T.F("Оставлены без изменений — после проверки записи изменились ({0}):", Skipped.Count));
                lines.AddRange(Skipped.Take(10).Select(s => $"• {s.Item.DisplayName} — {s.Reason}"));
                if (Skipped.Count > 10)
                    lines.Add("• " + T.F("…и ещё {0}", Skipped.Count - 10));
                lines.Add(T._("Обновите список автозагрузки, чтобы увидеть их текущее состояние."));
            }
            if (Failed.Count > 0)
            {
                lines.Add("");
                lines.Add(T.F("Не удалось удалить ({0}):", Failed.Count));
                lines.AddRange(Failed.Take(10).Select(f => $"• {f.Item.DisplayName} — {f.Error}"));
                if (Failed.Count > 10)
                    lines.Add("• " + T.F("…и ещё {0}", Failed.Count - 10));
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

    public string ModuleName => T._("Автозагрузка");

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

        progress?.Invoke(4, T._("Читаем разделы реестра Run…"));
        items.AddRange(ScanRegistry(ct));

        progress?.Invoke(10, T._("Проверяем папки «Автозагрузка»…"));
        items.AddRange(ScanFolders(ct));

        if (_env.IncludeTasks)
        {
            progress?.Invoke(16, T._("Просматриваем задания планировщика…"));
            items.AddRange(ScanTasks(ct));
        }

        if (_env.IncludeServices)
        {
            progress?.Invoke(32, T._("Собираем службы с автозапуском…"));
            items.AddRange(ScanServices(ct));
        }

        Enrich(items, ct, (done, total) =>
            progress?.Invoke(40 + 60.0 * done / Math.Max(1, total), T.F("Проверяем цифровые подписи ({0} из {1})…", done, total)));

        Log.Info(T.F("Автозагрузка: найдено {0} за {1:0.0} с", Format.Count(items.Count, "запись", "записи", "записей"), sw.Elapsed.TotalSeconds));
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
        if (!result.Flags.HasFlag(SuspicionFlags.KnownPup))
        {
            var pup = KnownPups.Match(item.Name, item.Publisher) ?? KnownPups.Match(item.FileDescription, null);
            if (pup is not null && !item.IsMicrosoft)
                result = AddPupMatch(result, pup, item.Signature);
        }
        item.Suspicion = result;
    }

    /// <summary>Highest score of an informational verdict («Внимание»): one point below <see cref="SuspicionLevel.Medium"/>.</summary>
    internal const int BelowMediumScore = 34;

    /// <summary>
    /// Adds a KnownPups match by the entry name, publisher or file description (+35). When that match is the ONLY signal and
    /// the program file has a valid signature, the verdict stays informational (below Medium): the page still shows why,
    /// but the dashboard reports the program once — as an unwanted program from «Программы» — instead of a second
    /// "suspicious startup entry" for the same program. An unsigned (or unchecked) file, or one with other signals,
    /// keeps the full boost.
    /// </summary>
    internal static SuspicionResult AddPupMatch(SuspicionResult result, string pup, SignatureInfo? signature)
    {
        var score = Math.Min(100, result.Score + 35);
        if (signature is { IsValid: true } && result.Reasons.Count == 0)
            score = Math.Min(score, BelowMediumScore);
        return result with { Score = score, Reasons = result.Reasons.Append(pup).ToList(), Flags = result.Flags | SuspicionFlags.KnownPup };
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
    /// What "Доверять" stores in AppSettings.TrustedItems: the program file, or the entry itself when the file is unknown or a
    /// generic host such as powershell.exe / rundll32.exe — trusting the interpreter would silence warnings for every script
    /// started through it, everywhere in the app.
    /// The file key is the same one «Процессы» checks for a process's exe path. «Программы» checks a program's DisplayName
    /// instead; it adds a file key only for the program's main exe (InstalledProgram.TrustFile) when the user presses
    /// «Доверять» there, so only that one file is shared with this page — not the program's other executables.
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
                Title = T.F("Подозрительная запись в автозагрузке: {0}", item.DisplayName),
                Details = item.IsRunOnce
                    ? T.F("{0}. Источник: {1}. Команда: {2}\nПроверьте файл на VirusTotal или Защитником Windows; если программа вам незнакома — кнопка удалит эту запись (сначала сохранится резервная копия).",
                        string.Join("; ", item.Reasons), item.SourceText, item.Command)
                    : T.F("{0}. Источник: {1}. Команда: {2}\nПроверьте файл на VirusTotal или Защитником Windows; если программа вам незнакома — кнопка отключит её; включить обратно можно на странице «Автозагрузка».",
                        string.Join("; ", item.Reasons), item.SourceText, item.Command),
                Severity = item.RiskLevel == SuspicionLevel.High ? IssueSeverity.High : IssueSeverity.Medium,
                TargetPageKey = ModuleKey,
                FixLabel = item.IsRunOnce ? T._("Удалить запись") : T._("Отключить"),
                FixAsync = token => FixSuspiciousAsync(item, token),
            });
        }

        // 2. Too many programs starting with Windows.
        var programs = CountedPrograms(items);
        if (programs.Count > 8)
        {
            var names = programs.Select(p => p.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var list = string.Join(", ", names.Take(12)) + (names.Count > 12 ? " " + T.F("и ещё {0}", names.Count - 12) : "");
            issues.Add(new HealthIssue
            {
                Module = ModuleName,
                Title = T.F("Много программ в автозагрузке ({0})", programs.Count),
                Details = T.F("Вместе с Windows запускаются: {0}. Отключите те, что не нужны сразу после включения, — компьютер будет загружаться быстрее, а в фоне освободится память. Отключение обратимо.", list),
                Severity = IssueSeverity.Medium,
                Gain = T._("Быстрее включение компьютера"),
                TargetPageKey = ModuleKey,
            });
        }

        // 3. Broken entries (target file is gone).
        var broken = items.Where(i => i.IsBroken && i.CanDelete).ToList();
        if (broken.Count > 0)
        {
            var list = string.Join(", ", broken.Take(8).Select(b => b.DisplayName)) + (broken.Count > 8 ? " " + T.F("и ещё {0}", broken.Count - 8) : "");
            issues.Add(new HealthIssue
            {
                Module = ModuleName,
                Title = T.F("Битые записи в автозагрузке ({0})", broken.Count),
                Details = T.F("Эти записи ссылаются на файлы, которых больше нет: {0}. Они ничего не запускают, только засоряют список. Исправление удалит их, предварительно сохранив резервные копии.", list),
                Severity = IssueSeverity.Low,
                Gain = T._("Порядок в автозагрузке"),
                TargetPageKey = ModuleKey,
                AutoFixSafe = false,
                FixAsync = ct => FixBrokenAsync(broken, ct),
            });
        }

        return issues;
    }

    /// <summary>
    /// Dashboard fix of a suspicious entry: disables it, or removes it (with a backup) when it is a RunOnce entry, which cannot
    /// be disabled. The entries are read again first: the scan may be hours old, and an entry that is gone, that the user trusted
    /// since or that no longer looks suspicious is left alone — the problem is gone, so the text reads as done, not as a failure.
    /// </summary>
    internal async Task<string> FixSuspiciousAsync(StartupItem scanned, CancellationToken ct)
    {
        var current = (await ScanAsync(null, ct)).FirstOrDefault(i => i.Id == scanned.Id);
        if (current is null)
            return T._("Запись уже удалена из автозагрузки — делать ничего не нужно.");
        if (!current.IsSuspicious)
            return T.F("Запись {0} больше не считается подозрительной — делать ничего не нужно.", T.Quote(current.DisplayName));

        var removed = current.IsRunOnce;
        var result = removed ? await DeleteAsync(current, ct) : await SetEnabledAsync(current, false, ct);
        if (result.Success)
            ChangedExternally?.Invoke();
        return SuspiciousFixText(current, result, removed);
    }

    /// <summary>What the dashboard shows after the fix; failures and missing rights use the wording the dashboard recognises.</summary>
    internal static string SuspiciousFixText(StartupItem item, StartupActionResult result, bool removed)
    {
        var name = item.DisplayName;
        if (result.Success)
            return removed
                ? T.F("Запись {0} удалена из автозагрузки — резервная копия сохранена.", T.Quote(name))
                : T.F("Запись {0} отключена — включить её можно на странице «Автозагрузка».", T.Quote(name));
        if (result.NeedsAdmin)
            return T.F("Нужны права администратора: {0}", result.Message);
        // The reason may already say that it could not be done (the Startup page shows it alone): one such phrase is enough, and the
        // dashboard row already names the entry. The dashboard's own reading of a result decides, in every language.
        if (PcCleaner.Modules.Dashboard.HealthScoring.LooksLikeFailure(result.Message))
            return result.Message.Trim();
        return removed
            ? T.F("Не удалось удалить {0}: {1}", T.Quote(name), result.Message)
            : T.F("Не удалось отключить {0}: {1}", T.Quote(name), result.Message);
    }

    /// <summary>Enabled non-Microsoft programs that start at logon (Run, Startup folder, tasks). Hidden Windows items never count.</summary>
    internal static List<StartupItem> CountedPrograms(IEnumerable<StartupItem> items) => items
        .Where(i => i.IsEnabled && !i.IsMicrosoft && !i.IsBroken && !i.IsProtected &&
                    i.Kind is StartupKind.RegistryRun or StartupKind.StartupFolder or StartupKind.ScheduledTask)
        .ToList();

    /// <summary>
    /// Dashboard fix: deletes the broken entries with backups. The dashboard has already asked the user.
    /// The scan may be hours old, so every entry is read again first (not only its old file): an entry that is gone,
    /// was rewritten by an update or whose file is back is left alone, and "already resolved" is reported as success.
    /// </summary>
    internal async Task<string> FixBrokenAsync(IReadOnlyList<StartupItem> broken, CancellationToken ct)
    {
        var current = new List<StartupItem>();
        foreach (var item in broken)
        {
            ct.ThrowIfCancellationRequested();
            if (!await NoLongerBrokenAsync(item, ct))
                current.Add(item);
        }
        // Entries that are gone or changed mean the Startup page list is out of date as well.
        var changed = current.Count < broken.Count;
        try
        {
            if (current.Count == 0)
                return T._("Битых записей больше нет — исправлять нечего.");
            var needAdmin = current.Where(i => i.RequiresAdmin).ToList();
            if (needAdmin.Count > 0 && !_env.IsElevated())
                return T.F("Нужны права администратора: {0} ко всем пользователям. Ничего не изменено — перезапустите программу от имени администратора и повторите.",
                    Format.Count(needAdmin.Count, "запись относится", "записи относятся", "записей относятся"));

            var result = await DeleteBrokenAsync(current, ct);
            changed |= result.Done.Count > 0 || result.Skipped.Count > 0;
            return result.Summary;
        }
        finally
        {
            if (changed)
                ChangedExternally?.Invoke();
        }
    }

    internal static string ChangedSinceScan => T._("в записи теперь другая команда запуска (похоже, программу обновили или переустановили)");
    internal static string TargetIsBack => T._("файл программы снова на месте");

    /// <summary>
    /// The re-check made right before a "broken" entry is deleted. <paramref name="currentCommand"/> / <paramref name="currentTarget"/>
    /// are read again from the registry value, the shortcut or the task. Returns null when the entry still holds exactly the scanned
    /// command and its file is still definitely missing; otherwise the reason to leave it alone. Programs that keep a version folder in
    /// their autostart command ("…\95.0.1.0\GoogleDriveFS.exe") rewrite the same value after an update — checking only the old file
    /// would delete the new, working entry.
    /// </summary>
    internal static string? WhyNoLongerBroken(StartupItem item, string? currentCommand, string? currentTarget)
    {
        if (currentCommand is null || !string.Equals(currentCommand.Trim(), item.Command, StringComparison.Ordinal))
            return ChangedSinceScan;
        // A per-user path in an entry of another account may exist in that account's profile: never "broken" (as in Enrich).
        if (item.TargetDependsOnUser || StartupNative.TargetExists(currentTarget, item.Is32Bit) != false)
            return TargetIsBack;
        return null;
    }

    /// <summary>
    /// Reads the entry again (registry value, shortcut target, task action). Null = still exactly as scanned and still broken
    /// (or nothing to re-read: the deletion refuses an entry without a location); otherwise why not, including "the entry is gone".
    /// May throw on access/COM errors.
    /// </summary>
    private async Task<string?> RecheckBrokenAsync(StartupItem item, CancellationToken ct)
    {
        switch (item.Kind)
        {
            case StartupKind.RegistryRun or StartupKind.RegistryRunOnce when item.RegistryKeyPath is not null:
            {
                var (exists, command) = await Task.Run(() => ReadRunValue(item), ct);
                return exists ? WhyNoLongerBroken(item, command, StartupTargets.Resolve(command)) : T._("записи уже нет");
            }
            case StartupKind.StartupFolder when !string.IsNullOrWhiteSpace(item.FilePath):
            {
                var path = item.FilePath;
                var current = await Task.Run(() => File.Exists(path) ? DescribeFolderFile(path) : ((string Command, string? Target)?)null, ct);
                return current is { } c ? WhyNoLongerBroken(item, c.Command, c.Target) : T._("ярлыка уже нет");
            }
            case StartupKind.ScheduledTask when !string.IsNullOrWhiteSpace(item.TaskPath):
            {
                (string Command, string? Target) current = default;
                try
                {
                    await RunOnTaskAsync(item.TaskPath, (_, task) => current = DescribeRegisteredTask(task), ct);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException ||
                                           (ex is COMException com && (uint)com.HResult is 0x80070002 or 0x80070003))
                {
                    return T._("задания уже нет");
                }
                return WhyNoLongerBroken(item, current.Command, current.Target);
            }
            default:
                return null; // no location (or a service): the deletion itself refuses such an entry
        }
    }

    /// <summary>
    /// True when the entry is definitely no longer the broken one from the scan. A read error is "undecided" (false):
    /// the deletion re-checks the entry itself and reports the error.
    /// </summary>
    private async Task<bool> NoLongerBrokenAsync(StartupItem item, CancellationToken ct)
    {
        try
        {
            return await RecheckBrokenAsync(item, ct) is not null;
        }
        catch (Exception ex) when (IsAccessDenied(ex) || ex is IOException or COMException or Win32Exception or InvalidOperationException)
        {
            return false;
        }
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
            return StartupActionResult.Ok(enable ? T._("Запись уже включена.") : T._("Запись уже отключена."));

        try
        {
            return item.Kind switch
            {
                StartupKind.RegistryRun or StartupKind.StartupFolder => await SetApprovedAsync(item, enable, ct),
                StartupKind.ScheduledTask => await SetTaskEnabledAsync(item, enable, ct),
                StartupKind.Service => await SetServiceEnabledAsync(item, enable, ct),
                _ => StartupActionResult.Fail(T._("Эту запись нельзя отключить.")),
            };
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            Log.Warn(T.F("Автозагрузка: нет прав на изменение {0}: {1}", T.Quote(item.DisplayName), ErrorText.For(ex)), ex);
            return StartupActionResult.Admin(T._("Не хватает прав для этого изменения. Запустите программу от имени администратора."));
        }
        catch (Exception ex) when (ex is IOException or COMException or Win32Exception or InvalidOperationException)
        {
            Log.Warn(T.F("Автозагрузка: не удалось изменить {0}: {1}", T.Quote(item.DisplayName), ErrorText.For(ex)), ex);
            return StartupActionResult.Fail(T.F("Не удалось изменить запись: {0}.", ErrorText.For(ex)));
        }
    }

    /// <summary>Removes an entry from startup (with a backup). Services are never deleted. The caller confirms first.</summary>
    public Task<StartupActionResult> DeleteAsync(StartupItem item, CancellationToken ct)
        => DeleteAsync(item, onlyIfStillBroken: false, ct);

    /// <summary>
    /// Removes an entry from startup (with a backup). With <paramref name="onlyIfStillBroken"/> the entry is read again right before
    /// the deletion and left alone (<see cref="StartupActionResult.Skipped"/>) unless it still holds the scanned command and its file
    /// is still missing — see <see cref="WhyNoLongerBroken"/>.
    /// </summary>
    internal async Task<StartupActionResult> DeleteAsync(StartupItem item, bool onlyIfStillBroken, CancellationToken ct)
    {
        if (!item.CanDelete)
            return StartupActionResult.Fail(item.IsService
                ? T._("Службы не удаляются — их можно только перевести в ручной режим.")
                : T._("Системные записи Windows программа не удаляет."));

        try
        {
            return item.Kind switch
            {
                StartupKind.RegistryRun or StartupKind.RegistryRunOnce => await DeleteRegistryValueAsync(item, onlyIfStillBroken, ct),
                StartupKind.StartupFolder => await DeleteFolderItemAsync(item, onlyIfStillBroken, ct),
                StartupKind.ScheduledTask => await DeleteTaskAsync(item, onlyIfStillBroken, ct),
                _ => StartupActionResult.Fail(T._("Эту запись нельзя удалить.")),
            };
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            Log.Warn(T.F("Автозагрузка: нет прав на удаление {0}: {1}", T.Quote(item.DisplayName), ErrorText.For(ex)), ex);
            return StartupActionResult.Admin(T._("Не хватает прав для удаления. Запустите программу от имени администратора."));
        }
        catch (Exception ex) when (ex is IOException or COMException or Win32Exception or InvalidOperationException)
        {
            Log.Warn(T.F("Автозагрузка: не удалось удалить {0}: {1}", T.Quote(item.DisplayName), ErrorText.For(ex)), ex);
            return StartupActionResult.Fail(T.F("Не удалось удалить запись: {0}.", ErrorText.For(ex)));
        }
    }

    /// <summary>Deletes several entries one by one; each one is backed up first.</summary>
    public Task<StartupBulkResult> DeleteManyAsync(IEnumerable<StartupItem> items, CancellationToken ct)
        => DeleteManyAsync(items, onlyIfStillBroken: false, ct);

    /// <summary>
    /// "Удалить битые" and the dashboard fix: deletes broken entries one by one (each backed up first), re-reading every entry right
    /// before its deletion. Entries rewritten since the scan (e.g. a program update moved them to a new version folder) or whose file
    /// is back are left alone and reported in <see cref="StartupBulkResult.Skipped"/>.
    /// </summary>
    public Task<StartupBulkResult> DeleteBrokenAsync(IEnumerable<StartupItem> items, CancellationToken ct)
        => DeleteManyAsync(items, onlyIfStillBroken: true, ct);

    private async Task<StartupBulkResult> DeleteManyAsync(IEnumerable<StartupItem> items, bool onlyIfStillBroken, CancellationToken ct)
    {
        var done = new List<StartupItem>();
        var failed = new List<(StartupItem, string)>();
        var skipped = new List<(StartupItem, string)>();
        var needsAdmin = false;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var result = await DeleteAsync(item, onlyIfStillBroken, ct);
            if (result.Success)
                done.Add(item);
            else if (result.Skipped)
                skipped.Add((item, result.Message));
            else
            {
                failed.Add((item, result.Message));
                needsAdmin |= result.NeedsAdmin;
            }
        }
        return new StartupBulkResult(done, failed) { NeedsAdmin = needsAdmin, Skipped = skipped };
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
            Log.Warn(T.F("Автозагрузка: нет доступа к {0}\\{1}: {2}", StartupEnvironment.HiveName(hive), keyPath, ErrorText.For(ex)), ex);
        }
        return result;
    }

    internal static string Quote(string path) => path.Contains(' ') && !path.StartsWith('"') ? $"\"{path}\"" : path;
}
