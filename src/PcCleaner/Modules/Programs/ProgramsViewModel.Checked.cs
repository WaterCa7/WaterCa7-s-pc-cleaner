using System.Text;
using PcCleaner.Core;

namespace PcCleaner.Modules.Programs;

/// <summary>
/// The checked rows a bulk button acts on, taken from the checks right before the question. Rows the list does not show right now
/// (search, filter chip) are acted on too, so the question names them first and never folds them into "…и ещё N"; the work goes in
/// the order of the question.
/// </summary>
internal sealed class CheckedRows<T>
{
    /// <summary>Checked rows the list does not show right now, by name.</summary>
    public List<T> Hidden { get; } = new();

    /// <summary>Checked rows the list shows, in list order.</summary>
    public List<T> Listed { get; } = new();

    /// <summary>All of them, in the order of the question: the hidden ones first.</summary>
    public List<T> All => Hidden.Concat(Listed).ToList();

    public int Count => Hidden.Count + Listed.Count;
}

// The checkbox columns, the header boxes and the bulk bars of both tabs: "Удалить отмеченные" for programs and for Microsoft Store
// apps. The page, its filters and the single-row actions are in ProgramsViewModel.cs.
public sealed partial class ProgramsViewModel
{
    // ===================== state, commands and test seams =====================

    /// <summary>"Удалить отмеченные" on the programs tab: one question, one restore point, then the programs one by one.</summary>
    public AsyncRelayCommand UninstallCheckedCommand { get; }

    /// <summary>"Снять отметки" on the programs tab.</summary>
    public RelayCommand ClearProgramChecksCommand { get; }

    /// <summary>A program row's checkbox click (parameter: the row).</summary>
    public RelayCommand ToggleProgramCheckCommand { get; }

    /// <summary>Programs header checkbox: checks every visible program that can be checked, or unchecks them when all are.</summary>
    public RelayCommand ToggleAllVisibleProgramsCommand { get; }

    /// <summary>"Удалить отмеченные" on the Microsoft Store tab.</summary>
    public AsyncRelayCommand RemoveCheckedStoreAppsCommand { get; }

    /// <summary>"Снять отметки" on the Microsoft Store tab.</summary>
    public RelayCommand ClearStoreChecksCommand { get; }

    /// <summary>A Store app row's checkbox click (parameter: the row).</summary>
    public RelayCommand ToggleStoreAppCheckCommand { get; }

    /// <summary>Store apps header checkbox.</summary>
    public RelayCommand ToggleAllVisibleStoreAppsCommand { get; }

    /// <summary>
    /// Checked programs, by <see cref="InstalledProgram.CheckKey"/> (the registry key) only: rows read
    /// <see cref="InstalledProgram.IsChecked"/> from here, so the checks survive a rescan and what the button removes is exactly this set.
    /// </summary>
    internal CheckedSet<string> CheckedPrograms { get; } = new();

    /// <summary>Checked Store apps, by <see cref="StoreApp.CheckKey"/> (the package full name).</summary>
    internal CheckedSet<string> CheckedStoreApps { get; } = new();

    /// <summary>"Отмечено: N" of the programs bulk bar.</summary>
    public int CheckedProgramsCount => CheckedPrograms.Count;

    /// <summary>"Отмечено: N" of the Store bulk bar.</summary>
    public int CheckedStoreAppsCount => CheckedStoreApps.Count;

    /// <summary>Programs header checkbox over the rows the list shows: true = all checked, false = none, null = some.</summary>
    public bool? AllVisibleProgramsChecked => CheckedPrograms.StateOf(VisibleCheckablePrograms().Select(p => p.CheckKey));

    /// <summary>Store apps header checkbox over the rows the list shows.</summary>
    public bool? AllVisibleStoreAppsChecked => CheckedStoreApps.StateOf(VisibleCheckableStoreApps().Select(a => a.CheckKey));

