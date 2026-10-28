# Новое меню (часть A, Windows) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Боковое меню из семи разделов (Главная, Очистка, Компьютер, Система, Защита; внизу Профиль и Настройки) с вкладками-страницами, новая страница «Комплектующие», заготовка «Профиля» и нижняя строка с версией и состоянием обновлений.

**Architecture:** Нынешние страницы (`PageViewModel`) не переписываются. Новый `SectionViewModel` держит список страниц-вкладок; `ShellMenu.Build` раскладывает страницы по разделам по ключам; `MainViewModel` показывает видимые разделы в двух списках боковой панели и полосу вкладок над страницей. Навигация по-прежнему идёт по ключу страницы (`NavigateTo("Disk")` открывает раздел «Система» и вкладку «Диски»). «Комплектующие» — отдельная страница, которая показывает карточки железа из того же `OptimizerViewModel` (одна загрузка на две вкладки).

**Tech Stack:** C# / WPF, .NET 7 SDK (`net7.0-windows`), xUnit, `T._` / `T.Ctx` + `en.json`.

## Global Constraints

- Спека: `A:\WaterCa7s-pc-cleaner\docs\superpowers\specs\2026-10-27-menu-and-accounts-design.md`, разделы 1 и 7 (читать с локального `main`).
- Вкладки — полоса сверху страницы раздела. Нынешние страницы (`PageViewModel`) не переписываются: раздел — новый `SectionViewModel` со списком вкладок-страниц. Страница с одной вкладкой показывается без полосы.
- `NavigateTo(key)` по-прежнему принимает ключ страницы (`"Disk"`, `"Network"`…): открывает раздел и вкладку. Все ссылки Главной и подсказки страниц работают без изменений.
- Простой режим (`UiModes`) прячет **вкладки**, а не пункты меню; раздел без видимых вкладок не показывается. Вопрос «переключиться на опытный режим?» для скрытой вкладки остаётся.
- Все страницы по-прежнему живут в `Pages` (проверка «страница занята» перед перезапуском, вклад в оценку Главной).
- Нижняя строка: убрать « · Сделано для WaterCa7» и текст журнала посередине; в углу — `v1.5.0 · последняя версия` / `v1.5.0 · скачана 1.6.0` / `v1.5.0 · проверка…` (из `UpdateService.State`).
- `--screenshots` получает `--page Раздел/Вкладка`; без него снимает каждую вкладку отдельно.
- Новые тексты — через `T._` с английским в `en.json`. Тест `LocalizationCatalogTests` требует запись для каждого текста и **запрещает лишние записи**: убранный текст удаляется из `en.json`. `T._` принимает только литералы.
- Windows 1.5.0. Работа идёт в ветке `menu-accounts` от `origin/master`; ничего не выпускается до части E и «да» владельца. **Не пушить** локальный `main` (там коммиты спек). Ветку `menu-accounts` тоже не пушить без «да» владельца.
- «Телефон» в меню не добавляется (раздел iPhone не готов). Замков Pro и витрин в этой части нет (часть B).
- Тесты не меняют настоящую систему; скриншоты — только режим `--screenshots` (все вопросы отвечаются «Нет»).
- Коммиты заканчиваются строкой `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Язык общения с владельцем — русский; комментарии в коде — английские, как в остальном коде.

## Карта меню (ключи)

| Раздел (`Key`) | Заголовок | Вкладки (ключи страниц) | Место |
|---|---|---|---|
| `Dashboard` | заголовок страницы («Главная») | `Dashboard` | сверху |
| `Cleaner` | «Очистка» | `Cleaner` | сверху |
| `Computer` | `T._("Компьютер")` | `Hardware`, `Optimizer` | сверху |
| `System` | `T._("Система")` | `Processes`, `Startup`, `Programs`, `Disk` | сверху |
| `Protection` | `T.Ctx("menu", "Защита")` ("Protection"; простое «Защита» в en.json уже значит "Security") | `Security`, `Network` | сверху |
| `Profile` | «Профиль» | `Profile` | внизу |
| `Settings` | «Настройки» | `Settings` | внизу |

Порядок `Pages` = порядок меню: Dashboard, Cleaner, Hardware, Optimizer, Processes, Startup, Programs, Disk, Security, Network, Profile, Settings.
Простой режим: `Dashboard`, `Cleaner`, `Programs`, `Profile`, `Settings` (добавлен только `Profile`; «Комплектующие», как раньше «Ускорение», — в опытном режиме).

## Файлы

- Create: `src/PcCleaner/Core/Updates/UpdateFooter.cs` — текст угла нижней строки (чистая функция).
- Create: `src/PcCleaner/Core/SectionViewModel.cs` — раздел меню: вкладки, видимые вкладки, последняя открытая вкладка.
- Create: `src/PcCleaner/Core/ShellMenu.cs` — таблица меню и раскладка страниц по разделам.
- Create: `src/PcCleaner/Modules/Optimizer/HardwareViewModel.cs`, `HardwareView.xaml`, `HardwareView.xaml.cs` — «Комплектующие».
- Create: `src/PcCleaner/Modules/Profile/ProfileViewModel.cs`, `ProfileView.xaml`, `ProfileView.xaml.cs` — заготовка «Профиля» (наполняет часть B).
- Modify: `src/PcCleaner/MainViewModel.cs` — разделы, навигация, текст нижней строки, порядок страниц.
- Modify: `src/PcCleaner/MainWindow.xaml` — два списка разделов, полоса вкладок, нижняя строка.
- Modify: `src/PcCleaner/Themes/Theme.xaml` — стиль `TabStripItem`.
- Modify: `src/PcCleaner/App.xaml` — шаблоны для новых страниц.
- Modify: `src/PcCleaner/App.xaml.cs` — текст справки `--page`.
- Modify: `src/PcCleaner/Core/UiModes.cs` — `Profile` в простом режиме.
- Modify: `src/PcCleaner/Core/ScreenshotRunner.cs` — `--page Раздел/Вкладка`, имена файлов.
- Modify: `src/PcCleaner/Modules/Optimizer/OptimizerViewModel.cs`, `OptimizerView.xaml` — карточки железа уходят во вкладку «Комплектующие».
- Modify: `src/PcCleaner/Core/Localization/en.json`, `src/PcCleaner/PcCleaner.csproj` (1.5.0), `docs/ARCHITECTURE.md`.
- Tests: `tests/PcCleaner.Tests/Updates/UpdateFooterTests.cs` (new), `tests/PcCleaner.Tests/Core/ShellMenuTests.cs` (new), `tests/PcCleaner.Tests/Optimizer/HardwareViewModelTests.cs` (new), `tests/PcCleaner.Tests/Core/ModesTests.cs`, `tests/PcCleaner.Tests/Core/ShellEnglishTests.cs`.

Команды (из корня рабочей копии `A:\WaterCa7s-pc-cleaner-menu`):

- сборка: `dotnet build src/PcCleaner -c Debug`
- один класс тестов: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~PcCleaner.Tests.Core.ShellMenuTests"`
- все тесты: `dotnet test tests/PcCleaner.Tests`

---

### Task 1: Ветка, версия 1.5.0 и нижняя строка

**Files:**
- Create: `src/PcCleaner/Core/Updates/UpdateFooter.cs`
- Modify: `src/PcCleaner/PcCleaner.csproj:19`, `src/PcCleaner/MainViewModel.cs`, `src/PcCleaner/MainWindow.xaml` (нижняя строка, строка 3 сетки содержимого), `src/PcCleaner/Core/Localization/en.json`
- Test: `tests/PcCleaner.Tests/Updates/UpdateFooterTests.cs`, `tests/PcCleaner.Tests/Core/ShellEnglishTests.cs`

**Interfaces:**
- Produces: `public static string UpdateFooter.Text(UpdateState state, UpdateManifest? manifest, Version current)`; `public string MainViewModel.FooterText { get; }`. `MainViewModel.StatusText` удаляется.

- [ ] **Step 1: Рабочая копия на ветке `menu-accounts`**

```bash
git -C A:/WaterCa7s-pc-cleaner fetch origin
git -C A:/WaterCa7s-pc-cleaner worktree add A:/WaterCa7s-pc-cleaner-menu -b menu-accounts origin/master
```

Дальше всё делается в `A:\WaterCa7s-pc-cleaner-menu`. План и спека остаются на локальном `main` в `A:\WaterCa7s-pc-cleaner` — читать их оттуда, в ветку не копировать.

- [ ] **Step 2: Версия 1.5.0**

В `src/PcCleaner/PcCleaner.csproj` заменить `<Version>1.4.0</Version>` на `<Version>1.5.0</Version>`. README и заметки к выпуску не трогать (часть E).

- [ ] **Step 3: Падающий тест текста угла**

Create `tests/PcCleaner.Tests/Updates/UpdateFooterTests.cs`:

```csharp
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

public class UpdateFooterTests
{
    private static readonly UpdateManifest M = UpdateManifest.Parse(
        UpdateTestKit.ManifestJson("1.6.0", 2_000_000, new string('a', 64)), out _)!;

    private static readonly Version Current = new(1, 5, 0);

    [Theory]
    [InlineData(UpdateState.Checking, "v1.5.0 · проверка…")]
    [InlineData(UpdateState.UpToDate, "v1.5.0 · последняя версия")]
    [InlineData(UpdateState.Downloading, "v1.5.0 · скачивается 1.6.0")]
    [InlineData(UpdateState.Ready, "v1.5.0 · скачана 1.6.0")]
    [InlineData(UpdateState.ManualRequired, "v1.5.0 · есть версия 1.6.0")]
    [InlineData(UpdateState.Updated, "v1.5.0 · обновлена")]
    [InlineData(UpdateState.None, "v1.5.0")]
    [InlineData(UpdateState.Failed, "v1.5.0")]
    public void TheCorner_ShowsTheVersionAndWhatTheUpdaterKnows(UpdateState state, string expected)
        => Assert.Equal(expected, UpdateFooter.Text(state, M, Current));

    [Theory]
    [InlineData(UpdateState.Downloading)]
    [InlineData(UpdateState.Ready)]
    [InlineData(UpdateState.ManualRequired)]
    public void WithoutAManifest_OnlyTheVersionIsShown(UpdateState state)
        => Assert.Equal("v1.5.0", UpdateFooter.Text(state, null, Current));

    [Fact]
    public void AVersionWithoutABuildNumber_IsShownWithThreeParts()
        => Assert.Equal("v1.5.0 · последняя версия", UpdateFooter.Text(UpdateState.UpToDate, null, new Version(1, 5)));
}
```

