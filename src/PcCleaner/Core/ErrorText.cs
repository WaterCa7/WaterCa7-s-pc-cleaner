using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace PcCleaner.Core;

/// <summary>
/// Russian text for exceptions shown to the user. The .NET runtime has no Russian resources, so ex.Message of
/// common errors ("Access to the path ... is denied.") would appear in English inside Russian dialogs.
/// Messages that are already Russian (the app's own, or Windows' localized ones) are kept as is.
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

        var message = ex.Message?.Trim() ?? "";
        if (HasCyrillic(message))
            return message.TrimEnd('.');

        return ex switch
        {
            OperationCanceledException => "операция отменена",
            UnauthorizedAccessException or SecurityException =>
                "нет доступа — нужны права администратора, или объект защищён Windows",
            FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException => "файл или папка не найдены",
            PathTooLongException => "слишком длинный путь к файлу",
            IOException io when io.HResult is ErrorSharingViolation or ErrorLockViolation => "файл занят другой программой",
            IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull => "на диске недостаточно места",
            IOException => "не удалось прочитать или записать файл",
            Win32Exception w => Win32Text(w.NativeErrorCode),
            ExternalException e => $"ошибка Windows (код 0x{e.HResult:X8})",
            TimeoutException => "превышено время ожидания",
            OutOfMemoryException or InsufficientExecutionStackException => "не хватило памяти",
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

    private static string Win32Text(int code) => code switch
    {
        5 => "нет доступа — нужны права администратора, или объект защищён Windows",
        2 or 3 => "файл или папка не найдены",
        32 or 33 => "файл занят другой программой",
        39 or 112 => "на диске недостаточно места",
        740 => "нужны права администратора",
        1223 => "действие отменено пользователем",
        _ => $"ошибка Windows (код {code})",
    };

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
