using System.Text;
using PcCleaner.Core;
using ElevatedRestart = PcCleaner.Modules.Settings.ElevatedRestart;

namespace PcCleaner.Modules.Startup;

/// <summary>The buttons of the bulk action bar.</summary>
internal enum StartupBulkAction
{
    Disable,
    Enable,
    Delete,
}

/// <summary>
/// What a bulk button is about to do with the checked entries, taken from the checks right before the question. Every checked entry
/// is in exactly one of the three lists, so the question can name all of them.
/// </summary>
internal sealed class StartupCheckedPlan
{
    public StartupCheckedPlan(StartupBulkAction action) => Action = action;

    public StartupBulkAction Action { get; }

    /// <summary>Checked entries the action changes and the list shows right now, in list order.</summary>
    public List<StartupItem> Listed { get; } = new();

    /// <summary>
    /// Checked entries the action changes but the list does not show right now (search, source tab, status card, the hidden-Windows
    /// toggles), by name. They are changed too, so the question names them first.
    /// </summary>
    public List<StartupItem> Hidden { get; } = new();

    /// <summary>Checked entries the action leaves alone, with the reason.</summary>
    public List<(StartupItem Item, string Reason)> Skipped { get; } = new();

    /// <summary>Everything the action changes: the listed entries, then the hidden ones.</summary>
    public IEnumerable<StartupItem> Items => Listed.Concat(Hidden);

    public int Count => Listed.Count + Hidden.Count;

    /// <summary>Moves the entries that match from the changed ones to the skipped ones.</summary>
    public void Skip(Func<StartupItem, bool> match, string reason)
    {
        foreach (var list in new[] { Listed, Hidden })
        {
            foreach (var item in list.Where(match).ToList())
            {
                list.Remove(item);
                Skipped.Add((item, reason));
            }
        }
    }
}

// The checkbox column, the header box and the bulk bar of the Startup page: "Отключить", "Включить", "Удалить" for the checked
// entries. The page, its filters and the single-entry actions are in StartupViewModel.cs.
public sealed partial class StartupViewModel
{
    // ===================== state, commands and test seams =====================

    /// <summary>"Отключить" in the bulk bar: switches off the checked entries.</summary>
    public AsyncRelayCommand DisableCheckedCommand { get; }

    /// <summary>"Включить" in the bulk bar.</summary>
    public AsyncRelayCommand EnableCheckedCommand { get; }

    /// <summary>"Удалить" in the bulk bar.</summary>
    public AsyncRelayCommand DeleteCheckedCommand { get; }

    /// <summary>"Снять отметки".</summary>
    public RelayCommand ClearChecksCommand { get; }

    /// <summary>A row's checkbox click (parameter: the row).</summary>
    public RelayCommand ToggleCheckCommand { get; }

    /// <summary>Header checkbox click: checks every visible row that can be checked, or unchecks them when all are.</summary>
    public RelayCommand ToggleAllVisibleCommand { get; }

    /// <summary>
    /// Checked entries, by <see cref="StartupItem.Id"/> only: rows read <see cref="StartupItem.IsChecked"/> from here, so the checks
    /// survive a rescan (which builds new row objects) and what the buttons act on is exactly this set.
    /// </summary>
    internal CheckedSet<string> Checked { get; } = new();

    /// <summary>"Отмечено: N" of the bulk action bar.</summary>
    public int CheckedCount => Checked.Count;

    /// <summary>Header checkbox over the rows the list shows: true = all checked, false = none, null = some.</summary>
    public bool? AllVisibleChecked => Checked.StateOf(VisibleCheckable().Select(i => i.Id));

    /// <summary>The page's Yes/No question before the bulk actions (<see cref="Dialogs.Confirm"/>; tests replace it).</summary>
    internal Func<string, string, bool> Confirm { get; set; } = Dialogs.Confirm;

    /// <summary>The one message after a bulk action (tests replace it).</summary>
    internal Action<string, string> ShowInfo { get; set; } = Dialogs.Info;

    /// <summary>
    /// Offers the restart as administrator (tests replace it so nothing restarts). Returns true when the user agreed and the restart
    /// has started: the program is closing, so the caller must stop at once. Declining the question returns false.
    /// </summary>
    /// <remarks><see cref="ElevatedRestart.RequireElevation"/> itself returns false in both cases; the restart is told by <see cref="Admin.RestartStarted"/>.</remarks>
    internal Func<string, bool> RequireElevation { get; set; } = action =>
    {
        ElevatedRestart.RequireElevation(action);
        return Admin.RestartStarted;
    };

