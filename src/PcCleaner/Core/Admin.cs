using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace PcCleaner.Core;

public static class Admin
{
    private static readonly Lazy<bool> Elevated = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    public static bool IsElevated => Elevated.Value;

    /// <summary>Restarts the app with a UAC prompt. Returns false if the user declined.</summary>
    public static bool RestartElevated()
    {
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

    /// <summary>Shows a standard message when an action needs elevation. Returns IsElevated.</summary>
    public static bool RequireElevation(string actionDescription)
    {
        if (IsElevated)
            return true;
        if (Dialogs.Confirm("Нужны права администратора",
                $"Для действия «{actionDescription}» нужны права администратора.\n\nПерезапустить программу от имени администратора?"))
            RestartElevated();
        return false;
    }
}
