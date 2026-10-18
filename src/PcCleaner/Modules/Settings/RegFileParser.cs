using System.Globalization;
using System.IO;
using System.Text;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Settings;

/// <summary>
/// One value an import of a .reg file would write. <see cref="Type"/> is the raw registry type
/// (1 = REG_SZ, 2 = REG_EXPAND_SZ, 3 = REG_BINARY, 4 = REG_DWORD, 7 = REG_MULTI_SZ, 11 = REG_QWORD…);
/// <see cref="Data"/> is a string, string[], uint, ulong or byte[]. Name "" is the default value ("@").
/// </summary>
internal sealed record RegFileValue(string Key, string Name, int Type, object Data);

/// <summary>
/// What importing a .reg file would touch: the first key, whether every other key lies under it,
/// whether it deletes keys ("[-KEY]") or values ("name"=-), and every key and value it would write.
/// </summary>
internal sealed record RegFileInfo(string? RootKey, bool SingleRoot, bool DeletesKeys)
{
    public bool DeletesValues { get; init; }

    /// <summary>Why the file is not a plain reg.exe export (null = well-formed). Such files are never imported.</summary>
    public string? Problem { get; init; }

    /// <summary>Every "[KEY]" header in short form ("HKLM\..."), in file order, without duplicates.</summary>
    public IReadOnlyList<string> Keys { get; init; } = Array.Empty<string>();

    /// <summary>Every value line (a later line for the same key and name wins, as in reg.exe).</summary>
    public IReadOnlyList<RegFileValue> Values { get; init; } = Array.Empty<RegFileValue>();
}

/// <summary>
/// Strict parser for the files RegistryBackup writes with "reg.exe export": UTF-16 LE with BOM, the
/// "Windows Registry Editor Version 5.00" header, CRLF line ends, "[KEY]" headers and one value per line
/// (hex data may continue on following lines after a trailing "\").
/// reg.exe imports line by line (a quoted string never spans lines — it silently drops such a value and then
/// honours whatever the next lines say), so anything this parser cannot read exactly is reported as a problem and
/// the file is not imported: what the user is shown must be exactly what Windows would write.
/// </summary>
internal static class RegFileParser
{
    public const string Header = "Windows Registry Editor Version 5.00";

    /// <summary>The app exports single keys; anything bigger is not one of its backups.</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    private static readonly string[] LongHives =
    {
        "HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER", "HKEY_CLASSES_ROOT", "HKEY_USERS", "HKEY_CURRENT_CONFIG",
    };

