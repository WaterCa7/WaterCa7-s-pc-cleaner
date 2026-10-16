using PcCleaner.Core;

namespace PcCleaner.Modules.Settings;

/// <summary>
/// "Перезапустить от имени администратора" that does not silently interrupt work running on another page
/// (an uninstall the app is waiting for, a disk optimization, a cleaning...). Used by the pages that offer a restart
/// button of their own (Настройки, Очистка, Диски).
/// </summary>
internal static class ElevatedRestart
{
    /// <summary>Asks first if any page is busy, then restarts with a UAC prompt. Returns true if the restart started.</summary>
    public static bool Request() => Request(AllPages(), Admin.RestartElevated);

    /// <summary>
    /// Like <see cref="Admin.RequireElevation"/> (same question), but the restart it offers also asks first when another
    /// page is still busy. Returns <see cref="Admin.IsElevated"/>.
    /// </summary>
    public static bool RequireElevation(string actionDescription)
    {
        if (Admin.IsElevated)
            return true;
        if (Dialogs.Confirm("Нужны права администратора",
                $"Для действия «{actionDescription}» нужны права администратора.\n\nПерезапустить программу от имени администратора?"))
            Request();
        return false;
    }

    /// <summary>Test seam: explicit pages and restart action.</summary>
    internal static bool Request(IEnumerable<PageViewModel> pages, Func<bool> restart)
    {
        if (Admin.IsElevated)
            return false;
        var busy = pages.Where(p => p.IsBusy).Select(p => (p.Title, p.BusyText)).ToList();
        if (busy.Count > 0 && !Dialogs.Confirm("Перезапуск от имени администратора", BusyWarning(busy)))
        {
            Log.Info("Перезапуск от имени администратора отложен: не закончена работа в разделах " +
                     string.Join(", ", busy.Select(b => $"«{b.Title}»")));
            return false;
        }
        return restart();
    }

    /// <summary>The confirmation shown when something is still running (pure, unit-tested).</summary>
    internal static string BusyWarning(IReadOnlyList<(string Title, string BusyText)> busy)
    {
        var lines = busy.Select(b => string.IsNullOrWhiteSpace(b.BusyText)
            ? $"• «{b.Title}»"
            : $"• «{b.Title}»: {b.BusyText.Trim().TrimEnd('…', '.')}");
        return (busy.Count == 1 ? "Сейчас ещё не закончена работа в разделе:\n" : "Сейчас ещё не закончена работа в разделах:\n") +
               string.Join("\n", lines) + "\n\n" +
               "При перезапуске программа закроется и эта работа прервётся: то, что уже сделано, сохранится, " +
               "остальное придётся запустить заново. Окна, которые открыли другие программы (например, деинсталлятор), " +
               "продолжат работу.\n\n" +
               "Перезапустить от имени администратора сейчас? Если нет — дождитесь окончания и нажмите кнопку ещё раз.";
    }

    private static IEnumerable<PageViewModel> AllPages()
        => (AppServices.Navigator as MainViewModel)?.Pages ?? Enumerable.Empty<PageViewModel>();
}
