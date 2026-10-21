using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Settings;

// Tabs "Восстановление системы" and "О программе".
public sealed partial class SettingsViewModel
{
    private string _restoreFrequencyText = "";
    private string _protectionText = T._("Проверяется…");
    private string? _restorePolicyWarning;
    private ObservableCollection<RestorePointInfo> _restorePoints = new();
    private string? _lastRestoreResult;
    private bool _restorePointsLoaded;

    public string RestoreFrequencyText
    {
        get => _restoreFrequencyText;
        private set => Set(ref _restoreFrequencyText, value);
    }

    public string ProtectionText
    {
        get => _protectionText;
        private set => Set(ref _protectionText, value);
    }

    public string? RestorePolicyWarning
    {
        get => _restorePolicyWarning;
        private set => Set(ref _restorePolicyWarning, value);
    }

    public ObservableCollection<RestorePointInfo> RestorePoints
    {
        get => _restorePoints;
        private set => Set(ref _restorePoints, value);
    }

    public bool RestorePointsLoaded
    {
        get => _restorePointsLoaded;
        private set => Set(ref _restorePointsLoaded, value);
    }

    public string RestorePointsHint => Admin.IsElevated
        ? T._("Точек восстановления не найдено. Возможно, защита системы выключена.")
        : T._("Список точек восстановления Windows показывает только администратору. Запустите программу от имени администратора, чтобы увидеть его.");

    public string? LastRestoreResult
    {
        get => _lastRestoreResult;
        private set => Set(ref _lastRestoreResult, value);
    }

    public AsyncRelayCommand CreateRestorePointCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenSystemRestoreCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenProtectionSettingsCommand { get; private set; } = null!;

    /// <summary>Starts «Восстановление системы» / «Защита системы» (tests replace it: nothing real is started).</summary>
    internal SystemToolLauncher ToolLauncher { get; set; } = new();

    /// <summary>
    /// How long a tool button stays disabled after it started the tool: the window (and its UAC prompt) takes a moment
    /// to appear, and a double click must not open a second one.
    /// </summary>
    internal TimeSpan ToolLaunchCooldown { get; set; } = TimeSpan.FromSeconds(3);

    // ----- "О программе" -----

    public string AppName => AppInfo.Name;

    public string VersionText => T.F("Версия {0}", AppInfo.Version);

    public string VersionNumber => AppInfo.Version;

    public string RuntimeText => $".NET {Environment.Version} ({RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})";

    public string OsText => RuntimeInformation.OSDescription.Trim();

    public string AdminStatusText => Admin.IsElevated
        ? T._("Да — доступны все функции")
        : T._("Нет — ограниченный режим: системные папки, службы и общие разделы реестра доступны только для чтения");

    public string DataDir => AppPaths.DataDir;

    public string RepoUrl => AppInfo.RepoUrl;

    public IReadOnlyList<string> SafetyLines { get; } = new[]
    {
        T._("Ничего не удаляется без вашего подтверждения — перед каждым действием вы видите, что именно будет сделано."),
        T._("Изменения реестра сохраняются в резервные копии — их можно вернуть во вкладке «Резервные копии»."),
        T._("Подозрительные файлы отправляются в карантин, а не удаляются — их можно восстановить."),
        T._("Перед крупными изменениями создаётся точка восстановления Windows (если это включено в настройках)."),
        T._("Все действия программы записываются в журнал."),
    };

    public RelayCommand OpenRepoCommand { get; private set; } = null!;
    public RelayCommand OpenDataFolderCommand { get; private set; } = null!;
    public RelayCommand RestartAsAdminCommand { get; private set; } = null!;

    private void InitRestoreCommands()
    {
        CreateRestorePointCommand = new AsyncRelayCommand(CreateRestorePointAsync, () => !IsBusy);
        OpenSystemRestoreCommand = new AsyncRelayCommand(() =>
            LaunchSystemToolAsync("rstrui.exe", T._("Восстановление системы")));
        OpenProtectionSettingsCommand = new AsyncRelayCommand(() =>
            LaunchSystemToolAsync("SystemPropertiesProtection.exe", T._("Защита системы")));
    }