- [ ] **Step 4: Убедиться, что тест падает**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~UpdateFooterTests"`
Expected: ошибка сборки `The name 'UpdateFooter' does not exist in the current context`.

- [ ] **Step 5: `UpdateFooter`**

Create `src/PcCleaner/Core/Updates/UpdateFooter.cs`:

```csharp
using PcCleaner.Core.Localization;

namespace PcCleaner.Core.Updates;

/// <summary>The corner of the window's bottom line: the version and what the updater knows (pure, unit-tested).</summary>
public static class UpdateFooter
{
    public static string Text(UpdateState state, UpdateManifest? manifest, Version current)
    {
        var version = "v" + UpdateManifest.Normalize(current).ToString(3);
        var found = manifest is null ? null : UpdateManifest.Normalize(manifest.Version).ToString(3);
        var tail = state switch
        {
            UpdateState.Checking => T._("проверка…"),
            UpdateState.UpToDate => T._("последняя версия"),
            UpdateState.Downloading when found is not null => T.F("скачивается {0}", found),
            UpdateState.Ready when found is not null => T.F("скачана {0}", found),
            UpdateState.ManualRequired when found is not null => T.F("есть версия {0}", found),
            UpdateState.Updated => T._("обновлена"),
            _ => null,
        };
        return tail is null ? version : version + " · " + tail;
    }
}
```

- [ ] **Step 6: Тест проходит**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~UpdateFooterTests"`
Expected: все тесты PASS.

- [ ] **Step 7: `FooterText` вместо `StatusText` в `MainViewModel`**

В `src/PcCleaner/MainViewModel.cs`:

1. Удалить поле `private string _statusText = T.Ctx("status", "Готово"); ...` (строка 24), свойство `StatusText` (строки ~132–136) и строку `Log.EntryAdded += entry => StatusText = entry.Message;` в конструкторе.
2. Свойство `VersionText` заменить на:

```csharp
    /// <summary>The corner of the bottom line: «v1.5.0 · последняя версия» (<see cref="UpdateFooter"/>).</summary>
    public string FooterText => UpdateFooter.Text(Updates.State, Updates.Manifest, Updates.CurrentVersion);
```

3. В `OnUpdateBannerChanged()` добавить строку `OnPropertyChanged(nameof(FooterText));` (метод уже вызывается на каждое изменение `Updates`).

Перед удалением `VersionText` проверить, что его больше нигде не используют: `git grep -n "VersionText" -- src tests` — допустимы только `MainWindow.xaml` (меняется в следующем шаге) и Mac-подобные совпадения в других классах (`SplashViewModel` и т. п. — не трогать, если это их собственные свойства).

- [ ] **Step 8: Нижняя строка окна**

В `src/PcCleaner/MainWindow.xaml` блок `<Border Grid.Row="3" ...>` заменить на:

```xml
                <Border Grid.Row="3" BorderBrush="{StaticResource LineBrush}" BorderThickness="0,1,0,0" Padding="32,7">
                    <!-- The version and the state of the updater (Core/Updates/UpdateFooter). -->
                    <TextBlock HorizontalAlignment="Right" Style="{StaticResource Muted}" FontSize="11.5" Text="{Binding FooterText}" />
                </Border>
```

- [ ] **Step 9: `en.json`**

В `src/PcCleaner/Core/Localization/en.json` (файл отсортирован по ключу как обычный JSON-объект; вставлять рядом с соседями по алфавиту, как остальные записи):

- удалить записи `" · Сделано для WaterCa7"` и `"status\u0004Готово"`;
- добавить:

```json
  "есть версия {0}": "version {0} available",
  "обновлена": "updated",
  "последняя версия": "latest version",
  "проверка…": "checking…",
  "скачана {0}": "{0} downloaded",
  "скачивается {0}": "downloading {0}",
```

Если какой-то из этих ключей уже есть — оставить существующую запись (дубликат ключа ломает JSON).

- [ ] **Step 10: Тест оболочки на английском**

В `tests/PcCleaner.Tests/Core/ShellEnglishTests.cs` тест `TheShell_ShowsItsStatusAndAdminLine_InEnglish` проверяет `StatusText`. Заменить в нём проверки `StatusText` (строки с `"Ready"` и `"Готово"`) на проверки нижней строки:

```csharp
            Assert.Equal("v1.5.0 · latest version", UpdateFooter.Text(UpdateState.UpToDate, null, new Version(1, 5, 0)));
            Assert.Equal("v1.5.0 · checking…", UpdateFooter.Text(UpdateState.Checking, null, new Version(1, 5, 0)));
```

(внутри `using (TestLanguage.English())`), а русскую проверку после блока — на
`Assert.Equal("v1.5.0 · последняя версия", UpdateFooter.Text(UpdateState.UpToDate, null, new Version(1, 5, 0)));`.
Проверку строки администратора в этом тесте оставить как есть. Переименовать тест в `TheShell_ShowsItsFooterAndAdminLine_InEnglish`.

- [ ] **Step 11: Все тесты**

Run: `dotnet test tests/PcCleaner.Tests`
Expected: PASS (в том числе `LocalizationCatalogTests` — нет текстов без записи и лишних записей).

- [ ] **Step 12: Commit**

```bash
git add -A
git commit -m "Menu: version 1.5.0, bottom line shows the version and the update state

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Раздел меню и раскладка страниц

**Files:**
- Create: `src/PcCleaner/Core/SectionViewModel.cs`, `src/PcCleaner/Core/ShellMenu.cs`
- Modify: `src/PcCleaner/Core/Localization/en.json`
- Test: `tests/PcCleaner.Tests/Core/ShellMenuTests.cs`

**Interfaces:**
- Consumes: `PageViewModel` (`Key`, `Title`, `IconGlyph`); тестовый `FakePage(string key)` из `tests/PcCleaner.Tests/Core/ModesTests.cs` (internal, тот же namespace `PcCleaner.Tests.Core`).
- Produces:
  - `public enum SectionPlace { Top, Bottom }`
  - `public sealed class SectionViewModel : ObservableObject` с конструктором `(string key, Func<string>? title, string? iconGlyph, IReadOnlyList<PageViewModel> tabs, SectionPlace place)` и членами `Key`, `Title`, `IconGlyph`, `Place`, `Tabs`, `VisibleTabs` (`ObservableCollection<PageViewModel>`), `ShowTabs`, `HasVisibleTabs`, `EntryTab`, `Contains(PageViewModel)`, `internal Remember(PageViewModel)`, `internal SyncVisibleTabs(Func<PageViewModel, bool>)`.
  - `public static class ShellMenu` с `public static List<SectionViewModel> Build(IReadOnlyList<PageViewModel> pages)`.

- [ ] **Step 1: Падающие тесты**

Create `tests/PcCleaner.Tests/Core/ShellMenuTests.cs`:

```csharp
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using Xunit;

namespace PcCleaner.Tests.Core;

public class ShellMenuTests
{
    internal static readonly string[] AllKeys =
    {
        "Dashboard", "Cleaner", "Hardware", "Optimizer", "Processes", "Startup", "Programs", "Disk", "Security", "Network",
        "Profile", "Settings",
    };

    private static List<FakePage> Pages(params string[] keys) => keys.Select(k => new FakePage(k)).ToList();

    [Fact]
    public void TheMenu_HasSevenSections_WithTheTabsOfTheSpec()
    {
        var sections = ShellMenu.Build(Pages(AllKeys));

        Assert.Equal(new[] { "Dashboard", "Cleaner", "Computer", "System", "Protection", "Profile", "Settings" }, sections.Select(s => s.Key));
        Assert.Equal(new[] { "Hardware", "Optimizer" }, sections[2].Tabs.Select(p => p.Key));
        Assert.Equal(new[] { "Processes", "Startup", "Programs", "Disk" }, sections[3].Tabs.Select(p => p.Key));
        Assert.Equal(new[] { "Security", "Network" }, sections[4].Tabs.Select(p => p.Key));
        Assert.Equal(new[] { SectionPlace.Top, SectionPlace.Top, SectionPlace.Top, SectionPlace.Top, SectionPlace.Top, SectionPlace.Bottom, SectionPlace.Bottom },
            sections.Select(s => s.Place));
    }

    [Fact]
    public void ASectionOfOnePage_TakesTheTitleAndIconOfThatPage()
    {
        var sections = ShellMenu.Build(Pages(AllKeys));

        Assert.Equal("Раздел Dashboard", sections[0].Title);
        Assert.Equal("Раздел Settings", sections[6].Title);
        Assert.Equal("Компьютер", sections[2].Title);
        Assert.Equal("Система", sections[3].Title);
        Assert.Equal("Защита", sections[4].Title);
    }

    [Fact]
    public void MissingPages_AreSkipped_AndASectionWithoutPagesIsLeftOut()
    {
        var sections = ShellMenu.Build(Pages("Dashboard", "Optimizer", "Settings"));

        Assert.Equal(new[] { "Dashboard", "Computer", "Settings" }, sections.Select(s => s.Key));
        Assert.Equal(new[] { "Optimizer" }, sections[1].Tabs.Select(p => p.Key));
    }

    [Fact]
    public void APageNoSectionNames_GetsASectionOfItsOwn_AtTheEndOfTheMainList()
    {
        var sections = ShellMenu.Build(Pages("Dashboard", "Phone", "Settings"));

        Assert.Equal(new[] { "Dashboard", "Phone", "Settings" }, sections.Select(s => s.Key));
        Assert.Equal(SectionPlace.Top, sections[1].Place);
    }

    [Fact]
    public void VisibleTabs_FollowTheFilter_InTheOrderOfTheTabs()
    {
        var system = ShellMenu.Build(Pages(AllKeys)).Single(s => s.Key == "System");

        system.SyncVisibleTabs(_ => true);
        Assert.Equal(new[] { "Processes", "Startup", "Programs", "Disk" }, system.VisibleTabs.Select(p => p.Key));
        Assert.True(system.ShowTabs);

        system.SyncVisibleTabs(p => p.Key == "Programs");
        Assert.Equal(new[] { "Programs" }, system.VisibleTabs.Select(p => p.Key));
        Assert.False(system.ShowTabs); // one tab: no strip
        Assert.True(system.HasVisibleTabs);

        system.SyncVisibleTabs(_ => false);
        Assert.False(system.HasVisibleTabs);
        Assert.Null(system.EntryTab);
    }

