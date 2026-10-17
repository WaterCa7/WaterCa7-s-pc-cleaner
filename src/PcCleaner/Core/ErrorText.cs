using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace PcCleaner.Core;

/// <summary>
/// Russian text for exceptions shown to the user. The .NET runtime has no Russian resources, so ex.Message of
/// common errors ("Access to the path ... is denied.") would appear in English inside Russian dialogs.
/// Messages that are already Russian (the app's own, or Windows' localized ones) are kept as is; Cyrillic inside a quoted
/// path ("C:\Users\Иван\…") does not make an English message Russian.
/// The raw message and stack trace still go to the log file (<see cref="Log.Error"/>).
/// </summary>
public static class ErrorText
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const int ErrorDiskFull = unchecked((int)0x80070070);

    /// <summary>Short lower-case phrase without a final period, e.g. "файл занят другой программой".</summary>
    public static string For(Exception? ex)
    {
        if (ex is null)
            return "неизвестная ошибка";
        if (ex is AggregateException { InnerExceptions.Count: 1 } aggregate)
            return For(aggregate.InnerExceptions[0]);
        if (ex is System.Reflection.TargetInvocationException { InnerException: { } inner })
            return For(inner);

        // Translated by type, whatever the message: .NET writes these in English, and any Cyrillic in them is a path.
        var known = ex switch
        {
            OperationCanceledException => "операция отменена",
            UnauthorizedAccessException or SecurityException =>
                "нет доступа — нужны права администратора, или объект защищён Windows",
            FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException => "файл или папка не найдены",
            PathTooLongException => "слишком длинный путь к файлу",
            IOException io when io.HResult is ErrorSharingViolation or ErrorLockViolation => "файл занят другой программой",
            IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull => "на диске недостаточно места",
            // Windows' own text for the code, not w.Message: Process.Start puts an English sentence with the quoted
            // paths in front of it ("An error occurred trying to start process '…' with working directory '…'. …").
            Win32Exception w => Win32Text(w.NativeErrorCode, new Win32Exception(w.NativeErrorCode).Message),
            TimeoutException => "превышено время ожидания",
            OutOfMemoryException or InsufficientExecutionStackException => "не хватило памяти",
            _ => null,
        };
        if (known is not null)
            return known;

        // The app's own text (it throws IOException and COMException with Russian messages too) or Windows' localized one.
        var message = ex.Message?.Trim() ?? "";
        if (IsRussian(message))
            return message.TrimEnd('.');

        return ex switch
        {
            IOException => "не удалось прочитать или записать файл",
            ExternalException e => $"ошибка Windows (код 0x{e.HResult:X8})",
            _ => "непредвиденная ошибка — подробности записаны в журнал",
        };
    }

    /// <summary>The same as a sentence for a dialog: capitalized, with a final period.</summary>
    public static string Sentence(Exception? ex)
    {
        var text = For(ex);
        if (text.Length == 0)
            return text;
        text = char.ToUpper(text[0], Format.Ru) + text[1..];
        return text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') || text.EndsWith('…') ? text : text + ".";
    }

    /// <param name="systemMessage">Windows' text for the code in the Windows display language: kept when it is Russian.</param>
    internal static string Win32Text(int code, string systemMessage) => code switch
    {
        5 => "нет доступа — нужны права администратора, или объект защищён Windows",
        2 or 3 => "файл или папка не найдены",
        32 or 33 => "файл занят другой программой",
        39 or 112 => "на диске недостаточно места",
        740 => "нужны права администратора",
        1223 => "действие отменено пользователем",
        _ when HasCyrillic(systemMessage) => systemMessage.Trim().TrimEnd('.'),
        _ => $"ошибка Windows (код {code})",
    };

    /// <summary>
    /// Cyrillic outside the quotes. .NET puts paths into its English messages in single quotes, and a path has Cyrillic
    /// when the Windows user name does ("Access to the path 'C:\Users\Иван\…' is denied."). Everything from the first
    /// quote to the last one is skipped, so an apostrophe inside a path ("C:\Users\D'Arc\…") changes nothing.
    /// </summary>
    private static bool IsRussian(string text)
    {
        var first = text.IndexOf('\'');
        var last = text.LastIndexOf('\'');
        if (last > first && first >= 0)
            text = string.Concat(text.AsSpan(0, first), text.AsSpan(last + 1));
        return HasCyrillic(text);
    }

    private static bool HasCyrillic(string text)
    {
        foreach (var c in text)
        {
            if (c is >= 'Ѐ' and <= 'ӿ')
                return true;
        }
        return false;
    }
}
