using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
    public RelayCommand OpenSystemRestoreCommand { get; private set; } = null!;
    public RelayCommand OpenProtectionSettingsCommand { get; private set; } = null!;

    // ----- "О программе" -----

    public string AppName => AppInfo.Name;

    public string VersionText => $"Версия {AppInfo.Version}";

    public string VersionNumber => AppInfo.Version;

    public string RuntimeText => $".NET {Environment.Version} ({RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})";

    public string OsText => RuntimeInformation.OSDescription.Trim();

    public string AdminStatusText => Admin.IsElevated
        ? "Да — доступны все функции"
        : "Нет — ограниченный режим (системные папки, службы и HKLM только для чтения)";

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
        OpenSystemRestoreCommand = new RelayCommand(() =>
            LaunchSystemTool("rstrui.exe", "Восстановление системы"));
        OpenProtectionSettingsCommand = new RelayCommand(() =>
            LaunchSystemTool("SystemPropertiesProtection.exe", "Защита системы"));
    }

    private void InitAboutCommands()
    {
        OpenRepoCommand = new RelayCommand(() => ProcessRunner.ShellOpen(AppInfo.RepoUrl));
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.DataDir));
        RestartAsAdminCommand = new RelayCommand(() => Admin.RestartElevated(), () => !Admin.IsElevated);
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
        if (!Admin.RequireElevation("Создать точку восстановления"))
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

        var ok = false;
        await RunBusyAsync("Создание точки восстановления… Это может занять пару минут", async ct =>
        {
            ok = await RestorePoint.TryCreateAsync("Создана вручную в настройках", ct, force: true);
        });

        LastRestoreResult = ok
            ? $"Точка восстановления создана {Format.DateTime(DateTime.Now)}."
            : "Windows не создала точку восстановления. Подробности — во вкладке «Журнал».";
        try
        {
            await LoadRestoreInfoAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Не удалось обновить список точек восстановления: {ex.Message}");
        }

        Dialogs.Info("Точка восстановления", ok
            ? "Готово: точка восстановления создана. К ней можно вернуться через «Открыть восстановление системы»."
            : "Windows не создала точку восстановления.\n\nЧаще всего причина в том, что точка уже создавалась за последние 24 часа, " +
              "или защита системы выключена групповой политикой. Подробности — во вкладке «Журнал».");
    }

    private static string SystemDriveText()
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
        return string.IsNullOrEmpty(drive) ? "C:" : drive;
    }

    /// <summary>Starts a Windows tool that shows its own UAC prompt; cancelling the prompt is not an error.</summary>
    private static void LaunchSystemTool(string exe, string displayName)
    {
        var path = Path.Combine(Environment.SystemDirectory, exe);
        if (!File.Exists(path))
        {
            Dialogs.Info(displayName, $"Не найден системный компонент {exe}. Возможно, он отключён в этой редакции Windows.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            Log.Info($"Открыто: {displayName}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info($"{displayName}: запуск отменён пользователем");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Не удалось открыть {displayName}: {ex.Message}");
            Dialogs.Error(displayName, $"Не удалось открыть «{displayName}»: {ex.Message}");
        }
    }
}
