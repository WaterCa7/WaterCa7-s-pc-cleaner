using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace PcCleaner.Core;

public enum LogLevel
{
    Info,
    Warning,
    Error,
    Action,
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    public string LevelText => Level switch
    {
        LogLevel.Warning => "Внимание",
        LogLevel.Error => "Ошибка",
        LogLevel.Action => "Действие",
        _ => "Инфо",
    };
}

/// <summary>
/// Application log: in-memory list for the UI plus a daily file in %LOCALAPPDATA%\WaterCa7sPcCleaner\Logs.
/// Use Log.Action for every change made to the system so the user can see what was done.
/// </summary>
public static class Log
{
    private static readonly object FileLock = new();
    private static readonly object EntriesLock = new();
    private const int MaxEntries = 2000;

    public static ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>Raised (on the UI thread) after every new entry.</summary>
    public static event Action<LogEntry>? EntryAdded;

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warning, message);

    /// <summary>
    /// The journal and the status bar get a Russian description of <paramref name="ex"/> (<see cref="ErrorText"/>);
    /// the log file keeps the exception type, its original message and the stack trace.
    /// </summary>
    public static void Error(string message, Exception? ex = null)
        => Write(LogLevel.Error, ex is null ? message : $"{message}: {ErrorText.For(ex)}", ex,
            ex is null ? null : $"{message}: {ex.GetType().Name}: {ex.Message}");

    /// <summary>Records a change made to the system (deleted files, disabled startup item, etc.).</summary>
    public static void Action(string message) => Write(LogLevel.Action, message);

    private static void Write(LogLevel level, string message, Exception? ex = null, string? fileMessage = null)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        Ui.Post(() =>
        {
            // Without a dispatcher (unit tests) this runs on the calling thread, so serialize the collection changes.
            lock (EntriesLock)
            {
                Entries.Add(entry);
                while (Entries.Count > MaxEntries)
                    Entries.RemoveAt(0);
            }
            EntryAdded?.Invoke(entry);
        });

        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                var file = Path.Combine(AppPaths.LogsDir, $"{entry.Time:yyyy-MM-dd}.log");
                var line = $"{entry.Time:HH:mm:ss} [{level}] {fileMessage ?? message}";
                if (ex is not null)
                    line += Environment.NewLine + ex;
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
