using System.IO;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Startup;
using Xunit;

namespace PcCleaner.Tests.Startup;

/// <summary>
/// The checkbox column of «Автозагрузка» and «Отключить / Включить / Удалить» for the checked entries: what the user sees as
/// checked is exactly what the action touches. Real entries live in the <see cref="StartupSandbox"/> (HKCU test keys, temp
/// folders, its RecycleFile). Scheduled tasks and services are never run: they are made by hand and only reach the planning
/// and the question (or fail on an invalid service name before touching anything).
/// </summary>
public class StartupCheckedTests
{
    private static readonly SignatureInfo Microsoft = new(SignatureStatus.Valid, "Microsoft Windows");
    private static readonly SignatureInfo Vendor = new(SignatureStatus.Valid, "Valve Corp.");

    public StartupCheckedTests() => TestEnvironment.Init();

    /// <summary>Records every question and message, and answers as told. Nothing is shown, restarted or created for real.</summary>
    private sealed class Seams
    {
        public List<(string Title, string Message)> Questions { get; } = new();

        public List<(string Title, string Message)> Infos { get; } = new();

        public List<string> ElevationRequests { get; } = new();

        public List<string> RestorePoints { get; } = new();

        public bool Answer { get; set; } = true;

        /// <summary>The answer to the restart question: true = the user agreed and the restart as administrator has started.</summary>
        public bool RestartStarts { get; set; }

        public string Question => Assert.Single(Questions).Message;

        public (string Title, string Message) Info => Assert.Single(Infos);

        public StartupViewModel Wire(StartupViewModel vm)
        {
            vm.Confirm = (title, message) =>
            {
                Questions.Add((title, message));
                return Answer;
            };
            vm.ShowInfo = (title, message) => Infos.Add((title, message));
            vm.RequireElevation = action =>
            {
                ElevationRequests.Add(action);
                return RestartStarts; // false: "Нет" / the restart was not made
            };
            vm.CreateRestorePoint = (description, _) =>
            {
                RestorePoints.Add(description);
                return Task.FromResult(true);
            };
            return vm;
        }
    }

