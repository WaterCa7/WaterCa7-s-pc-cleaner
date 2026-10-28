using System.Collections.ObjectModel;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Core.Updates;
using PcCleaner.Modules.Cleaner;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Disk;
using PcCleaner.Modules.Network;
using PcCleaner.Modules.Optimizer;
using PcCleaner.Modules.Processes;
using PcCleaner.Modules.Profile;
using PcCleaner.Modules.Programs;
using PcCleaner.Modules.Security;
using PcCleaner.Modules.Settings;
using PcCleaner.Modules.Startup;

namespace PcCleaner;

public sealed class MainViewModel : ObservableObject, INavigator
{
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _save;
    private PageViewModel? _selectedPage;
    private UiMode _shownMode;
    private bool _updateBannerDismissed;

    public MainViewModel()
        : this(null, null, null)
    {
    }

    /// <param name="pages">
    /// Null — the real pages, and this view model becomes <see cref="AppServices.Navigator"/> (the shell). Tests pass their own
    /// pages and leave the global navigator alone.
    /// </param>
    /// <param name="settings">Where the mode is kept (null — <see cref="AppServices.Settings"/>).</param>
    /// <param name="save">Saves the settings after the mode was switched (null — settings.json).</param>
    internal MainViewModel(IEnumerable<PageViewModel>? pages, Func<AppSettings>? settings, Action<AppSettings>? save)
    {
        _settings = settings ?? (() => AppServices.Settings);
        _save = save ?? (s => s.Save());
        if (pages is null)
            AppServices.Navigator = this;

        Pages = new ObservableCollection<PageViewModel>(pages ?? CreatePages());
        Sections = ShellMenu.Build(Pages);
        TopSections = new ObservableCollection<SectionViewModel>();
        BottomSections = new ObservableCollection<SectionViewModel>();
        VisiblePages = new ObservableCollection<PageViewModel>();
        _shownMode = UiModes.Of(_settings());
        SyncVisiblePages();
        UiModes.Changed += RefreshMode;

        // Admin.RestartElevated itself asks first when any page is busy (one guard for every restart button and prompt).
        RestartAsAdminCommand = new RelayCommand(() => Admin.RestartElevated(), () => !Admin.IsElevated);
        Updates = AppServices.Updates;
        // A shell made for tests (own pages) has nothing to restart with: its language switch only saves.
        Welcome = new WelcomeViewModel(_settings, _save, pages is null ? WelcomeViewModel.RestartProgram : () => false);
        Welcome.Chosen += _ => NavigateTo("Dashboard");
        // The sign-in window comes with SignInDialog (task 6 of the B2 plan); until then the buttons «Войти» / «Зарегистрироваться» do nothing.
        AccountIntro = new AccountIntroViewModel(_settings, _save, () => AppServices.Account.IsSignedIn, _ => false);
        Splash = new SplashViewModel(Updates, Environment.GetCommandLineArgs().Skip(1).ToList(),
            () => System.Windows.Application.Current.Shutdown());
        Updates.PropertyChanged += (_, _) => OnUpdateBannerChanged();
        UpdateBannerCommand = new RelayCommand(RunUpdateBannerAction);
        DismissUpdateBannerCommand = new RelayCommand(() =>
        {
            _updateBannerDismissed = true;
            OnUpdateBannerChanged();
        });

        if (OtherAccountWarning is { } warning)
            Log.Warn(warning);

        SelectedPage = VisiblePages.FirstOrDefault();
    }

    private static IEnumerable<PageViewModel> CreatePages()
    {
        var optimizer = new OptimizerViewModel(AppServices.Optimizer);
        return new PageViewModel[]
        {
            new DashboardViewModel(),
            new CleanerViewModel(AppServices.Cleaner),
            new HardwareViewModel(optimizer),
            optimizer,
            new ProcessesViewModel(AppServices.Processes),
            new StartupViewModel(AppServices.Startup),
            new ProgramsViewModel(AppServices.Programs),
            new DiskViewModel(AppServices.Disk),
            new SecurityViewModel(AppServices.Security),
            new NetworkViewModel(AppServices.Network),
            new ProfileViewModel(),
            new SettingsViewModel(),
        };
    }

