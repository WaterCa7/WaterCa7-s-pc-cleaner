using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using Xunit;
using static PcCleaner.Tests.Dashboard.FakeContributor;

namespace PcCleaner.Tests.Dashboard;

/// <summary>Pure report rendering: HTML (encoding, sections, grouping, no external resources) and plain text.</summary>
public class ReportBuilderTests
{
    public ReportBuilderTests() => TestEnvironment.Init();

    private const string Evil = "<script>alert(\"x\")</script> & 'q'";

    private static HealthIssue NewIssue(string module, IssueSeverity severity, string title, string details = "Описание",
        long reclaimable = 0, string? gain = null, bool fixable = false)
        => new()
        {
            Module = module,
            Title = title,
            Details = details,
            Severity = severity,
            Gain = gain,
            ReclaimableBytes = reclaimable,
            FixAsync = fixable ? _ => Task.FromResult("Готово.") : null,
        };

    private static DashboardReport SampleReport(IEnumerable<HealthIssue>? issues = null, string computerName = "WATERCAT-PC")
    {
        var list = (issues ?? new[]
        {
            NewIssue("Очистка", IssueSeverity.Info, "Корзина не пуста", reclaimable: 1024, fixable: true),
            NewIssue("Безопасность", IssueSeverity.Critical, "Антивирус выключен", "Защита в реальном времени отключена."),
            NewIssue("Диски", IssueSeverity.Medium, "Мало места на диске D:", gain: "Освободите ≈ 10 ГБ"),
            NewIssue("Сеть", IssueSeverity.Low, "Высокая задержка до DNS"),
        }).ToList();
        return new DashboardReport
        {
            CreatedAt = new DateTime(2026, 10, 16, 14, 40, 0),
            ScanTime = new DateTime(2026, 10, 16, 14, 32, 0),
            Score = 67,
            Delta = ScoreDelta.Create(52, 67, new DateTime(2026, 10, 14, 9, 0, 0)),
            Reclaimable = ReclaimableSplit.From(list),
            Pc = new ReportPcInfo
            {
                ComputerName = computerName,
                WindowsName = "Windows 11 Домашняя",
                WindowsVersion = "24H2, сборка 26100.2033",
                CpuName = "Intel Core i7-10700K",
                LogicalProcessors = 16,
                RamBytes = 32UL * 1024 * 1024 * 1024,
                Gpus = new[] { "NVIDIA GeForce RTX 2070 SUPER", "Intel UHD Graphics 630" },
                SystemDrive = "C:",
                SystemDriveFree = 400L * 1024 * 1024 * 1024,
                SystemDriveTotal = 1000L * 1024 * 1024 * 1024,
                Uptime = TimeSpan.FromHours(30),
                IsAdmin = false,
            },
            Modules = new[]
            {
                new ReportModule("Очистка", ModuleScanState.Done, "готово: 1 совет", null),
                new ReportModule("Сеть", ModuleScanState.Done, "готово: 1 проблема", null),
                new ReportModule("Программы", ModuleScanState.Failed, "ошибка проверки", "Нет доступа к реестру"),
            },
            Groups = DashboardReport.GroupIssues(list),
            Warning = "Не удалось проверить: Программы.",
            AppVersion = "1.2.3",
        };
    }

    [Fact]
    public void Html_IsSelfContainedUtf8Page_WithEverySection()
    {
        var html = ReportBuilder.BuildHtml(SampleReport());

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("<html lang=\"ru\">", html);
        Assert.Contains("prefers-color-scheme: dark", html);
        Assert.Contains("@media print", html);
        Assert.Contains("conic-gradient", html);
        Assert.Contains("--p:67", html);

        Assert.Contains("Отчёт о проверке компьютера", html);
        Assert.Contains("16 октября 2026 г., 14:32", html);
        Assert.Contains("Нужно внимание", html);                     // verdict for 67
        Assert.Contains("Было 52 → стало 67 (+15)", html);
        Assert.Contains("Об этом компьютере", html);
        Assert.Contains("WATERCAT-PC", html);
        Assert.Contains("Windows 11 Домашняя", html);
        Assert.Contains("24H2, сборка 26100.2033", html);
        Assert.Contains("Intel Core i7-10700K (16 логических процессоров)", html);
        Assert.Contains("32 ГБ", html);
        Assert.Contains("Видеокарты", html);
        Assert.Contains("NVIDIA GeForce RTX 2070 SUPER; Intel UHD Graphics 630", html);
        Assert.Contains("Системный диск (C:)", html);
        Assert.Contains("свободно 400 ГБ из 1000 ГБ (40%)", html);
        Assert.Contains("1 д 6 ч", html);                           // uptime
        Assert.Contains("Проверенные разделы", html);
        Assert.Contains("Ошибка проверки", html);
        Assert.Contains("Нет доступа к реестру", html);
        Assert.Contains("Не удалось проверить: Программы.", html);
        Assert.Contains("Найденные проблемы", html);
        Assert.Contains("Освободите ≈ 10 ГБ", html);
        Assert.Contains("Освободит 1 КБ", html);                     // gain derived from ReclaimableBytes

        // footer: name, version and the repository link
        Assert.Contains("WaterCa7s PC Cleaner", html);
        Assert.Contains("1.2.3", html);
        Assert.Contains($"href=\"{AppInfo.RepoUrl}\"", html);
    }

