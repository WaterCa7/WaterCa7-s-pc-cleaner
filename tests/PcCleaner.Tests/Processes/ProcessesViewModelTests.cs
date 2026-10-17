using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Processes;
using Xunit;
using static PcCleaner.Tests.Processes.ProcessTestData;

namespace PcCleaner.Tests.Processes;

public class ProcessFilterTests
{
    public ProcessFilterTests() => TestEnvironment.Init();

    private static readonly SignatureInfo MsSigned = new(SignatureStatus.Valid, "Microsoft Windows");
    private static readonly SignatureInfo VendorSigned = new(SignatureStatus.Valid, "Google LLC");

    [Fact]
    public void Search_MatchesNameDescriptionPublisherPathAndPid()
    {
        var row = Row(4242, "chrome.exe", ChromePath, VendorSigned, description: "Google Chrome", company: "Google LLC");
        Assert.True(new ProcessFilter("chrome", false, false).Matches(row));
        Assert.True(new ProcessFilter("GOOGLE", false, false).Matches(row));
        Assert.True(new ProcessFilter("program files", false, false).Matches(row));
        Assert.True(new ProcessFilter("4242", false, false).Matches(row));
        Assert.True(new ProcessFilter("google chrome", false, false).Matches(row)); // all words must match
        Assert.False(new ProcessFilter("firefox", false, false).Matches(row));
        Assert.False(new ProcessFilter("google firefox", false, false).Matches(row));
        Assert.True(new ProcessFilter("   ", false, false).Matches(row));
    }

    [Fact]
    public void SuspiciousOnly_KeepsMediumAndHigh()
    {
        var clean = Row(1, "a.exe", @"C:\Apps\a.exe", VendorSigned);
        var low = Row(2, "b.exe", @"C:\Apps\b.exe", suspicion: new SuspicionResult(20, new[] { "Нет цифровой подписи" }));
        var medium = Row(3, "c.exe", @"C:\Apps\c.exe", suspicion: new SuspicionResult(40, new[] { "x" }));
        var high = Row(4, "d.exe", @"C:\Apps\d.exe", suspicion: new SuspicionResult(80, new[] { "x" }));
        var filter = new ProcessFilter(null, suspiciousOnly: true, hideMicrosoft: false);
        Assert.False(filter.Matches(clean));
        Assert.False(filter.Matches(low));
        Assert.True(filter.Matches(medium));
        Assert.True(filter.Matches(high));
    }

