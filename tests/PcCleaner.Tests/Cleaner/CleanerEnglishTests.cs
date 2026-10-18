using System.IO;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Cleaner;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Tests.Core;
using Xunit;

namespace PcCleaner.Tests.Cleaner;

/// <summary>
/// The Cleanup page, its catalog and its dashboard findings in English (the real en.json). Runs alone (the language is
/// process-wide) and always switches back to Russian; the Russian twin of each text is checked outside the English block.
/// </summary>
[Collection(LanguageSequentialCollection.Name)]
public class CleanerEnglishTests
{
    private const long Mb = 1024L * 1024;
    private static readonly Regex Cyrillic = new("[Ѐ-ӿ]");

    public CleanerEnglishTests() => TestEnvironment.Init();

    /// <summary>A Delivery Optimization cache that is only a number (as in CleanerSystemJunkFixTests).</summary>
    private sealed class FakeDeliveryOptimization
    {
        public long Bytes { get; set; } = 600 * Mb;

        public bool CleanFails { get; set; }

        public void Attach(CleanerService service)
        {
            service.QueryDeliveryOptimization = _ => Task.FromResult<(long Bytes, long Files)?>((Bytes, Bytes > 0 ? 10 : 0));
            service.CleanDeliveryOptimizationCache = _ =>
            {
                if (CleanFails)
                    return Task.FromResult(new ProcessResult(1, "", "Access is denied", false));
                Bytes = 0;
                return Task.FromResult(new ProcessResult(0, "OK", "", false));
            };
        }
    }

    private static CleanerService Service(CleanerTestFolder t, bool elevated, FakeDeliveryOptimization delivery, FakeRecycleBin? bin = null)
    {
        var s = new AppSettings();
        var service = new CleanerService(CleanerEnvironment.ForTests(t.Root), null, bin ?? new FakeRecycleBin(), () => elevated, () => s);
        delivery.Attach(service);
        return service;
    }

    private static async Task<HealthIssue> SystemJunkIssueAsync(CleanerService service)
    {
        var issues = await service.AnalyzeAsync(CancellationToken.None);
        return Assert.Single(issues, i => i.Title.EndsWith(" of system junk", StringComparison.Ordinal));
    }

    // ================================================================ catalog

    [Fact]
    public void ACategory_HasAnEnglishNameDescriptionAndNote_AndTheRussianOnesAreUnchanged()
    {
        using var t = new CleanerTestFolder("en-catalog");
        var env = CleanerEnvironment.ForTests(t.Root);

        using (TestLanguage.English())
        {
            var categories = CleanerCatalog.Build(env, minAgeHours: 24).ToDictionary(c => c.Id);
            var temp = categories["win.usertemp"];

            Assert.Equal("User temporary files", temp.Name);
            Assert.Equal("Files that programs and installers left in the temporary folder. Files newer than 24 hours are left alone.", temp.Description);
            Assert.Equal("newer than 24 hours", temp.AgeNote);
            Assert.Equal("Recycle Bin", categories[CleanerCatalog.RecycleBinId].Name);
            Assert.Equal("newer than 30 days", categories["win.logs"].AgeNote);
            Assert.Equal("Files that programs and installers left in the temporary folder.",
                CleanerCatalog.Build(env, minAgeHours: 0).Single(c => c.Id == "win.usertemp").Description);
            Assert.Equal("newer than 1 hour", CleanerCatalog.Build(env, minAgeHours: 1).Single(c => c.Id == "win.usertemp").AgeNote);
        }

        var russian = CleanerCatalog.Build(env, minAgeHours: 24).ToDictionary(c => c.Id);
        Assert.Equal("Временные файлы пользователя", russian["win.usertemp"].Name);
        Assert.Equal("Файлы, которые программы и установщики оставили во временной папке; файлы моложе 24 часов не трогаем.", russian["win.usertemp"].Description);
        Assert.Equal("моложе 24 часов", russian["win.usertemp"].AgeNote);
    }

    [Fact]
    public void NoTextOfTheCatalog_StaysRussian_InEnglish()
    {
        using var t = new CleanerTestFolder("en-catalog-sweep");
        using var _ = TestLanguage.English();

        var categories = CleanerCatalog.Build(CleanerEnvironment.ForTests(t.Root), minAgeHours: 24);

        Assert.NotEmpty(categories);
        foreach (var c in categories)
        {
            foreach (var text in new[] { c.Name, c.Description, c.Hint, c.Warning, c.AgeNote })
                Assert.True(text is null || !Cyrillic.IsMatch(text), $"{c.Id}: {text}");
        }
    }

