using System.Net.NetworkInformation;
using PcCleaner.Modules.Network;
using Xunit;
using static PcCleaner.Tests.Network.NetworkTestData;

namespace PcCleaner.Tests.Network;

public class NetworkViewModelTests
{
    public NetworkViewModelTests() => TestEnvironment.Init();

    // Private addresses only: public ones would start background reverse DNS lookups.
    private static SocketSnapshot LanSnapshot() => Snapshot(
        Connection("192.168.1.20", 445, Chrome.Pid, localPort: 50001),
        Connection("192.168.1.20", 445, Chrome.Pid, localPort: 50002),
        Connection("192.168.1.21", 8009, Chrome.Pid),
        Connection("10.0.0.5", 22, Spotify.Pid),
        Connection("127.0.0.1", 9222, Chrome.Pid),
        Listen("0.0.0.0", 3306, MySql.Pid),
        Listen("127.0.0.1", 5939, Spotify.Pid),
        Listen("0.0.0.0", 135, Svchost.Pid));

    [Fact]
    public void Construct_HasTitleAndKey()
    {
        var vm = new NetworkViewModel(new NetworkService());
        Assert.Equal("Сеть", vm.Title);
        Assert.Equal("Network", vm.Key);
        Assert.False(string.IsNullOrWhiteSpace(vm.Subtitle));
    }

    [Fact]
    public void ApplySockets_GroupsByProgramAndCountsConnections()
    {
        var vm = new NetworkViewModel(new NetworkService());
        vm.ApplySockets(LanSnapshot());

        var groups = vm.ConnectionRows.OfType<ConnectionGroupItem>().ToList();
        Assert.Equal(2, groups.Count); // loopback hidden by default
        var chrome = groups.Single(g => g.ProcessName == "chrome.exe");
        Assert.Equal(3, chrome.Count);
        Assert.Equal(4, vm.VisibleConnections);
        // Small groups are expanded: rows follow their program.
        var index = vm.ConnectionRows.IndexOf(chrome);
        Assert.IsType<ConnectionItem>(vm.ConnectionRows[index + 1]);
        Assert.Contains(vm.ConnectionRows.OfType<ConnectionItem>(), c => c.Count == 2 && c.RemotePort == 445);
        Assert.StartsWith("Соединения · 4", vm.ConnectionsTabHeader);
    }

    [Fact]
    public void GroupRow_SummarizesCountAndState()
    {
        var vm = new NetworkViewModel(new NetworkService());
        vm.ApplySockets(Snapshot(
            Connection("192.168.1.20", 445, Chrome.Pid, localPort: 50001),
            Connection("192.168.1.20", 445, Chrome.Pid, localPort: 50002),
            Connection("192.168.1.21", 8009, Chrome.Pid, TcpState.SynSent),
            Connection("10.0.0.5", 22, Spotify.Pid, TcpState.SynSent),
            Connection("192.168.1.22", 443, Chrome.Pid, TcpState.FinWait2))); // closing: not shown

        var chrome = vm.ConnectionRows.OfType<ConnectionGroupItem>().Single(g => g.ProcessName == "chrome.exe");
        Assert.Equal("3", chrome.CountText);
        Assert.Equal("Установлено", chrome.StateText);
        Assert.Contains("подключаются: 1", chrome.StateToolTip);
        Assert.Contains("3 соединения", chrome.CountToolTip);

        var spotify = vm.ConnectionRows.OfType<ConnectionGroupItem>().Single(g => g.ProcessName == "Spotify.exe");
        Assert.Equal("Подключается", spotify.StateText);

        var merged = vm.ConnectionRows.OfType<ConnectionItem>().Single(c => c.RemotePort == 445);
        Assert.True(merged.HasCount);
        Assert.Equal("×2", merged.CountText);
        Assert.False(vm.ConnectionRows.OfType<ConnectionItem>().Single(c => c.RemotePort == 8009).HasCount);
        Assert.Equal("локальная сеть", merged.HostName); // private addresses are never looked up
    }