    [Fact]
    public void Html_EncodesEveryDynamicString()
    {
        var issues = new[] { NewIssue(Evil, IssueSeverity.High, Evil, Evil, gain: Evil) };
        var report = SampleReport(issues, computerName: Evil) with
        {
            Warning = Evil,
            Modules = new[] { new ReportModule(Evil, ModuleScanState.Failed, Evil, Evil) },
            Pc = SampleReport().Pc with { ComputerName = Evil, CpuName = Evil, Gpus = new[] { Evil } },
        };

        var html = ReportBuilder.BuildHtml(report);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(\"x\")", html);
        Assert.DoesNotContain("'q'", html);
        Assert.Contains("&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt; &amp; &#39;q&#39;", html);
    }

    [Fact]
    public void Html_HasNoScriptsAndNoExternalResources_ExceptTheRepoLink()
    {
        var html = ReportBuilder.BuildHtml(SampleReport());

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", html, StringComparison.OrdinalIgnoreCase);
        Assert.False(Regex.IsMatch(html, @"<[^>]+\son[a-z]+\s*=", RegexOptions.IgnoreCase), "no inline event handlers");

        var urls = Regex.Matches(html, @"(?i)\b(?:https?|ftp|file)://[^\s""'<>)]+|(?<![:\w])//[a-z0-9][^\s""'<>)]*")
            .Select(m => m.Value)
            .ToList();
        Assert.NotEmpty(urls);
        Assert.All(urls, u => Assert.Equal(AppInfo.RepoUrl, u));
    }

    [Fact]
    public void Html_GroupsIssuesBySeverity_MostSevereFirst()
    {
        var html = ReportBuilder.BuildHtml(SampleReport());

        var critical = html.IndexOf("<h3><span class=\"sev-dot\" aria-hidden=\"true\"></span>Критично", StringComparison.Ordinal);
        var medium = html.IndexOf("<h3><span class=\"sev-dot\" aria-hidden=\"true\"></span>Средне", StringComparison.Ordinal);
        var low = html.IndexOf("<h3><span class=\"sev-dot\" aria-hidden=\"true\"></span>Низко", StringComparison.Ordinal);
        var tips = html.IndexOf("<h3><span class=\"sev-dot\" aria-hidden=\"true\"></span>Советы", StringComparison.Ordinal);
        Assert.True(critical > 0 && critical < medium && medium < low && low < tips, $"{critical} {medium} {low} {tips}");
        Assert.DoesNotContain(">Важно <span class=\"count\">", html); // empty groups are omitted

        // every issue sits in its own group
        var antivirus = html.IndexOf("Антивирус выключен", StringComparison.Ordinal);
        var disk = html.IndexOf("Мало места на диске D:", StringComparison.Ordinal);
        var dns = html.IndexOf("Высокая задержка до DNS", StringComparison.Ordinal);
        var bin = html.IndexOf("Корзина не пуста", StringComparison.Ordinal);
        Assert.True(critical < antivirus && antivirus < medium);
        Assert.True(medium < disk && disk < low);
        Assert.True(low < dns && dns < tips);
        Assert.True(tips < bin);
        Assert.Equal(4, Regex.Matches(html, "<article class=\"issue ").Count);
    }

    [Fact]
    public void GroupIssues_UsesDashboardOrder_AndKeepsModuleAndGain()
    {
        var groups = DashboardReport.GroupIssues(new[]
        {
            NewIssue("A", IssueSeverity.Low, "мелочь"),
            NewIssue("B", IssueSeverity.Critical, "критично"),
            NewIssue("C", IssueSeverity.Low, "крупная мелочь", reclaimable: 5000),
            NewIssue("D", (IssueSeverity)42, "странная важность"),
        });

        Assert.Equal(new[] { IssueSeverity.Critical, IssueSeverity.Low }, groups.Select(g => g.Severity));
        Assert.Equal(new[] { "критично", "странная важность" }, groups[0].Issues.Select(i => i.Title));
        Assert.Equal(new[] { "крупная мелочь", "мелочь" }, groups[1].Issues.Select(i => i.Title)); // bigger gain first
        Assert.Equal("C", groups[1].Issues[0].Module);
        Assert.Equal("Освободит 4,88 КБ", groups[1].Issues[0].Gain);
        Assert.Equal("Критично", groups[0].Title);
    }

    [Fact]
    public void Html_ShowsWhatWasFixedInThisSession()
    {
        var fixedIssue = NewIssue("Очистка", IssueSeverity.Medium, "Временные файлы", fixable: true);
        fixedIssue.IsFixed = true;
        fixedIssue.FixResult = "Удалено 1,2 ГБ временных файлов.";
        var failed = NewIssue("Ускорение", IssueSeverity.Low, "Игровой режим", fixable: true);
        failed.FixResult = "Нужны права администратора — ничего не изменено.";
        var untouched = NewIssue("Сеть", IssueSeverity.Low, "Медленный DNS");

        var report = SampleReport(new[] { fixedIssue, failed, untouched });
        var html = ReportBuilder.BuildHtml(report);

        Assert.Contains("✓ Исправлено", html);
        Assert.Contains("Результат исправления:</b> Удалено 1,2 ГБ временных файлов.", html);
        Assert.Contains("Не исправлено", html);
        Assert.Contains("Попытка исправления:</b> Нужны права администратора — ничего не изменено.", html);
        Assert.Contains("Исправлено в этом сеансе: 1", html);
        Assert.Equal(1, report.FixedCount);
        Assert.Equal(2, report.RemainingProblems);
    }

