using System.ComponentModel;
using System.Diagnostics;
using PcCleaner.Core.Updates;

namespace PcCleaner.Core;

/// <summary>
/// «Всегда с правами администратора»: at startup the program restarts itself elevated (Windows asks once per start).
/// «Нет» in the Windows prompt opens the program with limited rights, as before. Switched off in Настройки
/// (<see cref="AppSettings.RunAsAdmin"/>).
/// </summary>
public static class StartupElevation
{
    /// <summary>Starts without asking for the rights (for a shortcut or a script).</summary>
    public const string DeclinedArg = "--elevation-declined";

    /// <summary>
    /// False when the restart must not happen: already elevated, switched off, a --screenshots verification run, the user
    /// has already said no (<see cref="DeclinedArg"/>), or the updater has just started this version
    /// (<see cref="UpdateService.UpdatedFromArg"/>: an elevated updater starts it elevated, a limited one was declined).
    /// </summary>
    public static bool ShouldElevate(bool isElevated, bool runAsAdmin, bool screenshotMode, IReadOnlyList<string> args)
    {
        if (isElevated || !runAsAdmin || screenshotMode)
            return false;
        return !args.Any(a => string.Equals(a, DeclinedArg, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, UpdateService.UpdatedFromArg, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Starts this exe again with the "runas" verb and the same arguments. True = the elevated copy is starting and this
    /// process must exit; false = the user said no in the Windows prompt or it could not be started (logged): carry on
    /// with limited rights.
    /// </summary>
    public static bool TryRelaunchElevated(IReadOnlyList<string> args, Func<ProcessStartInfo, bool> start)
        => TryRelaunchElevated(Environment.ProcessPath, args, start);

    internal static bool TryRelaunchElevated(string? exePath, IReadOnlyList<string> args, Func<ProcessStartInfo, bool> start)
    {
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Warn("Запуск с правами администратора невозможен: не найден путь к программе");
            return false;
        }
        try
        {
            if (start(Admin.ElevatedStartInfo(exePath, args)))
                return true;
            Log.Warn($"Не удалось перезапустить «{exePath}» с правами администратора: программа не запустилась");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Admin.ErrorCancelledByUser)
        {
            Log.Info("Права администратора не выданы: в окне Windows выбрано «Нет»");
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось перезапустить «{exePath}» с правами администратора: {ErrorText.For(ex)}");
        }
        return false;
    }

    /// <summary>The real start, for <see cref="TryRelaunchElevated(IReadOnlyList{string}, Func{ProcessStartInfo, bool})"/>.</summary>
    internal static bool Start(ProcessStartInfo info)
    {
        // No exception means Windows accepted the request: the shell may hand back no process handle, and a second
        // copy must not be left running next to the elevated one because of that.
        Process.Start(info)?.Dispose();
        return true;
    }
}