    private static StartupItem Manual(string name, StartupKind kind = StartupKind.RegistryRun, bool enabled = true,
        SignatureInfo? signature = null, string? taskPath = null, StartupScope scope = StartupScope.CurrentUser,
        bool runsAsCurrentUser = false, string? serviceName = null, bool exists = true)
    {
        var item = new StartupItem
        {
            Id = $"{kind}|{name}",
            Kind = kind,
            Scope = kind == StartupKind.Service ? StartupScope.AllUsers : scope,
            Name = name,
            Command = @"""C:\Program Files\App\app.exe"" --flag",
            TargetFile = @"C:\Program Files\App\app.exe",
            TaskPath = kind == StartupKind.ScheduledTask ? taskPath ?? $@"\Vendor\{name}" : null,
            TaskRunsAsCurrentUser = runsAsCurrentUser,
            ServiceName = kind == StartupKind.Service ? serviceName ?? name : null,
            IsEnabled = enabled,
        };
        item.Signature = signature ?? Vendor;
        item.Publisher = item.Signature.Publisher;
        item.TargetExists = exists;
        return item;
    }

    /// <summary>An auto-start service whose name no service can have: the real service code refuses it before touching anything.</summary>
    private static StartupItem InvalidService(string name, bool enabled = true)
        => Manual(name, StartupKind.Service, enabled, serviceName: "bad\"name-" + name);

    private static StartupViewModel NewVm(StartupSandbox sandbox, Seams seams, IEnumerable<StartupItem>? items = null)
    {
        var vm = seams.Wire(new StartupViewModel(sandbox.Service));
        vm.ApplyItems((items ?? Scan(sandbox)).ToList());
        return vm;
    }

    private static List<StartupItem> Scan(StartupSandbox sandbox) => sandbox.Service.Scan(null, CancellationToken.None);

    private static string AddRun(StartupSandbox sandbox, string name, string hive = "HKCU")
    {
        var exe = sandbox.CreateFile($"{name}.exe");
        sandbox.SetValue($@"{hive}\Run", name, $"\"{exe}\" --tray");
        return exe;
    }

    private static void AddDisabledRun(StartupSandbox sandbox, string name, string hive = "HKCU")
    {
        AddRun(sandbox, name, hive);
        sandbox.SetValue($@"{hive}\Explorer\StartupApproved\Run", name, StartupApproved.CreateDisabled(DateTime.UtcNow), RegistryValueKind.Binary);
    }

    private static string AddFolderFile(StartupSandbox sandbox, string fileName)
    {
        var path = Path.Combine(sandbox.UserFolder, fileName);
        File.WriteAllText(path, "@echo off");
        return path;
    }

    private static bool IsApprovedEnabled(StartupSandbox sandbox, string name, string hive = "HKCU", string key = "Run")
        => StartupApproved.IsEnabled(sandbox.GetValue($@"{hive}\Explorer\StartupApproved\{key}", name) as byte[]);

    private static StartupItem Row(StartupViewModel vm, string name) => vm.ItemsView.Cast<StartupItem>().Single(i => i.Name == name);

    private static void Check(StartupViewModel vm, params string[] names)
    {
        foreach (var name in names)
            Row(vm, name).IsChecked = true;
    }

    // ---------------------------------------------------------------- checking

    [Fact]
    public void Checks_SurviveAReload_ByEntryId_AndGoneEntriesLeaveThem()
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        AddRun(sandbox, "Beta");
        AddRun(sandbox, "Gamma");
        var seams = new Seams();
        var vm = NewVm(sandbox, seams);
        var oldAlpha = Row(vm, "Alpha");
        Check(vm, "Alpha", "Beta");
        Assert.Equal(2, vm.CheckedCount);

        // A rescan builds new objects for the same entries.
        vm.ApplyItems(Scan(sandbox));

        var newAlpha = Row(vm, "Alpha");
        Assert.NotSame(oldAlpha, newAlpha);
        Assert.True(newAlpha.IsChecked);
        Assert.True(Row(vm, "Beta").IsChecked);
        Assert.False(Row(vm, "Gamma").IsChecked);
        Assert.Equal(2, vm.CheckedCount);

        // An entry that is gone after the rescan leaves the checks.
        using (var key = Registry.CurrentUser.OpenSubKey($@"{sandbox.RegistryRoot}\HKCU\Run", writable: true)!)
            key.DeleteValue("Beta");
        vm.ApplyItems(Scan(sandbox));

        Assert.Equal(1, vm.CheckedCount);
        Assert.True(Row(vm, "Alpha").IsChecked);
    }

    [Fact]
    public void WhatCanBeChanged_CanBeChecked_AndProtectedEntriesCannot()
    {
        using var sandbox = new StartupSandbox();
        var seams = new Seams();
        var runOnce = Manual("Once", StartupKind.RegistryRunOnce);
        var service = Manual("Vendor Service", StartupKind.Service);
        var msService = Manual("WinDefend", StartupKind.Service, signature: Microsoft);
        var windowsTask = Manual("Defrag", StartupKind.ScheduledTask, signature: Microsoft, taskPath: @"\Microsoft\Windows\Defrag\Scheduled");
        var plain = Manual("Plain");
        var vm = NewVm(sandbox, seams, new[] { runOnce, service, msService, windowsTask, plain });

        Assert.False(runOnce.CanToggle);
        Assert.True(runOnce.CanCheck);   // can only be deleted
        Assert.False(service.CanDelete);
        Assert.True(service.CanCheck);   // can only be switched
        Assert.True(plain.CanCheck);
        Assert.False(msService.CanCheck);
        Assert.False(windowsTask.CanCheck);
        Assert.Null(plain.CheckTooltip);
        Assert.Equal(msService.ToggleBlockedReason, msService.CheckTooltip);
        Assert.Equal(windowsTask.ToggleBlockedReason, windowsTask.CheckTooltip);

        // The disabled box is never checked, whoever tries (a click, code, or "check all").
        msService.IsChecked = true;
        vm.ToggleCheckCommand.Execute(windowsTask);
        Assert.False(msService.IsChecked);
        Assert.False(windowsTask.IsChecked);
        Assert.Equal(0, vm.CheckedCount);
        Assert.False(vm.ToggleCheckCommand.CanExecute(msService));

        vm.ShowMicrosoftServices = true; // the protected service is listed now, but still can't be checked
        vm.ToggleAllVisibleCommand.Execute(null);
        Assert.Equal(3, vm.CheckedCount); // once, service, plain
        Assert.True(runOnce.IsChecked && service.IsChecked && plain.IsChecked);
        Assert.False(msService.IsChecked);
        Assert.True(vm.AllVisibleChecked);
    }

    [Fact]
    public void AnEntryThatBecomesProtected_LeavesTheChecks()
    {
        using var sandbox = new StartupSandbox();
        var seams = new Seams();
        var vm = NewVm(sandbox, seams, new[] { Manual("Helper", StartupKind.Service), Manual("Other") });
        Check(vm, "Helper", "Other");
        Assert.Equal(2, vm.CheckedCount);

        // The next scan finds the service signed by Microsoft: its box is disabled now, so it can't stay checked (or counted).
        vm.ApplyItems(new[] { Manual("Helper", StartupKind.Service, signature: Microsoft), Manual("Other") });

        Assert.Equal(1, vm.CheckedCount);
        Assert.True(Row(vm, "Other").IsChecked);
        Assert.Equal(true, vm.AllVisibleChecked);
    }

    [Fact]
    public void HeaderCheckbox_IsThreeStateOverTheVisibleRows()
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        AddRun(sandbox, "Alpine");
        AddRun(sandbox, "Beta");
        AddRun(sandbox, "Gamma");
        var seams = new Seams();
        var vm = NewVm(sandbox, seams);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.Equal(false, vm.AllVisibleChecked);

        vm.SearchText = "alp"; // Alpha, Alpine
        Assert.Equal(false, vm.AllVisibleChecked);
        vm.ToggleAllVisibleCommand.Execute(null);

        Assert.Equal(2, vm.CheckedCount);
        Assert.Equal(true, vm.AllVisibleChecked);
        Assert.True(Row(vm, "Alpha").IsChecked);
        Assert.Contains(nameof(StartupViewModel.CheckedCount), changes);
        Assert.Contains(nameof(StartupViewModel.AllVisibleChecked), changes);

        vm.SearchText = ""; // two of four
        var gamma = Row(vm, "Gamma");
        var beta = Row(vm, "Beta");
        Assert.Null(vm.AllVisibleChecked);
        Assert.False(beta.IsChecked);

        vm.ToggleAllVisibleCommand.Execute(null); // some -> all
        Assert.Equal(4, vm.CheckedCount);
        Assert.Equal(true, vm.AllVisibleChecked);

        vm.SearchText = "beta";
        vm.ToggleAllVisibleCommand.Execute(null); // clears only what is visible
        Assert.Equal(3, vm.CheckedCount);
        Assert.False(beta.IsChecked);
        Assert.True(gamma.IsChecked); // hidden by the search, so left alone
        vm.SearchText = "";
        Assert.Null(vm.AllVisibleChecked);

        vm.ClearChecksCommand.Execute(null);
        Assert.Equal(0, vm.CheckedCount);
        Assert.Equal(false, vm.AllVisibleChecked);
        Assert.False(vm.DisableCheckedCommand.CanExecute(null));
        Assert.False(vm.EnableCheckedCommand.CanExecute(null));
        Assert.False(vm.DeleteCheckedCommand.CanExecute(null));
        Row(vm, "Alpha").IsChecked = true;
        Assert.True(vm.DisableCheckedCommand.CanExecute(null));
        Assert.True(vm.EnableCheckedCommand.CanExecute(null));
        Assert.True(vm.DeleteCheckedCommand.CanExecute(null));
    }

    [Fact]
    public void RowCheckbox_ReadsTheSharedChecks_AndTellsTheRowWhenTheyChange()
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        var seams = new Seams();
        var vm = NewVm(sandbox, seams);
        var alpha = Row(vm, "Alpha");
        var changes = new List<string?>();
        alpha.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        vm.ToggleCheckCommand.Execute(alpha); // the row's own checkbox
        Assert.True(alpha.IsChecked);
        Assert.True(vm.Checked.IsChecked(alpha.Id));
        Assert.Contains(nameof(StartupItem.IsChecked), changes);

        changes.Clear();
        vm.ClearChecksCommand.Execute(null);
        Assert.False(alpha.IsChecked);
        Assert.Contains(nameof(StartupItem.IsChecked), changes);

        // An item that never joined a page can't hold a check.
        var loose = Manual("Loose");
        loose.IsChecked = true;
        Assert.False(loose.IsChecked);
    }

    // ---------------------------------------------------------------- planning

    [Fact]
    public void Plan_ListsWhatEachActionChanges_AndWhyTheRestIsLeftAlone()
    {
        using var sandbox = new StartupSandbox();
        var seams = new Seams();
        var on = Manual("On");
        var off = Manual("Off", enabled: false);
        var once = Manual("Once", StartupKind.RegistryRunOnce);
        var service = Manual("Svc", StartupKind.Service);
        var serviceOff = Manual("SvcOff", StartupKind.Service, enabled: false);
        var task = Manual("Task", StartupKind.ScheduledTask, runsAsCurrentUser: true);
        var vm = NewVm(sandbox, seams, new[] { on, off, once, service, serviceOff, task });
        foreach (var item in new[] { on, off, once, service, serviceOff, task })
            item.IsChecked = true;

        var disable = vm.PlanChecked(StartupBulkAction.Disable);
        Assert.Equal(new[] { "On", "Svc", "Task" }, disable.Items.Select(i => i.Name).OrderBy(n => n));
        Assert.Contains(disable.Skipped, s => s.Item == off && s.Reason == "уже отключена");
        Assert.Contains(disable.Skipped, s => s.Item == serviceOff && s.Reason == "уже отключена");
        Assert.Contains(disable.Skipped, s => s.Item == once && s.Reason.Contains("однократный запуск"));
        Assert.Equal(3, disable.Skipped.Count);

        var enable = vm.PlanChecked(StartupBulkAction.Enable);
        Assert.Equal(new[] { "Off", "SvcOff" }, enable.Items.Select(i => i.Name).OrderBy(n => n));
        Assert.Contains(enable.Skipped, s => s.Item == on && s.Reason == "уже включена");
        Assert.Contains(enable.Skipped, s => s.Item == once && s.Reason.Contains("однократный запуск"));
        Assert.Equal(4, enable.Skipped.Count);

        var delete = vm.PlanChecked(StartupBulkAction.Delete);
        Assert.Equal(new[] { "Off", "On", "Once", "Task" }, delete.Items.Select(i => i.Name).OrderBy(n => n));
        Assert.Contains(delete.Skipped, s => s.Item == service && s.Reason.Contains("служб"));
        Assert.Contains(delete.Skipped, s => s.Item == serviceOff && s.Reason.Contains("служб"));
        Assert.Equal(2, delete.Skipped.Count);

        // Planning only looks: nothing is changed, nothing asked.
        Assert.Empty(seams.Questions);
        Assert.Equal(6, vm.CheckedCount);
    }

    // ---------------------------------------------------------------- disable / enable

    [Fact]
    public void Disable_ConfirmNo_ChangesNothing() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        AddFolderFile(sandbox, "tool.bat");
        var seams = new Seams { Answer = false };
        var vm = NewVm(sandbox, seams);
        Check(vm, "Alpha", "tool");

        await vm.DisableCheckedCommand.ExecuteAsync(null);

        Assert.Single(seams.Questions);
        Assert.Empty(seams.Infos);
        Assert.True(IsApprovedEnabled(sandbox, "Alpha"));
        Assert.True(IsApprovedEnabled(sandbox, "tool.bat", key: "StartupFolder"));
        Assert.True(Row(vm, "Alpha").IsEnabled);
        Assert.Equal(2, vm.CheckedCount);
        Assert.Null(vm.Message);
    });

    [Fact]
    public void Disable_Confirmed_DisablesEveryCheckedEntry_AndReportsOnce() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        AddRun(sandbox, "Beta");
        AddRun(sandbox, "Delta");
        AddFolderFile(sandbox, "tool.bat");
        sandbox.SetValue(@"HKCU\RunOnce", "Finish", $"\"{sandbox.CreateFile("finish.exe")}\" /finish");
        var seams = new Seams();
        var vm = NewVm(sandbox, seams);
        Check(vm, "Alpha", "Beta", "tool", "Finish"); // Delta stays unchecked

        await vm.DisableCheckedCommand.ExecuteAsync(null);

        Assert.False(IsApprovedEnabled(sandbox, "Alpha"));
        Assert.False(IsApprovedEnabled(sandbox, "Beta"));
        Assert.False(IsApprovedEnabled(sandbox, "tool.bat", key: "StartupFolder"));
        Assert.True(IsApprovedEnabled(sandbox, "Delta"));
        Assert.False(Row(vm, "Alpha").IsEnabled);
        Assert.True(Row(vm, "Delta").IsEnabled);

        var (title, question) = Assert.Single(seams.Questions);
        Assert.Equal("Отключить отмеченные записи?", title);
        Assert.Contains("Alpha", question);
        Assert.Contains("Beta", question);
        Assert.Contains("tool", question);
        Assert.DoesNotContain("Delta", question);
        // The run-once entry can't be disabled: it is named with the reason, not silently dropped.
        Assert.Contains("Finish — однократный запуск", question);
        Assert.EndsWith("Продолжить?", question);

        // One message for all of it, in the banner and in one dialog.
        var (infoTitle, info) = seams.Info;
        Assert.Equal("Автозагрузка", infoTitle);
        Assert.Contains("Отключено: 3 записи.", info);
        Assert.Contains("Finish", info);
        Assert.Contains("Отключено: 3", vm.Message);
        Assert.False(vm.MessageIsError);

        // Done entries leave the checks, the one that was not touched stays checked; the counters follow.
        Assert.Equal(1, vm.CheckedCount);
        Assert.True(Row(vm, "Finish").IsChecked);
        Assert.Equal(2, vm.EnabledCount);  // Delta and the run-once entry
        Assert.Equal(3, vm.DisabledCount);
        Assert.Empty(seams.RestorePoints);
        Assert.Empty(seams.ElevationRequests);
    });

    [Fact]
    public void Enable_Confirmed_EnablesCheckedDisabledEntries_AndNamesTheOnesAlreadyOn() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        AddDisabledRun(sandbox, "Alpha");
        AddDisabledRun(sandbox, "Beta");
        AddRun(sandbox, "Gamma");
        var seams = new Seams();
        var vm = NewVm(sandbox, seams);
        Check(vm, "Alpha", "Beta", "Gamma");

        await vm.EnableCheckedCommand.ExecuteAsync(null);

        Assert.True(IsApprovedEnabled(sandbox, "Alpha"));
        Assert.True(IsApprovedEnabled(sandbox, "Beta"));
        Assert.True(Row(vm, "Alpha").IsEnabled);
        var (title, question) = Assert.Single(seams.Questions);
        Assert.Equal("Включить отмеченные записи?", title);
        Assert.Contains("Gamma — уже включена", question);
        Assert.Contains("Включено: 2 записи.", seams.Info.Message);
        Assert.Equal(1, vm.CheckedCount); // Gamma was not touched and stays checked
    });

    [Fact]
    public void Disable_ThatFailsForSomeEntries_KeepsThemChecked_AndListsWhy() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        var seams = new Seams();
        var broken = InvalidService("Bad");
        var vm = NewVm(sandbox, seams, Scan(sandbox).Append(broken));
        Check(vm, "Alpha", "Bad");

        await vm.DisableCheckedCommand.ExecuteAsync(null);

        Assert.False(IsApprovedEnabled(sandbox, "Alpha"));
        var info = seams.Info.Message;
        Assert.Contains("Отключено: 1 запись.", info);
        Assert.Contains("Не удалось изменить (1):", info);
        Assert.Contains("Bad — Некорректное имя службы.", info);
        Assert.True(vm.MessageIsError);
        Assert.Contains("не удалось: 1", vm.Message);
        Assert.Equal(1, vm.CheckedCount); // the failed one stays checked, to try again
        Assert.True(broken.IsChecked);
        Assert.True(broken.IsEnabled);
    });

    // ---------------------------------------------------------------- delete

    [Fact]
    public void Delete_ConfirmNo_ChangesNothing() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        var file = AddFolderFile(sandbox, "tool.bat");
        var seams = new Seams { Answer = false };
        var vm = NewVm(sandbox, seams);
        Check(vm, "Alpha", "tool");

        await vm.DeleteCheckedCommand.ExecuteAsync(null);

        var (title, _) = Assert.Single(seams.Questions);
        Assert.Equal("Удалить отмеченные записи из автозагрузки?", title);
        Assert.NotNull(sandbox.GetValue(@"HKCU\Run", "Alpha"));
        Assert.True(File.Exists(file));
        Assert.Equal(2, vm.ItemsView.Cast<StartupItem>().Count());
        Assert.Equal(2, vm.CheckedCount);
        Assert.Empty(seams.Infos);
    });

    [Fact]
    public void Delete_Confirmed_RemovesTheEntries_MovesFilesToTheRecycleBin_AndReportsOnce() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        AddRun(sandbox, "Beta");
        var file = AddFolderFile(sandbox, "tool.bat");
        sandbox.SetValue(@"HKCU\RunOnce", "Finish", $"\"{sandbox.CreateFile("finish.exe")}\" /finish");
        var seams = new Seams();
        var vm = NewVm(sandbox, seams);
        Check(vm, "Alpha", "tool", "Finish"); // Beta stays

        await vm.DeleteCheckedCommand.ExecuteAsync(null);

        Assert.Null(sandbox.GetValue(@"HKCU\Run", "Alpha"));
        Assert.Null(sandbox.GetValue(@"HKCU\RunOnce", "Finish"));
        Assert.NotNull(sandbox.GetValue(@"HKCU\Run", "Beta"));
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(sandbox.RecycledDir, "tool.bat"))); // the sandbox's own RecycleFile

        Assert.Equal(new[] { "Beta" }, vm.ItemsView.Cast<StartupItem>().Select(i => i.Name));
        Assert.Equal(0, vm.CheckedCount);
        var question = seams.Question;
        Assert.Contains("Alpha", question);
        Assert.Contains("Finish", question);
        Assert.DoesNotContain("Beta", question);
        Assert.Contains("Сами программы не удаляются", question);
        Assert.Contains("Удалено из автозагрузки: 3 записи.", seams.Info.Message);
        Assert.Contains("Удалено: 3", vm.Message);
        Assert.False(vm.MessageIsError);
    });

    [Fact]
    public void Delete_NamesTheEntriesItCannotDelete_AndTheyStayChecked() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        var service = Manual("Vendor Service", StartupKind.Service);
        var seams = new Seams();
        var vm = NewVm(sandbox, seams, Scan(sandbox).Append(service));
        Check(vm, "Alpha", "Vendor Service");

        await vm.DeleteCheckedCommand.ExecuteAsync(null);

        Assert.Contains("Vendor Service — службы не удаляются", seams.Question);
        Assert.Null(sandbox.GetValue(@"HKCU\Run", "Alpha"));
        Assert.Contains("Не затронуты (1):", seams.Info.Message);
        Assert.Contains("Vendor Service", seams.Info.Message);
        Assert.Equal(1, vm.CheckedCount);
        Assert.True(service.IsChecked);
    });

    [Fact]
    public void WhenNothingCanBeDone_ThereIsNothingToConfirm_ButTheReasonsAreShown() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        var service = Manual("Vendor Service", StartupKind.Service);
        var seams = new Seams();
        var vm = NewVm(sandbox, seams, new[] { service });
        service.IsChecked = true;

        await vm.DeleteCheckedCommand.ExecuteAsync(null);

        Assert.Empty(seams.Questions);
        Assert.Contains("Vendor Service — службы не удаляются", seams.Info.Message);
        Assert.NotNull(vm.Message);
        Assert.Empty(seams.RestorePoints);
        Assert.Equal(1, vm.CheckedCount);
    });

    // ---------------------------------------------------------------- what is checked is what is touched

    [Fact]
    public void CheckedEntriesTheListDoesNotShow_AreNamedFirst_NeverFolded_AndAreChangedToo() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        var hidden = Enumerable.Range(1, 20).Select(i => $"Hidden{i:00}").ToList();
        var shown = Enumerable.Range(1, 16).Select(i => $"Shown{i:00}").ToList();
        foreach (var name in hidden.Concat(shown))
            AddRun(sandbox, name);
        var seams = new Seams();
        var vm = NewVm(sandbox, seams);
        Check(vm, hidden.Concat(shown).ToArray());

        vm.SearchText = "shown"; // the search box hides the 20 checked entries
        Assert.Equal(36, vm.CheckedCount);

        await vm.DisableCheckedCommand.ExecuteAsync(null);

        var question = seams.Question;
        var hiddenHeading = question.IndexOf("Не видны в списке сейчас:", StringComparison.Ordinal);
        var listedHeading = question.IndexOf("В списке:", StringComparison.Ordinal);
        Assert.True(hiddenHeading >= 0 && listedHeading > hiddenHeading, question);
        var hiddenPart = HiddenPart(question);
        Assert.All(hidden, name => Assert.Contains(name, hiddenPart)); // all 20, none folded into "…и ещё"
        Assert.DoesNotContain("…и ещё", hiddenPart);
        var listedPart = question[listedHeading..];
        Assert.Contains("…и ещё 2", listedPart); // the visible list is cut after 14, like the other pages
        Assert.Contains("Отключить автозапуск (36 записей)", question);

        // What the question named is what was changed.
        Assert.All(hidden.Concat(shown), name => Assert.False(IsApprovedEnabled(sandbox, name), name));
        Assert.Equal(0, vm.CheckedCount);
    });

    [Fact]
    public void EntriesHiddenByTheSourceTabAndTheStatusCard_AreNamedAsHiddenToo() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        AddDisabledRun(sandbox, "RunOff");
        var taskOff = Manual("TaskOff", StartupKind.ScheduledTask, enabled: false, runsAsCurrentUser: true);
        var seams = new Seams { Answer = false }; // only the question is looked at: the task is never really enabled
        var vm = NewVm(sandbox, seams, Scan(sandbox).Append(taskOff));
        Check(vm, "RunOff", "TaskOff");

        // The "Реестр и папки" tab hides the task.
        vm.SelectedSource = vm.SourceFilters.Single(s => s.Value == StartupSourceFilter.RegistryAndFolders);
        await vm.EnableCheckedCommand.ExecuteAsync(null);
        var question = seams.Questions[^1].Message;
        Assert.Contains("TaskOff", HiddenPart(question));
        Assert.DoesNotContain("RunOff", HiddenPart(question));
        Assert.Contains("RunOff", question[question.IndexOf("В списке:", StringComparison.Ordinal)..]);

        // The "Включено" card hides the disabled registry entry (and the tab is back to "Все", so the task is hidden by the card too).
        vm.SelectedSource = vm.SourceFilters.Single(s => s.Value == StartupSourceFilter.All);
        vm.SetStatusFilterCommand.Execute("Enabled");
        await vm.EnableCheckedCommand.ExecuteAsync(null);
        question = seams.Questions[^1].Message;
        Assert.Contains("RunOff", HiddenPart(question));
        Assert.Contains("TaskOff", HiddenPart(question));
        Assert.DoesNotContain("В списке:", question);
        Assert.Equal(2, vm.CheckedCount);
        Assert.False(taskOff.IsEnabled);
    });

    private static string HiddenPart(string question)
    {
        var start = question.IndexOf("Не видны в списке сейчас:", StringComparison.Ordinal);
        Assert.True(start >= 0, question);
        var end = question.IndexOf("В списке:", start, StringComparison.Ordinal);
        return end < 0 ? question[start..] : question[start..end];
    }

    [Fact]
    public void NothingHidden_MeansNoHiddenHeading() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        var seams = new Seams { Answer = false };
        var vm = NewVm(sandbox, seams);
        Check(vm, "Alpha");

        await vm.DisableCheckedCommand.ExecuteAsync(null);

        Assert.DoesNotContain("Не видны в списке сейчас", seams.Question);
        Assert.DoesNotContain("В списке:", seams.Question);
    });

    // ---------------------------------------------------------------- administrator rights

    [Fact]
    public void EntriesNeedingAdmin_AreAskedAboutOnce_AndLeftAloneWhenTheProgramIsNotRestarted() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox(elevated: false);
        AddRun(sandbox, "Mine");
        AddRun(sandbox, "Machine", hive: "HKLM");
        AddRun(sandbox, "Machine2", hive: "HKLM");
        var seams = new Seams();
        var vm = NewVm(sandbox, seams);
        Check(vm, "Mine", "Machine", "Machine2");

        await vm.DisableCheckedCommand.ExecuteAsync(null);

        Assert.Single(seams.ElevationRequests); // once for both, before the question
        Assert.Contains("Отключение", seams.ElevationRequests[0]);
        var question = seams.Question;
        Assert.Contains("Не затронуты (2):", question);
        Assert.Contains("Machine — нужны права администратора", question);
        Assert.False(IsApprovedEnabled(sandbox, "Mine"));
        Assert.True(IsApprovedEnabled(sandbox, "Machine", "HKLM"));
        Assert.True(IsApprovedEnabled(sandbox, "Machine2", "HKLM"));
        Assert.Equal(2, vm.CheckedCount); // the ones left alone stay checked
    });

    [Fact]
    public void WhenEverythingNeedsAdmin_AndNoRestart_NothingIsAskedOrChanged() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox(elevated: false);
        AddRun(sandbox, "Machine", hive: "HKLM");
        var seams = new Seams();
        var vm = NewVm(sandbox, seams);
        Check(vm, "Machine");

        await vm.DeleteCheckedCommand.ExecuteAsync(null);

        Assert.Single(seams.ElevationRequests);
        Assert.Contains("Удаление", seams.ElevationRequests[0]);
        Assert.Empty(seams.Questions);
        Assert.Contains("нужны права администратора", seams.Info.Message);
        Assert.NotNull(sandbox.GetValue(@"HKLM\Run", "Machine"));
    });

    [Fact]
    public void NoQuestionAboutAdmin_ForCurrentUserEntries_ForScheduledTasks_OrWhenAlreadyElevated() => UiThread.Run(async () =>
    {
        // Current-user entries only.
        using (var sandbox = new StartupSandbox(elevated: false))
        {
            AddRun(sandbox, "Mine");
            var seams = new Seams();
            var vm = NewVm(sandbox, seams);
            Check(vm, "Mine");
            await vm.DisableCheckedCommand.ExecuteAsync(null);
            Assert.Empty(seams.ElevationRequests);
            Assert.False(IsApprovedEnabled(sandbox, "Mine"));
        }

        // A scheduled task needs no restart question (it may still be changeable); the question only lists it. Answering "Нет"
        // keeps the real Task Scheduler out of the test.
        using (var sandbox = new StartupSandbox(elevated: false))
        {
            var task = Manual("Updater", StartupKind.ScheduledTask, runsAsCurrentUser: false);
            Assert.True(task.RequiresAdmin);
            var seams = new Seams { Answer = false };
            var vm = NewVm(sandbox, seams, new[] { task });
            task.IsChecked = true;
            await vm.DisableCheckedCommand.ExecuteAsync(null);
            Assert.Empty(seams.ElevationRequests);
            Assert.Contains("Updater — Планировщик, \\Vendor\\Updater", seams.Question);
            Assert.True(task.IsEnabled);
        }

        // Already administrator.
        using (var sandbox = new StartupSandbox(elevated: true))
        {
            AddRun(sandbox, "Machine", hive: "HKLM");
            var seams = new Seams();
            var vm = NewVm(sandbox, seams);
            Check(vm, "Machine");
            await vm.DisableCheckedCommand.ExecuteAsync(null);
            Assert.Empty(seams.ElevationRequests);
            Assert.False(IsApprovedEnabled(sandbox, "Machine", "HKLM"));
        }
    });

    [Fact]
    public void WhenRightsAreLostAfterTheQuestion_TheRestartIsOffered_InsteadOfAnotherDialog() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox(elevated: true);
        AddRun(sandbox, "Machine", hive: "HKLM");
        var seams = new Seams();
        var vm = NewVm(sandbox, seams);
        Check(vm, "Machine");
        vm.Confirm = (_, _) =>
        {
            sandbox.Elevated = false; // e.g. the session lost its rights while the question was open
            return true;
        };

        await vm.DisableCheckedCommand.ExecuteAsync(null);

        Assert.True(IsApprovedEnabled(sandbox, "Machine", "HKLM"));
        Assert.True(vm.MessageIsError);
        Assert.Single(seams.ElevationRequests);
        Assert.Empty(seams.Infos);
        Assert.Equal(1, vm.CheckedCount);
    });

    [Fact]
    public void WhenTheRestartAsAdministratorStarts_NothingElseIsAskedShownOrChanged() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox(elevated: false);
        AddRun(sandbox, "Mine");
        AddRun(sandbox, "Machine", hive: "HKLM");
        var seams = new Seams { RestartStarts = true }; // "Да": the program is closing
        var vm = NewVm(sandbox, seams);
        Check(vm, "Mine", "Machine");

        // The app is shutting down: no question (it could be the deletion), no "Нечего…" message, no change; the checks stay.
        await vm.DisableCheckedCommand.ExecuteAsync(null);
        await vm.DeleteCheckedCommand.ExecuteAsync(null);
        Row(vm, "Mine").IsChecked = false;
        await vm.DeleteCheckedCommand.ExecuteAsync(null); // only the machine-wide entry: used to end in a "nothing to delete" message

        Assert.Equal(3, seams.ElevationRequests.Count);
        Assert.Empty(seams.Questions);
        Assert.Empty(seams.Infos);
        Assert.Empty(seams.RestorePoints);
        Assert.Null(vm.Message);
        Assert.True(IsApprovedEnabled(sandbox, "Mine"));
        Assert.True(IsApprovedEnabled(sandbox, "Machine", "HKLM"));
        Assert.NotNull(sandbox.GetValue(@"HKCU\Run", "Mine"));
        Assert.NotNull(sandbox.GetValue(@"HKLM\Run", "Machine"));
        Assert.Equal(1, vm.CheckedCount);
        Assert.True(Row(vm, "Machine").IsChecked);
    });

    // ---------------------------------------------------------------- stopped half way

    [Fact]
    public void Disable_StoppedWithCancel_KeepsWhatWasDone_AndSaysSo() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        AddRun(sandbox, "Beta");
        AddRun(sandbox, "Gamma");
        var seams = new Seams();
        var vm = NewVm(sandbox, seams);
        Check(vm, "Alpha", "Beta", "Gamma");
        // The first entry creates the StartupApproved key, the second one exports it first: «Отмена» is pressed right there.
        sandbox.BackupHook = (_, _, _) =>
        {
            vm.CancelCommand.Execute(null);
            return Task.FromResult<string?>("backup.reg");
        };

        await vm.DisableCheckedCommand.ExecuteAsync(null);

        var disabled = new[] { "Alpha", "Beta", "Gamma" }.Where(n => !IsApprovedEnabled(sandbox, n)).ToList();
        var done = Assert.Single(disabled); // the first one only
        Assert.Equal("Отключено: 1. Остановлено — остальные записи не тронуты.", vm.Message);
        Assert.False(vm.MessageIsError);
        var info = seams.Info.Message;
        Assert.Contains("Отключено: 1 запись.", info);
        Assert.Contains("Остановлено: остальные записи не тронуты.", info);
        Assert.False(Row(vm, done).IsEnabled);
        Assert.Equal(2, vm.CheckedCount); // the stopped ones stay checked
        Assert.False(Row(vm, done).IsChecked);
        Assert.Equal(2, vm.ItemsView.Cast<StartupItem>().Count(i => i.IsEnabled && i.IsChecked));
    });

    [Fact]
    public void Disable_WithAnErrorHalfWay_ShowsThePartialCountsInTheBanner() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        AddRun(sandbox, "Beta");
        AddRun(sandbox, "Gamma");
        var seams = new Seams();
        var vm = NewVm(sandbox, seams);
        Check(vm, "Alpha", "Beta", "Gamma");
        // Not an error the service handles (like an IOException): it reaches the page's busy wrapper, which shows its own dialog.
        sandbox.BackupHook = (_, _, _) => throw new NotSupportedException("тестовая ошибка");

        await vm.DisableCheckedCommand.ExecuteAsync(null);

        var disabled = new[] { "Alpha", "Beta", "Gamma" }.Where(n => !IsApprovedEnabled(sandbox, n)).ToList();
        var done = Assert.Single(disabled);
        Assert.Equal("Отключено: 1. Остановлено из-за ошибки — остальные записи не тронуты.", vm.Message);
        Assert.True(vm.MessageIsError);
        Assert.Empty(seams.Infos); // the error dialog is the one message
        Assert.Equal(2, vm.CheckedCount);
        Assert.False(Row(vm, done).IsChecked);
        Assert.False(Row(vm, done).IsEnabled);
    });

    [Fact]
    public void Delete_StoppedWithCancel_ReloadsTheListFromTheRealState() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        AddRun(sandbox, "Beta");
        AddRun(sandbox, "Gamma");
        var seams = new Seams();
        var vm = NewVm(sandbox, seams);
        Check(vm, "Alpha", "Beta");
        var calls = 0;
        sandbox.BackupHook = (_, _, _) =>
        {
            if (++calls == 2)
                vm.CancelCommand.Execute(null); // the second entry is never deleted, the first one already is
            return Task.FromResult<string?>("backup.reg");
        };

        await vm.DeleteCheckedCommand.ExecuteAsync(null);

        var remaining = new[] { "Alpha", "Beta", "Gamma" }.Where(n => sandbox.GetValue(@"HKCU\Run", n) is not null).ToList();
        Assert.Equal(2, remaining.Count);
        Assert.Contains("Gamma", remaining);
        // The list was read again: it shows exactly what is left, not the entries as they were before the deletion.
        Assert.Equal(remaining.OrderBy(n => n), vm.ItemsView.Cast<StartupItem>().Select(i => i.Name).OrderBy(n => n));
        Assert.Equal("Удаление остановлено. Часть записей могла быть удалена — список обновлён.", vm.Message);
        Assert.False(vm.MessageIsError);
        Assert.Empty(seams.Infos);
        Assert.Equal(1, vm.CheckedCount); // the one that was not deleted stays checked
        Assert.Equal(remaining.Single(n => n != "Gamma"), vm.ItemsView.Cast<StartupItem>().Single(i => i.IsChecked).Name);
    });

    [Fact]
    public void Delete_FailingHalfWay_ReloadsTheListAndSaysSo() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        AddRun(sandbox, "Beta");
        AddRun(sandbox, "Gamma");
        var seams = new Seams();
        var vm = NewVm(sandbox, seams);
        Check(vm, "Alpha", "Beta");
        var calls = 0;
        sandbox.BackupHook = (_, _, _) =>
        {
            if (++calls == 2)
                throw new NotSupportedException("тестовая ошибка");
            return Task.FromResult<string?>("backup.reg");
        };

        await vm.DeleteCheckedCommand.ExecuteAsync(null);

        var remaining = new[] { "Alpha", "Beta", "Gamma" }.Where(n => sandbox.GetValue(@"HKCU\Run", n) is not null).ToList();
        Assert.Equal(2, remaining.Count);
        Assert.Equal(remaining.OrderBy(n => n), vm.ItemsView.Cast<StartupItem>().Select(i => i.Name).OrderBy(n => n));
        Assert.Equal("Не удалось удалить отмеченные записи. Список обновлён.", vm.Message);
        Assert.True(vm.MessageIsError);
        Assert.Empty(seams.Infos);
        Assert.Equal(1, vm.CheckedCount);
    });

    // ---------------------------------------------------------------- one source of truth for the rights

    [Fact]
    public void SingleEntryActions_AndTheRestartButton_UseTheSameRightsAsTheService() => UiThread.Run(async () =>
    {
        using var admin = new StartupSandbox(elevated: true);
        using var standard = new StartupSandbox(elevated: false);
        var seams = new Seams();
        var adminVm = NewVm(admin, seams, Array.Empty<StartupItem>());
        var standardVm = NewVm(standard, seams, Array.Empty<StartupItem>());

        Assert.True(adminVm.IsElevated);
        Assert.False(standardVm.IsElevated);
        Assert.False(adminVm.RestartAsAdminCommand.CanExecute(null));  // nothing to restart for
        Assert.True(standardVm.RestartAsAdminCommand.CanExecute(null));

        // A machine-wide entry is switched on by the single switch without any restart question, because the service may change it
        // (the same rights the bulk buttons and the service itself see; the test runner's own rights do not matter).
        AddDisabledRun(admin, "Machine", hive: "HKLM");
        var vm = NewVm(admin, seams);
        await vm.ToggleCommand.ExecuteAsync(Row(vm, "Machine"));

        Assert.True(IsApprovedEnabled(admin, "Machine", "HKLM"));
        Assert.True(Row(vm, "Machine").IsEnabled);
        Assert.Empty(seams.ElevationRequests);
    });

    // ---------------------------------------------------------------- restore point

    [Fact]
    public void OneRestorePoint_ForAllServices_AndNoneWithoutThem() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox();
        AddRun(sandbox, "Alpha");
        var seams = new Seams();
        var vm = NewVm(sandbox, seams, Scan(sandbox).Concat(new[] { InvalidService("One"), InvalidService("Two") }));
        Check(vm, "Alpha", "One", "Two");

        await vm.DisableCheckedCommand.ExecuteAsync(null);

        var point = Assert.Single(seams.RestorePoints);
        Assert.Contains("2 службы", point);
        Assert.False(IsApprovedEnabled(sandbox, "Alpha"));
        Assert.Contains("ручной режим", seams.Question); // the question says services are switched to manual mode
        Assert.Contains("точка восстановления", seams.Question);
        Assert.Contains("Не удалось изменить (2):", seams.Info.Message);

        // Only registry entries: no restore point, no mention of one.
        var plain = new Seams();
        var vm2 = NewVm(sandbox, plain);
        Check(vm2, "Alpha");
        await vm2.EnableCheckedCommand.ExecuteAsync(null);
        Assert.Empty(plain.RestorePoints);
        Assert.DoesNotContain("точка восстановления", plain.Question);
    });

    [Fact]
    public void NoRestorePoint_WhenTheServicesWereDroppedForLackOfRights_OrTheQuestionWasDeclined() => UiThread.Run(async () =>
    {
        using var sandbox = new StartupSandbox(elevated: false);
        var seams = new Seams();
        var vm = NewVm(sandbox, seams, new[] { InvalidService("One") });
        vm.ToggleAllVisibleCommand.Execute(null);

        await vm.DisableCheckedCommand.ExecuteAsync(null);

        Assert.Single(seams.ElevationRequests);
        Assert.Empty(seams.RestorePoints);
        Assert.Empty(seams.Questions);

        using var elevated = new StartupSandbox(elevated: true);
        var declined = new Seams { Answer = false };
        var vm2 = NewVm(elevated, declined, new[] { InvalidService("One") });
        vm2.ToggleAllVisibleCommand.Execute(null);
        await vm2.DisableCheckedCommand.ExecuteAsync(null);
        Assert.Single(declined.Questions);
        Assert.Empty(declined.RestorePoints);
    });
}