    /// <summary>
    /// Every page of the program, not only those of the current mode (<see cref="VisiblePages"/>): screenshots, the busy check of a
    /// restart and the pages that look for each other use the whole list, and the hidden pages go on taking part in the
    /// Dashboard check (they are IHealthContributors of <see cref="AppServices"/>, not sidebar items). The sidebar shows
    /// <see cref="Sections"/>; a page is a tab of one of them.
    /// </summary>
    public ObservableCollection<PageViewModel> Pages { get; }

    /// <summary>The pages of the current mode (<see cref="UiModes"/>), in the order of <see cref="Pages"/>; the sections show them as tabs.</summary>
    public ObservableCollection<PageViewModel> VisiblePages { get; }

    /// <summary>Every section of the menu (<see cref="ShellMenu"/>); a section's tabs are pages of <see cref="Pages"/>.</summary>
    public IReadOnlyList<SectionViewModel> Sections { get; }

    /// <summary>The main list of the sidebar: the sections that have a tab in the current mode.</summary>
    public ObservableCollection<SectionViewModel> TopSections { get; }

    /// <summary>The list at the bottom of the sidebar (Профиль, Настройки).</summary>
    public ObservableCollection<SectionViewModel> BottomSections { get; }

    /// <summary>The section of the open page.</summary>
    public SectionViewModel? SelectedSection => _selectedPage is null ? null : Sections.FirstOrDefault(s => s.Contains(_selectedPage));

    /// <summary>
    /// The selection of the main list: the open section when it is there, else null. The list writes here: a section opens on
    /// its <see cref="SectionViewModel.EntryTab"/>; null (the list cleared its selection because a bottom section opened) is ignored.
    /// </summary>
    public SectionViewModel? SelectedTopSection
    {
        get => SelectedSection is { Place: SectionPlace.Top } s ? s : null;
        set => Open(value);
    }

    /// <summary>The selection of the bottom list; see <see cref="SelectedTopSection"/>.</summary>
    public SectionViewModel? SelectedBottomSection
    {
        get => SelectedSection is { Place: SectionPlace.Bottom } s ? s : null;
        set => Open(value);
    }

    /// <summary>The title above the page: the section when its tab strip is shown, else the page itself.</summary>
    public string HeaderTitle => SelectedSection is { ShowTabs: true } s ? s.Title : _selectedPage?.Title ?? "";

    /// <summary>The question about a page the simple mode hides (<see cref="Dialogs.Ask"/>; tests replace it).</summary>
    internal Func<string, string, bool> Ask { get; set; } = Dialogs.Ask;

    IEnumerable<PageViewModel> INavigator.Pages => Pages;

    /// <summary>Shown above the page when the program runs under another account than the signed-in user.</summary>
    public string? OtherAccountWarning => Admin.OtherAccountWarning;

    public bool HasOtherAccountWarning => OtherAccountWarning is not null;