    [Fact]
    public void EntryTab_IsTheLastOpenedTab_WhileItIsVisible_ElseTheFirstVisible()
    {
        var system = ShellMenu.Build(Pages(AllKeys)).Single(s => s.Key == "System");
        system.SyncVisibleTabs(_ => true);
        Assert.Equal("Processes", system.EntryTab!.Key);

        system.Remember(system.Tabs.Single(p => p.Key == "Disk"));
        Assert.Equal("Disk", system.EntryTab!.Key);

        system.SyncVisibleTabs(p => p.Key != "Disk");
        Assert.Equal("Processes", system.EntryTab!.Key);
    }

    [Fact]
    public void Remember_IgnoresAPageOfAnotherSection()
    {
        var sections = ShellMenu.Build(Pages(AllKeys));
        var system = sections.Single(s => s.Key == "System");
        system.SyncVisibleTabs(_ => true);

        system.Remember(sections[0].Tabs[0]);

        Assert.Equal("Processes", system.EntryTab!.Key);
        Assert.False(system.Contains(sections[0].Tabs[0]));
    }
}
```

Английские названия проверяются в `tests/PcCleaner.Tests/Core/ShellEnglishTests.cs` — только там можно переключать язык (класс в последовательной коллекции `LanguageSequentialCollection`; в параллельном классе смена языка сломала бы чужие тесты). Добавить туда (и `using PcCleaner.Core;` уже есть):

```csharp
    [Fact]
    public void TheMenuSectionTitles_AreEnglish()
    {
        var sections = ShellMenu.Build(ShellMenuTests.AllKeys.Select(k => new FakePage(k)).ToList());

        using (TestLanguage.English())
            Assert.Equal(new[] { "Computer", "System", "Protection" }, sections.Skip(2).Take(3).Select(s => s.Title));

        Assert.Equal("Защита", sections[4].Title); // and Russian is back
    }
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~ShellMenuTests|FullyQualifiedName~TheMenuSectionTitles_AreEnglish"`
Expected: ошибка сборки (`ShellMenu`, `SectionPlace` не найдены).

- [ ] **Step 3: `SectionViewModel`**

Create `src/PcCleaner/Core/SectionViewModel.cs`:

```csharp
using System.Collections.ObjectModel;

namespace PcCleaner.Core;

/// <summary>Where a section sits in the sidebar: the main list or the block at the bottom (Профиль, Настройки).</summary>
public enum SectionPlace
{
    Top,
    Bottom,
}

/// <summary>
/// A sidebar item: one or more pages shown as tabs in a strip at the top of the page. The pages are the unchanged
/// <see cref="PageViewModel"/>s; a section with one visible tab shows no strip. The simple mode hides tabs, not sections:
/// a section without visible tabs is not in the sidebar (<see cref="ShellMenu"/>, <see cref="MainViewModel"/>).
/// </summary>
public sealed class SectionViewModel : ObservableObject
{
    private readonly Func<string>? _title;
    private readonly string? _iconGlyph;
    private PageViewModel? _lastTab;

    /// <param name="title">The section's own title (read on every get, so it follows the UI language); null — the title of its first page.</param>
    /// <param name="iconGlyph">A Segoe MDL2 glyph; null — the glyph of its first page.</param>
    public SectionViewModel(string key, Func<string>? title, string? iconGlyph, IReadOnlyList<PageViewModel> tabs, SectionPlace place)
    {
        if (tabs.Count == 0)
            throw new ArgumentException("A section needs at least one page.", nameof(tabs));
        Key = key;
        _title = title;
        _iconGlyph = iconGlyph;
        Tabs = tabs;
        Place = place;
    }

    public string Key { get; }

    public string Title => _title?.Invoke() ?? Tabs[0].Title;

    public string IconGlyph => _iconGlyph ?? Tabs[0].IconGlyph;

    public SectionPlace Place { get; }

    /// <summary>All pages of the section, in tab order.</summary>
    public IReadOnlyList<PageViewModel> Tabs { get; }

    /// <summary>The tabs of the current mode, in the order of <see cref="Tabs"/>.</summary>
    public ObservableCollection<PageViewModel> VisibleTabs { get; } = new();

    /// <summary>The strip is shown only when there is a choice.</summary>
    public bool ShowTabs => VisibleTabs.Count > 1;

    public bool HasVisibleTabs => VisibleTabs.Count > 0;

    /// <summary>The tab the section opens on: the last one opened while it is still visible, else the first visible one.</summary>
    public PageViewModel? EntryTab => _lastTab is not null && VisibleTabs.Contains(_lastTab) ? _lastTab : VisibleTabs.FirstOrDefault();

    public bool Contains(PageViewModel page) => Tabs.Contains(page);

    /// <summary>Notes the tab that was opened (a page of another section is ignored).</summary>
    internal void Remember(PageViewModel page)
    {
        if (Tabs.Contains(page))
            _lastTab = page;
    }

    /// <summary>Makes <see cref="VisibleTabs"/> the tabs <paramref name="isVisible"/> lets through, removing and inserting only what differs.</summary>
    internal void SyncVisibleTabs(Func<PageViewModel, bool> isVisible)
    {
        var wanted = Tabs.Where(isVisible).ToList();
        foreach (var page in VisibleTabs.Where(p => !wanted.Contains(p)).ToList())
            VisibleTabs.Remove(page);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= VisibleTabs.Count || !ReferenceEquals(VisibleTabs[i], wanted[i]))
                VisibleTabs.Insert(i, wanted[i]);
        }
        OnPropertyChanged(nameof(ShowTabs));
        OnPropertyChanged(nameof(HasVisibleTabs));
    }
}
```

- [ ] **Step 4: `ShellMenu`**

Create `src/PcCleaner/Core/ShellMenu.cs`:

```csharp
using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

/// <summary>The sidebar of the program (spec 2026-10-27, part A): which pages are the tabs of which section.</summary>
public static class ShellMenu
{
    private sealed record Entry(string Key, Func<string>? Title, string? IconGlyph, string[] PageKeys, SectionPlace Place);

    private static readonly Entry[] Entries =
    {
        new("Dashboard", null, null, new[] { "Dashboard" }, SectionPlace.Top),
        new("Cleaner", null, null, new[] { "Cleaner" }, SectionPlace.Top),
        new("Computer", () => T._("Компьютер"), "\uE977", new[] { "Hardware", "Optimizer" }, SectionPlace.Top),
        new("System", () => T._("Система"), "\uE770", new[] { "Processes", "Startup", "Programs", "Disk" }, SectionPlace.Top),
        // The plain «Защита» is "Security" elsewhere in the UI; the menu says "Protection" (Security is one of its tabs).
        new("Protection", () => T.Ctx("menu", "Защита"), null, new[] { "Security", "Network" }, SectionPlace.Top),
        new("Profile", null, null, new[] { "Profile" }, SectionPlace.Bottom),
        new("Settings", null, null, new[] { "Settings" }, SectionPlace.Bottom),
    };

    /// <summary>
    /// The sections over <paramref name="pages"/>, in menu order. A section gets the pages it names that exist (tests pass a few
    /// pages) and is left out when there are none; a page no section names gets a section of its own at the end of the main
    /// list, so a new page is never lost.
    /// </summary>
    public static List<SectionViewModel> Build(IReadOnlyList<PageViewModel> pages)
    {
        PageViewModel? Find(string key) => pages.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

        var named = new List<(Entry Entry, List<PageViewModel> Tabs)>();
        foreach (var entry in Entries)
        {
            var tabs = entry.PageKeys.Select(Find).OfType<PageViewModel>().ToList();
            if (tabs.Count > 0)
                named.Add((entry, tabs));
        }
        var placed = named.SelectMany(n => n.Tabs).ToHashSet();
        var sections = named.Select(n => new SectionViewModel(n.Entry.Key, n.Entry.Title, n.Entry.IconGlyph, n.Tabs, n.Entry.Place)).ToList();
        var firstBottom = sections.FindIndex(s => s.Place == SectionPlace.Bottom);
        var at = firstBottom < 0 ? sections.Count : firstBottom;
        foreach (var page in pages.Where(p => !placed.Contains(p)))
            sections.Insert(at++, new SectionViewModel(page.Key, null, null, new[] { page }, SectionPlace.Top));
        return sections;
    }
}
```

- [ ] **Step 5: `en.json`**

Добавить (ключ `menu\u0004Защита` пишется в файле именно так — с `\u0004`, как уже записан `status\u0004Готово`):

```json
  "menu\u0004Защита": "Protection",
  "Компьютер": "Computer",
```

`"Система": "System"` уже есть — не дублировать.

- [ ] **Step 6: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~ShellMenuTests|FullyQualifiedName~ShellEnglishTests|FullyQualifiedName~LocalizationCatalogTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Menu: sections with tabs and the layout of the pages

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Оболочка показывает разделы

**Files:**
- Modify: `src/PcCleaner/MainViewModel.cs`, `src/PcCleaner/Core/UiModes.cs:~10` (`SimplePageKeys`)
- Test: `tests/PcCleaner.Tests/Core/ModesTests.cs`, `tests/PcCleaner.Tests/Core/ShellMenuTests.cs`

**Interfaces:**
- Consumes: `ShellMenu.Build`, `SectionViewModel` (Task 2).
- Produces (в `MainViewModel`):
  - `public IReadOnlyList<SectionViewModel> Sections { get; }` — все разделы;
  - `public ObservableCollection<SectionViewModel> TopSections { get; }`, `BottomSections { get; }` — видимые разделы двух списков;
  - `public SectionViewModel? SelectedSection { get; }` — раздел открытой страницы;
  - `public SectionViewModel? SelectedTopSection { get; set; }`, `SelectedBottomSection { get; set; }` — выбор в каждом из двух списков (null, когда открыт раздел другого списка; запись null игнорируется; запись раздела открывает его `EntryTab`);
  - `public string HeaderTitle { get; }` — заголовок над страницей: название раздела, когда видна полоса вкладок, иначе название страницы.
  - `UiModes.SimplePageKeys` = `{ "Dashboard", "Cleaner", "Programs", "Profile", "Settings" }`.

