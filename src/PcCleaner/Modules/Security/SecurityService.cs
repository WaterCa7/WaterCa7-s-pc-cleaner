using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Security;

/// <summary>Outcome of an action started from the page or the dashboard.</summary>
public sealed record SecurityActionResult(bool Success, string Message, bool ThreatsFound = false, bool Cancelled = false)
{
    public static SecurityActionResult Ok(string message) => new(true, message);

    public static SecurityActionResult Fail(string message) => new(false, message);
}

/// <summary>
/// Security checks: antivirus, firewall, UAC, hosts, Winlogon, IFEO, proxy, DNS, updates, SmartScreen, RDP,
/// browser extensions, SMBv1, Secure Boot, TPM, drive encryption and memory integrity. <see cref="ScanAsync"/> only
/// reads; the action methods change the system and must be confirmed by the caller (the page asks with
/// Dialogs.Confirm, the dashboard confirms before FixAsync).
/// </summary>
public sealed partial class SecurityService : IHealthContributor
{
    public const string NeedAdminMessage =
        "Для этого нужны права администратора. Перезапустите программу от имени администратора " +
        "(кнопка внизу слева) и повторите — пока ничего не изменено.";

    private const string BackupFailedMessage =
        "Не удалось сохранить резервную копию реестра, поэтому изменения отменены. Ничего не изменено.";

    // Results of successful dashboard fixes (tests check that the dashboard reads them as fixed).
    internal const string SignaturesUpdatedMessage = "Антивирусные базы Защитника Windows обновлены.";
    internal const string RealtimeEnabledMessage = "Защита в реальном времени включена.";
    internal const string FirewallEnabledMessage =
        "Брандмауэр включён для всех типов сетей. Выключить его снова можно в «Безопасность Windows» → «Брандмауэр и защита сети».";

    public SecurityService() : this(SecurityEnvironment.CreateDefault()) { }

    internal SecurityService(SecurityEnvironment environment)
    {
        Env = environment;
    }

    internal SecurityEnvironment Env { get; }

    public string ModuleName => "Безопасность";

    /// <summary>The most recent scan (from the page or the dashboard).</summary>
    public SecurityReport? LastReport { get; private set; }

    // =====================================================================
    // Read-only scan
    // =====================================================================

