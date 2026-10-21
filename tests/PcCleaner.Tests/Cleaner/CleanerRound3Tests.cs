using System.IO;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Xml.Linq;
using PcCleaner.Core;
using PcCleaner.Modules.Cleaner;
using PcCleaner.Modules.Dashboard;
using Xunit;

namespace PcCleaner.Tests.Cleaner;

/// <summary>
/// Round 3 regression tests: the Recycle Bin is emptied only with the size the user has seen (asked again when it grew),
/// and the dashboard's junk fix follows the latest checkbox choice of the Cleaner page.
/// None of them swaps a global handler: the questions go through the injectable ConfirmAgain / Confirm delegates.
/// </summary>
public class CleanerRecycleBinRecheckTests
{
    private const long Mb = 1024L * 1024;

    private static async Task<HealthIssue> AnalyzeBinIssueAsync(CleanerService service, FakeRecycleBin bin, long bytes,
        long items)
    {
        bin.Bytes = bytes;
        bin.Items = items;
        var issues = await service.AnalyzeAsync(CancellationToken.None);
        return Assert.Single(issues, i => i.Title.StartsWith("В корзине"));
    }

    [Theory]
    [InlineData(6, 4)] // one more file deleted into the bin after the check
    [InlineData(6, 3)] // same number of objects, but bigger
    [InlineData(5, 4)] // same size, but more objects (e.g. an empty folder)
    public async Task DashboardBinFix_BinGrewAfterTheCheck_AsksAgainWithTheCurrentSize_AndADeclineEmptiesNothing(int mbNow,
        int itemsNow)
    {
        using var t = new CleanerTestFolder("r3-bin-grew");
        var service = CleanerServiceTests.CreateService(t, out _, out var bin);
        var issue = await AnalyzeBinIssueAsync(service, bin, 5 * Mb, 3);
        var questions = new List<(string Title, string Text)>();
        service.ConfirmAgain = (title, text) =>
        {
            questions.Add((title, text));
            return false;
        };

        bin.Bytes = mbNow * Mb;
        bin.Items = itemsNow;
        var message = await issue.FixAsync!(CancellationToken.None);

        var question = Assert.Single(questions);
        Assert.Equal(CleanerService.RecycleBinGrewTitle, question.Title);
        Assert.Contains($"было показано: {Format.Bytes(5 * Mb)}, {CleanerText.Objects(3)}", question.Text);
        Assert.Contains($"сейчас в корзине: {Format.Bytes(mbNow * Mb)}, {CleanerText.Objects(itemsNow)}", question.Text);
        Assert.Equal(0, bin.EmptyCalls);
        Assert.Contains("ничего не удалено", message);
        Assert.Contains("новые файлы", message);
        Assert.True(HealthScoring.LooksLikeFailure(message), "the dashboard must not mark the issue as fixed");
    }

    [Fact]
    public async Task DashboardBinFix_BinGrew_UserAgreesWithTheNewSize_IsEmptied()
    {
        using var t = new CleanerTestFolder("r3-bin-grew-yes");
        var service = CleanerServiceTests.CreateService(t, out _, out var bin);
        var issue = await AnalyzeBinIssueAsync(service, bin, 5 * Mb, 3);
        var asked = 0;
        service.ConfirmAgain = (_, _) =>
        {
            asked++;
            return true;
        };

        bin.Bytes = 7 * Mb;
        bin.Items = 5;
        var message = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal(1, asked);
        Assert.Equal(1, bin.EmptyCalls);
        Assert.Equal($"Корзина очищена, освобождено {Format.Bytes(7 * Mb)}.", message);
        Assert.False(HealthScoring.LooksLikeFailure(message));
    }

    [Fact]
    public void ByDefault_BothQuestionsGoThroughDialogsConfirm()
    {
        // Checked by identity, not by answering through the dialogs: other test collections temporarily replace the
        // global Dialogs.ConfirmHandler with "Да" while this class runs in parallel.
        using var t = new CleanerTestFolder("r3-bin-default");
        var service = CleanerServiceTests.CreateService(t, out _, out _);
        var confirm = typeof(Dialogs).GetMethod(nameof(Dialogs.Confirm))!;

        Assert.Equal(confirm, service.ConfirmAgain.Method);
        Assert.Null(service.ConfirmAgain.Target);
        Assert.Equal(confirm, new CleanerViewModel(service).Confirm.Method);
    }