- [ ] **Step 1: Падающие тесты оболочки**

В `tests/PcCleaner.Tests/Core/ShellMenuTests.cs` добавить второй класс (в том же файле, ниже первого):

```csharp
/// <summary>The shell over the menu: two sidebar lists, the tab strip, navigation by page key.</summary>
[Collection(ModeSequentialCollection.Name)]
public class ShellSectionsTests
{
    private readonly AppSettings _settings = new() { UiMode = UiMode.Expert };
    private readonly List<FakePage> _pages = ShellMenuTests.AllKeys.Select(k => new FakePage(k)).ToList();
    private readonly List<string> _questions = new();
    private bool _answer;

    public ShellSectionsTests() => TestEnvironment.Init();

    private MainViewModel Shell() => new(_pages, () => _settings, _ => { })
    {
        Ask = (title, _) =>
        {
            _questions.Add(title);
            return _answer;
        },
    };

    [Fact]
    public void TheSidebar_HasFiveSectionsOnTop_AndProfileAndSettingsAtTheBottom()
    {
        var shell = Shell();

        Assert.Equal(new[] { "Dashboard", "Cleaner", "Computer", "System", "Protection" }, shell.TopSections.Select(s => s.Key));
        Assert.Equal(new[] { "Profile", "Settings" }, shell.BottomSections.Select(s => s.Key));
        Assert.Equal(12, shell.Pages.Count); // every page stays in Pages
        Assert.Equal("Dashboard", shell.SelectedSection!.Key);
    }

    [Theory]
    [InlineData("Dashboard", "Dashboard")]
    [InlineData("Cleaner", "Cleaner")]
    [InlineData("Hardware", "Computer")]
    [InlineData("Optimizer", "Computer")]
    [InlineData("Processes", "System")]
    [InlineData("Startup", "System")]
    [InlineData("Programs", "System")]
    [InlineData("Disk", "System")]
    [InlineData("Security", "Protection")]
    [InlineData("Network", "Protection")]
    [InlineData("Profile", "Profile")]
    [InlineData("Settings", "Settings")]
    public void NavigateTo_APageKey_OpensItsSectionAndTab(string pageKey, string sectionKey)
    {
        var shell = Shell();

        shell.NavigateTo(pageKey);

        Assert.Equal(pageKey, shell.SelectedPage!.Key);
        Assert.Equal(sectionKey, shell.SelectedSection!.Key);
    }

    [Fact]
    public void ASection_OpensOnItsFirstTab_ThenOnTheLastOpenedOne()
    {
        var shell = Shell();
        var system = shell.TopSections.Single(s => s.Key == "System");

        shell.SelectedTopSection = system;
        Assert.Equal("Processes", shell.SelectedPage!.Key);

        shell.NavigateTo("Disk");
        shell.NavigateTo("Dashboard");
        shell.SelectedTopSection = system;
        Assert.Equal("Disk", shell.SelectedPage!.Key);
    }

    [Fact]
    public void OnlyTheListOfTheOpenSection_HasASelection_AndANullWriteIsIgnored()
    {
        var shell = Shell();

        shell.NavigateTo("Settings");
        Assert.Null(shell.SelectedTopSection);
        Assert.Equal("Settings", shell.SelectedBottomSection!.Key);

        shell.SelectedTopSection = null; // what a list writes when its selection is cleared
        Assert.Equal("Settings", shell.SelectedPage!.Key);

        shell.NavigateTo("Network");
        Assert.Equal("Protection", shell.SelectedTopSection!.Key);
        Assert.Null(shell.SelectedBottomSection);
    }

    [Fact]
    public void TheHeader_ShowsTheSection_WhenThereAreTabs_ElseThePage()
    {
        var shell = Shell();

        shell.NavigateTo("Disk");
        Assert.Equal("Система", shell.HeaderTitle);
        Assert.True(shell.SelectedSection!.ShowTabs);

        shell.NavigateTo("Cleaner");
        Assert.Equal("Раздел Cleaner", shell.HeaderTitle);
        Assert.False(shell.SelectedSection!.ShowTabs);
    }

    [Fact]
    public void SimpleMode_HidesTabs_NotTheSectionsThatStillHaveOne()
    {
        _settings.UiMode = UiMode.Simple;
        var shell = Shell();

        Assert.Equal(new[] { "Dashboard", "Cleaner", "System" }, shell.TopSections.Select(s => s.Key));
        Assert.Equal(new[] { "Profile", "Settings" }, shell.BottomSections.Select(s => s.Key));
        var system = shell.TopSections.Single(s => s.Key == "System");
        Assert.Equal(new[] { "Programs" }, system.VisibleTabs.Select(p => p.Key));

        shell.SelectedTopSection = system;
        Assert.Equal("Programs", shell.SelectedPage!.Key);
        Assert.Equal("Раздел Programs", shell.HeaderTitle); // one tab: no strip, the page's own title
    }

    [Fact]
    public void AHiddenTab_IsStillOfferedWithTheExpertMode()
    {
        _settings.UiMode = UiMode.Simple;
        var shell = Shell();

        _answer = false;
        shell.NavigateTo("Disk");
        Assert.Equal(new[] { "Раздел Disk" }, _questions);
        Assert.Equal("Dashboard", shell.SelectedPage!.Key);
    }
}
```

Правило про `_answer = true` (переход в опытный режим и открытие вкладки) уже проверяет `ModesShellTests.NavigateTo_ForAHiddenPage_SwitchesToExpertModeAndGoesThere_WhenTheAnswerIsYes` — не дублировать: этот тест меняет общий режим (`UiModes.Choose`), а `ShellSectionsTests` его не трогают.

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~ShellSectionsTests"`
Expected: ошибка сборки (`TopSections`, `SelectedSection` и т. д. не найдены).

- [ ] **Step 3: Разделы в `MainViewModel`**

В `src/PcCleaner/MainViewModel.cs`:

1. В конструкторе сразу после `Pages = new ObservableCollection<PageViewModel>(pages ?? CreatePages());` добавить:

```csharp
        Sections = ShellMenu.Build(Pages);
        TopSections = new ObservableCollection<SectionViewModel>();
        BottomSections = new ObservableCollection<SectionViewModel>();
```

2. Рядом с `VisiblePages` добавить свойства:

```csharp
    /// <summary>Every section of the menu (<see cref="ShellMenu"/>); a section's tabs are pages of <see cref="Pages"/>.</summary>
    public IReadOnlyList<SectionViewModel> Sections { get; }

    /// <summary>The main list of the sidebar: the sections that have a tab in the current mode.</summary>
    public ObservableCollection<SectionViewModel> TopSections { get; }

    /// <summary>The list at the bottom of the sidebar (Профиль, Настройки).</summary>
    public ObservableCollection<SectionViewModel> BottomSections { get; }

    /// <summary>The section of the open page.</summary>
    public SectionViewModel? SelectedSection => _selectedPage is null ? null : Sections.FirstOrDefault(s => s.Contains(_selectedPage));

    /// <summary>
    /// The selection of the main list: the open section when it is there, else null. The list writes here: a section opens on
    /// its <see cref="SectionViewModel.EntryTab"/>; null (the list cleared its selection because a bottom section opened) is ignored.
    /// </summary>
    public SectionViewModel? SelectedTopSection
    {
        get => SelectedSection is { Place: SectionPlace.Top } s ? s : null;
        set => Open(value);
    }

    /// <summary>The selection of the bottom list; see <see cref="SelectedTopSection"/>.</summary>
    public SectionViewModel? SelectedBottomSection
    {
        get => SelectedSection is { Place: SectionPlace.Bottom } s ? s : null;
        set => Open(value);
    }

    /// <summary>The title above the page: the section when its tab strip is shown, else the page itself.</summary>
    public string HeaderTitle => SelectedSection is { ShowTabs: true } s ? s.Title : _selectedPage?.Title ?? "";
```

3. Добавить метод:

```csharp
    private void Open(SectionViewModel? section)
    {
        if (section?.EntryTab is { } tab && !ReferenceEquals(tab, _selectedPage))
            _ = NavigateAsync(tab);
    }
```

4. В `SyncVisiblePages()` в конце добавить синхронизацию разделов:

```csharp
        foreach (var section in Sections)
            section.SyncVisibleTabs(p => VisiblePages.Contains(p));
        SyncSections(TopSections, SectionPlace.Top);
        SyncSections(BottomSections, SectionPlace.Bottom);
        OnPropertyChanged(nameof(HeaderTitle));
```

и метод:

```csharp
    /// <summary>Makes <paramref name="list"/> the sections of <paramref name="place"/> that have a visible tab (the selection stays).</summary>
    private void SyncSections(ObservableCollection<SectionViewModel> list, SectionPlace place)
    {
        var wanted = Sections.Where(s => s.Place == place && s.HasVisibleTabs).ToList();
        foreach (var section in list.Where(s => !wanted.Contains(s)).ToList())
            list.Remove(section);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= list.Count || !ReferenceEquals(list[i], wanted[i]))
                list.Insert(i, wanted[i]);
        }
    }
```

`SyncVisiblePages()` вызывается в конструкторе до того, как созданы `TopSections`/`BottomSections`, если порядок строк другой, — проверить, что в конструкторе `Sections`, `TopSections`, `BottomSections` создаются **до** первого вызова `SyncVisiblePages()`.

5. В `NavigateAsync` заменить

```csharp
        var previous = _selectedPage;
        if (!Set(ref _selectedPage, page, nameof(SelectedPage)))
            return;
```

на

```csharp
        var previous = _selectedPage;
        if (ReferenceEquals(previous, page))
            return;
        _selectedPage = page;
        SelectedSection?.Remember(page);
        // The section first: the tab strip gets the tabs of the new section, then its selection (SelectedPage) is one of them.
        OnPropertyChanged(nameof(SelectedSection));
        OnPropertyChanged(nameof(SelectedTopSection));
        OnPropertyChanged(nameof(SelectedBottomSection));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(SelectedPage));
```

