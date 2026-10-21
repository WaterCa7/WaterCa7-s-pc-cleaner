using System.Management;
using System.Runtime.InteropServices;
using PcCleaner.Core.Localization;
using PcCleaner.Core;

namespace PcCleaner.Modules.Optimizer;

/// <summary>
/// WMI query for optional sensors (root\wmi thermal zones, battery capacity). Unlike Core's <see cref="Wmi.Query"/>, it tells
/// "needs administrator rights" from "not supported by this PC", so the page can explain why a value is missing,
/// and it does not fill the log with warnings for these expected cases. Read-only.
/// </summary>
internal static class WmiSensorQuery
{
    // WBEM_E_* codes (ManagementStatus) that simply mean "this PC does not have it".
    private static readonly ManagementStatus[] Unsupported =
    {
        ManagementStatus.InvalidClass, ManagementStatus.NotSupported, ManagementStatus.InvalidNamespace,
        ManagementStatus.NotFound, ManagementStatus.ProviderLoadFailure, ManagementStatus.ProviderNotFound,
    };

    public static (List<Dictionary<string, object?>> Rows, SensorState State) Query(string query, string scope, TimeSpan timeout)
    {
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            var options = new EnumerationOptions { Timeout = timeout, ReturnImmediately = true, Rewindable = false };
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
            return (rows, rows.Count > 0 ? SensorState.Available : SensorState.NotSupported);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return (rows, SensorState.NeedsAdmin);
        }
        catch (UnauthorizedAccessException)
        {
            return (rows, SensorState.NeedsAdmin);
        }
        catch (ManagementException ex) when (Unsupported.Contains(ex.ErrorCode))
        {
            return (rows, SensorState.NotSupported);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or TimeoutException)
        {
            Log.Warn(T.F("Ускорение: WMI ({0}) «{1}»: {2}", scope, query, ErrorText.For(ex)), ex);
            return (rows, SensorState.Unknown);
        }
    }
}
