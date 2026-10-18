# Экран загрузки с обновлением и запуск с правами администратора

Дата: 2026-10-18. Статус: решения приняты пользователем, реализация в ветке `startup-splash-admin`.

## Решения пользователя

- При открытии программы всё размыто, по центру логотип. В это время программа проверяет версию и, если есть новая,
  скачивает её и **сразу перезапускается** в новой версии. Кнопка «Пропустить» открывает программу сразу, а обновление
  ставится при следующем запуске.
- Программа **всегда работает с правами администратора**: при запуске она перезапускает себя с правами (Windows
  спрашивает разрешение). Если в окне Windows нажать «Нет», программа открывается в ограниченном режиме, как сейчас.
  В «Настройках» это можно выключить.

## 1. Экран загрузки

- `MainWindow.xaml`: существующий `Grid x:Name="Root"` оборачивается во внешний `Grid x:Name="Shell"`; рядом с `Root`
  лежит `Grid x:Name="SplashOverlay"` (над ним). Пока заставка видна, у `Root` эффект `BlurEffect` (радиус 18), оверлей —
  полупрозрачный фон `BackgroundBrush` (непрозрачность 0.35), по центру: логотип 120×120 (тот же рисунок, что в шапке
  боковой панели — вынести его в ресурс `AppLogo` в `Themes/Theme.xaml` и использовать в обоих местах), «WaterCa7s PC
  Cleaner», строка состояния, полоска прогресса и кнопка «Пропустить» (только во время скачивания). Исчезновение —
  плавное (200–300 мс), размытие снимается.
- `SplashViewModel` (`src/PcCleaner/Core/Updates/SplashViewModel.cs`), свойство `MainViewModel.Splash`:
  `IsVisible`, `StatusText`, `ShowProgress`, `Progress` (0..1), `CanSkip`, `SkipCommand`,
  `Task RunAsync(CancellationToken ct)`. Зависимости через конструктор/свойства: `UpdateService`,
  `Func<bool> installNow` (по умолчанию `() => updates.InstallNow(args)`), `Action shutdown`,
  `TimeSpan MinimumVisible` (700 мс), `TimeSpan CheckTimeout` (5 с — только на проверку версии).
  1. `IsVisible = true`, «Проверяем обновления…».
  2. Если `!updates.CanUpdate` или автообновление выключено — закрыть после `MinimumVisible`.
  3. `updates.CheckAsync(manual: false, ct)`. Если за `CheckTimeout` проверка не перешла в `Downloading`/`Ready` —
     закрыть заставку (проверка продолжается в фоне, как сейчас).
  4. `Downloading`: «Скачиваем версию {X}…», `ShowProgress`, `Progress` из `updates.Progress`, `CanSkip = true`.
     «Пропустить» закрывает заставку; скачивание идёт дальше, установка — при следующем запуске (как сейчас).
  5. `Ready` и не пропущено: «Устанавливаем обновление…», `installNow()`; `true` → `shutdown()`; `false` →
     закрыть заставку (полоса обновления в окне покажет, что делать).
  6. `UpToDate`, `Failed`, `ManualRequired`, `None` — закрыть.
- `App.xaml.cs`: вместо фонового `CheckAsync` после `window.Show()` (кроме режима `--updated-from`) —
  `vm.Splash.RunAsync(...)`. В режиме `--screenshots` заставка не запускается и никогда не видна (скриншоты снимают
  `window.Content`).
- Тесты `tests/PcCleaner.Tests/Updates/SplashViewModelTests.cs` на подставных `UpdateEnvironment`/`FakeHttp` из
  `UpdateTestKit`: актуальная версия → закрыта, `installNow` не вызван; новая версия → скачана → `installNow` и
  `shutdown` вызваны; «Пропустить» во время скачивания → закрыта, `shutdown` не вызван; нет сети / таймаут проверки →
  закрыта; сборка разработчика (`CanUpdate == false`) → закрыта.

## 2. Всегда с правами администратора

- `AppSettings.RunAsAdmin` (по умолчанию `true`); в «Настройках» переключатель «Запускать с правами администратора»
  с пояснением «Windows будет спрашивать разрешение при каждом запуске; без прав часть проверок и исправлений
  недоступна».
- `Core/StartupElevation.cs`: `bool ShouldElevate(bool isElevated, bool runAsAdmin, bool screenshotMode, IReadOnlyList<string> args)`
  — `false`, если уже с правами, настройка выключена, режим скриншотов или в аргументах `--elevation-declined` /
  `--updated-from`; `bool TryRelaunchElevated(IReadOnlyList<string> args, Func<ProcessStartInfo, bool> start)` —
  запускает тот же exe с `Verb = "runas"` и теми же аргументами; отказ в UAC (Win32Exception 1223) → `false`.
- `App.OnStartup`: сразу после разбора аргументов и до установки скачанного обновления: если `ShouldElevate` →
  `TryRelaunchElevated` → `true`: `Shutdown(); return;` → `false`: продолжить без прав и записать в журнал.
- Обновление: новый exe запускает уже повышенный процесс, поэтому он стартует с правами без второго вопроса
  (`--updated-from` не повышается заново).
- Тесты `tests/PcCleaner.Tests/Core/StartupElevationTests.cs`: таблица `ShouldElevate`; перезапуск передаёт
  `runas` и те же аргументы; отказ UAC → `false`; любая другая ошибка → `false` и запись в журнал.

## Проверка

Полный `dotnet test`, `dotnet build -c Release` без предупреждений, скриншоты `--screenshots` (заставки на них нет).
Сама заставка и запрос UAC вживую проверяются пользователем после сборки.
