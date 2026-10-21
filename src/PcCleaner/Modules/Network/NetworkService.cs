using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Network;

/// <summary>
/// Network module: read-only measurements (adapters, latency, DNS, Wi-Fi, connections, ports) for the page and the
/// dashboard, plus the few explicit tools (flush DNS cache, reset network settings) that the page runs after confirmation
/// (the dashboard's «Сбросить кэш DNS» button runs the first one after its own confirmation).
/// </summary>
public sealed class NetworkService : IHealthContributor
{
    private static readonly TimeSpan AnalysisBudget = TimeSpan.FromSeconds(14);

    public string ModuleName => NetworkRules.ModuleName;

    /// <summary>PID → process cache shared by the page and the analysis.</summary>
    public ProcessIdentities Processes { get; } = new();

    public Task<List<AdapterInfo>> GetAdaptersAsync(CancellationToken ct = default)
        => Task.Run(NetworkProbes.GetAdapters, ct);

    public Task<SocketSnapshot> ReadSocketsAsync(CancellationToken ct = default)
        => Task.Run(() => NetworkProbes.ReadSockets(Processes), ct);

    public Task<LatencyReport> MeasureLatencyAsync(IReadOnlyList<AdapterInfo> adapters, CancellationToken ct)
        => NetworkProbes.MeasureLatencyAsync(adapters, ct);

    public Task<DnsReport> MeasureDnsAsync(CancellationToken ct) => DnsProbe(ct);

    public Task<WifiReport> ReadWifiAsync(IReadOnlyList<AdapterInfo> adapters, CancellationToken ct)
        => NetworkProbes.ReadWifiAsync(adapters, ct);

    public TrafficSample SampleTraffic() => NetworkProbes.SampleTraffic();

    /// <summary>Windows Firewall state of the connected networks (WMI, read-only).</summary>
    public Task<FirewallReport> ReadFirewallAsync(CancellationToken ct = default) => Task.Run(NetworkProbes.ReadFirewall, ct);

    /// <summary>
    /// Read-only analysis (≈ 3–12 s): adapters, a 2-second traffic sample, latency, DNS, Wi-Fi, the socket tables and
    /// the firewall state, all in parallel. Parts that fail are skipped with a warning.
    /// </summary>
    public async Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(AnalysisBudget);
        var token = budget.Token;

