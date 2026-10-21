using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PcCleaner.Modules.Disk;

/// <summary>
/// Parsers for the output of console tools (fsutil, vssadmin, defrag, PowerShell JSON).
/// They accept both English and Russian Windows output.
/// </summary>
internal static class ToolOutputParsers
{
    private static readonly Regex DeleteNotifyRegex = new(
        @"^\s*(?:(?<fs>NTFS|ReFS)\s+)?DisableDeleteNotify\s*=\s*(?<v>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex VolumeRegex = new(@"\((?<v>[A-Za-z]:)\)", RegexOptions.CultureInvariant);

    // l10n:data-begin — Russian words of the output of Windows tools, matched and never shown
    private static readonly Regex SizeRegex = new(
        @"(?<num>\d[\d   ]*(?:[.,]\d+)?)\s*(?<unit>bytes|байт|[KMGTPE]?B|[КМГТП]?Б)(?![\p{L}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // l10n:data-end

    private static readonly Regex PercentRegex = new(@"\((?<p>\d+(?:[.,]\d+)?)\s*%\)", RegexOptions.CultureInvariant);

    private static readonly Regex ProgressRegex = new(@"(?<p>\d{1,3})(?:[.,]\d+)?\s*%", RegexOptions.CultureInvariant);

    /// <summary>
    /// "NTFS DisableDeleteNotify = 0  (...)" (Windows 10/11) or "DisableDeleteNotify = 0" (older builds).
    /// 0 means TRIM is enabled.
    /// </summary>
    public static TrimState ParseTrimState(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return TrimState.Unknown;
        string? ntfs = null;
        string? generic = null;
        foreach (Match m in DeleteNotifyRegex.Matches(output))
        {
            var fs = m.Groups["fs"].Value;
            if (fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                ntfs ??= m.Groups["v"].Value;
            else if (fs.Length == 0)
                generic ??= m.Groups["v"].Value;
        }
        var value = ntfs ?? generic;
        return value switch
        {
            null => TrimState.Unknown,
            "0" => TrimState.Enabled,
            _ => TrimState.Disabled,
        };
    }

    /// <summary>
    /// Parses "vssadmin list shadowstorage". Each association block yields the volume letter and the
    /// used / allocated / maximum sizes (maximum is null when unbounded).
    /// </summary>
    public static List<ShadowStorageInfo> ParseShadowStorage(string? output)
    {
        var result = new List<ShadowStorageInfo>();
        if (string.IsNullOrWhiteSpace(output))
            return result;

        string? volume = null;
        var sizes = new List<(string Kind, long? Bytes, double? Percent)>();

        void Flush()
        {
            if (volume is not null && sizes.Count > 0)
            {
                long? used = null, allocated = null, max = null;
                double? maxPercent = null;
                for (var i = 0; i < sizes.Count; i++)
                {
                    var (kind, bytes, percent) = sizes[i];
                    var resolved = kind != "?" ? kind : i switch { 0 => "used", 1 => "allocated", _ => "max" };
                    switch (resolved)
                    {
                        case "used":
                            used = bytes;
                            break;
                        case "allocated":
                            allocated = bytes;
                            break;
                        default:
                            max = bytes;
                            maxPercent = percent;
                            break;
                    }
                }
                result.Add(new ShadowStorageInfo(volume, used, allocated, max, maxPercent));
            }
            volume = null;
            sizes.Clear();
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            var vol = VolumeRegex.Match(value);
            if (vol.Success)
            {
                // The first "(C:)" line of a block is "For volume", the second is the storage volume.
                if (volume is null || sizes.Count > 0)
                {
                    if (sizes.Count > 0)
                        Flush();
                    volume = vol.Groups["v"].Value.ToUpperInvariant();
                }
                continue;
            }

            if (volume is null)
                continue;

            var kind = ClassifyShadowKey(key);
            var percentMatch = PercentRegex.Match(value);
            double? percent = percentMatch.Success ? ParseDouble(percentMatch.Groups["p"].Value) : null;
            var bytes = ParseSize(value);
            if (bytes is null && !percentMatch.Success)
                continue;
            sizes.Add((kind, bytes, percent));
        }
        Flush();
        return result;
    }

    private static string ClassifyShadowKey(string key)
    {
        var k = key.ToLowerInvariant();
        // l10n:data-begin — Russian words of the output of Windows tools, matched and never shown
        if (k.Contains("used") || k.Contains("использ"))
            return "used";
        if (k.Contains("allocated") || k.Contains("выделен"))
            return "allocated";
        if (k.Contains("maximum") || k.Contains("максим"))
            return "max";
        // l10n:data-end
        return "?";
    }

    /// <summary>"1.23 GB", "9,31 ГБ", "0 B", "512 bytes" -> bytes (binary multiples). Null if no size found.</summary>
    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var m = SizeRegex.Match(text);
        if (!m.Success)
            return null;
        var number = ParseDouble(m.Groups["num"].Value);
        if (number is null)
            return null;
        var unit = m.Groups["unit"].Value.ToUpperInvariant();
        var multiplier = unit switch
        {
            // l10n:data-begin — Russian words of the output of Windows tools, matched and never shown
            "KB" or "КБ" => 1L << 10,
            "MB" or "МБ" => 1L << 20,
            "GB" or "ГБ" => 1L << 30,
            "TB" or "ТБ" => 1L << 40,
            "PB" or "ПБ" => 1L << 50,
            // l10n:data-end
            "EB" => 1L << 60,
            _ => 1L,
        };
        return (long)Math.Round(number.Value * multiplier);
    }

    private static double? ParseDouble(string s)
    {
        var cleaned = s.Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace(',', '.');
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Last "NN%" in a progress line of defrag.exe /U, or null.</summary>
    public static double? ParseProgressPercent(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return null;
        var matches = ProgressRegex.Matches(line);
        if (matches.Count == 0)
            return null;
        var value = int.Parse(matches[^1].Groups["p"].Value, CultureInfo.InvariantCulture);
        return value is >= 0 and <= 100 ? value : null;
    }

    /// <summary>
    /// Parses JSON produced by Get-PhysicalDisk | Get-StorageReliabilityCounter | ConvertTo-Json
    /// (single object or array). Zero temperatures are treated as "not reported".
    /// </summary>
    public static Dictionary<string, ReliabilityInfo> ParseReliabilityJson(string? json)
    {
        var result = new Dictionary<string, ReliabilityInfo>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return result;
        var start = json.IndexOfAny(new[] { '[', '{' });
        if (start < 0)
            return result;
        try
        {
            using var doc = JsonDocument.Parse(json[start..]);
            IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new[] { doc.RootElement };
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var id = GetString(item, "DeviceId");
                if (string.IsNullOrEmpty(id))
                    continue;
                var temp = GetLong(item, "Temperature");
                var tempMax = GetLong(item, "TemperatureMax");
                var info = new ReliabilityInfo(
                    id,
                    temp is > 0 and < 200 ? (int)temp : null,
                    tempMax is > 0 and < 200 ? (int)tempMax : null,
                    GetLong(item, "Wear") is { } wear and >= 0 and <= 100 ? (int)wear : null,
                    GetLong(item, "PowerOnHours"),
                    GetLong(item, "ReadErrorsUncorrected"),
                    GetLong(item, "WriteErrorsUncorrected"));
                result[id] = info;
            }
        }
        catch (JsonException)
        {
            // malformed output -> no counters
        }
        return result;
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static long? GetLong(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l))
            return l;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            return (long)d;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s))
            return s;
        return null;
    }

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
