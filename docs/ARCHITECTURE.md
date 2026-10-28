# Архитектура WaterCa7s PC Cleaner

WPF-приложение на C# (.NET 7). Установщик ставит самодостаточную сборку: .NET лежит в папке программы, и установленный
.NET на компьютере ей не нужен. `RollForward=LatestMajor` важен только для сборки из исходников — она запускается и на
.NET 8/9 Desktop Runtime.
Код на английском, весь интерфейс — на русском.

```
src/PcCleaner/
  App.xaml(.cs)         запуск, глобальные обработчики ошибок, режим --screenshots
  MainWindow.xaml       оболочка: боковое меню разделов, полоса вкладок, заголовок, полоса прогресса, нижняя строка с версией
  MainViewModel.cs      список страниц и навигация (INavigator)
  Themes/Theme.xaml     тёмная тема и стили всех стандартных контролов
  Core/                 общие сервисы (см. ниже)
  Core/Updates/         автообновление: подписанный update.json релиза → скачивание → запуск установщика при старте
  Core/ElevatedTask.cs  задача планировщика «запуск с правами без UAC» (Core/TaskStore.cs — COM Task Scheduler 2.0)
  Modules/<Key>/        один раздел приложения = Service + ViewModel + View
tests/PcCleaner.Tests/  xUnit-тесты (Core/, Updates/ и <Key>/)
installer/              WaterCa7sPcCleaner.iss (Inno Setup) и WaterCa7sPcCleaner.installed (маркер установленной копии)
tools/                  make-icon.ps1 (иконка), build-installer.ps1 (папка publish + ISCC), test-installer.ps1
                        (установка/обновление/удаление на раннере CI), make-update-manifest.ps1 (update.json + подпись в CI),
                        update-signing-public.pem (открытый ключ подписи обновлений)
```

Выпуск релиза: версия в `PcCleaner.csproj` + `docs/release-notes/<версия>.md`, затем тег `v<версия>` —
workflow `release.yml` соберёт установщик (`tools/build-installer.ps1`), проверит его на раннере (`tools/test-installer.ps1`:
установка, запуск, обновление, удаление), подпишет `update.json` ключом из секрета `UPDATE_SIGNING_KEY` и опубликует релиз
из трёх файлов: `WaterCa7sPcCleaner-Setup.exe`, `update.json`, `update.json.sig`. То же самое (без подписи и релиза) делает CI
на каждый push; его артефакт `WaterCa7sPcCleaner-Setup` — это установщик текущего коммита.
Подробности — `docs/superpowers/specs/2026-10-17-auto-update-design.md` и `docs/superpowers/specs/2026-10-23-installer-design.md`.

## Установка, запуск и обновления

**Установка.** Установщик (Inno Setup, `PrivilegesRequired=admin`, только x64) кладёт самодостаточную папку в
`%ProgramFiles%\WaterCa7s PC Cleaner` (страницы выбора папки нет, `DisableDirPage=yes`: сама обновляется только копия
в `Program Files`; `/DIR=` в командной строке остаётся для опытных), рядом с exe — файл-маркер `WaterCa7sPcCleaner.installed` (его пишет только установщик),
добавляет ярлыки и запись в «Программы и компоненты», затем запускает `WaterCa7sPcCleaner.exe --register-task`. Папка
программы наследует права `Program Files`: менять её могут только администраторы. Одиночный файл (single-file) не
используется: программа не распаковывает себя при старте, а `StartupHookSupport=false` в `PcCleaner.csproj` отключает
`DOTNET_STARTUP_HOOKS`. Деинсталлятор запускает `--unregister-tasks` (задачи всех пользователей) и спрашивает, удалять ли
настройки, журнал и карантин (в тихом режиме они остаются).

**Копия установлена** (`UpdateEnvironment.IsInstalledBuild`), если рядом с `WaterCa7sPcCleaner.exe` лежит маркер. Только такая
копия обновляется сама; сборка из исходников или переименованный exe — никогда: обновление заменило бы её релизной.

**Запуск без UAC.** Для каждого пользователя в папке планировщика `\WaterCa7s PC Cleaner` есть задача `Start <SID>`
(`ElevatedTask`): запуск `"<папка>\WaterCa7sPcCleaner.exe" --from-task`, `InteractiveToken`, `HighestAvailable`, обычный
приоритет, без остановки на батарее и без лимита времени. Права администратора Windows спрашивает один раз — при
регистрации задачи (установщик, или первый запуск с правами у другого администратора). Задача регистрируется только для exe
внутри `Program Files`, рядом с которым лежит маркер (`ElevatedTask.MayRegister`): задача на файл в папке, куда пользователь
может писать, отдала бы права администратора тому, кто подменит файл. Запущенная без прав копия (ярлык, меню «Пуск») сама запускает эту задачу и завершается, а копия с
`--from-task` ждёт завершения копии, которая её запустила. Переключатель в «Настройки → Основные» включает и
выключает задачу (`ElevatedTask` синхронизирует её с настройкой). Тесты не трогают настоящий планировщик:
`TestEnvironment.Init` подставляет фальшивое хранилище задач (`ITaskStore`).

