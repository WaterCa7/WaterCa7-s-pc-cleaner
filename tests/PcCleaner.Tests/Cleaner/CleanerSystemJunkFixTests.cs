using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Cleaner;
using PcCleaner.Modules.Dashboard;
using Xunit;

namespace PcCleaner.Tests.Cleaner;

/// <summary>
/// The dashboard fix of «Ещё N системного мусора». Everything lives in a fake environment; the Delivery Optimization cache
/// (normally sized and cleaned through PowerShell) is a fake too, so the real one is never touched.
/// </summary>
public class CleanerSystemJunkFixTests
{
    private const long Mb = 1024L * 1024;
    private const string AdminText = "Нужны права администратора: системные папки можно очистить только с ними.";

    /// <summary>A Delivery Optimization cache that is only a number; its size is what makes the issue appear.</summary>
    private sealed class FakeDeliveryOptimization
    {
        public long Bytes { get; set; } = 600 * Mb;

        public int CleanCalls { get; private set; }

        public bool CleanFails { get; set; }

        public void Attach(CleanerService service)
        {
            service.QueryDeliveryOptimization = _ => Task.FromResult<(long Bytes, long Files)?>((Bytes, Bytes > 0 ? 10 : 0));
            service.CleanDeliveryOptimizationCache = _ =>
            {
                CleanCalls++;
                if (CleanFails)
                    return Task.FromResult(new ProcessResult(1, "", "Access is denied", false));
                Bytes = 0;
                return Task.FromResult(new ProcessResult(0, "OK", "", false));
            };
        }
    }

    private static CleanerService Service(CleanerTestFolder t, bool elevated, FakeDeliveryOptimization delivery,
        AppSettings? settings = null)
    {
        var s = settings ?? new AppSettings();
        var service = new CleanerService(CleanerEnvironment.ForTests(t.Root), null, new FakeRecycleBin(), () => elevated, () => s);
        delivery.Attach(service);
        return service;
    }

