using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Security;

/// <summary>Device checks (SMBv1, Secure Boot, TPM, drive encryption, memory integrity) and their actions.</summary>
public sealed partial class SecurityService
{
    private const string TpmNamespace = @"root\cimv2\Security\MicrosoftTpm";
    private const string BitLockerNamespace = @"root\cimv2\Security\MicrosoftVolumeEncryption";
    private const string DeviceGuardNamespace = @"root\Microsoft\Windows\DeviceGuard";
    private static string TestEnvironmentMessage => T._("Недоступно в тестовом окружении — ничего не изменено.");

    private volatile bool _smb1RebootPending;

    /// <summary>SMBv1 was disabled in this session and Windows waits for a reboot to finish the change.</summary>
    internal bool Smb1RebootPending
    {
        get => _smb1RebootPending;
        set => _smb1RebootPending = value;
    }

    // =====================================================================
    // Read-only checks
    // =====================================================================

    /// <summary>SMBv1: the Windows feature list (works without admin), PowerShell as admin, the registry as a last resort.</summary>
    internal Smb1State ReadSmb1(CancellationToken ct)
    {
        var registry = ReadSmb1Registry();
        if (!Env.IsRealSystem)
            return Smb1State.Unknown with { Registry = registry };

        var features = new Dictionary<string, FeatureState>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Wmi.Query("SELECT Name, InstallState FROM Win32_OptionalFeature WHERE Name LIKE 'SMB1Protocol%'",
                     Wmi.Cimv2, WmiTimeout))
        {
            if (row.Str("Name") is { } name)
                features[name] = SecurityRules.MapInstallState(row.Long("InstallState"));
        }

        if (!features.ContainsKey("SMB1Protocol") && Admin.IsElevated)
        {
            var r = ProcessRunner.PowerShellAsync(
                    "Get-WindowsOptionalFeature -Online -FeatureName 'SMB1Protocol*' -ErrorAction Stop | ForEach-Object { $_.FeatureName + '=' + $_.State }", TimeSpan.FromSeconds(40), ct)
                .GetAwaiter().GetResult();
            if (r.Success)
                features = SecurityRules.ParseFeatureList(r.StdOut);
            else
                Log.Warn(T.F("Безопасность: Get-WindowsOptionalFeature SMB1Protocol: {0}", FirstLine(r.StdErr)));
        }

