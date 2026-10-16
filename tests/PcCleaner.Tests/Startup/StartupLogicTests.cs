using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Startup;
using Xunit;

namespace PcCleaner.Tests.Startup;

/// <summary>Pure logic: filters, flags, target parsing, dashboard findings, confirmation rules.</summary>
public class StartupLogicTests
{
    private static readonly SignatureInfo Microsoft = new(SignatureStatus.Valid, "Microsoft Windows");
    private static readonly SignatureInfo Vendor = new(SignatureStatus.Valid, "Valve Corp.");
    private static readonly SignatureInfo Unsigned = new(SignatureStatus.Unsigned, null);

    public StartupLogicTests() => TestEnvironment.Init();

    private static StartupItem Item(string name, StartupKind kind = StartupKind.RegistryRun, bool enabled = true,
        SignatureInfo? signature = null, string? taskPath = null, bool? exists = true, int risk = 0,
        StartupScope scope = StartupScope.CurrentUser, string? target = @"C:\Program Files\App\app.exe")
    {
        var item = new StartupItem
        {
            Id = $"{kind}|{name}",
            Kind = kind,
            Scope = scope,
            Name = name,
            Command = $"\"{target}\" --flag",
            TargetFile = target,
            TaskPath = taskPath,
            ServiceName = kind == StartupKind.Service ? name : null,
            IsEnabled = enabled,
        };
        item.Signature = signature ?? Vendor;
        item.Publisher = item.Signature.Publisher;
        item.TargetExists = exists;
        if (risk > 0)
            item.Suspicion = new SuspicionResult(risk, new[] { "Тестовая причина" });
        return item;
    }

    // ---------- filters ----------

    [Fact]
    public void WindowsTasksAndMicrosoftServices_AreHiddenByDefault()
    {
        var winTask = Item("Defrag", StartupKind.ScheduledTask, signature: Microsoft, taskPath: @"\Microsoft\Windows\Defrag\ScheduledDefrag");
        var vendorTaskInMsFolder = Item("Fake", StartupKind.ScheduledTask, signature: Unsigned, taskPath: @"\Microsoft\Windows\Fake");
        var msService = Item("WinDefend", StartupKind.Service, signature: Microsoft);
        var vendorService = Item("Steam Service", StartupKind.Service, signature: Vendor);

        var defaults = new StartupFilterOptions();
        Assert.False(StartupFilter.IsVisible(winTask, defaults));
        Assert.True(StartupFilter.IsVisible(vendorTaskInMsFolder, defaults)); // classic hiding place stays visible
        Assert.False(StartupFilter.IsVisible(msService, defaults));
        Assert.True(StartupFilter.IsVisible(vendorService, defaults));

        Assert.True(StartupFilter.IsVisible(winTask, defaults with { ShowWindowsTasks = true }));
        Assert.True(StartupFilter.IsVisible(msService, defaults with { ShowMicrosoftServices = true }));
    }

