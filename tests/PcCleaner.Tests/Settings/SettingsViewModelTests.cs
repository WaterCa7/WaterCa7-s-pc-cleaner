using System.Collections.ObjectModel;
using PcCleaner.Core;
using PcCleaner.Modules.Settings;
using Xunit;

namespace PcCleaner.Tests.Settings;

/// <summary>
/// View-model tests use a private AppSettings instance and a private log source,
/// so they never touch AppServices.Settings (shared with other modules' tests) or the real settings file.
/// (AppSettings.AddTrusted/RemoveTrusted save to the temp test data folder — hence the shared collection.)
/// </summary>
[Collection(SettingsFileCollection.Name)]
public class SettingsViewModelTests
{
    private readonly AppSettings _settings = new();
    private readonly ObservableCollection<LogEntry> _log = new();
    private int _saves;

    public SettingsViewModelTests() => TestEnvironment.Init();

    private SettingsViewModel Create() => new(() => _settings, _ => _saves++, _log);

    [Fact]
    public void LoadGeneral_ShowsSettingsWithoutSaving()
    {
        _settings.CreateRestorePoint = false;
        _settings.CleanerMinFileAgeHours = 12;
        _settings.CleanerExclusions.Add(@"D:\Keep");
        _settings.AddTrusted("tool.exe");

        var vm = Create();
        vm.LoadGeneral();

        Assert.False(vm.CreateRestorePoint);
        Assert.Equal(12, vm.SelectedAgeOption!.Hours);
        Assert.Contains(vm.AgeOptions, o => o.Hours == 12);
        Assert.Equal(new[] { @"D:\Keep" }, vm.Exclusions);
        Assert.Equal(new[] { "tool.exe" }, vm.TrustedItems);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void Changes_AreSavedImmediately()
    {
        var vm = Create();
        vm.LoadGeneral();

        vm.CreateRestorePoint = false;
        Assert.False(_settings.CreateRestorePoint);
        Assert.Equal(1, _saves);

        vm.SelectedAgeOption = vm.AgeOptions.Single(o => o.Hours == 72);
        Assert.Equal(72, _settings.CleanerMinFileAgeHours);
        Assert.Equal(2, _saves);

        // An out-of-range option (e.g. crafted) is clamped before saving.
        vm.SelectedAgeOption = new AgeOption(5000, "x");
        Assert.Equal(720, _settings.CleanerMinFileAgeHours);
        Assert.Equal(3, _saves);
    }

    [Fact]
    public void RunAsAdmin_IsOnByDefault_AndShownWithoutSaving()
    {
        var vm = Create();
        vm.LoadGeneral();

        Assert.True(vm.RunAsAdmin);
        Assert.Equal(0, _saves);

        _settings.RunAsAdmin = false;
        vm.LoadGeneral();

        Assert.False(vm.RunAsAdmin);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void RunAsAdmin_IsSavedWhenChanged()
    {
        var vm = Create();
        vm.LoadGeneral();

        vm.RunAsAdmin = false;
        Assert.False(_settings.RunAsAdmin);
        Assert.Equal(1, _saves);

        vm.RunAsAdmin = true;
        Assert.True(_settings.RunAsAdmin);
        Assert.Equal(2, _saves);

        vm.RunAsAdmin = true; // no change, nothing to save
        Assert.Equal(2, _saves);
    }

    [Fact]
    public void RunAsAdmin_HintSaysWhatTheSwitchCosts()
    {
        Assert.Equal(
            "Установленная программа запускается с правами без вопросов Windows; без прав часть проверок и исправлений недоступна.",
            Create().RunAsAdminHint);
    }

    [Fact]
    public void AddAndRemoveExclusions()
    {
        var vm = Create();
        vm.LoadGeneral();

        Assert.False(vm.AddExclusion("not a full path"));
        Assert.True(vm.ExclusionMessageIsError);
        Assert.Empty(_settings.CleanerExclusions);
        Assert.Equal(0, _saves);

        vm.NewExclusionText = @"E:\Media\";
        vm.AddExclusionCommand.Execute(null);
        Assert.Equal(new[] { @"E:\Media" }, _settings.CleanerExclusions);
        Assert.Equal(new[] { @"E:\Media" }, vm.Exclusions);
        Assert.False(vm.ExclusionMessageIsError);
        Assert.Equal("", vm.NewExclusionText);
        Assert.Equal(1, _saves);

        Assert.False(vm.AddExclusion(@"e:\media"));
        Assert.Equal(1, _saves);

        Assert.False(vm.RemoveExclusionsCommand.CanExecute(new List<string>()));
        Assert.True(vm.RemoveExclusionsCommand.CanExecute(new List<string> { @"E:\Media" }));
        vm.RemoveExclusionsCommand.Execute(new List<string> { @"E:\Media" });
        Assert.Empty(_settings.CleanerExclusions);
        Assert.Empty(vm.Exclusions);
        Assert.Equal(2, _saves);
    }

    [Fact]
    public void RemoveTrusted_GoesThroughAppSettingsAndRefreshesTheList()
    {
        _settings.AddTrusted("a.exe");
        _settings.AddTrusted("b.exe");
        var shared = _settings.TrustedItems;
        var vm = Create();
        vm.LoadGeneral();
        Assert.Null(vm.TrustedMessage);

        vm.RemoveTrustedCommand.Execute(new List<object> { "A.EXE" });

        Assert.Equal(new[] { "b.exe" }, _settings.TrustedItems);
        Assert.Equal(new[] { "b.exe" }, vm.TrustedItems);
        Assert.False(_settings.IsTrusted("a.exe"));
        Assert.StartsWith("Убрано из доверенных", vm.TrustedMessage);
        // AppSettings.RemoveTrusted saved the change itself; the page does not save the whole settings again.
        Assert.Equal(0, _saves);
        // The list other modules may be reading was replaced, not modified in place.
        Assert.Equal(new[] { "a.exe", "b.exe" }, shared);

        // Removing something that is no longer there changes nothing.
        vm.RemoveTrustedCommand.Execute(new List<object> { "a.exe" });
        Assert.Equal(new[] { "b.exe" }, vm.TrustedItems);
    }

    [Fact]
    public void TrustedListChangedOnAnotherPage_IsShownOnTheNextVisit()
    {
        var vm = Create();
        vm.LoadGeneral();
        Assert.Empty(vm.TrustedItems);

        _settings.AddTrusted(@"C:\Tools\tool.exe"); // e.g. "Доверять" on the Startup page
        vm.LoadGeneral();                           // what OnNavigatedToAsync does on every later visit

        Assert.Equal(new[] { @"C:\Tools\tool.exe" }, vm.TrustedItems);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void Reset_IsCancelledWhenNotConfirmed()
    {
        // TestEnvironment makes every confirmation answer "No".
        _settings.CreateRestorePoint = false;
        _settings.AddTrusted("keep.exe");
        var vm = Create();
        vm.LoadGeneral();

        vm.ResetSettingsCommand.Execute(null);

        Assert.False(_settings.CreateRestorePoint);
        Assert.Equal(new[] { "keep.exe" }, _settings.TrustedItems);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void Journal_FollowsSourceLiveWithFilterAndSearch()
    {
        var t = new DateTime(2026, 10, 16, 9, 0, 0);
        _log.Add(new LogEntry(t, LogLevel.Info, "Запуск"));
        _log.Add(new LogEntry(t.AddSeconds(1), LogLevel.Warning, "Нет доступа к службе"));
        var vm = Create();
        vm.AttachLogForTests();

        Assert.Equal(new[] { "Нет доступа к службе", "Запуск" }, vm.FilteredLog.Select(e => e.Message));

        // New entries appear on top.
        _log.Add(new LogEntry(t.AddSeconds(2), LogLevel.Action, "Файл помещён в карантин"));
        Assert.Equal("Файл помещён в карантин", vm.FilteredLog[0].Message);
        Assert.Equal(3, vm.FilteredLog.Count);

        // Filter: only warnings; a non-matching new entry is not shown, a matching one is.
        vm.SelectedLogFilter = vm.LogFilterOptions.Single(o => o.Value == LogLevelFilter.Warnings);
        Assert.Single(vm.FilteredLog);
        Assert.True(vm.IsLogFiltered);
        _log.Add(new LogEntry(t.AddSeconds(3), LogLevel.Info, "Просто информация"));
        Assert.Single(vm.FilteredLog);
        _log.Add(new LogEntry(t.AddSeconds(4), LogLevel.Warning, "Нет прав на папку"));
        Assert.Equal(new[] { "Нет прав на папку", "Нет доступа к службе" }, vm.FilteredLog.Select(e => e.Message));

        // Search narrows further.
        vm.LogSearch = "папку";
        Assert.Equal("Нет прав на папку", Assert.Single(vm.FilteredLog).Message);
        Assert.Contains("Показано 1", vm.LogSummary);

        // Old entries trimmed from the source disappear from the view.
        vm.LogSearch = "";
        _log.RemoveAt(1); // "Нет доступа к службе"
        Assert.Equal(new[] { "Нет прав на папку" }, vm.FilteredLog.Select(e => e.Message));

        vm.ClearLogSearchCommand.Execute(null);
        Assert.False(vm.IsLogFiltered);
        Assert.Equal(4, vm.FilteredLog.Count);
        Assert.Contains("Всего 4 записи", vm.LogSummary);
    }

    [Fact]
    public void Journal_StopsFollowingAfterNavigatingAway()
    {
        var vm = Create();
        vm.AttachLogForTests();
        vm.OnNavigatedFrom();
        _log.Add(new LogEntry(DateTime.Now, LogLevel.Action, "Без подписки"));
        Assert.Empty(vm.FilteredLog);
    }

    [Fact]
    public async Task PreviewScan_LoadsEverythingReadOnly()
    {
        _settings.CleanerMinFileAgeHours = 48;
        var vm = Create();
        vm.SelectedTabIndex = SettingsViewModel.TabQuarantine;

        await vm.PreviewScanAsync(CancellationToken.None);

        Assert.Equal(SettingsViewModel.TabGeneral, vm.SelectedTabIndex);
        Assert.Equal(48, vm.SelectedAgeOption!.Hours);
        Assert.False(string.IsNullOrWhiteSpace(vm.BackupsSummary));
        Assert.False(string.IsNullOrWhiteSpace(vm.QuarantineSummary));
        Assert.False(string.IsNullOrWhiteSpace(vm.RestoreFrequencyText));
        Assert.True(vm.RestorePointsLoaded);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void ItemsFrom_AcceptsSingleItemListOrNull()
    {
        Assert.Equal(new[] { "a" }, SettingsViewModel.ItemsFrom<string>("a"));
        Assert.Equal(new[] { "a", "b" }, SettingsViewModel.ItemsFrom<string>(new List<object> { "a", 5, "b" }));
        Assert.Empty(SettingsViewModel.ItemsFrom<string>(null));
    }
}
