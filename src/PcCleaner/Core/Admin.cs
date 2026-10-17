using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Principal;
using System.Windows;

namespace PcCleaner.Core;

public static class Admin
{
    /// <summary>Title of the questions asked before a restart (also used by Settings/ElevatedRestart).</summary>
    internal const string RestartTitle = "Перезапуск от имени администратора";

    private const string AdministratorsSid = "S-1-5-32-544";

    private static readonly Lazy<bool> Elevated = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    private static readonly Lazy<bool> AdministratorAccount = new(() =>
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return IsAdministratorAccount(identity);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось определить тип учётной записи: {ErrorText.For(ex)}");
            return true; // unknown: behave as before (no extra question)
        }
    });

    private static readonly Lazy<(string Process, string? Session)> Accounts = new(ReadAccounts);

    public static bool IsElevated => Elevated.Value;

    /// <summary>
    /// True for a standard (non-administrator) Windows account. Restarting "as administrator" from it makes Windows ask
    /// for the password of ANOTHER account, and the restarted program then works with that account's profile
    /// (its temp files, startup entries, browsers, settings, quarantine) instead of the current user's.
    /// </summary>
    public static bool IsStandardUser => !IsElevated && !AdministratorAccount.Value;

    /// <summary>
    /// "DOMAIN\user" signed in to this Windows session when the program runs under a different account
    /// (e.g. an administrator's password was typed into the UAC prompt on a standard account); otherwise null.
    /// </summary>
    public static string? SessionUserIfDifferent
    {
        get
        {
            var (process, session) = Accounts.Value;
            return session is not null && !string.Equals(process, session, StringComparison.OrdinalIgnoreCase) ? session : null;
        }
    }

    /// <summary>"DOMAIN\user" the program runs as.</summary>
    public static string ProcessUser => Accounts.Value.Process;

    /// <summary>Warning shown in the shell when the program runs under another account than the signed-in user; null otherwise.</summary>
    public static string? OtherAccountWarning => SessionUserIfDifferent is { } session
        ? OtherAccountText(ProcessUser, session)
        : null;

    /// <summary>
    /// Restarts the app with a UAC prompt. Returns false if the user declined.
    /// Restarting closes the program, so it first asks when any page is still busy, and on a standard account
    /// explains that the program will run under the administrator's account.
    /// </summary>
    public static bool RestartElevated() => RestartElevated(standardUserNoteShown: false);

    /// <summary>Shows a standard message when an action needs elevation. Returns IsElevated.</summary>
    public static bool RequireElevation(string actionDescription)
    {
        if (IsElevated)
            return true;
        if (Dialogs.Confirm("Нужны права администратора", ElevationQuestion(actionDescription, IsStandardUser)))
            RestartElevated(standardUserNoteShown: true);
        return false;
    }

    private static bool RestartElevated(bool standardUserNoteShown)
    {
        var pages = AppServices.Navigator?.Pages ?? Enumerable.Empty<PageViewModel>();
        if (!ConfirmRestart(pages, IsStandardUser && !standardUserNoteShown))
            return false;

        var exe = Environment.ProcessPath;
        if (exe is null)
            return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });
            Application.Current?.Shutdown();
            return true;
        }
        catch (Win32Exception)
        {
            Log.Info("Пользователь отменил запуск от имени администратора");
            return false;
        }
    }

    /// <summary>
    /// The questions asked before a restart (test seam). Returns true if the restart may go ahead:
    /// nothing is running (or the user agreed to interrupt it) and, on a standard account, the user agreed to
    /// run the program under the administrator's account.
    /// </summary>
    internal static bool ConfirmRestart(IEnumerable<PageViewModel> pages, bool askAboutOtherAccount)
    {
        var busy = pages.Where(p => p.IsBusy).Select(p => (p.Title, p.BusyText)).ToList();
        // Settings/ElevatedRestart asks this very question right before calling RestartElevated: never ask twice.
        var justConfirmed = Dialogs.TakeRecentYes(RestartTitle, TimeSpan.FromSeconds(2));
        if (busy.Count > 0 && !justConfirmed && !Dialogs.Confirm(RestartTitle, BusyWarning(busy)))
        {
            Log.Info("Перезапуск от имени администратора отложен: не закончена работа в разделах " +
                     string.Join(", ", busy.Select(b => $"«{b.Title}»")));
            return false;
        }
        if (askAboutOtherAccount && !Dialogs.Confirm(RestartTitle, StandardUserWarning))
        {
            Log.Info("Перезапуск от имени администратора отменён: учётная запись без прав администратора");
            return false;
        }
        return true;
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

    internal const string StandardUserNote =
        "Ваша учётная запись Windows — обычная, без прав администратора. Windows попросит пароль администратора, " +
        "и программа запустится от его имени: очистка, автозагрузка, браузеры и другие проверки профиля будут относиться " +
        "к профилю администратора, а не к вашему. Настройки программы, карантин и резервные копии у той учётной записи тоже свои.";

    internal const string StandardUserWarning =
        StandardUserNote + "\n\nПерезапустить программу от имени администратора?";

    /// <summary>"Для действия «…» нужны права администратора…" (pure, unit-tested).</summary>
    internal static string ElevationQuestion(string actionDescription, bool standardUser)
        => $"Для действия «{actionDescription}» нужны права администратора.\n\n" +
           (standardUser ? StandardUserNote + "\n\n" : "") +
           "Перезапустить программу от имени администратора?";

    internal static string OtherAccountText(string processUser, string sessionUser)
        => $"Программа запущена от имени «{ShortName(processUser)}», а в Windows вошёл «{ShortName(sessionUser)}». " +
           $"Очистка, автозагрузка, браузеры и другие проверки профиля относятся к «{ShortName(processUser)}», а не к «{ShortName(sessionUser)}».";

    private static string ShortName(string account)
    {
        var slash = account.LastIndexOf('\\');
        return slash >= 0 && slash < account.Length - 1 ? account[(slash + 1)..] : account;
    }

    /// <summary>
    /// Elevated, a member of Administrators, or an administrator running with a filtered UAC token
    /// (Administrators is then present as a deny-only group).
    /// </summary>
    internal static bool IsAdministratorAccount(WindowsIdentity identity)
    {
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return true;
        return identity.Claims.Any(c => c.Type == ClaimTypes.DenyOnlySid && c.Value == AdministratorsSid);
    }

    private static (string Process, string? Session) ReadAccounts()
    {
        string process;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            process = identity.Name;
        }
        catch
        {
            process = $"{Environment.UserDomainName}\\{Environment.UserName}";
        }

        string? session = null;
        try
        {
            var user = QuerySession(WtsUserName);
            var domain = QuerySession(WtsDomainName);
            if (!string.IsNullOrWhiteSpace(user))
                session = string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            // Unknown session user: no warning.
        }
        return (process, session);
    }

    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;
    private const int WtsCurrentSession = -1;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private static string? QuerySession(int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession, infoClass, out var buffer, out _) || buffer == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }
}