    [Fact]
    public void Html_WithoutIssues_ShowsAFriendlyEmptyState()
    {
        var report = SampleReport(Array.Empty<HealthIssue>()) with { Score = 100, Delta = null, Warning = null };
        var html = ReportBuilder.BuildHtml(report);

        Assert.Contains("Проблем не найдено", html);
        Assert.Contains("Отлично", html);
        Assert.DoesNotContain("<article", html);
        Assert.DoesNotContain("Было ", html);
    }

    [Fact]
    public void Text_HasEverySection_WithoutHtml()
    {
        var issues = new[]
        {
            NewIssue("Безопасность", IssueSeverity.Critical, "Антивирус <выключен>", "Строка 1\nСтрока 2"),
            NewIssue("Диски", IssueSeverity.Medium, "Мало места & медленно", gain: "Освободите ≈ 10 ГБ"),
        };
        issues[1].IsFixed = true;
        issues[1].FixResult = "Готово.";
        var text = ReportBuilder.BuildText(SampleReport(issues));

        Assert.Contains("Отчёт о проверке компьютера — WaterCa7s PC Cleaner", text);
        Assert.Contains("Оценка: 67 из 100 — Нужно внимание", text);
        Assert.Contains("Было 52 → стало 67 (+15) — сравнение с проверкой 14 октября 2026 г., 09:00", text);
        Assert.Contains("ОБ ЭТОМ КОМПЬЮТЕРЕ", text);
        Assert.Contains("  Имя компьютера: WATERCAT-PC", text);
        Assert.Contains("ПРОВЕРЕННЫЕ РАЗДЕЛЫ", text);
        Assert.Contains("✗ Программы — ошибка проверки (Нет доступа к реестру)", text);
        Assert.Contains("НАЙДЕННЫЕ ПРОБЛЕМЫ", text);
        Assert.Contains("1. [Безопасность] Антивирус <выключен>", text);      // plain text is not encoded
        Assert.Contains("     Строка 1" + Environment.NewLine + "     Строка 2", text);
        Assert.Contains("1. [Диски] Мало места & медленно — исправлено", text);
        Assert.Contains("     Что даст: Освободите ≈ 10 ГБ", text);
        Assert.Contains("     Результат исправления: Готово.", text);
        Assert.True(text.IndexOf("Критично", StringComparison.Ordinal) < text.IndexOf("Средне", StringComparison.Ordinal));
        Assert.Contains(AppInfo.RepoUrl, text);
        Assert.DoesNotContain("&lt;", text);
        Assert.DoesNotContain("&amp;", text);
        Assert.DoesNotContain("<p", text);
    }

    [Fact]
    public void DefaultFileName_UsesDateAndSafeCharacters()
    {
        var name = ReportBuilder.DefaultFileName(new DateTime(2026, 10, 6, 9, 5, 0));
        Assert.Equal("Отчёт WaterCa7s PC Cleaner 2026-10-06 09-05.html", name);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Fact]
    public void PcRows_ShowMissingDataHonestly()
    {
        var rows = new ReportPcInfo().Rows().ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("нет данных", rows["Оперативная память"]);
        Assert.Equal("нет данных", rows["Видеокарта"]);
        Assert.Equal("нет данных", rows["Системный диск"]);
        Assert.Equal("нет данных", rows["Без перезагрузки"]);
        Assert.False(rows.ContainsKey("Версия"));
    }
}

/// <summary>"Было 52 → стало 78 (+26)" and the honest split of reclaimable space.</summary>
public class ScoreDeltaAndReclaimableTests
{
    public ScoreDeltaAndReclaimableTests() => TestEnvironment.Init();

    [Fact]
    public void Delta_NeedsBothScores()
    {
        Assert.Null(ScoreDelta.Create(null, 78));
        Assert.Null(ScoreDelta.Create(52, null));
    }

    [Theory]
    [InlineData(52, 78, ScoreTrend.Better, "Было 52 → стало 78 (+26)")]
    [InlineData(78, 52, ScoreTrend.Worse, "Было 78 → стало 52 (−26)")]
    [InlineData(78, 78, ScoreTrend.Same, "Было 78 → стало 78 (без изменений)")]
    [InlineData(-5, 140, ScoreTrend.Better, "Было 0 → стало 100 (+100)")]
    public void Delta_TextAndTrend(int previous, int current, ScoreTrend trend, string text)
    {
        var delta = ScoreDelta.Create(previous, current)!;
        Assert.Equal(trend, delta.Trend);
        Assert.Equal(text, delta.Text);
    }

    [Fact]
    public void Delta_Explanation_NamesThePreviousScan()
    {
        var delta = ScoreDelta.Create(50, 60, new DateTime(2026, 10, 15, 9, 5, 0))!;
        Assert.Equal("Сравнение с прошлой проверкой (вчера в 09:05)", delta.Explanation(new DateTime(2026, 10, 16, 12, 0, 0)));
    }

    [Fact]
    public void Delta_InASavedReport_NamesTheAbsoluteDate()
    {
        // "вчера" would be wrong when the file is opened a week later
        var delta = ScoreDelta.Create(50, 60, new DateTime(2026, 10, 15, 9, 5, 0))!;
        Assert.Equal("Сравнение с проверкой 15 октября 2026 г., 09:05", ReportBuilder.DeltaExplanation(delta));
        Assert.Equal("Сравнение с прошлой проверкой", ReportBuilder.DeltaExplanation(ScoreDelta.Create(50, 60)!));

        var html = ReportBuilder.BuildHtml(new DashboardReport
        {
            CreatedAt = new DateTime(2026, 10, 16, 12, 0, 0),
            ScanTime = new DateTime(2026, 10, 16, 11, 0, 0),
            Score = 60,
            Delta = delta,
        });
        Assert.Contains("title=\"Сравнение с проверкой 15 октября 2026 г., 09:05\"", html);
        Assert.DoesNotContain("вчера", html);
    }

