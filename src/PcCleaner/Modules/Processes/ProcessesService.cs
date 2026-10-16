using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PcCleaner.Core;

namespace PcCleaner.Modules.Processes;

/// <summary>Result of "В карантин" for a running program.</summary>
internal sealed record QuarantineOutcome(bool Success, string Message, KillReport Report);

/// <summary>
/// Everything about running processes: cheap snapshots for the live list, cached heavy details (path, version info,
/// command line, services, signatures), safe termination and quarantine, and the read-only dashboard analysis.
/// </summary>
public sealed class ProcessesService : IHealthContributor
{
    private const int ErrorAccessDenied = 5;

    private readonly IProcessSource _source;
    private readonly IProcessDetailsProvider _details;
    private readonly Func<string, SignatureInfo> _signatureProvider;
    private readonly Func<string, FileMeta?> _fileMetaProvider;
    private readonly ConcurrentDictionary<ProcessKey, ProcessStaticInfo> _cache = new();
    private readonly ConcurrentDictionary<string, FileMeta?> _metaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<SignatureInfo>>> _signatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signatureGate = new(3);
    private readonly SemaphoreSlim _detailsGate = new(1, 1);

    public ProcessesService()
        : this(new NativeProcessSource(), new WmiDetailsProvider())
    {
    }

    internal ProcessesService(
        IProcessSource source,
        IProcessDetailsProvider details,
        Func<string, SignatureInfo>? signatures = null,
        Func<string, FileMeta?>? fileMeta = null,
        Func<long>? clock = null,
        int? logicalCpus = null)
    {
        _source = source;
        _details = details;
        _signatureProvider = signatures ?? FileSignature.Get;
        _fileMetaProvider = fileMeta ?? ReadFileMeta;
        Clock = clock ?? Stopwatch.GetTimestamp;
        LogicalCpus = logicalCpus ?? Environment.ProcessorCount;
    }

    public string ModuleName => "Процессы";

    /// <summary>Timestamp source in Stopwatch ticks (replaceable in tests).</summary>
    internal Func<long> Clock { get; }

    internal int LogicalCpus { get; }

    // ---------------------------------------------------------------- snapshots

    /// <summary>
    /// One snapshot of all processes (except the "System Idle Process"). Cheap: static details are cached per process,
    /// only new processes get their path and version info resolved. Read-only. Call from a background thread.
    /// </summary>
    internal List<ProcessSample> TakeSnapshot(CpuSampler sampler)
    {
        var raw = _source.Enumerate();
        var cpu = sampler.Sample(raw, Clock(), LogicalCpus);
        var samples = new List<ProcessSample>(raw.Count);
        var live = new HashSet<ProcessKey>();
        for (var i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            if (r.Pid == 0)
                continue; // "Бездействие системы" is free CPU time, not a program
            if (!live.Add(r.Key))
                continue;
            var info = _cache.GetOrAdd(r.Key, _ => CreateInfo(r));
            samples.Add(new ProcessSample(r, info, cpu[i]));
        }

        foreach (var key in _cache.Keys)
            if (!live.Contains(key))
                _cache.TryRemove(key, out _);
        return samples;
    }