    /// <summary>The Yes/No question before a bulk removal (<see cref="Dialogs.Confirm"/>; tests replace it).</summary>
    internal Func<string, string, bool> Confirm { get; set; } = Dialogs.Confirm;

    /// <summary>The one message after a bulk removal (tests replace it).</summary>
    internal Action<string, string> ShowInfo { get; set; } = Dialogs.Info;

    /// <summary>The one message after a bulk removal that an unexpected error stopped half way (tests replace it).</summary>
    internal Action<string, string> ShowError { get; set; } = Dialogs.Error;

    // ===================== check several rows =====================

    private IEnumerable<InstalledProgram> VisibleCheckablePrograms() => _programsView.Cast<InstalledProgram>().Where(p => p.CanCheck);

    private IEnumerable<StoreApp> VisibleCheckableStoreApps() => _storeView.Cast<StoreApp>().Where(a => a.CanCheck);

    /// <summary>
    /// Programs that are gone after a rescan, and ones that can't be checked any more (a runtime now), leave the checks: a disabled box
    /// is never checked or counted. Runs with every new list.
    /// </summary>
    private void SyncProgramChecks()
    {
        CheckedPrograms.KeepOnly(_programs.Where(p => p.CanCheck).Select(p => p.CheckKey));
        OnPropertyChanged(nameof(AllVisibleProgramsChecked));
    }

    /// <summary>The same for Store apps: removed apps and apps that are protected now leave the checks.</summary>
    private void SyncStoreChecks()
    {
        CheckedStoreApps.KeepOnly(_storeApps.Where(a => a.CanCheck).Select(a => a.CheckKey));
        OnPropertyChanged(nameof(AllVisibleStoreAppsChecked));
    }

    private static void ToggleCheck(InstalledProgram? program)
    {
        if (program is not null)
            program.IsChecked = !program.IsChecked;
    }

    private static void ToggleCheck(StoreApp? app)
    {
        if (app is not null)
            app.IsChecked = !app.IsChecked;
    }

    private void ToggleAllVisiblePrograms()
    {
        CheckedPrograms.SetAll(VisibleCheckablePrograms().Select(p => p.CheckKey), AllVisibleProgramsChecked != true);
        // Also when nothing changed: the clicked header checkbox has toggled itself and must show the real state again.
        OnPropertyChanged(nameof(AllVisibleProgramsChecked));
    }

    private void ToggleAllVisibleStoreApps()
    {
        CheckedStoreApps.SetAll(VisibleCheckableStoreApps().Select(a => a.CheckKey), AllVisibleStoreAppsChecked != true);
        OnPropertyChanged(nameof(AllVisibleStoreAppsChecked));
    }

    private void OnProgramChecksChanged()
    {
        foreach (var p in _programs)
            p.OnCheckedChanged();
        OnPropertyChanged(nameof(CheckedProgramsCount));
        OnPropertyChanged(nameof(AllVisibleProgramsChecked));
        RelayCommand.Refresh();
    }

    private void OnStoreChecksChanged()
    {
        foreach (var a in _storeApps)
            a.OnCheckedChanged();
        OnPropertyChanged(nameof(CheckedStoreAppsCount));
        OnPropertyChanged(nameof(AllVisibleStoreAppsChecked));
        RelayCommand.Refresh();
    }

    /// <summary>Splits the checked rows into those the list shows (in list order) and those it does not (by name). Changes nothing.</summary>
    private static CheckedRows<T> SplitChecked<T>(IEnumerable<T> listed, IEnumerable<T> all, Func<T, bool> isChecked, Func<T, string> name)
    {
        var rows = new CheckedRows<T>();
        rows.Listed.AddRange(listed.Where(isChecked));
        var shown = rows.Listed.ToHashSet();
        rows.Hidden.AddRange(all.Where(r => isChecked(r) && !shown.Contains(r)).OrderBy(name, NameComparer));
        return rows;
    }

