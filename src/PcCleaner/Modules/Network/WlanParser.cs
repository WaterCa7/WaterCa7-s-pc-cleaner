using System.Text.RegularExpressions;

namespace PcCleaner.Modules.Network;

/// <summary>
/// Parses the output of "netsh wlan show interfaces" in English and Russian Windows.
/// Every interface block starts with "Name"/"Имя"; unknown lines are ignored.
/// </summary>
public static class WlanParser
{
    private static readonly Regex Line = new(@"^\s*(?<key>[^:]+?)\s*:\s?(?<value>.*)$", RegexOptions.Compiled);

    private enum Field
    {
        Name,
        Description,
        Guid,
        State,
        Ssid,
        Bssid,
        Band,
        Channel,
        Radio,
        Auth,
        Cipher,
        Receive,
        Transmit,
        Signal,
        Rssi,
        Profile,
    }

    private static readonly Dictionary<string, Field> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = Field.Name,
        ["Имя"] = Field.Name,
        ["Description"] = Field.Description,
        ["Описание"] = Field.Description,
        ["GUID"] = Field.Guid,
        ["Идентификатор GUID"] = Field.Guid,
        ["State"] = Field.State,
        ["Состояние"] = Field.State,
        ["SSID"] = Field.Ssid,
        ["BSSID"] = Field.Bssid,
        ["AP BSSID"] = Field.Bssid,
        ["BSSID точки доступа"] = Field.Bssid,
        ["Band"] = Field.Band,
        ["Диапазон"] = Field.Band,
        ["Channel"] = Field.Channel,
        ["Канал"] = Field.Channel,
        ["Radio type"] = Field.Radio,
        ["Тип радио"] = Field.Radio,
        ["Тип радиомодуля"] = Field.Radio,
        ["Authentication"] = Field.Auth,
        ["Проверка подлинности"] = Field.Auth,
        ["Cipher"] = Field.Cipher,
        ["Шифр"] = Field.Cipher,
        ["Signal"] = Field.Signal,
        ["Сигнал"] = Field.Signal,
        ["Rssi"] = Field.Rssi,
        ["Profile"] = Field.Profile,
        ["Профиль"] = Field.Profile,
    };

    private static Field? Resolve(string key)
    {
        if (Keys.TryGetValue(key, out var f))
            return f;
        // "Receive rate (Mbps)", "Скорость приема (Мбит/с)", "Скорость приёма (Мбит/с)"
        if (key.StartsWith("Receive rate", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Скорость прием", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Скорость приём", StringComparison.OrdinalIgnoreCase))
            return Field.Receive;
        if (key.StartsWith("Transmit rate", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Скорость передач", StringComparison.OrdinalIgnoreCase))
            return Field.Transmit;
        return null;
    }

    public static IReadOnlyList<WifiInterfaceInfo> Parse(string? output)
    {
        var result = new List<WifiInterfaceInfo>();
        if (string.IsNullOrWhiteSpace(output))
            return result;

        Dictionary<Field, string>? current = null;
        foreach (var raw in output.Split('\n'))
        {
            var m = Line.Match(raw.TrimEnd('\r'));
            if (!m.Success)
                continue;
            var field = Resolve(m.Groups["key"].Value.Trim());
            if (field is null)
                continue;
            var value = m.Groups["value"].Value.Trim();
            if (field == Field.Name)
            {
                if (current is not null)
                    result.Add(Build(current));
                current = new Dictionary<Field, string>();
            }
            if (current is null)
                continue; // a value before the first "Name" line
            current.TryAdd(field.Value, value);
        }
        if (current is not null)
            result.Add(Build(current));
        return result;
    }

    /// <summary>Windows 11 hides Wi-Fi details from netsh while apps may not use the location.</summary>
    public static bool IsLocationBlocked(string? output)
        => !string.IsNullOrEmpty(output) &&
           (output.Contains("location permission", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("местоположени", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("расположени", StringComparison.OrdinalIgnoreCase));

    private static WifiInterfaceInfo Build(Dictionary<Field, string> f)
    {
        string? Get(Field key) => f.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        var state = Get(Field.State) ?? "";
        var channel = NetworkRules.ParseNumber(Get(Field.Channel)) is { } ch ? (int?)(int)ch : null;
        var band = Get(Field.Band);
        var ghz = ParseBand(band) ?? GuessBand(channel);
        var signal = NetworkRules.ParseNumber(Get(Field.Signal)) is { } s ? (int?)Math.Clamp((int)Math.Round(s), 0, 100) : null;
        var rssi = NetworkRules.ParseNumber(Get(Field.Rssi)) is { } r ? (int?)(int)r : null;
        return new WifiInterfaceInfo
        {
            Name = Get(Field.Name) ?? "",
            Description = Get(Field.Description) ?? "",
            Guid = Get(Field.Guid)?.Trim('{', '}'),
            State = state,
            IsConnected = IsConnectedState(state),
            Ssid = Get(Field.Ssid),
            Bssid = Get(Field.Bssid),
            Band = band,
            BandGhz = ghz,
            Channel = channel,
            RadioType = Get(Field.Radio),
            Authentication = Get(Field.Auth),
            Cipher = Get(Field.Cipher),
            ReceiveMbps = NetworkRules.ParseNumber(Get(Field.Receive)),
            TransmitMbps = NetworkRules.ParseNumber(Get(Field.Transmit)),
            SignalPercent = signal,
            Rssi = rssi,
            Profile = Get(Field.Profile),
        };
    }

    /// <summary>"connected" / "подключено" (but not "disconnected" / "отключено").</summary>
    public static bool IsConnectedState(string? state)
    {
        var s = (state ?? "").Trim().ToLowerInvariant();
        return s == "connected" || s.StartsWith("подключ");
    }

    /// <summary>"5 GHz", "2.4 GHz", "2,4 ГГц", "6 ГГц" → 5 / 2.4 / 6.</summary>
    public static double? ParseBand(string? band)
    {
        var n = NetworkRules.ParseNumber(band);
        return n is > 0 and < 100 ? n : null;
    }

    /// <summary>Older Windows has no "Band" line: channels 1–14 are 2.4 GHz, 32–177 are 5 GHz.</summary>
    public static double? GuessBand(int? channel) => channel switch
    {
        >= 1 and <= 14 => 2.4,
        >= 32 and <= 177 => 5,
        _ => null,
    };
}
