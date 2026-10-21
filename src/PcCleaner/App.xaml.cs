using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Core.Updates;

namespace PcCleaner;

public partial class App : Application
{
    /// <summary>Parsed command line: --screenshots &lt;dir&gt; [--page Key1,Key2] [--no-scan] [--lang ru|en] [--mode simple|expert] [--welcome].</summary>
    internal static string? ScreenshotDir { get; private set; }
    internal static string[]? ScreenshotPages { get; private set; }
    internal static bool ScreenshotScan { get; private set; } = true;
    internal static (int Width, int Height)? ScreenshotSize { get; private set; }

    /// <summary>Set by the updater when it starts the freshly installed version: --updated-from &lt;old version&gt;.</summary>
    internal static string? UpdatedFrom { get; private set; }

    /// <summary>--lang ru|en: the UI language of this run instead of Настройки (screenshots in both languages); null if absent or unknown.</summary>
    internal static string? LanguageArg { get; private set; }

    /// <summary>--mode simple|expert: the mode of this verification run instead of Настройки; null if absent or unknown.</summary>
    internal static UiMode? ModeArg { get; private set; }

    /// <summary>--welcome: the screenshot run also takes a picture of the first-run screen (00-Welcome.png), whatever the mode. A real run ignores it.</summary>
    internal static bool WelcomeArg { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ParseArgs(e.Args);

        // Verification runs must never touch the user's real settings, logs, backups or quarantine.
        if (ScreenshotDir is not null)
            AppPaths.DataDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WaterCa7sPcCleaner-verify");

        // The settings are loaded here for the first time, from the redirected folder in screenshot mode.
        AppServices.Settings.UiMode = StartupMode(ModeArg, AppServices.Settings.UiMode, ScreenshotDir is not null);

        // Before any text is made: the system UI language is read before UseLanguage replaces it.
        AppServices.Settings.Language = StartupLanguageSetting(LanguageArg, AppServices.Settings.Language, ScreenshotDir is not null);
        EnsureLanguageSaved(AppServices.Settings, CultureInfo.CurrentUICulture, s => s.Save());
        UseLanguage(StartupLanguage(LanguageArg, AppServices.Settings.Language, CultureInfo.CurrentUICulture));

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(T._("Необработанная ошибка фоновой задачи"), args.Exception.GetBaseException());
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Error(T._("Критическая ошибка"), ex);
        };

        base.OnStartup(e);

        if (ScreenshotDir is not null)
            Dialogs.UseSilentMode();