    internal CheckedRows<InstalledProgram> PlanCheckedPrograms()
        => SplitChecked(_programsView.Cast<InstalledProgram>(), _programs, p => p.CanCheck && CheckedPrograms.IsChecked(p.CheckKey),
            p => p.DisplayName);

    internal CheckedRows<StoreApp> PlanCheckedStoreApps()
        => SplitChecked(_storeView.Cast<StoreApp>(), _storeApps, a => a.CanCheck && CheckedStoreApps.IsChecked(a.CheckKey),
            a => a.DisplayName);

    /// <summary>"Удалить 3 программы:" and the names; rows the list does not show come first, under their own heading, never folded.</summary>
    private static void AppendTargets<T>(StringBuilder sb, string total, CheckedRows<T> plan, Func<T, string> line)
    {
        if (plan.Hidden.Count == 0)
        {
            sb.AppendLine(total + ":");
            sb.AppendLine(BulkText.BulletList(plan.Listed.Select(line)));
            return;
        }
        sb.AppendLine(total + ".");
        sb.AppendLine();
        sb.AppendLine("Не видны в списке сейчас:");
        sb.AppendLine(BulkText.BulletList(plan.Hidden.Select(line), plan.Hidden.Count));
        if (plan.Listed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("В списке:");
            sb.AppendLine(BulkText.BulletList(plan.Listed.Select(line)));
        }
    }

    /// <summary>A section of a summary: the heading and every line as a bullet; nothing when there are no lines.</summary>
    private static void AddSection(List<string> sections, string heading, IReadOnlyCollection<string> lines, bool fold = false)
    {
        if (lines.Count > 0)
            sections.Add(heading + "\n" + (fold ? BulkText.BulletList(lines) : BulkText.BulletList(lines, lines.Count)));
    }

    // ===================== programs: "Удалить отмеченные" =====================

    /// <summary>What "Удалить отмеченные" did, filled while it runs, so a stop half way still reports the real state.</summary>
    private sealed class UninstallCheckedRun
    {
        public UninstallCheckedRun(IReadOnlyList<InstalledProgram> targets) => Targets = targets;

        /// <summary>The checked programs, in the order of the question.</summary>
        public IReadOnlyList<InstalledProgram> Targets { get; }

        /// <summary>Programs whose turn came and what happened; a null outcome: the program was gone before its turn.</summary>
        public List<(InstalledProgram Program, UninstallOutcome? Outcome)> Done { get; } = new();

        /// <summary>Programs left alone at their turn because they can't be checked any more (a runtime, a broken entry now).</summary>
        public List<(InstalledProgram Program, string Reason)> Skipped { get; } = new();

        /// <summary>The program whose turn it is (set while the batch works on it).</summary>
        public InstalledProgram? Current { get; set; }

        /// <summary>
        /// The program whose uninstaller was started and had not reported back when «Отмена» or an error stopped the batch: its removal
        /// may go on.
        /// </summary>
        public InstalledProgram? Interrupted { get; set; }

        /// <summary>The unexpected error that stopped the batch (already logged); null when it ran to the end or «Отмена» stopped it.</summary>
        public Exception? Error { get; set; }

        /// <summary>The program whose turn it was when <see cref="Error"/> happened.</summary>
        public InstalledProgram? ErrorOn { get; set; }

        /// <summary>Programs that were never started: «Отмена» or the error stopped the batch before their turn.</summary>
        public List<InstalledProgram> NotStarted => Targets
            .Where(t => !Done.Any(d => ReferenceEquals(d.Program, t)) && !Skipped.Any(s => ReferenceEquals(s.Program, t)) &&
                        !ReferenceEquals(t, Interrupted) && !ReferenceEquals(t, ErrorOn))
            .ToList();
    }

