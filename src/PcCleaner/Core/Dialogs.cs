using System.Windows;

namespace PcCleaner.Core;

/// <summary>
/// Message boxes behind replaceable delegates so tests and screenshot mode never block or confirm anything.
/// Every destructive action MUST be confirmed through <see cref="Confirm"/> first.
/// </summary>
public static class Dialogs
{
    private static readonly Func<string, string, bool> DefaultConfirmHandler = DefaultConfirm;
    private static readonly object AnswerLock = new();
    private static (string Title, bool Answer, long Tick)? _lastAnswer;

    public static Func<string, string, bool> ConfirmHandler { get; set; } = DefaultConfirmHandler;

    public static Action<string, string> InfoHandler { get; set; } = DefaultInfo;

    public static Action<string, string> ErrorHandler { get; set; } = DefaultError;

    /// <summary>Yes/No question about a change (warning icon). Returns true only if the user explicitly pressed "Да".</summary>
    public static bool Confirm(string title, string message)
        => Ui.Invoke(() => Remember(title, ConfirmHandler(title, message)));

    /// <summary>
    /// Neutral Yes/No question where "Да" changes nothing on the system (e.g. "Открыть отчёт в браузере?"):
    /// shown with a question icon instead of the warning one. Goes through <see cref="ConfirmHandler"/> like
    /// <see cref="Confirm"/>, so tests and screenshot mode answer it the same way.
    /// </summary>
    public static bool Ask(string title, string message)
        => Ui.Invoke(() =>
        {
            var handler = ConfirmHandler;
            var answer = ReferenceEquals(handler, DefaultConfirmHandler)
                ? Show(title, message, MessageBoxImage.Question)
                : handler(title, message);
            return Remember(title, answer);
        });

    public static void Info(string title, string message) => Ui.Post(() => InfoHandler(title, message));

    public static void Error(string title, string message) => Ui.Post(() => ErrorHandler(title, message));

    /// <summary>Non-interactive mode: every confirmation is declined, nothing is shown.</summary>
    public static void UseSilentMode()
    {
        ConfirmHandler = (_, _) => false;
        InfoHandler = (_, _) => { };
        ErrorHandler = (t, m) => Log.Warn($"[диалог] {t}: {m}");
    }

    /// <summary>
    /// True (once) if the last question answered was titled <paramref name="title"/>, was answered "Да" and was answered
    /// less than <paramref name="within"/> ago. Lets a second step skip repeating a question the user has just answered.
    /// </summary>
    internal static bool TakeRecentYes(string title, TimeSpan within)
    {
        lock (AnswerLock)
        {
            if (_lastAnswer is not { } last)
                return false;
            _lastAnswer = null;
            return last.Answer && last.Title == title && Environment.TickCount64 - last.Tick <= within.TotalMilliseconds;
        }
    }

    private static bool Remember(string title, bool answer)
    {
        lock (AnswerLock)
            _lastAnswer = (title, answer, Environment.TickCount64);
        return answer;
    }

    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    private static bool DefaultConfirm(string title, string message) => Show(title, message, MessageBoxImage.Warning);

    private static bool Show(string title, string message, MessageBoxImage icon)
    {
        var owner = Owner;
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private static void DefaultInfo(string title, string message)
    {
        var owner = Owner;
        if (owner is null)
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void DefaultError(string title, string message)
    {
        var owner = Owner;
        if (owner is null)
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