**Запуск без UAC и его границы.** Задача планировщика с наивысшими правами — общий способ обойти запрос UAC, и у него есть
известная слабость: программа, которая уже работает под той же учётной записью администратора, может повлиять на запуск
через переменные окружения, которые читаются при старте процесса (например, переменные профилировщика CoreCLR из
`HKCU\Environment`). Microsoft не считает UAC границей безопасности для учётных записей администраторов, и пользователь
принял это ограничение. Закрыто: замена exe (задача создаётся только для `Program Files`, где менять файлы могут только
администраторы), `DOTNET_STARTUP_HOOKS` (`StartupHookSupport=false`), `DOTNET_ROOT` и распаковка single-file
(самодостаточная папка вместо одного файла). Обычные (не администраторские) учётные записи по-прежнему видят запрос UAC.

**Обновления.** `UpdateService` проверяет подписанный `update.json` (ключ платформы `win-x64-setup`, подпись проверяется
открытым ключом из `UpdateKeys`), скачивает `WaterCa7sPcCleaner-Setup.exe` в `%LOCALAPPDATA%\WaterCa7sPcCleaner\Updates` и
проверяет размер и SHA-256. Установка запускается при следующем старте, если автообновление включено, или сразу по кнопке
«Перезапустить сейчас»:
- Копия без маркера (сборка для разработки, переименованный exe) не проверяет и не ставит обновления никогда.
- Только копия, установленная в `Program Files`, ставит обновление сама. В любой другой папке и на обычной (не
  администраторской) учётной записи баннер отправляет скачать установщик вручную со страницы релиза, и обновление даже не
  скачивается: установщик работает от администратора, а из папки, доступной пользователю, файл или папку `Updates` можно
  подменить; на обычной учётной записи копия с правами работала бы в профиле администратора и не нашла бы скачанного.
- Установщик запускает только копия с правами администратора. Копия без прав (в `Program Files`, на учётной записи
  администратора) скачивает обновление и показывает кнопку «Перезапустить с правами и установить»
  (`UpdateService.NeedsElevation`): она перезапускает программу с аргументом `--install-update`, и запущенная с правами
  копия ставит проверенное обновление, даже если автообновление выключено.
- Проверенная копия установщика кладётся в `<папка программы>\Updates` (`UpdateInstaller.PrepareSetup`). Пока процесс из неё
  не создан, копия открыта с запретом записи, переименования и удаления (`PreparedSetup`), и её байты проверяются ещё раз
  через этот дескриптор.
- Установщик запускается как `/SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATEDFROM=<x.y.z>` (видно только окно с ходом
  установки, вопросов нет), ждёт закрытия старой копии (мьютекс `WaterCa7sPcCleaner.Instance`, до 30 с; не дождался —
  выходит, ничего не меняя), заменяет файлы, оставляет задачу и запускает новую версию с `--updated-from`. Если установка
  началась, но не дошла до конца (файлы заняты, антивирус, диск), Inno откатывает файлы, а `DeinitializeSetup` снова
  запускает прежнюю версию. Журнал установки остаётся в `%TEMP%\Setup Log*.txt` (`SetupLogging=yes`).
- Пока установщик работает, он держит мьютекс `WaterCa7sPcCleaner.Setup` (`SetupMutex`). Запуск программы в это время (ярлык,
  запуск новой версии самим установщиком, запуск в конце ручной установки) ждёт, пока установщик закончит (до 3 минут,
  `SetupInProgress`), и только потом занимает место единственной копии.
- Неудачная установка засчитывается до запуска установщика (`UpdateStore.RecordFailure`), а снимает счёт только новая версия:
  при старте она удаляет папку `Updates` рядом с exe, скачанные файлы и счётчик (`CompleteUpdateAsync`). Так установщик,
  который запустился и откатился, не запускается при каждом старте снова: после двух таких попыток программа открывается и
  предлагает скачать версию вручную.

## Модули

Строки — в порядке `MainViewModel.Pages` (он же порядок меню).

| Key       | Страница      | Раздел меню (`Key`)           | Сервис (IHealthContributor)                   |
|-----------|---------------|-------------------------------|-----------------------------------------------|
| Dashboard | Главная       | Главная (`Dashboard`)         | — (собирает все остальные)                    |
| Cleaner   | Очистка       | Очистка (`Cleaner`)           | CleanerService                                |
| Hardware  | Комплектующие | Компьютер (`Computer`)        | OptimizerService (через `OptimizerViewModel`) |
| Optimizer | Ускорение     | Компьютер (`Computer`)        | OptimizerService                              |
| Processes | Процессы      | Система (`System`)            | ProcessesService                              |
| Startup   | Автозагрузка  | Система (`System`)            | StartupService                                |
| Programs  | Программы     | Система (`System`)            | ProgramsService                               |
| Disk      | Диски         | Система (`System`)            | DiskService                                   |
| Security  | Безопасность  | Защита (`Protection`)         | SecurityService                               |
| Network   | Сеть          | Защита (`Protection`)         | NetworkService                                |
| Profile   | Профиль       | Профиль (`Profile`, внизу)    | — (заготовка)                                 |
| Settings  | Настройки     | Настройки (`Settings`, внизу) | —                                             |

