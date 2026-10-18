using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Startup;
using Xunit;

namespace PcCleaner.Tests.Startup;

/// <summary>
/// The dashboard fix of «Подозрительная запись в автозагрузке»: disables the entry (RunOnce: removes it) after reading the
/// entries again. Registry roots and Startup folders live in the sandbox; the real autostart is never touched.
/// </summary>
// "Доверять" saves the settings file, like the Settings tests.
[Collection(PcCleaner.Tests.Settings.SettingsFileCollection.Name)]
public class StartupSuspiciousFixTests
{
    private const string ApprovedRun = @"HKCU\Explorer\StartupApproved\Run";

    /// <summary>Registers an unsigned program started from a temp folder (the sandbox lives there), scans and returns its issue.</summary>
    private static (HealthIssue Issue, StartupItem Item) SuspiciousEntry(StartupSandbox sandbox, string relativeKey, string name)
    {
        var exe = sandbox.CreateFile($"{name}.exe");
        sandbox.SetValue(relativeKey, name, $"\"{exe}\" /silent");
        var items = sandbox.Service.Scan(null, CancellationToken.None);
        var item = Assert.Single(items, i => i.Name == name);
        Assert.True(item.IsSuspicious, "the test entry must look suspicious");
        var issue = Assert.Single(sandbox.Service.BuildIssues(items), i => i.Title.Contains(name, StringComparison.Ordinal));
        return (issue, item);
    }

    [Fact]
    public void SuspiciousEntryIssue_HasADisableFixThatIsPartOfFixAll()
    {
        using var sandbox = new StartupSandbox();

        var (issue, _) = SuspiciousEntry(sandbox, @"HKCU\Run", "Evil");

        Assert.True(issue.CanFix);
        Assert.Equal("Отключить", issue.FixLabel);
        Assert.True(issue.InFixAll);
        Assert.False(issue.AutoFixSafe);
        Assert.Equal("Startup", issue.TargetPageKey);
    }

    [Fact]
    public async Task Fix_DisablesARunEntry_AndKeepsTheValueSoItCanBeTurnedBackOn()
    {
        using var sandbox = new StartupSandbox();
        var (issue, _) = SuspiciousEntry(sandbox, @"HKCU\Run", "Evil");
        var changes = 0;
        sandbox.Service.ChangedExternally += () => changes++;

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("Запись «Evil» отключена — включить её можно на странице «Автозагрузка».", text);
        Assert.False(HealthScoring.LooksLikeFailure(text));
        var flag = Assert.IsType<byte[]>(sandbox.GetValue(ApprovedRun, "Evil"));
        Assert.Equal(0x03, flag[0]);
        Assert.NotNull(sandbox.GetValue(@"HKCU\Run", "Evil"));
        Assert.Equal(1, changes); // the Startup page is told to refresh
    }

    [Fact]
    public async Task Fix_RemovesARunOnceEntry_BecauseItCannotBeDisabled()
    {
        using var sandbox = new StartupSandbox();
        var (issue, _) = SuspiciousEntry(sandbox, @"HKCU\RunOnce", "Evil");

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("Запись «Evil» удалена из автозагрузки — резервная копия сохранена.", text);
        Assert.False(HealthScoring.LooksLikeFailure(text));
        Assert.Null(sandbox.GetValue(@"HKCU\RunOnce", "Evil"));
    }

    [Fact]
    public async Task Fix_ForAnEntryThatIsGone_ChangesNothing()
    {
        using var sandbox = new StartupSandbox();
        var (issue, _) = SuspiciousEntry(sandbox, @"HKCU\Run", "Evil");
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{sandbox.RegistryRoot}\HKCU\Run", writable: true)!)
            key.DeleteValue("Evil");

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("Запись уже удалена — ничего не изменено.", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.Null(sandbox.GetValue(ApprovedRun, "Evil"));
    }

    [Fact]
    public async Task Fix_LeavesAnEntryTheUserTrustedAfterTheScan()
    {
        using var sandbox = new StartupSandbox();
        var (issue, item) = SuspiciousEntry(sandbox, @"HKCU\Run", "Evil");
        StartupService.SetTrusted(item, trusted: true, sandbox.Settings);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Contains("ничего не изменено", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.Null(sandbox.GetValue(ApprovedRun, "Evil"));
    }

    [Fact]
    public async Task Fix_ForAnEntryOfAllUsersWithoutAdminRights_AsksForThem()
    {
        using var sandbox = new StartupSandbox(elevated: false);
        var (issue, _) = SuspiciousEntry(sandbox, @"HKLM\Run", "Evil");

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.StartsWith("Нужны права администратора: ", text);
        Assert.True(HealthScoring.LooksLikeAdminRequired(text));
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.Null(sandbox.GetValue(@"HKLM\Explorer\StartupApproved\Run", "Evil"));
    }

    [Fact]
    public void ResultText_NamesTheEntryAndTheReasonOfAFailure()
    {
        var item = new StartupItem
        {
            Id = "reg|test",
            Kind = StartupKind.RegistryRun,
            Scope = StartupScope.CurrentUser,
            Name = "Evil",
        };

        var failed = StartupService.SuspiciousFixText(item, StartupActionResult.Fail("Реестр недоступен."), removed: false);
        var removalFailed = StartupService.SuspiciousFixText(item, StartupActionResult.Fail("Реестр недоступен."), removed: true);

        Assert.Equal("Не удалось отключить «Evil»: Реестр недоступен.", failed);
        Assert.Equal("Не удалось удалить «Evil»: Реестр недоступен.", removalFailed);
        Assert.True(HealthScoring.LooksLikeFailure(failed));
        Assert.True(HealthScoring.LooksLikeFailure(removalFailed));
    }
}
