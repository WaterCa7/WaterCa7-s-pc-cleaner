# Fix buttons on the Dashboard and bulk actions — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement `docs/superpowers/specs/2026-10-18-fix-buttons-and-bulk-actions-design.md`: more "Исправить" /
"Открыть настройки" buttons and "Исправить всё (N)" on Главная; checkboxes with one bulk action on Процессы,
Автозагрузка and Программы.

**Architecture:** `HealthIssue` gets `FixLabel`, `InFixAll`, `SettingsUri`; each module's `AnalyzeAsync` attaches
fixes built from service methods that already exist. A shared `CheckedSet<TKey>` keeps checkbox state across list
rebuilds; a shared `BulkActionBar` control draws "Отмечено: N · [actions] · [Снять отметки]"; each page VM adds
`IsChecked`/`CanCheck` to its rows and one bulk command per action.

**Tech Stack:** C# / WPF, net7.0-windows, xUnit (no mocking library), `dotnet test tests\PcCleaner.Tests`.

## Global Constraints

- UI text is Russian; code, comments and commit messages are English. Match the surrounding code style.
- Analysis (`AnalyzeAsync`) MUST NOT change the system; only `FixAsync` may.
- Every new `FixAsync` re-checks the current system state before changing anything (scan data may be stale).
- Failure results use wording `HealthScoring.LooksLikeFailure` recognises ("Не удалось…", "Нужны права
  администратора…", "ничего не изменено"); admin-only results use `HealthScoring.LooksLikeAdminRequired` wording.
- Tests never touch the real system: use the existing sandboxes/fakes (`StartupSandbox`, `SecuritySandbox`,
  `CleanerEnvironment.ForTests`, `ProcessFakes`, `ProgramsService` test constructor, `TestEnvironment.Init()`).
  Never kill real processes other than ones the test started itself, never run real uninstallers, never reboot.
- Dialogs: `TestEnvironment.Init()` makes every confirmation answer "No". To answer "Yes" use a per-VM
  `internal Func<string, string, bool> Confirm { get; set; } = Dialogs.Confirm;` seam (as `CleanerViewModel.cs:492`),
  not the global handler.
- Build must stay at 0 warnings. Full suite baseline: 2284 passed, 1 skipped.
- Commits: one per task, only the task's files, `git commit -F <file>`; message = English subject line, blank line,
  body, blank line, `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push.
- Build/test commands (PowerShell, repo root `A:\WaterCa7s-pc-cleaner`):
  - one class: `dotnet test tests\PcCleaner.Tests --filter "FullyQualifiedName~<ClassName>" --nologo`
  - full: `dotnet test tests\PcCleaner.Tests --nologo`

---

### Task 1: HealthIssue model, issue row buttons and "Исправить всё (N)"

**Files:**
- Modify: `src/PcCleaner/Core/Health.cs` (HealthIssue)
- Modify: `src/PcCleaner/Modules/Dashboard/DashboardItems.cs:84-173` (IssueItem)
- Modify: `src/PcCleaner/Modules/Dashboard/DashboardViewModel.cs` (commands ~83-90, counters ~391-395, ~734-736,
  `FixSafeAsync` ~1090-1213, item wiring where `FixCommand`/`OpenPageCommand` are assigned)
- Modify: `src/PcCleaner/Modules/Dashboard/DashboardView.xaml` (row template ~149-269, batch button ~589-596)
- Test: `tests/PcCleaner.Tests/Dashboard/*` (existing tests that use `FixSafeAsync`/`FixSafeText`/`SafeFixCount`)

**Interfaces — Produces:**
```csharp
// HealthIssue (Core/Health.cs)
/// <summary>Text of the fix button: "Исправить", or what exactly happens ("Перезагрузить", "Удалить программу").</summary>
public string FixLabel { get; init; } = "Исправить";
/// <summary>False for fixes that open their own windows, restart the PC or remove programs: they are not run by "Исправить всё".</summary>
public bool InFixAll { get; init; } = true;
/// <summary>Windows settings page or web page that helps with the issue ("Открыть настройки" / "Открыть страницу").</summary>
public string? SettingsUri { get; init; }

// IssueItem
public bool HasSettings { get; }            // !string.IsNullOrWhiteSpace(Issue.SettingsUri)
public string SettingsLabel { get; }        // http/https → "Открыть страницу", otherwise "Открыть настройки"
public ICommand? OpenSettingsCommand { get; internal set; }

// DashboardViewModel (renamed from the FixSafe* members)
public AsyncRelayCommand FixAllCommand { get; }
public int FixAllCount { get; }             // _issues.Count(i => i.CanFix && i.InFixAll)
public bool HasFixAll { get; }              // !IsScanning && FixAllCount > 0
public string FixAllText { get; }           // $"Исправить всё ({FixAllCount})"
internal Task<FixBatchResult?> FixAllAsync();
internal static string FixAllConfirmText(IReadOnlyList<IssueItem> items);
internal Action<string> OpenUri { get; set; } = ProcessRunner.ShellOpen;   // test seam
internal Func<string, bool> RequestElevation { get; set; } = reason => { Admin.RequireElevation(reason); return true; }; // test seam
```
`FixBatchResult` gets one more field `int NeedAdmin` (results where `HealthScoring.LooksLikeAdminRequired(FixResult)`).

- [ ] **Step 1: Write failing tests** (new file `tests/PcCleaner.Tests/Dashboard/FixAllTests.cs`, same collection /
  helpers as `DashboardViewModelTests.cs` — read it first, reuse its fake contributor and its `WithConfirm` helper):
  1. `HealthIssue_Defaults`: `new HealthIssue { Module="M", Title="T" }` → `FixLabel == "Исправить"`, `InFixAll`,
     `SettingsUri == null`.
  2. `FixAllCount_CountsFixableInFixAllIssues`: contributor returns 3 fixable issues (one `InFixAll=false`) and one
     without fix → after scan `FixAllCount == 2`, `FixAllText == "Исправить всё (2)"`.
  3. `FixAll_RunsOnlyInFixAllIssues`: confirm Yes → the `InFixAll=false` issue's fix was never called, both others were.
  4. `FixAll_NotAutoFixSafeIssuesAreIncluded`: an issue with `AutoFixSafe=false, InFixAll=true` is fixed by FixAll.
  5. `FixAll_AdminResults_OfferElevation`: one fix returns "Нужны права администратора: …", `RequestElevation` seam
     records the call → called once, `batch.NeedAdmin == 1`, summary text mentions "прав администратора".
  6. `IssueItem_SettingsLabel`: `ms-settings:windowsupdate` → "Открыть настройки"; `https://x` → "Открыть страницу";
     null → `HasSettings == false`.
  7. `OpenSettings_OpensTheUri`: issue with `SettingsUri`, `OpenUri` seam captures → executing the row's
     `OpenSettingsCommand` passes exactly that URI.
- [ ] **Step 2:** Run `dotnet test … --filter "FullyQualifiedName~FixAllTests"` — expect compile errors / failures.
- [ ] **Step 3: Implement.**
  - `Health.cs`: add the three properties above.
  - `IssueItem`: add `HasSettings`, `SettingsLabel`, `OpenSettingsCommand`; change `AutoFixHint` to
    `Issue.InFixAll ? "Входит в «Исправить всё»" : "Не входит в «Исправить всё»: это действие запускается только отдельно"`.
  - `DashboardViewModel`: rename `FixSafeCommand/SafeFixCount/HasSafeFixes/FixSafeText/FixSafeAsync/FixSafeConfirmText`
    to the `FixAll*` names (update every reference, including `OnPropertyChanged` calls ~734-736 and tests);
    selection `i.Issue.CanFix && i.Issue.InFixAll`; confirm title "Исправить всё"; first line
    "Будет исправлено:"/"Будут исправлены проблемы ({n}):"; replace the "низкорисковые действия" paragraph with
    "Каждое действие можно отменить вручную: файлы уходят в Корзину, настройки Windows включаются и выключаются
    обратно. Удаление программ и перезагрузка сюда не входят — для них есть отдельные кнопки."; busy text
    "Исправляем…"; restore point reason "Исправления с главной страницы". Count `NeedAdmin` from items whose
    `FixFailed && HealthScoring.LooksLikeAdminRequired(item.Issue.FixResult)`. `BatchSummaryText`: when
    `NeedAdmin > 0` append " Для {NeedAdmin} нужны права администратора." After the summary dialog, when
    `NeedAdmin > 0 && !Admin.IsElevated`, call `RequestElevation("исправления, которым нужны права администратора")`.
    Wire `item.OpenSettingsCommand = new RelayCommand(() => OpenUri(item.Issue.SettingsUri!), () => item.HasSettings)`
    next to where `OpenPageCommand` is assigned; wrap `OpenUri` in try/catch → `Dialogs.Error` with `ErrorText`.
  - `DashboardView.xaml`: `FixText` → `Text="{Binding Issue.FixLabel}"` (keep the "Исправляем…" trigger); FixButton
    `ToolTip="{Binding AutoFixHint}"`; add under the FixButton a `GhostButton` `Content="{Binding SettingsLabel}"`
    `Command="{Binding OpenSettingsCommand}"` `Visibility="{Binding HasSettings, Converter={StaticResource BoolToVis}}"`
    `Margin="0,6,0,0"`; batch button binds `FixAllCommand`/`FixAllText`/`HasFixAll`; the "Безопасно исправить" badge
    stays bound to `AutoFixSafe`.
- [ ] **Step 4:** Run the FixAllTests and all `Dashboard` tests — PASS. Build with 0 warnings.
- [ ] **Step 5: Commit** — "Dashboard: Исправить всё, fix labels and settings links".

---

### Task 2: Fixes for Cleaner, Startup and Disk issues

**Files:** `src/PcCleaner/Modules/Cleaner/CleanerService.cs` (~503-560), `src/PcCleaner/Modules/Startup/StartupService.cs`
(~300-360, `SetEnabledAsync` 478, `DeleteAsync` 510), `src/PcCleaner/Modules/Disk/DiskService.cs` (~560-670, `RecycleFiles` 828,
`OpenTool` 125); tests in `tests/PcCleaner.Tests/{Cleaner,Startup,Disk}`.

**Interfaces — Consumes:** `HealthIssue.FixLabel`, `HealthIssue.InFixAll` (Task 1).

Behaviour:
1. **Очистка, "Ещё {X} системного мусора" (CS:522):** `FixAsync` = if the service's elevation check is false →
   return `"Нужны права администратора: системные папки можно очистить только с ними."` (no change); else re-measure
   the admin categories and `CleanAsync(adminCategories, userFoldersOnly: false, …)`, return
   `"Удалено {Format.Bytes(freed)} системного мусора."` or `"Не удалось удалить системный мусор: …"`. `InFixAll = true`.
   The elevation check must be injectable for tests (follow how `CleanerService` already takes `() => true` for
   elevation in `CleanerReviewTests.cs:144`).
2. **Автозагрузка, "Подозрительная запись в автозагрузке: {name}" (SS:306):** `FixAsync` re-reads the entries,
   finds the item by `Id`; gone → `"Запись уже удалена — ничего не изменено."`; RunOnce → `DeleteAsync`; otherwise
   `SetEnabledAsync(item, false)`. Map `StartupActionResult`: success → `"Запись «{name}» отключена — включить её
   можно на странице «Автозагрузка»."` (or "удалена"), `NeedsAdmin` → `"Нужны права администратора: …"`, other →
   `"Не удалось отключить «{name}»: {Message}"`. `FixLabel = "Отключить"`, `InFixAll = true`.
3. **Диски, "Старые установщики и архивы в «Загрузках»" (DS:663):** `FixAsync` re-checks each file still exists
   with the same size and last-write time as in the scan, `RecycleFiles(stillSame)`; result
   `"В Корзину перемещено {n} файлов ({Format.Bytes(bytes)}) — их можно восстановить."`; none left →
   `"Файлы уже удалены — ничего не изменено."`. `FixLabel = "В Корзину"`, `InFixAll = true`.
4. **Диски, "Осталась папка Windows.old" (DS:587):** `FixAsync` opens the Disk Cleanup wizard through the existing
   method used by the Disk page (`OpenTool` / Cleaner `LaunchDiskCleanup`) via an injectable launcher; result
   `"Открыта «Очистка диска»: нажмите «Очистить системные файлы» и отметьте «Предыдущие установки Windows»."`
   or `"Не удалось открыть «Очистку диска»."`. `FixLabel = "Открыть очистку диска"`, `InFixAll = false`.

- [ ] **Step 1: Failing tests** — for each issue: the issue now has `FixAsync`, the expected `FixLabel`/`InFixAll`;
  running the fix in the sandbox does the change (startup entry disabled in the sandbox registry; files moved via the
  fake recycle function; system junk with elevation=false returns the admin text and deletes nothing; with
  elevation=true in `CleanerEnvironment.ForTests` cleans the fake `Windows\Temp` file; Windows.old fix calls the fake
  launcher once); stale cases (entry removed / file changed after scan) change nothing. Update the existing tests that
  asserted `FixAsync == null` for these issues: `tests/PcCleaner.Tests/Disk/DownloadsAdvisorTests.cs:136`,
  `tests/PcCleaner.Tests/Startup/StartupLogicTests.cs:473`.
- [ ] **Step 2:** run the three test folders' classes — FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run — PASS; also `--filter "FullyQualifiedName~Dashboard"` (ReclaimableSplit: old downloads now count
  as "can free now" — update `ReportModels`-related assertions only if a test pins the old split, and explain in the
  commit body).
- [ ] **Step 5: Commit** — "Dashboard fixes: system junk, suspicious startup entry, old downloads, Windows.old".

---

### Task 3: Fixes for Programs issues

**Files:** `src/PcCleaner/Modules/Programs/ProgramsService.cs` (~700-760, `RunUninstallerAsync` 490, `RemoveStoreAppAsync` 666),
`src/PcCleaner/Modules/Programs/ProgramRules.cs` (`BuildQuietLaunch` 143); tests `tests/PcCleaner.Tests/Programs`.

1. **"Нежелательная программа: {name}" (PrS:704):** `FixAsync` re-reads the program by registry key (gone →
   "Программа уже удалена — ничего не изменено."); quiet when `ProgramRules.BuildQuietLaunch` returns a command for it
   and it is not a runtime, otherwise normal; `RunUninstallerAsync(p, quiet, null, ct)`; map `UninstallOutcome`:
   `Removed` → "Программа «{name}» удалена.", `StillInstalled` → "Не удалось удалить «{name}»: программа осталась в
   списке установленных.", `UacDeclined` → "Не удалось удалить «{name}»: вы отклонили запрос Windows на права
   администратора.", `NotStarted` → "Не удалось запустить удаление «{name}».". `FixLabel = "Удалить программу"`,
   `InFixAll = false`. A restore point is created first (`RestorePoint.TryCreateAsync`, as `UninstallAsync` does).
2. **"{n} предустановленных приложений можно удалить" (PrS:746):** `FixAsync` re-reads the Store apps, takes those of
   the bloat kind that are still installed and `CanRemove`, removes each with `RemoveStoreAppAsync`, returns
   "Удалено приложений: {ok} из {n}." (+ " Не удалось: {names}." on failures; none left → "ничего не изменено").
   `FixLabel = "Удалить лишние"`, `InFixAll = false`.

The uninstall runner must be injectable for tests (add an internal seam on `ProgramsService`, e.g.
`internal Func<InstalledProgram, bool, CancellationToken, Task<UninstallOutcome>>? UninstallOverride`, and the same for
Store app removal) — tests must never launch real uninstallers or PowerShell.

- [ ] Steps: failing tests (labels, InFixAll=false, quiet chosen when a quiet command exists, normal otherwise,
  outcome texts, stale program → no call; bloat: only removable bloat apps passed to the fake) → FAIL → implement →
  PASS (update `ProgramsServiceTests.cs:301` which asserted no fix) → commit "Dashboard fixes: unwanted program and
  preinstalled Store apps".

---

### Task 4: Security fixes and settings links

**Files:** `src/PcCleaner/Modules/Security/SecurityService.cs` (BuildIssues ~1035-1260, `RemoveHostsLinesAsync` 704,
`RestoreWinlogonAsync` 749, `RemoveIfeoDebuggerAsync` 839, `DisableProxyAsync` 882); tests `tests/PcCleaner.Tests/Security`.

1. hosts "Файл hosts подменяет адреса сайтов" (1167): `FixAsync` → `RemoveHostsLinesAsync` with the entries whose
   `Risk == Danger` (the method re-reads the file); `FixLabel = "Убрать подмену"`.
2. "Подменён запуск при входе в Windows" (1178): restore point (`RestorePoint.TryCreateAsync`) then
   `RestoreWinlogonAsync(report.Winlogon, ct)`; `FixLabel = "Восстановить"`.
3. "Перехвачен запуск системных программ" (1181, Critical only): restore point, then `RemoveIfeoDebuggerAsync` for each
   entry with `Risk == Danger`; the low-severity "Найден перехват запуска" (1183) stays without a fix.
4. Proxy (1188): `DisableProxyAsync(report.Proxy, ct)`; `FixLabel = "Выключить прокси"`.
All four: `InFixAll = true`; result strings via the existing `DashboardText` helper (SeS:1035) so failures/admin
wording match.
5. `SettingsUri`: antivirus off (1074, 1102) `windowsdefender:`; updates (1194) `ms-settings:windowsupdate`;
   SmartScreen (1211) `windowsdefender://appbrowser`; RDP (1214) `ms-settings:remotedesktop`; encryption (1241)
   `ms-settings:deviceencryption`; memory integrity (1251) `windowsdefender://coreisolation`; wide exclusions (1095)
   `windowsdefender://threatsettings`.

- [ ] Steps: failing tests in `SecuritySandbox` (hosts temp file with a danger line → fix removes it and keeps the
  others; proxy sandbox key → disabled; Winlogon/IFEO sandbox keys → restored/removed; restore point must not be
  created for real in tests — check how the Security page tests stub `RestorePoint`; SettingsUri values per issue) →
  update tests asserting no fix: `SecurityServiceSandboxTests.cs:318` and `:605`, `SecurityConsistencyTests.cs:286`,
  `DeviceSecurityTests.cs:237/344/417/462/519` (only where they cover these issues) → implement → PASS → commit
  "Dashboard fixes: hosts, Winlogon, IFEO and proxy; Security settings links".

---

### Task 5: Network and Optimizer fixes and links

**Files:** `src/PcCleaner/Modules/Network/NetworkRules.cs` (`BuildIssues` 743, 767, 784), `NetworkService.cs`
(`FlushDnsAsync` 113), `src/PcCleaner/Modules/Optimizer/OptimizerService.cs` (233-290), `RecommendationEngine.cs`
(1500, 1524, 1275); new `src/PcCleaner/Core/SystemRestart.cs`; tests `tests/PcCleaner.Tests/{Network,Optimizer}`.

1. Network: plumb the finding kind so the service can attach a fix: "DNS не отвечает — сайты не открываются" (784) →
   `FixAsync` = `FlushDnsAsync`, `FixLabel = "Сбросить кэш DNS"`, `InFixAll = true`; "Нет доступа в интернет" (767) →
   `SettingsUri = "ms-settings:network-status"`. Update `NetworkIssuesTests.cs:49` accordingly.
2. Optimizer: `OptimizerService` (≈263) passes `rec.SettingsUri` into `HealthIssue.SettingsUri` for every
   recommendation (monitor Hz, power plan, drivers, etc. already carry it).
3. HAGS (OS:246): without elevation the issue still gets a `FixAsync` that returns
   "Нужны права администратора: планирование GPU включается только с ними." and changes nothing.
4. Reboot advice "Windows ждёт перезагрузки…" (1500) and "Компьютер не перезагружался {N}" (1524):
   new `SystemRestart` in Core:
   ```csharp
   /// <summary>Restarts Windows after the user confirmed it (unsaved work in other programs would be lost).</summary>
   public static class SystemRestart
   {
       internal static Func<string, string, bool> Confirm { get; set; } = Dialogs.Confirm;
       internal static Func<bool> Start { get; set; } = StartRestart;   // shutdown.exe /r /t 5
       public static Task<string> RestartAsync(CancellationToken ct) { … }
   }
   ```
   `RestartAsync`: confirm title "Перезагрузить компьютер", text "Сохраните работу во всех программах. Компьютер
   перезагрузится через несколько секунд. Продолжить?"; No → "Перезагрузка отменена — ничего не изменено.";
   start fails → "Не удалось перезагрузить компьютер."; OK → "Компьютер перезагружается…". `FixLabel = "Перезагрузить"`,
   `InFixAll = false`. The rule "manual advice is never auto-fixed" (`OptimizerParityServiceTests.cs:77`) stays for
   every other manual recommendation — adjust that test only to exempt the reboot recommendations.

- [ ] Steps: failing tests (DNS fix calls the fake flush; settings URIs; HAGS non-admin text; reboot: confirm No →
  Start not called, Yes → Start called once; never call the real shutdown) → FAIL → implement → PASS → commit
  "Dashboard fixes: DNS, reboot, GPU scheduling; settings links".

---

### Task 6: Shared bulk-selection building blocks

**Files:** Create `src/PcCleaner/Core/CheckedSet.cs`, `src/PcCleaner/Core/BulkText.cs` (moved `BulletList` from
`Modules/Settings/SettingsViewModel.cs:167`, keep a forwarding call or update its callers), `src/PcCleaner/Controls/BulkActionBar.xaml`
(+ `.xaml.cs`) — check where shared controls live (`src/PcCleaner/Controls` or `Themes`) and follow it; tests
`tests/PcCleaner.Tests/Core/CheckedSetTests.cs`.

```csharp
/// <summary>Checked rows of a list, kept by a stable key so the checks survive list rebuilds and refreshes.</summary>
public sealed class CheckedSet<TKey> : ObservableObject where TKey : notnull
{
    public int Count { get; }
    public bool IsChecked(TKey key);
    public void Set(TKey key, bool isChecked);          // raises Changed + PropertyChanged(Count)
    public void Clear();
    /// <summary>Header checkbox: true = all of visibleKeys checked, false = none, null = some.</summary>
    public bool? StateOf(IEnumerable<TKey> visibleKeys);
    public void SetAll(IEnumerable<TKey> visibleKeys, bool isChecked);
    /// <summary>Forgets keys that are no longer in the list (process exited, entry deleted).</summary>
    public void KeepOnly(IEnumerable<TKey> existingKeys);
    public IReadOnlyCollection<TKey> Keys { get; }
    public event EventHandler? Changed;
}
```
`BulkText.BulletList(IEnumerable<string> lines, int max = 14)` → "• a\n• b\n… и ещё N".
`BulkActionBar`: `UserControl` with dependency properties `CheckedCount` (int), `ActionsContent` (object, a
`ContentPresenter` slot for the page's buttons), `ClearCommand` (ICommand); shows
"Отмечено: {CheckedCount}" + actions + `GhostButton` "Снять отметки"; collapsed when `CheckedCount == 0`.
Use existing theme resources (`Card`/`AccentSoftBrush`, `GhostButton`).

- [ ] Steps: failing `CheckedSetTests` (set/clear/count, StateOf all/none/some/empty visible → false, SetAll only
  touches visible keys, KeepOnly drops vanished keys and raises Changed, keys survive "rebuild" = new row objects with
  same keys) and `BulkTextTests` (≤ max lines, "и ещё N") → FAIL → implement → PASS → build 0 warnings → commit
  "Core: CheckedSet, BulletList and BulkActionBar for bulk actions".

---

### Task 7: Processes — check several and end them

**Files:** `src/PcCleaner/Modules/Processes/ProcessesViewModel.cs`, `ProcessItems.cs`, `ProcessesView.xaml` (DataGrid
~383), tests `tests/PcCleaner.Tests/Processes`.

- Key: rows → `ProcessKey`; groups → group key (use a small record/union key type, e.g. `string` built from the existing
  keys — read `_rows`/`_groups` dictionaries first).
- `ProcessItem.IsChecked` (get from / set into the VM's `CheckedSet`), `ProcessItem.CanCheck => CanKill`,
  `CheckTooltip` = reason when it cannot be checked ("Системный процесс Windows — его нельзя завершать").
- After every `UpdateView` sync call `Checked.KeepOnly(current keys)`; checking a group checks the group key (the
  targets are expanded at action time).
- `KillCheckedCommand` ("Завершить отмеченные"): collect `KillTarget`s of all checked rows and of all instances of
  checked groups (distinct by pid+create time), skip ones whose `CanKill` became false, one confirm built with
  `BulkText.BulletList` of names (+ the explorer warning when explorer is among them), `Task.Run(() => _service.Kill(targets))`,
  report with the existing `BuildKillReport`, then if nothing could be ended for access reasons offer
  `Admin.RequireElevation` as `KillAsync` does; clear checks of ended processes; refresh.
- `ClearChecksCommand`, header checkbox bound to `Checked.StateOf(visible keys)` / `SetAll`.
- XAML: first `DataGridTemplateColumn` with a `CheckBox` (`IsChecked` two-way, `IsEnabled="{Binding CanCheck}"`,
  `ToolTip="{Binding CheckTooltip}"`), header `CheckBox` (three-state) for "все видимые"; `BulkActionBar` above the
  grid with an accent button "Завершить отмеченные".
- Tests (use `ProcessFakes`; add a kill seam `internal Func<IReadOnlyList<KillTarget>, KillReport> KillOverride` if
  `Kill` cannot be faked): checks survive `RefreshAsync`; exited process drops out; protected rows have
  `CanCheck == false` and `SetAll` skips them; confirm No → no kill call; confirm Yes → kill called with all targets of
  checked rows + group instances, no duplicates.
- Commit "Processes: check several processes and end them at once".

---

### Task 8: Startup — check several and disable / enable / delete them

**Files:** `src/PcCleaner/Modules/Startup/StartupViewModel.cs`, `StartupItem.cs`, `StartupView.xaml` (DataGrid ~362),
tests `tests/PcCleaner.Tests/Startup`.

- Key `StartupItem.Id`; `CanCheck => CanToggle || CanDelete`; `KeepOnly` after `ApplyItems`/`RemoveItems`.
- Commands `DisableCheckedCommand`, `EnableCheckedCommand`, `DeleteCheckedCommand`, `ClearChecksCommand`.
- Flow for each: build the applicable list (disable: `CanToggle && IsEnabled`; enable: `CanToggle && !IsEnabled`;
  delete: `CanDelete`) and the skipped list with reasons; if any applicable item `RequiresAdmin && !IsElevated && !IsTask`
  → ask `Admin.RequireElevation` (via a seam); if the user does not restart, drop those items into skipped
  ("нужны права администратора"); one confirm (`BulkText.BulletList`); `RunBusyAsync`: one
  `RestorePoint.TryCreateAsync` when any service is affected; disable/enable → `SetEnabledAsync` one by one with
  progress; delete → existing `DeleteManyAsync`; result → banner `ShowMessage` with
  `StartupBulkResult.Summary`-style text + one `Dialogs.Info`; refresh/AfterItemChanged as the single actions do.
- XAML: checkbox column before the toggle column + header checkbox; `BulkActionBar` with buttons "Отключить",
  "Включить", "Удалить".
- Tests in `StartupSandbox`: checks survive `ApplyItems` with new objects; protected/RunOnce rules; confirm No →
  nothing changes; Yes → all checked Run entries disabled in the sandbox registry; delete moves startup-folder files via
  the sandbox `RecycleFile`; skipped items listed in the result.
- Commit "Startup: check several entries and disable, enable or delete them at once".

---

### Task 9: Programs — check several and uninstall them

**Files:** `src/PcCleaner/Modules/Programs/ProgramsViewModel.cs`, `ProgramModels.cs`, `ProgramsView.xaml` (Programs grid
~469, Store grid ~817), tests `tests/PcCleaner.Tests/Programs`.

- Programs tab: key `RegistryKeyPath`; `CanCheck => !IsRuntime` (tooltip "Компоненты .NET, Visual C++ и т. п. нужны
  другим программам — удаляйте их по одной"); `KeepOnly` after `ApplyPrograms`.
- `UninstallCheckedCommand` ("Удалить отмеченные"): one confirm (names + "Программы, которые умеют удаляться тихо,
  удалятся без окон; для остальных по очереди откроются их окна удаления."), one restore point, then strictly one by
  one: re-find the program (gone → count as removed), quiet when `ProgramRules.BuildQuietLaunch` gives a command, else
  normal, via `RunUninstallerAsync` (use the Task 3 seam in tests); progress "Удаляем ({i} из {n}): {name}"; cancel stops
  before the next program; summary by `UninstallOutcome` (removed / still installed / UAC declined / not started) in one
  `Dialogs.Info`; `RescanProgramsAsync` at the end.
- Store tab: key `PackageFullName`; `CanCheck => CanRemove`; `RemoveCheckedStoreAppsCommand` like
  `RemoveAllBloatAsync` (one confirm, one restore point, loop `RemoveStoreAppAsync`, summary).
- XAML: checkbox columns + header checkboxes on both grids; one `BulkActionBar` per tab.
- Tests with the fake uninstall/remove seams: runtimes not checkable; confirm No → no calls; Yes → calls in order,
  quiet flag true only where a quiet command exists; cancellation after the first stops the rest; Store protected apps
  not checkable; checks survive `ApplyPrograms`.
- Commit "Programs: check several programs or Store apps and remove them at once".

---

### Task 10: Verification (controller)

- [ ] Full `dotnet test tests\PcCleaner.Tests --nologo` — all pass (baseline 2284 + new).
- [ ] `dotnet build src\PcCleaner -c Release --nologo` — 0 warnings, 0 errors.
- [ ] Read-only screenshots: `src\PcCleaner\bin\Release\net7.0-windows\WaterCa7sPcCleaner.exe --screenshots <scratchpad>
  --page Dashboard,Processes,Startup,Programs` and look at each.
- [ ] Short Russian report: what changed, what was not verified on the real PC (fixes, kills, uninstalls were only run
  against fakes).