    [Fact]
    public void HideMicrosoft_HidesOnlyVerifiedWindowsComponents()
    {
        var filter = new ProcessFilter(null, false, hideMicrosoft: true);
        Assert.False(filter.Matches(Row(1, "svchost.exe", SvchostPath, MsSigned)));
        Assert.False(filter.Matches(Row(2, "csrss.exe", null))); // known Windows process without access
        Assert.False(filter.Matches(Row(4, "System", null)));
        // Microsoft apps outside the Windows folder stay visible.
        Assert.True(filter.Matches(Row(3, "msedge.exe", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", MsSigned)));
        // A file that only *claims* to be in Windows but has no Microsoft signature stays visible.
        Assert.True(filter.Matches(Row(5, "svchost.exe", SvchostPath, new SignatureInfo(SignatureStatus.Unsigned, null))));
        // While the signature is still being checked nothing is hidden.
        Assert.True(filter.Matches(Row(6, "svchost.exe", SvchostPath, signature: null)));
        // Unknown inaccessible processes are not assumed to be Microsoft.
        Assert.True(filter.Matches(Row(7, "mystery.exe", null)));
    }

    [Fact]
    public void HideMicrosoft_NeverHidesSuspiciousWindowsComponents()
    {
        // PowerShell from C:\Windows running a hidden encoded script is a Windows component, but it must stay visible.
        var path = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var hidden = new SuspicionResult(65, new[] { "Скрытый или закодированный запуск PowerShell" });
        var bad = Row(8, "powershell.exe", path, MsSigned, hidden);
        var calm = Row(9, "powershell.exe", path, MsSigned);
        Assert.True(new ProcessFilter(null, false, hideMicrosoft: true).Matches(bad));
        Assert.True(new ProcessFilter(null, suspiciousOnly: true, hideMicrosoft: true).Matches(bad));
        Assert.False(new ProcessFilter(null, false, hideMicrosoft: true).Matches(calm));

        var group = new ProcessGroup("path:" + path.ToLowerInvariant(), "powershell.exe");
        group.SetInstances(new List<ProcessRow> { calm, bad });
        var hideMs = new ProcessFilter(null, false, hideMicrosoft: true);
        Assert.True(hideMs.ShowChild(group, bad));
        Assert.False(hideMs.ShowChild(group, calm));
    }

    [Fact]
    public void ExpandedGroup_ShowsMatchingChildren()
    {
        var group = new ProcessGroup("path:x", "powershell.exe");
        var calm = Row(10, "powershell.exe", @"C:\Tools\powershell.exe");
        var bad = Row(11, "powershell.exe", @"C:\Tools\powershell.exe", suspicion: new SuspicionResult(70, new[] { "Скрытый запуск" }));
        group.SetInstances(new List<ProcessRow> { calm, bad });
        group.Path = @"C:\Tools\powershell.exe";

        var suspicious = new ProcessFilter(null, true, false);
        Assert.False(suspicious.ShowChild(group, calm));
        Assert.True(suspicious.ShowChild(group, bad));

        // The search matched the program itself, so all its processes are listed.
        var byName = new ProcessFilter("powershell", false, false);
        Assert.True(byName.ShowChild(group, calm));
        // The search matched one PID only.
        var byPid = new ProcessFilter("11", false, false);
        Assert.True(byPid.ShowChild(group, bad));
        Assert.False(byPid.ShowChild(group, calm));
        Assert.True(byPid.Matches(group));
    }
}

public class ProcessesViewModelTests
{
    public ProcessesViewModelTests() => TestEnvironment.Init();

    private static async Task<(ProcessesViewModel Vm, FakeProcessSource Source, FakeClock Clock, FakeDetails Details)> LoadAsync()
    {
        var (service, source, details, clock) = CreateService();
        AddTypicalProcesses(source);
        details.Processes.Add(new WmiProcessRow(100, 0, "\"" + ChromePath + "\" --no-startup-window", ChromePath, null));
        details.Services.Add(new WmiServiceRow(201, "Spooler", "Диспетчер печати", SvchostPath + " -k LocalService"));
        var vm = new ProcessesViewModel(service) { BaselineDelay = TimeSpan.Zero };
        await vm.RefreshAsync();
        await vm.WaitForBackgroundAsync(CancellationToken.None);
        return (vm, source, clock, details);
    }

    private static ProcessGroup GroupOf(ProcessesViewModel vm, string exe)
        => vm.Groups.Single(g => string.Equals(g.ExeName, exe, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task GroupsByProgramByDefault()
    {
        var (vm, _, _, _) = await LoadAsync();
        Assert.True(vm.GroupByProgram);
        Assert.Equal(7, vm.ProcessCount);
        Assert.Equal(4, vm.ProgramCount); // System, chrome, svchost (incl. the inaccessible one), evilupdater
        Assert.All(vm.Items, i => Assert.True(i.IsGroup));

        var chrome = GroupOf(vm, "chrome.exe");
        Assert.Equal(3, chrome.Count);
        Assert.Equal(600_000_000, chrome.Memory);
        Assert.Equal("Google Chrome", chrome.FriendlyName);
        Assert.Equal("Google LLC", chrome.PublisherText);

        var svchost = GroupOf(vm, "svchost.exe");
        Assert.Equal(2, svchost.Count);
        Assert.True(svchost.IsProtected);
        Assert.False(svchost.CanKill);
    }

    [Fact]
    public async Task InaccessibleServiceProcess_GetsPathAndServicesFromServiceConfiguration()
    {
        var (vm, _, _, _) = await LoadAsync();
        var row = vm.Rows.Single(r => r.Pid == 201);
        Assert.Equal(SvchostPath, row.Path);
        Assert.Equal(new[] { "Диспетчер печати" }, row.Services);
        Assert.NotNull(row.Signature);
        Assert.Equal("\"" + ChromePath + "\" --no-startup-window", vm.Rows.Single(r => r.Pid == 100).CommandLine);
    }

    [Fact]
    public async Task SuspiciousProcessIsCountedAndExplained()
    {
        var (vm, _, _, _) = await LoadAsync();
        Assert.Equal(1, vm.SuspiciousCount);
        var evil = GroupOf(vm, "evilupdater.exe");
        Assert.True(evil.IsSuspicious);
        Assert.Contains(evil.Reasons, r => r.Contains("Temp"));
        Assert.Contains(evil.Reasons, r => r.Contains("подписи"));
        Assert.True(evil.CanQuarantine);
        Assert.False(GroupOf(vm, "chrome.exe").CanQuarantine);
        Assert.False(GroupOf(vm, "chrome.exe").IsSuspicious);
    }

    [Fact]
    public async Task NewProcesses_AskWmiOnlyAboutTheirOwnServices()
    {
        // Every live refresh that saw a new process used to enumerate all services of the system.
        var (vm, source, clock, details) = await LoadAsync();
        Assert.NotEmpty(details.ServiceQueries);
        Assert.All(details.ServiceQueries, q => Assert.NotNull(q)); // a handful of processes: always filtered
        Assert.Contains(details.ServiceQueries, q => q!.Contains(201));

        details.ServiceQueries.Clear();
        source.Add(400, "svchost.exe", null, memory: 5_000_000);
        details.Services.Add(new WmiServiceRow(400, "BITS", "Фоновая интеллектуальная служба передачи (BITS)",
            SvchostPath + " -k netsvcs"));
        clock.Advance(TimeSpan.FromSeconds(2));
        await vm.RefreshAsync();
        await vm.WaitForBackgroundAsync(CancellationToken.None);

        Assert.Equal(new[] { 400 }, Assert.Single(details.ServiceQueries));
        var row = vm.Rows.Single(r => r.Pid == 400);
        Assert.Equal(SvchostPath, row.Path);
        Assert.Equal(new[] { "Фоновая интеллектуальная служба передачи (BITS)" }, row.Services);
    }

    [Fact]
    public void WmiPidFilter_NamesEachPid_OrNothingWhenThereAreTooMany()
    {
        Assert.Equal("ProcessId = 7 OR ProcessId = 1234", WmiDetailsProvider.PidFilter(new[] { 7, 1234 }));
        Assert.Null(WmiDetailsProvider.PidFilter(null));
        Assert.Null(WmiDetailsProvider.PidFilter(Array.Empty<int>()));
        Assert.Null(WmiDetailsProvider.PidFilter(Enumerable.Range(1, WmiDetailsProvider.MaxFilteredPids + 1).ToList()));
        Assert.NotNull(WmiDetailsProvider.PidFilter(Enumerable.Range(1, WmiDetailsProvider.MaxFilteredPids).ToList()));
    }

    [Fact]
    public async Task Refresh_UpdatesRowsInPlace_AddsAndRemoves()
    {
        var (vm, source, clock, _) = await LoadAsync();
        var chromeMain = vm.Rows.Single(r => r.Pid == 100);
        var chromeGroup = GroupOf(vm, "chrome.exe");

        clock.Advance(TimeSpan.FromSeconds(2));
        source.AddCpu(100, TimeSpan.TicksPerSecond); // 1 s of CPU in 2 s on 4 cores = 12,5%
        source.SetMemory(100, 400_000_000);
        source.Remove(102);
        source.Add(400, "notepad.exe", @"C:\Windows\notepad.exe", memory: 5_000_000);
        await vm.RefreshAsync();
        await vm.WaitForBackgroundAsync(CancellationToken.None);

        Assert.Same(chromeMain, vm.Rows.Single(r => r.Pid == 100));
        Assert.Equal(12.5, chromeMain.Cpu, 6);
        Assert.Equal(400_000_000, chromeMain.Memory);
        Assert.Same(chromeGroup, GroupOf(vm, "chrome.exe"));
        Assert.Equal(2, chromeGroup.Count);
        Assert.Equal(12.5, chromeGroup.Cpu, 6);
        Assert.DoesNotContain(vm.Rows, r => r.Pid == 102);
        Assert.Contains(vm.Rows, r => r.Pid == 400);
        Assert.Contains(vm.Items, i => i.ExeName == "notepad.exe");
    }

    [Fact]
    public async Task Selection_SurvivesRefreshAndGroupingToggle()
    {
        var (vm, source, clock, _) = await LoadAsync();
        var chrome = GroupOf(vm, "chrome.exe");
        vm.SelectedItem = chrome;

        clock.Advance(TimeSpan.FromSeconds(2));
        source.AddCpu(300, 5 * TimeSpan.TicksPerSecond); // reorders the list when sorted by CPU
        vm.SortBy("Cpu");
        await vm.RefreshAsync();
        Assert.Same(chrome, vm.SelectedItem);

        vm.GroupByProgram = false;
        Assert.IsType<ProcessRow>(vm.SelectedItem);
        Assert.Equal(100, ((ProcessRow)vm.SelectedItem!).Pid); // the program's main process
        Assert.All(vm.Items, i => Assert.False(i.IsGroup));

        vm.GroupByProgram = true;
        Assert.Same(chrome, vm.SelectedItem);
    }

    [Fact]
    public async Task ExpandingAGroup_ListsItsProcessesUnderneath()
    {
        var (vm, _, _, _) = await LoadAsync();
        var chrome = GroupOf(vm, "chrome.exe");
        vm.ToggleExpand(chrome);
        var index = vm.Items.IndexOf(chrome);
        var children = vm.Items.Skip(index + 1).Take(3).ToList();
        Assert.All(children, c => Assert.True(c.IsChild));
        Assert.Equal(new[] { 100, 101, 102 }.OrderBy(x => x), children.Cast<ProcessRow>().Select(r => r.Pid).OrderBy(x => x));

        vm.ToggleExpand(chrome);
        Assert.DoesNotContain(vm.Items, i => i.IsChild);
    }

    [Fact]
    public async Task Filters_ApplyToTheList()
    {
        var (vm, _, _, _) = await LoadAsync();

        vm.SuspiciousOnly = true;
        Assert.Equal(new[] { "evilupdater.exe" }, vm.Items.Select(i => i.ExeName));

        vm.SuspiciousOnly = false;
        vm.SearchText = "chrome";
        Assert.Equal(new[] { "chrome.exe" }, vm.Items.Select(i => i.ExeName));

        vm.SearchText = "Диспетчер печати"; // service hosted by an svchost instance
        Assert.Equal(new[] { "svchost.exe" }, vm.Items.Select(i => i.ExeName));

        vm.SearchText = "";
        vm.HideMicrosoft = true;
        Assert.DoesNotContain(vm.Items, i => i.ExeName is "svchost.exe" or "System");
        Assert.Contains(vm.Items, i => i.ExeName == "chrome.exe");

        vm.SearchText = "nothing-like-this";
        Assert.True(vm.IsEmpty);
        Assert.True(vm.ShowResetFilters);
        vm.ResetFiltersCommand.Execute(null);
        Assert.Equal(4, vm.Items.Count);
    }

    [Fact]
    public async Task Sorting_ByMemoryAndName()
    {
        var (vm, _, _, _) = await LoadAsync();
        // Default: memory, largest first.
        Assert.Equal("chrome.exe", vm.Items[0].ExeName);
        Assert.Equal("System", vm.Items[^1].ExeName);

        vm.SortBy("Name");
        var names = vm.Items.Select(i => i.PrimaryText).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), names);

        vm.SortBy("Name"); // second click flips the direction
        names = vm.Items.Select(i => i.PrimaryText).ToList();
        Assert.Equal(names.OrderByDescending(n => n, StringComparer.CurrentCultureIgnoreCase), names);
    }

    [Fact]
    public async Task ConfirmationIsRequired_NothingHappensWhenDeclined()
    {
        // Dialogs are in silent mode in tests: every confirmation is "No".
        var (vm, source, _, _) = await LoadAsync();
        var evil = GroupOf(vm, "evilupdater.exe");
        vm.SelectedItem = evil;
        await vm.KillCommand.ExecuteAsync(null);
        await vm.QuarantineCommand.ExecuteAsync(null);
        vm.TrustCommand.Execute(null);
        Assert.Contains(source.Enumerate(), p => p.Pid == 300);
        Assert.False(AppServices.Settings.IsTrusted(EvilPath));
        Assert.True(evil.IsSuspicious);
    }

    [Fact]
    public void MicrosoftSignedScriptHost_CannotBeTrusted()
    {
        // The warning is about how PowerShell was started, not about the file: trusting powershell.exe would hide
        // every future hidden script in all modules.
        var msSigned = new SignatureInfo(SignatureStatus.Valid, "Microsoft Windows");
        var hidden = new SuspicionResult(65, new[] { "Скрытый или закодированный запуск PowerShell" });
        var powershell = Row(50, "powershell.exe", Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            msSigned, hidden);
        Assert.False(powershell.CanTrust);
        Assert.Contains("Microsoft", powershell.TrustToolTip);

        // An unsigned program with warnings can be trusted; an already trusted file can always be "un-trusted".
        var unsigned = Row(51, "tool.exe", @"C:\Tools\tool.exe", new SignatureInfo(SignatureStatus.Unsigned, null),
            new SuspicionResult(20, new[] { "Нет цифровой подписи" }));
        Assert.True(unsigned.CanTrust);
        powershell.IsTrusted = true;
        Assert.True(powershell.CanTrust);
    }

    [Fact]
    public async Task TrustChangedOnAnotherPage_IsPickedUpOnTheNextRefresh()
    {
        var (service, source, _, clock) = CreateService();
        var path = Path.Combine(Path.GetTempPath(), "unsigned-external-" + Guid.NewGuid().ToString("N")[..6] + ".exe");
        source.Add(500, Path.GetFileName(path), path);
        var vm = new ProcessesViewModel(service) { BaselineDelay = TimeSpan.Zero };
        await vm.RefreshAsync();
        await vm.WaitForBackgroundAsync(CancellationToken.None);
        Assert.Equal(1, vm.SuspiciousCount);

        var settings = AppServices.Settings;
        settings.AddTrusted(path); // e.g. "Доверять" on the Startup page
        try
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await vm.RefreshAsync();
            var row = vm.Rows.Single(r => r.Pid == 500);
            Assert.True(row.IsTrusted);
            Assert.False(row.IsSuspicious);
            Assert.Equal(0, vm.SuspiciousCount);
        }
        finally
        {
            settings.RemoveTrusted(path);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition(), what);
    }

    [Fact]
    public async Task LiveRefresh_RestsWhileTheWindowIsMinimized_AndCatchesUpWhenItIsBack()
    {
        var (service, source, details, clock) = CreateService();
        AddTypicalProcesses(source);
        var vm = new ProcessesViewModel(service) { BaselineDelay = TimeSpan.Zero };
        await vm.OnNavigatedToAsync();
        await WaitUntilAsync(() => vm.HasLoaded && !vm.IsBusy, "first load");
        Assert.True(vm.IsLiveRefreshDue);

        // Minimized: the 2-second ticks do nothing, so new processes cost no WMI queries or signature checks.
        vm.SetWindowHidden(true);
        Assert.False(vm.IsLiveRefreshDue);
        var queries = details.ProcessQueries;
        source.Add(600, "helper.exe", @"C:\Apps\helper.exe");

        // Restored: refreshed at once (not after the next tick), including the new process's details.
        clock.Advance(TimeSpan.FromSeconds(30));
        vm.SetWindowHidden(false);
        Assert.True(vm.IsLiveRefreshDue);
        await WaitUntilAsync(() => vm.Rows.Any(r => r.Pid == 600) && !vm.IsRefreshing, "refresh after restore");
        await vm.WaitForBackgroundAsync(CancellationToken.None);
        Assert.True(details.ProcessQueries > queries);

        // Paused by the user or another page open: restoring the window does not refresh.
        vm.IsPaused = true;
        vm.SetWindowHidden(true);
        source.Add(601, "other.exe", @"C:\Apps\other.exe");
        vm.SetWindowHidden(false);
        Assert.False(vm.IsLiveRefreshDue);
        vm.IsPaused = false; // un-pausing refreshes by itself
        await WaitUntilAsync(() => vm.Rows.Any(r => r.Pid == 601) && !vm.IsRefreshing, "refresh after un-pausing");

        vm.OnNavigatedFrom();
        vm.SetWindowHidden(true);
        source.Add(602, "late.exe", @"C:\Apps\late.exe");
        vm.SetWindowHidden(false);
        Assert.False(vm.IsLiveRefreshDue);
        await Task.Delay(200);
        Assert.DoesNotContain(vm.Rows, r => r.Pid == 602);
    }

    [Fact]
    public void StatusIconToolTip_NamesTheStatus()
    {
        var low = Row(1, "b.exe", @"C:\Apps\b.exe", new SignatureInfo(SignatureStatus.Unsigned, null),
            new SuspicionResult(20, new[] { "Нет цифровой подписи" }));
        Assert.StartsWith("Внимание. ", low.StatusIconToolTip);

        var pending = Row(2, "c.exe", @"C:\Apps\c.exe", signature: null, suspicion: null);
        pending.Suspicion = null;
        Assert.StartsWith("Проверка… ", pending.StatusIconToolTip);

        var noAccess = Row(3, "mystery.exe", null);
        Assert.StartsWith("Нет доступа. ", noAccess.StatusIconToolTip);

        var clean = Row(4, "a.exe", @"C:\Apps\a.exe", new SignatureInfo(SignatureStatus.Valid, "Vendor"));
        Assert.Null(clean.StatusIconToolTip);
    }

    [Fact]
    public async Task GroupedSearch_FindsProgramsByCommandLine()
    {
        var (vm, _, _, _) = await LoadAsync();
        Assert.True(vm.GroupByProgram);
        vm.SearchText = "--no-startup-window"; // only in chrome's command line
        Assert.Equal(new[] { "chrome.exe" }, vm.Items.Select(i => i.ExeName));
    }

    [Fact]
    public async Task KillConfirmation_NamesTheProcessAndWarnsAboutData()
    {
        var (vm, _, _, _) = await LoadAsync();
        var chrome = GroupOf(vm, "chrome.exe");
        var message = ProcessesViewModel.BuildKillMessage(chrome, chrome.Instances.ToList(), chrome.Instances.ToList(),
            new List<ProcessRow>(), tree: false);
        Assert.Contains("Google Chrome", message);
        Assert.Contains("100", message);
        Assert.Contains("Несохранённые данные", message);
    }
}
