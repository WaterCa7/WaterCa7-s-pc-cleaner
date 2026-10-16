using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using PcCleaner.Core;

namespace PcCleaner.Modules.Security;

/// <summary>One active (non-comment) line of the hosts file.</summary>
public sealed class HostsEntry : ObservableObject
{
    private bool _isSelected;

    /// <summary>0-based index of the line in the file.</summary>
    public int LineIndex { get; init; }

    public int LineNumber => LineIndex + 1;

    /// <summary>The line exactly as in the file (without the line break).</summary>
    public string RawLine { get; init; } = "";

    public string Address { get; init; } = "";

    public IReadOnlyList<string> Hostnames { get; init; } = Array.Empty<string>();

    public string HostnamesText => string.Join(", ", Hostnames);

    public string? Comment { get; init; }

    public bool IsValidAddress { get; init; }

    public SecurityStatus Risk { get; set; } = SecurityStatus.Ok;

    public string Reason { get; set; } = "";

    /// <summary>Checked for removal in the UI.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class HostsReport
{
    public string Path { get; init; } = "";
    public bool Exists { get; init; } = true;
    public string? Error { get; init; }
    public IReadOnlyList<HostsEntry> Entries { get; init; } = Array.Empty<HostsEntry>();
    public bool IsReadOnly { get; init; }
}

/// <summary>Thrown when the hosts file changed between the scan and an edit.</summary>
public sealed class HostsChangedException : Exception
{
    public HostsChangedException(string message) : base(message) { }
}

/// <summary>
/// Reading, classifying and editing the hosts file. Editing preserves every other line, the line breaks,
/// the encoding and the BOM.
/// </summary>
public static class HostsFile
{
    static HostsFile()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string DefaultPath => System.IO.Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    // ===== Reading =====

    public static HostsReport Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new HostsReport { Path = path, Exists = false };
            var bytes = File.ReadAllBytes(path);
            var (text, _, _) = Decode(bytes);
            var entries = Parse(text);
            foreach (var e in entries)
                HostsRules.Classify(e);
            bool readOnly;
            try
            {
                readOnly = new FileInfo(path).IsReadOnly;
            }
            catch
            {
                readOnly = false;
            }
            return new HostsReport { Path = path, Entries = entries, IsReadOnly = readOnly };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"Не удалось прочитать файл hosts: {ex.Message}");
            return new HostsReport { Path = path, Error = ex.Message };
        }
    }

    /// <summary>Parses the hosts text into active entries (comments and blank lines are skipped).</summary>
    public static List<HostsEntry> Parse(string text)
    {
        var result = new List<HostsEntry>();
        var lines = SplitLines(text);
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i].Content;
            var hash = raw.IndexOf('#');
            var body = hash >= 0 ? raw[..hash] : raw;
            var comment = hash >= 0 ? raw[(hash + 1)..].Trim() : null;
            body = body.Trim().TrimStart('﻿');
            if (body.Length == 0)
                continue;
            var tokens = Regex.Split(body, @"\s+").Where(t => t.Length > 0).ToArray();
            if (tokens.Length == 0)
                continue;
            var address = tokens[0];
            result.Add(new HostsEntry
            {
                LineIndex = i,
                RawLine = raw,
                Address = address,
                Hostnames = tokens.Skip(1).ToArray(),
                Comment = string.IsNullOrEmpty(comment) ? null : comment,
                IsValidAddress = TryParseAddress(address, out _),
            });
        }
        return result;
    }

    internal static bool TryParseAddress(string text, out IPAddress? address)
    {
        address = null;
        // IPAddress.TryParse accepts things like "1" or "1.2" — require a full dotted quad or an IPv6 address.
        if (text.Contains(':'))
            return IPAddress.TryParse(text, out address) && address.AddressFamily == AddressFamily.InterNetworkV6;
        return Regex.IsMatch(text, @"^\d{1,3}(\.\d{1,3}){3}$") && IPAddress.TryParse(text, out address);
    }

    // ===== Editing =====

    /// <summary>
    /// Removes the given lines (matched by index AND exact text) from the file. A raw copy of the file is saved
    /// into <paramref name="backupDir"/> first. Returns the backup file path.
    /// Throws <see cref="HostsChangedException"/> (without changing anything) if the file no longer matches.
    /// </summary>
    public static string RemoveLines(string path, IReadOnlyCollection<HostsEntry> lines, string backupDir)
    {
        var bytes = File.ReadAllBytes(path);
        var (text, encoding, preambleLength) = Decode(bytes);
        var parts = SplitLines(text);

        var remove = new HashSet<int>();
        foreach (var line in lines)
        {
            if (line.LineIndex < 0 || line.LineIndex >= parts.Count || parts[line.LineIndex].Content != line.RawLine)
                throw new HostsChangedException(
                    "Файл hosts изменился после проверки. Нажмите «Проверить снова» и выберите строки ещё раз.");
            remove.Add(line.LineIndex);
        }
        if (remove.Count == 0)
            throw new ArgumentException("Не выбрано ни одной строки.", nameof(lines));

        Directory.CreateDirectory(backupDir);
        var backup = System.IO.Path.Combine(backupDir, $"hosts_{DateTime.Now:yyyyMMdd-HHmmss-fff}.bak");
        File.WriteAllBytes(backup, bytes);

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < parts.Count; i++)
        {
            if (!remove.Contains(i))
                sb.Append(parts[i].Content).Append(parts[i].Ending);
        }

        var body = encoding.GetBytes(sb.ToString());
        var output = new byte[preambleLength + body.Length];
        Array.Copy(bytes, output, preambleLength);
        Array.Copy(body, 0, output, preambleLength, body.Length);

        var info = new FileInfo(path);
        var wasReadOnly = info.IsReadOnly;
        if (wasReadOnly)
            info.IsReadOnly = false;
        try
        {
            File.WriteAllBytes(path, output);
        }
        finally
        {
            if (wasReadOnly)
            {
                try
                {
                    new FileInfo(path).IsReadOnly = true;
                }
                catch
                {
                    // attribute restore is best-effort
                }
            }
        }
        return backup;
    }

    /// <summary>Detects BOM / UTF-8 / ANSI. Returns the text (without BOM), the encoding and the BOM length.</summary>
    internal static (string Text, Encoding Encoding, int PreambleLength) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(false), 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(false, false).GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(false, false), 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(true, false).GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(true, false), 2);
        try
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), new UTF8Encoding(false), 0);
        }
        catch (DecoderFallbackException)
        {
            Encoding ansi;
            try
            {
                ansi = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            }
            catch
            {
                ansi = Encoding.Latin1;
            }
            return (ansi.GetString(bytes), ansi, 0);
        }
    }

    /// <summary>Splits text into lines, keeping each line's original terminator ("\r\n", "\n", "\r" or "").</summary>
    internal static List<(string Content, string Ending)> SplitLines(string text)
    {
        var result = new List<(string, string)>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\r' || c == '\n')
            {
                var ending = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : c.ToString();
                result.Add((text[start..i], ending));
                i += ending.Length;
                start = i;
            }
            else
            {
                i++;
            }
        }
        if (start < text.Length)
            result.Add((text[start..], ""));
        return result;
    }
}
