using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using PcCleaner.Core;

namespace PcCleaner.Modules.Cleaner;

/// <summary>
/// Finds and removes junk: temporary files, caches of Windows, browsers and apps, the Recycle Bin.
/// Scanning never changes anything; cleaning happens only after the caller confirmed it with the user
/// (the dashboard confirms before calling a HealthIssue.FixAsync).
/// </summary>
public sealed class CleanerService : IHealthContributor
{
    private const long Mb = 1024L * 1024;
    private const long Gb = 1024L * Mb;

    private readonly CleanerEnvironment _env;
    private readonly RootGuard _guard;
    private readonly IRecycleBin _recycleBin;
    private readonly Func<bool> _isElevated;
    private readonly Func<AppSettings> _settings;

    public CleanerService()
        : this(CleanerEnvironment.FromSystem(), null, null, null, null)
    {
    }

    internal CleanerService(CleanerEnvironment env, RootGuard? guard, IRecycleBin? recycleBin, Func<bool>? isElevated,
        Func<AppSettings>? settings)
    {
        _env = env;
        _guard = guard ?? RootGuard.ForEnvironment(env);
        _recycleBin = recycleBin ?? new ShellRecycleBin();
        _isElevated = isElevated ?? (() => Admin.IsElevated);
        _settings = settings ?? (() => AppServices.Settings);
    }

    public string ModuleName => "Очистка";

    public bool IsElevated => _isElevated();

    /// <summary>The settings the cleaner reads (min file age, exclusions, unchecked categories).</summary>
    internal AppSettings Settings => _settings();

    /// <summary>Rules from the current settings (min file age, exclusions, elevation).</summary>
    public CleanerRules CurrentRules() => CleanerRules.FromSettings(_settings(), IsElevated);

    /// <summary>All categories available on this PC (installed browsers/apps only). Cheap: only checks folders.</summary>
    public IReadOnlyList<CleanerCategory> LoadCategories()
        => CleanerCatalog.Build(_env, Math.Clamp(_settings().CleanerMinFileAgeHours, 0, CleanerRules.MaxMinFileAgeHours));

    /// <summary>Re-checks which programs (browsers, Discord, Steam...) are running.</summary>
    public void RefreshRunning(IEnumerable<CleanerCategory> categories)
        => CleanerCatalog.UpdateRunning(categories, _env.RunningProcessNames());

    // ===== Scanning (read-only) =====

