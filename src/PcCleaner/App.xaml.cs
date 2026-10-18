using System.Text;
using System.Windows;
using System.Windows.Threading;
using PcCleaner.Core;
using PcCleaner.Core.Updates;

namespace PcCleaner;

public partial class App : Application
{
    /// <summary>Parsed command line: --screenshots &lt;dir&gt; [--page Key1,Key2] [--no-scan].</summary>
    internal static string? ScreenshotDir { get; private set; }
    internal static string[]? ScreenshotPages { get; private set; }
    internal static bool ScreenshotScan { get; private set; } = true;
    internal static (int Width, int Height)? ScreenshotSize { get; private set; }

    /// <summary>Set by the updater when it starts the freshly installed version: --updated-from &lt;old version&gt;.</summary>
    internal static string? UpdatedFrom { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ParseArgs(e.Args);

        // Verification runs must never touch the user's real settings, logs, backups or quarantine.
        if (ScreenshotDir is not null)
            AppPaths.DataDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WaterCa7sPcCleaner-verify");

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

        // Always as administrator (Настройки can switch it off): restart elevated before anything else happens, so the
        // update below is installed and started by the elevated copy and no second question is asked. «Нет» in the Windows
        // prompt keeps this process: the program opens with limited rights, as it always did.
        if (StartupElevation.ShouldElevate(Admin.IsElevated, AppServices.Settings.RunAsAdmin, ScreenshotDir is not null, e.Args))
        {
            if (StartupElevation.TryRelaunchElevated(e.Args, StartupElevation.Start))
            {
                Shutdown();
                return;
            }
            Log.Info("Программа открыта без прав администратора: часть проверок и исправлений недоступна");
        }

        // Install an already downloaded update before any window appears; the new version starts and this one exits.
        if (ScreenshotDir is null && UpdatedFrom is null && AppServices.Updates.TryInstallStaged(e.Args))
        {
            Shutdown();
            return;
        }

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
            if (ScreenshotSize is { } size)
            {
                window.MinWidth = 0;
                window.MinHeight = 0;
                window.Width = size.Width;
                window.Height = size.Height;
            }
            window.Loaded += async (_, _) =>
            {
                var exitCode = await ScreenshotRunner.RunAsync(window, vm, ScreenshotDir, ScreenshotPages, ScreenshotScan);
                Shutdown(exitCode);
            };
        }

        window.Show();

        // Screenshot mode never starts the splash: it must not be visible or blur anything there. Right after an update the
        // new version only cleans up; otherwise the splash looks for a newer version while the window is still blurred.
        if (ScreenshotDir is null)
        {
            _ = UpdatedFrom is not null
                ? AppServices.Updates.CompleteUpdateAsync(UpdatedFrom)
                : vm.Splash.RunAsync(CancellationToken.None);
        }
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
                case UpdateService.UpdatedFromArg when i + 1 < args.Length:
                    UpdatedFrom = args[++i];
                    break;
                case "--size" when i + 1 < args.Length:
                    var parts = args[++i].ToLowerInvariant().Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 200 && h > 200)
                        ScreenshotSize = (w, h);
                    break;
            }
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Необработанная ошибка", e.Exception);
        Dialogs.Error("Ошибка", ErrorText.Sentence(e.Exception));
        e.Handled = true;
    }
}
