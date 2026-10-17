using PcCleaner.Core;

namespace PcCleaner.Modules.Settings;

/// <summary>
/// "Перезапустить от имени администратора" for the pages that offer a restart button of their own (Настройки, Очистка,
/// Диски, Автозагрузка, Программы). A thin wrapper over <see cref="Admin"/>: Core itself asks first when another page is
/// still busy (an uninstall the app is waiting for, a disk optimization, a cleaning…) and, on a standard account, explains
/// that the program will run under the administrator's account — each question is asked once.
/// </summary>
internal static class ElevatedRestart
{
    /// <summary>Restarts with a UAC prompt (see <see cref="Admin.RestartElevated"/>). Returns true if the restart started.</summary>
    public static bool Request() => !Admin.IsElevated && Admin.RestartElevated();

    /// <summary>The same as <see cref="Admin.RequireElevation"/>. Returns <see cref="Admin.IsElevated"/>.</summary>
    public static bool RequireElevation(string actionDescription) => Admin.RequireElevation(actionDescription);

    /// <summary>The confirmation shown when something is still running (the text Core asks with).</summary>
    internal static string BusyWarning(IReadOnlyList<(string Title, string BusyText)> busy) => Admin.BusyWarning(busy);
}