    /// <summary>Sizes one category. Read-only; never throws except for cancellation.</summary>
    public async Task<CategoryScanResult> ScanCategoryAsync(CleanerCategory category, int sampleSize, CancellationToken ct)
    {
        var rules = CurrentRules();
        try
        {
            return category.Kind switch
            {
                CategoryKind.RecycleBin => ScanRecycleBin(category),
                CategoryKind.DeliveryOptimization => await ScanDeliveryOptimizationAsync(category, ct),
                _ => await Task.Run(() => CleanerEngine.Scan(category, rules, _guard, sampleSize, ct), ct),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Log.Warn($"Очистка: не удалось оценить «{category.Name}»: {ex.Message}");
            return new CategoryScanResult { CategoryId = category.Id, Error = "Не удалось прочитать папку" };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A bug, but one category must not break the whole analysis.
            Log.Error($"Очистка: ошибка при оценке «{category.Name}»", ex);
            return new CategoryScanResult { CategoryId = category.Id, Error = "Не удалось оценить размер" };
        }
    }

    /// <summary>
    /// Sizes several categories in parallel. <paramref name="onResult"/> is called (on a worker thread) as each one finishes.
    /// </summary>
    public async Task ScanAsync(IReadOnlyList<CleanerCategory> categories, int sampleSize,
        Action<CleanerCategory, CategoryScanResult>? onResult, CancellationToken ct)
    {
        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4),
        };
        // Special categories (PowerShell, shell API) first: they have the slowest start.
        var ordered = categories.OrderBy(c => c.Kind == CategoryKind.Files ? 1 : 0).ToList();
        await Parallel.ForEachAsync(ordered, options, async (category, token) =>
        {
            var result = await ScanCategoryAsync(category, sampleSize, token);
            onResult?.Invoke(category, result);
        });
    }

    private CategoryScanResult ScanRecycleBin(CleanerCategory category)
    {
        var q = _recycleBin.Query();
        if (q is null)
            return new CategoryScanResult { CategoryId = category.Id, Error = "Не удалось узнать размер корзины" };
        return new CategoryScanResult { CategoryId = category.Id, Bytes = q.Value.Bytes, Files = q.Value.Items };
    }

    private async Task<CategoryScanResult> ScanDeliveryOptimizationAsync(CleanerCategory category, CancellationToken ct)
    {
        var result = new CategoryScanResult { CategoryId = category.Id };
        var snapshot = await QueryDeliveryOptimizationAsync(ct);
        if (snapshot is not null)
        {
            result.Bytes = snapshot.Value.Bytes;
            result.Files = snapshot.Value.Files;
        }
        else if (SafeFileWalker.IsWalkableRoot(_env.DeliveryOptimizationCache))
        {
            // Fallback: the folder is readable when elevated. Sized directly (never cleaned this way).
            var stats = new WalkStats();
            foreach (var f in SafeFileWalker.EnumerateFiles(_env.DeliveryOptimizationCache, new WalkOptions(), stats, ct))
            {
                result.Bytes += f.Length;
                result.Files++;
            }
            result.DeniedDirectories = stats.DeniedDirectories;
        }
        else
        {
            result.Error = IsElevated ? "Не удалось узнать размер" : "Размер виден только администратору";
        }
        result.AdminBytes = result.Bytes;
        result.AdminFiles = result.Files;
        return result;
    }

    /// <summary>Get-DeliveryOptimizationPerfSnap works without admin rights and reports the cache size.</summary>
    private static async Task<(long Bytes, long Files)?> QueryDeliveryOptimizationAsync(CancellationToken ct)
    {
        try
        {
            var r = await ProcessRunner.PowerShellAsync(
                "$s = Get-DeliveryOptimizationPerfSnap -ErrorAction Stop; \"$($s.CacheSizeBytes)|$($s.Files)\"",
                TimeSpan.FromSeconds(25), ct);
            if (!r.Success)
                return null;
            var line = r.StdOut.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains('|'));
            if (line is null)
                return null;
            var parts = line.Split('|');
            if (long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
            {
                long.TryParse(parts.ElementAtOrDefault(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var files);
                return (Math.Max(0, bytes), Math.Max(0, files));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"Очистка: не удалось узнать размер кэша оптимизации доставки: {ex.Message}");
        }
        return null;
    }

    // ===== Cleaning (changes the system — callers confirm first) =====

    /// <summary>Cleans one category and writes a Log.Action summary. Never throws except for cancellation.</summary>
    public Task<CategoryCleanResult> CleanCategoryAsync(CleanerCategory category, CancellationToken ct)
        => CleanCategoryAsync(category, userFoldersOnly: false, ct);

    /// <param name="userFoldersOnly">Skip folders that need administrator rights even when elevated (dashboard fix).</param>
    private async Task<CategoryCleanResult> CleanCategoryAsync(CleanerCategory category, bool userFoldersOnly,
        CancellationToken ct)
    {
        CategoryCleanResult result;
        try
        {
            var rules = userFoldersOnly ? CleanerRules.FromSettings(_settings(), isElevated: false) : CurrentRules();
            result = category.Kind switch
            {
                CategoryKind.RecycleBin => await Task.Run(() => EmptyRecycleBin(category), ct),
                CategoryKind.DeliveryOptimization when userFoldersOnly =>
                    new CategoryCleanResult { CategoryId = category.Id, Name = category.Name, SkippedAdminRoots = 1 },
                CategoryKind.DeliveryOptimization => await CleanDeliveryOptimizationAsync(category, ct),
                _ => await Task.Run(() => CleanerEngine.Clean(category, rules, _guard, ct), CancellationToken.None),
            };
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Log.Warn($"Очистка «{category.Name}» не удалась: {ex.Message}");
            result = new CategoryCleanResult { CategoryId = category.Id, Name = category.Name };
            result.Errors.Add(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Очистка «{category.Name}»: непредвиденная ошибка", ex);
            result = new CategoryCleanResult { CategoryId = category.Id, Name = category.Name };
            result.Errors.Add(ex.Message);
        }
        LogResult(category, result, IsElevated);
        return result;
    }

    /// <summary>Cleans categories one after another; stops early (keeping the results so far) when cancelled.</summary>
    public async Task<IReadOnlyList<CategoryCleanResult>> CleanAsync(IReadOnlyList<CleanerCategory> categories,
        Action<int, CleanerCategory>? onStart, CancellationToken ct, bool userFoldersOnly = false)
    {
        var results = new List<CategoryCleanResult>();
        for (var i = 0; i < categories.Count; i++)
        {
            if (ct.IsCancellationRequested)
                break;
            onStart?.Invoke(i, categories[i]);
            try
            {
                results.Add(await CleanCategoryAsync(categories[i], userFoldersOnly, ct));
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return results;
    }

    private CategoryCleanResult EmptyRecycleBin(CleanerCategory category)
    {
        var result = new CategoryCleanResult { CategoryId = category.Id, Name = category.Name };
        var before = _recycleBin.Query();
        if (before is { Items: 0 })
        {
            result.Message = "Корзина уже пуста";
            return result;
        }
        if (_recycleBin.Empty(out var error))
        {
            var after = _recycleBin.Query();
            result.FreedBytes = Math.Max(0, (before?.Bytes ?? 0) - (after?.Bytes ?? 0));
            result.DeletedFiles = Math.Max(0, (before?.Items ?? 0) - (after?.Items ?? 0));
            result.Message = "Корзина очищена";
        }
        else
        {
            result.Errors.Add($"Не удалось очистить корзину ({error})");
        }
        return result;
    }

    private async Task<CategoryCleanResult> CleanDeliveryOptimizationAsync(CleanerCategory category, CancellationToken ct)
    {
        var result = new CategoryCleanResult { CategoryId = category.Id, Name = category.Name };
        if (!IsElevated)
        {
            result.SkippedAdminRoots = 1;
            result.Message = "Нужны права администратора";
            return result;
        }
        var before = await QueryDeliveryOptimizationAsync(ct);
        var r = await ProcessRunner.PowerShellAsync("Delete-DeliveryOptimizationCache -Force -ErrorAction Stop; 'OK'",
            TimeSpan.FromMinutes(5), ct);
        if (r.Success && r.StdOut.Contains("OK"))
        {
            var after = await QueryDeliveryOptimizationAsync(CancellationToken.None);
            result.FreedBytes = Math.Max(0, (before?.Bytes ?? 0) - (after?.Bytes ?? 0));
            result.DeletedFiles = Math.Max(0, (before?.Files ?? 0) - (after?.Files ?? 0));
            result.Message = "Кэш оптимизации доставки очищен";
        }
        else
        {
            var text = (r.StdErr + " " + r.StdOut).Trim();
            result.Errors.Add(string.IsNullOrEmpty(text) ? $"Код завершения {r.ExitCode}" : text);
        }
        return result;
    }

    private static void LogResult(CleanerCategory category, CategoryCleanResult r, bool elevated)
    {
        var text = new StringBuilder($"Очистка «{category.Name}»: ");
        text.Append(r.DeletedFiles > 0 || r.FreedBytes > 0
            ? $"{CleanerText.Deleted(r.DeletedFiles)}, освобождено {Format.Bytes(r.FreedBytes)}"
            : "ничего не удалено");
        if (r.DeletedDirectories > 0)
            text.Append($", удалено пустых папок: {Format.Number(r.DeletedDirectories)}");
        if (r.SkippedFiles > 0)
            text.Append($", пропущено занятых: {Format.Number(r.SkippedFiles)}");
        if (r.SkippedAdminRoots > 0)
            text.Append(elevated ? ", системные папки пропущены" : ", системные папки пропущены (нужны права администратора)");
        if (r.Cancelled)
            text.Append(" (прервано)");
        if (r.Message is not null)
            text.Append($". {r.Message}");

        if (r.DeletedFiles > 0 || r.FreedBytes > 0 || r.DeletedDirectories > 0)
            Log.Action(text.ToString());
        else
            Log.Info(text.ToString());
        foreach (var e in r.Errors.Take(5))
            Log.Warn($"Очистка «{category.Name}»: {e}");
    }

    private static bool IsExpected(Exception ex)
        => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or Win32Exception
            or InvalidOperationException;

    // ===== Deep cleaning (built-in Windows tools) =====

    /// <summary>
    /// Runs DISM /Online /Cleanup-Image /StartComponentCleanup (can take 5–30 minutes). Needs admin;
    /// when not elevated nothing is started. The caller confirms with the user first.
    /// </summary>
    public async Task<string> RunComponentCleanupAsync(CancellationToken ct)
    {
        if (!IsElevated)
            return "Нужны права администратора: перезапустите программу от имени администратора и повторите.";

        var freeBefore = FreeSpace(_env.SystemDrive);
        Log.Action("Запущена очистка компонентов Windows: DISM /Online /Cleanup-Image /StartComponentCleanup");
        var dism = Path.Combine(Environment.SystemDirectory, "dism.exe");
        using var registration = ct.Register(() =>
            Log.Info("Очистку компонентов Windows нельзя безопасно прервать — дождитесь её завершения"));
        // Not cancelled on purpose: killing DISM in the middle of servicing is worse than waiting.
        var r = await ProcessRunner.RunAsync(dism, "/Online /Cleanup-Image /StartComponentCleanup",
            TimeSpan.FromHours(3), CancellationToken.None);
        var freed = Math.Max(0, FreeSpace(_env.SystemDrive) - freeBefore);

        if (r.Success)
        {
            var msg = freed > 0
                ? $"Очистка компонентов завершена. Освобождено примерно {Format.Bytes(freed)}."
                : "Очистка компонентов завершена. Старых компонентов почти не было — место уже было освобождено ранее.";
            Log.Action(msg);
            return msg;
        }
        var details = LastMeaningfulLine(r.StdOut + "\n" + r.StdErr);
        var error = r.TimedOut
            ? "DISM работал слишком долго и был остановлен. Попробуйте позже."
            : $"DISM завершился с кодом {r.ExitCode}." + (details is null ? "" : $" {details}");
        Log.Warn($"Очистка компонентов Windows: {error}");
        return error;
    }

    /// <summary>Opens the built-in Windows "Disk Cleanup" for the system drive (it asks the user itself).</summary>
    public void LaunchDiskCleanup()
    {
        var drive = _env.SystemDrive.TrimEnd('\\').TrimEnd(':');
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cleanmgr.exe"), $"/d {drive}")
            {
                UseShellExecute = true,
            })?.Dispose();
            Log.Info("Открыт мастер очистки диска Windows");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn($"Не удалось открыть мастер очистки диска: {ex.Message}");
            Dialogs.Error("Очистка диска", "Не удалось открыть встроенную утилиту «Очистка диска».");
        }
    }

    private static long FreeSpace(string drive)
    {
        try
        {
            return new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string? LastMeaningfulLine(string text)
        => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 3 && !l.StartsWith('['));

    // ===== Dashboard =====

    /// <summary>Read-only quick sizing of the recommended categories (plus the Recycle Bin).</summary>
    public async Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
    {
        var issues = new List<HealthIssue>();
        var elevated = IsElevated;
        var disabled = new HashSet<string>(_settings().CleanerDisabledCategories ?? new List<string>(), StringComparer.Ordinal);
        var categories = await Task.Run(LoadCategories, ct);
        var junk = categories
            .Where(c => c.DefaultChecked && c.Kind != CategoryKind.RecycleBin && !disabled.Contains(c.Id))
            .ToList();

        // Keep the dashboard responsive: whatever is not sized within the budget is simply left out.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(18));
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, CategoryScanResult>();
        try
        {
            await ScanAsync(junk, 0, (c, r) => results[c.Id] = r, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Info("Очистка: быстрый анализ занял слишком много времени, показан частичный результат");
        }

        var sized = junk.Where(c => results.ContainsKey(c.Id)).ToList();
        // The one-click fix only cleans what a normal user may clean: system folders (Windows\Temp, update cache,
        // Delivery Optimization...) are never part of it, even when the app runs as administrator.
        var cleanable = sized
            .Where(c => c.Kind == CategoryKind.Files && !c.RequiresAdmin)
            .Select(c => (Category: c, Bytes: results[c.Id].CleanableBytes(elevated: false)))
            .Where(x => x.Bytes > 0)
            .OrderByDescending(x => x.Bytes)
            .ToList();
        var total = cleanable.Sum(x => x.Bytes);
        var systemBytes = sized.Sum(c => results[c.Id].AdminBytes);

        var severity = SeverityFor(total);
        if (severity is not null)
        {
            var top = string.Join(", ", cleanable.Take(3).Select(x => $"{x.Category.Name} — {Format.Bytes(x.Bytes)}"));
            var ids = cleanable.Select(x => x.Category.Id).ToHashSet(StringComparer.Ordinal);
            issues.Add(new HealthIssue
            {
                Module = ModuleName,
                Title = $"Можно освободить {Format.Bytes(total)} мусора",
                Details = $"Временные файлы и кэш, которые можно безопасно удалить. Больше всего: {top}. " +
                          "Пароли, история браузеров и ваши документы не затрагиваются; занятые файлы будут пропущены.",
                Severity = severity.Value,
                Gain = $"Освободит до {Format.Bytes(total)}",
                ReclaimableBytes = total,
                AutoFixSafe = true,
                TargetPageKey = "Cleaner",
                FixAsync = token => FixJunkAsync(ids, token),
            });
        }

        if (systemBytes > 500 * Mb)
        {
            issues.Add(new HealthIssue
            {
                Module = ModuleName,
                Title = $"Ещё {Format.Bytes(systemBytes)} системного мусора",
                Details = elevated
                    ? "Временные файлы Windows, кэш обновлений и другие системные папки не очищаются автоматически. " +
                      "Откройте «Очистку», проверьте список и удалите их вручную."
                    : "Временные файлы Windows, кэш обновлений и другие системные папки можно очистить только " +
                      "с правами администратора. Перезапустите программу от имени администратора и откройте «Очистку».",
                Severity = IssueSeverity.Info,
                Gain = $"Освободит до {Format.Bytes(systemBytes)}",
                TargetPageKey = "Cleaner",
            });
        }

        // SHQueryRecycleBin walks the bins of every drive: keep it off the caller's (UI) thread.
        var bin = await Task.Run(() => _recycleBin.Query(), ct);
        if (bin is { Items: > 0 })
        {
            var (bytes, items) = bin.Value;
            issues.Add(new HealthIssue
            {
                Module = ModuleName,
                Title = $"В корзине {Format.Bytes(bytes)}",
                Details = $"В корзине {Format.Count(items, "объект", "объекта", "объектов")}. Это файлы, которые вы удалили раньше: " +
                          "если они не нужны, корзину можно очистить. Восстановить их после этого будет нельзя.",
                Severity = IssueSeverity.Info,
                Gain = $"Освободит {Format.Bytes(bytes)}",
                ReclaimableBytes = bytes,
                AutoFixSafe = false,
                TargetPageKey = "Cleaner",
                FixAsync = async token =>
                {
                    var category = categories.First(c => c.Kind == CategoryKind.RecycleBin);
                    var r = await CleanCategoryAsync(category, token);
                    return r.Errors.Count > 0
                        ? "Не удалось очистить корзину: " + r.Errors[0]
                        : $"Корзина очищена, освобождено {Format.Bytes(r.FreedBytes)}.";
                },
            });
        }
        return issues;
    }

    /// <summary>How important "можно освободить N" is: &gt;5 ГБ high, &gt;1 ГБ medium, &gt;100 МБ low, otherwise not worth a finding.</summary>
    internal static IssueSeverity? SeverityFor(long reclaimableBytes) => reclaimableBytes switch
    {
        > 5 * Gb => IssueSeverity.High,
        > 1 * Gb => IssueSeverity.Medium,
        > 100 * Mb => IssueSeverity.Low,
        _ => null,
    };

    /// <summary>
    /// Dashboard fix: cleans the given recommended categories, user folders only. Folders that need administrator
    /// rights are skipped even when the app is elevated, so the fix does exactly what the issue promised.
    /// </summary>
    private async Task<string> FixJunkAsync(IReadOnlySet<string> categoryIds, CancellationToken ct)
    {
        var categories = (await Task.Run(LoadCategories, ct))
            .Where(c => categoryIds.Contains(c.Id) && c.Kind == CategoryKind.Files && !c.RequiresAdmin)
            .ToList();
        if (categories.Count == 0)
            return "Нечего очищать: подходящие файлы уже удалены.";
        var results = await CleanAsync(categories, null, ct, userFoldersOnly: true);
        return Summarize(results);
    }

    /// <summary>"Освобождено 3,2 ГБ, удалено 12 345 файлов, пропущено 87 занятых."</summary>
    public static string Summarize(IReadOnlyCollection<CategoryCleanResult> results)
    {
        var text = CleanerText.ResultLine(results.Sum(r => r.FreedBytes), results.Sum(r => r.DeletedFiles),
            results.Sum(r => r.SkippedFiles));
        if (results.Any(r => r.Cancelled))
            text += " (очистка прервана)";
        return text + ".";
    }
}
