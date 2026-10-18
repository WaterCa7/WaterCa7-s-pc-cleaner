using System.Text;
using PcCleaner.Core;

namespace PcCleaner.Modules.Processes;

// "Завершить отмеченные": the plan taken from the checks, the one question and the kill. The checkbox column, the header box
// and the single-process actions are in ProcessesViewModel.cs.
public sealed partial class ProcessesViewModel
{
    /// <summary>What "Завершить отмеченные" is about to do, taken from the checks right before the question.</summary>
    private sealed class CheckedKillPlan
    {
        /// <summary>The checked processes that may be ended, each once, in the order of the question: the hidden ones first.</summary>
        public List<ProcessRow> Victims { get; } = new();

        /// <summary>
        /// Checked processes the list does not show right now (search, filters, or inside a collapsed program that is only
        /// partly checked), one line per program with all their PIDs. They are ended too, so the question names them first.
        /// </summary>
        public List<string> HiddenLines { get; } = new();

        /// <summary>What the list shows as checked, in list order: a fully checked program is one line.</summary>
        public List<string> VisibleLines { get; } = new();

        /// <summary>Checked processes that can no longer be ended (protection changed since the last list update).</summary>
        public List<ProcessRow> Skipped { get; } = new();
    }

    private async Task KillCheckedAsync()
    {
        var plan = PlanCheckedKill();
        // A disabled box is never checked: processes that became protected leave the checks right away.
        Checked.SetAll(plan.Skipped.Select(r => r.Key), false);
        if (plan.Victims.Count == 0)
        {
            if (plan.Skipped.Count > 0)
                ShowInfo("Процесс защищён", "Отмеченные процессы теперь защищены, завершать их нельзя:\n" +
                                            BulkText.BulletList(plan.Skipped.Select(ProcessLine)));
            return;
        }
        var title = plan.Victims.Count > 1 ? "Завершить отмеченные процессы?" : "Завершить отмеченный процесс?";
        if (!Confirm(title, BuildCheckedKillMessage(plan)))
            return;

        var targets = plan.Victims.Select(ToTarget).ToList();
        var kill = KillOverride ?? _service.Kill;
        var report = await Task.Run(() => kill(targets));

        // Ended (or already gone) processes leave the checks; those that could not be ended stay checked.
        Checked.SetAll(report.Killed.Concat(report.AlreadyExited).Select(t => new ProcessKey(t.Pid, t.CreateTime)), false);

        // BuildKillReport names the item only when there was a single process, which is then Victims[0].
        if (report.Done == 0 && report.AccessDenied && !IsElevated())
            RequireElevation("Завершение отмеченных процессов");
        else if (report.Failed.Count > 0)
            Dialogs.Error("Не всё получилось", BuildKillReport(plan.Victims[0], report));
        else
            ShowInfo("Готово", BuildKillReport(plan.Victims[0], report));

        await SafeRefreshAsync();
    }

    private CheckedKillPlan PlanCheckedKill()
    {
        var plan = new CheckedKillPlan();
        lock (_stateLock)
        {
            var endable = new List<ProcessRow>();
            foreach (var key in Checked.Keys)
            {
                if (!_rows.TryGetValue(key, out var row))
                    continue; // exited: the next list update drops the check
                (row.CanCheck ? endable : plan.Skipped).Add(row);
            }
            var victims = endable.Select(r => r.Key).ToHashSet();

            // What the list shows as checked: a fully checked program, or a checked process row.
            var shown = new List<ProcessRow>();
            var shownKeys = new HashSet<ProcessKey>();
            foreach (var item in Items)
            {
                IEnumerable<ProcessRow> rows = item switch
                {
                    ProcessGroup { IsChecked: true } program => program.Instances,
                    ProcessRow r => new[] { r },
                    _ => Array.Empty<ProcessRow>(),
                };
                var listed = rows.Where(r => victims.Contains(r.Key) && shownKeys.Add(r.Key)).ToList();
                if (listed.Count > 0)
                {
                    plan.VisibleLines.Add(CheckedLine(item, listed, allPids: false));
                    shown.AddRange(listed);
                }
            }

            // The rest, one line per program by name: the question names them first, and they are ended first.
            var hidden = endable.Where(r => !shownKeys.Contains(r.Key))
                .GroupBy(r => (ProcessItem?)r.Group ?? r)
                .Select(g => (Rows: g.OrderBy(r => r.Pid).ToList(), Item: g.Key))
                .Select(g => (Line: CheckedLine(g.Item, g.Rows, allPids: true), g.Rows))
                .OrderBy(g => g.Line, StringComparer.CurrentCultureIgnoreCase);
            foreach (var (line, rows) in hidden)
            {
                plan.HiddenLines.Add(line);
                plan.Victims.AddRange(rows);
            }
            plan.Victims.AddRange(shown);
        }
        return plan;
    }

    /// <summary>
    /// "notepad.exe, PID 4242" or "Google Chrome (chrome.exe) — 3 процесса (PID 1, 2, 3)"; like the single kill's question,
    /// a long PID list is cut after 15 unless <paramref name="allPids"/>.
    /// </summary>
    private static string CheckedLine(ProcessItem item, List<ProcessRow> rows, bool allPids)
    {
        var name = string.Equals(item.FriendlyName, item.ExeName, StringComparison.OrdinalIgnoreCase)
            ? item.ExeName
            : $"{item.FriendlyName} ({item.ExeName})";
        if (rows.Count == 1)
            return $"{name}, PID {rows[0].Pid}";
        var cut = !allPids && rows.Count > 15;
        var pids = string.Join(", ", rows.Take(cut ? 15 : rows.Count).Select(r => r.Pid)) + (cut ? ", …" : "");
        return $"{name} — {Format.Count(rows.Count, "процесс", "процесса", "процессов")} (PID {pids})";
    }

    private static string ProcessLine(ProcessRow row) => CheckedLine(row, new List<ProcessRow> { row }, allPids: true);

    private static string BuildCheckedKillMessage(CheckedKillPlan plan)
    {
        var sb = new StringBuilder();
        var total = plan.Victims.Count == 1
            ? "Будет принудительно завершён 1 процесс"
            : $"Будут принудительно завершены {Format.Count(plan.Victims.Count, "процесс", "процесса", "процессов")}";
        sb.AppendLine(BulkText.TargetList(total, plan.HiddenLines, plan.VisibleLines));
        if (plan.Skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Эти отмеченные процессы теперь защищены и затронуты не будут:");
            sb.AppendLine(BulkText.BulletList(plan.Skipped.Select(ProcessLine)));
        }
        sb.AppendLine();
        sb.Append("Несохранённые данные в этих программах будут потеряны. Если программа отвечает, лучше закройте её обычным способом.");
        if (plan.Victims.Any(v => v.HasKillWarning))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("Внимание: ").Append(ProcessGuard.ExplorerText);
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.Append("Продолжить?");
        return sb.ToString();
    }
}
