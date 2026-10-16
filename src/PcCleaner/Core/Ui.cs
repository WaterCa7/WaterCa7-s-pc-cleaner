using System.Windows;
using System.Windows.Threading;

namespace PcCleaner.Core;

/// <summary>UI-thread helpers that also work in unit tests (no Application).</summary>
public static class Ui
{
    private static Dispatcher? Dispatcher => Application.Current?.Dispatcher;

    public static void Post(Action action)
    {
        var d = Dispatcher;
        if (d is null || d.CheckAccess())
            action();
        else
            d.BeginInvoke(action);
    }

    public static void Invoke(Action action)
    {
        var d = Dispatcher;
        if (d is null || d.CheckAccess())
            action();
        else
            d.Invoke(action);
    }

    public static T Invoke<T>(Func<T> func)
    {
        var d = Dispatcher;
        return d is null || d.CheckAccess() ? func() : d.Invoke(func);
    }
}
