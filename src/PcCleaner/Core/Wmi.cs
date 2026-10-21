using System.Management;

namespace PcCleaner.Core;

/// <summary>
/// Small WMI helper. Returns rows as case-insensitive dictionaries; never throws on access/namespace errors
/// (logs a warning and returns an empty list), so callers work without admin rights.
/// </summary>
public static class Wmi
{
    public const string Cimv2 = @"root\cimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string Defender = @"root\Microsoft\Windows\Defender";
    public const string SecurityCenter2 = @"root\SecurityCenter2";
    public const string StandardCimv2 = @"root\StandardCimv2";
    public const string WmiRoot = @"root\wmi";

    public static List<Dictionary<string, object?>> Query(string query, string scope = Cimv2, TimeSpan? timeout = null)
    {
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            var options = new EnumerationOptions
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(20),
                ReturnImmediately = true,
                Rewindable = false,
            };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), options);
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                using (obj)
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in obj.Properties)
                        row[prop.Name] = prop.Value;
                    rows.Add(row);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            Log.Warn($"WMI ({scope}) «{query}»: {ErrorText.For(ex)}", ex);
        }
        return rows;
    }

    public static Task<List<Dictionary<string, object?>>> QueryAsync(string query, string scope = Cimv2,
        CancellationToken ct = default, TimeSpan? timeout = null)
        => Task.Run(() => Query(query, scope, timeout), ct);

    public static string? Str(this Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is not null ? v.ToString()?.Trim() : null;

    public static long? Long(this Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null)
            return null;
        try
        {
            return Convert.ToInt64(v);
        }
        catch
        {
            return long.TryParse(v.ToString(), out var l) ? l : null;
        }
    }

    public static bool? Bool(this Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is bool b ? b : null;

    /// <summary>Parses WMI CIM_DATETIME ("20240131120000.000000+180").</summary>
    public static DateTime? Date(this Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null)
            return null;
        if (v is DateTime dt)
            return dt;
        try
        {
            return ManagementDateTimeConverter.ToDateTime(v.ToString()!);
        }
        catch
        {
            return null;
        }
    }
}