    /// <summary>Creates the restore point before services are changed (tests replace it so none is really created).</summary>
    internal Func<string, CancellationToken, Task<bool>> CreateRestorePoint { get; set; } =
        (description, ct) => RestorePoint.TryCreateAsync(description, ct);

    /// <summary>
    /// Entries that are gone, and ones that can't be changed any more (a rescan found them protected), leave the checks: a disabled
    /// box is never checked or counted. The header follows the visible rows. Runs with every change of the list or its filter.
    /// </summary>
    private void SyncChecks()
    {
        Checked.KeepOnly(_items.Where(i => i.CanCheck).Select(i => i.Id));
        OnPropertyChanged(nameof(AllVisibleChecked));
    }

    /// <summary>The same for several entries at once: every row is refreshed, the counters are recomputed once.</summary>
    private void AfterItemsChanged(IEnumerable<StartupItem> items)
    {
        foreach (var item in items)
            item.NotifyStateChanged();
        RecomputeCounts();
        RelayCommand.Refresh();
    }

    // ===================== check several entries =====================

    private IEnumerable<StartupItem> VisibleCheckable() => ItemsView.Cast<StartupItem>().Where(i => i.CanCheck);

    private static void ToggleCheck(StartupItem? item)
    {
        if (item is not null)
            item.IsChecked = !item.IsChecked;
    }

    private void ToggleAllVisible()
    {
        Checked.SetAll(VisibleCheckable().Select(i => i.Id), AllVisibleChecked != true);
        // Also when nothing changed: the clicked header checkbox has toggled itself and must show the real state again.
        OnPropertyChanged(nameof(AllVisibleChecked));
    }

