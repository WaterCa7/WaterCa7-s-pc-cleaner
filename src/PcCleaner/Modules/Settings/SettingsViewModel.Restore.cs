using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using PcCleaner.Core;

namespace PcCleaner.Modules.Settings;

// Tabs "Восстановление системы" and "О программе".
public sealed partial class SettingsViewModel
{
    private string _restoreFrequencyText = "";
    private string _protectionText = "Проверяется…";
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
        ? "Точек восстановления не найдено. Возможно, защита системы выключена."
        : "Список точек восстановления Windows показывает только администратору. Запустите программу от имени администратора, чтобы увидеть его.";

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

    public string VersionText => $"Версия {AppInfo.Version}";

    public string VersionNumber => AppInfo.Version;

    public string RuntimeText => $".NET {Environment.Version} ({RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})";

    public string OsText => RuntimeInformation.OSDescription.Trim();

    public string AdminStatusText => Admin.IsElevated
        ? "Да — доступны все функции"
        : "Нет — ограниченный режим: системные папки, службы и общие разделы реестра доступны только для чтения";

    public string DataDir => AppPaths.DataDir;

    public string RepoUrl => AppInfo.RepoUrl;

    public IReadOnlyList<string> SafetyLines { get; } = new[]
    {
        "Ничего не удаляется без вашего подтверждения — перед каждым действием вы видите, что именно будет сделано.",
        "Изменения реестра сохраняются в резервные копии — их можно вернуть во вкладке «Резервные копии».",
        "Подозрительные файлы отправляются в карантин, а не удаляются — их можно восстановить.",
        "Перед крупными изменениями создаётся точка восстановления Windows (если это включено в настройках).",
        "Все действия программы записываются в журнал.",
    };

    public RelayCommand OpenRepoCommand { get; private set; } = null!;
    public RelayCommand OpenDataFolderCommand { get; private set; } = null!;
    public RelayCommand RestartAsAdminCommand { get; private set; } = null!;

    private void InitRestoreCommands()
    {
        CreateRestorePointCommand = new AsyncRelayCommand(CreateRestorePointAsync, () => !IsBusy);
        OpenSystemRestoreCommand = new AsyncRelayCommand(() =>
            LaunchSystemToolAsync("rstrui.exe", "Восстановление системы"));
        OpenProtectionSettingsCommand = new AsyncRelayCommand(() =>
            LaunchSystemToolAsync("SystemPropertiesProtection.exe", "Защита системы"));
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

        RestoreFrequencyText = "Windows создаёт точки " + SystemRestoreInfo.FrequencyText(status.FrequencyMinutes) +
                               (status.FrequencyFromRegistry ? " (изменено в настройках системы)" : " (стандартная настройка)") +
                               ". Если точка уже создавалась недавно, Windows молча пропустит новую.";
        ProtectionText = status.ProtectionOn switch
        {
            true => "Защита системы включена",
            false => "Защита системы, похоже, выключена",
            null => "Состояние защиты системы неизвестно",
        };
        RestorePolicyWarning = status.DisabledByPolicy
            ? "Восстановление системы отключено групповой политикой — точки создать не получится."
            : null;
        RestorePoints = new ObservableCollection<RestorePointInfo>(points.Take(8));
        RestorePointsLoaded = true;
        OnPropertyChanged(nameof(RestorePointsHint));
    }

    private async Task CreateRestorePointAsync()
    {
        if (!ElevatedRestart.RequireElevation("Создать точку восстановления"))
            return;

        var message =
            "Создать точку восстановления Windows сейчас?\n\n" +
            "• Windows запишет текущее состояние системных файлов, драйверов и реестра — это займёт до пары минут;\n" +
            "• ваши документы, фото и другие личные файлы точка не затрагивает;\n" +
            "• если защита системы для диска " + SystemDriveText() + " выключена, программа включит её — " +
            "под точки восстановления Windows отведёт часть места на диске;\n" +
            "• по умолчанию Windows создаёт не больше одной точки в 24 часа — если точка уже была недавно, новая может не появиться.\n\n" +
            "Ненужные точки можно удалить в «Защите системы» (кнопка «Настроить защиту»).";
        if (!Dialogs.Confirm("Точка восстановления", message))
            return;

        RestorePointResult? outcome = null;
        var cancelled = false;
        await RunBusyAsync("Создание точки восстановления… Это может занять пару минут", async ct =>
        {
            try
            {
                // force: the switch on the «Основные» tab is about automatic points; this one the user asked for explicitly.
                outcome = await RestorePoint.CreateAsync("Создана вручную в настройках", ct, force: true);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
        });

        if (cancelled || outcome is null)
        {
            SetRestoreResult(RestoreResultKind.Warning, "Создание точки восстановления прервано.");
            if (cancelled)
                Dialogs.Info("Точка восстановления",
                    "Создание точки восстановления прервано. Если Windows успела её создать, она появится в списке справа " +
                    "и в мастере «Восстановление системы».");
            _ = SafeRun(() => LoadRestoreInfoAsync(CancellationToken.None));
            return;
        }

        try
        {
            await LoadRestoreInfoAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Не удалось обновить список точек восстановления: {ex.Message}");
        }

        var text = RestorePointText.Describe(outcome.Value, DateTime.Now, RestorePolicyWarning is not null);
        SetRestoreResult(text.Kind, text.Status);
        Dialogs.Info("Точка восстановления", text.Details);
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
                Log.Info($"Открыто: {displayName}");
                await Task.Delay(ToolLaunchCooldown);
                break;
            case ToolLaunchResult.AlreadyOpen:
                Log.Info($"{displayName}: окно уже открыто — второе не запускается");
                Dialogs.Info(displayName, $"Окно «{displayName}» уже открыто — переключитесь на него через панель задач.");
                break;
            case ToolLaunchResult.Cancelled:
                Log.Info($"{displayName}: запуск отменён пользователем");
                break;
            case ToolLaunchResult.Missing:
                Dialogs.Info(displayName, $"Не найден системный компонент {exe}. Возможно, он отключён в этой редакции Windows.");
                break;
            default:
                Dialogs.Error(displayName, $"Не удалось открыть «{displayName}». Подробности — во вкладке «Журнал».");
                break;
        }
    }
}
