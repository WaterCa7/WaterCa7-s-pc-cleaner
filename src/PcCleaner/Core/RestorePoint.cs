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

        var safe = description.Replace("'", "''");
        if (safe.Length > 60)
            safe = safe[..60];
        // Compare the newest sequence number before/after: Checkpoint-Computer only *warns* when Windows
        // skips the point because of SystemRestorePointCreationFrequency.
        var script =
            "try { Enable-ComputerRestore -Drive \"$env:SystemDrive\\\" -ErrorAction SilentlyContinue } catch {}\r\n" +
            "function Last { try { (Get-ComputerRestorePoint -ErrorAction Stop | Sort-Object SequenceNumber -Descending | Select-Object -First 1).SequenceNumber } catch { -1 } }\r\n" +
            "$before = Last\r\n" +
            $"Checkpoint-Computer -Description 'WaterCa7s PC Cleaner: {safe}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop -WarningVariable w -WarningAction SilentlyContinue\r\n" +
            "$after = Last\r\n" +
            "if ($after -ne -1 -and $after -ne $before) { 'RESULT:CREATED' } elseif ($w) { 'RESULT:SKIPPED' } elseif ($after -eq -1) { 'RESULT:CREATED' } else { 'RESULT:SKIPPED' }\r\n";
        try
        {
            var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromMinutes(3), ct);
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
            Log.Warn($"Точка восстановления не создана: {FirstLine(result.StdErr)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Точка восстановления не создана: {ex.Message}");
        }
        return RestorePointResult.Failed;
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? "защита системы выключена или неизвестная ошибка" : line;
    }
}
