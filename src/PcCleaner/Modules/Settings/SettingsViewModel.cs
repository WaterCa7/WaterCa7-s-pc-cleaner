using System.Collections;
using System.Collections.ObjectModel;
using PcCleaner.Core;

namespace PcCleaner.Modules.Settings;

/// <summary>
/// "Настройки": app preferences, live journal, registry/other backups, quarantine, system restore and "about".
/// Split into partial files per tab (SettingsViewModel.*.cs).
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    public const int TabGeneral = 0;
    public const int TabLog = 1;
    public const int TabBackups = 2;
    public const int TabQuarantine = 3;
    public const int TabRestore = 4;
    public const int TabAbout = 5;

    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _save;
    private readonly ObservableCollection<LogEntry> _logSource;
    private int _selectedTabIndex;
    private bool _loadedOnce;
    private Task? _loadTask;

    public SettingsViewModel()
        : this(() => AppServices.Settings, s => s.Save(), Log.Entries)
    {
        RestartProgram = AppRestart.RestartNow;
    }

    /// <summary>Test seam: custom settings instance, save action and log source.</summary>
    internal SettingsViewModel(Func<AppSettings> settings, Action<AppSettings> save, ObservableCollection<LogEntry> logSource)
    {
        _settings = settings;
        _save = save;
        _logSource = logSource;
        _selectedLogFilter = LogFilter.Options[0];

        InitGeneralCommands();
        InitLogCommands();
        InitBackupCommands();
        InitQuarantineCommands();
        InitRestoreCommands();
        InitAboutCommands();
    }

    public override string Title => "Настройки";

    public override string Subtitle => "Параметры программы, журнал действий, резервные копии, карантин и восстановление системы";

    public override string IconGlyph => "";

    internal AppSettings Settings => _settings();

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (Set(ref _selectedTabIndex, value))
                OnTabChanged(value);
        }
    }

    public bool IsAdmin => Admin.IsElevated;

    public override Task OnNavigatedToAsync()
    {
        AttachLog();
        if (!_loadedOnce)
        {
            _loadedOnce = true;
            _loadTask = SafeLoadAllAsync(CancellationToken.None);
        }
        else
        {
            // Cheap refresh: settings may have been changed by other pages, new backups may have appeared.
            LoadGeneral();
            _ = SafeRefreshFilesAsync();
        }
        return Task.CompletedTask;
    }

    public override void OnNavigatedFrom() => DetachLog();

    /// <summary>Read-only: loads every tab's data and shows the "Основные" tab.</summary>
    public override async Task PreviewScanAsync(CancellationToken ct)
    {
        if (_loadTask is { IsCompleted: false })
            await _loadTask;
        await LoadAllAsync(ct);
        SelectedTabIndex = TabGeneral;
    }

    internal async Task LoadAllAsync(CancellationToken ct)
    {
        LoadGeneral();
        RebuildLog();
        await Task.WhenAll(LoadBackupsAsync(ct), LoadQuarantineAsync(ct), LoadRestoreInfoAsync(ct));
    }

    private async Task SafeLoadAllAsync(CancellationToken ct)
    {
        try
        {
            await LoadAllAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // page closed
        }
        catch (Exception ex)
        {
            Log.Error("Настройки: не удалось загрузить данные", ex);
        }
    }

    private async Task SafeRefreshFilesAsync()
    {
        try
        {
            await Task.WhenAll(LoadBackupsAsync(CancellationToken.None), LoadQuarantineAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            Log.Error("Настройки: не удалось обновить списки", ex);
        }
    }

    private void OnTabChanged(int tab)
    {
        if (!_loadedOnce)
            return;
        // Refresh the lists the user is about to look at (cheap file reads).
        switch (tab)
        {
            case TabBackups:
                _ = SafeRun(() => LoadBackupsAsync(CancellationToken.None));
                break;
            case TabQuarantine:
                _ = SafeRun(() => LoadQuarantineAsync(CancellationToken.None));
                break;
        }
    }

    private static async Task SafeRun(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Log.Error("Настройки: ошибка обновления", ex);
        }
    }

    /// <summary>Command parameter (single item, IList of selected items or null) → typed items.</summary>
    internal static List<T> ItemsFrom<T>(object? parameter) where T : class => parameter switch
    {
        T single => new List<T> { single },
        IEnumerable list => list.OfType<T>().ToList(),
        _ => new List<T>(),
    };
}