    [Theory]
    [InlineData(5, 3)] // unchanged
    [InlineData(2, 1)] // the user restored something in the meantime
    public async Task DashboardBinFix_SameOrSmallerBin_IsEmptiedWithoutAskingAgain(int mbNow, int itemsNow)
    {
        using var t = new CleanerTestFolder("r3-bin-same");
        var service = CleanerServiceTests.CreateService(t, out _, out var bin);
        var issue = await AnalyzeBinIssueAsync(service, bin, 5 * Mb, 3);
        service.ConfirmAgain = (_, _) => throw new InvalidOperationException("must not ask again");

        bin.Bytes = mbNow * Mb;
        bin.Items = itemsNow;
        var message = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal(1, bin.EmptyCalls);
        Assert.StartsWith("Корзина очищена", message);
        Assert.False(HealthScoring.LooksLikeFailure(message));
    }

    [Fact]
    public async Task DashboardBinFix_BinEmptiedByTheUserMeanwhile_SaysSoAndDoesNothing()
    {
        using var t = new CleanerTestFolder("r3-bin-empty");
        var service = CleanerServiceTests.CreateService(t, out _, out var bin);
        var issue = await AnalyzeBinIssueAsync(service, bin, 5 * Mb, 3);

        bin.Bytes = 0;
        bin.Items = 0;
        var message = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal(0, bin.EmptyCalls);
        Assert.Equal("Корзина уже пуста.", message);
        Assert.False(HealthScoring.LooksLikeFailure(message));
    }

    [Fact]
    public async Task BinKeepsGrowingWhileTheUserAnswers_IsCheckedOnceMore()
    {
        using var t = new CleanerTestFolder("r3-bin-keeps-growing");
        var service = CleanerServiceTests.CreateService(t, out _, out var bin);
        bin.Bytes = 2000;
        bin.Items = 2;
        var category = service.LoadCategories().Single(c => c.Kind == CategoryKind.RecycleBin);
        var texts = new List<string>();
        service.ConfirmAgain = (_, text) =>
        {
            texts.Add(text);
            if (texts.Count == 1)
            {
                // Another file lands in the bin while the first question is on the screen.
                bin.Bytes = 9000;
                bin.Items = 4;
                return true;
            }
            return false;
        };

        var result = await service.CleanCategoryAsync(category, new RecycleBinSnapshot(1000, 1), CancellationToken.None);

        Assert.Equal(2, texts.Count);
        Assert.Contains(Format.Bytes(9000), texts[1]);
        Assert.Equal(0, bin.EmptyCalls);
        Assert.True(result.ChangedAfterConfirmation);
        Assert.Equal(0, result.FreedBytes);
    }

    [Fact]
    public async Task BinClean_WithConfirmedSnapshot_ButTheBinCannotBeChecked_EmptiesNothing()
    {
        using var t = new CleanerTestFolder("r3-bin-unknown");
        var service = CleanerServiceTests.CreateService(t, out _, out var bin);
        bin.Bytes = 1000;
        bin.Items = 2;
        bin.QueryFails = true;
        var category = service.LoadCategories().Single(c => c.Kind == CategoryKind.RecycleBin);

        var result = await service.CleanCategoryAsync(category, new RecycleBinSnapshot(1000, 2), CancellationToken.None);

        Assert.Equal(0, bin.EmptyCalls);
        var error = Assert.Single(result.Errors);
        Assert.Contains("ничего не удалено", error);
        Assert.Equal(0, result.FreedBytes);
    }

    [Fact]
    public async Task CleanAsync_BinGrewAndTheUserDeclined_ResultCardSaysSo()
    {
        using var t = new CleanerTestFolder("r3-bin-card");
        var service = CleanerServiceTests.CreateService(t, out _, out var bin);
        service.ConfirmAgain = (_, _) => false;
        bin.Bytes = 3000;
        bin.Items = 3;
        var category = service.LoadCategories().Single(c => c.Kind == CategoryKind.RecycleBin);

        var results = await service.CleanAsync(new[] { category }, null, CancellationToken.None,
            confirmedRecycleBin: new RecycleBinSnapshot(1000, 2));

        var r = Assert.Single(results);
        Assert.Equal(0, bin.EmptyCalls);
        Assert.True(r.ChangedAfterConfirmation);
        Assert.Equal(0, r.FreedBytes);
        Assert.Empty(r.Errors);
        var card = new CleanResultViewModel(results, cancelled: false);
        Assert.True(card.RecycleBinChanged);
        Assert.StartsWith("Корзина не очищена", card.Headline);
        Assert.Contains("Корзина не очищена", card.Hint);
        Assert.Contains("новые файлы", card.Lines.Single().Note);
    }

