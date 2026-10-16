namespace PcCleaner.Core;

/// <summary>Creates Windows restore points before batches of system changes.</summary>
public static class RestorePoint
{
    private static DateTime _lastCreated = DateTime.MinValue;

    /// <summary>
    /// Creates a restore point if enabled in settings and running as admin.
    /// Never throws; returns true if a point was created (or one was already created in this session recently).
    /// </summary>
    public static async Task<bool> TryCreateAsync(string description, CancellationToken ct = default, bool force = false)
    {
        if (!force && !AppServices.Settings.CreateRestorePoint)
            return false;
        if (!Admin.IsElevated)
        {
            Log.Warn("Точка восстановления не создана: нужны права администратора");
            return false;
        }
        if (!force && DateTime.Now - _lastCreated < TimeSpan.FromMinutes(30))
            return true;

        var safe = description.Replace("'", "''");
        if (safe.Length > 60)
            safe = safe[..60];
        var script =
            "try { Enable-ComputerRestore -Drive \"$env:SystemDrive\\\" -ErrorAction SilentlyContinue } catch {};" +
            $"Checkpoint-Computer -Description 'WaterCa7s PC Cleaner: {safe}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop;" +
            "'OK'";
        try
        {
            var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromMinutes(3), ct);
            if (result.Success && result.StdOut.Contains("OK"))
            {
                _lastCreated = DateTime.Now;
                Log.Action($"Создана точка восстановления: {description}");
                return true;
            }
            // Windows allows only one restore point per 24h by default; that is not an error for us.
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
        return false;
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? "неизвестная причина" : line;
    }
}
