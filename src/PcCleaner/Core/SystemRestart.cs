using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

/// <summary>Restarts Windows after the user confirmed it (unsaved work in other programs would be lost).</summary>
public static class SystemRestart
{
    /// <summary>The Yes/No question (<see cref="Dialogs.Confirm"/>); tests replace it.</summary>
    internal static Func<string, string, bool> Confirm { get; set; } = Dialogs.Confirm;

    /// <summary>
    /// Tells Windows to restart in a few seconds; false when it did not accept the request. Tests replace it:
    /// no test may restart Windows.
    /// </summary>
    internal static Func<bool> Start { get; set; } = StartRestart;

    /// <summary>
    /// Asks the user, then restarts. Returns the result for the dashboard row: «Компьютер перезагружается…» after the restart
    /// was requested, otherwise a text that says nothing was changed or that the restart did not start.
    /// </summary>
    public static Task<string> RestartAsync(CancellationToken ct) => RestartCoreAsync(ct, askFirst: true);

    /// <summary>
    /// Restarts without a question of its own — only for a caller that has just asked, like the dashboard's «Исправить проблему»
    /// question, which already shows the warning. Same results as <see cref="RestartAsync"/>.
    /// </summary>
    internal static Task<string> RestartWithoutAskingAsync(CancellationToken ct) => RestartCoreAsync(ct, askFirst: false);

    private static async Task<string> RestartCoreAsync(CancellationToken ct, bool askFirst)
    {
        ct.ThrowIfCancellationRequested();
        if (askFirst && !Confirm(T._("Перезагрузить компьютер"),
                T._("Сохраните работу во всех программах. Компьютер перезагрузится через несколько секунд. Продолжить?")))
        {
            Log.Info(T._("Перезагрузка: пользователь отказался"));
            return T._("Перезагрузка отменена — ничего не изменено.");
        }
        // "Отмена" pressed while the question was open: the computer must not restart behind the user's back.
        ct.ThrowIfCancellationRequested();

        var start = Start;
        if (!await Task.Run(start, CancellationToken.None).ConfigureAwait(false))
            return T._("Не удалось перезагрузить компьютер.");
        Log.Action(T._("Перезагрузка: Windows перезагрузится через несколько секунд (по просьбе пользователя)"));
        return T._("Компьютер перезагружается…");
    }

    /// <summary>shutdown.exe /r /t 5: restart in 5 seconds. Works for any signed-in user.</summary>
    private static bool StartRestart()
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 5")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            // shutdown.exe only schedules the restart and exits at once.
            if (process is not null && process.WaitForExit(10_000) && process.ExitCode == 0)
                return true;
            Log.Warn(process is { HasExited: true }
                ? T.F("Перезагрузка: shutdown.exe не принял запрос (код {0})", process.ExitCode)
                : T._("Перезагрузка: shutdown.exe не принял запрос"));
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Warn(T.F("Перезагрузка: не удалось запустить shutdown.exe: {0}", ErrorText.For(ex)));
            return false;
        }
    }
}
