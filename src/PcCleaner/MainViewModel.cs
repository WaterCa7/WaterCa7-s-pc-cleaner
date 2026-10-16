using System.Collections.ObjectModel;
using PcCleaner.Core;
using PcCleaner.Modules.Cleaner;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Disk;
using PcCleaner.Modules.Network;
using PcCleaner.Modules.Optimizer;
using PcCleaner.Modules.Processes;
using PcCleaner.Modules.Programs;
using PcCleaner.Modules.Security;
using PcCleaner.Modules.Settings;
using PcCleaner.Modules.Startup;

namespace PcCleaner;

public sealed class MainViewModel : ObservableObject, INavigator
{
    private PageViewModel? _selectedPage;
    private string _statusText = "Готово";

    public MainViewModel()
    {
        AppServices.Navigator = this;

        Pages = new ObservableCollection<PageViewModel>
        {
            new DashboardViewModel(),
            new CleanerViewModel(AppServices.Cleaner),
            new ProcessesViewModel(AppServices.Processes),
            new StartupViewModel(AppServices.Startup),
            new ProgramsViewModel(AppServices.Programs),
            new DiskViewModel(AppServices.Disk),
            new OptimizerViewModel(AppServices.Optimizer),
            new NetworkViewModel(AppServices.Network),
            new SecurityViewModel(AppServices.Security),
            new SettingsViewModel(),
        };

        RestartAsAdminCommand = new RelayCommand(() => Admin.RestartElevated(), () => !Admin.IsElevated);
        Log.EntryAdded += entry => StatusText = entry.Message;

        SelectedPage = Pages[0];
    }

    public ObservableCollection<PageViewModel> Pages { get; }

    public PageViewModel? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is not null && !ReferenceEquals(value, _selectedPage))
                _ = NavigateAsync(value);
        }
    }

    public bool IsAdmin => Admin.IsElevated;

    public string AdminText => IsAdmin ? "Права администратора" : "Ограниченный режим";

    public string VersionText => "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public RelayCommand RestartAsAdminCommand { get; }

    public void NavigateTo(string pageKey)
    {
        var page = Pages.FirstOrDefault(p => string.Equals(p.Key, pageKey, StringComparison.OrdinalIgnoreCase));
        if (page is not null)
            SelectedPage = page;
    }

    /// <summary>Selects a page and awaits its activation.</summary>
    public async Task NavigateAsync(PageViewModel page)
    {
        var previous = _selectedPage;
        if (!Set(ref _selectedPage, page, nameof(SelectedPage)))
            return;
        try
        {
            previous?.OnNavigatedFrom();
        }
        catch (Exception ex)
        {
            Log.Error($"Ошибка при уходе со страницы «{previous?.Title}»", ex);
        }
        try
        {
            await page.OnNavigatedToAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"Ошибка открытия страницы «{page.Title}»", ex);
        }
    }
}
