using System.Collections.ObjectModel;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Settings;

// Tab "Основные": restore point switch, temp-file age, cleaner exclusions, trusted items, reset.
public sealed partial class SettingsViewModel
{
    private bool _createRestorePoint = true;
    private bool _runAsAdmin = true;
    private List<AgeOption> _ageOptions = SettingsLogic.BuildAgeOptions(SettingsLogic.DefaultMinAgeHours);
    private AgeOption? _selectedAgeOption;
    private ObservableCollection<string> _exclusions = new();
    private ObservableCollection<string> _trustedItems = new();
    private string _newExclusionText = "";
    private string? _exclusionMessage;
    private bool _exclusionMessageIsError;
    private bool _loadingGeneral;

    public bool CreateRestorePoint
    {
        get => _createRestorePoint;
        set
        {
            if (!Set(ref _createRestorePoint, value) || _loadingGeneral)
                return;
            Settings.CreateRestorePoint = value;
            SaveSettings(value
                ? T._("Настройки: точка восстановления перед изменениями системы — включено")
                : T._("Настройки: точка восстановления перед изменениями системы — выключено"));
        }
    }

    /// <summary>
    /// «Запускать с правами администратора»: the installed program starts elevated through its task (<see cref="ElevatedTask"/>),
    /// other copies restart themselves through UAC (<see cref="StartupElevation"/>).
    /// </summary>
    public bool RunAsAdmin
    {
        get => _runAsAdmin;
        set
        {
            if (!Set(ref _runAsAdmin, value) || _loadingGeneral)
                return;
            Settings.RunAsAdmin = value;
            SaveSettings(value
                ? T._("Настройки: запуск с правами администратора — включён")
                : T._("Настройки: запуск с правами администратора — выключен"));
            // An elevated copy creates or removes the task at once; a limited one leaves it — the start reads the setting first.
            ElevatedTask.Sync(value, Admin.IsElevated, Environment.ProcessPath ?? "", ElevatedTask.ProgramFiles, ElevatedTask.CurrentUserSid());
        }
    }

    // ----- «Режим» and «Язык» -----
    // Two option buttons each (RadioButtons in one group). Choosing writes "false" into the other one too, so a setter only reacts to
    // "true". Nothing is kept here: the settings are the one truth, and LoadGeneral tells the buttons to read them again.

    /// <summary>«Простой»: the sidebar shows four pages (<see cref="UiModes"/>). Applies at once, no restart.</summary>
    public bool IsSimpleMode
    {
        get => UiModes.Of(Settings) == UiMode.Simple;
        set => ChooseMode(value, UiMode.Simple);
    }

    /// <summary>«Опытный»: every page. Also what is shown while no mode is chosen yet.</summary>
    public bool IsExpertMode
    {
        get => UiModes.Of(Settings) == UiMode.Expert;
        set => ChooseMode(value, UiMode.Expert);
    }

    private void ChooseMode(bool selected, UiMode mode)
    {
        if (!selected || _loadingGeneral)
            return;
        if (UiModes.Choose(Settings, mode, _save))
            RaiseModeChanged();
    }

    private void RaiseModeChanged()
    {
        OnPropertyChanged(nameof(IsSimpleMode));
        OnPropertyChanged(nameof(IsExpertMode));
    }

    /// <summary>The language of the buttons: the saved choice, else the one this run started with (the system's).</summary>
    private string SelectedLanguage => Settings.Language ?? T.Language;

    /// <summary>«Русский».</summary>
    public bool IsRussian
    {
        get => SelectedLanguage == T.Russian;
        set => ChooseLanguage(value, T.Russian, "Русский");
    }

    /// <summary>«English».</summary>
    public bool IsEnglish
    {
        get => SelectedLanguage == T.English;
        set => ChooseLanguage(value, T.English, "English");
    }

    /// <summary>The chosen language is not the one this run shows: «Перезапустить сейчас» is offered.</summary>
    public bool LanguageNeedsRestart => SelectedLanguage != T.Language;

