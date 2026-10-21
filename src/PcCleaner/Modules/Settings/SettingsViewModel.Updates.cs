using PcCleaner.Core;
using PcCleaner.Core.Localization;
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
            SaveSettings(value ? T._("Настройки: автоматическое обновление — включено") : T._("Настройки: автоматическое обновление — выключено"));
        }
    }

    public string VersionLine => T.F("Установлена версия {0}", AppInfo.Version);

    public string LastCheckText => Settings.LastUpdateCheckUtc is { } t
        ? T.F("Последняя проверка: {0}", Format.DateTime(t.ToLocalTime()))
        : T._("Обновления ещё не проверялись");

    public AsyncRelayCommand CheckUpdatesCommand => _checkUpdatesCommand ??= new AsyncRelayCommand(async () =>
    {
        await Updates.CheckAsync(manual: true, CancellationToken.None);
        OnPropertyChanged(nameof(LastCheckText));
    });
}