    [Fact]
    public void SourceStatusAndSearchFilters_Combine()
    {
        var run = Item("Steam", enabled: true);
        var folder = Item("Telegram", StartupKind.StartupFolder, enabled: false);
        var task = Item("Updater", StartupKind.ScheduledTask, taskPath: @"\Vendor\Updater");
        var service = Item("Vendor Service", StartupKind.Service);
        var broken = Item("Old", exists: false);
        var suspicious = Item("Weird", risk: 60, signature: Unsigned);
        var all = new[] { run, folder, task, service, broken, suspicious };

        Assert.Equal(4, StartupFilter.Apply(all, new StartupFilterOptions { Source = StartupSourceFilter.RegistryAndFolders }).Count());
        Assert.Equal(new[] { task }, StartupFilter.Apply(all, new StartupFilterOptions { Source = StartupSourceFilter.Tasks }));
        Assert.Equal(new[] { service }, StartupFilter.Apply(all, new StartupFilterOptions { Source = StartupSourceFilter.Services }));
        Assert.Equal(new[] { folder }, StartupFilter.Apply(all, new StartupFilterOptions { Status = StartupStatusFilter.Disabled }));
        Assert.Equal(new[] { broken }, StartupFilter.Apply(all, new StartupFilterOptions { Status = StartupStatusFilter.Broken }));
        Assert.Equal(new[] { suspicious }, StartupFilter.Apply(all, new StartupFilterOptions { Status = StartupStatusFilter.Suspicious }));
        Assert.Equal(new[] { run }, StartupFilter.Apply(all, new StartupFilterOptions { Search = "valve steam" }));
        Assert.Equal(new[] { task }, StartupFilter.Apply(all, new StartupFilterOptions { Search = @"\vendor\" }));
        Assert.Empty(StartupFilter.Apply(all, new StartupFilterOptions { Search = "steam", Status = StartupStatusFilter.Disabled }));
        Assert.Equal(all.Length, StartupFilter.Apply(all, new StartupFilterOptions { Search = "  " }).Count());
    }

    // ---------- item flags ----------

    [Fact]
    public void ProtectionAndCapabilities()
    {
        var runOnce = Item("Once", StartupKind.RegistryRunOnce);
        Assert.False(runOnce.CanToggle);
        Assert.True(runOnce.CanDelete);
        Assert.Equal(StartupState.Pending, runOnce.State);

        var service = Item("Vendor", StartupKind.Service);
        Assert.True(service.CanToggle);
        Assert.False(service.CanDelete); // services are never deleted
        Assert.True(service.RequiresAdmin);

        var msService = Item("Ms", StartupKind.Service, signature: Microsoft);
        Assert.True(msService.IsProtected);
        Assert.False(msService.CanToggle);

        // Unsigned helper living in System32 is treated as a system file too.
        var system32 = Path.Combine(Environment.SystemDirectory, "oemhelper.exe");
        var systemService = Item("Oem", StartupKind.Service, signature: Unsigned, target: system32);
        Assert.True(systemService.IsMicrosoftService);
        Assert.False(systemService.CanToggle);

        var winTask = Item("T", StartupKind.ScheduledTask, signature: Microsoft, taskPath: @"\Microsoft\Windows\T");
        Assert.True(winTask.IsWindowsTask);
        Assert.False(winTask.CanDelete);

        var userRun = Item("U");
        Assert.False(userRun.RequiresAdmin);
        Assert.True(Item("M", scope: StartupScope.AllUsers).RequiresAdmin);
    }

    [Fact]
    public void SuspiciousTaskUnderMicrosoftFolder_IsNeverShieldedByTheInterpreterSignature()
    {
        // Classic persistence: a task hidden among Windows tasks that runs Microsoft-signed powershell.exe with a payload.
        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var task = Item("Updater", StartupKind.ScheduledTask, signature: Microsoft, target: powershell,
            taskPath: @"\Microsoft\Windows\Maintenance\Updater", risk: 65);

        Assert.True(task.IsSuspicious);
        Assert.False(task.IsWindowsTask);
        Assert.False(task.IsProtected);
        Assert.True(task.CanToggle);
        Assert.True(task.CanDelete);
        Assert.True(StartupFilter.IsVisible(task, new StartupFilterOptions())); // not hidden behind "Показать задачи Windows"
    }

    [Fact]
    public void SuspiciousServiceRunningASystemInterpreter_IsNotAWindowsService()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var service = Item("UpdSvc", StartupKind.Service, signature: Microsoft, target: cmd, risk: 65);

        Assert.False(service.IsMicrosoftService);
        Assert.True(service.CanToggle);
        Assert.False(service.CanDelete); // services are still never deleted
        Assert.True(StartupFilter.IsVisible(service, new StartupFilterOptions()));

        // The same binary without suspicious signs stays a protected Windows service.
        Assert.True(Item("Ok", StartupKind.Service, signature: Microsoft, target: cmd).IsMicrosoftService);
    }

    [Fact]
    public void WindowsTaskWithMissingOrUncheckableTarget_StaysProtected()
    {
        var notFound = new SignatureInfo(SignatureStatus.NotFound, null);
        var missing = Item("Gone", StartupKind.ScheduledTask, signature: notFound, exists: false,
            taskPath: @"\Microsoft\Windows\Feature\Gone", target: @"C:\Windows\System32\removedfeature.exe");
        var unknown = Item("Com", StartupKind.ScheduledTask, signature: notFound, exists: null,
            taskPath: @"\Microsoft\Windows\Feature\Com", target: "handler.dll");
        var vendorBroken = Item("VendorGone", StartupKind.ScheduledTask, signature: notFound, exists: false,
            taskPath: @"\Vendor\Updater", target: @"C:\Program Files\Vendor\gone.exe");

        Assert.True(missing.IsWindowsTask);
        Assert.False(missing.CanDelete);
        Assert.True(unknown.IsWindowsTask);
        Assert.False(unknown.CanToggle);
        Assert.False(vendorBroken.IsProtected);

        using var sandbox = new StartupSandbox();
        var issue = Assert.Single(sandbox.Service.BuildIssues(new[] { missing, unknown, vendorBroken }));
        Assert.Contains("(1)", issue.Title); // only the vendor task is offered for removal
        Assert.Contains("VendorGone", issue.Details);
    }

    [Fact]
    public void HiddenWindowsTasks_AreNotCountedAsPrograms()
    {
        var hidden = Item("ComTask", StartupKind.ScheduledTask, signature: new SignatureInfo(SignatureStatus.NotFound, null),
            exists: null, taskPath: @"\Microsoft\Windows\X\ComTask", target: null);
        Assert.True(hidden.IsProtected);
        Assert.Empty(StartupService.CountedPrograms(new[] { hidden }));
    }

    [Fact]
    public void TrustKey_NeverTrustsAnInterpreterAsAFile()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var script = Item("Script", target: powershell);
        var dllHost = Item("Dll", target: Path.Combine(Environment.SystemDirectory, "rundll32.exe"));
        var app = Item("App", target: @"C:\Program Files\App\app.exe");
        var unknown = Item("Unknown", StartupKind.ScheduledTask, target: null);

        Assert.Equal(script.Id, StartupService.TrustKey(script));
        Assert.Equal(dllHost.Id, StartupService.TrustKey(dllHost));
        Assert.Equal(@"C:\Program Files\App\app.exe", StartupService.TrustKey(app));
        Assert.Equal(unknown.Id, StartupService.TrustKey(unknown));
    }

    [Fact]
    public void TrustedInterpreterPath_DoesNotVouchForAScriptEntry()
    {
        using var sandbox = new StartupSandbox();
        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var app = Path.Combine(sandbox.Dir, "app.exe");
        var script = Item("Script", target: powershell);
        var program = Item("Program", target: app);
        var settings = AppServices.Settings;
        settings.TrustedItems.Add(powershell);
        settings.TrustedItems.Add(app);
        try
        {
            sandbox.Service.Reanalyze(script);
            sandbox.Service.Reanalyze(program);
            Assert.False(script.IsTrusted);
            Assert.True(program.IsTrusted);

            settings.TrustedItems.Add(script.Id);
            sandbox.Service.Reanalyze(script);
            Assert.True(script.IsTrusted);
        }
        finally
        {
            settings.TrustedItems.RemoveAll(t => t == powershell || t == app || t == script.Id);
        }
    }

    [Theory]
    [InlineData(@"%LOCALAPPDATA%\Vendor\app.exe", true)]
    [InlineData(@"""%appdata%\Vendor\app.exe"" --tray", true)]
    [InlineData(@"%UserProfile%\app.exe", true)]
    [InlineData(@"%ProgramFiles%\Vendor\app.exe", false)]
    [InlineData(@"%windir%\system32\app.exe", false)]
    [InlineData(@"C:\Users\Someone\AppData\Local\app.exe", false)]
    [InlineData(null, false)]
    public void UserSpecificVariables_AreRecognised(string? raw, bool expected)
        => Assert.Equal(expected, StartupTargets.HasUserVariable(raw));

    [Fact]
    public void ComLocalServerWithSwitches_ResolvesTheExecutable()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.Equal(cmd, StartupTargets.ComServerFile("LocalServer32", $"{cmd} -Embedding"), ignoreCase: true);
        Assert.Equal(cmd, StartupTargets.ComServerFile("LocalServer32", $"\"{cmd}\" /automation"), ignoreCase: true);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "shell32.dll"),
            StartupTargets.ComServerFile("InprocServer32", @"%SystemRoot%\System32\shell32.dll"), ignoreCase: true);
        Assert.Null(StartupTargets.ComServerFile("InprocServer32", "   "));
    }

    [Fact]
    public void TrustedItems_AreNotSuspicious()
    {
        var item = Item("Weird", risk: 70, signature: Unsigned);
        Assert.True(item.IsSuspicious);
        Assert.Equal(SuspicionLevel.High, item.RiskLevel);
        item.IsTrusted = true;
        Assert.False(item.IsSuspicious);
        Assert.Equal("Доверено", item.RiskText);
        Assert.False(item.HasReasons);
    }

    [Fact]
    public void ToggleConfirmation_Rules()
    {
        Assert.False(StartupViewModel.NeedsToggleConfirm(Item("U"), enable: false));
        Assert.False(StartupViewModel.NeedsToggleConfirm(Item("F", StartupKind.StartupFolder), enable: false));
        Assert.True(StartupViewModel.NeedsToggleConfirm(Item("M", scope: StartupScope.AllUsers), enable: false));
        Assert.False(StartupViewModel.NeedsToggleConfirm(Item("M", scope: StartupScope.AllUsers), enable: true));
        Assert.True(StartupViewModel.NeedsToggleConfirm(Item("S", StartupKind.Service), enable: false));
        Assert.True(StartupViewModel.NeedsToggleConfirm(Item("S", StartupKind.Service), enable: true));
        Assert.True(StartupViewModel.NeedsToggleConfirm(Item("T", StartupKind.ScheduledTask, taskPath: @"\T"), enable: false));
    }

    // ---------- protected changes never reach the system ----------

    [Fact]
    public async Task ProtectedOrUnelevatedServices_AreNeverChanged()
    {
        using var sandbox = new StartupSandbox(elevated: false);
        var msService = Item("WinDefend", StartupKind.Service, signature: Microsoft);
        var vendorService = Item("VendorSvc", StartupKind.Service);

        var ms = await sandbox.Service.SetEnabledAsync(msService, false, CancellationToken.None);
        var vendor = await sandbox.Service.SetEnabledAsync(vendorService, false, CancellationToken.None);
        var delete = await sandbox.Service.DeleteAsync(vendorService, CancellationToken.None);

        Assert.False(ms.Success);
        Assert.False(ms.NeedsAdmin);
        Assert.True(vendor.NeedsAdmin);
        Assert.False(delete.Success);
        Assert.True(msService.IsEnabled);
        Assert.True(vendorService.IsEnabled);
        Assert.Null(AppServices.Settings.GetModuleValue("Startup", "svc:VendorSvc"));
    }

    [Fact]
    public async Task WindowsTask_IsNeverChanged()
    {
        using var sandbox = new StartupSandbox();
        var winTask = Item("T", StartupKind.ScheduledTask, signature: Microsoft, taskPath: @"\Microsoft\Windows\T");

        var toggle = await sandbox.Service.SetEnabledAsync(winTask, false, CancellationToken.None);
        var delete = await sandbox.Service.DeleteAsync(winTask, CancellationToken.None);

        Assert.False(toggle.Success);
        Assert.False(delete.Success);
        Assert.True(winTask.IsEnabled);
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("delayed-auto", "delayed-auto")]
    [InlineData("DELAYED-AUTO", "delayed-auto")]
    [InlineData("disabled", "auto")]
    [InlineData("auto & calc.exe", "auto")]
    [InlineData(null, "auto")]
    public void RememberedServiceMode_IsSanitized(string? stored, string expected)
        => Assert.Equal(expected, StartupService.NormalizeAutoMode(stored));

    // ---------- target parsing ----------

    [Fact]
    public void Rundll32WithSwitches_ResolvesTheDll()
    {
        var system32 = Environment.SystemDirectory;
        var shell32 = Path.Combine(system32, "shell32.dll");
        Assert.Equal(shell32, StartupTargets.Resolve(@"C:\Windows\System32\rundll32.exe /d shell32.dll,Control_RunDLL"), ignoreCase: true);
        Assert.Equal(shell32, StartupTargets.Resolve("rundll32.exe shell32.dll,Control_RunDLL"), ignoreCase: true);
        Assert.Equal(@"C:\Program Files\Vendor\x.dll", StartupTargets.Resolve("rundll32.exe \"C:\\Program Files\\Vendor\\x.dll\",Start"));
        Assert.Equal(@"C:\Program Files\Vendor\y.dll", StartupTargets.Resolve(@"rundll32.exe C:\Program Files\Vendor\y.dll,Start"));
        Assert.EndsWith("rundll32.exe", StartupTargets.Resolve("rundll32.exe javascript:\"\\..\\mshtml,RunHTMLApplication\""), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"C:\Apps\tool.exe", StartupTargets.Resolve("\"C:\\Apps\\tool.exe\" -silent"));
        Assert.Null(StartupTargets.Resolve("   "));
    }

    [Theory]
    [InlineData("OneDrive Startup Task-S-1-5-21-4143620902-971273762-1401345188-1001", @"\OneDrive Startup Task-S-1-5-21-4143620902-971273762-1401345188-1001", "OneDrive Startup Task")]
    [InlineData("S-1-5-21-4143620902-971273762-1401345188-1001", @"\Agent Activation Runtime\S-1-5-21-4143620902-971273762-1401345188-1001", "Agent Activation Runtime")]
    [InlineData("GoogleUpdaterTaskSystem140.0{3B4B1A1C-1234-4C2B-9C3D-0123456789AB}", @"\GoogleUpdaterTaskSystem140.0{3B4B1A1C-1234-4C2B-9C3D-0123456789AB}", "GoogleUpdaterTaskSystem140.0")]
    [InlineData("Adobe Acrobat Update Task", @"\Adobe Acrobat Update Task", "Adobe Acrobat Update Task")]
    public void FriendlyTaskNames(string name, string path, string expected)
        => Assert.Equal(expected, StartupTargets.FriendlyTaskName(name, path));

    [Fact]
    public void TargetExists_IsConservative()
    {
        Assert.True(StartupNative.TargetExists(Path.Combine(Environment.SystemDirectory, "cmd.exe")));
        Assert.False(StartupNative.TargetExists(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nope.exe")));
        Assert.Null(StartupNative.TargetExists("nope.exe"));          // bare name: may be on PATH
        Assert.Null(StartupNative.TargetExists(@"\\server\share\x.exe")); // network: never probed
        Assert.Null(StartupNative.TargetExists("%APPDATA%\\x.exe"));   // unexpanded variable
        Assert.Null(StartupNative.TargetExists(null));
    }

    // ---------- heuristics ----------

    [Fact]
    public void GenuineDefenderBinary_IsNotAnImitation()
    {
        var path = @"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.26090.9-0\MsMpEng.exe";
        var raw = Suspicion.Analyze(path, Microsoft);
        Assert.True(raw.Score >= 60); // core heuristic alone flags the name

        Assert.Equal(SuspicionLevel.None, StartupService.WithoutGenuineMicrosoftNames(raw, Microsoft).Level);

        var fake = Suspicion.Analyze(@"C:\Users\Public\MsMpEng.exe", Unsigned);
        Assert.Equal(SuspicionLevel.High, StartupService.WithoutGenuineMicrosoftNames(fake, Unsigned).Level);

        const string hidden = "powershell.exe -w hidden -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQAIABOAGUAdAA=";
        var scripted = Suspicion.Analyze(path, Microsoft, hidden);
        Assert.True(StartupService.WithoutGenuineMicrosoftNames(scripted, Microsoft, hidden).IsSuspicious);
    }

    // ---------- dashboard findings ----------

    [Fact]
    public void ManyEnabledPrograms_ProduceOneMediumIssue()
    {
        using var sandbox = new StartupSandbox();
        var items = Enumerable.Range(1, 9).Select(i => Item($"App{i}")).ToList();
        items.Add(Item("Ms", signature: Microsoft));                         // Microsoft: not counted
        items.Add(Item("Off", enabled: false));                              // disabled: not counted
        items.Add(Item("Svc", StartupKind.Service));                         // services: not counted

        var issues = sandbox.Service.BuildIssues(items);

        var issue = Assert.Single(issues);
        Assert.Equal(IssueSeverity.Medium, issue.Severity);
        Assert.Equal("Много программ в автозагрузке (9)", issue.Title);
        Assert.Contains("App1", issue.Details);
        Assert.DoesNotContain("Off", issue.Details);
        Assert.Equal("Startup", issue.TargetPageKey);
        Assert.Null(issue.FixAsync);

        Assert.Empty(sandbox.Service.BuildIssues(items.Skip(1).ToList()));   // 8 programs: fine
    }

    [Fact]
    public void SuspiciousEnabledItems_ProduceIssuesBySeverity()
    {
        using var sandbox = new StartupSandbox();
        var items = new List<StartupItem>
        {
            Item("Evil", risk: 80, signature: Unsigned),
            Item("Odd", risk: 40, signature: Unsigned),
            Item("Low", risk: 20, signature: Unsigned),
            Item("DisabledEvil", risk: 90, enabled: false, signature: Unsigned),
        };

        var issues = sandbox.Service.BuildIssues(items);

        Assert.Equal(2, issues.Count);
        Assert.Equal(IssueSeverity.High, issues[0].Severity);
        Assert.Contains("Evil", issues[0].Title);
        Assert.Contains("Тестовая причина", issues[0].Details);
        Assert.Equal(IssueSeverity.Medium, issues[1].Severity);
        Assert.All(issues, i => Assert.Equal("Startup", i.TargetPageKey));
    }

    [Fact]
    public void BrokenButUndeletableItems_AreNotOfferedForFixing()
    {
        using var sandbox = new StartupSandbox();
        var items = new List<StartupItem>
        {
            Item("BrokenService", StartupKind.Service, exists: false),
            Item("BrokenWinTask", StartupKind.ScheduledTask, signature: Microsoft, exists: false, taskPath: @"\Microsoft\Windows\X"),
        };
        Assert.Empty(sandbox.Service.BuildIssues(items));
    }

    [Fact]
    public void Sort_PutsSuspiciousAndBrokenFirst()
    {
        var items = new[]
        {
            Item("b-normal"),
            Item("a-broken", exists: false),
            Item("c-evil", risk: 70, signature: Unsigned),
            Item("a-service", StartupKind.Service),
        };
        var sorted = StartupService.Sort(items).Select(i => i.Name).ToArray();
        Assert.Equal(new[] { "c-evil", "a-broken", "b-normal", "a-service" }, sorted);
    }
}
