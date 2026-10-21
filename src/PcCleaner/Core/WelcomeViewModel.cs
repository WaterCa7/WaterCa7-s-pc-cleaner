using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

/// <summary>
/// The first-run screen over the blurred window, after the startup splash: the logo, «Добро пожаловать…», two cards —
/// «Простой» and «Опытный» — and the RU / EN switch. A card saves the mode (<see cref="UiModes.Choose"/>, the shell follows
/// at once) and closes the screen; the other language saves <see cref="AppSettings.Language"/> and restarts the program, which
/// shows this screen again because no mode is chosen yet. A choice is required: there is no way to close it without one.
/// Everything outside (settings, saving, restart, the running language) is passed in, so tests never write a file or restart.
/// </summary>
public sealed class WelcomeViewModel : ObservableObject
{
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _save;
    private readonly Func<bool> _restart;
    private readonly Func<string> _language;
    private bool _isVisible;
    private bool _restarting;

    /// <summary>The real one: <see cref="AppServices.Settings"/>, settings.json and <see cref="AppRestart.RestartNow"/>.</summary>
    public WelcomeViewModel()
        : this(() => AppServices.Settings, s => s.Save(), RestartProgram)
    {
    }

    /// <param name="settings">The settings, read at the time of use.</param>
    /// <param name="save">Saves the settings after a choice.</param>
    /// <param name="restart">Starts the program again and closes this copy; false when the new copy did not start.</param>
    /// <param name="language">The language this run shows ("ru" / "en"); null — <see cref="T.Language"/>.</param>
    internal WelcomeViewModel(Func<AppSettings> settings, Action<AppSettings> save, Func<bool> restart, Func<string>? language = null)
    {
        _settings = settings;
        _save = save;
        _restart = restart;
        _language = language ?? (() => T.Language);
        ChooseSimpleCommand = new RelayCommand(() => ChooseMode(UiMode.Simple));
        ChooseExpertCommand = new RelayCommand(() => ChooseMode(UiMode.Expert));
        ChooseRussianCommand = new RelayCommand(() => ChooseLanguage(T.Russian, "Русский"));
        ChooseEnglishCommand = new RelayCommand(() => ChooseLanguage(T.English, "English"));
    }

    /// <summary>Hidden until <see cref="ShowAtStart"/>, so screenshot mode and tests never see it.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        private set => Set(ref _isVisible, value);
    }

    /// <summary>The switch marks the language this run shows. «Русский» / «English» are its tooltips and names.</summary>
    public bool IsRussian => _language() == T.Russian;

    public bool IsEnglish => _language() == T.English;

    /// <summary>A mode was chosen on the screen, after it was saved; the shell opens the Dashboard.</summary>
    public event Action<UiMode>? Chosen;

    public RelayCommand ChooseSimpleCommand { get; }

    public RelayCommand ChooseExpertCommand { get; }

    public RelayCommand ChooseRussianCommand { get; }

    public RelayCommand ChooseEnglishCommand { get; }

    /// <summary>
    /// Shows the screen when it is due at start: a real run — while no mode is chosen; a screenshot run — never, except with
    /// <c>--welcome</c> (<paramref name="forced"/>, for a picture of the screen, whatever the mode). A real run ignores
    /// <paramref name="forced"/>. Returns whether the screen is shown now.
    /// </summary>
    public bool ShowAtStart(bool screenshotRun, bool forced = false)
    {
        var due = screenshotRun ? forced : _settings().UiMode is null;
        if (due)
            IsVisible = true;
        return IsVisible;
    }

    /// <summary>Closes the screen without choosing anything (the screenshot run takes its picture and goes on).</summary>
    public void Hide() => IsVisible = false;

    private void ChooseMode(UiMode mode)
    {
        UiModes.Choose(_settings(), mode, _save); // false = the same mode was already saved: still a choice
        IsVisible = false;
        Chosen?.Invoke(mode);
    }

    /// <summary>
    /// Saves the other language and restarts at once. The language this run already shows does nothing, and so does a second
    /// click while the program is restarting (it would start another copy). If the restart fails the saved language is kept
    /// (it applies at the next start) and the screen goes on working.
    /// </summary>
    private void ChooseLanguage(string language, string name)
    {
        if (_restarting || _language() == language)
            return;
        var settings = _settings();
        settings.Language = language;
        _save(settings);
        Log.Info(T.F("Настройки: язык — {0}", name));

        var restarted = false;
        _restarting = true;
        try
        {
            restarted = _restart();
        }
        finally
        {
            _restarting = restarted; // this copy is going away; the screen stays up until it does
        }
    }

    /// <summary>The restart of the real screen. If the new copy does not start, this one says so and goes on.</summary>
    internal static bool RestartProgram()
    {
        if (AppRestart.RestartNow())
            return true;
        Log.Warn(T._("Не удалось перезапустить программу"));
        Dialogs.Error(T._("Перезапуск программы"), T._("Не удалось перезапустить программу. Закройте её и откройте снова."));
        return false;
    }
}
