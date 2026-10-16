using System.IO;
using PcCleaner.Modules.Network;
using Xunit;

namespace PcCleaner.Tests.Network;

public class NetworkToolsTests
{
    public NetworkToolsTests() => TestEnvironment.Init();

    [Fact]
    public void FailureReason_NetshIpReset_PicksTheFailedStepNotTheProgressLines()
    {
        const string output = "Resetting Compartment Forwarding, OK!\r\nResetting Global, OK!\r\nResetting Interface, OK!\r\n" +
                              "Resetting , failed.\r\nAccess is denied.\r\n\r\nResetting , OK!\r\nResetting Route, OK!\r\n" +
                              "Restart the computer to complete this action.\r\n";
        Assert.Equal("Resetting , failed. Access is denied.", NetworkService.FailureReason(output));
    }

    [Fact]
    public void FailureReason_RussianIpconfig_SkipsTheHeader()
    {
        const string output = "\r\nНастройка протокола IP для Windows\r\n\r\n" +
                              "Не удается очистить кэш сопоставителя DNS: Ошибка при выполнении функции.\r\n";
        Assert.Equal("Не удается очистить кэш сопоставителя DNS: Ошибка при выполнении функции.", NetworkService.FailureReason(output));
    }

    [Fact]
    public void FailureReason_NoErrorWords_UsesTheLastLine()
    {
        Assert.Equal("Something unexpected", NetworkService.FailureReason("Header\r\n\r\nSomething unexpected\r\n"));
        Assert.Null(NetworkService.FailureReason(""));
    }

    [Fact]
    public void DecodeConsoleOutput_HandlesUtf8AndOemCodePage()
    {
        const string text = "Сбой. Отказано в доступе.";
        Assert.Equal(text, NetworkProbes.DecodeConsoleOutput(System.Text.Encoding.UTF8.GetBytes(text)));
        var oem = PcCleaner.Core.ProcessRunner.OemEncoding;
        if (oem.CodePage == 866)
            Assert.Equal(text, NetworkProbes.DecodeConsoleOutput(oem.GetBytes(text)));
    }

    [Fact]
    public void IsWindowsPath_ExcludesTheWindowsTempFolder()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.True(ProcessIdentities.IsWindowsPath(Path.Combine(windows, "System32", "svchost.exe")));
        Assert.False(ProcessIdentities.IsWindowsPath(Path.Combine(windows, "Temp", "update.exe")));
        Assert.False(ProcessIdentities.IsWindowsPath(@"C:\Program Files\App\app.exe"));
    }

    [Fact]
    public void TrafficSample_ListsAdaptersEvenWithoutCounters()
    {
        var counters = new Dictionary<string, TrafficCounters> { ["{a}"] = new(1, 2) };
        Assert.Equal(new[] { "{a}" }, new TrafficSample(counters, 0).AdapterIds);
        var sample = new TrafficSample(counters, 0) { AdapterIds = new[] { "{a}", "{b}" } };
        Assert.Contains("{b}", sample.AdapterIds);
    }
}
