using System.IO;
using System.Text;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

/// <summary>«Приложить сведения о системе и журнал»: one line about the system and the end of the newest log, as base64.</summary>
public static class SupportAttachment
{
    /// <param name="maxBytes">The limit of the text before base64 (700 KiB → base64 under the API's 1 MiB).</param>
    public static string Build(int maxBytes = 700 * 1024) => Build(maxBytes, AppPaths.LogsDir);

    /// <summary>
    /// The head is the program, Windows, bitness and language only — no e-mail, computer id or hardware hash; the log is the program's
    /// own journal, which never holds passwords, codes or tokens. When the limit is tight the log is cut first (its beginning goes).
    /// </summary>
    internal static string Build(int maxBytes, string logsDir)
    {
        var head = $"{AppInfo.Name} {AppInfo.Version}; {Environment.OSVersion.VersionString}; {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}; {T.Language}\n";
        var room = Math.Max(0, maxBytes - Encoding.UTF8.GetByteCount(head));
        string tail;
        try
        {
            tail = ReadEnd(logsDir, room);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tail = T._("(журнал не прочитан)"); // no ex.Message: it carries the path with the user's name
        }
        var text = head + tail;
        while (Encoding.UTF8.GetByteCount(text) > maxBytes)
            text = text[1..];
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    }

    // The last `room` bytes of the newest *.log, starting on a whole character; "" when there is no log.
    private static string ReadEnd(string logsDir, int room)
    {
        var dir = new DirectoryInfo(logsDir);
        var newest = dir.Exists ? dir.GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() : null;
        if (newest is null || room == 0)
            return "";
        using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var start = Math.Max(0, stream.Length - room);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(room, stream.Length - start)];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
                break;
            read += n;
        }
        var from = 0;
        if (start == 0 && read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            from = 3; // the BOM of a file the log has just created
        else if (start > 0)
            while (from < read && (buffer[from] & 0xC0) == 0x80)
                from++; // the cut fell inside a character: skip its tail bytes
        return Encoding.UTF8.GetString(buffer, from, read - from);
    }
}
