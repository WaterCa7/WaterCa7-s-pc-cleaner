using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Settings;

// Tab "Журнал": live, filterable view of Log.Entries (newest first).
public sealed partial class SettingsViewModel
{
    private LogFilterOption _selectedLogFilter;
    private string _logSearch = "";
    private string[] _logTerms = Array.Empty<string>();
    private ObservableCollection<LogEntry> _filteredLog = new();
    private bool _logAttached;
    private string _logSummary = "";
    private string _logCounters = "";

    public IReadOnlyList<LogFilterOption> LogFilterOptions => LogFilter.Options;

    public LogFilterOption SelectedLogFilter
    {
        get => _selectedLogFilter;
        set
        {
            if (value is not null && Set(ref _selectedLogFilter, value))
                RebuildLog();
        }
    }

    public string LogSearch
    {
        get => _logSearch;
        set
        {
            if (Set(ref _logSearch, value ?? ""))
            {
                _logTerms = LogFilter.SplitTerms(_logSearch);
                RebuildLog();
            }
        }
    }

    /// <summary>Entries matching the filter and search, newest first. Updated live while the page is open.</summary>
    public ObservableCollection<LogEntry> FilteredLog
    {
        get => _filteredLog;
        private set => Set(ref _filteredLog, value);
    }

    public string LogSummary
    {
        get => _logSummary;
        private set => Set(ref _logSummary, value);
    }

    public string LogCounters
    {
        get => _logCounters;
        private set => Set(ref _logCounters, value);
    }

    public bool IsLogFiltered => SelectedLogFilter.Value != LogLevelFilter.All || _logTerms.Length > 0;

    public string LogEmptyText => IsLogFiltered
        ? T._("Нет записей, подходящих под фильтр или поиск.")
        : T._("Журнал пока пуст — здесь появятся все действия программы.");

    public RelayCommand CopyLogCommand { get; private set; } = null!;
    public RelayCommand OpenLogsFolderCommand { get; private set; } = null!;
    public RelayCommand ClearLogSearchCommand { get; private set; } = null!;

    private void InitLogCommands()
    {
        CopyLogCommand = new RelayCommand(p => CopyLog(ItemsFrom<LogEntry>(p)));
        OpenLogsFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.LogsDir));
        ClearLogSearchCommand = new RelayCommand(() =>
        {
            LogSearch = "";
            SelectedLogFilter = LogFilter.Options[0];
        }, () => IsLogFiltered);
    }

    private void AttachLog()
    {
        if (_logAttached)
            return;
        _logSource.CollectionChanged += OnLogSourceChanged;
        _logAttached = true;
        RebuildLog();
    }

    private void DetachLog()
    {
        if (!_logAttached)
            return;
        _logSource.CollectionChanged -= OnLogSourceChanged;
        _logAttached = false;
    }

    /// <summary>Test seam: start following the log source without navigation.</summary>
    internal void AttachLogForTests() => AttachLog();

    internal void RebuildLog()
    {
        FilteredLog = new ObservableCollection<LogEntry>(LogFilter.Apply(_logSource.ToList(), SelectedLogFilter.Value, LogSearch));
        UpdateLogSummary();
    }

    // Log adds entries on the UI thread (Ui.Post), so this handler runs there too.
    private void OnLogSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        try
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                    foreach (var entry in e.NewItems.OfType<LogEntry>())
                    {
                        if (LogFilter.Matches(entry, SelectedLogFilter.Value, _logTerms))
                            FilteredLog.Insert(0, entry);
                    }
                    break;
                case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                    foreach (var entry in e.OldItems.OfType<LogEntry>())
                    {
                        // Oldest entries are trimmed from the source; they sit at the end of our list.
                        for (var i = FilteredLog.Count - 1; i >= 0; i--)
                        {
                            if (ReferenceEquals(FilteredLog[i], entry))
                            {
                                FilteredLog.RemoveAt(i);
                                break;
                            }
                        }
                    }
                    break;
                default:
                    RebuildLog();
                    return;
            }
            UpdateLogSummary();
        }
        catch (InvalidOperationException)
        {
            // Collection changed from another thread (only possible in tests) — rebuild from scratch.
            RebuildLog();
        }
    }

    private void UpdateLogSummary()
    {
        var all = LogFilter.Count(_logSource.ToList());
        LogCounters = T.F("Действий: {0} · предупреждений: {1} · ошибок: {2}", all.Actions, all.Warnings, all.Errors);
        LogSummary = IsLogFiltered
            ? T.F("Показано {0} из {1}", Format.Number(FilteredLog.Count), Format.Count(all.Total, "записи", "записей", "записей"))
            : T.F("Всего {0} за этот запуск", Format.Count(all.Total, "запись", "записи", "записей"));
        LogTotal = all.Total;
        OnPropertyChanged(nameof(IsLogFiltered));
        OnPropertyChanged(nameof(LogEmptyText));
        OnPropertyChanged(nameof(LogTileLabel));
    }

    private int _logTotal;

    /// <summary>All entries of this run (for the "Данные программы" tile).</summary>
    public int LogTotal
    {
        get => _logTotal;
        private set => Set(ref _logTotal, value);
    }

    private void CopyLog(List<LogEntry> selected)
    {
        var entries = selected.Count > 0 ? selected : FilteredLog.ToList();
        if (entries.Count == 0)
        {
            Dialogs.Info(T._("Журнал"), T._("Нечего копировать: список записей пуст."));
            return;
        }

        var text = LogFilter.ToClipboardText(entries);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                Log.Info(T.F("Журнал: скопировано в буфер обмена — {0}", Format.Count(entries.Count, "запись", "записи", "записей")));
                return;
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                if (attempt == 4)
                {
                    Log.Warn(T.F("Не удалось скопировать журнал в буфер обмена: {0}", ex.Message));
                    Dialogs.Info(T._("Журнал"), T._("Буфер обмена сейчас занят другой программой. Попробуйте ещё раз через пару секунд."));
                    return;
                }
                Thread.Sleep(60);
            }
        }
    }

    private static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(T.F("Не удалось создать папку {0}: {1}", dir, ex.Message));
        }
        ProcessRunner.ShowInExplorer(dir);
    }
}