        var adapters = await Safe(T._("адаптеры"), () => GetAdaptersAsync(token), token).ConfigureAwait(false) ?? new List<AdapterInfo>();
        var upload = Safe(T._("трафик"), () => NetworkProbes.MeasureUploadAsync(adapters, TimeSpan.FromSeconds(2), token), token);
        var latency = Safe(T._("задержка"), () => MeasureLatencyAsync(adapters, token), token);
        var dns = Safe("DNS", () => MeasureDnsAsync(token), token);
        var wifi = Safe("Wi-Fi", () => ReadWifiAsync(adapters, token), token);
        var sockets = Safe(T._("соединения"), () => ReadSocketsAsync(token), token);
        var firewall = Safe(T._("брандмауэр"), () => ReadFirewallAsync(token), token);
        await Task.WhenAll(upload, latency, dns, wifi, sockets, firewall).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var snapshot = new NetworkSnapshot
        {
            Adapters = adapters,
            Latency = latency.Result,
            Dns = dns.Result,
            Wifi = wifi.Result,
            Sockets = sockets.Result,
            UploadBytesPerSecond = upload.Result,
            Firewall = firewall.Result,
        };
        return BuildIssues(snapshot);
    }

    /// <summary>The dashboard issues of a snapshot; "DNS не отвечает" gets the «Сбросить кэш DNS» fix.</summary>
    internal IReadOnlyList<HealthIssue> BuildIssues(NetworkSnapshot snapshot) => NetworkRules.BuildIssues(snapshot, FlushDnsFixAsync);

    /// <summary>
    /// The dashboard fix of the DNS issue: <see cref="FlushDnsAsync"/>, then the same name lookups as the analysis. Clearing
    /// the cache only helps against stale entries; a DNS server that does not answer is still broken afterwards, and the
    /// row must not be marked fixed (and the score must not rise) then. Clearing the cache is harmless and repeatable, so
    /// there is no scan data to compare with the system first. If the check cannot say anything, the DNS counts as not restored.
    /// </summary>
    private async Task<string> FlushDnsFixAsync(CancellationToken ct)
    {
        var (ok, message) = await FlushDnsAsync(ct).ConfigureAwait(false);
        if (!ok)
            return message;

        var dns = await Safe("DNS", () => MeasureDnsAsync(ct), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); // "Отмена" during the check is a cancellation, not "DNS still broken"
        if (dns is { Lookups.Count: > 0, AllFailed: false })
        {
            Log.Action(T._("Сеть: после очистки кэша DNS адреса сайтов находятся"));
            return T._("Кэш DNS очищен — адреса сайтов снова находятся.");
        }
        Log.Warn(T._("Сеть: кэш DNS очищен, но адреса сайтов по-прежнему не находятся"));
        return T._("Не удалось восстановить DNS: кэш очищен, но адреса сайтов по-прежнему не находятся — смените DNS-сервер в настройках сети.");
    }

    /// <summary>The name lookups of <see cref="MeasureDnsAsync"/>. Tests replace it, so that no real lookup runs.</summary>
    internal Func<CancellationToken, Task<DnsReport>> DnsProbe { get; set; } = NetworkProbes.MeasureDnsAsync;

    /// <summary>Runs a console tool (ipconfig, netsh). Tests replace it, so that no real tool runs.</summary>
    internal Func<string, string, TimeSpan, CancellationToken, Task<(bool Ok, string Output, int ExitCode)>> RunTool { get; set; } = RunToolAsync;

    /// <summary>Runs one measurement; a failure or timeout of one part never breaks the others.</summary>
    internal static async Task<TResult?> Safe<TResult>(string what, Func<Task<TResult>> work, CancellationToken ct) where TResult : class
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException
                                       or System.Net.NetworkInformation.NetworkInformationException)
        {
            Log.Warn(T.F("Сеть: проверка «{0}» не удалась: {1}", what, ex.Message));
            return null;
        }
    }

    internal static async Task<double?> Safe(string what, Func<Task<double?>> work, CancellationToken ct)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.NetworkInformation.NetworkInformationException)
        {
            Log.Warn(T.F("Сеть: проверка «{0}» не удалась: {1}", what, ex.Message));
            return null;
        }
    }

    // ===================================================================== tools (change the system; the page confirms first)

    /// <summary>ipconfig /flushdns. Returns a result message for the user.</summary>
    public async Task<(bool Ok, string Message)> FlushDnsAsync(CancellationToken ct)
    {
        var ipconfig = Path.Combine(Environment.SystemDirectory, "ipconfig.exe");
        var (ok, output, exitCode) = await RunTool(ipconfig, "/flushdns", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (ok)
        {
            Log.Action(T._("Сеть: очищен кэш DNS (ipconfig /flushdns)"));
            return (true, T._("Кэш DNS очищен. Сайты, которые не открывались после смены адреса или DNS, должны заработать."));
        }
        var reason = FailureReason(output) ?? T.F("код {0}", exitCode);
        Log.Warn(T.F("Сеть: не удалось очистить кэш DNS: {0}", reason));
        return (false, T.F("Не удалось очистить кэш DNS: {0}", reason));
    }

    /// <summary>
    /// Runs a console tool and decodes its output: ipconfig and netsh write UTF-8 or the OEM code page depending on the
    /// console settings, so the raw bytes are kept (Latin-1) and decoded afterwards.
    /// </summary>
    private static async Task<(bool Ok, string Output, int ExitCode)> RunToolAsync(string exe, string args, TimeSpan timeout,
        CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(exe, args, timeout, ct, Encoding.Latin1).ConfigureAwait(false);
        var output = NetworkProbes.DecodeConsoleOutput(Encoding.Latin1.GetBytes(result.StdOut + "\n" + result.StdErr));
        return (result.Success, output, result.TimedOut ? -1 : result.ExitCode);
    }

    // l10n:data-begin — the words of the failure lines of ipconfig/netsh in the Windows language; matched and never shown
    private static readonly Regex FailureLine = new(
        @"fail|denied|error|could not|unable|cannot|ошиб|сбой|отказ|не удалось|не удается|не удаётся|невозможно",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // l10n:data-end

    /// <summary>
    /// The line that explains a failure ("Resetting , failed. Access is denied."), not the tool's header or progress
    /// lines; the last line if no line looks like an error.
    /// </summary>
    internal static string? FailureReason(string? output)
    {
        var lines = (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var failures = lines.Where(l => FailureLine.IsMatch(l)).Distinct().Take(2).ToList();
        return failures.Count > 0 ? string.Join(" ", failures) : lines.LastOrDefault();
    }

    /// <summary>
    /// netsh winsock reset + netsh int ip reset (admin only; the page creates a restore point first).
    /// Returns a result message; nothing is changed without admin rights.
    /// </summary>
    public async Task<(bool Ok, string Message)> ResetNetworkAsync(CancellationToken ct)
    {
        if (!Admin.IsElevated)
            return (false, T._("Для сброса сетевых настроек нужны права администратора — ничего не изменено."));

        var netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
        return await ResetStepsAsync((args, token) => RunTool(netsh, args, ResetStepTimeout, token), ct).ConfigureAwait(false);
    }

    /// <summary>Only a netsh that hangs this long is stopped (it normally finishes within seconds).</summary>
    private static readonly TimeSpan ResetStepTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Runs the reset commands one by one. A started netsh is never stopped by "Отмена": it rewrites the network
    /// configuration in the registry, and killing it halfway could leave that half-written (DISM and SMBv1 changes are
    /// guarded the same way). Cancelling only skips the commands that have not started yet, and the message then says what
    /// was already reset and that a reboot is needed. Cancelled before the first command → nothing changed, throws.
    /// </summary>
    internal static async Task<(bool Ok, string Message)> ResetStepsAsync(
        Func<string, CancellationToken, Task<(bool Ok, string Output, int ExitCode)>> run, CancellationToken ct)
    {
        var steps = new (string Args, string Title)[]
        {
            ("winsock reset", T._("каталог Winsock сброшен (netsh winsock reset)")),
            ("int ip reset", T._("параметры TCP/IP сброшены (netsh int ip reset)")),
        };
        var done = new List<string>();
        foreach (var (args, title) in steps)
        {
            if (ct.IsCancellationRequested)
            {
                if (done.Count == 0)
                    ct.ThrowIfCancellationRequested(); // nothing was changed
                Log.Action(T.F("Сеть: сброс сетевых настроек остановлен пользователем, выполнено: {0}", string.Join("; ", done)));
                return (false, T.F("Сброс остановлен по вашей команде. Выполнено: {0}. Команда «netsh {1}» не запускалась. Перезагрузите компьютер — уже сброшенные параметры начнут действовать после перезагрузки. Если после неё интернет не заработает, запустите сброс ещё раз.", string.Join("; ", done), args));
            }
            // CancellationToken.None: see above — a running netsh is always allowed to finish.
            var (ok, output, exitCode) = await run(args, CancellationToken.None).ConfigureAwait(false);
            if (ok)
            {
                Log.Action(T.F("Сеть: {0}", title));
                done.Add(title);
            }
            else
            {
                var reason = FailureReason(output) ?? T.F("код {0}", exitCode);
                Log.Warn(T.F("Сеть: netsh {0} завершился с ошибкой: {1}", args, reason));
                var message = T.F("Команда «netsh {0}» завершилась с ошибкой: {1}.", args, reason.TrimEnd('.'));
                if (done.Count > 0)
                {
                    // "netsh int ip reset" usually resets almost everything and fails on one protected setting; what was
                    // reset (and the Winsock reset before it) only takes effect after a reboot.
                    Log.Action(T.F("Сеть: сброс сетевых настроек выполнен не полностью (netsh {0}: {1})", args, reason));
                    message = T.F("Выполнено: {0}. {1} Перезагрузите компьютер — уже сброшенные параметры начнут действовать после перезагрузки. Если интернет не заработает, воспользуйтесь «Параметры» → «Сеть и Интернет» → «Дополнительные сетевые параметры» → «Сброс сети».", string.Join("; ", done), message);
                }
                return (false, message);
            }
        }
        return (true, T.F("Сетевые настройки сброшены: {0}. Перезагрузите компьютер, чтобы изменения вступили в силу. Если вы пользовались статическим IP-адресом, своим DNS или VPN-программой, проверьте их настройки после перезагрузки.", string.Join("; ", done)));
    }
}
