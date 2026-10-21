using System.Text.RegularExpressions;

namespace PcCleaner.Modules.Network;

/// <summary>
/// Parses the output of "netsh wlan show interfaces". The labels are translated by Windows: English, Russian, Ukrainian
/// and German ones are known. In other languages every interface block still starts with its name (the first
/// "label : value" line), the GUID is recognised by its value, and an interface that reports a signal and a network
/// name counts as connected. Unknown lines are ignored.
/// </summary>
public static class WlanParser
{
    private static readonly Regex Line = new(@"^\s*(?<key>[^:]+?)\s*:\s?(?<value>.*)$", RegexOptions.Compiled);

    private static readonly Regex GuidValue = new(@"^\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        // l10n:data-begin — the words of netsh output (any Windows language), matched and never shown
        ["Name"] = Field.Name,
        ["Имя"] = Field.Name,
        ["Ім'я"] = Field.Name,
        ["Ім’я"] = Field.Name,
        ["Description"] = Field.Description,
        ["Описание"] = Field.Description,
        ["Опис"] = Field.Description,
        ["Beschreibung"] = Field.Description,
        ["GUID"] = Field.Guid,
        ["Идентификатор GUID"] = Field.Guid,
        ["State"] = Field.State,
        ["Состояние"] = Field.State,
        ["Стан"] = Field.State,
        ["Status"] = Field.State,
        ["SSID"] = Field.Ssid,
        ["BSSID"] = Field.Bssid,
        ["AP BSSID"] = Field.Bssid,
        ["BSSID точки доступа"] = Field.Bssid,
        ["Band"] = Field.Band,
        ["Диапазон"] = Field.Band,
        ["Channel"] = Field.Channel,
        ["Канал"] = Field.Channel,
        ["Kanal"] = Field.Channel,
        ["Radio type"] = Field.Radio,
        ["Тип радио"] = Field.Radio,
        ["Тип радиомодуля"] = Field.Radio,
        ["Authentication"] = Field.Auth,
        ["Проверка подлинности"] = Field.Auth,
        ["Authentifizierung"] = Field.Auth,
        ["Cipher"] = Field.Cipher,
        ["Шифр"] = Field.Cipher,
        ["Verschlüsselung"] = Field.Cipher,
        ["Signal"] = Field.Signal,
        ["Сигнал"] = Field.Signal,
        ["Rssi"] = Field.Rssi,
        ["Profile"] = Field.Profile,
        ["Профиль"] = Field.Profile,
        ["Профіль"] = Field.Profile,
        ["Profil"] = Field.Profile,
        // l10n:data-end
    };

    private static Field? Resolve(string key)
    {
        if (Keys.TryGetValue(key, out var f))
            return f;
        // l10n:data-begin — the words of netsh output, matched and never shown
        // "Receive rate (Mbps)", "Скорость приема (Мбит/с)", "Скорость приёма (Мбит/с)"
        if (key.StartsWith("Receive rate", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Скорость прием", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Скорость приём", StringComparison.OrdinalIgnoreCase))
            return Field.Receive;
        if (key.StartsWith("Transmit rate", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Скорость передач", StringComparison.OrdinalIgnoreCase))
        // l10n:data-end
            return Field.Transmit;
        return null;
    }

    public static IReadOnlyList<WifiInterfaceInfo> Parse(string? output)
    {
        var result = new List<WifiInterfaceInfo>();
        if (string.IsNullOrWhiteSpace(output))
            return result;

        Dictionary<Field, string>? current = null;
        string? nameKey = null; // the label of the name line when the language is unknown
        foreach (var raw in output.Split('\n'))
        {
            var m = Line.Match(raw.TrimEnd('\r'));
            if (!m.Success)
                continue;
            var key = m.Groups["key"].Value.Trim();
            var value = m.Groups["value"].Value.Trim();
            var field = Resolve(key);
            if (field is null && current is null && nameKey is null && value.Length > 0)
                nameKey = key; // the first "label : value" line of the output is the interface name in every language
            if (field is null && nameKey is not null && key.Equals(nameKey, StringComparison.OrdinalIgnoreCase))
                field = Field.Name;
            if (field is null && GuidValue.IsMatch(value))
                field = Field.Guid;
            if (field is null)
                continue;
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
        // Guessed name lines: only blocks with an interface GUID are interfaces (not, e.g., the
        // "ms-settings:privacy-location" line of the location message).
        return nameKey is null ? result : result.Where(i => i.Guid is not null).ToList();
    }

    /// <summary>Windows 11 hides Wi-Fi details from netsh while apps may not use the location.</summary>
    public static bool IsLocationBlocked(string? output)
        => !string.IsNullOrEmpty(output) &&
           // l10n:data-begin — the words of netsh output, matched and never shown
           (output.Contains("location permission", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("местоположени", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("расположени", StringComparison.OrdinalIgnoreCase));
           // l10n:data-end

    private static WifiInterfaceInfo Build(Dictionary<Field, string> f)
    {
        string? Get(Field key) => f.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        var state = Get(Field.State) ?? "";
        var ssid = Get(Field.Ssid);
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
            // netsh prints the network name and the signal only for a connected interface: that is enough when the state is
            // in a language this parser does not know ("Verbunden", "Підключено"…).
            IsConnected = IsConnectedState(state) || (!IsKnownState(state) && signal is not null && ssid is not null),
            Ssid = ssid,
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

    /// <summary>"connected" / "подключено" / "підключено" / "verbunden" (but not "disconnected" / "отключено").</summary>
    public static bool IsConnectedState(string? state)
    {
        var s = (state ?? "").Trim().ToLowerInvariant();
        // l10n:data-begin — the words of netsh output, matched and never shown
        return s == "connected" || s == "verbunden" || s.StartsWith("подключ") || s.StartsWith("підключ");
        // l10n:data-end
    }

    // l10n:data-begin — the words of netsh output, matched and never shown
    private static readonly Regex KnownState = new(
        @"^(connected|disconnected|disconnecting|associating|authenticating|discovering|not ready|ad hoc|подключ|отключ|" +
        @"підключ|відключ|verbunden|getrennt)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // l10n:data-end

    /// <summary>A state word this parser understands (otherwise the signal decides whether the interface is connected).</summary>
    private static bool IsKnownState(string state) => KnownState.IsMatch(state.Trim());

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