        var version = typeof(App).Assembly.GetName().Version?.ToString(3);
        Log.Info(Admin.IsElevated ? T.F("Запуск WaterCa7s PC Cleaner {0} (администратор)", version) : T.F("Запуск WaterCa7s PC Cleaner {0}", version));

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
            Log.Info(T._("Программа открыта без прав администратора: часть проверок и исправлений недоступна"));
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
                var exitCode = await ScreenshotRunner.RunAsync(window, vm, ScreenshotDir, ScreenshotPages, ScreenshotScan, WelcomeArg);
                Shutdown(exitCode);
            };
        }

        window.Show();

        // Screenshot mode never starts the splash or the welcome screen (the runner shows the latter only with --welcome): they
        // must not be visible or blur anything there. Right after an update the new version only cleans up, and the first-run
        // screen appears at once; otherwise the splash looks for a newer version while the window is still blurred, and the
        // first-run screen takes over when it closes.
        if (ScreenshotDir is null)
        {
            if (UpdatedFrom is not null)
            {
                vm.Welcome.ShowAtStart(screenshotRun: false);
                _ = AppServices.Updates.CompleteUpdateAsync(UpdatedFrom);
            }
            else
            {
                _ = RunSplashThenWelcomeAsync(vm);
            }
        }
    }

    /// <summary>
    /// The splash, then the first-run screen if no mode is chosen yet. A splash that stays up (a new version is installed and the
    /// program is going away) is followed by nothing. <see cref="SplashViewModel.RunAsync"/> never throws.
    /// </summary>
    private static async Task RunSplashThenWelcomeAsync(MainViewModel vm)
    {
        await vm.Splash.RunAsync(CancellationToken.None);
        if (!vm.Splash.IsVisible)
            vm.Welcome.ShowAtStart(screenshotRun: false);
    }

    /// <summary>The UI language of this run: --lang, else Настройки, else the system (ru-* → Russian, otherwise English).</summary>
    internal static string StartupLanguage(string? argument, string? setting, CultureInfo system)
        => T.ResolveLanguage(T.IsSupported(argument) ? argument : setting, system);

    /// <summary>
    /// The language setting of this run: Настройки, except that --lang replaces it in a screenshot run (its settings are in a temp
    /// folder), so the picture of the Settings page marks the language it is taken in. A real run keeps the saved setting: --lang
    /// only changes the language of that one run.
    /// </summary>
    internal static string? StartupLanguageSetting(string? argument, string? setting, bool screenshotRun)
        => screenshotRun && T.IsSupported(argument) ? argument : setting;

    /// <summary>
    /// Settings without a language get one and are saved at once. Versions up to 1.2 were Russian only and never wrote a language, so
    /// a settings.json that existed before this run belongs to an older install and stays Russian — also on an English Windows, also
    /// in the run the updater starts with <c>--updated-from</c>. A new install takes the language of the system. Saving it right away
    /// matters: the settings.json that the first run writes (the mode, the update check) must not look like an older install next time.
    /// A language that is set ("ru" / "en") is never touched.
    /// </summary>
    internal static void EnsureLanguageSaved(AppSettings settings, CultureInfo system, Action<AppSettings> save)
    {
        if (T.IsSupported(settings.Language))
            return;
        settings.Language = settings.ExistedOnLoad ? T.Russian : T.ResolveLanguage(null, system);
        save(settings);
    }

    /// <summary>
    /// The mode of this run: Настройки, except that --mode replaces it in a screenshot run (its settings are in a temp folder).
    /// A real run ignores --mode: the next change of any setting would save it for good.
    /// </summary>
    internal static UiMode? StartupMode(UiMode? argument, UiMode? setting, bool screenshotRun)
        => screenshotRun && argument is not null ? argument : setting;

    /// <summary>
    /// Texts (<see cref="T"/>), and numbers and dates formatted by code and by XAML bindings, follow the language for the
    /// whole run; a change in Настройки applies after a restart.
    /// </summary>
    private static void UseLanguage(string language)
    {
        T.Use(language);
        CultureInfo.DefaultThreadCurrentCulture = T.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = T.Culture;
        CultureInfo.CurrentCulture = T.Culture;
        CultureInfo.CurrentUICulture = T.Culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(T.Culture.IetfLanguageTag)));
    }

    /// <summary>
    /// Reads the command line into the static properties above. Every call starts from the defaults, so the properties describe
    /// exactly these arguments and nothing an earlier call (another start, another test) left behind.
    /// </summary>
    internal static void ParseArgs(string[] args)
    {
        ScreenshotDir = null;
        ScreenshotPages = null;
        ScreenshotScan = true;
        ScreenshotSize = null;
        UpdatedFrom = null;
        LanguageArg = null;
        ModeArg = null;
        WelcomeArg = false;

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
                case StartupElevation.ElevatedRestartArg:
                    break; // our own restart with rights: only StartupElevation.ShouldElevate looks at it
                case "--lang" when i + 1 < args.Length:
                    var language = args[++i].Trim().ToLowerInvariant();
                    LanguageArg = T.IsSupported(language) ? language : null;
                    break;
                case "--mode" when i + 1 < args.Length:
                    ModeArg = UiModes.TryParse(args[++i]);
                    break;
                case "--welcome":
                    WelcomeArg = true;
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
        Log.Error(T._("Необработанная ошибка"), e.Exception);
        Dialogs.Error(T._("Ошибка"), ErrorText.Sentence(e.Exception));
        e.Handled = true;
    }
}
