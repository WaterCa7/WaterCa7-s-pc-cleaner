using System.Windows;
using PcCleaner.Core;

namespace PcCleaner.Modules.Profile;

/// <summary>Opens the sign-in window over the main window; true when the user signed in.</summary>
public static class SignInDialog
{
    public static bool Show(SignInTab tab)
    {
        var vm = new SignInViewModel(AppServices.Account, url => ProcessRunner.ShellOpen(url), tab);
        var window = Create(vm);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
            window.Owner = owner;
        return window.ShowDialog() == true;
    }

    internal static SignInWindow Create(SignInViewModel vm) => new(vm);
}