    [Fact]
    public void ShownRecycleBin_IsWhatTheConfirmationLists()
    {
        using var t = new CleanerTestFolder("r3-bin-shown");
        var service = CleanerServiceTests.CreateService(t, out _, out _);
        var categories = service.LoadCategories();
        var binItem = new CategoryItemViewModel(categories.Single(c => c.Kind == CategoryKind.RecycleBin),
            Array.Empty<string>(), true, false, _ => { });
        binItem.ApplyResult(new CategoryScanResult { CategoryId = CleanerCatalog.RecycleBinId, Bytes = 4096, Files = 2 });
        var temp = new CategoryItemViewModel(categories.Single(c => c.Id == "win.usertemp"), Array.Empty<string>(), true,
            false, _ => { });

        Assert.Equal(new RecycleBinSnapshot(4096, 2), CleanerViewModel.ShownRecycleBin(new[] { temp, binItem }));
        Assert.Null(CleanerViewModel.ShownRecycleBin(new[] { temp }));
    }

    [Theory]
    [InlineData(100, 2, false)]
    [InlineData(50, 1, false)]
    [InlineData(101, 2, true)]
    [InlineData(100, 3, true)]
    [InlineData(10, 3, true)] // more objects, even if smaller: something new was deleted into it
    public void RecycleBinSnapshot_OnlyGrowthCounts(long bytes, long items, bool exceeded)
        => Assert.Equal(exceeded, new RecycleBinSnapshot(100, 2).IsExceededBy(new RecycleBinSnapshot(bytes, items)));

    // ===== The Cleaner page =====

    private static async Task<(CleanerViewModel Vm, CategoryItemViewModel Bin)> BinOnlyPageAsync(CleanerService service)
    {
        var vm = new CleanerViewModel(service);
        await vm.PreviewScanAsync(CancellationToken.None);
        vm.UnselectAllCommand.Execute(null);
        var binItem = vm.Groups.SelectMany(g => g.Items).Single(i => i.IsRecycleBin);
        binItem.IsChecked = true;
        Assert.Equal(1, vm.SelectedCount); // only the (fake) Recycle Bin is cleaned below
        return (vm, binItem);
    }

    [Fact]
    public async Task CleanerPage_ConfirmationShowsTheBinAsItIsNow_NotAsTheAnalysisSawIt()
    {
        using var t = new CleanerTestFolder("r3-vm-bin-fresh");
        var service = CleanerServiceTests.CreateService(t, out _, out var bin);
        bin.Bytes = 4096;
        bin.Items = 1;
        var (vm, binItem) = await BinOnlyPageAsync(service);
        string? confirmation = null;
        vm.Confirm = (_, text) =>
        {
            confirmation = text;
            return false;
        };

        // Something else is deleted into the bin after the analysis.
        bin.Bytes = 9000;
        bin.Items = 2;
        await ((AsyncRelayCommand)vm.CleanCommand).ExecuteAsync();

        Assert.NotNull(confirmation);
        Assert.Contains($"Корзина — {Format.Bytes(9000)} ({CleanerText.Objects(2)})", confirmation);
        Assert.Contains($"сейчас в ней {Format.Bytes(9000)}, {CleanerText.Objects(2)}", confirmation);
        Assert.DoesNotContain(Format.Bytes(4096), confirmation);
        Assert.Equal(0, bin.EmptyCalls); // declined
        Assert.Equal(9000, binItem.Bytes); // the row shows the fresh size too
    }

