using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Optimizer;
using Xunit;
using static PcCleaner.Tests.Optimizer.OptimizerTestData;

namespace PcCleaner.Tests.Optimizer;

/// <summary>Page behaviour with synthetic probes (nothing on the system is read or changed).</summary>
[Collection("OptimizerState")]
public sealed class OptimizerViewModelTests : IDisposable
{
    private readonly string _sandbox = $@"Software\WaterCa7sPcCleanerTests\Optimizer\{Guid.NewGuid():N}";
    private readonly AppSettings _settings = new();

    public OptimizerViewModelTests() => TestEnvironment.Init();

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_sandbox, throwOnMissingSubKey: false);

    private OptimizerService Create(Func<Task<HardwareSnapshot>> hardware) => new(
        hardware,
        _ => Task.FromResult(GoodSettings()),
        new RegistryTweakEngine(_sandbox, _settings, (_, _) => throw new InvalidOperationException("nothing may be changed")),
        new PowerPlanSwitcher((_, _) => Task.FromResult(new ProcessResult(1, "", "", false)), _settings),
        () => false);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition(), "timed out");
    }

    [Fact]
    public async Task FailedFirstLoad_ShowsRetry_AndTheNextVisitLoadsAgain()
    {
        // Regression: a failed first load left "Изучаем ваш компьютер…" on screen forever, with no way to retry.
        var fail = true;
        var vm = new OptimizerViewModel(Create(() => fail
            ? Task.FromException<HardwareSnapshot>(new InvalidOperationException("WMI не отвечает"))
            : Task.FromResult(GoodDesktop())));

        await vm.PreviewScanAsync(CancellationToken.None);
        Assert.False(vm.HasData);
        Assert.False(vm.ShowPlaceholder);
        Assert.True(vm.ShowLoadError);
        Assert.False(string.IsNullOrWhiteSpace(vm.LoadError));

        fail = false;
        await vm.OnNavigatedToAsync();
        await WaitUntilAsync(() => vm.HasData);
        Assert.False(vm.ShowLoadError);
        Assert.Null(vm.LoadError);
        Assert.NotEmpty(vm.HardwareCards);
    }

    [Fact]
    public async Task CancelledFirstLoad_OffersRetry()
    {
        var never = new TaskCompletionSource<HardwareSnapshot>();
        var vm = new OptimizerViewModel(Create(() => never.Task));

        await vm.OnNavigatedToAsync();
        await WaitUntilAsync(() => vm.IsBusy);
        vm.CancelCommand.Execute(null);
        await WaitUntilAsync(() => vm.ShowLoadError);

        Assert.False(vm.IsBusy);
        Assert.False(vm.ShowPlaceholder);
        Assert.Contains("остановлен", vm.LoadError);
        never.TrySetResult(GoodDesktop());
    }
}