    private static HealthIssue Space(long bytes, bool fixable, bool isFixed = false)
    {
        var issue = new HealthIssue
        {
            Module = "Очистка",
            Title = "Место",
            Severity = IssueSeverity.Low,
            ReclaimableBytes = bytes,
            FixAsync = fixable ? _ => Task.FromResult("ok") : null,
        };
        issue.IsFixed = isFixed;
        return issue;
    }

    [Fact]
    public void Split_SeparatesFixableFromManualReview()
    {
        var split = ReclaimableSplit.From(new[]
        {
            Space(1000, fixable: true),
            Space(500, fixable: true),
            Space(7000, fixable: false),           // old downloads / Windows.old
            Space(9999, fixable: true, isFixed: true),
            Space(-50, fixable: false),
            Space(0, fixable: true),
        });
        Assert.Equal(1500, split.AutoBytes);
        Assert.Equal(7000, split.ManualBytes);
        Assert.Equal(8500, split.TotalBytes);
    }

    [Fact]
    public void Split_Saturates()
    {
        var split = ReclaimableSplit.From(new[] { Space(long.MaxValue, true), Space(long.MaxValue, true), Space(long.MaxValue, false) });
        Assert.Equal(long.MaxValue, split.AutoBytes);
        Assert.Equal(long.MaxValue, split.TotalBytes);
    }

    [Fact]
    public void Split_Texts_AreHonest()
    {
        const long gb = 1024L * 1024 * 1024;
        var both = new ReclaimableSplit(2 * gb, 40 * gb);
        Assert.Equal("Можно освободить 2 ГБ", both.AutoText);
        Assert.Equal("Ещё до 40 ГБ — если решите удалить сами", both.ManualText);
        Assert.Equal("Можно освободить 2 ГБ (ещё до 40 ГБ — если решите удалить сами)", both.SummaryText);

        var manualOnly = new ReclaimableSplit(0, 40 * gb);
        Assert.Null(manualOnly.AutoText);
        Assert.Equal("До 40 ГБ можно освободить, если решите удалить сами", manualOnly.ManualText);
        Assert.Equal(manualOnly.ManualText, manualOnly.SummaryText);

        var autoOnly = new ReclaimableSplit(2 * gb, 0);
        Assert.Null(autoOnly.ManualText);
        Assert.Equal("Можно освободить 2 ГБ", autoOnly.SummaryText);

        Assert.Null(default(ReclaimableSplit).SummaryText);
        Assert.True(default(ReclaimableSplit).IsEmpty);
    }

    [Fact]
    public void Summary_RoundTripsNewFields_AndReadsOldSummaries()
    {
        var settings = new AppSettings();
        var summary = new ScanSummary
        {
            Score = 78,
            Time = new DateTime(2026, 10, 16, 14, 32, 0),
            ReclaimableBytes = 9000,
            ManualReclaimableBytes = 7000,
            PreviousScore = 52,
            PreviousTime = new DateTime(2026, 10, 10, 8, 0, 0),
        };
        settings.ModuleData["Dashboard"] = new Dictionary<string, string>
        {
            ["LastScan"] = System.Text.Json.JsonSerializer.Serialize(summary),
        };
        var loaded = ScanSummary.Load(settings)!;
        Assert.Equal(summary, loaded);
        Assert.Equal(new ReclaimableSplit(2000, 7000), loaded.Reclaimable);
        Assert.Equal("Было 52 → стало 78 (+26)", loaded.Delta!.Text);

        // a summary saved by the previous version: everything counts as "fixable", no comparison
        settings.ModuleData["Dashboard"]["LastScan"] = "{\"Score\":60,\"Time\":\"2026-10-01T10:00:00\",\"ReclaimableBytes\":4096}";
        var legacy = ScanSummary.Load(settings)!;
        Assert.Equal(new ReclaimableSplit(4096, 0), legacy.Reclaimable);
        Assert.Null(legacy.Delta);
        Assert.Null(legacy.MeasuredScore);
        Assert.Equal(60, legacy.BaselineScore);                        // older summaries compare with their score

        // absurd stored values are clamped
        settings.ModuleData["Dashboard"]["LastScan"] =
            "{\"Score\":60,\"Time\":\"2026-10-01T10:00:00\",\"ReclaimableBytes\":100,\"ManualReclaimableBytes\":900,\"PreviousScore\":400,\"MeasuredScore\":-7}";
        var odd = ScanSummary.Load(settings)!;
        Assert.Equal(new ReclaimableSplit(0, 100), odd.Reclaimable);
        Assert.Equal(100, odd.PreviousScore);
        Assert.Equal(0, odd.BaselineScore);
    }

    [Fact]
    public void Summary_RoundTripsTheMeasuredScore()
    {
        var settings = new AppSettings();
        var summary = new ScanSummary { Score = 100, MeasuredScore = 94, Time = new DateTime(2026, 10, 16, 14, 32, 0) };
        summary.Save(settings);
        var loaded = ScanSummary.Load(settings)!;
        Assert.Equal(summary, loaded);
        Assert.Equal(94, loaded.BaselineScore);
    }
}