    private void InitAboutCommands()
    {
        OpenRepoCommand = new RelayCommand(() => ProcessRunner.ShellOpen(AppInfo.RepoUrl));
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.DataDir));
        RestartAsAdminCommand = new RelayCommand(() => ElevatedRestart.Request(), () => !Admin.IsElevated);
    }

    internal async Task LoadRestoreInfoAsync(CancellationToken ct)
    {
        var (status, points) = await Task.Run(() =>
        {
            var s = SystemRestoreInfo.ReadStatus();
            ct.ThrowIfCancellationRequested();
            var p = SystemRestoreInfo.ListPoints();
            return (s, p);
        }, ct);

        var frequency = SystemRestoreInfo.FrequencyText(status.FrequencyMinutes);
        RestoreFrequencyText = status.FrequencyFromRegistry
            ? T.F("Windows создаёт точки {0} (изменено в настройках системы). Если точка уже создавалась недавно, Windows молча пропустит новую.", frequency)
            : T.F("Windows создаёт точки {0} (стандартная настройка). Если точка уже создавалась недавно, Windows молча пропустит новую.", frequency);
        ProtectionText = status.ProtectionOn switch
        {
            true => T._("Защита системы включена"),
            false => T._("Защита системы, похоже, выключена"),
            null => T._("Состояние защиты системы неизвестно"),
        };
        RestorePolicyWarning = status.DisabledByPolicy
            ? T._("Восстановление системы отключено групповой политикой — точки создать не получится.")
            : null;
        RestorePoints = new ObservableCollection<RestorePointInfo>(points.Take(8));
        RestorePointsLoaded = true;
        OnPropertyChanged(nameof(RestorePointsHint));
    }

    private async Task CreateRestorePointAsync()
    {
        if (!ElevatedRestart.RequireElevation(T._("Создать точку восстановления")))
            return;

        var message = T.F("Создать точку восстановления Windows сейчас?\n\n• Windows запишет текущее состояние системных файлов, драйверов и реестра — это займёт до пары минут;\n• ваши документы, фото и другие личные файлы точка не затрагивает;\n• если защита системы для диска {0} выключена, программа включит её — под точки восстановления Windows отведёт часть места на диске;\n• по умолчанию Windows создаёт не больше одной точки в 24 часа — если точка уже была недавно, новая может не появиться.\n\nНенужные точки можно удалить в «Защите системы» (кнопка «Настроить защиту»).", SystemDriveText());
        if (!Dialogs.Confirm(T._("Точка восстановления"), message))
            return;

        RestorePointResult? outcome = null;
        var cancelled = false;
        await RunBusyAsync(T._("Создание точки восстановления… Это может занять пару минут"), async ct =>
        {
            try
            {
                // force: the switch on the «Основные» tab is about automatic points; this one the user asked for explicitly.
                outcome = await RestorePoint.CreateAsync(T._("Создана вручную в настройках"), ct, force: true);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
        });

        if (cancelled || outcome is null)
        {
            SetRestoreResult(RestoreResultKind.Warning, T._("Создание точки восстановления прервано."));
            if (cancelled)
                Dialogs.Info(T._("Точка восстановления"),
                    T._("Создание точки восстановления прервано. Если Windows успела её создать, она появится в списке справа и в мастере «Восстановление системы»."));
            _ = SafeRun(() => LoadRestoreInfoAsync(CancellationToken.None));
            return;
        }

        try
        {
            await LoadRestoreInfoAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn(T.F("Не удалось обновить список точек восстановления: {0}", ErrorText.For(ex)), ex);
        }

        var text = RestorePointText.Describe(outcome.Value, DateTime.Now, RestorePolicyWarning is not null);
        SetRestoreResult(text.Kind, text.Status);
        Dialogs.Info(T._("Точка восстановления"), text.Details);
    }

    private void SetRestoreResult(RestoreResultKind kind, string text)
    {
        LastRestoreResultKind = kind;
        LastRestoreResult = text;
    }

    /// <summary>How the last result line is colored: green (created), blue (Windows skipped it), amber (not created).</summary>
    public RestoreResultKind LastRestoreResultKind
    {
        get => _lastRestoreResultKind;
        private set => Set(ref _lastRestoreResultKind, value);
    }

    private RestoreResultKind _lastRestoreResultKind;

    private static string SystemDriveText()
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
        return string.IsNullOrEmpty(drive) ? "C:" : drive;
    }

    /// <summary>
    /// Starts a Windows tool that shows its own UAC prompt — once: not again while its window is open, and not twice on a
    /// double click (the command stays disabled for a moment). Cancelling the UAC prompt is not an error.
    /// </summary>
    private async Task LaunchSystemToolAsync(string exe, string displayName)
    {
        var launcher = ToolLauncher;
        // Off the UI thread: Process.Start waits for the UAC prompt of an elevated tool.
        var result = await Task.Run(() => launcher.Launch(exe));
        switch (result)
        {
            case ToolLaunchResult.Started:
                Log.Info(T.F("Открыто: {0}", displayName));
                await Task.Delay(ToolLaunchCooldown);
                break;
            case ToolLaunchResult.AlreadyOpen:
                Log.Info(T.F("{0}: окно уже открыто — второе не запускается", displayName));
                Dialogs.Info(displayName, T.F("Окно «{0}» уже открыто — переключитесь на него через панель задач.", displayName));
                break;
            case ToolLaunchResult.Cancelled:
                Log.Info(T.F("{0}: запуск отменён пользователем", displayName));
                break;
            case ToolLaunchResult.Missing:
                Dialogs.Info(displayName, T.F("Не найден системный компонент {0}. Возможно, он отключён в этой редакции Windows.", exe));
                break;
            default:
                Dialogs.Error(displayName, T.F("Не удалось открыть «{0}». Подробности — во вкладке «Журнал».", displayName));
                break;
        }
    }
}
