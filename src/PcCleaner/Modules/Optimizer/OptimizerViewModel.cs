using System.Diagnostics;
using System.Text;
using PcCleaner.Core;

namespace PcCleaner.Modules.Optimizer;

/// <summary>A titled block of recommendations with the same impact.</summary>
public sealed record RecommendationGroup(string Title, string Description, Tone Tone, IReadOnlyList<Recommendation> Items)
{
    public int Count => Items.Count;
}

/// <summary>Small counter chip in the summary card.</summary>
public sealed record SummaryChip(string Text, Tone Tone);

public sealed class OptimizerViewModel : PageViewModel
{
    private readonly OptimizerService _service;
    private bool _initialized;
    private Task? _loadTask;
    private HardwareSnapshot? _hardware;
    private IReadOnlyList<Recommendation> _all = Array.Empty<Recommendation>();
    private IReadOnlyList<HardwareCard> _cards = Array.Empty<HardwareCard>();
    private IReadOnlyList<RecommendationGroup> _groups = Array.Empty<RecommendationGroup>();
    private IReadOnlyList<Recommendation> _passed = Array.Empty<Recommendation>();
    private IReadOnlyList<Recommendation> _links = Array.Empty<Recommendation>();
    private IReadOnlyList<SummaryChip> _chips = Array.Empty<SummaryChip>();
    private string _summaryTitle = "";
    private string _summaryText = "";
    private int _recommendationCount;
    private int _safeCount;
    private bool _hasData;
    private bool _showAdminHint;
    private string? _banner;
    private Tone _bannerTone = Tone.Good;

