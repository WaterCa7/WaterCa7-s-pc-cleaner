using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Cleaner;
using Xunit;

namespace PcCleaner.Tests.Cleaner;

public class CleanerViewModelTests
{
    [Fact]
    public async Task PreviewScan_FillsThePage_AndDeletesNothing()
    {
        using var t = new CleanerTestFolder("vm-preview");
        var service = CleanerServiceTests.CreateService(t, out var env, out var bin);
        bin.Bytes = 4096;
        bin.Items = 1;
        Directory.CreateDirectory(env.Temp);
        var junk = Path.Combine(env.Temp, "old.tmp");
        File.WriteAllBytes(junk, new byte[3000]);
        CleanerTestFolder.SetAge(junk, TimeSpan.FromDays(4));
        var vm = new CleanerViewModel(service);

        await vm.PreviewScanAsync(CancellationToken.None);

        Assert.True(vm.HasScanned);
        Assert.True(vm.IsCatalogLoaded);
        Assert.Equal(3, vm.Groups.Count);
        var temp = vm.Groups[0].Items.Single(i => i.Id == "win.usertemp");
        Assert.Equal(ScanState.Done, temp.State);
        Assert.Equal(3000, temp.Bytes);
        Assert.True(temp.IsChecked);
        Assert.True(vm.TotalBytes >= 3000 + 4096);
        Assert.Equal(3000, vm.SelectedBytes); // the Recycle Bin is not preselected
        Assert.True(File.Exists(junk));
        Assert.Equal(0, bin.EmptyCalls);
    }

    [Fact]
    public async Task Clean_DeclinedConfirmation_ChangesNothing()
    {
        using var t = new CleanerTestFolder("vm-declined");
        var service = CleanerServiceTests.CreateService(t, out var env, out var bin);
        bin.Bytes = 10;
        bin.Items = 1;
        Directory.CreateDirectory(env.Temp);
        var junk = Path.Combine(env.Temp, "old.tmp");
        File.WriteAllBytes(junk, new byte[3000]);
        CleanerTestFolder.SetAge(junk, TimeSpan.FromDays(4));
        var vm = new CleanerViewModel(service);
        await vm.PreviewScanAsync(CancellationToken.None);
        foreach (var item in vm.Groups.SelectMany(g => g.Items).Where(i => i.CanClean))
            item.IsChecked = true;

        // TestEnvironment puts Dialogs in silent mode: every confirmation is answered "No".
        await ((AsyncRelayCommand)vm.CleanCommand).ExecuteAsync();

        Assert.True(File.Exists(junk));
        Assert.Equal(0, bin.EmptyCalls);
        Assert.Null(vm.LastResult);
    }

    [Fact]
    public async Task UncheckingARecommendedCategory_IsRemembered()
    {
        using var t = new CleanerTestFolder("vm-remember");
        var settings = new AppSettings();
        var service = CleanerServiceTests.CreateService(t, out _, out _, settings);
        var vm = new CleanerViewModel(service);
        await vm.PreviewScanAsync(CancellationToken.None);
        var thumbs = vm.Groups.SelectMany(g => g.Items).Single(i => i.Id == "win.thumbcache");

        thumbs.IsChecked = false;
        Assert.Contains("win.thumbcache", settings.CleanerDisabledCategories);

        thumbs.IsChecked = true;
        Assert.DoesNotContain("win.thumbcache", settings.CleanerDisabledCategories);

        vm.UnselectAllCommand.Execute(null);
        Assert.Contains("win.usertemp", settings.CleanerDisabledCategories);
        Assert.All(vm.Groups.SelectMany(g => g.Items), i => Assert.False(i.IsChecked));

        vm.SelectRecommendedCommand.Execute(null);
        Assert.Empty(settings.CleanerDisabledCategories);
        Assert.True(thumbs.IsChecked);
        Assert.False(vm.Groups.SelectMany(g => g.Items).Single(i => i.Id == "win.recyclebin").IsChecked);

        // A new page instance starts with the remembered choice.
        settings.CleanerDisabledCategories.Add("win.thumbcache");
        var vm2 = new CleanerViewModel(service);
        await vm2.PreviewScanAsync(CancellationToken.None);
        Assert.False(vm2.Groups.SelectMany(g => g.Items).Single(i => i.Id == "win.thumbcache").IsChecked);
    }

    [Fact]
    public async Task AdminOnlyCategories_AreNotSelectableWithoutElevation()
    {
        using var t = new CleanerTestFolder("vm-admin");
        var service = CleanerServiceTests.CreateService(t, out var env, out _);
        var file = Path.Combine(env.WindowsDir, "Temp", "sys.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[500]);
        CleanerTestFolder.SetAge(file, TimeSpan.FromDays(4));
        var vm = new CleanerViewModel(service);

        await vm.PreviewScanAsync(CancellationToken.None);

        var systemTemp = vm.Groups.SelectMany(g => g.Items).Single(i => i.Id == "win.systemtemp");
        Assert.True(systemTemp.NeedsAdmin);
        Assert.False(systemTemp.CanClean);
        Assert.Equal(500, systemTemp.Bytes);
        Assert.False(systemTemp.IsSelectedForCleaning);
        Assert.Equal(500, vm.AdminOnlyBytes);
        Assert.True(vm.ShowAdminHint);
    }
}