6. Комментарий над `Pages` дополнить одной фразой: «The sidebar shows <see cref="Sections"/>; a page is a tab of one of them.»

- [ ] **Step 4: `Profile` в простом режиме**

В `src/PcCleaner/Core/UiModes.cs` заменить набор простых страниц на

```csharp
    public static readonly IReadOnlyList<string> SimplePageKeys = new[] { "Dashboard", "Cleaner", "Programs", "Profile", "Settings" };
```

(сохранить нынешний тип и модификаторы поля — если это не `IReadOnlyList<string>`, а массив или `HashSet`, поменять только содержимое).

В `tests/PcCleaner.Tests/Core/ModesTests.cs`, класс `ModesRulesTests`:
- массив `AllKeys` заменить на порядок меню: `"Dashboard", "Cleaner", "Hardware", "Optimizer", "Processes", "Startup", "Programs", "Disk", "Security", "Network", "Profile", "Settings"`;
- тест `SimpleMode_ShowsDashboardCleanerProgramsAndSettings_InThatOrder` переименовать в `SimpleMode_ShowsDashboardCleanerProgramsProfileAndSettings_InThatOrder` и ожидать `new[] { "Dashboard", "Cleaner", "Programs", "Profile", "Settings" }`.

Остальные тесты `ModesShellTests` (десять страниц без `Hardware` и `Profile`) остаются как есть и должны проходить: раздел без найденных страниц просто не создаётся.

- [ ] **Step 5: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~ShellSectionsTests|FullyQualifiedName~ModesRulesTests|FullyQualifiedName~ModesShellTests|FullyQualifiedName~ShellMenuTests"`
Expected: PASS.

- [ ] **Step 6: Все тесты**

Run: `dotnet test tests/PcCleaner.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Menu: the shell shows sections and opens a page key on its section and tab

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Боковая панель и полоса вкладок в окне

**Files:**
- Modify: `src/PcCleaner/MainWindow.xaml` (боковая панель — `ListBox ItemsSource="{Binding VisiblePages}"`; шапка — `TextBlock Style="{StaticResource H1}"`), `src/PcCleaner/Themes/Theme.xaml` (после стиля `NavItem`)

**Interfaces:**
- Consumes: `TopSections`, `BottomSections`, `SelectedTopSection`, `SelectedBottomSection`, `SelectedSection.VisibleTabs`, `SelectedSection.ShowTabs`, `SelectedPage`, `HeaderTitle` (Task 3).

- [ ] **Step 1: Стиль вкладки полосы**

В `src/PcCleaner/Themes/Theme.xaml` сразу после стиля `NavItem` добавить:

```xml
    <!-- A tab of the strip at the top of a section (MainWindow): the look of a TabItem for a ListBox item. -->
    <Style x:Key="TabStripItem" TargetType="ListBoxItem">
        <Setter Property="Foreground" Value="{StaticResource MutedBrush}" />
        <Setter Property="FocusVisualStyle" Value="{x:Null}" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ListBoxItem">
                    <Border x:Name="Bd" Background="Transparent" CornerRadius="6" Padding="14,6" Margin="0,0,4,0">
                        <ContentPresenter />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Foreground" Value="{StaticResource TextBrush}" />
                        </Trigger>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True">
                            <Setter Property="Foreground" Value="{StaticResource TextBrush}" />
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{StaticResource Surface3Brush}" />
                            <Setter Property="Foreground" Value="{StaticResource TextBrush}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

- [ ] **Step 2: Два списка разделов**

В `src/PcCleaner/MainWindow.xaml` заменить комментарий `<!-- The pages of the current mode (simple: four; expert: all). -->` и следующий за ним `ListBox` (до `</ListBox>` включительно) на:

```xml
                    <!-- Профиль and Настройки: above the admin card (docked to the bottom after it, so it sits above). -->
                    <ListBox DockPanel.Dock="Bottom" ItemsSource="{Binding BottomSections}"
                             SelectedItem="{Binding SelectedBottomSection, Mode=TwoWay}"
                             ItemContainerStyle="{StaticResource NavItem}" ItemTemplate="{StaticResource SectionNavTemplate}" />
                    <Border DockPanel.Dock="Bottom" Height="1" Margin="20,6" Background="{StaticResource LineBrush}" />

                    <!-- The sections of the current mode (a section is shown while it has a visible tab). -->
                    <ListBox ItemsSource="{Binding TopSections}" SelectedItem="{Binding SelectedTopSection, Mode=TwoWay}"
                             ItemContainerStyle="{StaticResource NavItem}" ItemTemplate="{StaticResource SectionNavTemplate}" />
```

и в `<Window.Resources>` (после стиля `WelcomeLanguage`) добавить общий шаблон пункта:

```xml
        <!-- A sidebar item: the icon and the title of a section (Core/SectionViewModel). -->
        <DataTemplate x:Key="SectionNavTemplate">
            <StackPanel Orientation="Horizontal">
                <TextBlock Style="{StaticResource Icon}" Text="{Binding IconGlyph}"
                           Foreground="{Binding Foreground, RelativeSource={RelativeSource AncestorType=ListBoxItem}}" />
                <TextBlock Text="{Binding Title}" Margin="13,0,0,0" FontSize="13.5" VerticalAlignment="Center"
                           Foreground="{Binding Foreground, RelativeSource={RelativeSource AncestorType=ListBoxItem}}" />
            </StackPanel>
        </DataTemplate>
```

Порядок элементов в `DockPanel` боковой панели после правки: логотип (`Top`), карточка администратора (`Bottom`), нижний список (`Bottom`), линия (`Bottom`), верхний список (заполняет остаток).

- [ ] **Step 3: Заголовок и полоса вкладок**

В шапке (`<StackPanel Margin="32,24,32,10">`) заменить

```xml
                    <TextBlock Style="{StaticResource H1}" Text="{Binding SelectedPage.Title}" />
```

на

```xml
                    <TextBlock Style="{StaticResource H1}" Text="{Binding HeaderTitle}" />
                    <!-- The tabs of the section; a section with one visible tab has no strip. -->
                    <ListBox Margin="0,10,0,2" ItemsSource="{Binding SelectedSection.VisibleTabs}"
                             SelectedItem="{Binding SelectedPage, Mode=TwoWay}" ItemContainerStyle="{StaticResource TabStripItem}"
                             Visibility="{Binding SelectedSection.ShowTabs, Converter={StaticResource BoolToVis}}">
                        <ListBox.ItemsPanel>
                            <ItemsPanelTemplate>
                                <StackPanel Orientation="Horizontal" />
                            </ItemsPanelTemplate>
                        </ListBox.ItemsPanel>
                        <ListBox.ItemTemplate>
                            <DataTemplate>
                                <TextBlock Text="{Binding Title}" FontSize="13"
                                           Foreground="{Binding Foreground, RelativeSource={RelativeSource AncestorType=ListBoxItem}}" />
                            </DataTemplate>
                        </ListBox.ItemTemplate>
                    </ListBox>
```

Подзаголовок (`SelectedPage.Subtitle`) остаётся под полосой — он описывает открытую вкладку.

- [ ] **Step 4: Сборка и тесты**

Run: `dotnet build src/PcCleaner -c Debug` → Expected: `Build succeeded`, 0 errors.
Run: `dotnet test tests/PcCleaner.Tests` → Expected: PASS.

- [ ] **Step 5: Скриншоты для проверки глазами**

```powershell
$out = "$env:TEMP\menu-shots-task4"
& src\PcCleaner\bin\Debug\net7.0-windows\WaterCa7sPcCleaner.exe --screenshots $out --no-scan --page Dashboard,Disk,Settings --mode expert
```

(Команда ждёт окончания: в PowerShell запускать через `Start-Process -Wait`, если вызов возвращается сразу.) Открыть PNG и проверить:
- слева пять разделов сверху, линия, «Профиль»/«Настройки» внизу над карточкой прав (Профиля ещё нет — внизу только «Настройки»);
- на снимке «Диски» заголовок «Система», полоса «Процессы · Автозагрузка · Программы · Диски», выделена «Диски»; в боковой панели выделена «Система»;
- на снимке «Настройки» в верхнем списке ничего не выделено;
- нижняя строка — только `v1.5.0` справа.

Если в полосе вкладок не выделена открытая вкладка — проверить порядок уведомлений в `NavigateAsync` (Task 3, Step 3.5). Если иконка раздела «Компьютер» (`\uE977`) или «Система» (`\uE770`) рисуется пустым квадратом — заменить на `\uE7F4` / `\uE7F8` и переснять.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Menu: sidebar of sections and the tab strip in the window

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Вкладка «Комплектующие»

**Files:**
- Create: `src/PcCleaner/Modules/Optimizer/HardwareViewModel.cs`, `src/PcCleaner/Modules/Optimizer/HardwareView.xaml`, `src/PcCleaner/Modules/Optimizer/HardwareView.xaml.cs`
- Modify: `src/PcCleaner/Modules/Optimizer/OptimizerView.xaml` (блок `<!-- ===== Hardware ===== -->`, строки ~427–448, и ресурсы карточки), `src/PcCleaner/Modules/Optimizer/OptimizerViewModel.cs:66` (подзаголовок), `src/PcCleaner/Core/PageViewModel.cs` (помощник для тестов), `src/PcCleaner/MainViewModel.cs` (`CreatePages`), `src/PcCleaner/App.xaml`, `src/PcCleaner/Core/Localization/en.json`
- Test: `tests/PcCleaner.Tests/Optimizer/HardwareViewModelTests.cs`, `tests/PcCleaner.Tests/Core/ShellEnglishTests.cs`

**Interfaces:**
- Consumes: `OptimizerViewModel` — `HardwareCards`, `HasData`, `ShowPlaceholder`, `ShowLoadError`, `LoadError`, `RetryCommand`, `RefreshCommand`, `CardActionCommand`, `IsBusy`, `BusyText`, `Progress`, `OnNavigatedToAsync()`, `PreviewScanAsync(ct)`.
- Produces: `public sealed class HardwareViewModel : PageViewModel` (`Key` = `"Hardware"`), конструктор `(OptimizerViewModel optimizer)`, свойства `Optimizer`, `CardActionCommand`.

- [ ] **Step 1: Как устроены тесты Ускорения**

