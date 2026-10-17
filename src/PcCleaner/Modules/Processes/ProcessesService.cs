using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PcCleaner.Core;

namespace PcCleaner.Modules.Processes;

/// <summary>Result of "В карантин" for a running program.</summary>
/// <param name="NeedsElevation">Nothing was done because moving the file needs administrator rights.</param>
/// <param name="Cancelled">The user cancelled after the processes had been terminated: the file was not moved.</param>
internal sealed record QuarantineOutcome(bool Success, string Message, KillReport Report, bool NeedsElevation = false,
    bool Cancelled = false);

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
    private readonly Func<string, bool> _canMoveFile;
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
        int? logicalCpus = null,
        Func<string, bool>? canMoveFile = null,
        int? selfPid = null,
        Func<MemoryStatus>? memory = null,
        TimeSpan? analysisInterval = null)
    {
        _source = source;
        _details = details;
        _signatureProvider = signatures ?? FileSignature.Get;
        _fileMetaProvider = fileMeta ?? ReadFileMeta;
        _canMoveFile = canMoveFile ?? FileMoveProbe.CanMove;
        Clock = clock ?? Stopwatch.GetTimestamp;
        LogicalCpus = logicalCpus ?? Environment.ProcessorCount;
        SelfPid = selfPid ?? Environment.ProcessId;
        Memory = memory ?? SystemStats.Memory;
        AnalysisInterval = analysisInterval ?? TimeSpan.FromSeconds(1.5);
    }

    public string ModuleName => "Процессы";

    /// <summary>Timestamp source in Stopwatch ticks (replaceable in tests).</summary>
    internal Func<long> Clock { get; }

    internal int LogicalCpus { get; }

    /// <summary>PID of WaterCa7s PC Cleaner itself (a fake one in tests): it and its helpers are never judged.</summary>
    internal int SelfPid { get; }

    internal Func<MemoryStatus> Memory { get; }

    /// <summary>Pause between the CPU measurements of the dashboard analysis.</summary>
    internal TimeSpan AnalysisInterval { get; }

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
        // A live refresh usually brings only a few new processes: ask WMI about those, not about the whole system.
        var filter = pids.Count <= WmiDetailsProvider.MaxFilteredPids ? pids : null;
        IReadOnlyList<WmiProcessRow> processRows;
        IReadOnlyList<WmiServiceRow> serviceRows;
        try
        {
            processRows = _details.QueryProcesses(filter);
            serviceRows = _details.QueryServices(filter);
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

    /// <summary>
    /// Heuristic verdict for a process: <see cref="Suspicion.Analyze"/> (which already knows that Microsoft files in
    /// Microsoft's folders are genuine and that signed programs may pass web addresses), plus two process-only checks:
    /// the file's description/company against known potentially unwanted programs, and a sustained full processor load
    /// of an unusual file (possible hidden miner). Respects the user's trusted list.
    /// </summary>
    /// <param name="sustainedHighCpu">The process kept the processor busy over several measurements (see <see cref="CpuStreaks"/>).</param>
    internal static SuspicionResult Evaluate(string? path, SignatureInfo? signature, string? commandLine, FileMeta? meta,
        bool sustainedHighCpu = false)
    {
        // Core skips trusted paths too, but the extra checks below must not bring the warning back.
        if (path is not null && AppServices.Settings.IsTrusted(path))
            return SuspicionResult.Clean;

        var result = Suspicion.Analyze(path, signature, commandLine);
        result = MinerHeuristic.Apply(result, sustainedHighCpu, path, signature);

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
    /// Cancellation before the processes are terminated throws (nothing has changed). Once they are terminated it no
    /// longer throws: it stops waiting for the file to unlock and returns an outcome that says so, because the user must
    /// learn that the program was closed while its file stayed in place.
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

        // Check the rights first: never terminate the program and then fail to move its file (e.g. in Program Files).
        if (!await Task.Run(() => _canMoveFile(path), ct).ConfigureAwait(false))
        {
            Log.Warn($"Карантин: нет прав на перемещение {path}");
            return Admin.IsElevated
                ? new QuarantineOutcome(false, "Windows не разрешает перемещать этот файл. Ничего не изменено: процесс не завершён, файл на месте.", empty)
                : new QuarantineOutcome(false, "Чтобы переместить этот файл, нужны права администратора. Ничего не изменено: процесс не завершён, файл на месте.",
                    empty, NeedsElevation: true);
        }

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
                try
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Log.Warn($"Карантин отменён после завершения процессов: файл остался на месте ({path})");
                    return new QuarantineOutcome(false, CancelledAfterKillMessage(path, report), report, Cancelled: true);
                }
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

    /// <summary>What "Отмена" did once the program's processes were already terminated.</summary>
    internal static string CancelledAfterKillMessage(string path, KillReport report)
    {
        var closed = report.Killed.Count > 0
            ? $"Программа уже завершена ({Format.Count(report.Killed.Count, "процесс", "процесса", "процессов")}), но её файл " +
              "не перемещён в карантин"
            : "Файл не перемещён в карантин";
        return $"Операция отменена. {closed} и остался на месте:\n{path}\n\n" +
               "Если программа запустится снова, её можно будет поместить в карантин здесь же.";
    }

    /// <summary>Adds/removes a path from the user's trusted list (shared by all modules, thread-safe, logged by AppSettings).</summary>
    internal static void SetTrusted(string path, bool trusted)
    {
        if (trusted)
            AppServices.Settings.AddTrusted(path);
        else
            AppServices.Settings.RemoveTrusted(path);
    }

    /// <summary>Parent PID from the snapshot, or from WMI when the process list did not provide one.</summary>
    internal static int ParentOf(ProcessSample sample) => sample.Raw.ParentPid != 0 ? sample.Raw.ParentPid : sample.Info.ParentPid;

    // ---------------------------------------------------------------- dashboard analysis

    /// <summary>Number of CPU measurements taken by the dashboard analysis (a miner must stay busy in all of them).</summary>
    internal const int AnalysisMeasurements = MinerHeuristic.RequiredSamples;

    /// <summary>
    /// Read-only analysis for the dashboard: suspicious processes (including a sustained full load of an unusual file),
    /// low free memory, memory hogs and programs that keep the CPU busy. CPU is measured <see cref="AnalysisMeasurements"/>
    /// times, <see cref="AnalysisInterval"/> apart (about 3 seconds in total).
    /// </summary>
    public async Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
    {
        var sampler = new CpuSampler();
        var streaks = new CpuStreaks();
        var cpuTotals = new Dictionary<ProcessKey, double>();
        await Task.Run(() => TakeSnapshot(sampler), ct).ConfigureAwait(false);
        List<ProcessSample> samples = new();
        for (var i = 0; i < AnalysisMeasurements; i++)
        {
            await Task.Delay(AnalysisInterval, ct).ConfigureAwait(false);
            samples = await Task.Run(() => TakeSnapshot(sampler), ct).ConfigureAwait(false);
            streaks.Update(samples.Select(s => (s.Key, s.CpuPercent)));
            foreach (var s in samples)
                cpuTotals[s.Key] = cpuTotals.GetValueOrDefault(s.Key) + s.CpuPercent;
        }

        await LoadDetailsAsync(samples.Select(s => s.Info).ToList(), ct).ConfigureAwait(false);

        var paths = samples.Select(s => s.Info.Path).Where(p => p is not null).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        await Task.WhenAll(paths.Select(p => GetSignatureAsync(p!))).WaitAsync(ct).ConfigureAwait(false);

        var evaluated = samples.Select(s =>
        {
            var signature = TryGetSignature(s.Info.Path);
            // Average load over the whole analysis, not just the last moment.
            var averaged = s with { CpuPercent = cpuTotals.GetValueOrDefault(s.Key) / AnalysisMeasurements };
            var suspicion = Evaluate(s.Info.Path, signature, s.Info.CommandLine, s.Info.Meta, streaks.IsSustained(s.Key));
            return new ProcessIssues.Evaluated(averaged, suspicion);
        }).ToList();
        var seconds = AnalysisInterval.TotalSeconds * AnalysisMeasurements;
        return ProcessIssues.Build(evaluated, Memory(), SelfPid, seconds);
    }
}

