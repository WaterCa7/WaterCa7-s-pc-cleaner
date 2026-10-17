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
    public async Task ResetNetwork_CancelDuringFirstStep_LetsNetshFinish_SkipsTheRest_AndSaysWhatWasReset()
    {
        using var cts = new CancellationTokenSource();
        var started = new List<string>();
        var tokens = new List<CancellationToken>();

        var (ok, message) = await NetworkService.ResetStepsAsync((args, token) =>
        {
            started.Add(args);
            tokens.Add(token);
            cts.Cancel(); // the user presses "Отмена" while netsh runs
            return Task.FromResult((true, "Sucessfully reset the Winsock Catalog.", 0));
        }, cts.Token);

        Assert.Equal(new[] { "winsock reset" }, started); // "int ip reset" never started
        Assert.All(tokens, t => Assert.False(t.CanBeCanceled)); // a running netsh is never killed
        Assert.False(ok);
        Assert.Contains("Сброс остановлен", message);
        Assert.Contains("netsh winsock reset", message);
        Assert.Contains("netsh int ip reset", message);
        Assert.Contains("Перезагрузите компьютер", message);
    }

    [Fact]
    public async Task ResetNetwork_CancelledBeforeTheFirstStep_ChangesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var started = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NetworkService.ResetStepsAsync((_, _) =>
        {
            started++;
            return Task.FromResult((true, "", 0));
        }, cts.Token));
        Assert.Equal(0, started);
    }

    [Fact]
    public async Task ResetNetwork_BothSteps_Succeed()
    {
        var started = new List<string>();
        var (ok, message) = await NetworkService.ResetStepsAsync((args, _) =>
        {
            started.Add(args);
            return Task.FromResult((true, "OK", 0));
        }, CancellationToken.None);
        Assert.True(ok);
        Assert.Equal(new[] { "winsock reset", "int ip reset" }, started);
        Assert.Contains("Перезагрузите компьютер", message);
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
