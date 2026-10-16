using System.Windows;

namespace PcCleaner.Core;

/// <summary>
/// Message boxes behind replaceable delegates so tests and screenshot mode never block or confirm anything.
/// Every destructive action MUST be confirmed through <see cref="Confirm"/> first.
/// </summary>
public static class Dialogs
{
    public static Func<string, string, bool> ConfirmHandler { get; set; } = DefaultConfirm;

    public static Action<string, string> InfoHandler { get; set; } = DefaultInfo;

    public static Action<string, string> ErrorHandler { get; set; } = DefaultError;

    /// <summary>Yes/No question. Returns true only if the user explicitly pressed "Да".</summary>
    public static bool Confirm(string title, string message) => Ui.Invoke(() => ConfirmHandler(title, message));

    public static void Info(string title, string message) => Ui.Post(() => InfoHandler(title, message));

    public static void Error(string title, string message) => Ui.Post(() => ErrorHandler(title, message));

    /// <summary>Non-interactive mode: every confirmation is declined, nothing is shown.</summary>
    public static void UseSilentMode()
    {
        ConfirmHandler = (_, _) => false;
        InfoHandler = (_, _) => { };
        ErrorHandler = (t, m) => Log.Warn($"[диалог] {t}: {m}");
    }

    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    private static bool DefaultConfirm(string title, string message)
    {
        var owner = Owner;
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
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