/// <summary>Builds the dashboard findings from an evaluated snapshot (pure, testable).</summary>
internal static class ProcessIssues
{
    internal sealed record Evaluated(ProcessSample Sample, SuspicionResult Suspicion);

    public const double HighCpuPercent = 50;
    public const uint LowMemoryLoadPercent = 85;

    /// <summary>One program (fewer than <see cref="ManyCopies"/> processes) holding more than this share of RAM.</summary>
    public const double MemoryHogShare = 0.25;

    /// <summary>So many processes of one executable (browser tabs, Electron apps)...</summary>
    public const int ManyCopies = 25;

    /// <summary>...holding more than this share of RAM together.</summary>
    public const double ManyCopiesShare = 0.20;

    private const string Module = "Процессы";
    private const string PageKey = "Processes";

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe", "browser.exe", "yandex.exe",
        "chromium.exe", "librewolf.exe", "waterfox.exe", "floorp.exe", "thorium.exe", "arc.exe",
    };

    /// <param name="cpuSeconds">How long the CPU was measured (only for the wording of the high-load finding).</param>
    public static List<HealthIssue> Build(IReadOnlyList<Evaluated> items, MemoryStatus memory, int selfPid, double cpuSeconds = 3)
    {
        var issues = new List<HealthIssue>();
        var keys = ProcessGrouping.AssignKeys(items, e => e.Sample.Info.Name, e => e.Sample.Info.Path);

        // The cleaner and the helpers it starts for its own checks (powershell.exe, reg.exe...) are not the user's problem.
        var own = OwnProcesses.Find(items, selfPid, e => e.Sample.Pid, e => ProcessesService.ParentOf(e.Sample),
            e => e.Sample.Raw.CreateTime, e => e.Sample.Info.Name, e => e.Sample.Info.Path);
        var programs = items.GroupBy(e => keys[e]).ToList();

        // 1. Suspicious programs (one issue per executable, not per instance).
        foreach (var program in programs)
        {
            var suspicious = program.Where(e => e.Suspicion.IsSuspicious && !own.Contains(e)).ToList();
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

        // 3. Programs keeping the processor busy (our own helpers don't count).
        foreach (var program in programs)
        {
            var theirs = program.Where(e => !own.Contains(e)).ToList();
            if (theirs.Count == 0)
                continue;
            var cpu = Math.Min(100, theirs.Sum(e => e.Sample.CpuPercent));
            if (cpu <= HighCpuPercent)
                continue;
            var name = DisplayName(theirs);
            // System, Defender, svchost... can't be terminated from the Processes page: don't suggest it.
            var advice = IsProtected(theirs, selfPid)
                ? "Это системный процесс Windows, завершать его нельзя. Обычно такая нагрузка спадает сама " +
                  "(например, после проверки Защитником или установки обновлений); если она держится часами — перезагрузите компьютер."
                : "Если сейчас не идёт игра, обработка видео или архивация — возможно, программа зависла " +
                  "или без нужды работает в фоне. Её можно завершить в разделе «Процессы».";
            var seconds = Math.Max(1, (int)Math.Round(cpuSeconds));
            issues.Add(new HealthIssue
            {
                Module = Module,
                Title = $"Высокая нагрузка на процессор: {name}",
                Details = $"За {seconds} {Format.Plural(seconds, "секунду", "секунды", "секунд")} «{name}» " +
                          $"в среднем занимал {Format.Percent(cpu)} мощности процессора. " + advice,
                Severity = IssueSeverity.Low,
                TargetPageKey = PageKey,
            });
        }

        // 4. Memory hogs: one heavy program, or a swarm of copies of one executable (browser tabs).
        if (memory.TotalBytes > 0)
        {
            foreach (var program in programs)
            {
                var theirs = program.Where(e => !own.Contains(e)).ToList();
                if (theirs.Count == 0 || IsProtected(theirs, selfPid))
                    continue; // Windows itself: nothing the user can close
                var bytes = theirs.Sum(e => e.Sample.MemoryBytes);
                var share = (double)bytes / memory.TotalBytes;
                var issue = theirs.Count >= ManyCopies
                    ? share > ManyCopiesShare ? ManyCopiesIssue(theirs, bytes, share, memory) : null
                    : share > MemoryHogShare ? MemoryHogIssue(theirs, bytes, share, memory) : null;
                if (issue is not null)
                    issues.Add(issue);
            }
        }

        return issues.OrderByDescending(i => i.Severity).ToList();
    }

    private static HealthIssue MemoryHogIssue(List<Evaluated> program, long bytes, double share, MemoryStatus memory)
    {
        var percent = PercentText(share);
        var copies = program.Count > 1 ? $" ({Format.Count(program.Count, "процесс", "процесса", "процессов")})" : "";
        return new HealthIssue
        {
            Module = Module,
            Title = $"{ShortName(program)} занимает {Format.Bytes(bytes)} памяти ({percent} от всей)",
            Details = $"«{DisplayName(program)}»{copies} занимает {Format.Bytes(bytes)} из {Format.Bytes(memory.TotalBytes)} " +
                      "оперативной памяти — другим программам остаётся меньше, и компьютер может начать тормозить.\n" +
                      "• Если программа сейчас не нужна — закройте её.\n" +
                      "• Если нужна, но занимает всё больше памяти, — перезапустите её: так она вернёт память, " +
                      "которую со временем перестала освобождать.",
            Severity = IssueSeverity.Low,
            TargetPageKey = PageKey,
        };
    }

    private static HealthIssue ManyCopiesIssue(List<Evaluated> program, long bytes, double share, MemoryStatus memory)
    {
        var n = program.Count;
        var fileName = program[0].Sample.Info.FileName;
        var isBrowser = Browsers.Contains(fileName);
        var details = $"{Format.Count(n, "копия", "копии", "копий")} «{DisplayName(program)}» вместе " +
                      $"{Format.Plural(n, "занимает", "занимают", "занимают")} {Format.Bytes(bytes)} " +
                      $"из {Format.Bytes(memory.TotalBytes)} оперативной памяти ({PercentText(share)}).\n";
        details += isBrowser
            ? "Браузер запускает отдельный процесс для каждой вкладки и расширения.\n" +
              "• Закройте вкладки, которые сейчас не нужны.\n" +
              "• Включите в настройках браузера экономию памяти (раздел «Производительность»): неактивные вкладки будут " +
              "«засыпать» и отдавать память.\n" +
              "• Отключите расширения, которыми не пользуетесь."
            : "Многие программы (например, мессенджеры и лаунчеры игр на основе браузера) запускают отдельный процесс " +
              "для каждого окна, вкладки или задачи.\n" +
              "• Закройте лишние окна и вкладки этой программы.\n" +
              "• Если она давно запущена — перезапустите её.";
        return new HealthIssue
        {
            Module = Module,
            Title = $"{Format.Count(n, "копия", "копии", "копий")} {fileName} {Format.Plural(n, "занимает", "занимают", "занимают")} " +
                    Format.Bytes(bytes),
            Details = details,
            Severity = IssueSeverity.Low,
            TargetPageKey = PageKey,
        };
    }

    private static bool IsProtected(IEnumerable<Evaluated> program, int selfPid)
        => program.Any(e => ProcessGuard.Check(e.Sample.Pid, e.Sample.Info.FileName, e.Sample.Info.Path, selfPid).Verdict ==
                            KillVerdict.Blocked);

    /// <summary>"27%" (whole percents read better in a title).</summary>
    private static string PercentText(double share) => $"{Math.Round(share * 100):0}%";

    private static string? Description(ProcessStaticInfo info)
    {
        var description = info.Meta?.Description ?? KnownWindowsProcesses.Describe(info.Name);
        return string.IsNullOrWhiteSpace(description) || string.Equals(description, info.FileName, StringComparison.OrdinalIgnoreCase)
            ? null
            : description;
    }

    /// <summary>"Google Chrome (chrome.exe)" or just the file name.</summary>
    private static string DisplayName(IEnumerable<Evaluated> program)
    {
        var info = program.First().Sample.Info;
        return Description(info) is { } description ? $"{description} ({info.FileName})" : info.FileName;
    }

    /// <summary>"Google Chrome" or the file name.</summary>
    private static string ShortName(IEnumerable<Evaluated> program)
    {
        var info = program.First().Sample.Info;
        return Description(info) ?? info.FileName;
    }
}
