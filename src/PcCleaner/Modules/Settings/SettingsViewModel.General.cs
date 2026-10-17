using System.Collections.ObjectModel;
using PcCleaner.Core;

namespace PcCleaner.Modules.Settings;

// Tab "Основные": restore point switch, temp-file age, cleaner exclusions, trusted items, reset.
public sealed partial class SettingsViewModel
{
    private bool _createRestorePoint = true;
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
                ? "Настройки: точка восстановления перед изменениями системы — включено"
                : "Настройки: точка восстановления перед изменениями системы — выключено");
        }
    }

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
            SaveSettings($"Настройки: не удалять временные файлы моложе {Format.Count(hours, "часа", "часов", "часов")}");
            OnPropertyChanged(nameof(AgeHint));
        }
    }

    public string AgeHint => (SelectedAgeOption?.Hours ?? SettingsLogic.DefaultMinAgeHours) switch
    {
        0 => "Внимание: временные файлы будут удаляться сразу, даже если их только что создала работающая программа или установщик.",
        < 6 => "Короткий срок: возможны ошибки у программ, которые прямо сейчас что-то устанавливают или обновляют.",
        _ => "Свежие временные файлы часто принадлежат работающим программам и установщикам — их безопаснее не трогать.",
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
        "Перед пакетными изменениями (службы, оптимизация, удаление программ) Windows сохранит состояние системы — если что-то пойдёт не так, его можно будет вернуть. " +
        "Если защита системы выключена, точка не создаётся — программа её не включает сама.";

    public string? RestorePointAdminWarning => Admin.IsElevated
        ? null
        : "Без прав администратора точки не создаются.";

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
            Log.Info($"Настройки: исправлено некорректных значений — {fixes}");

        _loadingGeneral = true;
        try
        {
            // Backing fields + explicit notifications: loading must never trigger a save.
            _createRestorePoint = s.CreateRestorePoint;
            OnPropertyChanged(nameof(CreateRestorePoint));

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

        SaveSettings($"Настройки: добавлено исключение очистки — {normalized}");
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
        var path = FolderPicker.Pick("Папка, которую очистка не должна трогать", initial);
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
        SaveSettings($"Настройки: удалено исключений очистки — {removed}: {string.Join("; ", items)}");
        Exclusions = new ObservableCollection<string>(Settings.CleanerExclusions);
        ExclusionMessage = removed == 1 ? "Исключение удалено." : $"Удалено исключений: {removed}.";
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
            ? "Убрано из доверенных. Проверки в «Процессах», «Автозагрузке» и «Программах» снова будут это учитывать."
            : $"Убрано из доверенных: {Format.Number(removed)}. Проверки снова будут их учитывать.";
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
            "Вернуть настройки программы к значениям по умолчанию?\n\n" +
            "• точка восстановления перед изменениями системы — включена;\n" +
            "• не удалять временные файлы моложе 24 часов;\n" +
            $"• список исключений очистки будет очищен (сейчас в нём {Format.Count(s.CleanerExclusions.Count, "запись", "записи", "записей")});\n" +
            $"• список доверенных программ будет очищен (сейчас в нём {Format.Count(s.TrustedItems.Count, "запись", "записи", "записей")});\n" +
            "• выбор категорий на странице «Очистка» будет сброшен.\n\n" +
            "Журнал, резервные копии, карантин и сведения для отмены оптимизаций не изменятся. " +
            "Сам сброс отменить нельзя — исключения и доверенные программы придётся добавить заново.";
        if (!Dialogs.Confirm("Сброс настроек", message))
            return;

        SettingsLogic.ResetToDefaults(s);
        SaveSettings("Настройки сброшены к значениям по умолчанию");
        LoadGeneral();
        ExclusionMessage = null;
        Dialogs.Info("Сброс настроек", "Готово: настройки возвращены к значениям по умолчанию.");
    }

    private void SaveSettings(string logMessage)
    {
        _save(Settings);
        Log.Info(logMessage);
    }
}
