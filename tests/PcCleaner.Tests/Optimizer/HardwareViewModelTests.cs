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