    private void ChooseLanguage(bool selected, string language, string name)
    {
        if (!selected || _loadingGeneral || Settings.Language == language)
            return;
        Settings.Language = language;
        SaveSettings(T.F("Настройки: язык — {0}", name));
        RaiseLanguageChanged();
    }

    private void RaiseLanguageChanged()
    {
        OnPropertyChanged(nameof(IsRussian));
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(LanguageNeedsRestart));
    }

    /// <summary>
    /// Restarts the program (<see cref="AppRestart"/>). The public constructor puts the real one here; the constructor of the
    /// tests leaves one that starts nothing, so no test can ever start the program.
    /// </summary>
    internal Func<bool> RestartProgram { get; set; } = () => false;

    /// <summary>The pages whose work a restart would interrupt (all pages of the shell).</summary>
    internal Func<IEnumerable<PageViewModel>> OpenPages { get; set; } = () => AppServices.Navigator?.Pages ?? Enumerable.Empty<PageViewModel>();

    /// <summary>The Yes/No question (<see cref="Dialogs.Confirm"/>); tests replace it.</summary>
    internal Func<string, string, bool> Confirm { get; set; } = Dialogs.Confirm;

    public RelayCommand RestartNowCommand { get; private set; } = null!;

    /// <summary>«Перезапустить сейчас»: asks first if a page is still working, then starts the program again with the same arguments.</summary>
    private void RestartNow()
    {
        var busy = OpenPages().Where(p => p.IsBusy).ToList();
        if (busy.Count > 0)
        {
            var list = string.Join("\n", busy.Select(p => $"• {T.Quote(p.Title)}"));
            if (!Confirm(T._("Перезапуск программы"),
                    T.F("Сейчас ещё идёт работа:\n{0}\n\nПри перезапуске она прервётся. Перезапустить программу сейчас?", list)))
                return;
        }
        if (RestartProgram())
            return;
        Log.Warn(T._("Не удалось перезапустить программу"));
        Dialogs.Error(T._("Перезапуск программы"), T._("Не удалось перезапустить программу. Закройте её и откройте снова."));
    }

    public string RunAsAdminHint =>
        T._("Установленная программа запускается с правами без вопросов Windows; без прав часть проверок и исправлений недоступна.");

    public List<AgeOption> AgeOptions
    {
        get => _ageOptions;
        private set => Set(ref _ageOptions, value);
    }

    public AgeOption? SelectedAgeOption
    {
        get => _selectedAgeOption;
        set
        {
            if (value is null || !Set(ref _selectedAgeOption, value) || _loadingGeneral)
                return;
            var hours = SettingsLogic.ClampMinAge(value.Hours);
            if (hours == Settings.CleanerMinFileAgeHours)
                return;
            Settings.CleanerMinFileAgeHours = hours;
            SaveSettings(T.F("Настройки: не удалять временные файлы моложе {0}", Format.Count(hours, "часа", "часов", "часов")));
            OnPropertyChanged(nameof(AgeHint));
        }
    }

    public string AgeHint => (SelectedAgeOption?.Hours ?? SettingsLogic.DefaultMinAgeHours) switch
    {
        0 => T._("Внимание: временные файлы будут удаляться сразу, даже если их только что создала работающая программа или установщик."),
        < 6 => T._("Короткий срок: возможны ошибки у программ, которые прямо сейчас что-то устанавливают или обновляют."),
        _ => T._("Свежие временные файлы часто принадлежат работающим программам и установщикам — их безопаснее не трогать."),
    };

    public ObservableCollection<string> Exclusions
    {
        get => _exclusions;
        private set => Set(ref _exclusions, value);
    }

    public ObservableCollection<string> TrustedItems
    {
        get => _trustedItems;
        private set => Set(ref _trustedItems, value);
    }

    public string NewExclusionText
    {
        get => _newExclusionText;
        set
        {
            if (Set(ref _newExclusionText, value ?? "") && _exclusionMessageIsError)
                ExclusionMessage = null;
        }
    }

    public string? ExclusionMessage
    {
        get => _exclusionMessage;
        private set => Set(ref _exclusionMessage, value);
    }

    public bool ExclusionMessageIsError
    {
        get => _exclusionMessageIsError;
        private set => Set(ref _exclusionMessageIsError, value);
    }

    public string RestorePointHint =>
        T._("Перед пакетными изменениями (службы, оптимизация, удаление программ) Windows сохранит состояние системы — если что-то пойдёт не так, его можно будет вернуть. Если защита системы выключена, точка не создаётся — программа её не включает сама.");

    public string? RestorePointAdminWarning => Admin.IsElevated
        ? null
        : T._("Без прав администратора точки не создаются.");

    // ----- "Данные программы" tiles -----

    public string LogTileLabel => Format.Plural(LogTotal, "запись в журнале", "записи в журнале", "записей в журнале");

    public string BackupsTileLabel => Format.Plural(Backups.Count, "резервная копия", "резервные копии", "резервных копий");

    public string QuarantineTileLabel => Format.Plural(QuarantineItems.Count, "файл в карантине", "файла в карантине", "файлов в карантине");

    public RelayCommand SelectTabCommand { get; private set; } = null!;

    public RelayCommand AddExclusionCommand { get; private set; } = null!;
    public RelayCommand BrowseExclusionCommand { get; private set; } = null!;
    public RelayCommand RemoveExclusionsCommand { get; private set; } = null!;
    public RelayCommand RemoveTrustedCommand { get; private set; } = null!;
    public RelayCommand ResetSettingsCommand { get; private set; } = null!;

    private void InitGeneralCommands()
    {
        AddExclusionCommand = new RelayCommand(() => AddExclusion(NewExclusionText));
        BrowseExclusionCommand = new RelayCommand(BrowseExclusion);
        RemoveExclusionsCommand = new RelayCommand(p => RemoveExclusions(ItemsFrom<string>(p)),
            p => ItemsFrom<string>(p).Count > 0);
        RemoveTrustedCommand = new RelayCommand(p => RemoveTrusted(ItemsFrom<string>(p)),
            p => ItemsFrom<string>(p).Count > 0);
        ResetSettingsCommand = new RelayCommand(ResetSettings);
        RestartNowCommand = new RelayCommand(RestartNow);
        SelectTabCommand = new RelayCommand(p =>
        {
            if (int.TryParse(p?.ToString(), out var tab) && tab is >= TabGeneral and <= TabAbout)
                SelectedTabIndex = tab;
        });
    }

    /// <summary>Reads current settings into the page without saving anything.</summary>
    internal void LoadGeneral()
    {
        var s = Settings;
        var fixes = SettingsLogic.Sanitize(s);
        if (fixes > 0)
            Log.Info(T.F("Настройки: исправлено некорректных значений — {0}", fixes));

        _loadingGeneral = true;
        try
        {
            // Backing fields + explicit notifications: loading must never trigger a save.
            _createRestorePoint = s.CreateRestorePoint;
            OnPropertyChanged(nameof(CreateRestorePoint));
            _runAsAdmin = s.RunAsAdmin;
            OnPropertyChanged(nameof(RunAsAdmin));
            _autoUpdate = s.AutoUpdate;
            OnPropertyChanged(nameof(AutoUpdate));
            OnPropertyChanged(nameof(LastCheckText));
            RaiseModeChanged();
            RaiseLanguageChanged();

            var options = SettingsLogic.BuildAgeOptions(s.CleanerMinFileAgeHours);
            if (!options.SequenceEqual(AgeOptions))
                AgeOptions = options;
            _selectedAgeOption = AgeOptions.FirstOrDefault(o => o.Hours == s.CleanerMinFileAgeHours)
                                 ?? AgeOptions.First(o => o.Hours == SettingsLogic.DefaultMinAgeHours);
            OnPropertyChanged(nameof(SelectedAgeOption));
            OnPropertyChanged(nameof(AgeHint));

            Exclusions = new ObservableCollection<string>(s.CleanerExclusions);
            // Other pages may have trusted or untrusted something since the last visit.
            if (!TrustedItems.SequenceEqual(s.TrustedItems))
            {
                TrustedItems = new ObservableCollection<string>(s.TrustedItems);
                TrustedMessage = null;
            }
        }
        finally
        {
            _loadingGeneral = false;
        }
    }

    internal bool AddExclusion(string? input)
    {
        var result = SettingsLogic.TryAddExclusion(Settings, input, out var normalized, out var message);
        ExclusionMessage = message;
        ExclusionMessageIsError = result != ExclusionAddResult.Added;
        if (result != ExclusionAddResult.Added)
            return false;

        SaveSettings(T.F("Настройки: добавлено исключение очистки — {0}", normalized));
        Exclusions = new ObservableCollection<string>(Settings.CleanerExclusions);
        NewExclusionText = "";
        return true;
    }

    private void BrowseExclusion()
    {
        var last = Exclusions.LastOrDefault();
        var initial = last is not null && !SettingsLogic.IsNetworkPath(last)
            ? last
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = FolderPicker.Pick(T._("Папка, которую очистка не должна трогать"), initial);
        if (path is null)
            return;
        NewExclusionText = path;
        AddExclusion(path);
    }

    internal void RemoveExclusions(IReadOnlyCollection<string> items)
    {
        if (items.Count == 0)
            return;
        var remaining = SettingsLogic.Without(Settings.CleanerExclusions, items, out var removed);
        if (removed == 0)
            return;
        Settings.CleanerExclusions = remaining;
        SaveSettings(T.F("Настройки: удалено исключений очистки — {0}: {1}", removed, string.Join("; ", items)));
        Exclusions = new ObservableCollection<string>(Settings.CleanerExclusions);
        ExclusionMessage = removed == 1 ? T._("Исключение удалено.") : T.F("Удалено исключений: {0}.", removed);
        ExclusionMessageIsError = false;
    }

    internal void RemoveTrusted(IReadOnlyCollection<string> items)
    {
        if (items.Count == 0)
            return;
        // Only through AppSettings.RemoveTrusted: other pages read the shared list on background threads.
        // It saves the settings and logs every removed item itself.
        var removed = SettingsLogic.RemoveTrusted(Settings, items);
        TrustedItems = new ObservableCollection<string>(Settings.TrustedItems);
        if (removed == 0)
            return;
        TrustedMessage = removed == 1
            ? T._("Убрано из доверенных. Проверки в «Процессах», «Автозагрузке» и «Программах» снова будут это учитывать.")
            : T.F("Убрано из доверенных: {0}. Проверки снова будут их учитывать.", Format.Number(removed));
    }

    /// <summary>Result of the last removal from the trusted list (shown under the list).</summary>
    public string? TrustedMessage
    {
        get => _trustedMessage;
        private set => Set(ref _trustedMessage, value);
    }

    private string? _trustedMessage;

    private void ResetSettings()
    {
        var s = Settings;
        var message =
            T.F("Вернуть настройки программы к значениям по умолчанию?\n\n• точка восстановления перед изменениями системы — включена;\n• не удалять временные файлы моложе 24 часов;\n• список исключений очистки будет очищен (сейчас в нём {0});\n• список доверенных программ будет очищен (сейчас в нём {1});\n• выбор категорий на странице «Очистка» будет сброшен.\n\nЖурнал, резервные копии, карантин и сведения для отмены оптимизаций не изменятся. Сам сброс отменить нельзя — исключения и доверенные программы придётся добавить заново.", Format.Count(s.CleanerExclusions.Count, "запись", "записи", "записей"), Format.Count(s.TrustedItems.Count, "запись", "записи", "записей"));
        if (!Dialogs.Confirm(T._("Сброс настроек"), message))
            return;

        SettingsLogic.ResetToDefaults(s);
        SaveSettings(T._("Настройки сброшены к значениям по умолчанию"));
        LoadGeneral();
        ExclusionMessage = null;
        Dialogs.Info(T._("Сброс настроек"), T._("Готово: настройки возвращены к значениям по умолчанию."));
    }

    private void SaveSettings(string logMessage)
    {
        _save(Settings);
        Log.Info(logMessage);
    }
}