    private static async Task<HealthIssue> SystemJunkIssueAsync(CleanerService service)
    {
        var issues = await service.AnalyzeAsync(CancellationToken.None);
        return Assert.Single(issues, i => i.Title.StartsWith("Ещё ", StringComparison.Ordinal) &&
                                          i.Title.EndsWith(" системного мусора", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyAdminFolders_KeepsTheAdminFoldersAndEverythingElseOfTheCategory()
    {
        var category = new CleanerCategory
        {
            Id = "test.mixed",
            Group = CleanerCategory.GroupWindows,
            Name = "Тест",
            Description = "Описание.",
            Glyph = "G",
            Roots = new[] { CleanRoot.Folder(@"C:\User"), CleanRoot.Folder(@"C:\System", requiresAdmin: true) },
            DefaultChecked = false,
            Hint = "Подсказка",
            Warning = "Предупреждение",
            Age = AgeRule.Fixed,
            FixedMinAge = TimeSpan.FromDays(30),
            AgeNote = "моложе 30 дней",
            RemoveEmptyDirectories = false,
            ProcessNames = new[] { "prog" },
            IsRunning = true,
            OpenFolder = @"C:\Open",
        };

        var admin = category.OnlyAdminFolders()!;

        Assert.Equal(@"C:\System", Assert.Single(admin.Roots).Path);
        Assert.Equal((category.Id, category.Group, category.Name, category.Description, category.Glyph, category.Kind),
            (admin.Id, admin.Group, admin.Name, admin.Description, admin.Glyph, admin.Kind));
        Assert.Equal((category.DefaultChecked, category.Hint, category.Warning, category.Age, category.FixedMinAge, category.AgeNote),
            (admin.DefaultChecked, admin.Hint, admin.Warning, admin.Age, admin.FixedMinAge, admin.AgeNote));
        Assert.Equal((category.RemoveEmptyDirectories, category.ProcessNames, category.IsRunning, category.OpenFolder),
            (admin.RemoveEmptyDirectories, admin.ProcessNames, admin.IsRunning, admin.OpenFolder));
    }

    [Fact]
    public void OnlyAdminFolders_IsNullWithoutAdminFolders_AndTheSameCategoryWhenAllNeedAdmin()
    {
        using var t = new CleanerTestFolder("sysfix-onlyadmin");
        var categories = CleanerCatalog.Build(CleanerEnvironment.ForTests(t.Root), minAgeHours: 24).ToDictionary(c => c.Id);

        Assert.Null(categories["win.usertemp"].OnlyAdminFolders());
        Assert.Null(categories[CleanerCatalog.RecycleBinId].OnlyAdminFolders());
        Assert.Same(categories["win.systemtemp"], categories["win.systemtemp"].OnlyAdminFolders());
        Assert.Same(categories["win.deliveryopt"], categories["win.deliveryopt"].OnlyAdminFolders());
        Assert.Equal(2, categories["win.wer"].OnlyAdminFolders()!.Roots.Count);
    }

    [Fact]
    public async Task SystemJunkIssue_HasAFixThatIsPartOfFixAll()
    {
        using var t = new CleanerTestFolder("sysfix-issue");
        var service = Service(t, elevated: true, new FakeDeliveryOptimization());

        var issue = await SystemJunkIssueAsync(service);

        Assert.True(issue.CanFix);
        Assert.True(issue.InFixAll);
        Assert.Equal(FixKind.DeletesJunk, issue.FixKind);
        Assert.False(issue.AutoFixSafe);
        Assert.Equal("Cleaner", issue.TargetPageKey);
    }

    [Fact]
    public async Task WithoutAdminRights_TheFixSaysSoAndDeletesNothing()
    {
        using var t = new CleanerTestFolder("sysfix-noadmin");
        var delivery = new FakeDeliveryOptimization();
        var service = Service(t, elevated: false, delivery);
        var systemTemp = t.File(@"Windows\Temp\system.tmp");
        var issue = await SystemJunkIssueAsync(service);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal(AdminText, text);
        Assert.True(HealthScoring.LooksLikeAdminRequired(text));
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.True(File.Exists(systemTemp));
        Assert.Equal(0, delivery.CleanCalls);
    }

    [Fact]
    public async Task WithAdminRights_TheFixCleansTheSystemFoldersOnly()
    {
        using var t = new CleanerTestFolder("sysfix-admin");
        var delivery = new FakeDeliveryOptimization();
        var service = Service(t, elevated: true, delivery);
        var systemTemp = t.File(@"Windows\Temp\system.tmp", 1000);
        var reportQueue = t.File(@"ProgramData\Microsoft\Windows\WER\ReportQueue\crash.wer", 2000);
        // The user's own folders of the same categories belong to «Можно освободить N мусора», not to this fix.
        var userTemp = t.File(@"Users\Test\AppData\Local\Temp\leftover.tmp", 3000);
        var userReports = t.File(@"Users\Test\AppData\Local\Microsoft\Windows\WER\old.wer", 4000);
        var issue = await SystemJunkIssueAsync(service);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal($"Удалено {Format.Bytes(600 * Mb + 3000)} системного мусора.", text);
        Assert.False(HealthScoring.LooksLikeFailure(text));
        Assert.False(File.Exists(systemTemp));
        Assert.False(File.Exists(reportQueue));
        Assert.Equal(1, delivery.CleanCalls);
        Assert.True(File.Exists(userTemp), "the user's temp folder is not system junk");
        Assert.True(File.Exists(userReports), "only the administrator-only folders of a category are cleaned");
    }

    [Fact]
    public async Task SystemJunkThatIsGoneByNow_IsNotAFailureAndStartsNothing()
    {
        using var t = new CleanerTestFolder("sysfix-stale");
        var delivery = new FakeDeliveryOptimization();
        var service = Service(t, elevated: true, delivery);
        var systemTemp = t.File(@"Windows\Temp\system.tmp");
        var issue = await SystemJunkIssueAsync(service);
        File.Delete(systemTemp);
        delivery.Bytes = 0; // cleaned by someone else after the scan

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.StartsWith("Нечего очищать", text);
        Assert.False(HealthScoring.LooksLikeFailure(text));
        Assert.Equal(0, delivery.CleanCalls);
    }

    [Fact]
    public async Task ACategoryTheUserUncheckedAfterTheScan_IsLeftAlone()
    {
        using var t = new CleanerTestFolder("sysfix-unchecked");
        var delivery = new FakeDeliveryOptimization();
        var settings = new AppSettings();
        var service = Service(t, elevated: true, delivery, settings);
        var systemTemp = t.File(@"Windows\Temp\system.tmp", 1000);
        var issue = await SystemJunkIssueAsync(service);
        settings.CleanerDisabledCategories = new List<string> { "win.systemtemp" };

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.True(File.Exists(systemTemp), "the latest choice on the Cleaner page wins");
        Assert.StartsWith($"Удалено {Format.Bytes(600 * Mb)} системного мусора.", text);
        Assert.Contains("Пропущено по вашему выбору", text);
        Assert.Contains("Временные файлы Windows", text);
    }

    [Fact]
    public async Task WhenTheUserUncheckedEveryCategory_NothingIsDeletedAndTheTextSaysWhy()
    {
        using var t = new CleanerTestFolder("sysfix-all-unchecked");
        var delivery = new FakeDeliveryOptimization();
        var settings = new AppSettings();
        var service = Service(t, elevated: true, delivery, settings);
        var issue = await SystemJunkIssueAsync(service);
        settings.CleanerDisabledCategories = new List<string> { "win.deliveryopt" };

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.StartsWith("Ничего не удалено: вы сняли отметку", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.Equal(0, delivery.CleanCalls);
    }

    [Fact]
    public async Task WhenNothingCouldBeCleaned_TheFixReportsAFailure()
    {
        using var t = new CleanerTestFolder("sysfix-failed");
        var delivery = new FakeDeliveryOptimization { CleanFails = true };
        var service = Service(t, elevated: true, delivery);
        var issue = await SystemJunkIssueAsync(service);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.StartsWith("Не удалось удалить системный мусор: ", text);
        Assert.Contains("Access is denied", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.Equal(1, delivery.CleanCalls);
    }

    [Fact]
    public async Task ACategoryThatFailedNextToOneThatWorked_IsNamed()
    {
        using var t = new CleanerTestFolder("sysfix-partial");
        var delivery = new FakeDeliveryOptimization { CleanFails = true };
        var service = Service(t, elevated: true, delivery);
        var systemTemp = t.File(@"Windows\Temp\system.tmp", 1000);
        var issue = await SystemJunkIssueAsync(service);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.False(File.Exists(systemTemp));
        Assert.StartsWith($"Удалено {Format.Bytes(1000)} системного мусора.", text);
        Assert.Contains("Кэш оптимизации доставки", text);
        Assert.False(HealthScoring.LooksLikeFailure(text));
    }
}
