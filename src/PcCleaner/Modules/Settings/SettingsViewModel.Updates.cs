using PcCleaner.Core;
using PcCleaner.Core.Updates;

namespace PcCleaner.Modules.Settings;

// Card «Обновления» on the «Основные» tab: version, last check, «Проверить сейчас», the AutoUpdate switch.
public sealed partial class SettingsViewModel
{
    private bool _autoUpdate = true;
    private AsyncRelayCommand? _checkUpdatesCommand;

    public UpdateService Updates => AppServices.Updates;

    public bool AutoUpdate
    {
        get => _autoUpdate;
        set
        {
            if (!Set(ref _autoUpdate, value) || _loadingGeneral)
                return;
            Settings.AutoUpdate = value;
            SaveSettings(value ? "Настройки: автоматическое обновление — включено" : "Настройки: автоматическое обновление — выключено");
        }
    }

    public string VersionLine => $"Установлена версия {AppInfo.Version}";

    public string LastCheckText => Settings.LastUpdateCheckUtc is { } t
        ? $"Последняя проверка: {t.ToLocalTime():dd.MM.yyyy HH:mm}"
        : "Обновления ещё не проверялись";

    public AsyncRelayCommand CheckUpdatesCommand => _checkUpdatesCommand ??= new AsyncRelayCommand(async () =>
    {
        await Updates.CheckAsync(manual: true, CancellationToken.None);
        OnPropertyChanged(nameof(LastCheckText));
    });
}