    [Fact]
    public async Task CleanerPage_BinGrewAfterTheConfirmation_IsAskedAgain_AndEmptiedOnlyWithTheNewSize()
    {
        using var t = new CleanerTestFolder("r3-vm-bin-grew");
        var service = CleanerServiceTests.CreateService(t, out _, out var bin);
        bin.Bytes = 4096;
        bin.Items = 1;
        var (vm, binItem) = await BinOnlyPageAsync(service);
        var secondQuestions = new List<string>();
        service.ConfirmAgain = (_, text) =>
        {
            secondQuestions.Add(text);
            return false;
        };
        vm.Confirm = (_, _) =>
        {
            // A file is deleted into the bin right after the user said "Да".
            bin.Bytes = 20000;
            bin.Items = 3;
            return true;
        };

        await ((AsyncRelayCommand)vm.CleanCommand).ExecuteAsync();

        var question = Assert.Single(secondQuestions);
        Assert.Contains($"было показано: {Format.Bytes(4096)}, {CleanerText.Objects(1)}", question);
        Assert.Contains($"сейчас в корзине: {Format.Bytes(20000)}, {CleanerText.Objects(3)}", question);
        Assert.Equal(0, bin.EmptyCalls);
        Assert.True(vm.LastResult!.RecycleBinChanged);
        Assert.Equal(20000, binItem.Bytes); // re-sized after the attempt

        // Next time the confirmation lists the current size, so the bin is emptied without a second question.
        string? confirmation = null;
        vm.Confirm = (_, text) =>
        {
            confirmation = text;
            return true;
        };
        await ((AsyncRelayCommand)vm.CleanCommand).ExecuteAsync();

        Assert.Contains($"Корзина — {Format.Bytes(20000)}", confirmation);
        Assert.Single(secondQuestions);
        Assert.Equal(1, bin.EmptyCalls);
        Assert.False(vm.LastResult!.RecycleBinChanged);
    }

    [Fact]
    public async Task CleanerPage_BinEmptiedMeanwhile_NothingIsConfirmedOrEmptied()
    {
        using var t = new CleanerTestFolder("r3-vm-bin-empty");
        var service = CleanerServiceTests.CreateService(t, out _, out var bin);
        bin.Bytes = 4096;
        bin.Items = 1;
        var (vm, _) = await BinOnlyPageAsync(service);
        var asked = false;
        vm.Confirm = (_, _) => asked = true;

        bin.Bytes = 0;
        bin.Items = 0;
        await ((AsyncRelayCommand)vm.CleanCommand).ExecuteAsync();

        Assert.False(asked);
        Assert.Equal(0, bin.EmptyCalls);
        Assert.Equal(0, vm.SelectedCount);
    }
}

public class CleanerDashboardJunkFixTests
{
    private const long Mb = 1024L * 1024;