    /// <summary>Runs every check in parallel. Read-only, never throws for system access problems.</summary>
    public async Task<SecurityReport> ScanAsync(Action<double, string>? progress, CancellationToken ct)
    {
        var total = 14;
        var done = 0;

        Task<T> Run<T>(string name, Func<T> work, T fallback) => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            T result;
            try
            {
                result = work();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"Безопасность: проверка «{name}» не удалась: {ex.GetType().Name}: {ex.Message}");
                result = fallback;
            }
            var n = Interlocked.Increment(ref done);
            progress?.Invoke(n * 100.0 / total, $"Проверяем защиту: {name}…");
            return result;
        }, ct);

        var defenderTask = Run("антивирус", ReadDefender, new DefenderData());
        var firewallTask = Run("брандмауэр", ReadFirewallProfiles, new List<FirewallProfileState>());
        var registryTask = Run("настройки Windows", ReadRegistryChecks, new RegistryData());
        var hostsTask = Run("файл hosts", () => HostsFile.Read(Env.HostsPath), new HostsReport { Error = "Ошибка чтения" });
        var ifeoTask = Run("перехват запуска", ReadIfeo, (new List<IfeoEntry>(), false, false));
        var dnsTask = Run("DNS", ReadDns, (new List<DnsServerInfo>(), false));
        var updatesTask = Run("обновления", ReadUpdates, new UpdatesState(null, 0, null, null));
        // Fallback -1 = "could not read" (shown as "Нет данных", not as "browsers not found").
        var extTask = Run("расширения браузеров", () => BrowserExtensionScanner.Scan(Env.BrowserRoots, ct),
            (new List<BrowserExtension>(), -1));
        var mpTask = Run("Защитник Windows", () => Env.IsRealSystem && FileTools.FindMpCmdRun() is not null, false);
        var smb1Task = Run("SMBv1", () => ReadSmb1(ct), Smb1State.Unknown);
        var secureBootTask = Run("Secure Boot", ReadSecureBoot, new SecureBootState(false, null, null));
        var tpmTask = Run("TPM", () => ReadTpm(ct), TpmState.Unknown);
        var bitLockerTask = Run("шифрование диска", () => ReadBitLocker(ct), BitLockerState.Unknown);
        var deviceGuardTask = Run("изоляция ядра", ReadDeviceGuard, DeviceGuardState.Unknown);

        // WaitAsync: "Отмена" returns at once even if a WMI provider hangs (the stuck query finishes in the background).
        await Task.WhenAll(defenderTask, firewallTask, registryTask, hostsTask, ifeoTask, dnsTask, updatesTask, extTask, mpTask,
                smb1Task, secureBootTask, tpmTask, bitLockerTask, deviceGuardTask)
            .WaitAsync(ct);

        var defender = defenderTask.Result;
        var reg = registryTask.Result;
        var (ifeo, ifeoPartial, ifeoReadable) = ifeoTask.Result;
        var (dns, dnsReadable) = dnsTask.Result;
        var (extensions, browsers) = extTask.Result;
        var profiles = firewallTask.Result;
        var hosts = hostsTask.Result;
        var updates = updatesTask.Result;
        var isAdmin = Env.IsRealSystem && Admin.IsElevated;

        var evaluated = new[]
        {
            SecurityRules.EvaluateAntivirus(defender.Status, defender.Antiviruses, defender.Threats),
            SecurityRules.EvaluateFirewall(profiles, defender.Firewalls),
            SecurityRules.EvaluateUac(reg.Uac),
            SecurityRules.EvaluateHosts(hosts),
            SecurityRules.EvaluateWinlogon(reg.Winlogon, Env.WindowsDir),
            SecurityRules.EvaluateIfeo(ifeo, ifeoPartial, ifeoReadable),
            SecurityRules.EvaluateProxy(reg.Proxy),
            SecurityRules.EvaluateDns(dns, dnsReadable),
            SecurityRules.EvaluateUpdates(updates, DateTime.Now),
            SecurityRules.EvaluateSmartScreen(reg.SmartScreen),
            SecurityRules.EvaluateRdp(reg.Rdp),
            SecurityRules.EvaluateExtensions(extensions, browsers),
            SecurityRules.EvaluateSmb1(smb1Task.Result, isAdmin, Smb1RebootPending),
            SecurityRules.EvaluateSecureBoot(secureBootTask.Result),
            SecurityRules.EvaluateTpm(tpmTask.Result, isAdmin),
            SecurityRules.EvaluateBitLocker(bitLockerTask.Result),
            SecurityRules.EvaluateMemoryIntegrity(deviceGuardTask.Result),
        };
        // Keep the report in the order of importance (SecurityCheckIds.All).
        var checks = evaluated.OrderBy(c => SecurityCheckIds.Importance(c.Id)).ToList();

        var report = new SecurityReport
        {
            ScannedAt = DateTime.Now,
            Defender = defender.Status,
            Antiviruses = defender.Antiviruses,
            Firewalls = defender.Firewalls,
            Threats = defender.Threats,
            FirewallProfiles = profiles,
            Uac = reg.Uac,
            Hosts = hosts,
            Winlogon = reg.Winlogon,
            Ifeo = ifeo,
            IfeoPartial = ifeoPartial,
            IfeoReadable = ifeoReadable,
            Proxy = reg.Proxy,
            Dns = dns,
            DnsReadable = dnsReadable,
            Updates = updates,
            SmartScreen = reg.SmartScreen,
            Rdp = reg.Rdp,
            Extensions = extensions,
            BrowsersFound = Math.Max(0, browsers),
            MpCmdRunAvailable = mpTask.Result,
            Smb1 = smb1Task.Result,
            SecureBoot = secureBootTask.Result,
            Tpm = tpmTask.Result,
            BitLocker = bitLockerTask.Result,
            DeviceGuard = deviceGuardTask.Result,
            Checks = checks,
        };
        LastReport = report;
        // The same split and numbers as the page header and the dashboard tile ("6 проблем и 1 совет").
        var (problems, tips) = CountFindings(report);
        Log.Info($"Безопасность: проверено {Format.Count(checks.Count, "параметр", "параметра", "параметров")} — " +
                 SecurityRules.FindingsText(problems, tips));
        return report;
    }

    /// <summary>
    /// Problems and tips of a report, counted exactly as the dashboard counts them: from <see cref="BuildIssues"/>,
    /// Info issues are tips, everything above is a problem. The page header and the log line use this too.
    /// </summary>
    internal (int Problems, int Tips) CountFindings(SecurityReport report)
    {
        var issues = BuildIssues(report);
        var problems = issues.Count(i => i.Severity > IssueSeverity.Info);
        return (problems, issues.Count - problems);
    }

    private sealed class DefenderData
    {
        public DefenderStatus? Status { get; init; }
        public List<SecurityProduct> Antiviruses { get; init; } = new();
        public List<SecurityProduct> Firewalls { get; init; } = new();
        public List<ThreatDetection> Threats { get; init; } = new();
    }

    private sealed class RegistryData
    {
        public UacState Uac { get; init; } = new(false, null, null, null);
        public WinlogonState Winlogon { get; init; } = new(false, null, null, null);
        public ProxyState Proxy { get; init; } = new(false, false, null, null);
        public SmartScreenState SmartScreen { get; init; } = new(null, null);
        public RdpState Rdp { get; init; } = new(null);
    }

    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(12);

    private DefenderData ReadDefender()
    {
        if (!Env.IsRealSystem)
            return new DefenderData();

        // The queries are independent: run them in parallel so one slow WMI provider does not add up.
        // (This method already runs on a worker thread, so waiting here never blocks the UI.)
        Task<List<Dictionary<string, object?>>> Q(string query, string scope)
            => Task.Run(() => Wmi.Query(query, scope, WmiTimeout));
        var statusQ = Q("SELECT * FROM MSFT_MpComputerStatus", Wmi.Defender);
        var prefsQ = Q("SELECT ExclusionPath, ExclusionProcess, ExclusionExtension FROM MSFT_MpPreference", Wmi.Defender);
        var avQ = Q("SELECT displayName, productState FROM AntiVirusProduct", Wmi.SecurityCenter2);
        var fwQ = Q("SELECT displayName, productState FROM FirewallProduct", Wmi.SecurityCenter2);
        var catalogQ = Q("SELECT ThreatID, ThreatName, SeverityID FROM MSFT_MpThreat", Wmi.Defender);
        var detectionsQ = Q("SELECT ThreatID, InitialDetectionTime, ThreatStatusID, Resources, ProcessName FROM MSFT_MpThreatDetection",
            Wmi.Defender);
        Task.WaitAll(statusQ, prefsQ, avQ, fwQ, catalogQ, detectionsQ);

        DefenderStatus? status = null;
        var rows = statusQ.Result;
        if (rows.Count > 0)
        {
            var r = rows[0];
            var policyAv = SecurityEnvironment.AsInt(SecurityEnvironment.ReadValue(Env.DefenderPolicy, "DisableAntiSpyware").Value) == 1;
            var policyRt = SecurityEnvironment.AsInt(SecurityEnvironment.ReadValue(
                Env.DefenderPolicy.Child("Real-Time Protection"), "DisableRealtimeMonitoring").Value) == 1;

            var exclusions = new List<string>();
            var hidden = false;
            var prefs = prefsQ.Result;
            if (prefs.Count > 0)
            {
                foreach (var name in new[] { "ExclusionPath", "ExclusionProcess", "ExclusionExtension" })
                {
                    if (prefs[0].TryGetValue(name, out var v) && v is string[] arr)
                    {
                        foreach (var s in arr.Where(s => !string.IsNullOrWhiteSpace(s)))
                        {
                            if (s.StartsWith("N/A", StringComparison.OrdinalIgnoreCase))
                                hidden = true;
                            else
                                exclusions.Add(s.Trim());
                        }
                    }
                }
            }

            status = new DefenderStatus
            {
                ServiceEnabled = r.Bool("AMServiceEnabled") ?? false,
                AntivirusEnabled = r.Bool("AntivirusEnabled") ?? false,
                RealTimeEnabled = r.Bool("RealTimeProtectionEnabled") ?? false,
                TamperProtected = r.Bool("IsTamperProtected"),
                SignatureUpdated = r.Date("AntivirusSignatureLastUpdated"),
                SignatureAgeDays = SecurityRules.NormalizeAge(r.Long("AntivirusSignatureAge")),
                QuickScanAgeDays = SecurityRules.NormalizeAge(r.Long("QuickScanAge")),
                FullScanAgeDays = SecurityRules.NormalizeAge(r.Long("FullScanAge")),
                Version = r.Str("AMProductVersion"),
                RunningMode = r.Str("AMRunningMode"),
                PolicyDisablesAntivirus = policyAv,
                PolicyDisablesRealtime = policyRt,
                Exclusions = exclusions,
                ExclusionsHidden = hidden,
            };
        }

        var av = avQ.Result
            .Select(row => SecurityRules.MakeProduct(row.Str("displayName") ?? "Антивирус", row.Long("productState") ?? 0))
            .ToList();
        var fw = fwQ.Result
            .Select(row => SecurityRules.MakeProduct(row.Str("displayName") ?? "Брандмауэр", row.Long("productState") ?? 0))
            .ToList();

        var catalog = catalogQ.Result
            .GroupBy(t => t.Long("ThreatID") ?? -1)
            .ToDictionary(g => g.Key, g => g.First());
        var threats = detectionsQ.Result
            .Select(d =>
            {
                var id = d.Long("ThreatID") ?? -1;
                catalog.TryGetValue(id, out var info);
                var resources = d.TryGetValue("Resources", out var res) && res is string[] arr
                    ? string.Join("; ", arr.Select(CleanResource))
                    : "";
                return new ThreatDetection
                {
                    ThreatId = id,
                    Time = d.Date("InitialDetectionTime"),
                    StatusId = (int)(d.Long("ThreatStatusID") ?? 0),
                    Name = info?.Str("ThreatName") ?? $"Угроза #{id}",
                    SeverityId = (int)(info?.Long("SeverityID") ?? 0),
                    Resources = resources,
                    Process = d.Str("ProcessName"),
                };
            })
            .OrderByDescending(t => t.Time ?? DateTime.MinValue)
            .Take(200)
            .ToList();

        return new DefenderData { Status = status, Antiviruses = av, Firewalls = fw, Threats = threats };
    }

    /// <summary>"file:_C:\x\y.exe" / "containerfile:_C:\x.zip" → the path without the Defender resource type prefix.</summary>
    internal static string CleanResource(string resource)
    {
        var r = resource.Trim();
        var idx = r.IndexOf(":_", StringComparison.Ordinal);
        return idx > 0 && idx <= 24 && r[..idx].All(char.IsLetter) ? r[(idx + 2)..] : r;
    }

    private List<FirewallProfileState> ReadFirewallProfiles()
    {
        if (!Env.IsRealSystem)
            return new List<FirewallProfileState>();
        var order = new[] { "Private", "Public", "Domain" };
        // MSFT_NetFirewallProfile reports the local (persistent) setting; a Group Policy value overrides it.
        var local = Wmi.Query("SELECT Name, Enabled FROM MSFT_NetFirewallProfile", Wmi.StandardCimv2, WmiTimeout)
            .Select(r => new FirewallProfileState(r.Str("Name") ?? "?",
                SecurityRules.ParseGpoBoolean(r.TryGetValue("Enabled", out var v) ? v : null)))
            .OrderBy(p => Array.FindIndex(order, o => o.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 ? i : 9)
            .ToList();
        return SecurityRules.ApplyFirewallPolicy(local, ReadFirewallPolicy());
    }

    /// <summary>EnableFirewall policy values per profile ("Domain"/"Private"/"Public" → 0/1 or null).</summary>
    internal Dictionary<string, int?> ReadFirewallPolicy()
    {
        var result = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "Domain", "Private", "Public" })
        {
            var loc = Env.FirewallPolicy.Child(SecurityRules.FirewallPolicySubKey(name));
            result[name] = SecurityEnvironment.AsInt(SecurityEnvironment.ReadValue(loc, "EnableFirewall").Value);
        }
        return result;
    }

    private RegistryData ReadRegistryChecks()
    {
        static int? I(object? v) => SecurityEnvironment.AsInt(v);

        var lua = SecurityEnvironment.ReadValue(Env.UacPolicies, "EnableLUA");
        var consent = SecurityEnvironment.ReadValue(Env.UacPolicies, "ConsentPromptBehaviorAdmin");
        var secure = SecurityEnvironment.ReadValue(Env.UacPolicies, "PromptOnSecureDesktop");
        var uac = new UacState(lua.Readable && consent.Readable, I(lua.Value), I(consent.Value), I(secure.Value));

        var shell = SecurityEnvironment.ReadValue(Env.Winlogon, "Shell");
        var userinit = SecurityEnvironment.ReadValue(Env.Winlogon, "Userinit");
        var userShell = SecurityEnvironment.ReadValue(Env.UserWinlogon, "Shell");
        var winlogon = new WinlogonState(shell.Readable && userinit.Readable,
            shell.Value?.ToString(), userinit.Value?.ToString(), userShell.Value?.ToString());

        var proxy = ReadProxy();

        var ss = SecurityEnvironment.ReadValue(Env.ExplorerKey, "SmartScreenEnabled");
        var ssPolicy = SecurityEnvironment.ReadValue(Env.SmartScreenPolicy, "EnableSmartScreen");
        var rdp = SecurityEnvironment.ReadValue(Env.TerminalServer, "fDenyTSConnections");

        return new RegistryData
        {
            Uac = uac,
            Winlogon = winlogon,
            Proxy = proxy,
            SmartScreen = new SmartScreenState(ss.Value?.ToString(), I(ssPolicy.Value)),
            Rdp = new RdpState(I(rdp.Value)),
        };
    }

    /// <summary>The WinINet proxy settings of the current user (HKCU Internet Settings).</summary>
    private ProxyState ReadProxy()
    {
        var pe = SecurityEnvironment.ReadValue(Env.InternetSettings, "ProxyEnable");
        var ps = SecurityEnvironment.ReadValue(Env.InternetSettings, "ProxyServer");
        var pac = SecurityEnvironment.ReadValue(Env.InternetSettings, "AutoConfigURL");
        return new ProxyState(pe.Readable, SecurityEnvironment.AsInt(pe.Value) == 1, ps.Value?.ToString(), pac.Value?.ToString());
    }

    /// <summary>Same values as <see cref="ReadProxy"/>, from a key that is already open.</summary>
    private static ProxyState ReadProxy(RegistryKey key)
        => new(true, SecurityEnvironment.AsInt(key.GetValue("ProxyEnable")) == 1,
            key.GetValue("ProxyServer")?.ToString(), key.GetValue("AutoConfigURL")?.ToString());

    private (List<IfeoEntry> Entries, bool Partial, bool Readable) ReadIfeo()
    {
        var entries = new List<IfeoEntry>();
        var partial = false;
        var readable = true;
        foreach (var (loc, wow) in new[] { (Env.Ifeo, false), (Env.IfeoWow, true) })
        {
            try
            {
                using var key = loc.Open();
                if (key is null)
                    continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(name);
                        var dbg = sub?.GetValue("Debugger")?.ToString();
                        if (string.IsNullOrWhiteSpace(dbg))
                            continue;
                        var (risk, reason) = SecurityRules.ClassifyIfeo(name, dbg);
                        entries.Add(new IfeoEntry
                        {
                            ImageName = name,
                            Debugger = dbg,
                            IsWow64 = wow,
                            Location = loc.Child(name),
                            Risk = risk,
                            Reason = reason,
                        });
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                        partial = true;
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                Log.Warn($"Нет доступа к {loc.FullPath}: {ex.Message}");
                if (!wow)
                    readable = false;
            }
        }
        // On 64-bit Windows the WOW6432Node IFEO key is shared with the 64-bit one (it is the same physical key),
        // so every Debugger value shows up in both views. Keep one entry per image + debugger.
        var primary = entries.Where(e => !e.IsWow64)
            .Select(e => (e.ImageName.ToLowerInvariant(), e.Debugger))
            .ToHashSet();
        entries.RemoveAll(e => e.IsWow64 && primary.Contains((e.ImageName.ToLowerInvariant(), e.Debugger)));
        if (partial)
            Log.Info("Безопасность: часть разделов IFEO доступна только администратору");
        return (entries, partial, readable);
    }

    private (List<DnsServerInfo> Servers, bool Readable) ReadDns()
    {
        if (!Env.IsRealSystem)
            return (new List<DnsServerInfo>(), true);
        var list = new List<DnsServerInfo>();
        var rows = Wmi.Query("SELECT Description, DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE",
            Wmi.Cimv2, WmiTimeout);
        foreach (var row in rows)
        {
            var adapter = row.Str("Description") ?? "Сетевой адаптер";
            if (!row.TryGetValue("DNSServerSearchOrder", out var v) || v is not string[] servers)
                continue;
            foreach (var s in servers.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                var (label, kind) = SecurityRules.LabelDns(s);
                list.Add(new DnsServerInfo(adapter, s.Trim(), label, kind));
            }
        }
        return (list, true);
    }

    private UpdatesState ReadUpdates()
    {
        if (!Env.IsRealSystem)
            return new UpdatesState(null, 0, null, null);
        var rows = Wmi.Query("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering", Wmi.Cimv2, WmiTimeout);
        DateTime? last = null;
        string? lastId = null;
        foreach (var row in rows)
        {
            var date = SecurityRules.ParseQfeDate(row.Str("InstalledOn"));
            if (date is not null && date <= DateTime.Now.AddDays(1) && (last is null || date > last))
            {
                last = date;
                lastId = row.Str("HotFixID");
            }
        }

        bool? disabled = null;
        try
        {
            using var sc = new ServiceController("wuauserv");
            disabled = sc.StartType == ServiceStartMode.Disabled;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            disabled = null;
        }
        return new UpdatesState(last, rows.Count, lastId, disabled);
    }

    // =====================================================================
    // Actions (the caller confirms first)
    // =====================================================================

    /// <summary>Runs a Microsoft Defender quick (1) or full (2) scan. Cancelling stops the scan.</summary>
    public async Task<SecurityActionResult> RunDefenderScanAsync(bool full, CancellationToken ct)
    {
        if (!Env.IsRealSystem)
            return SecurityActionResult.Fail("Проверка недоступна в тестовом окружении.");
        var mp = FileTools.FindMpCmdRun();
        if (mp is null)
            return SecurityActionResult.Fail("Защитник Windows не найден на этом компьютере.");
        var kind = full ? "полная" : "быстрая";
        Log.Action($"Запущена {kind} проверка Защитником Windows");
        try
        {
            var r = await ProcessRunner.RunAsync(mp, $"-Scan -ScanType {(full ? 2 : 1)}",
                full ? TimeSpan.FromHours(24) : TimeSpan.FromHours(3), ct);
            if (r.TimedOut)
            {
                // Killing MpCmdRun does not stop the scan inside the Defender service: cancel it there too.
                await CancelDefenderScanAsync(mp);
                Log.Warn($"Проверка Защитником Windows ({kind}) не завершилась вовремя");
                return SecurityActionResult.Fail("Проверка идёт слишком долго и была остановлена. Попробуйте ещё раз позже.");
            }
            Log.Info($"Проверка Защитником Windows ({kind}) завершена с кодом {r.ExitCode}");
            return r.ExitCode switch
            {
                0 => LogResult(SecurityActionResult.Ok("Проверка завершена: угроз не найдено.")),
                2 => LogResult(new SecurityActionResult(false,
                    "Защитник Windows обнаружил угрозы! Откройте «Безопасность Windows» → «Журнал защиты», чтобы посмотреть, что найдено и что с этим сделано.",
                    ThreatsFound: true)),
                _ => SecurityActionResult.Fail(
                    $"Проверка завершилась с кодом {HResult(r.ExitCode)}. {FirstLine(r.StdOut)}".Trim()),
            };
        }
        catch (OperationCanceledException)
        {
            await CancelDefenderScanAsync(mp);
            Log.Info($"Проверка Защитником Windows ({kind}) отменена пользователем");
            return new SecurityActionResult(false, "Проверка отменена.", Cancelled: true);
        }

        static SecurityActionResult LogResult(SecurityActionResult res)
        {
            Log.Action("Результат проверки Защитником Windows: " + res.Message);
            return res;
        }
    }

    private static async Task CancelDefenderScanAsync(string mpCmdRun)
    {
        try
        {
            await ProcessRunner.RunAsync(mpCmdRun, "-Cancel", TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Не удалось остановить проверку Защитником Windows: {ex.Message}");
        }
    }

    public async Task<SecurityActionResult> UpdateSignaturesAsync(CancellationToken ct)
    {
        if (!Env.IsRealSystem)
            return SecurityActionResult.Fail("Обновление недоступно в тестовом окружении.");
        var mp = FileTools.FindMpCmdRun();
        if (mp is null)
            return SecurityActionResult.Fail("Защитник Windows не найден на этом компьютере.");
        var r = await ProcessRunner.RunAsync(mp, "-SignatureUpdate", TimeSpan.FromMinutes(15), ct);
        if (r.Success)
        {
            Log.Action("Обновлены антивирусные базы Защитника Windows");
            return SecurityActionResult.Ok(SignaturesUpdatedMessage);
        }
        Log.Warn($"MpCmdRun -SignatureUpdate: код {HResult(r.ExitCode)}: {FirstLine(r.StdOut + r.StdErr)}");
        var denied = (uint)r.ExitCode == 0x80070005;
        return SecurityActionResult.Fail(
            $"Не удалось обновить базы (код {HResult(r.ExitCode)}). " +
            (denied && !Admin.IsElevated
                ? "Нужны права администратора — или обновите базы в «Безопасности Windows»."
                : "Проверьте подключение к интернету или обновите базы в «Безопасности Windows» → «Защита от вирусов и угроз» → «Обновления защиты»."));
    }

    public async Task<SecurityActionResult> EnableRealtimeProtectionAsync(CancellationToken ct)
    {
        if (!Env.IsRealSystem)
            return SecurityActionResult.Fail("Недоступно в тестовом окружении.");
        if (!Admin.IsElevated)
            return SecurityActionResult.Fail(NeedAdminMessage);
        var r = await ProcessRunner.PowerShellAsync("Set-MpPreference -DisableRealtimeMonitoring $false -ErrorAction Stop; 'OK'",
            TimeSpan.FromMinutes(2), ct);
        // The caller may be on the UI thread (page or dashboard): WMI and registry reads go to the thread pool.
        var (enabled, policy) = await Task.Run(async () =>
        {
            bool? on = null;
            for (var attempt = 0; attempt < 3 && on != true; attempt++)
            {
                await Task.Delay(1500, ct);
                var row = Wmi.Query("SELECT RealTimeProtectionEnabled FROM MSFT_MpComputerStatus", Wmi.Defender, WmiTimeout)
                    .FirstOrDefault();
                on = row?.Bool("RealTimeProtectionEnabled");
            }
            var byPolicy = SecurityEnvironment.AsInt(SecurityEnvironment.ReadValue(
                Env.DefenderPolicy.Child("Real-Time Protection"), "DisableRealtimeMonitoring").Value) == 1;
            return (on == true, byPolicy);
        }, ct);
        if (enabled)
        {
            Log.Action("Включена защита в реальном времени Защитника Windows");
            return SecurityActionResult.Ok(RealtimeEnabledMessage);
        }
        Log.Warn($"Не удалось включить защиту в реальном времени: {FirstLine(r.StdErr)}");
        return SecurityActionResult.Fail(policy
            ? "Защиту не удалось включить: она отключена групповой политикой. Если компьютер не рабочий, политику оставил «активатор» или вирус."
            : "Windows не дала включить защиту (возможно, мешает «Защита от подделки» или другой антивирус). " +
              "Включите её вручную: «Безопасность Windows» → «Защита от вирусов и угроз» → «Управление настройками».");
    }

    public async Task<SecurityActionResult> EnableFirewallAsync(CancellationToken ct)
    {
        if (!Env.IsRealSystem)
            return SecurityActionResult.Fail("Недоступно в тестовом окружении.");
        if (!Admin.IsElevated)
            return SecurityActionResult.Fail(NeedAdminMessage);
        var r = await ProcessRunner.RunAsync("netsh.exe", "advfirewall set allprofiles state on", TimeSpan.FromSeconds(60), ct);
        if (!r.Success)
        {
            Log.Warn($"netsh advfirewall: код {r.ExitCode}: {FirstLine(r.StdOut + r.StdErr)}");
            return SecurityActionResult.Fail($"Не удалось включить брандмауэр: {FirstLine(r.StdOut + r.StdErr)}");
        }
        Log.Action("Брандмауэр Windows включён для всех профилей (netsh advfirewall set allprofiles state on)");
        // The caller may be on the UI thread: query WMI on the thread pool.
        var stillOff = (await Task.Run(ReadFirewallProfiles, ct)).Where(p => p.Enabled == false).ToList();
        if (stillOff.Count > 0)
        {
            return SecurityActionResult.Fail("Команда выполнена, но брандмауэр всё ещё выключен для: " +
                                             string.Join(", ", stillOff.Select(p => p.DisplayName)) +
                                             (stillOff.Any(p => p.ByPolicy)
                                                 ? ". Его отключает групповая политика — её меняет администратор компьютера " +
                                                   "(или удалите её, если компьютер не рабочий)."
                                                 : ". Вероятно, его отключает другая программа."));
        }
        return SecurityActionResult.Ok(FirewallEnabledMessage);
    }

    /// <summary>Values that <see cref="EnableUacAsync"/> changes: EnableLUA → 1 if it is 0; the prompt → 5 if it is 0.</summary>
    /// <remarks>A stricter prompt setting the user chose ("Всегда уведомлять", 1–4) is kept.</remarks>
    public static (bool FixLua, bool FixConsent) UacChangesNeeded(int? enableLua, int? consent)
        => (enableLua == 0, consent == 0);

    /// <summary>Turns UAC back on: EnableLUA=1 and ConsentPromptBehaviorAdmin=5 (Windows defaults) where they are off.</summary>
    public async Task<SecurityActionResult> EnableUacAsync(CancellationToken ct)
    {
        var loc = Env.UacPolicies;
        if (loc.NeedsAdmin && !Admin.IsElevated)
            return SecurityActionResult.Fail(NeedAdminMessage);
        var (fixLua, fixConsent) = UacChangesNeeded(
            SecurityEnvironment.AsInt(SecurityEnvironment.ReadValue(loc, "EnableLUA").Value),
            SecurityEnvironment.AsInt(SecurityEnvironment.ReadValue(loc, "ConsentPromptBehaviorAdmin").Value));
        if (!fixLua && !fixConsent)
            return SecurityActionResult.Ok("UAC уже включён и запрашивает разрешение — менять ничего не нужно.");
        var backup = await RegistryBackup.ExportAsync(loc.FullPath, "Перед включением UAC", ct);
        if (backup is null)
            return SecurityActionResult.Fail(BackupFailedMessage);
        var done = new List<string>();
        try
        {
            using var key = loc.Open(writable: true);
            if (key is null)
                return SecurityActionResult.Fail("Раздел реестра с настройками UAC не найден.");
            var luaBefore = SecurityEnvironment.AsInt(key.GetValue("EnableLUA"));
            var consentBefore = SecurityEnvironment.AsInt(key.GetValue("ConsentPromptBehaviorAdmin"));
            (fixLua, fixConsent) = UacChangesNeeded(luaBefore, consentBefore);
            if (fixLua)
            {
                key.SetValue("EnableLUA", 1, RegistryValueKind.DWord);
                done.Add($"EnableLUA: {luaBefore} → 1");
            }
            if (fixConsent)
            {
                key.SetValue("ConsentPromptBehaviorAdmin", 5, RegistryValueKind.DWord);
                done.Add($"ConsentPromptBehaviorAdmin: {consentBefore} → 5");
            }
            if (done.Count == 0)
                return SecurityActionResult.Ok("UAC уже включён и запрашивает разрешение — менять ничего не нужно.");
            Log.Action($"UAC: {string.Join(", ", done)} ({loc.FullPath}). Резервная копия: {Path.GetFileName(backup)}");
            return SecurityActionResult.Ok(fixLua
                ? $"UAC включён. Перезагрузите компьютер, чтобы изменение вступило в силу.\n\nРезервная копия реестра: {backup}"
                : $"Запросы UAC снова включены: перед изменением системы Windows будет спрашивать разрешение.\n\nРезервная копия реестра: {backup}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"Не удалось изменить настройки UAC: {ex.Message}");
            if (done.Count > 0)
                Log.Action($"UAC (частично): {string.Join(", ", done)} ({loc.FullPath}). Резервная копия: {Path.GetFileName(backup)}");
            return SecurityActionResult.Fail($"Не удалось изменить настройки UAC: {ErrorText.For(ex)}." +
                                             (done.Count > 0 ? $"\n\nУспели измениться: {string.Join(", ", done)}. Резервная копия: {backup}" : ""));
        }
    }

    /// <summary>Removes the selected lines from the hosts file (a copy is saved to the backups folder first).</summary>
    public async Task<SecurityActionResult> RemoveHostsLinesAsync(IReadOnlyCollection<HostsEntry> lines, CancellationToken ct)
    {
        if (lines.Count == 0)
            return SecurityActionResult.Fail("Не выбрано ни одной строки.");
        if (Env.IsRealSystem && !Admin.IsElevated)
            return SecurityActionResult.Fail(NeedAdminMessage);
        try
        {
            var backup = await Task.Run(() => HostsFile.RemoveLines(Env.HostsPath, lines, AppPaths.BackupsDir), ct);
            Log.Action($"Из файла hosts удалены строки: {string.Join("; ", lines.Select(l => $"№{l.LineNumber} «{l.RawLine.Trim()}»"))}. " +
                       $"Копия файла: {backup}");
            if (Env.IsRealSystem)
            {
                var flush = await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", TimeSpan.FromSeconds(30), ct);
                if (flush.Success)
                    Log.Action("Очищен кэш DNS (ipconfig /flushdns)");
            }
            return SecurityActionResult.Ok(
                $"{SecurityRules.LinesRemovedText(lines.Count)} из файла hosts. Сайты снова открываются по настоящим адресам.\n\n" +
                $"Копия прежнего файла: {backup}");
        }
        catch (HostsChangedException ex)
        {
            return SecurityActionResult.Fail(ErrorText.Sentence(ex));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"Нет доступа к файлу hosts: {ex.Message}");
            return SecurityActionResult.Fail("Нет доступа к файлу hosts. Его может защищать антивирус — " +
                                             "или программа запущена без прав администратора. Ничего не изменено.");
        }
        catch (IOException ex)
        {
            Log.Warn($"Не удалось изменить файл hosts: {ex.Message}");
            return SecurityActionResult.Fail($"Не удалось изменить файл hosts: {ErrorText.For(ex)}.");
        }
    }

    /// <summary>Restores Shell=explorer.exe and Userinit=...\userinit.exe, (and removes a per-user Shell override).</summary>
    public Task<SecurityActionResult> RestoreWinlogonAsync(CancellationToken ct) => RestoreWinlogonAsync(null, ct);

    /// <summary>
    /// Same as <see cref="RestoreWinlogonAsync(CancellationToken)"/>; with <paramref name="expected"/> (the values the user
    /// confirmed) nothing is changed if the current values differ, so only what the confirmation listed can change.
    /// </summary>
    public async Task<SecurityActionResult> RestoreWinlogonAsync(WinlogonState? expected, CancellationToken ct)
    {
        var win = Env.WindowsDir;
        var shell = SecurityEnvironment.ReadValue(Env.Winlogon, "Shell").Value?.ToString();
        var userinit = SecurityEnvironment.ReadValue(Env.Winlogon, "Userinit").Value?.ToString();
        var userShell = SecurityEnvironment.ReadValue(Env.UserWinlogon, "Shell").Value?.ToString();
        if (expected is not null &&
            (!string.Equals(shell, expected.Shell, StringComparison.Ordinal) ||
             !string.Equals(userinit, expected.Userinit, StringComparison.Ordinal) ||
             !string.Equals(userShell, expected.UserShell, StringComparison.Ordinal)))
        {
            return SecurityActionResult.Fail("Значения изменились после проверки, поэтому ничего не изменено. Нажмите «Проверить снова».");
        }
        var fixShell = !SecurityRules.IsShellDefault(shell, win);
        var fixUserinit = !SecurityRules.IsUserinitDefault(userinit, win);
        var fixUserShell = !string.IsNullOrWhiteSpace(userShell) && !SecurityRules.IsShellDefault(userShell, win);
        if (!fixShell && !fixUserinit && !fixUserShell)
            return SecurityActionResult.Ok("Параметры входа уже стандартные — менять ничего не нужно.");
        if ((fixShell || fixUserinit) && Env.Winlogon.NeedsAdmin && !Admin.IsElevated)
            return SecurityActionResult.Fail(NeedAdminMessage);

        // Take every backup first: if one fails, nothing has been changed yet.
        var backups = new List<string>();
        if (fixShell || fixUserinit)
        {
            var backup = await RegistryBackup.ExportAsync(Env.Winlogon.FullPath, "Перед восстановлением Winlogon", ct);
            if (backup is null)
                return SecurityActionResult.Fail(BackupFailedMessage);
            backups.Add(backup);
        }
        if (fixUserShell)
        {
            var backup = await RegistryBackup.ExportAsync(Env.UserWinlogon.FullPath, "Перед восстановлением Winlogon (пользователь)", ct);
            if (backup is null)
                return SecurityActionResult.Fail(BackupFailedMessage);
            backups.Add(backup);
        }

        var done = new List<string>();
        try
        {
            if (fixShell || fixUserinit)
            {
                using var key = Env.Winlogon.Open(writable: true);
                if (key is null)
                    return SecurityActionResult.Fail("Раздел Winlogon не найден. Ничего не изменено.");
                if (fixShell)
                {
                    key.SetValue("Shell", "explorer.exe", RegistryValueKind.String);
                    done.Add($"Shell: «{shell}» → «explorer.exe»");
                }
                if (fixUserinit)
                {
                    var def = SecurityRules.DefaultUserinit(win);
                    key.SetValue("Userinit", def, RegistryValueKind.String);
                    done.Add($"Userinit: «{userinit}» → «{def}»");
                }
            }
            if (fixUserShell)
            {
                using var key = Env.UserWinlogon.Open(writable: true);
                if (key is not null)
                {
                    key.DeleteValue("Shell", throwOnMissingValue: false);
                    done.Add($"удалён пользовательский Shell «{userShell}»");
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"Не удалось восстановить Winlogon: {ex.Message}");
            if (done.Count > 0)
            {
                Log.Action("Winlogon восстановлен частично: " + string.Join("; ", done) + ". Резервные копии: " +
                           string.Join(", ", backups.Select(Path.GetFileName)));
            }
            return SecurityActionResult.Fail($"Не удалось восстановить значения: {ErrorText.For(ex)}." +
                                             (done.Count > 0 ? "\n\nУспели измениться: " + string.Join("; ", done) : "") +
                                             "\n\nРезервная копия: " + string.Join(", ", backups));
        }

        Log.Action("Winlogon восстановлен: " + string.Join("; ", done) + ". Резервные копии: " +
                   string.Join(", ", backups.Select(Path.GetFileName)));
        return SecurityActionResult.Ok(
            "Восстановлены стандартные значения:\n• " + string.Join("\n• ", done) +
            "\n\nПерезагрузите компьютер, а затем запустите полную проверку антивирусом: программа, которая подменила значения, " +
            "могла остаться на диске.\n\nРезервная копия: " + string.Join(", ", backups));
    }

    /// <summary>Deletes only the "Debugger" value of one IFEO subkey (after a .reg backup of that subkey).</summary>
    public async Task<SecurityActionResult> RemoveIfeoDebuggerAsync(IfeoEntry entry, CancellationToken ct)
    {
        var loc = entry.Location;
        if (loc.NeedsAdmin && !Admin.IsElevated)
            return SecurityActionResult.Fail(NeedAdminMessage);
        var backup = await RegistryBackup.ExportAsync(loc.FullPath, $"Перед удалением перехвата {entry.ImageName}", ct);
        if (backup is null)
            return SecurityActionResult.Fail(BackupFailedMessage);
        try
        {
            using var key = loc.Open(writable: true);
            if (key is null)
                return SecurityActionResult.Fail("Этого раздела уже нет — перехват удалён.");
            var current = key.GetValue("Debugger")?.ToString();
            if (current is null)
                return SecurityActionResult.Ok("Перехвата уже нет — ничего менять не нужно.");
            if (!string.Equals(current, entry.Debugger, StringComparison.Ordinal))
                return SecurityActionResult.Fail("Значение изменилось после проверки. Нажмите «Проверить снова».");
            key.DeleteValue("Debugger", throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"Не удалось удалить перехват {entry.ImageName}: {ex.Message}");
            return SecurityActionResult.Fail($"Не удалось удалить перехват: {ErrorText.For(ex)}.");
        }
        Log.Action($"Удалён перехват запуска (IFEO Debugger) для {entry.ImageName}: «{entry.Debugger}». " +
                   $"Резервная копия: {Path.GetFileName(backup)}");
        return SecurityActionResult.Ok(
            $"Перехват для {entry.ImageName} удалён — программа снова запускается как обычно.\n\n" +
            $"Вернуть как было можно двойным щелчком по резервной копии: {backup}");
    }

    internal const string ProxyChangedMessage =
        "Настройки прокси изменились после проверки, поэтому ничего не изменено. Нажмите «Проверить снова».";

    /// <summary>Sets ProxyEnable=0 and deletes AutoConfigURL (HKCU, the proxy address itself is kept).</summary>
    public Task<SecurityActionResult> DisableProxyAsync(CancellationToken ct) => DisableProxyAsync(null, ct);

    /// <summary>
    /// Same as <see cref="DisableProxyAsync(CancellationToken)"/>; with <paramref name="expected"/> (the settings the user
    /// confirmed) nothing is changed if ProxyEnable, ProxyServer or AutoConfigURL differ now — a proxy or a script that a
    /// VPN or the user set after the check is never removed without being named in the confirmation.
    /// </summary>
    public async Task<SecurityActionResult> DisableProxyAsync(ProxyState? expected, CancellationToken ct)
    {
        var loc = Env.InternetSettings;
        if (expected is not null && !SecurityRules.SameProxySettings(ReadProxy(), expected))
            return SecurityActionResult.Fail(ProxyChangedMessage);
        var backup = await RegistryBackup.ExportAsync(loc.FullPath, "Перед отключением прокси", ct);
        if (backup is null)
            return SecurityActionResult.Fail(BackupFailedMessage);
        string? pac;
        try
        {
            using var key = loc.Open(writable: true);
            if (key is null)
                return SecurityActionResult.Fail("Раздел настроек интернета не найден.");
            // Check again right before writing: exporting the backup takes a moment.
            if (expected is not null && !SecurityRules.SameProxySettings(ReadProxy(key), expected))
                return SecurityActionResult.Fail(ProxyChangedMessage);
            pac = key.GetValue("AutoConfigURL")?.ToString();
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"Не удалось отключить прокси: {ex.Message}");
            return SecurityActionResult.Fail($"Не удалось отключить прокси: {ErrorText.For(ex)}.");
        }
        if (Env.IsRealSystem)
        {
            // Browsers read the per-connection settings of WinINet: switch the connection to "direct" through the API
            // too (it also rewrites the binary DefaultConnectionSettings), then tell running programs to reload them.
            SetDirectConnection();
            NotifyInternetSettingsChanged();
        }
        Log.Action("Прокси отключён: ProxyEnable=0" + (pac is null ? "" : $", удалён скрипт AutoConfigURL «{pac}»") +
                   $". Резервная копия: {Path.GetFileName(backup)}");
        return SecurityActionResult.Ok(
            "Прокси отключён — браузеры подключаются к сайтам напрямую.\n\n" +
            "Если пропал интернет или перестал работать VPN, включите прокси снова: «Параметры» → «Сеть и Интернет» → «Прокси-сервер».\n\n" +
            $"Резервная копия настроек: {backup}");
    }

    private const int InternetOptionRefresh = 37;
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionPerConnectionOption = 75;
    private const int InternetPerConnFlags = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct InternetPerConnOptionList
    {
        public int Size;
        public IntPtr Connection; // null = the LAN connection
        public int OptionCount;
        public int OptionError;
        public IntPtr Options;
    }

    // Size = 8: the native union also holds a FILETIME, so it is 8 bytes on 32-bit Windows too.
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct InternetPerConnOptionValue
    {
        [FieldOffset(0)] public int Number;
        [FieldOffset(0)] public IntPtr Pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InternetPerConnOption
    {
        public int Option;
        public InternetPerConnOptionValue Value;
    }

    [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetSetOptionW")]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetSetOptionW")]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, ref InternetPerConnOptionList list, int size);

    [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetQueryOptionW")]
    private static extern bool InternetQueryOption(IntPtr hInternet, int dwOption, ref InternetPerConnOptionList list, ref int size);

    /// <summary>Sets the WinINet per-connection flags of the LAN connection to "direct" (keeps automatic detection).</summary>
    private static void SetDirectConnection()
    {
        var buffer = IntPtr.Zero;
        try
        {
            buffer = Marshal.AllocCoTaskMem(Marshal.SizeOf<InternetPerConnOption>());
            var option = new InternetPerConnOption { Option = InternetPerConnFlags };
            Marshal.StructureToPtr(option, buffer, false);
            var list = new InternetPerConnOptionList
            {
                Size = Marshal.SizeOf<InternetPerConnOptionList>(),
                Connection = IntPtr.Zero,
                OptionCount = 1,
                Options = buffer,
            };
            var size = list.Size;
            var current = InternetQueryOption(IntPtr.Zero, InternetOptionPerConnectionOption, ref list, ref size)
                ? Marshal.PtrToStructure<InternetPerConnOption>(buffer).Value.Number
                : 0;
            option.Value.Number = SecurityRules.DirectProxyFlags(current);
            Marshal.StructureToPtr(option, buffer, true);
            list.Size = Marshal.SizeOf<InternetPerConnOptionList>();
            if (!InternetSetOption(IntPtr.Zero, InternetOptionPerConnectionOption, ref list, list.Size))
                Log.Warn($"Не удалось переключить подключение на «без прокси» через WinINet (код {Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn($"Не удалось переключить подключение на «без прокси»: {ex.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <summary>INTERNET_OPTION_SETTINGS_CHANGED + INTERNET_OPTION_REFRESH: running browsers reload the proxy settings.</summary>
    private static void NotifyInternetSettingsChanged()
    {
        try
        {
            var changed = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            var refreshed = InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
            if (!changed || !refreshed)
                Log.Warn($"Система не подтвердила обновление настроек прокси (код {Marshal.GetLastWin32Error()}) — " +
                         "браузеры подхватят их после перезапуска");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn($"Не удалось уведомить систему об изменении прокси: {ex.Message}");
        }
    }

    // =====================================================================
    // Dashboard
    // =====================================================================

    public async Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
    {
        var report = await ScanAsync(null, ct);
        return BuildIssues(report);
    }

    /// <summary>Start of the dashboard text of a failed fix (see <see cref="DashboardText"/>).</summary>
    internal const string DashboardFailurePrefix = "Не удалось исправить.";

    /// <summary>
    /// The text a dashboard fix returns. HealthIssue.FixAsync has no success flag: the dashboard decides from the wording
    /// whether the problem is fixed, and some failures read like a plain statement ("Команда выполнена, но брандмауэр всё
    /// ещё выключен…", "Windows не дала включить защиту…"). So the text of every failed action starts with "Не удалось",
    /// which the dashboard always counts as "not fixed"; a successful result is passed on unchanged.
    /// </summary>
    internal static string DashboardText(SecurityActionResult result)
    {
        var message = result.Message?.Trim() ?? "";
        if (result.Success)
            return message;
        if (message.StartsWith("Не удалось", StringComparison.OrdinalIgnoreCase))
            return message;
        return message.Length == 0 ? DashboardFailurePrefix : DashboardFailurePrefix + " " + message;
    }

    internal IReadOnlyList<HealthIssue> BuildIssues(SecurityReport report)
    {
        var issues = new List<HealthIssue>();

        HealthIssue Issue(string title, string details, IssueSeverity severity,
            Func<CancellationToken, Task<string>>? fix = null, bool autoFix = false, string? gain = null) => new()
            {
                Module = ModuleName,
                Title = title,
                Details = details,
                Severity = severity,
                FixAsync = fix,
                AutoFixSafe = autoFix,
                Gain = gain,
                TargetPageKey = "Security",
            };

        // The dashboard gets only the text: DashboardText keeps a failed action from reading as "Исправлено".
        static Func<CancellationToken, Task<string>> Wrap(Func<CancellationToken, Task<SecurityActionResult>> action)
            => async ct => DashboardText(await action(ct));

        // Antivirus
        var d = report.Defender;
        var thirdParty = report.HasThirdPartyAntivirus;
        if (!thirdParty && d is not null)
        {
            if (d.PolicyDisablesAntivirus || !d.ServiceEnabled || !d.AntivirusEnabled)
            {
                var other = SecurityRules.InactiveThirdPartyText(report.Antiviruses);
                issues.Add(Issue("Антивирус выключен",
                    (d.PolicyDisablesAntivirus ? "Защитник Windows отключён групповой политикой, " : "Защитник Windows выключен, ") +
                    (other is null ? "а другого антивируса нет." : $"а {other}.") + " Компьютер не защищён от вирусов.",
                    IssueSeverity.Critical));
            }
            else if (!d.RealTimeEnabled)
            {
                issues.Add(Issue("Защита в реальном времени выключена",
                    "Защитник Windows не проверяет файлы при запуске, а другого антивируса нет. Включение безопасно.",
                    IssueSeverity.Critical, d.PolicyDisablesRealtime ? null : Wrap(EnableRealtimeProtectionAsync),
                    gain: "Файлы снова проверяются при запуске"));
            }
            if (d.SignatureAgeDays is > SecurityRules.SignatureMaxAgeDays && d.AntivirusEnabled)
            {
                issues.Add(Issue("Антивирусные базы устарели",
                    $"Базы Защитника Windows не обновлялись {SecurityRules.Days(d.SignatureAgeDays.Value)}. Старые базы не знают новых вирусов.",
                    IssueSeverity.Medium, Wrap(UpdateSignaturesAsync), autoFix: true, gain: "Свежие базы вирусов"));
            }
            var badExclusions = d.Exclusions.Where(SecurityRules.IsDangerousExclusion).ToList();
            if (badExclusions.Count > 0)
            {
                issues.Add(Issue("Слишком широкие исключения антивируса",
                    $"Защитник Windows не проверяет: {string.Join(", ", badExclusions.Take(3))}. Майнеры и вирусы добавляют себе такие исключения.",
                    IssueSeverity.Medium));
            }
        }
        else if (!thirdParty && d is null && report.Antiviruses.Count > 0 && report.Antiviruses.All(a => !a.Enabled))
        {
            issues.Add(Issue("Антивирус выключен",
                $"{report.Antiviruses[0].Name} установлен, но защита не работает.", IssueSeverity.Critical));
        }
        else if (thirdParty && report.Antiviruses.Where(a => !a.IsMicrosoft && a.Enabled).All(a => !a.UpToDate))
        {
            issues.Add(Issue("Антивирусные базы устарели",
                "Обновите базы в настройках вашего антивируса.", IssueSeverity.Medium));
        }
        var active = report.ActiveThreats;
        if (active.Count > 0)
        {
            var canRemove = report.CanRemoveThreats;
            issues.Add(Issue($"Есть необезвреженные угрозы: {active.Count}",
                $"Защитник Windows нашёл «{active[0].Name}» и не завершил лечение." +
                (canRemove
                    ? " Исправление попросит Защитник Windows обезвредить угрозы (обычно — поместить в карантин, откуда файл можно вернуть)."
                    : " Откройте «Безопасность Windows» → «Журнал защиты»."),
                IssueSeverity.High, canRemove ? Wrap(RemoveActiveThreatsAsync) : null, gain: "Угрозы обезврежены"));
        }
        var antiviruses = report.ActiveAntivirusNames;
        if (antiviruses.Count >= 2)
        {
            issues.Add(new HealthIssue
            {
                Module = ModuleName,
                Title = "Установлено несколько антивирусов",
                Details = $"Одновременно работают: {string.Join(", ", antiviruses)}. Антивирусы конфликтуют друг с другом и " +
                          "замедляют компьютер — оставьте один, а остальные удалите в разделе «Программы».",
                Severity = IssueSeverity.Medium,
                TargetPageKey = "Programs",
            });
        }

        // Firewall
        var fw = report.Check(SecurityCheckIds.Firewall);
        if (fw is { Status: SecurityStatus.Danger or SecurityStatus.Warning })
        {
            issues.Add(Issue(fw.Status == SecurityStatus.Danger ? "Брандмауэр Windows выключен" : "Брандмауэр выключен для доменной сети",
                fw.State + ". Без брандмауэра компьютер открыт для атак из сети." +
                (report.FirewallOffOnlyByPolicy ? " Выключение задано групповой политикой — исправить его здесь нельзя." : ""),
                fw.Status == SecurityStatus.Danger ? IssueSeverity.High : IssueSeverity.Low,
                // netsh cannot override Group Policy: no fix when only the policy keeps it off.
                report.FirewallOffOnlyByPolicy ? null : Wrap(EnableFirewallAsync), gain: "Защита от атак из сети"));
        }

        // UAC
        var uac = report.Check(SecurityCheckIds.Uac);
        if (uac is { Status: SecurityStatus.Danger })
        {
            issues.Add(Issue("Контроль учётных записей (UAC) отключён",
                "Любая программа получает права администратора без вашего ведома. После включения нужна перезагрузка.",
                IssueSeverity.High, Wrap(EnableUacAsync)));
        }
        else if (uac is { Status: SecurityStatus.Warning })
        {
            issues.Add(Issue("Запросы UAC отключены",
                "Программы получают права администратора молча, без окна подтверждения. Исправление вернёт стандартный запрос.",
                IssueSeverity.Medium, Wrap(EnableUacAsync)));
        }

        // Hosts
        var hosts = report.Check(SecurityCheckIds.Hosts);
        if (hosts is { Status: SecurityStatus.Danger })
        {
            var site = report.Hosts.Entries.Where(e => e.Risk == SecurityStatus.Danger).SelectMany(e => e.Hostnames).FirstOrDefault();
            issues.Add(Issue("Файл hosts подменяет адреса сайтов",
                hosts.State + (site is null ? "" : $", например {site}") +
                ". Так вирусы уводят на поддельные страницы. Нажмите «Подробнее», чтобы проверить и удалить эти строки.",
                IssueSeverity.High));
        }
        else if (hosts is { Status: SecurityStatus.Warning })
            issues.Add(Issue("В файле hosts есть записи, которые стоит проверить", hosts.Details, IssueSeverity.Low));

        // Winlogon / IFEO
        var winlogon = report.Check(SecurityCheckIds.Winlogon);
        if (winlogon is { Status: SecurityStatus.Danger })
            issues.Add(Issue("Подменён запуск при входе в Windows", winlogon.Details, IssueSeverity.Critical));
        var ifeo = report.Check(SecurityCheckIds.Ifeo);
        if (ifeo is { Status: SecurityStatus.Danger })
            issues.Add(Issue("Перехвачен запуск системных программ", ifeo.State + ". " + ifeo.Details, IssueSeverity.Critical));
        else if (ifeo is { Status: SecurityStatus.Warning })
            issues.Add(Issue("Найден перехват запуска программ", ifeo.Details, IssueSeverity.Low));

        // Proxy
        var proxy = report.Check(SecurityCheckIds.Proxy);
        if (proxy is { Status: SecurityStatus.Warning })
            issues.Add(Issue(proxy.State, "Рекламные программы подменяют прокси. Если вы не включали его сами (VPN, работа) — отключите.",
                IssueSeverity.Medium));

        // Updates
        var updates = report.Check(SecurityCheckIds.Updates);
        if (updates is { Status: SecurityStatus.Warning })
            issues.Add(Issue(updates.State.StartsWith("Служба", StringComparison.Ordinal) ? "Служба обновлений Windows отключена" : "Windows давно не обновлялась",
                updates.Details, IssueSeverity.Medium));

        // Extensions
        var ext = report.Check(SecurityCheckIds.Extensions);
        if (ext is { Status: SecurityStatus.Warning })
        {
            var first = report.Extensions.FirstOrDefault(e => e.Risk == SecurityStatus.Warning);
            issues.Add(Issue("Подозрительные расширения в браузере",
                ext.State + (first is null ? "" : $", например «{first.Name}» ({first.Browser})") +
                ". Удалить расширение можно только в самом браузере — нажмите «Подробнее», там есть инструкция.",
                IssueSeverity.Low));
        }

        // SmartScreen / RDP
        var ss = report.Check(SecurityCheckIds.SmartScreen);
        if (ss is { Status: SecurityStatus.Warning })
            issues.Add(Issue(ss.State, ss.Details, IssueSeverity.Low));
        var rdp = report.Check(SecurityCheckIds.Rdp);
        if (rdp is { Status: SecurityStatus.Warning })
            issues.Add(Issue(rdp.State, rdp.Details, IssueSeverity.Low));

        // SMBv1: the server part lets worms like WannaCry in; the client part alone is a smaller risk.
        var smb1 = report.Check(SecurityCheckIds.Smb1);
        if (smb1 is { Status: SecurityStatus.Danger or SecurityStatus.Warning })
        {
            issues.Add(Issue("Включён устаревший протокол SMBv1",
                FirstParagraph(smb1.Details) + " Исправление создаст точку восстановления и отключит компонент Windows " +
                "«SMB 1.0», после него нужна перезагрузка. Вернуть его можно в «Включение или отключение компонентов Windows».",
                smb1.Status == SecurityStatus.Danger ? IssueSeverity.High : IssueSeverity.Medium,
                Wrap(DisableSmb1Async), gain: "Защита от сетевых червей вроде WannaCry"));
        }

        var secureBoot = report.Check(SecurityCheckIds.SecureBoot);
        if (secureBoot is { Status: SecurityStatus.Warning })
            issues.Add(Issue("Secure Boot выключен", secureBoot.Details, IssueSeverity.Medium));

        var tpm = report.Check(SecurityCheckIds.Tpm);
        if (tpm is { Status: SecurityStatus.Warning })
        {
            issues.Add(Issue(report.Tpm.Present == false ? "TPM не найден или выключен в BIOS" : "Модуль TPM выключен",
                tpm.Details, IssueSeverity.Low));
        }

        if (SecurityRules.LaptopDriveUnprotected(report.BitLocker))
        {
            var bitLocker = report.Check(SecurityCheckIds.BitLocker);
            issues.Add(Issue(report.BitLocker.Encryption == DriveEncryption.Suspended
                    ? "Защита BitLocker на ноутбуке приостановлена"
                    : "Системный диск ноутбука не зашифрован",
                bitLocker?.Details ?? "Если ноутбук украдут, файлы можно будет прочитать.", IssueSeverity.Low));
        }

        var memory = report.Check(SecurityCheckIds.MemoryIntegrity);
        if (memory is { Status: SecurityStatus.Info } && report.DeviceGuard.Available &&
            !report.DeviceGuard.HvciRunning && !report.DeviceGuard.HvciConfigured)
        {
            issues.Add(Issue("Можно включить «Целостность памяти»",
                "Изоляция ядра не даёт вредоносным драйверам проникнуть в ядро Windows. Включается в «Безопасность Windows» → " +
                "«Безопасность устройства» → «Изоляция ядра». В отдельных играх может немного снизить FPS.",
                IssueSeverity.Info));
        }

        return issues;
    }

    // =====================================================================

    private static string HResult(int code) => code is >= 0 and < 1000 ? code.ToString() : $"0x{(uint)code:X8}";

    private static string FirstLine(string s)
        => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    /// <summary>Card details without the trailing hint lines ("Для точной проверки нужны права администратора.").</summary>
    private static string FirstParagraph(string s) => s.Split('\n')[0].Trim();
}
