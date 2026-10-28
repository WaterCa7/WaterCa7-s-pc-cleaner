using System.Collections.ObjectModel;
using PcCleaner.Core;
using PcCleaner.Modules.Optimizer;
using PcCleaner.Modules.Settings;
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
    public void BuiltWhileTheOptimizerIsBusy_StartsBusyToo()
    {
        var optimizer = new OptimizerViewModel(AppServices.Optimizer);
        optimizer.SetBusyForTest(true, "Собираем сведения о компьютере…", 25);

        var hardware = new HardwareViewModel(optimizer);

        Assert.True(hardware.IsBusy);
        Assert.Equal("Собираем сведения о компьютере…", hardware.BusyText);
        Assert.Equal(25, hardware.Progress);
        optimizer.SetBusyForTest(false, "", -1);
    }

    [Fact]
    public void OneJob_IsListedOnce_AmongTheBusyPages()
    {
        var optimizer = new OptimizerViewModel(AppServices.Optimizer);
        var hardware = new HardwareViewModel(optimizer);
        var pages = new PageViewModel[] { hardware, optimizer };

        optimizer.SetBusyForTest(true, "Собираем сведения о компьютере…", -1);

        Assert.True(hardware.IsBusy); // its own strip still shows the work
        Assert.True(hardware.MirrorsBusyState);
        Assert.False(optimizer.MirrorsBusyState);
        Assert.Equal(new[] { "Ускорение" }, PageViewModel.BusyPages(pages).Select(p => p.Title)); // the restart/update warnings name it once

        optimizer.SetBusyForTest(false, "", -1);
        Assert.Empty(PageViewModel.BusyPages(pages));
    }

    [Fact]
    public void TheRestartWarningOfSettings_NamesTheOptimizerOnly()
    {
        var optimizer = new OptimizerViewModel(AppServices.Optimizer);
        var hardware = new HardwareViewModel(optimizer);
        var settings = new SettingsViewModel(() => new AppSettings(), _ => { }, new ObservableCollection<LogEntry>());
        var questions = new List<string>();
        settings.RestartProgram = () => true;
        settings.OpenPages = () => new PageViewModel[] { hardware, optimizer };
        settings.Confirm = (_, message) =>
        {
            questions.Add(message);
            return false;
        };
        optimizer.SetBusyForTest(true, "Собираем сведения о компьютере…", -1);
        try
        {
            settings.RestartNowCommand.Execute(null);
        }
        finally
        {
            optimizer.SetBusyForTest(false, "", -1);
        }

        var message = Assert.Single(questions);
        Assert.Contains("«Ускорение»", message);
        Assert.DoesNotContain("Комплектующие", message);
    }

    [Fact]
    public void TheOptimizerPage_NoLongerSpeaksOfTheHardwareInItsSubtitle()
        => Assert.Equal("Честные советы, как сделать компьютер быстрее", new OptimizerViewModel(AppServices.Optimizer).Subtitle);
}