Открыть `tests/PcCleaner.Tests/Optimizer/` и найти, как тесты создают `OptimizerViewModel` без настоящей системы (поддельный `OptimizerService` или его зависимости). Если готового способа нет — тест ниже строится на `new OptimizerViewModel(AppServices.Optimizer)` **без** вызова загрузки (только проверки, которые не трогают систему).

- [ ] **Step 2: Падающие тесты**

Create `tests/PcCleaner.Tests/Optimizer/HardwareViewModelTests.cs`:

```csharp
using PcCleaner.Core;
using PcCleaner.Modules.Optimizer;
using Xunit;

namespace PcCleaner.Tests.Optimizer;

public class HardwareViewModelTests
{
    public HardwareViewModelTests() => TestEnvironment.Init();

    [Fact]
    public void TheTab_IsAPageOfItsOwn_OverTheSameOptimizer()
    {
        var optimizer = new OptimizerViewModel(AppServices.Optimizer);
        var hardware = new HardwareViewModel(optimizer);

        Assert.Equal("Hardware", hardware.Key);
        Assert.Equal("Комплектующие", hardware.Title);
        Assert.Same(optimizer, hardware.Optimizer);
        Assert.Same(optimizer.CardActionCommand, hardware.CardActionCommand);
    }

    [Fact]
    public void TheBusyStrip_FollowsTheOptimizer()
    {
        var optimizer = new OptimizerViewModel(AppServices.Optimizer);
        var hardware = new HardwareViewModel(optimizer);

        optimizer.SetBusyForTest(true, "Собираем сведения о компьютере…", 40);
        Assert.True(hardware.IsBusy);
        Assert.Equal("Собираем сведения о компьютере…", hardware.BusyText);
        Assert.Equal(40, hardware.Progress);

        optimizer.SetBusyForTest(false, "", -1);
        Assert.False(hardware.IsBusy);
    }

    [Fact]
    public void TheOptimizerPage_NoLongerSpeaksOfTheHardwareInItsSubtitle()
        => Assert.Equal("Честные советы, как сделать компьютер быстрее", new OptimizerViewModel(AppServices.Optimizer).Subtitle);
}
```

Сеттеры состояния «занято» у `PageViewModel` защищённые, а `OptimizerViewModel` — `sealed`, поэтому тестам нужен внутренний помощник (`InternalsVisibleTo` уже есть). В `src/PcCleaner/Core/PageViewModel.cs` добавить в конец класса:

```csharp
    /// <summary>Tests: puts the page into a busy state without running work (the setters are protected).</summary>
    internal void SetBusyForTest(bool busy, string text, double progress)
    {
        BusyText = text;
        Progress = progress;
        IsBusy = busy;
    }
```

- [ ] **Step 3: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~HardwareViewModelTests"`
Expected: ошибка сборки (`HardwareViewModel` не найден).

- [ ] **Step 4: `HardwareViewModel`**

Create `src/PcCleaner/Modules/Optimizer/HardwareViewModel.cs`:

```csharp
using System.ComponentModel;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Optimizer;

/// <summary>
/// «Комплектующие»: the hardware cards of <see cref="OptimizerViewModel"/> on a tab of their own (menu «Компьютер»). One scan
/// serves both tabs: opening either starts or waits for the same load, and this page mirrors the optimizer's busy strip.
/// Its own Cancel stays disabled (the work belongs to the optimizer); the strip on «Ускорение» can cancel it.
/// </summary>
public sealed class HardwareViewModel : PageViewModel
{
    public HardwareViewModel(OptimizerViewModel optimizer)
    {
        Optimizer = optimizer;
        optimizer.PropertyChanged += OnOptimizerChanged;
    }

    public OptimizerViewModel Optimizer { get; }

    public override string Title => T._("Комплектующие");

    public override string Subtitle => T._("Процессор, память, видеокарта, диски и мониторы вашего компьютера");

    public override string IconGlyph => "\uE950";

    /// <summary>The «Подробнее» link of a card (the card template binds to the page's DataContext).</summary>
    public AsyncRelayCommand CardActionCommand => Optimizer.CardActionCommand;

    public override Task OnNavigatedToAsync() => Optimizer.OnNavigatedToAsync();

    /// <summary>Read-only: the optimizer's scan, unless it has already run (the «Ускорение» tab runs its own preview).</summary>
    public override Task PreviewScanAsync(CancellationToken ct) => Optimizer.HasData ? Task.CompletedTask : Optimizer.PreviewScanAsync(ct);

    private void OnOptimizerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IsBusy):
                IsBusy = Optimizer.IsBusy;
                break;
            case nameof(BusyText):
                BusyText = Optimizer.BusyText;
                break;
            case nameof(Progress):
                Progress = Optimizer.Progress;
                break;
        }
    }
}
```

Если тип `CardActionCommand` в `OptimizerViewModel` не `AsyncRelayCommand` — взять его настоящий тип (строка с `public ... CardActionCommand { get; }`).

- [ ] **Step 5: Подзаголовок Ускорения**

В `src/PcCleaner/Modules/Optimizer/OptimizerViewModel.cs` заменить
`T._("Ваше железо и честные советы, как сделать компьютер быстрее")` на `T._("Честные советы, как сделать компьютер быстрее")`.

- [ ] **Step 6: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~HardwareViewModelTests"`
Expected: PASS.

- [ ] **Step 7: Вид «Комплектующих»**

Create `src/PcCleaner/Modules/Optimizer/HardwareView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace PcCleaner.Modules.Optimizer;

public partial class HardwareView : UserControl
{
    public HardwareView()
    {
        InitializeComponent();
    }
}
```

Create `src/PcCleaner/Modules/Optimizer/HardwareView.xaml` — перенос, а не новая вёрстка:

```xml
<UserControl x:Class="PcCleaner.Modules.Optimizer.HardwareView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:local="clr-namespace:PcCleaner.Modules.Optimizer"
             xmlns:loc="clr-namespace:PcCleaner.Core.Localization"
             mc:Ignorable="d"
             d:DataContext="{d:DesignInstance Type=local:HardwareViewModel}"
             d:DesignWidth="1000" d:DesignHeight="700">
    <UserControl.Resources>
        <!-- MOVE here from OptimizerView.xaml, unchanged: the converters ToneBrush, ToneSoft, BalancedColumns,
             the styles ToneBadgeText and HardwareCardStyle, the DataTemplate HardwareCardTemplate. -->
    </UserControl.Resources>

    <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Focusable="False">
        <!-- The optimizer's state: the bindings below are the ones of OptimizerView. -->
        <StackPanel Margin="0,0,10,8" DataContext="{Binding Optimizer}">

            <!-- MOVE/COPY here from OptimizerView.xaml: the «Loading placeholder» Border and the «First load cancelled / failed» Border (copy: Ускорение keeps them too). -->

            <DockPanel Margin="0,4,0,10" Visibility="{Binding HasData, Converter={StaticResource BoolToVis}}">
                <Button DockPanel.Dock="Right" Padding="12,5" VerticalAlignment="Center" Command="{Binding RefreshCommand}"
                        Content="{loc:T 'Обновить'}" />
                <TextBlock Style="{StaticResource H2}" Text="{loc:T 'Ваш компьютер'}" Margin="0" VerticalAlignment="Center" />
                <TextBlock Style="{StaticResource Muted}" Margin="12,3,12,0" VerticalAlignment="Center" TextWrapping="Wrap"
                           Text="{loc:T 'Наведите на карточку, чтобы увидеть подробности; правый клик — скопировать или найти в интернете.'}" />
            </DockPanel>

            <!-- MOVE here from OptimizerView.xaml, unchanged: <ItemsControl x:Name="HardwareGrid" ...> … </ItemsControl>. -->
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

Что именно перенести (комментарии `MOVE`/`COPY` в файле заменить настоящей разметкой, сами комментарии удалить):
1. Из `<UserControl.Resources>` `OptimizerView.xaml` — `ToneBrush`, `ToneSoft`, `BalancedColumns`, `ToneBadgeText`, `HardwareCardStyle`, `HardwareCardTemplate`. После переноса в `OptimizerView.xaml` удалить `HardwareCardStyle`, `HardwareCardTemplate`, `BalancedColumns`; `ToneBrush`, `ToneSoft`, `ToneBadgeText` оставить и там, если они используются в остальной разметке Ускорения (`git grep -n "ToneBrush\|ToneSoft\|ToneBadgeText" -- src/PcCleaner/Modules/Optimizer/OptimizerView.xaml`).
2. Заглушку загрузки и карточку ошибки первой загрузки — **скопировать** (Ускорение их тоже показывает).
3. Блок `<!-- ===== Hardware ===== -->` целиком (заголовок «Ваш компьютер» с подсказкой и `ItemsControl x:Name="HardwareGrid"`) из `OptimizerView.xaml` **удалить**; `ItemsControl` перенести в новый вид, заголовок в новом виде уже написан выше (с кнопкой «Обновить»).

Привязка кнопки карточки `DataContext.CardActionCommand` с `AncestorType=UserControl` остаётся без изменений: у `HardwareView` `DataContext` — `HardwareViewModel`, у которого есть `CardActionCommand`.

- [ ] **Step 8: Шаблон и порядок страниц**

В `src/PcCleaner/App.xaml` после строки шаблона `OptimizerViewModel` добавить:

```xml
            <DataTemplate DataType="{x:Type optimizer:HardwareViewModel}"><optimizer:HardwareView /></DataTemplate>
```

В `src/PcCleaner/MainViewModel.cs` заменить `CreatePages()` на (порядок = порядок меню; строку `SettingsViewModel` оставить такой, как сейчас):

```csharp
    private static IEnumerable<PageViewModel> CreatePages()
    {
        var optimizer = new OptimizerViewModel(AppServices.Optimizer);
        return new PageViewModel[]
        {
            new DashboardViewModel(),
            new CleanerViewModel(AppServices.Cleaner),
            new HardwareViewModel(optimizer),
            optimizer,
            new ProcessesViewModel(AppServices.Processes),
            new StartupViewModel(AppServices.Startup),
            new ProgramsViewModel(AppServices.Programs),
            new DiskViewModel(AppServices.Disk),
            new SecurityViewModel(AppServices.Security),
            new NetworkViewModel(AppServices.Network),
            new SettingsViewModel(),
        };
    }