    private async Task UninstallCheckedAsync()
    {
        if (IsBusy)
            return; // RunBusyAsync would silently skip the work
        var plan = PlanCheckedPrograms();
        if (plan.Count == 0)
            return;
        if (!Confirm(plan.Count == 1 ? "Удалить отмеченную программу?" : "Удалить отмеченные программы?",
                BuildUninstallCheckedConfirmation(plan)))
            return;

        var run = new UninstallCheckedRun(plan.All);
        await RunBusyAsync("Удаляем отмеченные программы…", async ct =>
        {
            await UninstallEachAsync(run, ct);
            // Whatever happened, the list shows what is installed now: removed programs leave it, and with it the checks.
            ReportProgress(-1, "Обновляем список программ…");
            await RescanProgramsAsync();
        });
        // An uninstaller that may still be running leaves the checks, so the button can't start a second one beside it.
        if (run.Interrupted is { } interrupted)
            CheckedPrograms.Set(interrupted.CheckKey, false);
        (run.Error is null ? ShowInfo : ShowError)("Удаление отмеченных программ", UninstallCheckedSummary(run));
    }

    /// <summary>
    /// Strictly one after another: each program is read again right before its turn (gone: counted as removed; no longer checkable:
    /// skipped; nothing is started for either), then uninstalled quietly when its installer allows it, otherwise with its own window.
    /// One restore point for the batch, right before the first program that is really started. «Отмена» stops before the next program;
    /// while an uninstaller runs it only stops the waiting. An unexpected error stops the batch too: it is logged and reported with
    /// what was done, in the one message.
    /// </summary>
    private async Task UninstallEachAsync(UninstallCheckedRun run, CancellationToken ct)
    {
        var targets = run.Targets;
        var restorePointMade = false;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested(); // «Отмена» between two programs: the next turn does not begin
                var scanned = targets[i];
                var (number, percent) = (i + 1, 100.0 * i / targets.Count);
                run.Current = scanned;
                ReportProgress(percent, $"Удаляем ({number} из {targets.Count}): {scanned.DisplayName}");
                // An earlier uninstaller may have taken it along, it may have become a broken entry, or an update may have changed
                // its uninstall command.
                var current = (await _service.ScanProgramsAsync(ct)).Programs.FirstOrDefault(p => p.CheckKey == scanned.CheckKey);
                if (current is null)
                {
                    run.Done.Add((scanned, null));
                }
                else if (!current.CanCheck)
                {
                    run.Skipped.Add((scanned, SkipReason(current)));
                }
                else
                {
                    if (!restorePointMade)
                    {
                        ReportProgress(-1, "Создаём точку восстановления…");
                        await _service.CreateRestorePointAsync($"Удаление отмеченных программ ({targets.Count})", ct);
                        restorePointMade = true;
                    }
                    var quiet = ProgramsService.ShouldUninstallQuietly(current);
                    ReportProgress(percent, UninstallProgressText(number, targets.Count, scanned.DisplayName, quiet));
                    run.Interrupted = scanned;
                    var outcome = await _service.UninstallQuietWhenPossibleAsync(current, ct,
                        exe => ReportProgress(percent, UninstallProgressText(number, targets.Count, scanned.DisplayName, quiet, exe)));
                    run.Interrupted = null;
                    run.Done.Add((scanned, outcome));
                }
                run.Current = null;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info(run.Interrupted is { } p
                ? $"Удаление отмеченных программ остановлено пользователем: ожидание деинсталлятора «{p.DisplayName}» прервано"
                : "Удаление отмеченных программ остановлено пользователем");
        }
        catch (Exception ex)
        {
            run.Error = ex;
            run.ErrorOn = run.Current;
            Log.Error("Удаление отмеченных программ", ex);
        }
    }

    /// <summary>
    /// The busy text while a checked program is being removed: "Удаляем (2 из 5): name" and, like the single uninstall, what the page
    /// is waiting for (<paramref name="exe"/> once the runner knows it). A quiet run has no window to speak of.
    /// </summary>
    internal static string UninstallProgressText(int number, int total, string name, bool quiet, string? exe = null)
    {
        var process = exe is null ? "" : $" ({exe})";
        return $"Удаляем ({number} из {total}): {name}\n" + (quiet
            ? $"Ждём завершения тихого удаления{process}. «Отмена» остановит только ожидание, а не само удаление."
            : $"Ждём завершения деинсталлятора{process}. Если его окно уже закрыто, нажмите «Отмена».");
    }

    /// <summary>Why a checked program is left alone at its turn (it can't be checked any more), as a short phrase.</summary>
    private static string SkipReason(InstalledProgram p)
        => p.IsRuntime ? "компонент, который нужен другим программам: такие удаляют по одному"
            : p.IsBroken ? "деинсталлятор не найден"
            : "у программы не указан деинсталлятор";

    /// <summary>
    /// One line per program: its name and how it will be removed. A program in the list can only be checked when it can be uninstalled,
    /// so the "деинсталлятор не найден" mark is a fallback.
    /// </summary>
    private static string UninstallLine(InstalledProgram p)
        => !p.CanUninstall ? $"{p.DisplayName} — деинсталлятор не найден"
            : ProgramsService.ShouldUninstallQuietly(p) ? $"{p.DisplayName} — тихо"
            : p.DisplayName;

    /// <summary>The question before "Удалить отмеченные": exactly the programs that will be removed, hidden ones first.</summary>
    internal static string BuildUninstallCheckedConfirmation(CheckedRows<InstalledProgram> plan)
    {
        var sb = new StringBuilder();
        AppendTargets(sb, $"Удалить {Format.Count(plan.Count, "программу", "программы", "программ")}", plan, UninstallLine);
        sb.AppendLine();
        sb.AppendLine("Программы, которые умеют удаляться тихо, удалятся без окон; для остальных по очереди откроются их окна удаления.");
        sb.AppendLine("Программы удаляются по одной. «Отмена» не прерывает уже начатое удаление, но следующие программы удаляться не начнут.");
        sb.AppendLine();
        sb.AppendLine("Перед удалением будет создана точка восстановления Windows (если это включено в настройках и есть права администратора).");
        sb.AppendLine("Отменить удаление нельзя, но программы можно установить заново.");
        sb.AppendLine();
        sb.Append("Продолжить?");
        return sb.ToString();
    }

    /// <summary>
    /// The one message after "Удалить отмеченные", grouped by what happened. Read after the rescan: a program the uninstaller reported
    /// as still installed, or whose uninstaller was still awaited, counts as removed when it is no longer in the list. An error that
    /// stopped the batch is named right after the count.
    /// </summary>
    private string UninstallCheckedSummary(UninstallCheckedRun run)
    {
        bool StillListed(InstalledProgram p) => _programs.Any(x => x.CheckKey == p.CheckKey);

        var removed = new List<string>();
        var stillInstalled = new List<string>();
        var uacDeclined = new List<string>();
        var notStarted = new List<string>();
        foreach (var (p, outcome) in run.Done)
        {
            if (outcome is null)
            {
                removed.Add(p.DisplayName); // gone before its turn
                continue;
            }
            switch (outcome.Status)
            {
                case UninstallStatus.Removed:
                    removed.Add(p.DisplayName);
                    break;
                case UninstallStatus.StillInstalled:
                    (StillListed(p) ? stillInstalled : removed).Add(p.DisplayName);
                    break;
                case UninstallStatus.UacDeclined:
                    uacDeclined.Add(p.DisplayName);
                    break;
                default:
                    notStarted.Add($"{p.DisplayName} — {FirstParagraph(outcome.Message)}");
                    break;
            }
        }
        // After an error the program it happened on is named with the error instead.
        var mayContinue = new List<string>();
        if (run.Error is null && run.Interrupted is { } interrupted)
            (StillListed(interrupted) ? mayContinue : removed).Add(interrupted.DisplayName);

        var sections = new List<string> { $"Удалено: {removed.Count} из {run.Targets.Count}." };
        if (run.Error is { } error)
            sections.Add(run.ErrorOn is { } on
                ? $"Остановлено из-за ошибки на «{on.DisplayName}»: {ErrorText.For(error)}."
                : $"Остановлено из-за ошибки: {ErrorText.For(error)}.");
        AddSection(sections, $"Удалены ({removed.Count}):", removed, fold: true);
        AddSection(sections, $"Пропущены ({run.Skipped.Count}):", run.Skipped.Select(s => $"{s.Program.DisplayName} — {s.Reason}").ToList());
        AddSection(sections, $"Остались установленными ({stillInstalled.Count}) — деинсталлятор завершил работу, но программа всё ещё в " +
                             "списке (возможно, удаление отменили в его окне):", stillInstalled);
        AddSection(sections, $"Вы отклонили запрос контроля учётных записей ({uacDeclined.Count}):", uacDeclined);
        AddSection(sections, $"Не удалось запустить удаление ({notStarted.Count}):", notStarted);
        AddSection(sections, "Ожидание остановлено — удаление могло продолжиться:", mayContinue);
        if (mayContinue.Count > 0)
            sections.Add("Если окно удаления ещё открыто, дождитесь его завершения и нажмите «Обновить».");
        var notBegun = run.NotStarted.Select(p => p.DisplayName).ToList();
        AddSection(sections, $"Не начаты из-за {(run.Error is null ? "отмены" : "ошибки")} ({notBegun.Count}):", notBegun);
        return string.Join("\n\n", sections);
    }

    /// <summary>The first paragraph of an uninstaller message, on one line ("Файл деинсталлятора не найден: C:\…\uninst.exe").</summary>
    private static string FirstParagraph(string message)
    {
        var text = message.ReplaceLineEndings("\n");
        var end = text.IndexOf("\n\n", StringComparison.Ordinal);
        return (end < 0 ? text : text[..end]).Replace('\n', ' ').Trim();
    }

    // ===================== Store apps: "Удалить отмеченные" =====================

    /// <summary>What "Удалить отмеченные" did on the Store tab, filled while it runs.</summary>
    private sealed class RemoveCheckedAppsRun
    {
        public RemoveCheckedAppsRun(IReadOnlyList<StoreApp> targets) => Targets = targets;

        /// <summary>The checked apps, in the order of the question.</summary>
        public IReadOnlyList<StoreApp> Targets { get; }

        public List<StoreApp> Removed { get; } = new();

        public List<(StoreApp App, string Error)> Failed { get; } = new();

        /// <summary>The app whose turn it is (set while the batch works on it).</summary>
        public StoreApp? Current { get; set; }

        /// <summary>The unexpected error that stopped the batch (already logged); null when it ran to the end or «Отмена» stopped it.</summary>
        public Exception? Error { get; set; }

        /// <summary>The app whose turn it was when <see cref="Error"/> happened.</summary>
        public StoreApp? ErrorOn { get; set; }

        /// <summary>Apps «Отмена» or the error stopped: the one being removed («Отмена») and those after it.</summary>
        public List<StoreApp> Stopped => Targets
            .Where(t => !Removed.Contains(t) && !Failed.Any(f => ReferenceEquals(f.App, t)) && !ReferenceEquals(t, ErrorOn))
            .ToList();
    }

    private async Task RemoveCheckedStoreAppsAsync()
    {
        if (IsBusy)
            return;
        var plan = PlanCheckedStoreApps();
        if (plan.Count == 0)
            return;
        if (!Confirm(plan.Count == 1 ? "Удалить отмеченное приложение?" : "Удалить отмеченные приложения?",
                BuildRemoveCheckedAppsConfirmation(plan)))
            return;

        var run = new RemoveCheckedAppsRun(plan.All);
        await RunBusyAsync("Удаляем отмеченные приложения…", async ct =>
        {
            await RemoveEachAsync(run, ct);
            ReportProgress(-1, "Обновляем список приложений…");
            await RescanStoreAppsAsync();
        });
        (run.Error is null ? ShowInfo : ShowError)("Удаление отмеченных приложений", RemoveCheckedAppsSummary(run));
    }

    /// <summary>
    /// Like "Удалить лишние": one restore point, then the apps one by one; «Отмена» stops before the next one. An unexpected error stops
    /// the batch too: it is logged and reported with what was done, in the one message.
    /// </summary>
    private async Task RemoveEachAsync(RemoveCheckedAppsRun run, CancellationToken ct)
    {
        var targets = run.Targets;
        try
        {
            ReportProgress(-1, "Создаём точку восстановления…");
            await _service.CreateRestorePointAsync($"Удаление приложений Microsoft Store ({targets.Count})", ct);
            for (var i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var app = targets[i];
                run.Current = app;
                ReportProgress(100.0 * i / targets.Count, $"Удаляем «{app.DisplayName}» ({i + 1} из {targets.Count})…");
                var (success, message) = await _service.RemoveStoreAppAsync(app, ct);
                if (success)
                    run.Removed.Add(app);
                else
                    run.Failed.Add((app, message));
                run.Current = null;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("Удаление отмеченных приложений Microsoft Store остановлено пользователем");
        }
        catch (Exception ex)
        {
            run.Error = ex;
            run.ErrorOn = run.Current;
            Log.Error("Удаление отмеченных приложений Microsoft Store", ex);
        }
    }

    /// <summary>The question before "Удалить отмеченные" on the Store tab: exactly the apps that will be removed, hidden ones first.</summary>
    internal static string BuildRemoveCheckedAppsConfirmation(CheckedRows<StoreApp> plan)
    {
        var all = plan.All;
        var sb = new StringBuilder();
        AppendTargets(sb, $"Удалить {Format.Count(plan.Count, "приложение", "приложения", "приложений")}", plan, a => a.DisplayName);
        sb.AppendLine();
        sb.Append("Приложения удаляются только для вашей учётной записи. ");
        sb.AppendLine(all.Count == 1
            ? all[0].UndoText
            : all.All(a => a.IsFromStore)
                ? "Любое из них можно вернуть, установив заново из Microsoft Store."
                : "Приложения из Microsoft Store можно вернуть, установив заново оттуда же; остальные — только переустановив программу, " +
                  "которая их поставила.");
        sb.AppendLine("Перед удалением будет создана точка восстановления (если это включено в настройках и есть права администратора).");
        sb.AppendLine();
        sb.Append("Продолжить?");
        return sb.ToString();
    }

    /// <summary>
    /// The one message after "Удалить отмеченные" on the Store tab. An app «Отмена» stopped counts as removed when the list, read
    /// again without an error, no longer has it. An error that stopped the batch is named right after the count.
    /// </summary>
    private string RemoveCheckedAppsSummary(RemoveCheckedAppsRun run)
    {
        var gone = run.Stopped.Where(a => !HasStoreError && !_storeApps.Any(x => x.CheckKey == a.CheckKey)).ToList();
        var removed = run.Removed.Count + gone.Count;
        var stopped = run.Stopped.Except(gone).Select(a => a.DisplayName).ToList();

        var sections = new List<string>
        {
            $"Удалено {Format.Count(removed, "приложение", "приложения", "приложений")} из {run.Targets.Count}.",
        };
        if (run.Error is { } error)
            sections.Add(run.ErrorOn is { } on
                ? $"Остановлено из-за ошибки на «{on.DisplayName}»: {ErrorText.For(error)}."
                : $"Остановлено из-за ошибки: {ErrorText.For(error)}.");
        AddSection(sections, $"Не удалось удалить ({run.Failed.Count}):", run.Failed.Select(f => $"{f.App.DisplayName} — {f.Error}").ToList());
        AddSection(sections, $"Не удалены из-за {(run.Error is null ? "отмены" : "ошибки")} ({stopped.Count}):", stopped);
        if (removed > 0)
            sections.Add("Приложения из Microsoft Store можно вернуть через Microsoft Store.");
        return string.Join("\n\n", sections);
    }
}