    private void OnChecksChanged()
    {
        foreach (var item in _items)
            item.OnCheckedChanged();
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(AllVisibleChecked));
        RelayCommand.Refresh();
    }

    private const string ProtectedReason = "системная запись Windows, программа её не меняет";

    private const string NeedsAdminReason = "нужны права администратора";

    /// <summary>Why a checkable entry is left alone by the action; null when the action changes it.</summary>
    private static string? NotApplicableReason(StartupItem item, StartupBulkAction action) => action switch
    {
        StartupBulkAction.Disable => !item.CanToggle
            ? "однократный запуск нельзя отключить, его можно только удалить"
            : item.IsEnabled ? null : "уже отключена",
        StartupBulkAction.Enable => !item.CanToggle
            ? "однократный запуск не бывает отключённым, включать нечего"
            : item.IsEnabled ? "уже включена" : null,
        _ => item.CanDelete ? null : "службы не удаляются, их можно только перевести в ручной режим",
    };

    /// <summary>
    /// Machine-wide entries and services need administrator rights. A scheduled task is not asked about: it may well be changeable
    /// without them (a task of the current user), and when it is not, the change reports it.
    /// </summary>
    private static bool NeedsAdminRights(StartupItem item) => item.RequiresAdmin && !item.IsTask;

    /// <summary>
    /// Takes the checks as they are right now: the checked entries the list shows are split from those it does not (search, tab,
    /// card), and each lands in the list the action changes or in the skipped one with its reason. Changes nothing.
    /// </summary>
    internal StartupCheckedPlan PlanChecked(StartupBulkAction action)
    {
        var plan = new StartupCheckedPlan(action);
        var listed = ItemsView.Cast<StartupItem>().ToList();
        var listedIds = listed.Select(i => i.Id).ToHashSet();
        var hidden = _items.Where(i => !listedIds.Contains(i.Id)).OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase);
        foreach (var item in listed.Concat(hidden).Where(i => Checked.IsChecked(i.Id)))
        {
            var reason = item.CanCheck ? NotApplicableReason(item, action) : ProtectedReason;
            if (reason is not null)
                plan.Skipped.Add((item, reason));
            else
                (listedIds.Contains(item.Id) ? plan.Listed : plan.Hidden).Add(item);
        }
        return plan;
    }

    /// <summary>What a bulk button did: filled while it runs, so a stop or an error half way still shows the real state.</summary>
    private sealed class CheckedOutcome
    {
        public bool Started { get; set; }

        public bool Finished { get; set; }

        /// <summary>The user pressed «Отмена»: the rest was left alone.</summary>
        public bool Cancelled { get; set; }

        public bool NeedsAdmin { get; set; }

        /// <summary>Entries a change was tried on (their rows are refreshed afterwards).</summary>
        public List<StartupItem> Attempted { get; } = new();

        public List<StartupItem> Done { get; } = new();

        public List<(StartupItem Item, string Error)> Failed { get; } = new();

        /// <summary>The result of the deletion; null if it was stopped or failed half way.</summary>
        public StartupBulkResult? Deleted { get; set; }
    }

    private async Task ChangeCheckedAsync(StartupBulkAction action)
    {
        if (IsBusy)
            return; // RunBusyAsync would silently skip the work
        var plan = PlanChecked(action);
        // A disabled box is never checked: entries found protected right now leave the checks at once.
        Checked.SetAll(plan.Skipped.Where(s => !s.Item.CanCheck).Select(s => s.Item.Id), false);

        if (!IsElevated && plan.Items.Any(NeedsAdminRights))
        {
            // Agreeing starts the restart, and the program closes: nothing more is asked, shown or changed (the checks stay, they
            // are gone with the process anyway). Declining leaves the rest of the entries to this run.
            if (RequireElevation(ElevationText(action)))
                return;
            plan.Skip(NeedsAdminRights, NeedsAdminReason);
        }

        if (plan.Count == 0)
        {
            if (plan.Skipped.Count == 0)
                return;
            var nothing = action switch
            {
                StartupBulkAction.Disable => "Нечего отключать",
                StartupBulkAction.Enable => "Нечего включать",
                _ => "Нечего удалять",
            };
            ShowMessage($"{nothing}: ни одна из отмеченных записей не подходит для этого действия.");
            ShowInfo("Автозагрузка", $"{nothing}.\n\n{SkippedSection(plan.Skipped)}");
            return;
        }

        if (!Confirm(CheckedTitle(plan), BuildCheckedConfirm(plan)))
            return;

        var outcome = new CheckedOutcome();
        await RunBusyAsync(action switch
        {
            StartupBulkAction.Disable => "Отключаем отмеченные записи…",
            StartupBulkAction.Enable => "Включаем отмеченные записи…",
            _ => "Удаляем отмеченные записи из автозагрузки…",
        }, ct => RunCheckedAsync(plan, outcome, ct));

        // Whatever happened, the rows show the real state again (this also resets a switch after a failure or «Отмена»), and what was
        // done leaves the checks; what failed or was left alone stays checked.
        Checked.SetAll(outcome.Done.Select(i => i.Id), false);
        AfterItemsChanged(outcome.Attempted);
        if (!outcome.Started)
            return;
        if (action == StartupBulkAction.Delete && outcome.Deleted is null)
        {
            // Stopped or failed half way: some entries may be gone already, so show the real list.
            ShowMessage(outcome.Cancelled
                ? "Удаление остановлено. Часть записей могла быть удалена — список обновлён."
                : "Не удалось удалить отмеченные записи. Список обновлён.", isError: !outcome.Cancelled);
            await LoadAsync();
            return;
        }
        if (action != StartupBulkAction.Delete && !outcome.Finished)
        {
            // An unexpected error half way: RunBusyAsync has shown it. The banner says what was done before it, so the entries that
            // were changed do not just stop being checked without a word.
            ShowMessage(CheckedBanner(action, outcome.Done.Count, outcome.Failed.Count, plan.Skipped.Count, CheckedStop.Error), isError: true);
            return;
        }

        int done, failed;
        bool needsAdmin;
        string details;
        if (action == StartupBulkAction.Delete)
        {
            var result = outcome.Deleted!;
            RemoveItems(result.Done);
            (done, failed, needsAdmin) = (result.Done.Count, result.Failed.Count, result.NeedsAdmin);
            details = result.Summary;
            if (plan.Skipped.Count > 0)
                details += $"{Environment.NewLine}{Environment.NewLine}{SkippedSection(plan.Skipped)}";
        }
        else
        {
            (done, failed, needsAdmin) = (outcome.Done.Count, outcome.Failed.Count, outcome.NeedsAdmin);
            details = ToggleDetails(plan, outcome);
        }

        ShowMessage(CheckedBanner(action, done, failed, plan.Skipped.Count, outcome.Cancelled ? CheckedStop.Cancelled : CheckedStop.None),
            isError: failed > 0);
        if (done == 0 && needsAdmin && !IsElevated)
            RequireElevation(ElevationText(action));
        else
            ShowInfo("Автозагрузка", details);
    }

    private async Task RunCheckedAsync(StartupCheckedPlan plan, CheckedOutcome outcome, CancellationToken ct)
    {
        outcome.Started = true;
        var items = plan.Items.ToList();
        try
        {
            if (plan.Action == StartupBulkAction.Delete)
            {
                outcome.Deleted = await _service.DeleteManyAsync(items, ct);
            }
            else
            {
                var enable = plan.Action == StartupBulkAction.Enable;
                var services = items.Count(i => i.IsService);
                if (services > 0)
                {
                    // One restore point for all of them, like the single switch makes one for its service.
                    ReportProgress(-1, "Создаём точку восстановления…");
                    await CreateRestorePoint($"Автозагрузка: {Format.Count(services, "служба", "службы", "служб")}", ct);
                }
                for (var i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = items[i];
                    ReportProgress(i * 100.0 / items.Count,
                        $"{(enable ? "Включаем" : "Отключаем")} «{item.DisplayName}»… ({i + 1} из {items.Count})");
                    outcome.Attempted.Add(item);
                    var result = await _service.SetEnabledAsync(item, enable, ct);
                    if (result.Success)
                    {
                        outcome.Done.Add(item);
                        continue;
                    }
                    outcome.Failed.Add((item, result.Message));
                    outcome.NeedsAdmin |= result.NeedsAdmin;
                }
            }
        }
        catch (OperationCanceledException)
        {
            outcome.Cancelled = true; // keep what was done; the rest stays as it was
        }
        outcome.Finished = true;
    }

    private static string ElevationText(StartupBulkAction action) => action switch
    {
        StartupBulkAction.Disable => "Отключение отмеченных записей автозагрузки",
        StartupBulkAction.Enable => "Включение отмеченных записей автозагрузки",
        _ => "Удаление отмеченных записей из автозагрузки",
    };

    private static string CheckedTitle(StartupCheckedPlan plan)
    {
        var one = plan.Count == 1;
        return plan.Action switch
        {
            StartupBulkAction.Disable => one ? "Отключить отмеченную запись?" : "Отключить отмеченные записи?",
            StartupBulkAction.Enable => one ? "Включить отмеченную запись?" : "Включить отмеченные записи?",
            _ => one ? "Удалить отмеченную запись из автозагрузки?" : "Удалить отмеченные записи из автозагрузки?",
        };
    }

    /// <summary>One line per entry: its name and where it comes from, so two entries of the same name can be told apart.</summary>
    private static string EntryLine(StartupItem item) => item.Kind switch
    {
        StartupKind.ScheduledTask => $"{item.DisplayName} — Планировщик, {item.TaskPath}",
        StartupKind.Service => $"{item.DisplayName} — служба {item.ServiceName}",
        _ => $"{item.DisplayName} — {item.SourceShort}, {item.SourceDetail}",
    };

    private static string SkippedSection(IReadOnlyList<(StartupItem Item, string Reason)> skipped)
        => $"Не затронуты ({skipped.Count}):\n" + BulkText.BulletList(skipped.Select(s => $"{s.Item.DisplayName} — {s.Reason}"));

    /// <summary>The question before a bulk action: exactly the entries the action will change, hidden ones first.</summary>
    private static string BuildCheckedConfirm(StartupCheckedPlan plan)
    {
        var count = Format.Count(plan.Count, "запись", "записи", "записей");
        var total = plan.Action switch
        {
            StartupBulkAction.Disable => $"Отключить автозапуск ({count})",
            StartupBulkAction.Enable => $"Включить автозапуск ({count})",
            _ => $"Удалить из автозагрузки ({count})",
        };

        var sb = new StringBuilder();
        if (plan.Hidden.Count == 0)
        {
            sb.AppendLine(total + ":");
            sb.AppendLine(BulkText.BulletList(plan.Listed.Select(EntryLine)));
        }
        else
        {
            // They are changed too, so they come first and are never folded into "…и ещё N".
            sb.AppendLine(total + ".");
            sb.AppendLine();
            sb.AppendLine("Не видны в списке сейчас:");
            sb.AppendLine(BulkText.BulletList(plan.Hidden.Select(EntryLine), plan.Hidden.Count));
            if (plan.Listed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("В списке:");
                sb.AppendLine(BulkText.BulletList(plan.Listed.Select(EntryLine)));
            }
        }
        if (plan.Skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(SkippedSection(plan.Skipped));
        }

        var items = plan.Items.ToList();
        sb.AppendLine();
        switch (plan.Action)
        {
            case StartupBulkAction.Disable:
                sb.AppendLine("Записи не удаляются: Windows просто перестанет запускать их при входе — так же, как в Диспетчере задач. " +
                              "Включить их обратно можно здесь в любой момент.");
                if (items.Any(i => i.IsService))
                    sb.AppendLine("Службы будут переведены в ручной режим запуска и не удаляются: программы смогут запустить их, когда понадобится. " +
                                  "Прежний режим запоминается — «Включить» вернёт его. " +
                                  "Перед изменением будет создана точка восстановления Windows (если это разрешено в настройках).");
                if (items.Any(i => i.IsTask))
                    sb.AppendLine("Задания останутся в планировщике, но перестанут запускаться.");
                break;

            case StartupBulkAction.Enable:
                sb.AppendLine("Записи снова будут запускаться вместе с Windows.");
                if (items.Any(i => i.IsService))
                    sb.AppendLine("Службам вернётся прежний автоматический запуск. " +
                                  "Перед изменением будет создана точка восстановления Windows (если это разрешено в настройках).");
                break;

            default:
                sb.AppendLine("Сами программы не удаляются — исчезнут только записи автозапуска.");
                sb.AppendLine($"Перед удалением сохраняются резервные копии: реестр — в .reg-файлы, задания — в XML (папка {AppPaths.BackupsDir}), " +
                              "ярлыки перемещаются в Корзину.");
                if (items.Any(i => !i.IsBroken && !i.IsRunOnce))
                    sb.AppendLine("Совет: если вы просто не хотите, чтобы программы запускались сами, их достаточно отключить — это обратимо.");
                break;
        }
        sb.AppendLine();
        sb.Append("Продолжить?");
        return sb.ToString();
    }

    /// <summary>Why a bulk action ended before the last entry.</summary>
    private enum CheckedStop
    {
        None,
        Cancelled,
        Error,
    }

    /// <summary>The page banner after a bulk action: "Отключено: 3, не удалось: 1, не затронуто: 2."</summary>
    private static string CheckedBanner(StartupBulkAction action, int done, int failed, int skipped, CheckedStop stop)
    {
        var verb = action switch
        {
            StartupBulkAction.Disable => "Отключено",
            StartupBulkAction.Enable => "Включено",
            _ => "Удалено",
        };
        var text = $"{verb}: {done}";
        if (failed > 0)
            text += $", не удалось: {failed}";
        if (skipped > 0)
            text += $", не затронуто: {skipped}";
        return text + stop switch
        {
            CheckedStop.Cancelled => ". Остановлено — остальные записи не тронуты.",
            CheckedStop.Error => ". Остановлено из-за ошибки — остальные записи не тронуты.",
            _ => ".",
        };
    }

    /// <summary>The one message after "Отключить" / "Включить" (<see cref="StartupBulkResult.Summary"/> does the same for the deletion).</summary>
    private static string ToggleDetails(StartupCheckedPlan plan, CheckedOutcome outcome)
    {
        var enable = plan.Action == StartupBulkAction.Enable;
        var lines = new List<string>
        {
            outcome.Done.Count > 0
                ? $"{(enable ? "Включено" : "Отключено")}: {Format.Count(outcome.Done.Count, "запись", "записи", "записей")}."
                : $"Ничего не {(enable ? "включено" : "отключено")}.",
        };
        if (outcome.Cancelled)
            lines.Add("Остановлено: остальные записи не тронуты.");
        if (outcome.Failed.Count > 0)
        {
            lines.Add("");
            lines.Add($"Не удалось изменить ({outcome.Failed.Count}):");
            lines.Add(BulkText.BulletList(outcome.Failed.Select(f => $"{f.Item.DisplayName} — {f.Error}"), 10));
        }
        if (plan.Skipped.Count > 0)
        {
            lines.Add("");
            lines.Add(SkippedSection(plan.Skipped));
        }
        return string.Join(Environment.NewLine, lines);
    }
}