    private static (CleanerService Service, AppSettings Settings, string Big, string Shader) Setup(CleanerTestFolder t,
        AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        var service = CleanerServiceTests.CreateService(t, out var env, out _, settings);
        Directory.CreateDirectory(env.Temp);
        var big = Path.Combine(env.Temp, "leftover.tmp");
        using (var fs = new FileStream(big, FileMode.CreateNew))
            fs.SetLength(120 * Mb);
        CleanerTestFolder.SetAge(big, TimeSpan.FromDays(5));
        var shader = Path.Combine(env.LocalAppData, @"D3DSCache\abc\shader.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(shader)!);
        File.WriteAllBytes(shader, new byte[4096]);
        CleanerTestFolder.SetAge(shader, TimeSpan.FromDays(5));
        return (service, settings, big, shader);
    }

    [Fact]
    public async Task SkipsACategoryUncheckedAfterTheAnalysis_AndSaysSo()
    {
        using var t = new CleanerTestFolder("r3-junk-unchecked");
        var (service, settings, big, shader) = Setup(t);

        var issues = await service.AnalyzeAsync(CancellationToken.None);
        var junk = Assert.Single(issues, i => i.FixAsync is not null && i.AutoFixSafe);
        Assert.Contains("Кэш шейдеров DirectX", junk.Details); // it was part of the confirmed estimate
        Assert.Contains("снимете отметку", junk.Details);
        // After the analysis the user unchecks the shader cache on the Cleaner page (saved to settings at once).
        settings.CleanerDisabledCategories = new List<string> { "app.d3dscache" };
        var message = await junk.FixAsync!(CancellationToken.None);

        Assert.False(File.Exists(big));
        Assert.True(File.Exists(shader), "an unchecked category must not be cleaned by the dashboard fix");
        Assert.StartsWith("Освобождено", message);
        Assert.Contains("Пропущено по вашему выбору", message);
        Assert.Contains("Кэш шейдеров DirectX", message);
        Assert.False(HealthScoring.LooksLikeFailure(message));
    }

    [Fact]
    public async Task EverythingUncheckedAfterTheAnalysis_DeletesNothing()
    {
        using var t = new CleanerTestFolder("r3-junk-all-unchecked");
        var (service, settings, big, shader) = Setup(t);

        var issues = await service.AnalyzeAsync(CancellationToken.None);
        var junk = Assert.Single(issues, i => i.FixAsync is not null && i.AutoFixSafe);
        settings.CleanerDisabledCategories = new List<string> { "win.usertemp", "app.d3dscache" };
        var message = await junk.FixAsync!(CancellationToken.None);

        Assert.True(File.Exists(big));
        Assert.True(File.Exists(shader));
        Assert.StartsWith("Ничего не удалено", message);
        Assert.Contains("Временные файлы пользователя", message);
        Assert.True(HealthScoring.LooksLikeFailure(message), "nothing was cleaned, so the issue stays open");
    }

    [Fact]
    public async Task NeverCleansMoreThanTheConfirmedEstimate_EvenIfACategoryIsCheckedAgainLater()
    {
        using var t = new CleanerTestFolder("r3-junk-rechecked");
        // The shader cache is unchecked when the dashboard runs its check, so the estimate does not include it.
        var (service, settings, big, shader) = Setup(t,
            new AppSettings { CleanerDisabledCategories = new List<string> { "app.d3dscache" } });

        var issues = await service.AnalyzeAsync(CancellationToken.None);
        var junk = Assert.Single(issues, i => i.FixAsync is not null && i.AutoFixSafe);
        Assert.DoesNotContain("Кэш шейдеров DirectX", junk.Details);
        settings.CleanerDisabledCategories = new List<string>(); // checked again on the Cleaner page afterwards
        var message = await junk.FixAsync!(CancellationToken.None);

        Assert.False(File.Exists(big));
        Assert.True(File.Exists(shader), "the confirmed estimate did not include the shader cache");
        Assert.StartsWith("Освобождено", message);
    }
}

public class CleanerKeyboardAndTooltipTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument LoadView()
    {
        const string relative = @"src\PcCleaner\Modules\Cleaner\CleanerView.xaml";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return XDocument.Load(candidate);
        }
        throw new FileNotFoundException("CleanerView.xaml not found above the test folder", relative);
    }

    private static XElement StyleByKey(XDocument view, string key)
        => view.Descendants(P + "Style").Single(s => (string?)s.Attribute(X + "Key") == key);

    private static string? SetterValue(XElement style, string property)
        => style.Elements(P + "Setter").FirstOrDefault(s => (string?)s.Attribute("Property") == property)
            ?.Attribute("Value")?.Value;

    [Fact]
    public void ExpandChevron_IsReachableWithTab_ShowsTheThemeFocusRing_AndTogglesWithEnter()
    {
        var style = StyleByKey(LoadView(), "ExpandToggle");

        Assert.NotEqual("False", SetterValue(style, "Focusable"));
        Assert.Equal("{StaticResource FocusRing}", SetterValue(style, "FocusVisualStyle"));
        Assert.Equal("True", SetterValue(style, "KeyboardNavigation.AcceptsReturn"));
    }

    [Fact]
    public void CategoryRow_ClickToExpandHasKeyboardEquivalents()
    {
        var view = LoadView();
        var row = view.Descendants(P + "DataTemplate").Single(d => (string?)d.Attribute(X + "Key") == "CategoryTemplate");

        // The only mouse-only gesture toggles the details; the same row has the chevron and Right/Left key bindings.
        var mouse = Assert.Single(view.Descendants(P + "MouseBinding"));
        Assert.Equal("{Binding ToggleExpandCommand}", (string?)mouse.Attribute("Command"));
        Assert.Contains(row.Descendants(P + "ToggleButton"), b => (string?)b.Attribute("Style") == "{StaticResource ExpandToggle}"
                                                                 && (string?)b.Attribute("IsChecked") == "{Binding IsExpanded, Mode=TwoWay}");
        var keys = row.Descendants(P + "KeyBinding").ToDictionary(k => (string)k.Attribute("Key")!, k => (string?)k.Attribute("Command"));
        Assert.Equal("{Binding ExpandCommand}", keys["Right"]);
        Assert.Equal("{Binding CollapseCommand}", keys["Left"]);
    }

    [Fact]
    public void DetailsLinks_AndFileRows_ShowKeyboardFocus_AndTheFileMenuOpensFromTheKeyboard()
    {
        var view = LoadView();

        Assert.Equal("{StaticResource FocusRing}", SetterValue(StyleByKey(view, "FocusLink"), "FocusVisualStyle"));
        Assert.DoesNotContain(view.Descendants(P + "Button"), b => (string?)b.Attribute("Style") == "{StaticResource LinkButton}");
        Assert.Equal(2, view.Descendants(P + "Button").Count(b => (string?)b.Attribute("Style") == "{StaticResource FocusLink}"));

        var fileItem = StyleByKey(view, "FileItem");
        // On the row container, so the menu key / Shift+F10 on the focused row opens it.
        Assert.Equal("{DynamicResource FileMenu}", SetterValue(fileItem, "ContextMenu"));
        Assert.Contains(fileItem.Descendants(P + "Trigger"), tr => (string?)tr.Attribute("Property") == "IsKeyboardFocused");
        var menu = view.Descendants(P + "ContextMenu").Single(m => (string?)m.Attribute(X + "Key") == "FileMenu");
        Assert.Equal("False", (string?)menu.Attribute(X + "Shared"));
    }

    [Theory]
    [InlineData("AnalyzeCommand", 2)]
    [InlineData("CleanCommand", 2)]
    [InlineData("SelectRecommendedCommand", 1)]
    [InlineData("UnselectAllCommand", 1)]
    [InlineData("RestartAsAdminCommand", 1)]
    [InlineData("RunDismCommand", 1)]
    [InlineData("LaunchDiskCleanupCommand", 1)]
    public void ButtonsThatCanBeDisabled_StillExplainWhy(string command, int count)
    {
        var buttons = LoadView().Descendants(P + "Button")
            .Where(b => (string?)b.Attribute("Command") == $"{{Binding {command}}}")
            .ToList();

        Assert.Equal(count, buttons.Count);
        foreach (var b in buttons)
        {
            Assert.Equal("True", (string?)b.Attribute("ToolTipService.ShowOnDisabled"));
            Assert.True(b.Attribute("ToolTip") is not null
                        || b.Descendants(P + "Setter").Any(s => (string?)s.Attribute("Property") == "ToolTip"),
                $"{command} has no tooltip");
        }
    }

    [Fact]
    public async Task Tooltips_SayWhyCleaningIsNotAvailable()
    {
        using var t = new CleanerTestFolder("r3-tips");
        var service = CleanerServiceTests.CreateService(t, out var env, out _);
        Directory.CreateDirectory(env.Temp);
        var junk = Path.Combine(env.Temp, "old.tmp");
        File.WriteAllBytes(junk, new byte[3 * 1024 * 1024]);
        CleanerTestFolder.SetAge(junk, TimeSpan.FromDays(4));
        var vm = new CleanerViewModel(service);

        Assert.StartsWith("Сначала выполните анализ", vm.CleanTip);
        Assert.StartsWith("Найти временные файлы", vm.AnalyzeTip);
        Assert.StartsWith("Список категорий ещё не готов", vm.UnselectAllTip);

        await vm.PreviewScanAsync(CancellationToken.None);
        Assert.StartsWith("Удалить файлы в отмеченных категориях", vm.CleanTip);
        Assert.StartsWith("Проверить ещё раз", vm.AnalyzeTip);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.UnselectAllCommand.Execute(null);
        Assert.Contains(nameof(CleanerViewModel.CleanTip), changed);
        Assert.StartsWith("Отметьте в списке ниже", vm.CleanTip);
        Assert.Equal("Снять отметки со всех категорий", vm.UnselectAllTip);
        Assert.True(File.Exists(junk));
    }

    [Fact]
    public void RowCommands_OpenAndCloseTheDetails()
    {
        using var t = new CleanerTestFolder("r3-row-keys");
        var service = CleanerServiceTests.CreateService(t, out _, out _);
        var item = new CategoryItemViewModel(service.LoadCategories().First(), Array.Empty<string>(), true, false, _ => { });

        item.ExpandCommand.Execute(null);
        Assert.True(item.IsExpanded);
        item.ExpandCommand.Execute(null);
        Assert.True(item.IsExpanded); // Right on an open row keeps it open
        item.CollapseCommand.Execute(null);
        Assert.False(item.IsExpanded);
        item.ToggleExpandCommand.Execute(null);
        Assert.True(item.IsExpanded);
    }

    [Fact]
    public void ToggleButtonThatAcceptsReturn_IsToggledByEnter()
    {
        // What the ExpandToggle style relies on: with KeyboardNavigation.AcceptsReturn a ToggleButton reacts to Enter.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var toggle = new ToggleButton();
                KeyboardNavigation.SetAcceptsReturn(toggle, true);
                using var source = new HwndSource(new HwndSourceParameters("PcCleaner.Tests") { Width = 10, Height = 10, WindowStyle = 0 });
                source.RootVisual = toggle;

                toggle.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent,
                });
                Assert.True(toggle.IsChecked);
                toggle.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent,
                });
                Assert.False(toggle.IsChecked);
                source.RootVisual = null;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the UI thread did not finish");
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
