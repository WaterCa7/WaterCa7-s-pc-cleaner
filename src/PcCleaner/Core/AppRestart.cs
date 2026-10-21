using System.Windows;
using PcCleaner.Core.Localization;
using PcCleaner.Core.Updates;

namespace PcCleaner.Core;

/// <summary>
/// Restarts the program as it is (Настройки → «Язык» → «Перезапустить сейчас»): the same exe with the arguments of this start.
/// The rights stay as they are at start: a copy that runs elevated starts the next one elevated (the token is inherited, no new
/// question), and a limited one starts it as a normal launch does, so «Запускать с правами администратора» asks again.
/// </summary>
internal static class AppRestart
{
    /// <summary>The switches that belong to one run of the program: the next one reads the saved settings instead.</summary>
    private static readonly string[] RunOnlySwitches = { "--lang", "--mode" };

    /// <summary>
    /// The arguments for the restarted copy: those of this start without what belongs to this start only
    /// (<see cref="UpdateService.WithoutStartMarkers"/>) and without <c>--lang</c> / <c>--mode</c> with their values — they
    /// replace the settings for one run, so a language chosen in Настройки would never take effect.
    /// </summary>
    internal static List<string> Args(IReadOnlyList<string> args)
    {
        var kept = UpdateService.WithoutStartMarkers(args);
        var result = new List<string>();
        for (var i = 0; i < kept.Count; i++)
        {
            if (i + 1 < kept.Count && RunOnlySwitches.Contains(kept[i], StringComparer.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }
            result.Add(kept[i]);
        }
        return result;
    }

    /// <summary>Starts this program again and closes this copy. False — and this copy goes on — when the new one did not start.</summary>
    internal static bool RestartNow()
        => Restart(Environment.ProcessPath, Environment.GetCommandLineArgs().Skip(1).ToList(), Start,
            () => Application.Current?.Shutdown());

    /// <summary>Starts the exe like the updater does; a failure is logged as a problem of the restart, not of an update.</summary>
    internal static bool Start(string exe, IReadOnlyList<string> args)
    {
        if (UpdateEnvironment.TryStart(exe, args, out var error))
            return true;
        if (error is not null)
            Log.Warn(T.F("Перезапуск: не удалось запустить {0}: {1}", exe, ErrorText.For(error)), error);
        return false;
    }

    /// <summary>The restart with everything outside replaceable: tests never start a process or close a window.</summary>
    internal static bool Restart(string? exe, IReadOnlyList<string> commandLine,
        Func<string, IReadOnlyList<string>, bool> start, Action shutdown)
    {
        if (string.IsNullOrEmpty(exe))
        {
            Log.Warn(T._("Перезапуск невозможен: не найден путь к программе"));
            return false;
        }
        if (!start(exe, Args(commandLine)))
            return false;
        Log.Info(T._("Программа перезапускается"));
        shutdown();
        return true;
    }
}