    /// <summary>
    /// The open page. The tab strip's binding writes here (the sidebar writes <see cref="SelectedTopSection"/> and
    /// <see cref="SelectedBottomSection"/>), and only what <see cref="VisiblePages"/> offers is taken: a page the
    /// mode hides is ignored (no question — a binding write is not the user asking for it). A hidden page is reached through
    /// <see cref="NavigateTo"/> or <see cref="NavigateAsync"/>, which ask first.
    /// </summary>
    public PageViewModel? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is not null && !ReferenceEquals(value, _selectedPage) && VisiblePages.Contains(value))
                _ = NavigateAsync(value);
        }
    }

    /// <summary>The simple mode is shown: the header keeps the subtitle of a page to one line (the whole text is in the tooltip).</summary>
    public bool IsSimpleMode => _shownMode == UiMode.Simple;

    public bool IsAdmin => Admin.IsElevated;

    public string AdminText => IsAdmin ? T._("Права администратора") : T._("Ограниченный режим");

    /// <summary>The corner of the bottom line: «v1.5.0 · последняя версия» (<see cref="UpdateFooter"/>).</summary>
    public string FooterText => UpdateFooter.Text(Updates.State, Updates.Manifest, Updates.CurrentVersion);

    public RelayCommand RestartAsAdminCommand { get; }

    public UpdateService Updates { get; }

    /// <summary>The startup splash over the blurred window; App starts it, screenshot mode never does.</summary>
    public SplashViewModel Splash { get; }

    /// <summary>
    /// The first-run screen (mode and language) over the blurred window; App shows it after the splash while no mode is chosen.
    /// A mode chosen there opens the Dashboard.
    /// </summary>
    public WelcomeViewModel Welcome { get; }

    /// <summary>The start screen «Войти / Зарегистрироваться / Продолжить без входа»; App shows it after the splash, before Welcome.</summary>
    public AccountIntroViewModel AccountIntro { get; }

    private UpdateBanner? Banner => _updateBannerDismissed ? null : UpdateBanner.For(Updates.State, Updates.Manifest, Updates.CurrentVersion, Updates.NeedsElevation);

    public bool ShowUpdateBanner => Banner is not null;

    public string UpdateBannerText => Banner?.Text ?? "";

    public string UpdateBannerAction => Banner?.Action ?? "";

    public RelayCommand UpdateBannerCommand { get; }

    public RelayCommand DismissUpdateBannerCommand { get; }

    /// <summary>
    /// Opens a page by key. Every link to a page — the Dashboard's tiles and «Подробнее», the hints of the other pages — comes
    /// here. A page the simple mode hides is offered together with the expert mode: «Да» switches the mode (saved) and opens
    /// the page, «Нет» stays where it is.
    /// </summary>
    public void NavigateTo(string pageKey)
    {
        var page = Pages.FirstOrDefault(p => string.Equals(p.Key, pageKey, StringComparison.OrdinalIgnoreCase));
        if (page is not null && IsReachable(page))
            SelectedPage = page;
    }

    private void Open(SectionViewModel? section)
    {
        if (section?.EntryTab is { } tab && !ReferenceEquals(tab, _selectedPage))
            _ = NavigateAsync(tab);
    }

    /// <summary>
    /// True when the page is in the sidebar. A page of <see cref="Pages"/> that the mode hides is offered together with the
    /// expert mode: «Да» switches the mode (saved) and the page is then reachable, «Нет» leaves everything as it was. A page
    /// that is not one of <see cref="Pages"/> is never reachable.
    /// </summary>
    private bool IsReachable(PageViewModel page)
    {
        if (VisiblePages.Contains(page))
            return true;
        if (!Pages.Contains(page) || !Ask(page.Title, T._("Этот раздел есть в опытном режиме. Переключиться на опытный режим?")))
            return false;
        UiModes.Choose(_settings(), UiMode.Expert, _save);
        RefreshMode(); // the same as the event, in case it did not reach this shell
        return VisiblePages.Contains(page);
    }

    /// <summary>
    /// The mode was switched (Настройки, the question above): the sidebar follows at once, the pages are told, and if the open
    /// page is not in the new mode the Dashboard opens. Nothing happens when this shell's own mode is the same.
    /// </summary>
    private void RefreshMode()
    {
        var mode = UiModes.Of(_settings());
        if (mode == _shownMode)
            return;
        _shownMode = mode;
        OnPropertyChanged(nameof(IsSimpleMode));
        SyncVisiblePages();

        foreach (var page in Pages)
        {
            try
            {
                page.OnModeChanged();
            }
            catch (Exception ex)
            {
                Log.Error(T.F("Ошибка при смене режима на странице «{0}»", page.Title), ex);
            }
        }

        if (_selectedPage is null || !VisiblePages.Contains(_selectedPage))
            SelectedPage = VisiblePages.FirstOrDefault(p => p.Key == "Dashboard") ?? VisiblePages.FirstOrDefault();
    }

    /// <summary>Makes <see cref="VisiblePages"/> the pages of the shown mode by removing and inserting only what differs (the selection stays).</summary>
    private void SyncVisiblePages()
    {
        var wanted = Pages.Where(p => UiModes.IsVisible(_shownMode, p.Key)).ToList();
        foreach (var page in VisiblePages.Where(p => !wanted.Contains(p)).ToList())
            VisiblePages.Remove(page);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= VisiblePages.Count || !ReferenceEquals(VisiblePages[i], wanted[i]))
                VisiblePages.Insert(i, wanted[i]);
        }
        foreach (var section in Sections)
            section.SyncVisibleTabs(p => VisiblePages.Contains(p));
        SyncSections(TopSections, SectionPlace.Top);
        SyncSections(BottomSections, SectionPlace.Bottom);
        OnPropertyChanged(nameof(HeaderTitle));
    }

    /// <summary>Makes <paramref name="list"/> the sections of <paramref name="place"/> that have a visible tab (the selection stays).</summary>
    private void SyncSections(ObservableCollection<SectionViewModel> list, SectionPlace place)
    {
        var wanted = Sections.Where(s => s.Place == place && s.HasVisibleTabs).ToList();
        foreach (var section in list.Where(s => !wanted.Contains(s)).ToList())
            list.Remove(section);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= list.Count || !ReferenceEquals(list[i], wanted[i]))
                list.Insert(i, wanted[i]);
        }
    }

    private void OnUpdateBannerChanged()
    {
        OnPropertyChanged(nameof(ShowUpdateBanner));
        OnPropertyChanged(nameof(UpdateBannerText));
        OnPropertyChanged(nameof(UpdateBannerAction));
        OnPropertyChanged(nameof(FooterText));
    }

    private void RunUpdateBannerAction()
    {
        switch (Updates.State)
        {
            case UpdateState.Ready:
                if (Updates.NeedsElevation)
                {
                    // The elevated copy installs the staged setup at its start, whatever AutoUpdate says (UpdateService.TryInstallAtStart).
                    Admin.RestartElevated(new[] { UpdateService.InstallUpdateArg });
                    break;
                }
                var busy = PageViewModel.BusyPages(Pages).Select(p => p.Title).ToList();
                if (busy.Count > 0 && !Dialogs.Confirm(T._("Установка обновления"), UpdateBanner.BusyWarning(busy)))
                    return;
                if (Updates.InstallNow(Environment.GetCommandLineArgs().Skip(1).ToList()))
                    System.Windows.Application.Current.Shutdown();
                break;
            case UpdateState.Updated:
                Dialogs.Info(T.F("Что нового в версии {0}", Updates.CurrentVersion.ToString(3)),
                    UpdateBanner.WhatsNewText(Updates.Manifest?.Notes));
                break;
            case UpdateState.ManualRequired:
                ProcessRunner.ShellOpen(UpdateService.ReleasesUrl);
                break;
        }
    }

    /// <summary>
    /// Selects a page and awaits its activation. A page the mode hides is offered together with the expert mode first (the
    /// question of <see cref="NavigateTo"/>); when the answer is «Нет», or the page is not one of <see cref="Pages"/>, nothing happens.
    /// </summary>
    public async Task NavigateAsync(PageViewModel page)
    {
        if (!IsReachable(page))
            return;
        var previous = _selectedPage;
        if (ReferenceEquals(previous, page))
            return;
        _selectedPage = page;
        SelectedSection?.Remember(page);
        // The page first, then the section. The tab strip (MainWindow) binds SelectedItem to SelectedPage and ItemsSource to
        // SelectedSection.VisibleTabs. Given the page first, the strip keeps it and selects it when the new tabs arrive. Given
        // the section first, the strip drops its old selection, writes null back (ignored), and the binding re-reads the new
        // page while that change is still running, so the strip keeps SelectedItem without selecting it and the later
        // SelectedPage event changes nothing: the open tab would not be highlighted (checked with --screenshots).
        OnPropertyChanged(nameof(SelectedPage));
        OnPropertyChanged(nameof(SelectedSection));
        OnPropertyChanged(nameof(SelectedTopSection));
        OnPropertyChanged(nameof(SelectedBottomSection));
        OnPropertyChanged(nameof(HeaderTitle));
        try
        {
            previous?.OnNavigatedFrom();
        }
        catch (Exception ex)
        {
            Log.Error(T.F("Ошибка при уходе со страницы «{0}»", previous?.Title), ex);
        }
        try
        {
            await page.OnNavigatedToAsync();
        }
        catch (Exception ex)
        {
            Log.Error(T.F("Ошибка открытия страницы «{0}»", page.Title), ex);
        }
    }
}