```

- [ ] **Step 9: `en.json`**

- заменить ключ `"Ваше железо и честные советы, как сделать компьютер быстрее"` на `"Честные советы, как сделать компьютер быстрее": "Honest tips on making your computer faster"`;
- добавить `"Комплектующие": "Components"` и `"Процессор, память, видеокарта, диски и мониторы вашего компьютера": "The processor, memory, graphics card, drives and monitors of your computer"`.

- [ ] **Step 10: Тест английских заголовков**

В `tests/PcCleaner.Tests/Core/ShellEnglishTests.cs`:
- `RealPages()` переписать в порядке меню (`HardwareViewModel` над тем же `OptimizerViewModel`):

```csharp
    private static PageViewModel[] RealPages()
    {
        var optimizer = new OptimizerViewModel(AppServices.Optimizer);
        return new PageViewModel[]
        {
            new DashboardViewModel(),
            new CleanerViewModel(AppServices.Cleaner),
            new HardwareViewModel(optimizer),
            optimizer,
            new ProcessesViewModel(AppServices.Processes),
            new StartupViewModel(AppServices.Startup),
            new ProgramsViewModel(AppServices.Programs),
            new DiskViewModel(AppServices.Disk),
            new SecurityViewModel(AppServices.Security),
            new NetworkViewModel(AppServices.Network),
            new SettingsViewModel(() => new AppSettings(), _ => { }, new ObservableCollection<LogEntry>()),
        };
    }
```

- в `TheSidebarTitles_AreEnglish_AndEverySubtitleHasNoRussianLeft` ожидать
  `{ "Home", "Cleanup", "Components", "Speed up", "Processes", "Startup", "Programs", "Disks", "Security", "Network", "Settings" }`.
- добавить тест названий разделов:

```csharp
    [Fact]
    public void TheMenuSections_AreEnglish()
    {
        var sections = ShellMenu.Build(RealPages());

        using (TestLanguage.English())
            Assert.Equal(new[] { "Home", "Cleanup", "Computer", "System", "Protection", "Settings" }, sections.Select(s => s.Title));
    }
```

- [ ] **Step 11: Все тесты и сборка**

Run: `dotnet build src/PcCleaner -c Debug` → `Build succeeded`.
Run: `dotnet test tests/PcCleaner.Tests` → PASS.

- [ ] **Step 12: Скриншоты**

```powershell
$out = "$env:TEMP\menu-shots-task5"
Start-Process -Wait src\PcCleaner\bin\Debug\net7.0-windows\WaterCa7sPcCleaner.exe -ArgumentList "--screenshots", $out, "--page", "Hardware,Optimizer", "--mode", "expert"
```

Проверить: «Комплектующие» — заголовок «Компьютер», полоса «Комплектующие · Ускорение», карточки железа в сетке, кнопка «Обновить»; «Ускорение» — без карточек железа, рекомендации на месте, подзаголовок новый. В `log.txt` нет `[Error]`.

- [ ] **Step 13: Commit**

```bash
git add -A
git commit -m "Menu: Компьютер → Комплектующие (hardware cards) and Ускорение

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Заготовка «Профиля»

**Files:**
- Create: `src/PcCleaner/Modules/Profile/ProfileViewModel.cs`, `src/PcCleaner/Modules/Profile/ProfileView.xaml`, `src/PcCleaner/Modules/Profile/ProfileView.xaml.cs`
- Modify: `src/PcCleaner/App.xaml`, `src/PcCleaner/MainViewModel.cs` (`CreatePages`), `src/PcCleaner/Core/Localization/en.json`
- Test: `tests/PcCleaner.Tests/Core/ShellEnglishTests.cs`

**Interfaces:**
- Produces: `public sealed class ProfileViewModel : PageViewModel` (`Key` = `"Profile"`), конструктор без параметров. Часть B заменит содержимое (гость / вошёл / компьютеры / промокод / «Написать нам»).

- [ ] **Step 1: Падающий тест**

В `ShellEnglishTests.RealPages()` добавить `new ProfileViewModel(),` перед `SettingsViewModel` (и `using PcCleaner.Modules.Profile;`). В `TheSidebarTitles_AreEnglish_AndEverySubtitleHasNoRussianLeft` ожидать
`{ "Home", "Cleanup", "Components", "Speed up", "Processes", "Startup", "Programs", "Disks", "Security", "Network", "Profile", "Settings" }`,
в `TheMenuSections_AreEnglish` — `{ "Home", "Cleanup", "Computer", "System", "Protection", "Profile", "Settings" }`.

- [ ] **Step 2: Убедиться, что тест падает**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~ShellEnglishTests"`
Expected: ошибка сборки (`ProfileViewModel` не найден).

- [ ] **Step 3: Страница**

Create `src/PcCleaner/Modules/Profile/ProfileViewModel.cs`:

```csharp
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

/// <summary>
/// «Профиль» (the bottom of the sidebar). A placeholder of the new menu: the account, Pro, the computers and the promo code
/// come with the accounts work (spec 2026-10-27, part B). Nothing here touches the system or the network.
/// </summary>
public sealed class ProfileViewModel : PageViewModel
{
    public override string Title => T._("Профиль");

    public override string Subtitle => T._("Аккаунт WaterCa7, Pro и ваши компьютеры");

    public override string IconGlyph => "\uE77B";
}
```

Create `src/PcCleaner/Modules/Profile/ProfileView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace PcCleaner.Modules.Profile;

public partial class ProfileView : UserControl
{
    public ProfileView()
    {
        InitializeComponent();
    }
}
```

Create `src/PcCleaner/Modules/Profile/ProfileView.xaml`:

```xml
<UserControl x:Class="PcCleaner.Modules.Profile.ProfileView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:loc="clr-namespace:PcCleaner.Core.Localization">
    <StackPanel Margin="0,0,10,8">
        <Border Style="{StaticResource Card}" Padding="22,20">
            <StackPanel Orientation="Horizontal">
                <Border Width="44" Height="44" CornerRadius="12" Background="{StaticResource AccentSoftBrush}">
                    <TextBlock Style="{StaticResource Icon}" Text="&#xE77B;" FontSize="22" Foreground="{StaticResource AccentBrush}"
                               HorizontalAlignment="Center" />
                </Border>
                <StackPanel Margin="16,0,0,0" VerticalAlignment="Center">
                    <TextBlock Style="{StaticResource H3}" Text="{loc:T 'Вход в аккаунт появится здесь'}" />
                    <TextBlock Style="{StaticResource Muted}" Margin="0,3,0,0" TextWrapping="Wrap"
                               Text="{loc:T 'Аккаунт WaterCa7 включит Pro на этом компьютере, покажет ваши компьютеры и примет промокод. Без входа программа работает как раньше.'}" />
                </StackPanel>
            </StackPanel>
        </Border>
    </StackPanel>
</UserControl>
```

- [ ] **Step 4: Шаблон и страница в оболочке**

`src/PcCleaner/App.xaml`: в корневой элемент добавить `xmlns:profile="clr-namespace:PcCleaner.Modules.Profile"`, перед шаблоном `SettingsViewModel` —

```xml
            <DataTemplate DataType="{x:Type profile:ProfileViewModel}"><profile:ProfileView /></DataTemplate>
```

`src/PcCleaner/MainViewModel.cs`, `CreatePages()`: `new ProfileViewModel(),` перед `new SettingsViewModel(),` (+ `using PcCleaner.Modules.Profile;`).

- [ ] **Step 5: `en.json`**

```json
  "Аккаунт WaterCa7 включит Pro на этом компьютере, покажет ваши компьютеры и примет промокод. Без входа программа работает как раньше.": "A WaterCa7 account will turn on Pro on this computer, show your computers and take a promo code. Without signing in the program works as before.",
  "Аккаунт WaterCa7, Pro и ваши компьютеры": "Your WaterCa7 account, Pro and your computers",
  "Вход в аккаунт появится здесь": "Signing in to your account will be here",
  "Профиль": "Profile",
```

(не дублировать уже существующие ключи).

- [ ] **Step 6: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Menu: Профиль at the bottom of the sidebar (placeholder for the accounts part)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Скриншоты по разделам и вкладкам

**Files:**
- Modify: `src/PcCleaner/Core/ScreenshotRunner.cs`, `src/PcCleaner/App.xaml.cs:16` (комментарий с форматом командной строки)
- Test: `tests/PcCleaner.Tests/Core/ModesTests.cs` (три теста `Screenshots_*` в `ModesRulesTests`)

**Interfaces:**
- Consumes: `MainViewModel.Sections`, `SectionViewModel.Key`, `Contains`.
- Produces:
  - `internal static List<(int Number, PageViewModel Page)> PagesToShoot(IReadOnlyList<PageViewModel> all, IEnumerable<PageViewModel> visible, IReadOnlyList<SectionViewModel> sections, string[]? requests)`;
  - `internal static List<string> HiddenRequested(IReadOnlyList<PageViewModel> all, IEnumerable<PageViewModel> visible, IReadOnlyList<SectionViewModel> sections, string[]? requests)`;
  - `internal static string FileName(int number, PageViewModel page, IReadOnlyList<SectionViewModel> sections)` — `NN-Key.png` для раздела из одной страницы с тем же ключом, иначе `NN-Раздел-Вкладка.png` (`03-Computer-Hardware.png`).

Запрос `--page` — список через запятую; каждый элемент — ключ страницы (`Disk`), ключ раздела (`System` — все видимые вкладки раздела) или `Раздел/Вкладка` (`System/Disk`). Регистр не важен.

- [ ] **Step 1: Падающие тесты**

В `ModesRulesTests` заменить массив `Ten` и три теста `Screenshots_*` на:

