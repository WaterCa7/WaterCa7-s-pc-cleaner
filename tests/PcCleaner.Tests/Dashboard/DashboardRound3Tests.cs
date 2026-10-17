using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using Xunit;
using static PcCleaner.Tests.Dashboard.FakeContributor;

namespace PcCleaner.Tests.Dashboard;

/// <summary>Exception texts on the dashboard are Russian (ErrorText), never the English .NET message.</summary>
[Collection(DashboardSequentialCollection.Name)]
public class DashboardRound3Tests
{
    public DashboardRound3Tests() => TestEnvironment.Init();

    private static DashboardViewModel Vm(params IHealthContributor[] contributors)
        => new(contributors, new AppSettings())
        {
            CreateRestorePointAsync = (_, _) => Task.FromResult(false),
            ReadStaticInfo = () => new StaticSystemInfo("Windows 11 Pro", "24H2", "TEST-PC", "Test CPU", 8, 16UL << 30),
        };

    [Fact]
    public async Task FixThatThrows_ShowsARussianReason()
    {
        var issue = Issue("Очистка", IssueSeverity.Low, "Временные файлы",
            fix: _ => throw new IOException("The process cannot access the file because it is being used by another process.",
                unchecked((int)0x80070020)));
        var vm = Vm(new FakeContributor("Очистка", issue));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var item = vm.Groups.Single().Items.Single();

        Assert.False(await vm.FixOneCoreAsync(item, CancellationToken.None)); // no dialog: the core step is called directly

        Assert.Equal("Не удалось исправить: файл занят другой программой.", issue.FixResult);
        Assert.True(item.FixFailed);
        Assert.False(issue.IsFixed);
    }

    [Fact]
    public async Task ModuleThatThrows_IsDescribedInRussian()
    {
        var failing = new FakeContributor("Диски",
            (Func<CancellationToken, Task<IReadOnlyList<HealthIssue>>>)(_ => throw new UnauthorizedAccessException("Access to the path 'C:\\x' is denied.")));

        var result = await SmartScanner.RunModuleAsync(failing, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(ModuleScanState.Failed, result.State);
        Assert.NotNull(result.Error);
        Assert.StartsWith("Нет доступа", result.Error);
        Assert.EndsWith(".", result.Error);
        Assert.DoesNotContain("Access", result.Error);
    }
}
