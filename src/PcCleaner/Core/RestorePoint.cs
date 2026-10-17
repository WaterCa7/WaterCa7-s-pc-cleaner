using System.Text;

namespace PcCleaner.Core;

public enum RestorePointResult
{
    /// <summary>A new restore point was created.</summary>
    Created,
    /// <summary>Windows skipped it: a point was already created recently (default limit: one per 24 h).</summary>
    RecentPointExists,
    /// <summary>Disabled in the app settings.</summary>
    DisabledInSettings,
    /// <summary>The app is not running as administrator.</summary>
    NotElevated,
    /// <summary>System Restore is off or the command failed.</summary>
    Failed,
}

/// <summary>Creates Windows restore points before batches of system changes.</summary>
public static class RestorePoint
{
    private static DateTime _lastCreated = DateTime.MinValue;

    /// <summary>
    /// Creates a restore point if enabled in settings and running as admin. Never throws (except cancellation).
    /// Returns true if the system is protected by a fresh point: created now, created by this app in the last
    /// 30 minutes, or skipped by Windows because a point already exists from the last 24 hours.
    /// Never turns System Protection on by itself: if it is off, no point is created (false / Failed). Only
    /// <paramref name="force"/> = true (the explicit manual action in Settings, whose confirmation says so) enables it,
    /// and that change is recorded with Log.Action.
    /// </summary>
    public static async Task<bool> TryCreateAsync(string description, CancellationToken ct = default, bool force = false)
    {
        var result = await CreateAsync(description, ct, force);
        return result is RestorePointResult.Created or RestorePointResult.RecentPointExists;
    }

    /// <summary>Same as <see cref="TryCreateAsync"/> but tells exactly what happened.</summary>
    public static async Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default, bool force = false)
    {
        if (!force && !AppServices.Settings.CreateRestorePoint)
            return RestorePointResult.DisabledInSettings;
        if (!Admin.IsElevated)
        {
            Log.Warn("Точка восстановления не создана: нужны права администратора");
            return RestorePointResult.NotElevated;
        }
        if (!force && DateTime.Now - _lastCreated < TimeSpan.FromMinutes(30))
            return RestorePointResult.Created;

        var script = BuildScript(description, force);
        try
        {
            var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromMinutes(3), ct);
            if (result.StdOut.Contains(ProtectionEnabledMarker))
                Log.Action($"Включена защита системы для диска {SystemDrive} (нужна для точки восстановления: {description})");
            if (result.StdOut.Contains("RESULT:CREATED"))
            {
                _lastCreated = DateTime.Now;
                Log.Action($"Создана точка восстановления: {description}");
                return RestorePointResult.Created;
            }
            if (result.StdOut.Contains("RESULT:SKIPPED"))
            {
                Log.Info("Windows не создала новую точку восстановления: последняя создана меньше суток назад — она и защищает систему");
                return RestorePointResult.RecentPointExists;
            }
            Log.Warn(result.StdOut.Contains(ProtectionOffMarker)
                ? $"Точка восстановления не создана: защита системы для диска {SystemDrive} выключена. Программа сама её не включает — " +
                  "это можно сделать в «Настройки» → «Восстановление системы» (кнопка «Создать точку восстановления сейчас» " +
                  "или «Настроить защиту»)"
                : $"Точка восстановления не создана: {FirstLine(result.StdErr)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Точка восстановления не создана: {ErrorText.For(ex)}");
        }
        return RestorePointResult.Failed;
    }

    /// <summary>
    /// Builds the PowerShell script (pure, unit-tested).
    /// <para>
    /// The description is carried into the script as Base64 and decoded there, never interpolated into the command:
    /// it may come from untrusted sources (e.g. a program's registry DisplayName), and Windows PowerShell also treats
    /// the Unicode "smart" quotes U+2018/U+2019/U+201C/U+201D as string delimiters, so a naive single-quoted string
    /// (escaping only the ASCII apostrophe) could be broken out of and the rest run as code.
    /// </para>
    /// <para>
    /// <paramref name="force"/> also controls whether System Protection is turned on. Enabling it is a lasting system
    /// change, so it happens only for the explicit, disclosed manual action. Automatic points never silently turn
    /// protection on: if it is off, Checkpoint-Computer fails and the caller gets <see cref="RestorePointResult.Failed"/>.
    /// </para>
    /// </summary>
    internal static string BuildScript(string description, bool force)
    {
        var text = "WaterCa7s PC Cleaner: " + Truncate(description ?? "", 60);
        // Base64 of UTF-8: only [A-Za-z0-9+/=], so it is safe inside the single-quoted literal below.
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        // RPSessionInterval is 0 while System Protection is off (the same value the Settings page shows).
        var protection =
            "function Prot { try { (Get-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore' -Name RPSessionInterval -ErrorAction Stop).RPSessionInterval } catch { $null } }\r\n" +
            "$p0 = Prot\r\n";
        var enable = force
            ? "try { Enable-ComputerRestore -Drive \"$env:SystemDrive\\\" -ErrorAction SilentlyContinue } catch {}\r\n" +
              "$p1 = Prot\r\n" +
              $"if ($p0 -eq 0 -and $null -ne $p1 -and $p1 -ne 0) {{ '{ProtectionEnabledMarker}' }}\r\n"
            : $"if ($p0 -eq 0) {{ '{ProtectionOffMarker}' }}\r\n";

        // Compare the newest sequence number before/after: Checkpoint-Computer only *warns* when Windows
        // skips the point because of SystemRestorePointCreationFrequency.
        return
            protection +
            enable +
            "function Last { try { (Get-ComputerRestorePoint -ErrorAction Stop | Sort-Object SequenceNumber -Descending | Select-Object -First 1).SequenceNumber } catch { -1 } }\r\n" +
            $"$desc = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{b64}'))\r\n" +
            "$before = Last\r\n" +
            "Checkpoint-Computer -Description $desc -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop -WarningVariable w -WarningAction SilentlyContinue\r\n" +
            "$after = Last\r\n" +
            "if ($after -ne -1 -and $after -ne $before) { 'RESULT:CREATED' } elseif ($w) { 'RESULT:SKIPPED' } elseif ($after -eq -1) { 'RESULT:CREATED' } else { 'RESULT:SKIPPED' }\r\n";
    }

    internal const string ProtectionEnabledMarker = "PROTECTION:ENABLED";
    internal const string ProtectionOffMarker = "PROTECTION:OFF";

    private static string SystemDrive => Environment.GetEnvironmentVariable("SystemDrive") is { Length: > 0 } d ? d : "C:";

    /// <summary>Cuts to <paramref name="max"/> chars without splitting a surrogate pair (emoji etc.).</summary>
    internal static string Truncate(string s, int max)
    {
        if (s.Length <= max)
            return s;
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut];
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? "защита системы выключена или неизвестная ошибка" : line;
    }
}