    public OptimizerViewModel(OptimizerService service)
    {
        _service = service;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(refreshHardware: true), () => !IsBusy);
        ApplyAllCommand = new AsyncRelayCommand(ApplyAllAsync, () => !IsBusy && SafeCount > 0);
        ApplyCommand = new AsyncRelayCommand(p => ApplyAsync(p as Recommendation), p => !IsBusy && p is Recommendation);
        RevertCommand = new AsyncRelayCommand(p => RevertAsync(p as Recommendation), p => !IsBusy && p is Recommendation);
        ToggleStepsCommand = new RelayCommand(p =>
        {
            if (p is Recommendation r)
                r.IsExpanded = !r.IsExpanded;
        });
        OpenSettingsCommand = new RelayCommand(p => OpenSettings(p as Recommendation));
        NavigateCommand = new RelayCommand(p => Navigate(p as Recommendation));
        CopyRecommendationCommand = new RelayCommand(p => CopyRecommendation(p as Recommendation));
        CopyReportCommand = new RelayCommand(CopyReport, () => HasData);
        DismissBannerCommand = new RelayCommand(() => Banner = null);
        RestartAsAdminCommand = new RelayCommand(() => Admin.RestartElevated(), () => !Admin.IsElevated);
    }

    public override string Title => "Ускорение";

    public override string Subtitle => "Ваше железо и честные советы, как сделать компьютер быстрее";

    public override string IconGlyph => "\uE945";

    // ----- bindable state -----

    public IReadOnlyList<HardwareCard> HardwareCards
    {
        get => _cards;
        private set => Set(ref _cards, value);
    }

    public IReadOnlyList<RecommendationGroup> Groups
    {
        get => _groups;
        private set => Set(ref _groups, value);
    }

    /// <summary>Checks that passed ("уже в порядке").</summary>
    public IReadOnlyList<Recommendation> Passed
    {
        get => _passed;
        private set
        {
            if (Set(ref _passed, value))
                OnPropertyChanged(nameof(HasPassed));
        }
    }

    /// <summary>Links to other pages (startup, cleaner).</summary>
    public IReadOnlyList<Recommendation> Links
    {
        get => _links;
        private set => Set(ref _links, value);
    }

    public IReadOnlyList<SummaryChip> Chips
    {
        get => _chips;
        private set => Set(ref _chips, value);
    }

    /// <summary>All engine output (recommendations + passed checks + links).</summary>
    public IReadOnlyList<Recommendation> AllItems => _all;

    public string SummaryTitle
    {
        get => _summaryTitle;
        private set => Set(ref _summaryTitle, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    public int RecommendationCount
    {
        get => _recommendationCount;
        private set
        {
            if (Set(ref _recommendationCount, value))
                OnPropertyChanged(nameof(HasRecommendations));
        }
    }

    /// <summary>Safe auto-fixes for "Применить все рекомендуемые".</summary>
    public int SafeCount
    {
        get => _safeCount;
        private set
        {
            if (Set(ref _safeCount, value))
            {
                OnPropertyChanged(nameof(ApplyAllText));
                OnPropertyChanged(nameof(ApplyAllToolTip));
                OnPropertyChanged(nameof(HasSafeFixes));
                RelayCommand.Refresh();
            }
        }
    }

    public bool HasSafeFixes => SafeCount > 0;

    public string ApplyAllText => SafeCount > 0 ? $"Применить все рекомендуемые ({SafeCount})" : "Применить все рекомендуемые";

    public string ApplyAllToolTip => SafeCount > 0
        ? "Применит безопасные настройки, которые можно вернуть одной кнопкой. Перед этим покажет полный список."
        : "Сейчас нет настроек, которые можно безопасно применить автоматически.";

    public bool HasData
    {
        get => _hasData;
        private set
        {
            if (Set(ref _hasData, value))
            {
                OnPropertyChanged(nameof(ShowPlaceholder));
                RelayCommand.Refresh();
            }
        }
    }

    public bool ShowPlaceholder => !HasData;

    public bool HasRecommendations => RecommendationCount > 0;

    public bool HasPassed => Passed.Count > 0;

    public bool ShowAdminHint
    {
        get => _showAdminHint;
        private set => Set(ref _showAdminHint, value);
    }

    /// <summary>Result summary of the last action (shown at the top of the page).</summary>
    public string? Banner
    {
        get => _banner;
        private set => Set(ref _banner, value);
    }

    public Tone BannerTone
    {
        get => _bannerTone;
        private set => Set(ref _bannerTone, value);
    }

    // ----- commands -----

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ApplyAllCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand RevertCommand { get; }
    public RelayCommand ToggleStepsCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand CopyRecommendationCommand { get; }
    public RelayCommand CopyReportCommand { get; }
    public RelayCommand DismissBannerCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }

    // ----- navigation -----

    public override Task OnNavigatedToAsync()
    {
        if (!_initialized)
        {
            _initialized = true;
            _loadTask = LoadAsync(refreshHardware: false);
            return Task.CompletedTask;
        }
        if (!IsBusy && HasData)
            _ = QuietRefreshAsync();
        return Task.CompletedTask;
    }

    /// <summary>Read-only: collects the snapshots and shows the full page. Never changes the system.</summary>
    public override async Task PreviewScanAsync(CancellationToken ct)
    {
        if (_loadTask is { IsCompleted: false } running)
        {
            await running.WaitAsync(ct);
            if (HasData)
                return;
        }
        _initialized = true;
        _loadTask = LoadAsync(refreshHardware: false);
        await _loadTask.WaitAsync(ct);
    }

    // ----- loading -----

    private Task LoadAsync(bool refreshHardware) => RunBusyAsync(
        refreshHardware ? "Обновляем сведения о компьютере…" : "Собираем сведения о компьютере…",
        async ct =>
        {
            var sw = Stopwatch.StartNew();
            ReportProgress(-1, "Определяем процессор, память, видеокарту и мониторы…");
            var hardwareTask = _service.GetHardwareAsync(refreshHardware, ct);
            var settingsTask = _service.GetSettingsAsync(ct);
            var hardware = await hardwareTask;
            ReportProgress(-1, "Проверяем настройки Windows…");
            var settings = await settingsTask;
            Show(hardware, settings);
            Log.Info($"Ускорение: анализ за {sw.Elapsed.TotalSeconds:0.0} с, рекомендаций: {RecommendationCount}");
        });

    private async Task QuietRefreshAsync()
    {
        try
        {
            var settings = await _service.GetSettingsAsync();
            if (!IsBusy && _hardware is not null)
                Show(_hardware, settings);
        }
        catch (Exception ex)
        {
            Log.Warn($"Ускорение: не удалось обновить настройки: {ex.Message}");
        }
    }

    private async Task ReloadSettingsAsync(CancellationToken ct)
    {
        var hardware = _hardware ?? await _service.GetHardwareAsync(false, ct);
        var settings = await _service.GetSettingsAsync(ct);
        Show(hardware, settings);
    }

    /// <summary>Runs the engine and rebuilds every list (UI thread).</summary>
    private void Show(HardwareSnapshot hardware, SettingsSnapshot settings)
    {
        _hardware = hardware;
        var expanded = _all.Where(r => r.IsExpanded).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var items = RecommendationEngine.Evaluate(hardware, settings, DateTime.Now);
        _service.AddRevertEntries(items);
        _service.MarkRevertable(items);
        foreach (var item in items.Where(r => expanded.Contains(r.Id)))
            item.IsExpanded = true; // keep opened instructions open after a refresh
        _all = items;
        OnPropertyChanged(nameof(AllItems));

        var recs = items.Where(r => r.Kind is RecommendationKind.AutoFix or RecommendationKind.Manual or RecommendationKind.Info).ToList();
        var groups = new List<RecommendationGroup>();
        void AddGroup(string title, string description, Tone tone, Func<Recommendation, bool> filter)
        {
            var list = recs.Where(filter).ToList();
            if (list.Count > 0)
                groups.Add(new RecommendationGroup(title, description, tone, list));
        }
        AddGroup("Высокий эффект", "Начните с этого — разница будет заметна сразу.", Tone.Danger,
            r => r.Kind != RecommendationKind.Info && r.Impact == RecommendationImpact.High);
        AddGroup("Средний эффект", "Ощутимое улучшение за пару минут.", Tone.Warning,
            r => r.Kind != RecommendationKind.Info && r.Impact == RecommendationImpact.Medium);
        AddGroup("Низкий эффект", "Мелочи, которые всё равно стоит поправить.", Tone.Info,
            r => r.Kind != RecommendationKind.Info && r.Impact == RecommendationImpact.Low);
        AddGroup("Для сведения", "Менять ничего не обязательно — просто полезно знать.", Tone.Neutral,
            r => r.Kind == RecommendationKind.Info);

        Groups = groups;
        Passed = items.Where(r => r.Kind == RecommendationKind.Good).ToList();
        Links = items.Where(r => r.Kind == RecommendationKind.Navigate).ToList();
        HardwareCards = HardwareCardBuilder.Build(hardware, settings, items, DateTime.Now);

        RecommendationCount = recs.Count;
        var autoCount = recs.Count(r => r.Kind == RecommendationKind.AutoFix);
        SafeCount = recs.Count(r => r.Kind == RecommendationKind.AutoFix && r.IsSafe && !r.NeedsAdmin);
        SummaryTitle = BuildSummaryTitle(recs.Count, autoCount, recs.Count(r => r.Kind == RecommendationKind.Info));
        SummaryText = $"Проверок: {recs.Count + Passed.Count} · в порядке: {Passed.Count} · данные на {DateTime.Now:HH:mm}. " +
                      "Все изменения обратимы: прежние значения запоминаются, а перед правкой реестра создаётся резервная копия.";
        Chips = groups.Select(g => new SummaryChip($"{g.Title}: {g.Count}", g.Tone)).ToList();
        ShowAdminHint = !Admin.IsElevated && recs.Any(r => r.NeedsAdmin);
        HasData = true;
    }

    /// <summary>"Найдено 3 рекомендации, из них 2 можно применить автоматически".</summary>
    internal static string BuildSummaryTitle(int total, int auto, int info = 0)
    {
        if (total == 0)
            return "Всё настроено отлично — рекомендаций нет";
        if (info == total)
            return $"Серьёзных проблем нет — {Format.Count(total, "совет", "совета", "советов")} для сведения";
        var verb = Format.Plural(total, "Найдена", "Найдено", "Найдено");
        var head = $"{verb} {Format.Count(total, "рекомендация", "рекомендации", "рекомендаций")}";
        return auto > 0
            ? $"{head}, из них {auto} можно применить автоматически"
            : $"{head} — подскажем, что сделать вручную";
    }

    // ----- actions -----

    private static string BuildConfirmation(IReadOnlyList<Recommendation> recs)
    {
        var sb = new StringBuilder();
        sb.AppendLine(recs.Count == 1 ? "Будет изменена настройка:" : $"Будут изменены настройки ({recs.Count}):");
        foreach (var r in recs)
        {
            sb.AppendLine();
            sb.AppendLine(r.Title);
            if (!string.IsNullOrWhiteSpace(r.ApplySummary))
                sb.AppendLine(r.ApplySummary);
            if (!string.IsNullOrWhiteSpace(r.AfterApplyNote))
                sb.AppendLine("Важно: " + r.AfterApplyNote);
        }
        sb.AppendLine();
        sb.AppendLine("Как вернуть: прежние значения будут сохранены — кнопка «Вернуть» на этой странице восстановит их в точности. " +
                      "Перед изменением реестра программа сохраняет резервную копию раздела.");
        if (recs.Any(r => r.NeedsAdmin) || recs.Count > 1)
            sb.AppendLine("Также будет создана точка восстановления Windows (если она включена в настройках и есть права администратора).");
        sb.AppendLine();
        sb.Append("Продолжить?");
        return sb.ToString();
    }

    private async Task ApplyAsync(Recommendation? rec)
    {
        if (rec is null || rec.Kind != RecommendationKind.AutoFix || IsBusy)
            return;
        if (rec.NeedsAdmin && !Admin.RequireElevation(rec.Title))
            return;
        if (!Dialogs.Confirm("Применить рекомендацию", BuildConfirmation(new[] { rec })))
            return;

        TweakResult? result = null;
        await RunBusyAsync($"Применяем: {rec.Title}…", async ct =>
        {
            result = await _service.ApplyAsync(rec, createRestorePoint: true, ct);
            await ReloadSettingsAsync(ct);
        });
        if (result is null)
            return;

        var text = result.Message;
        if (result.Success && result.Changed)
        {
            if (!string.IsNullOrWhiteSpace(rec.AfterApplyNote))
                text += " " + rec.AfterApplyNote;
            text += " Вернуть прежнее значение можно кнопкой «Вернуть» в списке «Уже в порядке».";
        }
        ShowBanner(text, result.Success ? Tone.Good : Tone.Warning);
        if (result.Success && result.Changed && rec.NeedsRestart)
            Dialogs.Info("Нужна перезагрузка", text);
    }

    private async Task ApplyAllAsync()
    {
        if (IsBusy)
            return;
        var items = _all.Where(r => r.Kind == RecommendationKind.AutoFix && r.IsSafe && !r.NeedsAdmin).ToList();
        if (items.Count == 0)
            return;
        if (!Dialogs.Confirm("Применить все рекомендуемые", BuildConfirmation(items)))
            return;

        var results = new List<(Recommendation Rec, TweakResult Result)>();
        await RunBusyAsync("Применяем рекомендуемые настройки…", async ct =>
        {
            ReportProgress(-1, "Создаём точку восстановления…");
            await RestorePoint.TryCreateAsync("Ускорение: рекомендуемые настройки", ct);
            for (var i = 0; i < items.Count; i++)
            {
                ReportProgress(100.0 * i / items.Count, $"Применяем: {items[i].Title}");
                results.Add((items[i], await _service.ApplyAsync(items[i], createRestorePoint: false, ct)));
            }
            ReportProgress(100, "Обновляем сведения…");
            await ReloadSettingsAsync(ct);
        });
        if (results.Count == 0)
            return;

        var ok = results.Count(r => r.Result.Success);
        var sb = new StringBuilder($"Применено {ok} из {items.Count}.");
        foreach (var (rec, result) in results)
        {
            sb.Append("\n• ").Append(result.Success ? "" : "Не удалось: ").Append(result.Message);
            if (result.Success && result.Changed && !string.IsNullOrWhiteSpace(rec.AfterApplyNote))
                sb.Append(' ').Append(rec.AfterApplyNote);
        }
        if (ok > 0)
            sb.Append("\nВсё можно вернуть кнопками «Вернуть» в списке «Уже в порядке».");
        var summary = sb.ToString();
        ShowBanner(summary, ok == items.Count ? Tone.Good : Tone.Warning);
        Dialogs.Info("Готово", summary);
    }

    private async Task RevertAsync(Recommendation? rec)
    {
        if (rec?.FixId is not { } fixId || IsBusy || !_service.CanRevert(fixId))
            return;
        var title = OptimizerService.TweakTitle(fixId);
        if (RegistryTweaks.Get(fixId)?.NeedsAdmin == true && !Admin.RequireElevation($"Вернуть: {title}"))
            return;
        var lines = _service.DescribeRevert(fixId);
        var message = $"Вернуть прежнее значение настройки «{title}» — как было до изменения программой?\n\n" +
                      (lines.Count > 0 ? string.Join("\n", lines) + "\n\n" : "") +
                      (fixId == TweakIds.PowerPlan ? "" : "Перед изменением реестра будет сохранена резервная копия раздела.\n\n") +
                      "Продолжить?";
        if (!Dialogs.Confirm("Вернуть настройку", message))
            return;

        TweakResult? result = null;
        await RunBusyAsync($"Возвращаем: {title}…", async ct =>
        {
            result = await _service.RevertAsync(fixId, ct);
            await ReloadSettingsAsync(ct);
        });
        if (result is null)
            return;
        var text = result.Message;
        if (result.Success && fixId == TweakIds.Hags)
            text += " Изменение вступит в силу после перезагрузки.";
        else if (result.Success && fixId == TweakIds.VisualEffects)
            text += " Полностью изменения применятся после выхода из системы и повторного входа.";
        ShowBanner(text, result.Success ? Tone.Good : Tone.Warning);
    }

    private void ShowBanner(string text, Tone tone)
    {
        BannerTone = tone;
        Banner = text;
    }

    private static void OpenSettings(Recommendation? rec)
    {
        if (rec?.SettingsUri is not { Length: > 0 } uri)
            return;
        Log.Info($"Ускорение: открыто «{rec.SettingsLabel ?? uri}» ({uri})");
        ProcessRunner.ShellOpen(uri);
    }

    private static void Navigate(Recommendation? rec)
    {
        if (rec?.NavigateKey is { Length: > 0 } key)
            AppServices.Navigator?.NavigateTo(key);
    }

    private void CopyRecommendation(Recommendation? rec)
    {
        if (rec is null)
            return;
        var sb = new StringBuilder(rec.Title);
        if (rec.HasCurrentValue)
            sb.Append("\nСейчас: ").Append(rec.CurrentValue);
        if (!string.IsNullOrWhiteSpace(rec.Explanation))
            sb.Append('\n').Append(rec.Explanation);
        foreach (var step in rec.StepItems)
            sb.Append('\n').Append(step.Number).Append(". ").Append(step.Text);
        if (ClipboardText.TrySet(sb.ToString()))
            ShowBanner("Текст рекомендации скопирован в буфер обмена.", Tone.Good);
    }

    private void CopyReport()
    {
        if (!HasData)
            return;
        if (ClipboardText.TrySet(HardwareCardBuilder.BuildReport(_cards, _all)))
            ShowBanner("Сведения о компьютере скопированы — их можно вставить в сообщение на форуме или в поддержку.", Tone.Good);
    }
}