/// <summary>Balanced tile layouts, GPU names and small view helpers.</summary>
public class DashboardLayoutTests
{
    [Theory]
    [InlineData(8, 4, 4)]   // 4 x 2
    [InlineData(8, 3, 2)]   // never 3 + 3 + 2
    [InlineData(8, 5, 4)]
    [InlineData(6, 3, 3)]   // quick links: 3 x 2
    [InlineData(6, 2, 2)]   // 2 x 3
    [InlineData(6, 4, 3)]
    [InlineData(7, 4, 4)]   // nothing divides 7: keep the widest
    [InlineData(4, 4, 4)]
    [InlineData(4, 3, 2)]
    [InlineData(8, 1, 1)]
    [InlineData(1, 4, 1)]
    [InlineData(0, 4, 1)]
    public void BalancedColumns_FillsEveryRowWhenPossible(int count, int max, int expected)
        => Assert.Equal(expected, LayoutMath.BalancedColumns(count, max));

    [Fact]
    public void BalancedColumnsConverter_UsesWidthAndCount()
    {
        var c = new BalancedColumnsConverter();
        Assert.Equal(4, c.Convert(new object[] { 960.0, 8 }, typeof(int), "215", Format.Ru));
        Assert.Equal(2, c.Convert(new object[] { 680.0, 8 }, typeof(int), "215", Format.Ru));
        Assert.Equal(3, c.Convert(new object[] { 630.0, 6 }, typeof(int), "185", Format.Ru));
        Assert.Equal(2, c.Convert(new object[] { 440.0, 6 }, typeof(int), "185", Format.Ru));
        Assert.Equal(4, c.Convert(new object[] { 0.0, 8 }, typeof(int), "215", Format.Ru)); // not measured yet
    }

    [Theory]
    [InlineData(950.0, "820", true)]
    [InlineData(670.0, "820", false)]
    [InlineData(0.0, "820", true)]
    [InlineData(950.0, "<820", false)]
    [InlineData(670.0, "<820", true)]
    [InlineData(0.0, "<820", false)]
    public void WidthToVisibility_SwitchesLayouts(double width, string parameter, bool visible)
    {
        var result = new WidthToVisibilityConverter().Convert(width, typeof(System.Windows.Visibility), parameter, Format.Ru);
        Assert.Equal(visible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed, result);
    }

    [Fact]
    public void GpuNames_AreCleaned_AndVirtualAdaptersDropped()
    {
        Assert.Equal(new[] { "NVIDIA GeForce RTX 2070 SUPER", "Intel UHD Graphics 630" },
            SystemInfo.CleanGpuNames(new[]
            {
                "  NVIDIA GeForce RTX 2070 SUPER ", "Parsec Virtual Display Adapter", null, "",
                "Intel(R) UHD   Graphics 630", "nvidia geforce rtx 2070 super", "Microsoft Remote Display Adapter",
            }));
        // only a basic adapter: keep it rather than showing nothing
        Assert.Equal(new[] { "Microsoft Basic Display Adapter" }, SystemInfo.CleanGpuNames(new[] { "Microsoft Basic Display Adapter" }));
        Assert.Empty(SystemInfo.CleanGpuNames(Array.Empty<string?>()));
    }

    [Fact]
    public void ModuleRow_DisplayStatus_IsCapitalized()
    {
        var row = new ModuleScanRow("Сеть");
        Assert.Equal("Ждёт очереди", row.DisplayStatus);
        row.SetResult(new ModuleScanResult("Сеть", ModuleScanState.Done, Array.Empty<HealthIssue>(), null, TimeSpan.Zero));
        Assert.Equal("готово: проблем нет", row.StatusText);
        Assert.Equal("Готово: проблем нет", row.DisplayStatus);
    }
}

/// <summary>The dashboard page: report commands, before/after chip and the reclaimable chips.</summary>
[Collection(DashboardSequentialCollection.Name)]
public class DashboardReportViewModelTests
{
    public DashboardReportViewModelTests() => TestEnvironment.Init();

    private static StaticSystemInfo FakeInfo() => new("Windows 11 Pro", "24H2, сборка 26100.1", "TEST-PC", "Test CPU", 8, 16UL << 30)
    {
        Gpus = new[] { "Test GPU" },
    };

    private static DashboardViewModel Vm(AppSettings settings, params IHealthContributor[] contributors)
        => new(contributors, settings)
        {
            CreateRestorePointAsync = (_, _) => Task.FromResult(false),
            ReadStaticInfo = FakeInfo,
            PickReportPath = _ => throw new InvalidOperationException("the file dialog must not be shown here"),
            SetClipboardText = _ => throw new InvalidOperationException("the clipboard must not be touched here"),
        };

    [Fact]
    public void QuickLinks_IncludeNetwork_AndFillBalancedRows()
    {
        var vm = Vm(new AppSettings());
        Assert.Equal(7, vm.QuickLinks.Count);
        var network = vm.QuickLinks.Single(l => l.Key == "Network");
        Assert.Equal("Сеть", network.Title);
        Assert.Equal("Скорость, задержки, Wi-Fi и порты", network.Description.Replace('\u2011', '-'));
        Assert.Contains("Wi\u2011Fi", network.Description); // never wrapped as "Wi-" / "Fi" on a narrow tile
        Assert.Equal("", network.Glyph);
        Assert.Equal("Cleaner", vm.FeaturedLink.Key);
        Assert.Equal(6, vm.MoreLinks.Count);                      // 3 x 2 or 2 x 3
        Assert.DoesNotContain(vm.FeaturedLink, vm.MoreLinks);
        Assert.Equal(7, vm.QuickLinks.Select(l => l.Key).Distinct().Count());
    }

