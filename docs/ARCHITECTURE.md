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
  Core/Updates/         автообновление: подписанный update.json релиза → скачивание → замена exe при запуске
  Modules/<Key>/        один раздел приложения = Service + ViewModel + View
tests/PcCleaner.Tests/  xUnit-тесты (Core/, Updates/ и <Key>/)
tools/                  make-icon.ps1 (иконка), make-update-manifest.ps1 (update.json + подпись в CI),
                        update-signing-public.pem (открытый ключ подписи обновлений)
```

Выпуск релиза: версия в `PcCleaner.csproj` + `docs/release-notes/<версия>.md`, затем тег `v<версия>` —
workflow `release.yml` соберёт exe, подпишет `update.json` ключом из секрета `UPDATE_SIGNING_KEY` и опубликует релиз.
Подробности — `docs/superpowers/specs/2026-10-17-auto-update-design.md`.

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
- `Dialogs.Confirm/Info/Error` — **каждое изменение системы подтверждается через `Dialogs.Confirm`** (значок
  предупреждения). `Dialogs.Ask` — нейтральный вопрос, после которого система не меняется («Открыть отчёт?»), со значком
  вопроса. В тестах и режиме скриншотов все подтверждения и вопросы автоматически = «Нет».
- `Log.Info/Warn/Error/Action` — `Action` для каждого изменения системы. Нет прав → `Warn`, не `Error`.
  `Log.Error(msg, ex)` показывает в журнале и статус-баре русское описание ошибки, тип и текст исключения — только в файле.
- `ErrorText.For(ex)` / `ErrorText.Sentence(ex)` — русский текст ошибки для пользователя (сообщения .NET — на английском).
  Не показывайте `ex.Message` в диалогах и результатах: используйте `ErrorText`.
- `Admin.IsElevated`, `Admin.RequireElevation("...")`, `Admin.RestartElevated()`. Перезапуск закрывает программу, поэтому
  `RestartElevated` (и `RequireElevation`) сначала спрашивает, если какая-то страница занята (`IsBusy`), а с обычной
  учётной записи — предупреждает, что программа запустится от имени администратора и будет работать с его профилем.
  `Admin.IsStandardUser`, `Admin.OtherAccountWarning` (программа запущена не от имени вошедшего пользователя — оболочка
  показывает предупреждение над страницей).
- `RegistryBackup.ExportAsync(@"HKCU\...")` — обязательная резервная копия перед изменением/удалением ключа
  (если вернула null — изменение отменяется). `RegistryBackup.OpenKey(...)`.
  `RegistryBackup.ImportCheckedAsync(file, verify)` — импорт .reg именно в том виде, в котором его проверили
  (снимок файла проверяется и импортируется; папку Backups может переписать любой процесс пользователя).
  `ImportAsync(file)` импортирует файл таким, каким он будет в момент импорта: если содержимое проверяли или показывали
  в подтверждении — только `ImportCheckedAsync` или собственная заблокированная копия.
- `RestorePoint.TryCreateAsync("...")` — перед пакетом системных изменений. Сама **не включает** защиту системы: если
  она выключена, точка не создаётся (результат `Failed`). Включает её только ручное действие в «Настройках»
  (`force: true`, это сказано в подтверждении), и это записывается через `Log.Action`.
- `FileSignature.Get(path)` — проверка Authenticode (встроенная и каталожная подписи), кэшируется. Файлы приложений
  Microsoft Store / MSIX в `%ProgramFiles%\WindowsApps\<PackageFullName>\` своей подписи не имеют: они считаются
  подписанными через пакет (`SignatureInfo.IsPackageSigned`, `PackagePublisher` — издатель из AppxManifest.xml), только
  если путь действительно внутри этой папки (без junction/symlink и путей устройств), имя папки совпадает с манифестом
  (имя, версия, хеш издателя), есть AppxSignature.p7x, а Windows зарегистрировала эту папку как подписанный пакет.
  `IsMicrosoft` у таких файлов определяется по издателю пакета (`O=Microsoft Corporation`), а не по отображаемому имени.
  Отображаемое имя с сертификатом не сверяется: если пакет не от Microsoft, а имя упоминает Microsoft в любом написании
  («Корпорация Майкрософт», кириллическая «М», невидимые символы), в `Publisher` показывается полное имя издателя.
- `Suspicion.Analyze(path, signature, commandLine)` / `Suspicion.AnalyzeProgram(...)` — эвристика с причинами.
  `KnownPups.Match(name, publisher)` — потенциально нежелательные программы.
- `CommandLine.Split(cmd)` / `CommandLine.TargetFile(cmd)` — разбор командных строк.
- `Quarantine.Add/Restore/Delete` — вместо удаления подозрительных файлов.
- `FileTools.Sha256Async`, `OpenVirusTotalAsync`, `SearchWeb`, `ScanWithDefenderAsync`, `SendToRecycleBin`, `IsReparsePoint`.
- `ProcessRunner.RunAsync(exe, args)` (кодировка OEM 866 учтена), `ProcessRunner.PowerShellAsync(script)` (UTF-8),
  `ShellOpen`, `ShowInExplorer` (`ProcessRunner.ExplorerPath` — полный путь к explorer.exe в папке Windows).
  С правами администратора скрипт PowerShell пишется в `%WINDIR%\Temp` с доступом только для SYSTEM и администраторов
  (`SecureTemp`), а не в `%LOCALAPPDATA%`, который может переписать любой процесс пользователя.
  Никогда не вставляйте в скрипт текст из реестра/файлов как код: передавайте его как данные (см. `RestorePoint.BuildScript`).
- `Wmi.Query(query, scope)` + расширения `Str/Long/Bool/Date`. Без прав/при ошибке → пустой список.
- `AppServices.Settings` (`AppSettings`, JSON в `%LOCALAPPDATA%\WaterCa7sPcCleaner`), `ModuleData` для состояния модулей:
  `GetModuleValue/SetModuleValue`; кто меняет `ModuleData` напрямую — только внутри `lock (settings)`, `Save()` — после него.
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
`FocusRing` — рамка фокуса клавиатуры: свой шаблон кнопки/плитки без триггера `IsKeyboardFocused` должен ставить
`FocusVisualStyle="{StaticResource FocusRing}"`, а не `{x:Null}` (иначе при переходе по Tab не видно, где фокус).
У кнопок только со значком задавайте `AutomationProperties.Name` (тот же текст, что в подсказке).
Конвертеры: `BoolToVis` (параметр `Invert`), `InverseBool`, `HasValueToVis`, `Bytes`, `SeverityToBrush`,
`SeverityToText` (значок важности: «Критично», «Важно», «Внимание», «Мелочь», «Совет»), `PercentToBrush`.

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
WaterCa7sPcCleaner.exe --screenshots <папка> [--page Cleaner,Startup] [--no-scan] [--size 1040x660] [--mode simple|expert] [--lang ru|en]
```
Окно открывается за пределами экрана, данные приложения берутся из временной папки (настоящие настройки,
журналы и резервные копии не затрагиваются), все диалоги отклоняются, для каждой страницы выполняется
`PreviewScanAsync` (только чтение), сохраняется `NN-Key.png` и `log.txt`. Код выхода 1 — если в журнале были ошибки.
`--mode` и `--lang` заменяют режим и язык из настроек только на этот запуск; в простом режиме снимаются четыре
страницы (Главная, Очистка, Программы, Настройки), номер `NN` в имени файла — место страницы в полном списке.

## Режимы (простой и опытный)

`AppSettings.UiMode` (`simple` / `expert`, пусто — ещё не выбран, показываются все страницы). Простой режим показывает в
боковой панели четыре страницы (`UiModes.SimplePageKeys`), остальные скрыты, но по-прежнему входят в проверку на Главной.
`MainViewModel.Pages` — все страницы, `VisiblePages` — страницы боковой панели. Любой переход на страницу идёт через
`MainViewModel.NavigateTo`: для скрытой страницы он спрашивает «Переключиться на опытный режим?». Режим меняется сразу
(`UiModes.Choose` → событие `UiModes.Changed` → боковая панель и `PageViewModel.OnModeChanged` у каждой страницы). Язык
читается один раз при запуске; «Перезапустить сейчас» в Настройках запускает программу заново с теми же аргументами
(`AppRestart`).
