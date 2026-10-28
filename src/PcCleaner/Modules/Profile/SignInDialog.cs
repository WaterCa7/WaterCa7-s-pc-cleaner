using System.Windows;
using PcCleaner.Core;

namespace PcCleaner.Modules.Profile;

/// <summary>Opens the sign-in window over the main window; true when the user signed in.</summary>
public static class SignInDialog
{
    public static bool Show(SignInTab tab)
    {
        var account = AppServices.Account;
        var vm = new SignInViewModel(account, url => ProcessRunner.ShellOpen(url), tab, confirm: Dialogs.Confirm);
        var window = Create(vm);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
            window.Owner = owner;
        // A request still on its way when the window was closed may sign in after all: the account decides, not only the window.
        return window.ShowDialog() == true || account.IsSignedIn;
    }

    internal static SignInWindow Create(SignInViewModel vm) => new(vm);
}