Каждый сервис — синглтон в `Core/AppServices.cs`. Страница получает свой сервис через конструктор. «Комплектующие» —
отдельная страница над тем же `OptimizerViewModel`, что и «Ускорение» (одна загрузка на две вкладки): она показывает его
карточки железа, его полосу занятости (`MirrorsBusyState` — в предупреждениях перед перезапуском работа называется один
раз, «Ускорение») и его строку результата.

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

## Аккаунт

> **Аккаунт (`Core/Account/`, `Core/AccountWindows/`).** `ApiClient` — HTTP к `https://waterca7.ru/api/v1` (ошибки — `ApiException` с кодом `error`). `LicenseTicket.Verify` проверяет подпись билета (ECDSA P-256, DER) ключами `LicenseKeys.Live` (встроенный `license_pub.pem`), `LicenseState.Evaluate` даёт уровень Гость / Free / Pro, `Features` — таблица Pro-функций (копия `features.php` сайта; тест сверяет). `AccountService` — вход, выход, билет, устройства, промокод, оплата, обращение; хранит данные в `IAccountStore` (Windows — `DpapiAccountStore`, файл `account.bin` под DPAPI текущего пользователя), номер компьютера — `IHwHash` (Windows — `WmiHwHash`). Файлы `Core/Account/` копируются в Mac-версию без изменений; платформенные реализации — нет. Пока `license_pub.pem` нет в сборке, `LicenseKeys.Live` пуст и любой билет считается неподписанным (программа бесплатна).

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
WaterCa7sPcCleaner.exe --screenshots <папка> [--page Cleaner,System/Disk,Computer] [--no-scan] [--size 1040x660] [--mode simple|expert] [--lang ru|en] [--welcome]
```
Окно открывается за пределами экрана, данные приложения берутся из временной папки (настоящие настройки,
журналы и резервные копии не затрагиваются), все диалоги отклоняются, для каждой вкладки выполняется
`PreviewScanAsync` (только чтение) и сохраняется снимок, в конце — `log.txt`. Код выхода 1 — если в журнале были ошибки.
`--page` (через запятую) принимает ключ страницы (`Disk`), ключ раздела (`System` — все его видимые вкладки) или
`Раздел/Вкладка` (`System/Disk`); без `--page` снимается каждая видимая вкладка отдельно. Имя файла — `NN-Раздел-Вкладка.png`
(`08-System-Disk.png`), а для раздела из одной страницы с тем же ключом — `NN-Key.png` (`01-Dashboard.png`); в журнале снимок
называется так же (`System/Disk`). `--welcome` добавляет снимок первого экрана (`00-Welcome.png`).
`--mode` и `--lang` заменяют режим и язык из настроек только на этот запуск; в простом режиме снимаются пять
страниц (Главная, Очистка, Программы, Профиль, Настройки), номер `NN` в имени файла — место страницы в полном списке.

## Режимы (простой и опытный)

`AppSettings.UiMode` (`simple` / `expert`, пусто — ещё не выбран, показываются все страницы). Простой режим показывает пять
страниц (`UiModes.SimplePageKeys`: Главная, Очистка, Программы, Профиль, Настройки), остальные скрыты, но по-прежнему входят в
проверку на Главной. `MainViewModel.Pages` — все страницы, `VisiblePages` — страницы текущего режима (видимые вкладки разделов);
боковая панель показывает разделы (`TopSections`, `BottomSections`), у которых есть видимая вкладка. Любой переход на страницу идёт через
`MainViewModel.NavigateTo`: для скрытой страницы он спрашивает «Переключиться на опытный режим?». Режим меняется сразу
(`UiModes.Choose` → событие `UiModes.Changed` → боковая панель и `PageViewModel.OnModeChanged` у каждой страницы). Язык
читается один раз при запуске; «Перезапустить сейчас» в Настройках запускает программу заново с теми же аргументами
(`AppRestart`).

Боковое меню — разделы (`Core/SectionViewModel`, таблица в `Core/ShellMenu`). Раздел держит одну или несколько страниц-вкладок; полоса вкладок видна, когда в разделе больше одной видимой вкладки. Навигация идёт по ключу страницы (`INavigator.NavigateTo("Disk")` открывает «Система → Диски»). Простой режим прячет вкладки; раздел без видимых вкладок не показывается. Все страницы остаются в `MainViewModel.Pages`.

Главная в простом режиме (`DashboardViewModel.IsSimpleMode`): большая кнопка «Проверить и исправить» (`CheckAndFixCommand`:
проверка, затем обычный вопрос «Исправить всё (N)» — он не обходится никогда; нечего исправлять — «Всё в порядке»), строки
проблем из заголовка, кнопки исправления и «Подробнее» (`IssueItem`: страница простого режима открывается, для скрытой
описание раскрывается в строке), без строк по разделам и кнопок отчёта. `MainViewModel.IsSimpleMode` — подзаголовок
страницы в шапке в одну строку, полный текст в подсказке.
