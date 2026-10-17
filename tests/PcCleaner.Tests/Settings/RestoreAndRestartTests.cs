using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Settings;
using Xunit;

namespace PcCleaner.Tests.Settings;

public class RestoreAndRestartTests
{
    public RestoreAndRestartTests() => TestEnvironment.Init();

    // ---------- restore point outcome ----------

    [Fact]
    public void RestorePointOutcomes_AreDescribedExactly()
    {
        var now = new DateTime(2026, 10, 16, 14, 5, 0);

        var created = RestorePointText.Describe(RestorePointResult.Created, now, disabledByPolicy: false);
        Assert.Equal(RestoreResultKind.Success, created.Kind);
        Assert.Equal("Точка восстановления создана 16.10.2026 14:05.", created.Status);

        var skipped = RestorePointText.Describe(RestorePointResult.RecentPointExists, now, false);
        Assert.Equal(RestoreResultKind.Info, skipped.Kind);
        Assert.Contains("Windows пропустила", skipped.Status);
        Assert.Contains("за последние сутки", skipped.Status);
        Assert.Contains("24 часа", skipped.Details);

        var disabled = RestorePointText.Describe(RestorePointResult.DisabledInSettings, now, false);
        Assert.Equal(RestoreResultKind.Warning, disabled.Kind);
        Assert.Contains("выключено", disabled.Status);

        var notAdmin = RestorePointText.Describe(RestorePointResult.NotElevated, now, false);
        Assert.Equal(RestoreResultKind.Warning, notAdmin.Kind);
        Assert.Contains("нужны права администратора", notAdmin.Status);

        var failed = RestorePointText.Describe(RestorePointResult.Failed, now, false);
        Assert.Equal(RestoreResultKind.Warning, failed.Kind);
        Assert.Contains("не создала", failed.Status);
        Assert.Contains("Настроить защиту", failed.Details);

        var policy = RestorePointText.Describe(RestorePointResult.Failed, now, disabledByPolicy: true);
        Assert.Contains("групповой политикой", policy.Details);

        // Every outcome has its own, different status line.
        var all = Enum.GetValues<RestorePointResult>().Select(r => RestorePointText.Describe(r, now, false).Status).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    // ---------- «Открыть восстановление системы» / «Настроить защиту» ----------

    [Fact]
    public void ToolLauncher_StartsTheToolOnlyWhenItsWindowIsNotOpen()
    {
        var started = new List<string>();
        var running = false;
        var launcher = new SystemToolLauncher
        {
            FileExists = _ => true,
            IsRunning = name => running && name == "rstrui",
            Start = path =>
            {
                started.Add(path);
                return ToolLaunchResult.Started;
            },
        };

        Assert.Equal(ToolLaunchResult.Started, launcher.Launch("rstrui.exe"));
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "rstrui.exe"), Assert.Single(started));

        running = true;
        Assert.Equal(ToolLaunchResult.AlreadyOpen, launcher.Launch("rstrui.exe"));
        Assert.Single(started);

        var missing = new SystemToolLauncher
        {
            FileExists = _ => false,
            IsRunning = _ => false,
            Start = _ => throw new InvalidOperationException("must not be started"),
        };
        Assert.Equal(ToolLaunchResult.Missing, missing.Launch("rstrui.exe"));
    }

    [Fact]
    public void ToolRunningCheck_CountsOnlyProcessesWithAWindow()
    {
        // "Окно уже открыто" must mean a window the user can switch to; the windowless test host does not count.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.False(SystemToolLauncher.IsProcessRunning(self.ProcessName));
    }

    [Fact]
    public async Task ProtectionButton_OpensOneWindowOnADoubleClick()
    {
        var launches = 0;
        var vm = new SettingsViewModel(() => new AppSettings(), _ => { }, new System.Collections.ObjectModel.ObservableCollection<LogEntry>())
        {
            ToolLauncher = new SystemToolLauncher
            {
                FileExists = _ => true,
                IsRunning = _ => false,
                Start = _ =>
                {
                    Interlocked.Increment(ref launches);
                    return ToolLaunchResult.Started;
                },
            },
            ToolLaunchCooldown = TimeSpan.FromMilliseconds(200),
        };

        var first = vm.OpenProtectionSettingsCommand.ExecuteAsync();
        Assert.False(vm.OpenProtectionSettingsCommand.CanExecute(null));
        var second = vm.OpenProtectionSettingsCommand.ExecuteAsync();
        await Task.WhenAll(first, second);

        Assert.Equal(1, launches);
        Assert.True(vm.OpenProtectionSettingsCommand.CanExecute(null)); // usable again once the window had time to appear
    }

    // ---------- restart as administrator ----------

    [Fact]
    public void BusyWarning_NamesThePagesAndWhatTheyAreDoing()
    {
        var one = ElevatedRestart.BusyWarning(new[] { ("Программы", "Удаление «Test App»…") });
        Assert.StartsWith("Сейчас ещё не закончена работа в разделе:", one);
        Assert.Contains("• «Программы»: Удаление «Test App»", one);
        Assert.DoesNotContain("…", one.Split('\n')[1]);

        var two = ElevatedRestart.BusyWarning(new[] { ("Очистка", "Очистка…"), ("Диски", "") });
        Assert.StartsWith("Сейчас ещё не закончена работа в разделах:", two);
        Assert.Contains("• «Диски»\n", two);
        Assert.Contains("прервётся", two);
        Assert.Contains("Перезапустить от имени администратора сейчас?", two);
    }

    [Fact]
    public void BusyWarning_IsTheTextCoreAsksWith()
    {
        // ElevatedRestart only delegates: Admin.RestartElevated/RequireElevation ask the busy and standard-account
        // questions themselves, once (Core tests: Restart_AsksWhileAnyPageIsBusy, RequireElevation_Declined_DoesNotRestart).
        var busy = new[] { ("Программы", "Удаление «Test App»…"), ("Диски", "") };
        Assert.Equal(Admin.BusyWarning(busy), ElevatedRestart.BusyWarning(busy));
    }
}
