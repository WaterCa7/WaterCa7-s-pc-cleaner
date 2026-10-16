using System.IO;
using PcCleaner.Core;

namespace PcCleaner.Modules.Network;

/// <summary>
/// Network module: read-only measurements (adapters, latency, DNS, Wi-Fi, connections, ports) for the page and the
/// dashboard, plus the few explicit tools (flush DNS cache, reset network settings) that the page runs after confirmation.
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

    public Task<DnsReport> MeasureDnsAsync(CancellationToken ct) => NetworkProbes.MeasureDnsAsync(ct);

    public Task<WifiReport> ReadWifiAsync(IReadOnlyList<AdapterInfo> adapters, CancellationToken ct)
        => NetworkProbes.ReadWifiAsync(adapters, ct);

    public TrafficSample SampleTraffic() => NetworkProbes.SampleTraffic();

    /// <summary>
    /// Read-only analysis (≈ 3–12 s): adapters, a 2-second traffic sample, latency, DNS, Wi-Fi and the socket tables,
    /// all in parallel. Parts that fail are skipped with a warning.
    /// </summary>
    public async Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(AnalysisBudget);
        var token = budget.Token;

        var adapters = await Safe("адаптеры", () => GetAdaptersAsync(token), token).ConfigureAwait(false) ?? new List<AdapterInfo>();
        var upload = Safe("трафик", () => NetworkProbes.MeasureUploadAsync(adapters, TimeSpan.FromSeconds(2), token), token);
        var latency = Safe("задержка", () => MeasureLatencyAsync(adapters, token), token);
        var dns = Safe("DNS", () => MeasureDnsAsync(token), token);
        var wifi = Safe("Wi-Fi", () => ReadWifiAsync(adapters, token), token);
        var sockets = Safe("соединения", () => ReadSocketsAsync(token), token);
        await Task.WhenAll(upload, latency, dns, wifi, sockets).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var snapshot = new NetworkSnapshot
        {
            Adapters = adapters,
            Latency = latency.Result,
            Dns = dns.Result,
            Wifi = wifi.Result,
            Sockets = sockets.Result,
            UploadBytesPerSecond = upload.Result,
        };
        return NetworkRules.BuildIssues(snapshot);
    }

    /// <summary>Runs one measurement; a failure or timeout of one part never breaks the others.</summary>
    internal static async Task<T?> Safe<T>(string what, Func<Task<T>> work, CancellationToken ct) where T : class
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
            Log.Warn($"Сеть: проверка «{what}» не удалась: {ex.Message}");
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
            Log.Warn($"Сеть: проверка «{what}» не удалась: {ex.Message}");
            return null;
        }
    }

    // ===================================================================== tools (change the system; the page confirms first)

    /// <summary>ipconfig /flushdns. Returns a result message for the user.</summary>
    public async Task<(bool Ok, string Message)> FlushDnsAsync(CancellationToken ct)
    {
        var ipconfig = Path.Combine(Environment.SystemDirectory, "ipconfig.exe");
        var result = await ProcessRunner.RunAsync(ipconfig, "/flushdns", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (result.Success)
        {
            Log.Action("Сеть: очищен кэш DNS (ipconfig /flushdns)");
            return (true, "Кэш DNS очищен. Сайты, которые не открывались после смены адреса или DNS, должны заработать.");
        }
        var reason = FirstLine(result.StdOut + "\n" + result.StdErr) ?? $"код {result.ExitCode}";
        Log.Warn($"Сеть: не удалось очистить кэш DNS: {reason}");
        return (false, $"Не удалось очистить кэш DNS: {reason}");
    }

    /// <summary>
    /// netsh winsock reset + netsh int ip reset (admin only; the page creates a restore point first).
    /// Returns a result message; nothing is changed without admin rights.
    /// </summary>
    public async Task<(bool Ok, string Message)> ResetNetworkAsync(CancellationToken ct)
    {
        if (!Admin.IsElevated)
            return (false, "Для сброса сетевых настроек нужны права администратора — ничего не изменено.");

        var netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
        var steps = new (string Args, string Title)[]
        {
            ("winsock reset", "каталог Winsock сброшен (netsh winsock reset)"),
            ("int ip reset", "параметры TCP/IP сброшены (netsh int ip reset)"),
        };
        var done = new List<string>();
        foreach (var (args, title) in steps)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ProcessRunner.RunAsync(netsh, args, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            if (result.Success)
            {
                Log.Action($"Сеть: {title}");
                done.Add(title);
            }
            else
            {
                var reason = FirstLine(result.StdOut + "\n" + result.StdErr) ?? $"код {result.ExitCode}";
                Log.Warn($"Сеть: netsh {args} завершился с ошибкой: {reason}");
                var prefix = done.Count > 0 ? "Выполнено: " + string.Join("; ", done) + ". " : "";
                return (false, $"{prefix}Не удалось выполнить «netsh {args}»: {reason}");
            }
        }
        return (true, "Сетевые настройки сброшены: " + string.Join("; ", done) +
                      ". Перезагрузите компьютер, чтобы изменения вступили в силу. Если вы пользовались статическим IP-адресом, " +
                      "своим DNS или VPN-программой, проверьте их настройки после перезагрузки.");
    }

    private static string? FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