    private ProcessStaticInfo CreateInfo(RawProcess r)
    {
        var info = new ProcessStaticInfo(r.Key, r.Name);
        if (KnownWindowsProcesses.IsPseudo(r.Name))
        {
            info.DetailsLoaded = true;
            return info;
        }
        string? path = null;
        try
        {
            path = _source.QueryImagePath(r.Pid, r.CreateTime);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        {
            // access denied: the WMI/service pass may still find it
        }
        if (path is not null)
        {
            info.Path = path;
            info.Meta = GetMeta(path);
        }
        return info;
    }

    internal FileMeta? GetMeta(string path) => _metaCache.GetOrAdd(path, p => _fileMetaProvider(p));

    private static FileMeta? ReadFileMeta(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var v = FileVersionInfo.GetVersionInfo(path);
            return new FileMeta(Clean(v.FileDescription), Clean(v.CompanyName), Clean(v.ProductName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    // ---------------------------------------------------------------- slow details (WMI)

    /// <summary>
    /// Fills command lines, hosted services and — for processes Windows wouldn't open — the executable path from the
    /// service configuration. One WMI pass at a time; read-only. Command lines of other users' processes need admin.
    /// </summary>
    internal async Task LoadDetailsAsync(IReadOnlyCollection<ProcessStaticInfo> infos, CancellationToken ct)
    {
        if (infos.All(i => i.DetailsLoaded))
            return;
        await _detailsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var todo = infos.Where(i => !i.DetailsLoaded).ToList();
            if (todo.Count == 0)
                return;
            await Task.Run(() => LoadDetails(todo), ct).ConfigureAwait(false);
        }
        finally
        {
            _detailsGate.Release();
        }
    }

    private void LoadDetails(List<ProcessStaticInfo> todo)
    {
        var pids = todo.Select(i => i.Key.Pid).Distinct().ToList();
        IReadOnlyList<WmiProcessRow> processRows;
        IReadOnlyList<WmiServiceRow> serviceRows;
        try
        {
            processRows = _details.QueryProcesses(pids.Count <= WmiDetailsProvider.MaxFilteredPids ? pids : null);
            serviceRows = _details.QueryServices();
        }
        catch (Exception ex)
        {
            Log.Warn($"Процессы: не удалось получить командные строки через WMI: {ex.Message}");
            processRows = Array.Empty<WmiProcessRow>();
            serviceRows = Array.Empty<WmiServiceRow>();
        }

        var byPid = new Dictionary<int, WmiProcessRow>();
        foreach (var row in processRows)
            byPid.TryAdd(row.Pid, row);
        var servicesByPid = serviceRows.GroupBy(s => s.Pid).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var info in todo)
        {
            if (byPid.TryGetValue(info.Key.Pid, out var row) && SameStart(row.CreationDate, info.Key.CreateTime))
            {
                if (!string.IsNullOrWhiteSpace(row.CommandLine))
                    info.CommandLine = row.CommandLine.Trim();
                info.ParentPid = row.ParentPid;
                if (info.Path is null && IsPlausiblePath(row.ExecutablePath, info.Name))
                    SetPath(info, row.ExecutablePath!, fromService: false);
            }

            if (servicesByPid.TryGetValue(info.Key.Pid, out var services))
            {
                info.Services = services
                    .Select(s => string.IsNullOrWhiteSpace(s.DisplayName) ? s.Name : s.DisplayName!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                if (info.Path is null)
                {
                    foreach (var service in services)
                    {
                        var exe = CommandLine.Split(service.PathName).Executable;
                        if (IsPlausiblePath(exe, info.Name))
                        {
                            SetPath(info, exe!, fromService: true);
                            break;
                        }
                    }
                }
            }
            info.DetailsLoaded = true;
        }
    }

    private void SetPath(ProcessStaticInfo info, string path, bool fromService)
    {
        info.Meta = GetMeta(path);
        info.PathFromService = fromService;
        info.Path = path;
    }

    /// <summary>A path from WMI/service config is used only if it is absolute and names the same file as the process.</summary>
    internal static bool IsPlausiblePath(string? path, string processName)
        => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) &&
           string.Equals(PathUtil.FileNameOrNull(path), processName, StringComparison.OrdinalIgnoreCase);

    /// <summary>WMI and the snapshot must describe the same process (PIDs are reused).</summary>
    internal static bool SameStart(DateTime? wmiCreationDate, long createFileTime)
    {
        if (wmiCreationDate is null || createFileTime <= 0)
            return true;
        try
        {
            return Math.Abs(wmiCreationDate.Value.ToFileTime() - createFileTime) < 2 * TimeSpan.TicksPerSecond;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    // ---------------------------------------------------------------- signatures & suspicion

    /// <summary>Signature if it has already been checked, otherwise null.</summary>
    internal SignatureInfo? TryGetSignature(string? path)
        => path is not null && _signatures.TryGetValue(path, out var lazy) && lazy.IsValueCreated &&
           lazy.Value.IsCompletedSuccessfully
            ? lazy.Value.Result
            : null;

    /// <summary>Checks the Authenticode signature in the background (max 3 at a time), once per path.</summary>
    internal Task<SignatureInfo> GetSignatureAsync(string path)
        => _signatures.GetOrAdd(path, p => new Lazy<Task<SignatureInfo>>(() => CheckSignatureAsync(p))).Value;

    private async Task<SignatureInfo> CheckSignatureAsync(string path)
    {
        await _signatureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => _signatureProvider(path)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Процессы: не удалось проверить подпись {path}: {ex.Message}");
            return new SignatureInfo(SignatureStatus.Error, null);
        }
        finally
        {
            _signatureGate.Release();
        }
    }

    /// <summary>Text of the Core heuristic reason for web addresses in a command line.</summary>
    internal const string UrlReason = "Командная строка содержит интернет-адрес";

    private static readonly string[] MicrosoftSoftwareRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft"),
    };

    /// <summary>
    /// Heuristic verdict for a process: <see cref="Suspicion.Analyze"/> adapted to running processes, plus a check of the
    /// file's description/company against known potentially unwanted programs. Respects the user's trusted list.
    /// </summary>
    internal static SuspicionResult Evaluate(string? path, SignatureInfo? signature, string? commandLine, FileMeta? meta)
    {
        if (path is not null && AppServices.Settings.IsTrusted(path))
            return SuspicionResult.Clean;

        var result = Suspicion.Analyze(path, signature, commandLine);
        var analyzedPath = path;
        var analyzedCommandLine = commandLine;

        // Signed programs routinely pass web addresses on their command line (browsers opening a link, crash reporters,
        // updaters). Only a script interpreter with a URL is a real signal, and that is reported separately.
        if (signature?.IsValid == true && result.Reasons.Contains(UrlReason) &&
            !result.Reasons.Any(r => r.StartsWith("Скрытый", StringComparison.Ordinal) ||
                                     r.StartsWith("Запуск через скриптовый", StringComparison.Ordinal)))
            analyzedCommandLine = null;

        // Genuine Microsoft binaries outside C:\Windows (Defender in ProgramData, Office, Edge...) are not imitations
        // of system files. A copy of a Microsoft file in Temp/AppData is still analysed with its location.
        if (signature?.IsMicrosoft == true && path is not null && MicrosoftSoftwareRoots.Any(r => Suspicion.IsUnder(path, r)))
            analyzedPath = null;

        if (!ReferenceEquals(analyzedPath, path) || !ReferenceEquals(analyzedCommandLine, commandLine))
            result = Suspicion.Analyze(analyzedPath, signature, analyzedCommandLine);

        if (signature?.IsMicrosoft == true || meta is null)
            return result;
        var pup = KnownPups.Match(meta.Description ?? meta.Product, meta.Company);
        if (pup is null || result.Reasons.Contains(pup))
            return result;
        var score = Math.Clamp(result.Score + (signature?.IsValid == true ? 10 : 35), 0, 100);
        return new SuspicionResult(score, result.Reasons.Append(pup).ToList());
    }

    // ---------------------------------------------------------------- actions

    /// <summary>
    /// Terminates the given processes (Process.Kill). The caller must have confirmed with the user.
    /// Protected processes and the cleaner itself are refused here too, as a second line of defence.
    /// </summary>
    internal KillReport Kill(IReadOnlyList<KillTarget> targets)
    {
        var report = new KillReport();
        foreach (var t in targets)
        {
            var (verdict, _) = ProcessGuard.Check(t.Pid, t.Name, t.Path);
            if (verdict == KillVerdict.Blocked)
            {
                report.Failed.Add((t, "защищённый системный процесс"));
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(t.Pid);
                if (!IsSameProcess(process, t.CreateTime))
                {
                    report.AlreadyExited.Add(t);
                    continue;
                }
                process.Kill();
                try
                {
                    process.WaitForExit(5000);
                }
                catch (SystemException)
                {
                    // the kill itself succeeded; waiting is best effort
                }
                report.Killed.Add(t);
                Log.Action($"Завершён процесс {t.Name} (PID {t.Pid})");
            }
            catch (ArgumentException)
            {
                report.AlreadyExited.Add(t); // no process with this PID any more
            }
            catch (InvalidOperationException)
            {
                report.AlreadyExited.Add(t);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied)
            {
                report.AccessDenied = true;
                report.Failed.Add((t, "нет доступа"));
                Log.Warn($"Не удалось завершить {t.Name} (PID {t.Pid}): нет доступа");
            }
            catch (Exception ex) when (ex is Win32Exception or NotSupportedException or UnauthorizedAccessException)
            {
                report.Failed.Add((t, ex.Message));
                Log.Warn($"Не удалось завершить {t.Name} (PID {t.Pid}): {ex.Message}");
            }
        }
        return report;
    }

    private static bool IsSameProcess(Process process, long expectedCreateTime)
    {
        if (expectedCreateTime <= 0)
            return true;
        try
        {
            return Math.Abs(process.StartTime.ToFileTime() - expectedCreateTime) < TimeSpan.TicksPerSecond;
        }
        catch (Win32Exception)
        {
            return true; // can't verify without rights; Kill will be refused the same way
        }
        catch (InvalidOperationException)
        {
            return false; // exited
        }
    }

    /// <summary>
    /// Terminates the program's processes and moves its executable to quarantine. Caller must have confirmed.
    /// Refuses files in the Windows folder and Microsoft-signed files.
    /// </summary>
    internal async Task<QuarantineOutcome> QuarantineAsync(string path, IReadOnlyList<KillTarget> running, string reason,
        CancellationToken ct)
    {
        var empty = new KillReport();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (Suspicion.IsUnder(path, windows))
            return new QuarantineOutcome(false, "Файлы из папки Windows нельзя помещать в карантин.", empty);
        var signature = await GetSignatureAsync(path).WaitAsync(ct).ConfigureAwait(false);
        if (signature.IsMicrosoft)
            return new QuarantineOutcome(false, "Файл подписан Microsoft — он не будет помещён в карантин.", empty);

        var report = await Task.Run(() => Kill(running), ct).ConfigureAwait(false);
        if (report.Failed.Count > 0)
        {
            var why = report.AccessDenied
                ? "Windows не дала завершить процесс — нужны права администратора."
                : "Не удалось завершить процесс: " + report.Failed[0].Error;
            return new QuarantineOutcome(false, why + " Файл оставлен на месте.", report);
        }

        Exception? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return new QuarantineOutcome(false, "Файл уже удалён или перемещён.", report);
                var item = Quarantine.Add(path, reason);
                return new QuarantineOutcome(true, $"Файл перемещён в карантин: {item.StoredPath}", report);
            }
            catch (FileNotFoundException)
            {
                return new QuarantineOutcome(false, "Файл уже удалён или перемещён.", report);
            }
            catch (IOException ex)
            {
                last = ex; // the file is still locked for a moment after the process exits
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            {
                last = ex;
                break;
            }
        }
        Log.Warn($"Не удалось поместить в карантин {path}: {last?.Message}");
        var hint = last is UnauthorizedAccessException && !Admin.IsElevated ? " Попробуйте запустить программу от имени администратора." : "";
        return new QuarantineOutcome(false, $"Процесс завершён, но файл переместить не удалось: {last?.Message}{hint}", report);
    }

    /// <summary>Adds/removes a path from the user's trusted list (shared by all modules).</summary>
    internal static void SetTrusted(string path, bool trusted)
    {
        var settings = AppServices.Settings;
        // A new list is assigned instead of mutating the old one: background analyses may be reading it right now.
        var list = settings.TrustedItems.Where(t => !string.Equals(t, path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (trusted)
            list.Add(path);
        settings.TrustedItems = list;
        settings.Save();
        Log.Action(trusted ? $"Добавлено в доверенные: {path}" : $"Удалено из доверенных: {path}");
    }

    // ---------------------------------------------------------------- dashboard analysis

    /// <summary>
    /// Read-only analysis for the dashboard: suspicious processes, low free memory, and programs that keep the CPU busy
    /// over a 2-second sample.
    /// </summary>
    public async Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
    {
        var sampler = new CpuSampler();
        await Task.Run(() => TakeSnapshot(sampler), ct).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        var samples = await Task.Run(() => TakeSnapshot(sampler), ct).ConfigureAwait(false);

        await LoadDetailsAsync(samples.Select(s => s.Info).ToList(), ct).ConfigureAwait(false);

        var paths = samples.Select(s => s.Info.Path).Where(p => p is not null).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        await Task.WhenAll(paths.Select(p => GetSignatureAsync(p!))).WaitAsync(ct).ConfigureAwait(false);

        var evaluated = samples.Select(s =>
        {
            var signature = TryGetSignature(s.Info.Path);
            return new ProcessIssues.Evaluated(s, Evaluate(s.Info.Path, signature, s.Info.CommandLine, s.Info.Meta));
        }).ToList();
        return ProcessIssues.Build(evaluated, SystemStats.Memory(), Environment.ProcessId);
    }
}

/// <summary>Builds the dashboard findings from an evaluated snapshot (pure, testable).</summary>
internal static class ProcessIssues
{
    internal sealed record Evaluated(ProcessSample Sample, SuspicionResult Suspicion);

    public const double HighCpuPercent = 50;
    public const uint LowMemoryLoadPercent = 85;
    private const string Module = "Процессы";
    private const string PageKey = "Processes";

    public static List<HealthIssue> Build(IReadOnlyList<Evaluated> items, MemoryStatus memory, int selfPid)
    {
        var issues = new List<HealthIssue>();
        var keys = ProcessGrouping.AssignKeys(items, e => e.Sample.Info.Name, e => e.Sample.Info.Path);
        var programs = items.GroupBy(e => keys[e]).ToList();

        // 1. Suspicious programs (one issue per executable, not per instance).
        foreach (var program in programs)
        {
            var suspicious = program.Where(e => e.Suspicion.IsSuspicious && e.Sample.Pid != selfPid).ToList();
            if (suspicious.Count == 0)
                continue;
            var worst = suspicious.MaxBy(e => e.Suspicion.Score)!;
            var info = worst.Sample.Info;
            var reasons = suspicious.SelectMany(e => e.Suspicion.Reasons).Distinct().ToList();
            var lines = new List<string> { info.Path ?? info.Name };
            lines.AddRange(reasons.Select(r => "• " + r));
            var total = program.Count();
            if (total > 1)
                lines.Add(suspicious.Count == total
                    ? $"Запущено процессов: {total}."
                    : $"Подозрительных процессов: {suspicious.Count} из {total}.");
            lines.Add("Это подсказка, а не приговор: в разделе «Процессы» файл можно проверить на VirusTotal или Защитником Windows.");
            issues.Add(new HealthIssue
            {
                Module = Module,
                Title = $"Подозрительный процесс: {info.FileName}",
                Details = string.Join("\n", lines),
                Severity = worst.Suspicion.Level == SuspicionLevel.High ? IssueSeverity.High : IssueSeverity.Medium,
                TargetPageKey = PageKey,
            });
        }

        // 2. Low free memory: name the biggest consumers.
        if (memory.TotalBytes > 0 && memory.LoadPercent > LowMemoryLoadPercent)
        {
            var top = programs
                .Select(g => (Name: DisplayName(g), Memory: g.Sum(e => e.Sample.MemoryBytes), Count: g.Count()))
                .OrderByDescending(x => x.Memory)
                .Take(5)
                .Select(x => x.Count > 1
                    ? $"• {x.Name} — {Format.Bytes(x.Memory)} ({Format.Count(x.Count, "процесс", "процесса", "процессов")})"
                    : $"• {x.Name} — {Format.Bytes(x.Memory)}");
            issues.Add(new HealthIssue
            {
                Module = Module,
                Title = "Мало свободной оперативной памяти",
                Details = $"Занято {Format.Bytes(memory.UsedBytes)} из {Format.Bytes(memory.TotalBytes)} ({memory.LoadPercent}%). " +
                          "Из-за этого Windows начинает использовать медленный файл подкачки.\nБольше всего памяти занимают:\n" +
                          string.Join("\n", top) +
                          "\nЗакройте ненужные программы и вкладки браузера.",
                Severity = IssueSeverity.Medium,
                TargetPageKey = PageKey,
            });
        }

        // 3. Programs keeping the processor busy.
        foreach (var program in programs)
        {
            if (program.Any(e => e.Sample.Pid == selfPid))
                continue; // our own scan is not the user's problem
            var cpu = Math.Min(100, program.Sum(e => e.Sample.CpuPercent));
            if (cpu <= HighCpuPercent)
                continue;
            var name = DisplayName(program);
            issues.Add(new HealthIssue
            {
                Module = Module,
                Title = $"Высокая нагрузка на процессор: {name}",
                Details = $"За 2 секунды «{name}» занял {Format.Percent(cpu)} мощности процессора. " +
                          "Если сейчас не идёт игра, обработка видео или архивация — возможно, программа зависла " +
                          "или без нужды работает в фоне. Её можно завершить в разделе «Процессы».",
                Severity = IssueSeverity.Low,
                TargetPageKey = PageKey,
            });
        }

        return issues.OrderByDescending(i => i.Severity).ToList();
    }

    private static string DisplayName(IEnumerable<Evaluated> program)
    {
        var info = program.First().Sample.Info;
        var description = info.Meta?.Description ?? KnownWindowsProcesses.Describe(info.Name);
        return string.IsNullOrWhiteSpace(description) || string.Equals(description, info.FileName, StringComparison.OrdinalIgnoreCase)
            ? info.FileName
            : $"{description} ({info.FileName})";
    }
}