    [Fact]
    public void ShowLocalConnections_AddsLoopback()
    {
        var vm = new NetworkViewModel(new NetworkService());
        vm.ApplySockets(LanSnapshot());
        vm.ShowLocalConnections = true;
        Assert.Equal(5, vm.VisibleConnections);
    }

    [Fact]
    public void Search_FiltersConnectionsAndPorts()
    {
        var vm = new NetworkViewModel(new NetworkService());
        vm.ApplySockets(LanSnapshot());

        vm.SearchText = "10.0.0.5";
        var group = Assert.Single(vm.ConnectionRows.OfType<ConnectionGroupItem>());
        Assert.Equal("Spotify.exe", group.ProcessName);
        Assert.Equal(1, vm.VisibleConnections);

        vm.SearchText = "3306";
        Assert.Equal(1, vm.VisiblePorts);
        vm.SearchText = "";
        Assert.Equal(2, vm.VisiblePorts); // 5939 is local-only and hidden by default
        vm.OnlyNetworkPorts = false;
        Assert.Equal(3, vm.VisiblePorts);
    }

    [Fact]
    public void ToggleGroup_CollapsesAndExpands()
    {
        var vm = new NetworkViewModel(new NetworkService());
        vm.ApplySockets(LanSnapshot());
        var chrome = vm.ConnectionRows.OfType<ConnectionGroupItem>().Single(g => g.ProcessName == "chrome.exe");
        var before = vm.ConnectionRows.Count;

        vm.ToggleGroupCommand.Execute(chrome);
        Assert.False(chrome.IsExpanded);
        Assert.Equal(before - 2, vm.ConnectionRows.Count);

        vm.ToggleGroupCommand.Execute(chrome);
        Assert.Equal(before, vm.ConnectionRows.Count);
    }

    [Fact]
    public void SortConnections_KeepsProgramsTogether()
    {
        var vm = new NetworkViewModel(new NetworkService());
        vm.ApplySockets(LanSnapshot());
        vm.SortConnections(ConnectionSortKeys.Address);
        Assert.Equal(ConnectionSortKeys.Address, vm.ConnectionSortKey);
        Assert.False(vm.ConnectionSortDescending);

        // Every connection row belongs to the program row above it.
        ConnectionGroupItem? current = null;
        foreach (var row in vm.ConnectionRows)
        {
            if (row is ConnectionGroupItem g)
                current = g;
            else
                Assert.Equal(current!.GroupKey, row.GroupKey);
        }

        vm.SortConnections(ConnectionSortKeys.Address);
        Assert.True(vm.ConnectionSortDescending);
    }

    [Fact]
    public void Tools_AreDeclinedInSilentMode_AndChangeNothing()
    {
        var vm = new NetworkViewModel(new NetworkService());
        // Dialogs are in silent mode (every confirmation = "No"): nothing runs and no message appears.
        vm.FlushDnsCommand.Execute(null);
        Assert.Null(vm.Message);
    }

    [Fact]
    public void ReadSockets_RealSystem_Works()
    {
        // Read-only: TCP/UDP tables and process names of the real system.
        var snapshot = new NetworkService().ReadSocketsAsync().GetAwaiter().GetResult();
        Assert.NotEmpty(snapshot.Entries);
        Assert.All(snapshot.Entries.Select(e => e.Pid).Distinct(), pid => Assert.True(snapshot.Processes.ContainsKey(pid)));
        Assert.Contains(snapshot.Entries, e => e.State == TcpState.Listen);
    }

    [Fact]
    public void GetAdapters_RealSystem_SkipsFilterDriversAndLoopback()
    {
        var adapters = new NetworkService().GetAdaptersAsync().GetAwaiter().GetResult();
        Assert.All(adapters, a =>
        {
            Assert.DoesNotContain("-0000", a.Name);
            Assert.DoesNotContain("Miniport", a.Description);
            Assert.NotEqual(NetworkInterfaceType.Loopback, a.Type);
        });
    }
}