    private static readonly UnicodeEncoding StrictUtf16 = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Reads the whole file once. Null if it cannot be read (logged); <paramref name="tooBig"/> is set for oversized files.
    /// </summary>
    public static byte[]? ReadBytes(string path, out bool tooBig)
    {
        tooBig = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes)
            {
                tooBig = true;
                return null;
            }
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(T.F("Не удалось прочитать резервную копию {0}: {1}", Path.GetFileName(path), ex.Message));
            return null;
        }
    }

    /// <summary>Parses the raw bytes of a .reg file (only UTF-16 LE with BOM, as reg.exe writes it).</summary>
    public static RegFileInfo Parse(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0xFF || bytes[1] != 0xFE || bytes.Length % 2 != 0)
            return Fail(null, T._("файл не в кодировке UTF-16, в которой Windows сохраняет копии реестра"));
        string text;
        try
        {
            // Strict: a broken surrogate would be shown as "�" while reg.exe writes the raw character.
            text = StrictUtf16.GetString(bytes, 2, bytes.Length - 2);
        }
        catch (DecoderFallbackException)
        {
            return Fail(null, T._("в файле есть недопустимые символы"));
        }
        return ParseText(text);
    }


    /// <summary>Parses already decoded text (without the BOM).</summary>
    public static RegFileInfo ParseText(string text)
    {
        text = text.TrimEnd('\0');
        if (text.Contains('\0'))
            return Fail(null, T._("в файле есть нулевые символы"));

        var lines = text.Split("\r\n");
        if (lines.Any(l => l.Contains('\r') || l.Contains('\n')))
            return Fail(null, T._("в файле есть переводы строк, которых не бывает в копиях reg.exe"));
        if (lines.Length == 0 || lines[0].TrimEnd(' ', '\t') != Header)
            return Fail(null, T.F("нет заголовка «{0}»", Header));

        string? root = null;
        var singleRoot = true;
        var deletesKeys = false;
        var deletesValues = false;
        var keys = new List<string>();
        var keySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Key + name → value; registry names are case-insensitive and a later line overwrites an earlier one.
        var values = new Dictionary<(string Key, string Name), RegFileValue>(KeyNameComparer.Instance);
        var order = new List<(string Key, string Name)>();

        string? currentKey = null;
        (string Key, string Name, int Type, StringBuilder Hex)? pending = null;

        RegFileInfo Broken(int index, string what) =>
            Fail(root, T.F("строка {0}: {1}", Format.Number(index + 1), what), singleRoot, deletesKeys);

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];

            if (pending is { } p)
            {
                // Continuation of hex data: indented bytes, possibly ending with "\" again.
                if (line.Length == 0 || (line[0] != ' ' && line[0] != '\t'))
                    return Broken(i, T._("оборвано продолжение двоичных данных"));
                var body = line.Trim(' ', '\t');
                var more = body.EndsWith('\\');
                p.Hex.Append(more ? body[..^1] : body);
                if (more)
                    continue;
                pending = null;
                if (!TryHexValue(p.Key, p.Name, p.Type, p.Hex.ToString(), out var value))
                    return Broken(i, T._("неверные двоичные данные"));
                Put(value);
                continue;
            }

            var t = line.TrimEnd(' ', '\t');
            if (t.Length == 0)
                continue;

            if (t[0] == '[')
            {
                if (t.Length < 3 || t[^1] != ']')
                    return Broken(i, T._("неверный заголовок раздела"));
                var key = t[1..^1];
                var deleting = key.StartsWith('-');
                if (deleting)
                {
                    deletesKeys = true;
                    key = key[1..];
                }
                if (!IsValidKey(key))
                    return Broken(i, T._("неверный путь раздела"));
                var shortKey = BackupCatalog.ShortHive(key);
                if (keySet.Add(shortKey))
                    keys.Add(shortKey);
                if (root is null)
                    root = shortKey;
                else if (!SettingsLogic.IsSameOrUnder(shortKey, root))
                    singleRoot = false;
                // Values after "[-KEY]" are not written to that key; the file is refused anyway.
                currentKey = deleting ? null : shortKey;
                continue;
            }

            if (t[0] is not ('"' or '@'))
                return Broken(i, T._("строка не распознана"));
            if (currentKey is null)
                return Broken(i, T._("значение вне раздела"));

            var pos = 0;
            string name;
            if (t[0] == '@')
            {
                name = "";
                pos = 1;
            }
            else if (!TryQuoted(t, ref pos, out name))
            {
                return Broken(i, T._("незакрытые кавычки в имени значения"));
            }
            if (pos >= t.Length || t[pos] != '=')
                return Broken(i, T._("нет знака «=»"));
            var data = t[(pos + 1)..];

            if (data == "-")
            {
                deletesValues = true;
                continue;
            }
            if (data.StartsWith('"'))
            {
                var dpos = 0;
                if (!TryQuoted(data, ref dpos, out var str) || dpos != data.Length)
                    return Broken(i, T._("незакрытые кавычки или лишний текст после строки"));
                Put(new RegFileValue(currentKey, name, 1, str));
                continue;
            }
            if (data.StartsWith("dword:", StringComparison.Ordinal))
            {
                var digits = data[6..];
                if (digits.Length != 8 || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var dword))
                    return Broken(i, T._("неверное число dword"));
                Put(new RegFileValue(currentKey, name, 4, dword));
                continue;
            }
            if (data.StartsWith("hex", StringComparison.Ordinal) && TryHexPrefix(data, out var type, out var hex))
            {
                if (hex.EndsWith('\\'))
                {
                    pending = (currentKey, name, type, new StringBuilder(hex[..^1]));
                    continue;
                }
                if (!TryHexValue(currentKey, name, type, hex, out var value))
                    return Broken(i, T._("неверные двоичные данные"));
                Put(value);
                continue;
            }
            return Broken(i, T._("неизвестный формат значения"));
        }

        if (pending is not null)
            return Fail(root, T._("файл обрывается посреди двоичных данных"), singleRoot, deletesKeys);

        return new RegFileInfo(root, root is not null && singleRoot, deletesKeys)
        {
            DeletesValues = deletesValues,
            Keys = keys,
            Values = order.Select(k => values[k]).ToList(),
        };

        void Put(RegFileValue value)
        {
            var k = (value.Key, value.Name);
            if (!values.ContainsKey(k))
                order.Add(k);
            values[k] = value;
        }
    }

    private static RegFileInfo Fail(string? root, string problem, bool singleRoot = false, bool deletesKeys = false)
        => new(root, root is not null && singleRoot, deletesKeys)
        {
            Problem = T.F("Файл копии не похож на экспорт реестра Windows ({0}).", problem),
        };

    /// <summary>"HKEY_xxx" or "HKEY_xxx\a\b" with non-empty segments.</summary>
    private static bool IsValidKey(string key)
    {
        var parts = key.Split('\\');
        return LongHives.Any(h => string.Equals(h, parts[0], StringComparison.OrdinalIgnoreCase))
               && parts.Skip(1).All(s => s.Length > 0);
    }

    /// <summary>Reads "..." starting at <paramref name="pos"/> (only \\ and \" escapes, as reg.exe writes them).</summary>
    private static bool TryQuoted(string s, ref int pos, out string value)
    {
        value = "";
        if (pos >= s.Length || s[pos] != '"')
            return false;
        var sb = new StringBuilder();
        for (var i = pos + 1; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\')
            {
                if (i + 1 >= s.Length || s[i + 1] is not ('\\' or '"'))
                    return false;
                sb.Append(s[++i]);
                continue;
            }
            if (c == '"')
            {
                pos = i + 1;
                value = sb.ToString();
                return true;
            }
            sb.Append(c);
        }
        return false;
    }

    /// <summary>"hex:…" → REG_BINARY, "hex(N):…" → type N (hex digits).</summary>
    private static bool TryHexPrefix(string data, out int type, out string rest)
    {
        type = 0;
        rest = "";
        if (data.StartsWith("hex:", StringComparison.Ordinal))
        {
            type = 3;
            rest = data[4..];
            return true;
        }
        if (!data.StartsWith("hex(", StringComparison.Ordinal))
            return false;
        var close = data.IndexOf("):", StringComparison.Ordinal);
        if (close < 5 || close > 12)
            return false;
        if (!uint.TryParse(data[4..close], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var t) || t > int.MaxValue)
            return false;
        type = (int)t;
        rest = data[(close + 2)..];
        return true;
    }

    private static bool TryHexValue(string key, string name, int type, string hex, out RegFileValue value)
    {
        value = null!;
        var bytes = new List<byte>();
        if (hex.Length > 0)
        {
            foreach (var token in hex.Split(','))
            {
                if (token.Length != 2 || !byte.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
                    return false;
                bytes.Add(b);
            }
        }
        value = new RegFileValue(key, name, type, Decode(type, bytes.ToArray()));
        return true;
    }

    /// <summary>Raw bytes → the same shape RegistryKey.GetValue returns (string, string[], uint, ulong or byte[]).</summary>
    internal static object Decode(int type, byte[] bytes)
    {
        switch (type)
        {
            case 1 or 2 when bytes.Length % 2 == 0:
                return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            case 7 when bytes.Length % 2 == 0:
                return TrimMulti(Encoding.Unicode.GetString(bytes).Split('\0'));
            case 4 when bytes.Length == 4:
                return BitConverter.ToUInt32(bytes, 0);
            case 11 when bytes.Length == 8:
                return BitConverter.ToUInt64(bytes, 0);
            default:
                return bytes;
        }
    }

    /// <summary>Drops the empty strings produced by the double-NUL terminator of REG_MULTI_SZ.</summary>
    internal static string[] TrimMulti(string[] parts)
    {
        var n = parts.Length;
        while (n > 0 && parts[n - 1].Length == 0)
            n--;
        return parts[..n];
    }

    private sealed class KeyNameComparer : IEqualityComparer<(string Key, string Name)>
    {
        public static readonly KeyNameComparer Instance = new();

        public bool Equals((string Key, string Name) x, (string Key, string Name) y)
            => string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Key, string Name) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