```csharp
    private static List<FakePage> Pages(params string[] keys) => keys.Select(k => new FakePage(k)).ToList();

    private static readonly string[] Twelve = ShellMenuTests.AllKeys;

    [Fact]
    public void Screenshots_OfExpertMode_ShootEveryTab_NumberedByPlace()
    {
        var all = Pages(Twelve);
        var sections = ShellMenu.Build(all);

        var shots = ScreenshotRunner.PagesToShoot(all, all, sections, null);

        Assert.Equal(Enumerable.Range(1, 12), shots.Select(s => s.Number));
        Assert.Equal(Twelve, shots.Select(s => s.Page.Key));
    }

    [Fact]
    public void Screenshots_AreNamedBySectionAndTab_AndASingleTabSectionByItsKey()
    {
        var all = Pages(Twelve);
        var sections = ShellMenu.Build(all);

        Assert.Equal("01-Dashboard.png", ScreenshotRunner.FileName(1, all[0], sections));
        Assert.Equal("03-Computer-Hardware.png", ScreenshotRunner.FileName(3, all[2], sections));
        Assert.Equal("08-System-Disk.png", ScreenshotRunner.FileName(8, all[7], sections));
        Assert.Equal("12-Settings.png", ScreenshotRunner.FileName(12, all[11], sections));
    }

    [Fact]
    public void Screenshots_OfSimpleMode_SkipHiddenTabs_ButKeepTheNumbersOfTheFullList()
    {
        var all = Pages(Twelve);
        var visible = all.Where(p => UiModes.IsVisible(UiMode.Simple, p.Key)).ToList();

        var shots = ScreenshotRunner.PagesToShoot(all, visible, ShellMenu.Build(all), null);

        Assert.Equal(new[] { 1, 2, 7, 11, 12 }, shots.Select(s => s.Number));
        Assert.Equal(new[] { "Dashboard", "Cleaner", "Programs", "Profile", "Settings" }, shots.Select(s => s.Page.Key));
    }

    [Theory]
    [InlineData("System", new[] { "Processes", "Startup", "Programs", "Disk" })]
    [InlineData("system/disk", new[] { "Disk" })]
    [InlineData("Disk", new[] { "Disk" })]
    [InlineData("Computer/Optimizer,Settings", new[] { "Optimizer", "Settings" })]
    [InlineData("System/Network", new string[0])] // Сеть is a tab of Защита, not of Система
    public void Screenshots_ARequest_NamesAPage_ASection_OrASectionTab(string request, string[] expected)
    {
        var all = Pages(Twelve);

        var shots = ScreenshotRunner.PagesToShoot(all, all, ShellMenu.Build(all), request.Split(','));

        Assert.Equal(expected, shots.Select(s => s.Page.Key));
    }

    [Fact]
    public void Screenshots_WithARequest_ShootOnlyTheVisibleTabsAmongThem_AndNameTheHiddenOnes()
    {
        var all = Pages(Twelve);
        var visible = all.Where(p => UiModes.IsVisible(UiMode.Simple, p.Key)).ToList();
        var sections = ShellMenu.Build(all);
        var requested = new[] { "settings", "Protection/Network", "Computer", "Dashboard", "Nowhere" };

        var shots = ScreenshotRunner.PagesToShoot(all, visible, sections, requested);
        var hidden = ScreenshotRunner.HiddenRequested(all, visible, sections, requested);

        Assert.Equal(new[] { (1, "Dashboard"), (12, "Settings") }, shots.Select(s => (s.Number, s.Page.Key)));
        Assert.Equal(new[] { "Protection/Network", "Computer" }, hidden); // an unknown key is not "hidden"
    }
```

Если в файле метод `Pages(...)` уже объявлен (он объявлен в секции «screenshot mode») — не дублировать, а оставить существующий.

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~ModesRulesTests"`
Expected: ошибка сборки (нет перегрузок с `sections`, нет `FileName`).

- [ ] **Step 3: `ScreenshotRunner`**

В `src/PcCleaner/Core/ScreenshotRunner.cs`:

1. Методы `PagesToShoot`, `HiddenRequested`, `IsNamed` заменить на:

```csharp
    /// <summary>
    /// The tabs to photograph: the visible ones of the current mode (the simple mode hides most), only those named in
    /// <paramref name="requests"/> when it is given (a page key, a section key or «Section/Tab»). <c>Number</c> is the place
    /// in <paramref name="all"/> (1-based), so a tab keeps its file name in every mode and the two modes compare by name.
    /// </summary>
    internal static List<(int Number, PageViewModel Page)> PagesToShoot(IReadOnlyList<PageViewModel> all, IEnumerable<PageViewModel> visible,
        IReadOnlyList<SectionViewModel> sections, string[]? requests)
    {
        var shown = visible.ToList();
        return all
            .Select((page, index) => (Number: index + 1, Page: page))
            .Where(p => shown.Contains(p.Page) &&
                        (requests is not { Length: > 0 } || requests.Any(r => Matches(r, SectionOf(sections, p.Page), p.Page))))
            .ToList();
    }

    /// <summary>The requests (<c>--page</c>) that name an existing tab or section of which nothing is visible in this mode.</summary>
    internal static List<string> HiddenRequested(IReadOnlyList<PageViewModel> all, IEnumerable<PageViewModel> visible,
        IReadOnlyList<SectionViewModel> sections, string[]? requests)
    {
        var shown = visible.ToList();
        return (requests ?? Array.Empty<string>())
            .Where(r => all.Any(p => Matches(r, SectionOf(sections, p), p)) &&
                        !shown.Any(p => Matches(r, SectionOf(sections, p), p)))
            .ToList();
    }

    /// <summary>NN-Key.png for a section of one page with the same key (01-Dashboard.png), else NN-Section-Tab.png (08-System-Disk.png).</summary>
    internal static string FileName(int number, PageViewModel page, IReadOnlyList<SectionViewModel> sections)
        => SectionOf(sections, page) is { } section && !string.Equals(section.Key, page.Key, StringComparison.OrdinalIgnoreCase)
            ? $"{number:00}-{section.Key}-{page.Key}.png"
            : $"{number:00}-{page.Key}.png";

    private static SectionViewModel? SectionOf(IReadOnlyList<SectionViewModel> sections, PageViewModel page)
        => sections.FirstOrDefault(s => s.Contains(page));

    private static bool Matches(string request, SectionViewModel? section, PageViewModel page)
    {
        var parts = request.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
            return section is not null && Same(parts[0], section.Key) && Same(parts[1], page.Key);
        return Same(request.Trim(), page.Key) || (section is not null && Same(request.Trim(), section.Key));
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
```

2. В `RunAsync`: параметр `string[]? pageKeys` переименовать в `string[]? requests`; вызовы заменить на
`HiddenRequested(allPages, vm.VisiblePages, vm.Sections, requests)` и `PagesToShoot(allPages, vm.VisiblePages, vm.Sections, requests)`; имя файла —
`var file = Path.Combine(outDir, FileName(index, page, vm.Sections));`.

3. Комментарий класса: строку формата заменить на
`WaterCa7sPcCleaner.exe --screenshots <dir> [--page Dashboard,System,Computer/Hardware] [--no-scan] [--mode simple|expert] [--lang ru|en] [--welcome]`
и фразу про файлы — на «saves NN-Key.png (a section of one page) or NN-Section-Tab.png and log.txt».

4. В `src/PcCleaner/App.xaml.cs:16` комментарий `--page Key1,Key2` заменить на `--page Key,Section,Section/Tab`.

- [ ] **Step 4: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~ModesRulesTests"`
Expected: PASS.

- [ ] **Step 5: Все тесты**

Run: `dotnet test tests/PcCleaner.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Menu: --page takes a section or Section/Tab; screenshots are named by section and tab

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Полная проверка и документация

**Files:**
- Modify: `docs/ARCHITECTURE.md` (раздел про оболочку/навигацию, если есть)

- [ ] **Step 1: Все тесты, Release-сборка**

Run: `dotnet test tests/PcCleaner.Tests` → PASS, число тестов не меньше, чем до работы.
Run: `dotnet build src/PcCleaner -c Release` → `Build succeeded`, 0 warnings новых по сравнению с `origin/master`.

- [ ] **Step 2: Скриншоты всех вкладок в четырёх вариантах**

```powershell
$exe = "src\PcCleaner\bin\Debug\net7.0-windows\WaterCa7sPcCleaner.exe"
foreach ($mode in "expert","simple") { foreach ($lang in "ru","en") {
  Start-Process -Wait $exe -ArgumentList "--screenshots", "$env:TEMP\menu-shots\$mode-$lang", "--mode", $mode, "--lang", $lang
} }
```

Проверить каждый PNG:
- опытный режим: 12 снимков (`01-Dashboard` … `12-Settings`); простой: 5 (`01`, `02`, `07-System-Programs`, `11-Profile`, `12-Settings`);
- в простом режиме у «Системы» нет полосы вкладок, заголовок — «Программы»;
- английские снимки: «Computer», «System», «Protection», «Components», «Profile»; нигде нет кириллицы в меню, полосе и нижней строке;
- нижняя строка — `v1.5.0` (в режиме скриншотов обновления не проверяются);
- в каждом `log.txt` нет `[Error]` (код выхода 0).

- [ ] **Step 3: Ссылки страниц**

Запустить программу обычным образом (`Start-Process $exe`), вручную пройти: на Главной «Подробнее» у плиток открывает нужный раздел и вкладку; на «Сети» кнопка перехода в «Безопасность» открывает «Защита → Безопасность»; «Открыть в процессах» у соединения — «Система → Процессы»; ссылки «Ещё проверить» в «Ускорении» ведут в свои вкладки. Переключить режим в Настройках на простой и обратно — меню перестраивается сразу, открытая вкладка, если её не стало, сменяется Главной. Ничего не применять и не очищать.

- [ ] **Step 4: Документация**

В `docs/ARCHITECTURE.md` в описании оболочки (найти по словам `MainViewModel` / `Pages` / `sidebar`) добавить абзац:

> Боковое меню — разделы (`Core/SectionViewModel`, таблица в `Core/ShellMenu`). Раздел держит одну или несколько страниц-вкладок; полоса вкладок видна, когда в разделе больше одной видимой вкладки. Навигация идёт по ключу страницы (`INavigator.NavigateTo("Disk")` открывает «Система → Диски»). Простой режим прячет вкладки; раздел без видимых вкладок не показывается. Все страницы остаются в `MainViewModel.Pages`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Menu: architecture notes

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 6: Отчёт владельцу**

Показать владельцу несколько снимков (опытный RU: Главная, Система/Диски, Компьютер/Комплектующие; простой EN: Система/Программы) и ветку `menu-accounts` (не запушена). Дальше — часть B (аккаунты) в этой же ветке.
