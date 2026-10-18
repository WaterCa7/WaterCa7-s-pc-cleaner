# Simple / expert modes and English — Implementation Plan (Windows)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `docs/superpowers/specs/2026-10-18-modes-and-english-design.md` for the Windows app: a first-run screen (mode +
language), a simple mode (Главная, Очистка, Программы, Настройки; one-line texts) and an English UI. The Mac app follows
in its own plan with the same scheme.

**Architecture:** gettext-style localization — the Russian text itself is the key. `T._("Проверить снова")` returns the
English entry from an embedded `en.json` when the UI language is English, otherwise (or when the entry is missing) the
Russian text unchanged. This keeps ~5 700 existing strings and ~1 600 Russian test asserts working, lets the translation
land page by page, and a completeness test lists what is still untranslated. XAML uses `{loc:T 'Проверить снова'}`.
The language is chosen at start (settings → system) and changing it restarts the app; no live switching.

**Tech Stack:** C# / WPF, net7.0-windows, xUnit, `dotnet test tests\PcCleaner.Tests`.

**Start condition:** begin only after the bulk-actions work (`2026-10-18-fix-buttons-and-bulk-actions.md`) is in `main`;
then merge branch `startup-splash-admin` into `main` and work on a new branch from it.

## Global Constraints

- Russian stays the default language and the key; tests keep running in Russian (no culture set in tests unless a test
  is about English).
- `T._`/`T.F` argument must be a string literal (or a const) — the completeness test reads them from the source.
- Placeholders: `T.F("Найдено {0} из {1}", a, b)`; the English entry must use the same `{n}` set.
- English plural rule: one (n == 1) / other; `T.Plural(n, one, few, many)` → English uses `one` and `many` entries.
- Units and dates follow the UI language: ru — «Б, КБ, МБ…», dd.MM.yyyy; en — «B, KB, MB…», MMM d, yyyy.
- English texts: short, plain, sentence case, no Russian left; product name «WaterCa7s PC Cleaner» is not translated.
- Logs are written in the UI language (they show in the status bar and the log tab).
- Screenshot mode: `--mode simple|expert`, `--lang ru|en` (settings are redirected to a temp folder there already).
- Build 0 warnings; one commit per task (`git commit -F`, subject, blank line, body, Co-Authored-By trailer); no push.

---

### Task 1: Localization core

**Files:** create `src/PcCleaner/Core/Localization/T.cs`, `LocExtension.cs`, `Localization/en.json` (embedded resource),
modify `App.xaml.cs` (culture at start, `--lang`), `Core/AppSettings.cs` (`Language`: "ru" | "en" | null = system),
`Modules/Settings/SettingsLogic.cs` (sanitize), `Core/Format.cs` (culture-aware units/dates/plural);
tests `tests/PcCleaner.Tests/Core/LocalizationTests.cs`.

- `T.Language` ("ru" | "en"), `T.Use(string language)`, `T._(string ru)`, `T.F(string ru, params object?[] args)`,
  `T.Plural(long n, string one, string few, string many)`; `T.Culture` (ru-RU / en-US).
- `T.ResolveLanguage(string? setting, CultureInfo system)` → setting if valid, else "ru" for `ru*` system UI culture,
  else "en".
- `LocExtension : MarkupExtension` — `{loc:T 'текст'}` returns `T._(text)`; xmlns `loc` mapped in App.xaml / views.
- App start: right after `ParseArgs`: language = `--lang` ?? settings ?? system → `T.Use`, `CultureInfo.DefaultThreadCurrent(UI)Culture`,
  `FrameworkElement.LanguageProperty.OverrideMetadata(… XmlLanguage.GetLanguage(T.Culture.IetfLanguageTag))`.
- `Format`: `Units` by language, `Date`/`DateTime` by `T.Culture`, `Plural` delegates to `T.Plural`, `Format.Ru` stays
  for parsing and for callers that must stay Russian.
- Completeness test infrastructure: `LocalizationCatalogTests` scans `src/PcCleaner/**/*.cs` and `*.xaml` for
  `T._("…")`, `T.F("…"`, `T.Plural(…)` literals and `{loc:T '…'}`, and fails when a literal has no `en.json` entry
  or an entry's placeholders differ; also fails on `en.json` entries never used (stale).
- [ ] TDD: fallback to Russian; English lookup; `T.F` placeholders; plural ru/en; `ResolveLanguage` table;
  `LocExtension.ProvideValue`; `Format.Bytes`/`Date` in both languages; catalog test passes with an empty catalog.
- [ ] Commit "Localization: T, {loc:T}, language at start, culture-aware Format".

### Task 2: Modes, Settings options, navigation

