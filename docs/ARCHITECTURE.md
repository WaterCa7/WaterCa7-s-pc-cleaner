# Архитектура WaterCa7s PC Cleaner

WPF-приложение на C# (.NET 7, `RollForward=LatestMajor` — запускается и на .NET 8/9 Desktop Runtime).
Код на английском, весь интерфейс — на русском.

```
src/PcCleaner/
  App.xaml(.cs)         запуск, глобальные обработчики ошибок, режим --screenshots
  MainWindow.xaml       оболочка: боковое меню, заголовок страницы, полоса прогресса, статус-бар
  MainViewModel.cs      список страниц и навигация (INavigator)
  Themes/Theme.xaml     тёмная тема и стили всех стандартных контролов
  Core/                 общие сервисы (см. ниже)
  Modules/<Key>/        один раздел приложения = Service + ViewModel + View
tests/PcCleaner.Tests/  xUnit-тесты (Core/ и <Key>/)
```

## Модули

| Key        | Раздел        | Сервис (IHealthContributor) |
|------------|---------------|-----------------------------|
| Dashboard  | Главная       | — (собирает все остальные)  |
| Cleaner    | Очистка       | CleanerService              |
| Processes  | Процессы      | ProcessesService            |
| Startup    | Автозагрузка  | StartupService              |
| Programs   | Программы     | ProgramsService             |
| Disk       | Диски         | DiskService                 |
| Optimizer  | Ускорение     | OptimizerService            |
| Security   | Безопасность  | SecurityService             |
| Settings   | Настройки     | —                           |

Каждый сервис — синглтон в `Core/AppServices.cs`. Страница получает свой сервис через конструктор.

## Core API (кратко)

- `PageViewModel` — база страницы: `Title`, `Subtitle`, `IconGlyph`, `Key`, `IsBusy/BusyText/Progress`,
  `RunBusyAsync(text, ct => ...)`, `ReportProgress(percent, text)`, `OnNavigatedToAsync()`, `OnNavigatedFrom()`,
  `PreviewScanAsync(ct)` (только чтение!). Оболочка сама показывает заголовок и полосу прогресса с кнопкой «Отмена».
- `HealthIssue` / `IHealthContributor.AnalyzeAsync(ct)` — находки для «Главной». Анализ только читает систему.
- `ObservableObject.Set(ref field, value)`, `RelayCommand`, `AsyncRelayCommand` (ловит исключения).
- `Dialogs.Confirm/Info/Error` — **каждое изменение системы подтверждается через `Dialogs.Confirm`**.
  В тестах и режиме скриншотов все подтверждения автоматически = «Нет».
- `Log.Info/Warn/Error/Action` — `Action` для каждого изменения системы. Нет прав → `Warn`, не `Error`.
- `Admin.IsElevated`, `Admin.RequireElevation("...")`, `Admin.RestartElevated()`.
- `RegistryBackup.ExportAsync(@"HKCU\...")` — обязательная резервная копия перед изменением/удалением ключа
  (если вернула null — изменение отменяется). `RegistryBackup.OpenKey(...)`.
- `RestorePoint.TryCreateAsync("...")` — перед пакетом системных изменений.
- `FileSignature.Get(path)` — проверка Authenticode (встроенная и каталожная подписи), кэшируется.
- `Suspicion.Analyze(path, signature, commandLine)` / `Suspicion.AnalyzeProgram(...)` — эвристика с причинами.
  `KnownPups.Match(name, publisher)` — потенциально нежелательные программы.
- `CommandLine.Split(cmd)` / `CommandLine.TargetFile(cmd)` — разбор командных строк.
- `Quarantine.Add/Restore/Delete` — вместо удаления подозрительных файлов.
- `FileTools.Sha256Async`, `OpenVirusTotalAsync`, `SearchWeb`, `ScanWithDefenderAsync`, `SendToRecycleBin`, `IsReparsePoint`.
- `ProcessRunner.RunAsync(exe, args)` (кодировка OEM 866 учтена), `ProcessRunner.PowerShellAsync(script)` (UTF-8),
  `ShellOpen`, `ShowInExplorer`.
- `Wmi.Query(query, scope)` + расширения `Str/Long/Bool/Date`. Без прав/при ошибке → пустой список.
- `AppServices.Settings` (`AppSettings`, JSON в `%LOCALAPPDATA%\WaterCa7sPcCleaner`), `ModuleData` для состояния модулей.
- `Ui.Post/Invoke` — выполнение в UI-потоке (в тестах — напрямую).
- `Format.Bytes/Percent/Number/Duration/Date/Plural/Count` — русское форматирование.
- `SystemStats.CpuUsagePercent()`, `SystemStats.Memory()`, `SystemStats.Uptime` — живые метрики без WMI.
- `AppServices.Settings.AddTrusted/RemoveTrusted/IsTrusted` — список доверенных (потокобезопасно, не менять `TrustedItems` напрямую).

## Тема (ключи ресурсов)

Кисти: `BackgroundBrush SidebarBrush SurfaceBrush Surface2Brush Surface3Brush LineBrush TextBrush MutedBrush
AccentBrush AccentHoverBrush AccentTextBrush AccentSoftBrush DangerBrush DangerSoftBrush WarningBrush WarningSoftBrush
SuccessBrush InfoBrush InfoSoftBrush HoverBrush SelectionBrush`.
Шрифты: `UiFont`, `IconFont` (Segoe Fluent Icons / MDL2).
Стили: текст `H1 H2 H3 Muted BigNumber Icon`; контейнеры `Card InnerCard Badge`; кнопки (по умолчанию) +
`AccentButton DangerButton GhostButton LinkButton`; `ToggleSwitch` (для CheckBox). Для DataGrid, ListBox, TabControl,
ComboBox, TextBox, CheckBox, RadioButton, ProgressBar, ScrollBar, ToolTip, ContextMenu стили применяются автоматически.
Конвертеры: `BoolToVis` (параметр `Invert`), `InverseBool`, `HasValueToVis`, `Bytes`, `SeverityToBrush`,
`SeverityToText`, `PercentToBrush`.

## Правила безопасности

1. Ничего не удаляется и не меняется без явного подтверждения пользователя со списком того, что будет сделано.
2. Очистка удаляет только внутри разрешённых корней категорий, **никогда не проходит по junction/symlink**
   (`FileTools.IsReparsePoint`), не трогает корень категории, пропускает занятые файлы.
3. Реестр — только после `RegistryBackup.ExportAsync`. Автозагрузка отключается обратимо (как в Диспетчере задач).
4. Программы удаляются только их собственным деинсталлятором. Подозрительные файлы — в карантин, не удаление.
5. Критические системные процессы завершить нельзя.
6. Твики оптимизации обратимы: прежнее значение сохраняется в `AppSettings.ModuleData`.
7. Эвристика только подсказывает. Автоматически ничего «подозрительного» не удаляется.

## Режим проверки (скриншоты)

```
WaterCa7sPcCleaner.exe --screenshots <папка> [--page Cleaner,Startup] [--no-scan] [--size 1040x660]
```
Окно открывается за пределами экрана, данные приложения берутся из временной папки (настоящие настройки,
журналы и резервные копии не затрагиваются), все диалоги отклоняются, для каждой страницы выполняется
`PreviewScanAsync` (только чтение), сохраняется `NN-Key.png` и `log.txt`. Код выхода 1 — если в журнале были ошибки.