    [Fact]
    public void TheRootGuardReasons_AreEnglish()
    {
        var guard = RootGuard.ForTests(@"C:\Allowed");

        using (TestLanguage.English())
        {
            Assert.False(guard.IsSafeFolder(@"C:\", out var reason));
            Assert.Equal("this is the root of a drive", reason);
            Assert.False(guard.IsSafeFolder(@"C:\Elsewhere\Folder", out reason));
            Assert.Equal("the folder is outside the places allowed for cleaning", reason);
            Assert.False(guard.IsSafeFile(@"C:\Allowed\file.exe", out reason));
            Assert.Equal("this type of file cannot be deleted directly", reason);
        }

        Assert.False(guard.IsSafeFolder(@"C:\", out var russian));
        Assert.Equal("это корень диска", russian);
    }

    // ================================================================ number agreement

    [Fact]
    public void TheResultPhrases_AgreeWithTheNumber_InEnglishAndRussian()
    {
        using (TestLanguage.English())
        {
            Assert.Equal("deleted 1 file", CleanerText.Deleted(1));
            Assert.Equal("deleted 21 files", CleanerText.Deleted(21));
            Assert.Equal("deleted 1 object", CleanerText.DeletedObjects(1));
            Assert.Equal("skipped 87 in use", CleanerText.SkippedBusy(87));
            Assert.Equal("skipped 5 files without access", CleanerText.SkippedDenied(5));
            Assert.Equal("skipped: 87 in use, 5 without access", CleanerText.Skipped(87, 5));
            Assert.Equal("", CleanerText.Skipped(0, 0));
            Assert.Equal($"Freed {Format.Bytes(3 * Mb)}, deleted 12,345 files, skipped 87 in use",
                CleanerText.ResultLine(3 * Mb, 12345, 87));
            Assert.Equal($"Freed {Format.Bytes(2048)}, deleted 3 files.",
                CleanerService.Summarize(new[] { new CategoryCleanResult { CategoryId = "x", Name = "X", FreedBytes = 2048, DeletedFiles = 3 } }));
        }

        Assert.Equal("удалён 21 файл", CleanerText.Deleted(21));
        Assert.Equal("удалено 3 файла", CleanerText.Deleted(3));
        Assert.Equal($"пропущено: 87 занятых, 5 без доступа", CleanerText.Skipped(87, 5));
    }

    // ================================================================ dashboard findings and their fixes

    [Fact]
    public async Task TheRecycleBinFinding_IsEnglish_AndItsFixToo()
    {
        using var t = new CleanerTestFolder("en-bin");
        var bin = new FakeRecycleBin { Bytes = 9 * Mb, Items = 3 };
        var service = Service(t, elevated: false, new FakeDeliveryOptimization { Bytes = 0 }, bin);
        string title, details, gain, module, result;

        using (TestLanguage.English())
        {
            var issue = Assert.Single(await service.AnalyzeAsync(CancellationToken.None), i => i.Title.EndsWith("in the Recycle Bin", StringComparison.Ordinal));
            title = issue.Title;
            details = issue.Details;
            gain = issue.Gain!;
            module = issue.Module!;
            result = await issue.FixAsync!(CancellationToken.None);
        }

        Assert.Equal("9 MB in the Recycle Bin", title);
        Assert.Equal("Cleanup", module);
        Assert.StartsWith("The Recycle Bin holds 3 objects.", details);
        Assert.Equal("Frees up 9 MB", gain);
        Assert.Equal("The Recycle Bin was emptied, 9 MB freed.", result);
        Assert.DoesNotMatch(Cyrillic, details + gain + result);
    }

    [Fact]
    public async Task TheJunkFinding_IsEnglish_AndTheFixResultIsASuccess()
    {
        using var t = new CleanerTestFolder("en-junk");
        var service = Service(t, elevated: false, new FakeDeliveryOptimization { Bytes = 0 });
        var env = CleanerEnvironment.ForTests(t.Root);
        Directory.CreateDirectory(env.Temp);
        var big = Path.Combine(env.Temp, "big-leftover.tmp");
        using (var fs = new FileStream(big, FileMode.CreateNew))
            fs.SetLength(120 * Mb);
        CleanerTestFolder.SetAge(big, TimeSpan.FromDays(5));

        using (TestLanguage.English())
        {
            var issue = Assert.Single(await service.AnalyzeAsync(CancellationToken.None), i => i.Title.EndsWith(" of junk", StringComparison.Ordinal));

            Assert.Equal($"You can free up {Format.Bytes(120 * Mb)} of junk", issue.Title);
            Assert.StartsWith("Temporary files and cache that are safe to delete (1 category).", issue.Details);
            Assert.Contains("Most of it: User temporary files — ", issue.Details);
            Assert.Equal($"Frees up to {Format.Bytes(120 * Mb)}", issue.Gain);
            Assert.DoesNotMatch(Cyrillic, issue.Title + issue.Details + issue.Gain);

            var text = await issue.FixAsync!(CancellationToken.None);

            Assert.Equal($"Freed {Format.Bytes(120 * Mb)}, deleted 1 file.", text);
            Assert.False(HealthScoring.LooksLikeFailure(text), text);
        }
    }

    [Fact]
    public async Task ThePartialSystemJunkFix_IsASuccess_InEnglishAsInRussian()
    {
        // The same scenario as CleanerSystemJunkFixTests.ACategoryThatFailedNextToOneThatWorked_IsNamed.
        async Task<string> RunAsync()
        {
            using var t = new CleanerTestFolder("en-partial");
            var service = Service(t, elevated: true, new FakeDeliveryOptimization { CleanFails = true });
            t.File(@"Windows\Temp\system.tmp", 1000);
            var issues = await service.AnalyzeAsync(CancellationToken.None);
            var issue = Assert.Single(issues, i => i.Title.Contains("системного мусора", StringComparison.Ordinal) ||
                                                    i.Title.Contains("of system junk", StringComparison.Ordinal));
            return await issue.FixAsync!(CancellationToken.None);
        }

        string english;
        using (TestLanguage.English())
        {
            english = await RunAsync();

            Assert.Equal($"Deleted {Format.Bytes(1000)} of system junk. Could not clean: Delivery Optimization cache.", english);
            Assert.False(HealthScoring.LooksLikeFailure(english), english);
            Assert.False(HealthScoring.LooksLikeAdminRequired(english), english);
        }

        var russian = await RunAsync();
        Assert.Equal($"Удалено {Format.Bytes(1000)} системного мусора. Не удалось очистить: Кэш оптимизации доставки.", russian);
        Assert.False(HealthScoring.LooksLikeFailure(russian));
    }

    [Fact]
    public async Task WhenNothingCouldBeCleaned_TheEnglishFixResultIsAFailure()
    {
        using var t = new CleanerTestFolder("en-failed");
        var service = Service(t, elevated: true, new FakeDeliveryOptimization { CleanFails = true });

        using var _ = TestLanguage.English();
        var issue = await SystemJunkIssueAsync(service);
        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("Could not delete the system junk: Access is denied.", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.False(HealthScoring.LooksLikeAdminRequired(text));
    }

    [Fact]
    public async Task WithoutAdminRights_TheEnglishFixResultNeedsAdministratorRights()
    {
        using var t = new CleanerTestFolder("en-noadmin");
        var service = Service(t, elevated: false, new FakeDeliveryOptimization());

        using var _ = TestLanguage.English();
        var issue = await SystemJunkIssueAsync(service);
        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("Administrator rights are needed: system folders can only be cleaned with them.", text);
        Assert.True(HealthScoring.LooksLikeAdminRequired(text));
        Assert.True(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public async Task ARecycleBinThatGrewAfterTheCheck_GivesAnEnglishFailureResult()
    {
        using var t = new CleanerTestFolder("en-bin-grew");
        var bin = new FakeRecycleBin { Bytes = 9 * Mb, Items = 3 };
        var service = Service(t, elevated: false, new FakeDeliveryOptimization { Bytes = 0 }, bin);
        service.ConfirmAgain = (_, _) => false; // the user says "No" to the second question

        using var _ = TestLanguage.English();
        var issue = Assert.Single(await service.AnalyzeAsync(CancellationToken.None), i => i.Title.EndsWith("in the Recycle Bin", StringComparison.Ordinal));
        bin.Bytes = 20 * Mb;
        bin.Items = 5;
        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.StartsWith("The Recycle Bin was not emptied and nothing was deleted:", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.Equal(0, bin.EmptyCalls);
    }

    // ================================================================ the page

    [Fact]
    public async Task ThePage_SpeaksEnglish_AndItsRussianTextsAreUnchanged()
    {
        using var t = new CleanerTestFolder("en-page");
        var service = CleanerServiceTests.CreateService(t, out var env, out var bin);
        bin.Bytes = 4096;
        bin.Items = 2;
        Directory.CreateDirectory(env.Temp);
        var junk = Path.Combine(env.Temp, "old.tmp");
        File.WriteAllBytes(junk, new byte[(int)(1.5 * Mb)]);
        CleanerTestFolder.SetAge(junk, TimeSpan.FromDays(4));
        string? question = null;

        using (TestLanguage.English())
        {
            var vm = new CleanerViewModel(service) { Confirm = (title, text) => { question = title + "|" + text; return false; } };
            Assert.Equal("Cleanup", vm.Title);
            Assert.Equal("Click “Analyze” to see how much space the junk takes.", vm.SummaryText);

            await vm.PreviewScanAsync(CancellationToken.None);

            Assert.Equal(new[] { "Windows", "Browsers", "Apps and games" }, vm.Groups.Select(g => g.Name));
            Assert.Equal(1, vm.SelectedCount);
            Assert.Equal($"Selected {Format.Bytes(vm.SelectedBytes)} in 1 category", vm.SummaryText);
            var temp = vm.Groups[0].Items.Single(i => i.Id == "win.usertemp");
            Assert.Equal("User temporary files", temp.Name);
            Assert.Equal(Format.Bytes(temp.Bytes), temp.SizeText);
            Assert.Equal("1 file", temp.FilesText);
            Assert.Equal("Empty", vm.Groups[0].Items.Single(i => i.Id == "win.wer").SizeText);
            Assert.Equal("Administrator rights needed", vm.Groups[0].Items.Single(i => i.Id == "win.systemtemp").AdminBadgeText);
            Assert.StartsWith("Checked at ", vm.ScannedAtText);
            Assert.All(vm.Groups, g => Assert.DoesNotMatch(Cyrillic, g.Name + g.Note + g.EmptyText));
            Assert.DoesNotMatch(Cyrillic, vm.AnalyzeTip + vm.CleanTip + vm.SelectRecommendedTip + vm.UnselectAllTip + vm.RestartAsAdminTip + vm.RunDismTip);

            await ((AsyncRelayCommand)vm.CleanCommand).ExecuteAsync();

            Assert.NotNull(question);
            Assert.StartsWith("Cleanup|Unneeded files from the selected categories will be deleted:", question);
            Assert.Contains("•  User temporary files — ", question);
            Assert.Contains("Total: about ", question);
            Assert.EndsWith("Deleted files do not go to the Recycle Bin, so cleaning cannot be undone. Continue?", question);
            Assert.DoesNotMatch(Cyrillic, question!);
            Assert.True(File.Exists(junk));
        }

        var russian = new CleanerViewModel(service);
        Assert.Equal("Очистка", russian.Title);
        Assert.Equal("Нажмите «Анализ», чтобы узнать, сколько места занимает мусор.", russian.SummaryText);
    }

    [Fact]
    public void TheDismTexts_AreEnglish_AndTheBusyTipFollowsTheStatus()
    {
        using (TestLanguage.English())
        {
            Assert.Equal("Running…", CleanerViewModel.DismRunning);
            Assert.Equal(CleanerViewModel.DismBusyTip, CleanerViewModel.BusyTipFor(CleanerViewModel.DismRunning));
            Assert.Equal(CleanerViewModel.BusyTip, CleanerViewModel.BusyTipFor("Windows component cleanup finished."));
            Assert.DoesNotMatch(Cyrillic, CleanerViewModel.BusyTip + CleanerViewModel.DismBusyTip + CleanerViewModel.IdleCleanTip(0, true, 0));
            Assert.Equal("Nothing to delete: there is almost no junk", CleanerViewModel.IdleCleanTip(0, true, 100));
        }

        Assert.Equal("Выполняется…", CleanerViewModel.DismRunning);
    }
}
