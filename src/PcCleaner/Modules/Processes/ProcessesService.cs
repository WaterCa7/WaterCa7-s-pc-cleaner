using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

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
    private readonly Func<string, string, QuarantineItem> _addToQuarantine;
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
        TimeSpan? analysisInterval = null,
        Func<string, string, QuarantineItem>? addToQuarantine = null)
    {
        _source = source;
        _details = details;
        _signatureProvider = signatures ?? FileSignature.Get;
        _fileMetaProvider = fileMeta ?? ReadFileMeta;
        _canMoveFile = canMoveFile ?? FileMoveProbe.CanMove;
        _addToQuarantine = addToQuarantine ?? Quarantine.Add;
        Clock = clock ?? Stopwatch.GetTimestamp;
        LogicalCpus = logicalCpus ?? Environment.ProcessorCount;
        SelfPid = selfPid ?? Environment.ProcessId;
        Memory = memory ?? SystemStats.Memory;
        AnalysisInterval = analysisInterval ?? TimeSpan.FromSeconds(1.5);
    }

    public string ModuleName => T._("Процессы");

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
            Log.Warn(T.F("Процессы: не удалось получить командные строки через WMI: {0}", ErrorText.For(ex)), ex);
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
            Log.Warn(T.F("Процессы: не удалось проверить подпись {0}: {1}", path, ErrorText.For(ex)), ex);
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
        return AddPupMatch(result, pup, signature);
    }

    /// <summary>Highest score of an informational finding: one point below <see cref="SuspicionLevel.Medium"/>.</summary>
    internal const int BelowMediumScore = 34;

    /// <summary>
    /// Adds a KnownPups match by the file's description or company. A validly signed file gets a small boost only, and when
    /// that match is the ONLY signal it stays informational (below Medium): the program is reported once on the dashboard,
    /// as an unwanted program by «Программы», not again as a suspicious process here. An unsigned file, or one with other
    /// signals, keeps the full boost.
    /// </summary>
    internal static SuspicionResult AddPupMatch(SuspicionResult result, string pup, SignatureInfo? signature)
    {
        var signed = signature?.IsValid == true;
        var score = Math.Clamp(result.Score + (signed ? 10 : 35), 0, 100);
        if (signed && result.Reasons.Count == 0)
            score = Math.Min(score, BelowMediumScore);
        return result with { Score = score, Reasons = result.Reasons.Append(pup).ToList(), Flags = result.Flags | SuspicionFlags.KnownPup };
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
                report.Failed.Add((t, T._("защищённый системный процесс")));
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
                Log.Action(T.F("Завершён процесс {0} (PID {1})", t.Name, t.Pid));
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
                report.Failed.Add((t, T._("нет доступа")));
                Log.Warn(T.F("Не удалось завершить {0} (PID {1}): нет доступа", t.Name, t.Pid));
            }
            catch (Exception ex) when (ex is Win32Exception or NotSupportedException or UnauthorizedAccessException)
            {
                report.Failed.Add((t, ErrorText.For(ex)));
                Log.Warn(T.F("Не удалось завершить {0} (PID {1}): {2}", t.Name, t.Pid, ErrorText.For(ex)), ex);
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
            return new QuarantineOutcome(false, T._("Файлы из папки Windows нельзя помещать в карантин."), empty);
        var signature = await GetSignatureAsync(path).WaitAsync(ct).ConfigureAwait(false);
        if (signature.IsMicrosoft)
            return new QuarantineOutcome(false, T._("Файл подписан Microsoft — он не будет помещён в карантин."), empty);

        // Check the rights first: never terminate the program and then fail to move its file (e.g. in Program Files).
        if (!await Task.Run(() => _canMoveFile(path), ct).ConfigureAwait(false))
        {
            Log.Warn(T.F("Карантин: нет прав на перемещение {0}", path));
            return Admin.IsElevated
                ? new QuarantineOutcome(false, T._("Windows не разрешает перемещать этот файл. Ничего не изменено: процесс не завершён, файл на месте."), empty)
                : new QuarantineOutcome(false, T._("Чтобы переместить этот файл, нужны права администратора. Ничего не изменено: процесс не завершён, файл на месте."),
                    empty, NeedsElevation: true);
        }

        var report = await Task.Run(() => Kill(running), ct).ConfigureAwait(false);
        if (report.Failed.Count > 0)
            return new QuarantineOutcome(false, KillFailedMessage(report), report);

        Exception? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return new QuarantineOutcome(false, T._("Файл уже удалён или перемещён."), report);
                var item = _addToQuarantine(path, reason);
                return new QuarantineOutcome(true, T.F("Файл перемещён в карантин: {0}", item.StoredPath), report);
            }
            catch (FileNotFoundException)
            {
                return new QuarantineOutcome(false, T._("Файл уже удалён или перемещён."), report);
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
                    Log.Warn(T.F("Карантин отменён после завершения процессов: файл остался на месте ({0})", path));
                    return new QuarantineOutcome(false, CancelledAfterKillMessage(path, report), report, Cancelled: true);
                }
            }
            catch (InvalidOperationException ex)
            {
                // Quarantine.Add refuses to move the file when its list (index.json) cannot be read, and moves the file
                // back when the list cannot be written: retrying would not help.
                Log.Warn(T.F("Не удалось поместить в карантин {0}: {1}", path, ErrorText.For(ex)));
                return new QuarantineOutcome(false, QuarantineRefusedMessage(path, ex, report.Killed.Count > 0), report);
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
                break;
            }
        }
        Log.Warn(T.F("Не удалось поместить в карантин {0}: {1}", path, ErrorText.For(last)));
        return new QuarantineOutcome(false, MoveFailedMessage(path, last, Admin.IsElevated, report.Killed.Count > 0), report);
    }

    /// <summary>
    /// A process of the program could not be terminated, so the file was not touched. The reasons in
    /// <see cref="KillReport.Failed"/> are short phrases without a final period ("защищённый системный процесс",
    /// <see cref="ErrorText.For"/>), so the sentence gets its own.
    /// </summary>
    internal static string KillFailedMessage(KillReport report)
    {
        return report.AccessDenied
            ? T._("Windows не дала завершить процесс — нужны права администратора. Файл оставлен на месте.")
            : T.F("Не удалось завершить процесс: {0}. Файл оставлен на месте.", report.Failed[0].Error.Trim().TrimEnd('.'));
    }

    /// <summary>
    /// The file could not be moved (it stayed locked, or Windows refused access) — after the program's processes were
    /// terminated (<paramref name="killed"/>), or when nothing of it was running any more.
    /// </summary>
    internal static string MoveFailedMessage(string path, Exception? error, bool elevated, bool killed)
    {
        var hint = error is UnauthorizedAccessException && !elevated
            ? "\n\n" + T._("Перезапустите программу от имени администратора и повторите.")
            : "";
        var message = killed
            ? T.F("Программа завершена, но её файл не удалось переместить в карантин: {0}. Файл остался на месте:\n{1}", ErrorText.For(error), path)
            : T.F("Файл не удалось переместить в карантин: {0}. Файл остался на месте:\n{1}", ErrorText.For(error), path);
        return message + hint;
    }

    /// <summary>
    /// The quarantine refused the file (InvalidOperationException from Quarantine.Add), possibly after the program was
    /// terminated: the quarantine list (index.json) could not be read or written, so the file was left in place — otherwise
    /// it could never be restored. The quarantine's own explanation goes to the log; the user gets what it means and what to do.
    /// </summary>
    internal static string QuarantineRefusedMessage(string path, InvalidOperationException error, bool killed)
    {
        var what = killed ? T._("Программа завершена, но её файл не помещён в карантин:") : T._("Файл не помещён в карантин:");
        if (error.InnerException is null)
        {
            // Any other refusal (e.g. "Системные файлы Windows нельзя помещать в карантин."): the quarantine list is fine,
            // so there is nothing to check in «Настройки» — the reason the quarantine gave (in the language of the UI) says it all.
            return $"{what} {LowerFirst(ErrorText.For(error))}.\n\n{path}";
        }
        return $"{what} " + T.F("не удалось прочитать или записать список карантина (файл index.json в папке карантина). Файл оставлен на месте, иначе его нельзя было бы потом восстановить.\n\n{0}\n\nОткройте «Настройки» → «Карантин», чтобы проверить список карантина, и повторите. Подробности — в журнале.", path);
    }

    private static string LowerFirst(string text)
        => !T.IsEnglish && text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) // English phrases of ErrorText are lower-case already
            ? char.ToLower(text[0], Format.Ru) + text[1..]
            : text;

    /// <summary>What "Отмена" did once the program's processes were already terminated.</summary>
    internal static string CancelledAfterKillMessage(string path, KillReport report)
    {
        var closed = report.Killed.Count > 0
            ? T.F("Операция отменена. Программа уже завершена ({0}), но её файл не перемещён в карантин и остался на месте:\n{1}",
                Format.Count(report.Killed.Count, "процесс", "процесса", "процессов"), path)
            : T.F("Операция отменена. Файл не перемещён в карантин и остался на месте:\n{0}", path);
        return closed + "\n\n" + T._("Если программа запустится снова, её можно будет поместить в карантин здесь же.");
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

    private static string Module => T._("Процессы");
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
                    ? T.F("Запущено процессов: {0}.", total)
                    : T.F("Подозрительных процессов: {0} из {1}.", suspicious.Count, total));
            lines.Add(T._("Это подсказка, а не приговор: в разделе «Процессы» файл можно проверить на VirusTotal или Защитником Windows."));
            issues.Add(new HealthIssue
            {
                Module = Module,
                Title = T.F("Подозрительный процесс: {0}", info.FileName),
                Details = string.Join("\n", lines),
                Severity = worst.Suspicion.Level == SuspicionLevel.High ? IssueSeverity.High : IssueSeverity.Medium,
                TargetPageKey = PageKey,
            });
        }

        // 2. Low free memory: name the biggest consumers. "85% or more", the same boundary as the «Ускорение» page's RAM
        // item: this module alone reports high RAM load on the dashboard (Optimizer keeps it page-only).
        if (memory.TotalBytes > 0 && memory.LoadPercent >= LowMemoryLoadPercent)
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
                Title = T._("Мало свободной оперативной памяти"),
                Details = T.F("Занято {0} из {1} ({2}%). Из-за этого Windows начинает использовать медленный файл подкачки.\nБольше всего памяти занимают:\n{3}\nЗакройте ненужные программы и вкладки браузера.",
                    Format.Bytes(memory.UsedBytes), Format.Bytes(memory.TotalBytes), memory.LoadPercent, string.Join("\n", top)),
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
                ? T._("Это системный процесс Windows, завершать его нельзя. Обычно такая нагрузка спадает сама (например, после проверки Защитником или установки обновлений); если она держится часами — перезагрузите компьютер.")
                : T._("Если сейчас не идёт игра, обработка видео или архивация — возможно, программа зависла или без нужды работает в фоне. Её можно завершить в разделе «Процессы».");
            var seconds = Math.Max(1, (int)Math.Round(cpuSeconds));
            issues.Add(new HealthIssue
            {
                Module = Module,
                Title = T.F("Высокая нагрузка на процессор: {0}", name),
                Details = T.F("За {0} {1} {2} в среднем занимал {3} мощности процессора.",
                    seconds, Format.Plural(seconds, "секунду", "секунды", "секунд"), T.Quote(name), Format.Percent(cpu)) + " " + advice,
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
            Title = T.F("{0} занимает {1} памяти ({2} от всей)", ShortName(program), Format.Bytes(bytes), percent),
            Details = T.F("{0}{1} занимает {2} из {3} оперативной памяти — другим программам остаётся меньше, и компьютер может начать тормозить.\n• Если программа сейчас не нужна — закройте её.\n• Если нужна, но занимает всё больше памяти, — перезапустите её: так она вернёт память, которую со временем перестала освобождать.",
                T.Quote(DisplayName(program)), copies, Format.Bytes(bytes), Format.Bytes(memory.TotalBytes)),
            Severity = IssueSeverity.Low,
            TargetPageKey = PageKey,
        };
    }

    private static HealthIssue ManyCopiesIssue(List<Evaluated> program, long bytes, double share, MemoryStatus memory)
    {
        var n = program.Count;
        var fileName = program[0].Sample.Info.FileName;
        var isBrowser = Browsers.Contains(fileName);
        var details = T.F("{0} {1} вместе {2} {3} из {4} оперативной памяти ({5}).\n",
            Format.Count(n, "копия", "копии", "копий"), T.Quote(DisplayName(program)), Format.Plural(n, "занимает", "занимают", "занимают"),
            Format.Bytes(bytes), Format.Bytes(memory.TotalBytes), PercentText(share));
        details += isBrowser
            ? T._("Браузер запускает отдельный процесс для каждой вкладки и расширения.\n• Закройте вкладки, которые сейчас не нужны.\n• Включите в настройках браузера экономию памяти (раздел «Производительность»): неактивные вкладки будут «засыпать» и отдавать память.\n• Отключите расширения, которыми не пользуетесь.")
            : T._("Многие программы (например, мессенджеры и лаунчеры игр на основе браузера) запускают отдельный процесс для каждого окна, вкладки или задачи.\n• Закройте лишние окна и вкладки этой программы.\n• Если она давно запущена — перезапустите её.");
        return new HealthIssue
        {
            Module = Module,
            // "3 копии chrome.exe занимают 1,2 ГБ": the context keeps this placeholder-only key from catching any other one.
            Title = T.CtxF("copies", "{0} {1} {2} {3}", Format.Count(n, "копия", "копии", "копий"), fileName, Format.Plural(n, "занимает", "занимают", "занимают"),
                Format.Bytes(bytes)),
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