**Files:** `Core/AppSettings.cs` (`UiMode`: Simple | Expert | null), `MainViewModel.cs` (`VisiblePages`), `MainWindow.xaml`
(sidebar binds `VisiblePages`), `Modules/Dashboard/DashboardViewModel.cs` (quick links by mode), `App.xaml.cs` (`--mode`),
Settings partial `SettingsViewModel.General.cs` + `SettingsView.xaml` (cards «Режим» and «Язык»), `Core/ScreenshotRunner.cs`
(iterate `VisiblePages`, keep file numbering from the index in `Pages`); tests `tests/PcCleaner.Tests/Core/ModesTests.cs`.

- Simple mode pages: Dashboard, Cleaner, Programs, Settings (in this order). Hidden pages still contribute to the
  dashboard analysis.
- `NavigateTo(key)` of a hidden page in simple mode asks: «Этот раздел есть в опытном режиме. Переключиться на
  опытный режим?» → Yes: switch mode (saved) and navigate; No: stay.
- Settings: «Режим» — two option buttons «Простой» / «Опытный» (applies at once); «Язык» — «Русский» / «English»
  with the hint «Язык изменится после перезапуска» and a «Перезапустить сейчас» button (restart with the same args,
  elevation as at start).
- [ ] TDD: VisiblePages per mode; NavigateTo hidden page asks (seam) and switches; quick links filtered; Settings saves
  mode/language; `--mode`/`--lang` parsed; screenshot runner skips hidden pages.
- [ ] Commit "Modes: simple and expert, Settings options for mode and language".

### Task 3: First-run welcome screen

**Files:** `MainWindow.xaml` (second overlay or reuse `SplashOverlay` area), new `Core/WelcomeViewModel.cs`,
`App.xaml.cs` (after the splash closes, show welcome when `UiMode == null`, never in screenshot mode unless `--welcome`);
tests `tests/PcCleaner.Tests/Core/WelcomeViewModelTests.cs`.

- Over the blurred window: logo, «Добро пожаловать в WaterCa7s PC Cleaner», two large cards: «Простой» — «Главное в
  одном месте: проверка, очистка и программы» and «Опытный» — «Все разделы, подробности и тонкие настройки»;
  RU / EN switch at the top right (changing it saves the language and restarts the app immediately, the welcome screen
  shows again because no mode is chosen yet). Choosing a card saves `UiMode` and closes the overlay.
- [ ] TDD: shown only when mode is null; choosing saves and closes; language switch saves and requests restart (seam);
  screenshot mode never shows it unless `--welcome` (for a screenshot of it).
- [ ] Commit "Welcome screen: choose simple or expert mode and the language".

### Task 4: Simple dashboard and one-line texts

**Files:** `Modules/Dashboard/DashboardView.xaml` (+ VM): in simple mode the main button is «Проверить и исправить»
(scan, then the usual «Исправить всё (N)» confirmation when there is something to fix; nothing → «Всё в порядке»),
issue rows show title + fix button + «Подробнее» (details, gain, module badge, per-module scan rows hidden);
page subtitles one line (`TextTrimming="CharacterEllipsis"`, full text in the tooltip) in simple mode for Cleaner and
Programs headers; tests in `tests/PcCleaner.Tests/Dashboard/SimpleModeTests.cs`.
- [ ] TDD → commit "Simple mode: one button to check and fix, short rows".

### Task 5: English — shell, splash, welcome, Settings, Dashboard

Wrap every user-visible Russian literal of `MainWindow.xaml`, `MainViewModel.cs`, `Core/Updates/*` (banner, splash),
`Core/WelcomeViewModel.cs`, `Modules/Settings/**`, `Modules/Dashboard/**`, `Core/HealthScoring`-style helpers in
`T._`/`T.F`/`T.Plural`/`{loc:T}`, add the English entries; the catalog test enforces these folders. Screenshots `--lang en`
of Dashboard and Settings in both modes. Commit "English: shell, Dashboard and Settings".

### Task 6: English — Cleaner and Programs (pages + services + catalogs)
Same method; catalog test enforces `Modules/Cleaner/**`, `Modules/Programs/**`. Commit "English: Cleaner and Programs".

### Task 7: English — issue texts of every module, HealthScoring, ErrorText, report
`AnalyzeAsync`/`BuildIssues` texts of all modules (they show on the Dashboard in both modes), `HealthScoring`,
`ErrorText` (English UI keeps .NET's own English message), `ReportBuilder` (`lang` attribute, file name, dates),
`Dialogs`, `Converters`. Commit "English: dashboard findings, errors and the report".

### Task 8: English — Processes and Startup
### Task 9: English — Disk and Optimizer
### Task 10: English — Network and Security
### Task 11: English — logs and the rest; the catalog test enforces all of `src/PcCleaner`
Each: same method, own commit, screenshots `--lang en` of the pages.

### Task 12: Verification (controller)
Full suite, Release build 0 warnings, screenshots of all pages: `--lang ru` and `--lang en`, `--mode simple` and
`--mode expert`; a short Russian report to the user; ask before pushing or releasing.
