using System.Text;
using System.Windows;
using System.Windows.Threading;
using PcCleaner.Core;

namespace PcCleaner;

public partial class App : Application
{
    /// <summary>Parsed command line: --screenshots &lt;dir&gt; [--page Key1,Key2] [--no-scan].</summary>
    internal static string? ScreenshotDir { get; private set; }
    internal static string[]? ScreenshotPages { get; private set; }
    internal static bool ScreenshotScan { get; private set; } = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ParseArgs(e.Args);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Необработанная ошибка фоновой задачи", args.Exception.GetBaseException());
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Error("Критическая ошибка", ex);
        };

        base.OnStartup(e);

        if (ScreenshotDir is not null)
            Dialogs.UseSilentMode();

        Log.Info($"Запуск WaterCa7s PC Cleaner {typeof(App).Assembly.GetName().Version?.ToString(3)}" +
                 (Admin.IsElevated ? " (администратор)" : ""));

        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;

        if (ScreenshotDir is not null)
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.Loaded += async (_, _) =>
            {
                var exitCode = await ScreenshotRunner.RunAsync(window, vm, ScreenshotDir, ScreenshotPages, ScreenshotScan);
                Shutdown(exitCode);
            };
        }

        window.Show();
    }

    private static void ParseArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--screenshots" when i + 1 < args.Length:
                    ScreenshotDir = args[++i];
                    break;
                case "--page" when i + 1 < args.Length:
                    ScreenshotPages = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--no-scan":
                    ScreenshotScan = false;
                    break;
            }
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Необработанная ошибка", e.Exception);
        Dialogs.Error("Ошибка", e.Exception.Message);
        e.Handled = true;
    }
}