        FeatureState F(string name) => features.TryGetValue(name, out var s) ? s : FeatureState.Unknown;
        return new Smb1State(F("SMB1Protocol"), F("SMB1Protocol-Client"), F("SMB1Protocol-Server"), registry);
    }

    private Smb1Registry ReadSmb1Registry()
    {
        var smb1 = SecurityEnvironment.ReadValue(Env.LanmanServerParameters, "SMB1");
        var server = SecurityEnvironment.ReadValue(Env.Smb1ServerDriver, "Start");
        var client = SecurityEnvironment.ReadValue(Env.Smb1ClientDriver, "Start");
        return new Smb1Registry(smb1.Readable && server.Readable && client.Readable,
            SecurityEnvironment.AsInt(smb1.Value), SecurityEnvironment.AsInt(server.Value), SecurityEnvironment.AsInt(client.Value));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFirmwareType(out int firmwareType);

    internal SecureBootState ReadSecureBoot()
    {
        var value = SecurityEnvironment.ReadValue(Env.SecureBootState, "UEFISecureBootEnabled");
        bool? uefi = null;
        if (Env.IsRealSystem)
        {
            try
            {
                // FirmwareTypeBios = 1, FirmwareTypeUefi = 2.
                if (GetFirmwareType(out var type) && type is 1 or 2)
                    uefi = type == 2;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                uefi = null;
            }
        }
        return new SecureBootState(value.Readable, SecurityEnvironment.AsInt(value.Value), uefi);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint InterfaceType;
        public uint ImpRevision;
    }

    [DllImport("tbs.dll")]
    private static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

    /// <summary>
    /// TPM: Win32_Tpm (administrator only, the most detailed), then the TPM Base Services API (works for every user),
    /// then "tpmtool getdeviceinformation".
    /// </summary>
    internal TpmState ReadTpm(CancellationToken ct)
    {
        if (!Env.IsRealSystem)
            return TpmState.Unknown;

        if (Admin.IsElevated)
        {
            var row = Wmi.Query("SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm",
                TpmNamespace, WmiTimeout).FirstOrDefault();
            if (row is not null)
            {
                return new TpmState(true, row.Bool("IsEnabled_InitialValue"), row.Bool("IsActivated_InitialValue"),
                    SecurityRules.NormalizeTpmVersion(row.Str("SpecVersion")));
            }
        }

        try
        {
            var result = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), out var info);
            var fromTbs = SecurityRules.TpmFromTbs(result, info.TpmVersion);
            if (fromTbs.Present is not null)
                return fromTbs;
            Log.Info(T.F("Безопасность: служба TPM ответила кодом 0x{0:X8}, пробуем tpmtool", result));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Info(T.F("Безопасность: TPM Base Services недоступны ({0}), пробуем tpmtool", ErrorText.For(ex)), ex);
        }

        // Note: without a console window tpmtool prints only the values, without the labels — then nothing is parsed
        // and the card honestly says "Нет данных".
        var tool = Path.Combine(Environment.SystemDirectory, "tpmtool.exe");
        if (!File.Exists(tool))
            return TpmState.Unknown;
        // Classic console tools print in the OEM code page, newer builds sometimes in UTF-8: try both.
        foreach (var encoding in new Encoding?[] { null, new UTF8Encoding(false) })
        {
            var r = ProcessRunner.RunAsync(tool, "getdeviceinformation", TimeSpan.FromSeconds(20), ct, encoding)
                .GetAwaiter().GetResult();
            var parsed = SecurityRules.ParseTpmTool(r.StdOut);
            if (parsed.Present is not null)
                return parsed;
            if (r.TimedOut || string.IsNullOrWhiteSpace(r.StdOut))
            {
                Log.Warn(T.F("Безопасность: tpmtool не ответил (код {0}): {1}", r.ExitCode, FirstLine(r.StdErr)));
                break;
            }
        }
        return TpmState.Unknown;
    }

    /// <summary>System drive encryption: Win32_EncryptableVolume as admin, otherwise the Explorer drive property.</summary>
    internal BitLockerState ReadBitLocker(CancellationToken ct)
    {
        if (!Env.IsRealSystem)
            return BitLockerState.Unknown;
        var drive = (Path.GetPathRoot(Env.WindowsDir) ?? @"C:\").TrimEnd('\\');
        if (drive.Length != 2 || drive[1] != ':')
            drive = "C:";

        var encryption = DriveEncryption.Unknown;
        if (Admin.IsElevated)
        {
            var row = Wmi.Query($"SELECT ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{drive}'",
                BitLockerNamespace, WmiTimeout).FirstOrDefault();
            if (row is not null)
                encryption = SecurityRules.MapWmiBitLocker(row.Long("ProtectionStatus"), row.Long("ConversionStatus"));
        }
        if (encryption == DriveEncryption.Unknown)
            encryption = ReadShellBitLocker(drive, ct);

        var batteries = Wmi.Query("SELECT Name FROM Win32_Battery", Wmi.Cimv2, WmiTimeout).Count;
        var chassis = new List<int>();
        foreach (var row in Wmi.Query("SELECT ChassisTypes FROM Win32_SystemEnclosure", Wmi.Cimv2, WmiTimeout))
        {
            if (row.TryGetValue("ChassisTypes", out var v) && v is Array arr)
            {
                foreach (var item in arr)
                {
                    if (item is not null && int.TryParse(Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture), out var n))
                        chassis.Add(n);
                }
            }
        }
        var edition = SecurityEnvironment.ReadValue(Env.WindowsVersion, "EditionID").Value?.ToString();
        return new BitLockerState(drive, encryption, SecurityRules.IsLaptop(batteries, chassis), SecurityRules.IsHomeEdition(edition));
    }

    /// <summary>
    /// Reads Explorer's "System.Volume.BitLockerProtection" (the lock on the drive icon) — works without admin rights.
    /// Shell COM objects need an STA thread; if that fails, the same property is read through PowerShell.
    /// </summary>
    private static DriveEncryption ReadShellBitLocker(string drive, CancellationToken ct)
    {
        const string property = "System.Volume.BitLockerProtection";
        object? value = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            object? shell = null;
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type is null)
                    throw ErrorText.Own(new COMException(T._("Shell.Application недоступен")));
                shell = Activator.CreateInstance(type);
                dynamic folder = ((dynamic)shell!).NameSpace(drive);
                value = folder is null ? null : folder.Self.ExtendedProperty(property);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                if (shell is not null && Marshal.IsComObject(shell))
                    Marshal.FinalReleaseComObject(shell);
            }
        })
        {
            IsBackground = true,
            Name = "BitLocker shell property",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (thread.Join(TimeSpan.FromSeconds(8)) && error is null)
            return SecurityRules.MapShellBitLocker(value);

        Log.Info(T.F("Безопасность: свойство шифрования диска недоступно напрямую ({0}), читаем через PowerShell", error is null ? T._("нет ответа") : ErrorText.For(error)), error);
        var r = ProcessRunner.PowerShellAsync(
                $"$v = (New-Object -ComObject Shell.Application).NameSpace('{drive}').Self.ExtendedProperty('{property}')\r\n'VALUE:' + $v", TimeSpan.FromSeconds(30), ct)
            .GetAwaiter().GetResult();
        var line = r.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("VALUE:", StringComparison.Ordinal));
        if (line is null)
        {
            Log.Warn(T.F("Безопасность: не удалось узнать состояние шифрования диска: {0}", FirstLine(r.StdErr)));
            return DriveEncryption.Unknown;
        }
        var text = line["VALUE:".Length..].Trim();
        return SecurityRules.MapShellBitLocker(text.Length == 0 ? null : text);
    }

    internal DeviceGuardState ReadDeviceGuard()
    {
        var registry = SecurityEnvironment.AsInt(SecurityEnvironment.ReadValue(Env.HvciScenario, "Enabled").Value);
        if (!Env.IsRealSystem)
            return DeviceGuardState.Unknown with { HvciRegistryEnabled = registry };
        var row = Wmi.Query(
            "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning, SecurityServicesConfigured FROM Win32_DeviceGuard",
            DeviceGuardNamespace, WmiTimeout).FirstOrDefault();
        if (row is null)
            return DeviceGuardState.Unknown with { HvciRegistryEnabled = registry };
        return new DeviceGuardState(true, (int?)row.Long("VirtualizationBasedSecurityStatus"),
            IntArray(row, "SecurityServicesRunning"), IntArray(row, "SecurityServicesConfigured"), registry);
    }

    private static IReadOnlyList<int> IntArray(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is not Array arr)
            return Array.Empty<int>();
        var list = new List<int>();
        foreach (var item in arr)
        {
            if (item is not null && int.TryParse(Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture), out var n))
                list.Add(n);
        }
        return list;
    }

    /// <summary>Active (not neutralized, not allowed) Defender detections right now.</summary>
    private int CountActiveThreats()
        => Wmi.Query("SELECT ThreatStatusID FROM MSFT_MpThreatDetection", Wmi.Defender, WmiTimeout)
            .Count(row => row.Long("ThreatStatusID") is { } id &&
                          !SecurityRules.IsThreatResolved((int)id) && !SecurityRules.IsThreatAllowed((int)id));

    // =====================================================================
    // Actions (the caller confirms first)
    // =====================================================================

    /// <summary>
    /// Turns off the Windows feature "SMB 1.0/CIFS File Sharing Support" (Disable-WindowsOptionalFeature -NoRestart)
    /// after a restore point. Needs administrator rights; a reboot finishes the change.
    /// </summary>
    public async Task<SecurityActionResult> DisableSmb1Async(CancellationToken ct)
    {
        if (!Env.IsRealSystem)
            return SecurityActionResult.Fail(TestEnvironmentMessage);
        if (!Admin.IsElevated)
            return SecurityActionResult.Fail(NeedAdminMessage);

        var restorePoint = await RestorePoint.CreateAsync(T._("Отключение SMBv1"), ct);
        ct.ThrowIfCancellationRequested();
        // Not cancellable once started: "Отмена" would kill PowerShell together with DismHost in the middle of
        // servicing the component store. The generous timeout is only a safety net for a hung servicing stack.
        var r = await ProcessRunner.PowerShellAsync(
            "$r = Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -NoRestart -ErrorAction Stop\r\n'RESTART:' + $r.RestartNeeded", TimeSpan.FromMinutes(30), CancellationToken.None);
        var restartLine = r.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("RESTART:", StringComparison.Ordinal));
        if (!r.Success || restartLine is null)
        {
            var reason = r.TimedOut ? T._("Windows не ответила вовремя") : FirstLine(r.StdErr + "\n" + r.StdOut);
            Log.Warn(T.F("Не удалось отключить SMBv1: {0}", reason));
            return SecurityActionResult.Failed(
                T.F("Не удалось отключить SMBv1: {0}.\n\nОтключите его вручную: «Включение или отключение компонентов Windows» → снимите флажок «Поддержка общего доступа к файлам SMB 1.0/CIFS».", reason));
        }
        var restart = !restartLine.EndsWith("False", StringComparison.OrdinalIgnoreCase);
        // Until the reboot the SMBv1 driver keeps running: the next scans say so instead of "SMBv1 выключен"
        // (or, if Windows still lists the feature as enabled, instead of offering to disable it again).
        Smb1RebootPending = restart;
        Log.Action(T.F("Отключён компонент Windows «Поддержка общего доступа к файлам SMB 1.0/CIFS» (SMB1Protocol){0}. {1}",
            restart ? ", " + T._("нужна перезагрузка") : "", SecurityRules.RestorePointText(restorePoint)));
        return SecurityActionResult.Ok(SecurityRules.Smb1DisabledText(restart, restorePoint));
    }

    /// <summary>
    /// Asks Microsoft Defender to apply its default action (usually quarantine) to every active detection
    /// (Remove-MpThreat). Needs administrator rights. Quarantined files can be restored in "Журнал защиты".
    /// </summary>
    public async Task<SecurityActionResult> RemoveActiveThreatsAsync(CancellationToken ct)
    {
        if (!Env.IsRealSystem)
            return SecurityActionResult.Fail(TestEnvironmentMessage);
        if (!Admin.IsElevated)
            return SecurityActionResult.Fail(NeedAdminMessage);

        var before = await Task.Run(CountActiveThreats, ct);
        if (before == 0)
            return SecurityActionResult.Ok(T._("Необезвреженных угроз уже нет — делать ничего не нужно."));
        Log.Action(T.F("Запущено обезвреживание угроз Защитником Windows (Remove-MpThreat), активных угроз: {0}", before));
        var r = await ProcessRunner.PowerShellAsync("Remove-MpThreat -ErrorAction Stop\r\n'OK'", TimeSpan.FromMinutes(10), ct);
        // Defender finishes the remediation in its service: give it a moment before counting again. Remove-MpThreat
        // has run by now, so "Отмена" no longer hides the outcome (the wait is only a few seconds).
        var after = before;
        for (var attempt = 0; attempt < 3 && after > 0; attempt++)
        {
            await Task.Delay(1500, CancellationToken.None);
            after = await Task.Run(CountActiveThreats, CancellationToken.None);
        }
        var removed = Math.Max(0, before - after);
        if (after > 0)
            Log.Warn(T.F("Remove-MpThreat: осталось активных угроз {0} из {1}. {2}", after, before, FirstLine(r.StdErr)));
        if (removed > 0)
            Log.Action(T.F("Защитник Windows обезвредил угрозы: {0} из {1}", removed, before));
        return SecurityRules.ThreatRemovalOutcome(before, after, FirstLine(r.StdErr));
    }
}