    [Fact]
    public async Task ReportCommands_AreDisabledBeforeAScan()
    {
        var vm = Vm(new AppSettings(), new FakeContributor("Очистка"));
        Assert.False(vm.HasReport);
        Assert.False(vm.SaveReportCommand.CanExecute(null));
        Assert.False(vm.CopyReportCommand.CanExecute(null));
        Assert.Null(await vm.SaveReportAsync());                   // the picker would throw if it were shown
        Assert.False(await vm.CopyReportAsync());
        Assert.Null(await vm.BuildReportAsync());
    }

    [Fact]
    public async Task CancelledScan_HasNoReport()
    {
        var vm = Vm(new AppSettings(), new FakeContributor("Долгий", async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<HealthIssue>();
        }));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await vm.ScanCoreAsync(cts.Token, persist: false);
        Assert.Equal(DashboardScanState.Cancelled, vm.State);
        Assert.False(vm.HasReport);
    }

    [Fact]
    public async Task SaveReport_WritesSelfContainedUtf8Html_AndLogsIt()
    {
        var dir = TestEnvironment.NewTempDir("report");
        try
        {
            var vm = Vm(new AppSettings(),
                new FakeContributor("Очистка", Issue("Очистка", IssueSeverity.Medium, "Временные <файлы>", reclaimable: 2048,
                    fix: _ => Task.FromResult("ok"), autoFixSafe: true)),
                new FakeContributor("Сеть"));
            vm.Now = () => new DateTime(2026, 10, 16, 20, 15, 0);
            await vm.ScanCoreAsync(CancellationToken.None, persist: false);
            Assert.True(vm.HasReport);

            string? suggested = null;
            vm.PickReportPath = name =>
            {
                suggested = name;
                return Path.Combine(dir, name);
            };
            // "Открыть отчёт в браузере?" is asked, and declined here: no browser is started by the test.
            string? question = null;
            var previous = Dialogs.ConfirmHandler;
            Dialogs.ConfirmHandler = (_, m) =>
            {
                question = m;
                return false;
            };
            string? path;
            try
            {
                path = await vm.SaveReportAsync();
            }
            finally
            {
                Dialogs.ConfirmHandler = previous;
            }

            Assert.Contains("Открыть отчёт в браузере?", question);
            Assert.Equal("Отчёт WaterCa7s PC Cleaner 2026-10-16 20-15.html", suggested);
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            var bytes = await File.ReadAllBytesAsync(path!);
            Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 BOM expected");
            var html = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Assert.Contains("TEST-PC", html);
            Assert.Contains("Test GPU", html);
            Assert.Contains("Временные &lt;файлы&gt;", html);
            Assert.Contains("Сеть", html);
            Assert.True(vm.HasReportStatus);
            Assert.True(vm.ReportStatusOk);
            Assert.Contains(path!, vm.ActionSummary);
            Assert.Contains(Log.Entries.ToList(), e => e.Level == LogLevel.Action && e.Message.Contains(path!));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveReport_CancelledDialog_WritesNothing()
    {
        var vm = Vm(new AppSettings(), new FakeContributor("Очистка"));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var asked = 0;
        vm.PickReportPath = _ =>
        {
            asked++;
            return null;
        };
        Assert.Null(await vm.SaveReportAsync());
        Assert.Equal(1, asked);
        Assert.False(vm.HasReportStatus);
    }

    [Fact]
    public async Task SaveReport_ToAnUnwritablePlace_ReportsTheProblem()
    {
        var dir = TestEnvironment.NewTempDir("report-bad");
        try
        {
            var vm = Vm(new AppSettings(), new FakeContributor("Очистка"));
            await vm.ScanCoreAsync(CancellationToken.None, persist: false);
            vm.PickReportPath = _ => Path.Combine(dir, "нет такой папки", "отчёт.html");

            Assert.Null(await vm.SaveReportAsync());
            Assert.False(vm.ReportStatusOk);
            Assert.True(vm.HasReportStatus);
            Assert.False(vm.IsBusy);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CopyReport_PutsPlainTextOnTheClipboard_AndSurvivesALockedClipboard()
    {
        var vm = Vm(new AppSettings(), new FakeContributor("Безопасность", Issue("Безопасность", IssueSeverity.High, "Брандмауэр & защита")));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        string? copied = null;
        vm.SetClipboardText = t => copied = t;
        Assert.True(await vm.CopyReportAsync());
        Assert.NotNull(copied);
        Assert.Contains("Брандмауэр & защита", copied);
        Assert.Contains("TEST-PC", copied);
        Assert.DoesNotContain("<html", copied);
        Assert.DoesNotContain("&amp;", copied);
        Assert.True(vm.ReportStatusOk);

        vm.SetClipboardText = _ => throw new System.Runtime.InteropServices.COMException("OpenClipboard Failed", unchecked((int)0x800401D0));
        Assert.False(await vm.CopyReportAsync());
        Assert.False(vm.ReportStatusOk);
        Assert.Contains("Буфер обмена", vm.ReportStatus);
    }

    [Fact]
    public async Task Report_ContainsFixResultsOfThisSession()
    {
        var vm = Vm(new AppSettings(), new FakeContributor("Очистка",
            Issue("Очистка", IssueSeverity.Medium, "Кэш", reclaimable: 1000, fix: _ => Task.FromResult("Удалено 1000 байт."), autoFixSafe: true),
            Issue("Очистка", IssueSeverity.Low, "Игровой режим", fix: _ => Task.FromResult("Нужны права администратора — ничего не изменено."))));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        foreach (var item in vm.Groups.SelectMany(g => g.Items).ToList())
            await vm.FixOneCoreAsync(item, CancellationToken.None);

        var report = (await vm.BuildReportAsync())!;
        var issues = report.AllIssues.ToDictionary(i => i.Title);
        Assert.True(issues["Кэш"].IsFixed);
        Assert.Equal("Удалено 1000 байт.", issues["Кэш"].FixResult);
        Assert.True(issues["Игровой режим"].FixFailed);
        Assert.Equal("TEST-PC", report.Pc.ComputerName);
        Assert.Equal(new[] { "Test GPU" }, report.Pc.Gpus);
        Assert.Single(report.Modules);
    }

    [Fact]
    public async Task ScoreDelta_IsHiddenOnTheFirstScan_AndComparesWithThePreviousOne()
    {
        var settings = new AppSettings();
        var t0 = new DateTime(2026, 10, 10, 9, 0, 0);
        var problems = new FakeContributor("Безопасность", Issue("Безопасность", IssueSeverity.Critical));
        var first = Vm(settings, problems);
        first.Now = () => t0;
        await first.ScanCoreAsync(CancellationToken.None, persist: true);
        Assert.Equal(75, first.Score);
        Assert.False(first.HasScoreDelta);
        Assert.Null(first.ScoreDelta);

        // a second scan in the same session: the problem is gone
        var clean = new FakeContributor("Безопасность");
        var second = Vm(settings, clean);
        second.Now = () => t0.AddDays(2);
        await second.OnNavigatedToAsync();
        second.OnNavigatedFrom();
        await second.ScanCoreAsync(CancellationToken.None, persist: true);

        Assert.True(second.HasScoreDelta);
        Assert.Equal("Было 75 → стало 100 (+25)", second.ScoreDeltaText);
        Assert.Equal(ScoreTrend.Better, second.ScoreTrend);
        Assert.Contains("10 октября", second.ScoreDeltaToolTip);

        // survives a restart
        var restarted = Vm(settings, clean);
        restarted.Now = () => t0.AddDays(2).AddHours(1);
        await restarted.OnNavigatedToAsync();
        restarted.OnNavigatedFrom();
        Assert.Equal("Было 75 → стало 100 (+25)", restarted.ScoreDeltaText);

        // a worse result is red
        var third = Vm(settings, new FakeContributor("Безопасность", Issue("Безопасность", IssueSeverity.High)));
        await third.OnNavigatedToAsync();
        third.OnNavigatedFrom();
        await third.ScanCoreAsync(CancellationToken.None, persist: false);
        Assert.Equal("Было 100 → стало 88 (−12)", third.ScoreDeltaText);
        Assert.Equal(ScoreTrend.Worse, third.ScoreTrend);
    }

    [Fact]
    public async Task ScoreDelta_KeepsThePreviousScoreAfterFixes()
    {
        var settings = new AppSettings();
        new ScanSummary { Score = 40, Time = new DateTime(2026, 10, 1, 12, 0, 0), IssueCount = 3 }.Save(settings);
        var vm = Vm(settings, new FakeContributor("Очистка",
            Issue("Очистка", IssueSeverity.Medium, "Кэш", fix: _ => Task.FromResult("Удалено."), autoFixSafe: true)));
        await vm.OnNavigatedToAsync();
        vm.OnNavigatedFrom();
        await vm.ScanCoreAsync(CancellationToken.None, persist: true);
        Assert.Equal("Было 40 → стало 94 (+54)", vm.ScoreDeltaText);

        var item = vm.Groups.Single().Items.Single();
        var previous = Dialogs.ConfirmHandler;
        Dialogs.ConfirmHandler = (_, _) => true;
        try
        {
            await vm.FixIssueAsync(item);
        }
        finally
        {
            Dialogs.ConfirmHandler = previous;
        }
        Assert.Equal(100, vm.Score);
        Assert.Equal("Было 40 → стало 100 (+60)", vm.ScoreDeltaText);
        Assert.Equal(40, ScanSummary.Load(settings)!.PreviousScore);
    }

    [Fact]
    public async Task ReclaimableChips_SeparateFixableFromManualReview()
    {
        const long gb = 1024L * 1024 * 1024;
        var settings = new AppSettings();
        var vm = Vm(settings,
            new FakeContributor("Очистка",
                Issue("Очистка", IssueSeverity.Medium, "Мусор", reclaimable: 2 * gb, fix: _ => Task.FromResult("ok"), autoFixSafe: true)),
            new FakeContributor("Диски",
                Issue("Диски", IssueSeverity.Low, "Windows.old", reclaimable: 30 * gb),
                Issue("Диски", IssueSeverity.Low, "Старые загрузки", reclaimable: 10 * gb)));
        await vm.ScanCoreAsync(CancellationToken.None, persist: true);

        Assert.Equal(42 * gb, vm.ReclaimableBytes);
        Assert.True(vm.HasAutoReclaimable);
        Assert.Equal("Можно освободить 2 ГБ", vm.AutoReclaimableText);
        Assert.True(vm.HasManualReclaimable);
        Assert.Equal("Ещё до 40 ГБ — если решите удалить сами", vm.ManualReclaimableText);
        Assert.Equal("Можно освободить 2 ГБ (ещё до 40 ГБ — если решите удалить сами)", vm.ReclaimableText);
        Assert.Equal(new ReclaimableSplit(2 * gb, 40 * gb), ScanSummary.Load(settings)!.Reclaimable);
    }

    [Fact]
    public async Task ReclaimableChips_AfterFixing_ShowOnlyTheManualPart()
    {
        const long gb = 1024L * 1024 * 1024;
        var vm = Vm(new AppSettings(),
            new FakeContributor("Очистка",
                Issue("Очистка", IssueSeverity.Medium, "Мусор", reclaimable: 2 * gb, fix: _ => Task.FromResult("Удалено."), autoFixSafe: true),
                Issue("Очистка", IssueSeverity.Low, "Старые загрузки", reclaimable: 10 * gb)));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        var previous = Dialogs.ConfirmHandler;
        Dialogs.ConfirmHandler = (_, _) => true;
        try
        {
            await vm.FixAllAsync();
        }
        finally
        {
            Dialogs.ConfirmHandler = previous;
        }

        Assert.False(vm.HasAutoReclaimable);
        Assert.True(vm.HasManualReclaimable);
        Assert.Equal("До 10 ГБ можно освободить, если решите удалить сами", vm.ManualReclaimableText);
        Assert.Equal(10 * gb, vm.ReclaimableBytes);
    }

    [Fact]
    public async Task ScoreDelta_AfterFixingAndRescanning_ComparesWithWhatThePreviousScanMeasured()
    {
        // Scan 1 measures 94. The fix is estimated to bring it to 100, but it frees a bit less and the rescan still
        // finds a small leftover (98). The user improved the PC: "Было 94 → стало 98", never a red "100 → 98".
        var settings = new AppSettings();
        var round = 0;
        var cleaner = new FakeContributor("Очистка", _ => Task.FromResult<IReadOnlyList<HealthIssue>>(
            Interlocked.Increment(ref round) == 1
                ? new[] { Issue("Очистка", IssueSeverity.Medium, "Мусор", reclaimable: 2048, fix: _ => Task.FromResult("Удалено."), autoFixSafe: true) }
                : new[] { Issue("Очистка", IssueSeverity.Low, "Немного мусора осталось") }));
        var vm = Vm(settings, cleaner);
        await vm.ScanCoreAsync(CancellationToken.None, persist: true);
        Assert.Equal(94, vm.Score);

        var previous = Dialogs.ConfirmHandler;
        Dialogs.ConfirmHandler = (_, _) => true;
        try
        {
            await vm.FixAllAsync();
        }
        finally
        {
            Dialogs.ConfirmHandler = previous;
        }
        Assert.Equal(100, vm.Score);                                   // estimate after the fix
        Assert.Equal(94, ScanSummary.Load(settings)!.MeasuredScore);   // the measured value is kept

        await vm.ScanCoreAsync(CancellationToken.None, persist: true);
        Assert.Equal(98, vm.Score);
        Assert.Equal("Было 94 → стало 98 (+4)", vm.ScoreDeltaText);
        Assert.Equal(ScoreTrend.Better, vm.ScoreTrend);

        // the same after a restart
        var restarted = Vm(settings, cleaner);
        await restarted.OnNavigatedToAsync();
        restarted.OnNavigatedFrom();
        Assert.Equal("Было 94 → стало 98 (+4)", restarted.ScoreDeltaText);
    }

    [Theory]
    [InlineData(@"C:\Отчёты\отчёт.html", true)]
    [InlineData(@"C:\Отчёты\отчёт.HTM", true)]
    [InlineData(@"C:\Отчёты\отчёт.txt", false)]
    [InlineData(@"C:\Отчёты\отчёт.bat", false)]
    [InlineData(@"C:\Отчёты\отчёт.html.cmd", false)]
    [InlineData(@"C:\Отчёты\отчёт", false)]
    public void OnlyHtmlFiles_AreOfferedToOpen(string path, bool html)
        => Assert.Equal(html, DashboardViewModel.IsHtmlFile(path));

    [Fact]
    public async Task SaveReport_WithAnotherExtension_IsSaved_ButNeverHandedToTheShell()
    {
        var dir = TestEnvironment.NewTempDir("report-ext");
        try
        {
            var vm = Vm(new AppSettings(), new FakeContributor("Очистка"));
            await vm.ScanCoreAsync(CancellationToken.None, persist: false);
            var target = Path.Combine(dir, "отчёт.txt");
            vm.PickReportPath = _ => target;

            var asked = 0;
            string? info = null;
            var previousConfirm = Dialogs.ConfirmHandler;
            var previousInfo = Dialogs.InfoHandler;
            Dialogs.ConfirmHandler = (_, _) =>
            {
                asked++;
                return false;
            };
            Dialogs.InfoHandler = (_, m) => info = m;
            string? path;
            try
            {
                path = await vm.SaveReportAsync();
            }
            finally
            {
                Dialogs.ConfirmHandler = previousConfirm;
                Dialogs.InfoHandler = previousInfo;
            }

            Assert.Equal(target, path);
            Assert.True(File.Exists(target));
            Assert.Equal(0, asked);                                    // "Открыть отчёт в браузере?" is not offered
            Assert.Contains(".html", info);
            Assert.True(vm.ReportStatusOk);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
