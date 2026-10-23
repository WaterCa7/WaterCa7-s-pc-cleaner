using System.Collections.ObjectModel;
using PcCleaner.Core;
using PcCleaner.Modules.Settings;
using Xunit;

namespace PcCleaner.Tests.Settings;

/// <summary>«Запускать с правами администратора» for the installed program: no Windows prompt at each start any more.</summary>
public class RunAsAdminTaskTests
{
    public RunAsAdminTaskTests() => TestEnvironment.Init();

    [Fact]
    public void TheHint_SaysTheInstalledProgramDoesNotAsk()
    {
        var vm = new SettingsViewModel(() => new AppSettings(), _ => { }, new ObservableCollection<LogEntry>());

        Assert.Contains("без вопросов", vm.RunAsAdminHint);
    }
}
