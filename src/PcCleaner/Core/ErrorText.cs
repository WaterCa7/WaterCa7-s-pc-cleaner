using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

/// <summary>
/// Text for exceptions shown to the user, in the language of the UI. The .NET runtime has no Russian resources, so ex.Message of
/// common errors ("Access to the path ... is denied.") would appear in English inside Russian dialogs: the common errors are
/// told by their type, Win32 code or HResult instead and get the app's own phrase (Russian or English, see <see cref="T"/>).
/// Russian UI: messages that are already Russian (the app's own, or Windows' localized ones) are kept as is; Cyrillic inside a
/// quoted path ("C:\Users\Иван\…") does not make an English message Russian.
/// English UI: a message with Cyrillic outside the quotes (a Windows text in Russian, an app phrase nobody translated) is never
/// shown; .NET's own English message is kept where it is understandable for the user (file and I/O errors, Windows' text for a
/// Win32 code, a message the app wrote itself and marked with <see cref="Own"/>), anything else gets a general phrase.
/// The raw message and stack trace still go to the log file (<see cref="Log.Error"/>).
/// </summary>
public static class ErrorText
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const int ErrorDiskFull = unchecked((int)0x80070070);

    private const string OwnMarker = "WaterCa7s.ErrorText.Own";

    /// <summary>
    /// Marks an exception whose message the app wrote itself (<c>throw ErrorText.Own(new InvalidOperationException(T._("…")))</c>):
    /// it is shown as it is in every language. An unmarked exception of an unknown type only gets a general phrase in English,
    /// because its message is a developer's sentence in English.
    /// </summary>
    public static TException Own<TException>(TException ex) where TException : Exception
    {
        ex.Data[OwnMarker] = true;
        return ex;
    }

    /// <summary>Short lower-case phrase without a final period, e.g. "файл занят другой программой" ("the file is in use by another program").</summary>
    public static string For(Exception? ex)
    {
        if (ex is null)
            return T._("неизвестная ошибка");
        if (ex is AggregateException { InnerExceptions.Count: 1 } aggregate)
            return For(aggregate.InnerExceptions[0]);
        if (ex is System.Reflection.TargetInvocationException { InnerException: { } inner })
            return For(inner);
        // HttpClient without a network: the reason is the socket error inside (Windows' own text for its code), if there is one.
        if (ex is System.Net.Http.HttpRequestException http)
            return http.InnerException is { } cause ? For(cause) : T._("нет связи с сервером");

        // Translated by type, whatever the message: .NET writes these in English, and any Cyrillic in them is a path.
        var known = ex switch
        {
            OperationCanceledException => T._("операция отменена"),
            UnauthorizedAccessException or SecurityException =>
                T._("нет доступа — нужны права администратора, или объект защищён Windows"),
            FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException => T._("файл или папка не найдены"),
            PathTooLongException => T._("слишком длинный путь к файлу"),
            IOException io when io.HResult is ErrorSharingViolation or ErrorLockViolation => T._("файл занят другой программой"),
            IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull => T._("на диске недостаточно места"),
            // Windows' own text for the code, not w.Message: Process.Start puts an English sentence with the quoted
            // paths in front of it ("An error occurred trying to start process '…' with working directory '…'. …").
            Win32Exception w => Win32Text(w.NativeErrorCode, new Win32Exception(w.NativeErrorCode).Message),
            TimeoutException => T._("превышено время ожидания"),
            OutOfMemoryException or InsufficientExecutionStackException => T._("не хватило памяти"),
            _ => null,
        };
        if (known is not null)
            return known;

        // The app's own text (it throws IOException and COMException with its own messages too) or Windows' localized one.
        var message = ex.Message?.Trim() ?? "";
        if (T.IsEnglish ? ShowsEnglishMessage(ex, message) : IsRussian(message))
            return Phrase(message);

        return ex switch
        {
            IOException => T._("не удалось прочитать или записать файл"),
            ExternalException e => T.F("ошибка Windows (код 0x{0:X8})", e.HResult),
            _ => T._("непредвиденная ошибка — подробности записаны в журнал"),
        };
    }

    /// <summary>The same as a sentence for a dialog: capitalized, with a final period.</summary>
    public static string Sentence(Exception? ex)
    {
        var text = For(ex);
        if (text.Length == 0)
            return text;
        text = char.ToUpper(text[0], T.Culture) + text[1..];
        return text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') || text.EndsWith('…') ? text : text + ".";
    }

    /// <param name="systemMessage">Windows' text for the code in the Windows display language: kept when it is in the language of the UI.</param>
    internal static string Win32Text(int code, string systemMessage) => code switch
    {
        5 => T._("нет доступа — нужны права администратора, или объект защищён Windows"),
        2 or 3 => T._("файл или папка не найдены"),
        32 or 33 => T._("файл занят другой программой"),
        39 or 112 => T._("на диске недостаточно места"),
        740 => T._("нужны права администратора"),
        1223 => T._("действие отменено пользователем"),
        _ when (T.IsEnglish ? IsEnglishText(systemMessage) : HasCyrillic(systemMessage)) => Phrase(systemMessage.Trim()),
        _ => T.F("ошибка Windows (код {0})", code),
    };

    /// <summary>A message shown as the reason, without its final period (in English also without the capital of its first word).</summary>
    private static string Phrase(string message)
    {
        var text = message.TrimEnd('.');
        return T.IsEnglish ? LowerFirstWord(text) : text;
    }

    /// <summary>
    /// English UI: .NET's message of a file or I/O error (English text only, like <see cref="IsEnglishText"/>: a German or Chinese
    /// Windows gives its own text, the quoted path aside), or a message the app wrote itself; never a Russian one.
    /// </summary>
    private static bool ShowsEnglishMessage(Exception ex, string message)
        => message.Length > 0 && !IsRussian(message) && (ex.Data.Contains(OwnMarker) || (ex is IOException && IsEnglishText(OutsideQuotes(message))));

    /// <summary>
    /// Windows' text in English: not empty, no Cyrillic, and no letters outside the Latin alphabet (a German or Chinese Windows
    /// gives a text the English UI should not show as it is).
    /// </summary>
    private static bool IsEnglishText(string text)
        => !string.IsNullOrWhiteSpace(text) && text.All(c => !char.IsLetter(c) || c < 0x80);

    private static readonly string[] ProperNouns = { "Windows", "Microsoft", "PowerShell", "BitLocker", "Defender", "Explorer", "Shell" };

    /// <summary>"The file exists" → "the file exists" (it follows a colon or a dash); "Windows …", "SQL …", "C:\x …" stay.</summary>
    private static string LowerFirstWord(string text)
    {
        var space = text.IndexOf(' ');
        var word = space < 0 ? text : text[..space];
        if (word.Length < 2 || !char.IsUpper(word[0]) || !word.Skip(1).All(char.IsLower) || ProperNouns.Contains(word))
            return text;
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    /// <summary>
    /// Cyrillic outside the quotes. .NET puts paths into its English messages in single quotes, and a path has Cyrillic
    /// when the Windows user name does ("Access to the path 'C:\Users\Иван\…' is denied."). Everything from the first
    /// quote to the last one is skipped, so an apostrophe inside a path ("C:\Users\D'Arc\…") changes nothing.
    /// </summary>
    private static bool IsRussian(string text) => HasCyrillic(OutsideQuotes(text));

    /// <summary>The text without everything from the first single quote to the last one (the quoted path).</summary>
    private static string OutsideQuotes(string text)
    {
        var first = text.IndexOf('\'');
        var last = text.LastIndexOf('\'');
        return last > first && first >= 0 ? string.Concat(text.AsSpan(0, first), text.AsSpan(last + 1)) : text;
    }

    internal static bool HasCyrillic(string text)
    {
        foreach (var c in text)
        {
            if (c is >= 'Ѐ' and <= 'ӿ')
                return true;
        }
        return false;
    }
}
