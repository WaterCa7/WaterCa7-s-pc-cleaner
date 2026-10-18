using System.Collections.ObjectModel;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Settings;

// Tab "Карантин": restore or permanently delete quarantined files.
public sealed partial class SettingsViewModel
{
    private ObservableCollection<QuarantineRow> _quarantine = new();
    private string _quarantineSummary = "";

    public ObservableCollection<QuarantineRow> QuarantineItems
    {
        get => _quarantine;
        private set
        {
            if (Set(ref _quarantine, value))
                OnPropertyChanged(nameof(QuarantineTileLabel));
        }
    }

    public string QuarantineSummary
    {
        get => _quarantineSummary;
        private set => Set(ref _quarantineSummary, value);
    }

    public string QuarantineDir => AppPaths.QuarantineDir;

    public AsyncRelayCommand RestoreQuarantineCommand { get; private set; } = null!;
    public RelayCommand DeleteQuarantineCommand { get; private set; } = null!;
    public RelayCommand ShowQuarantineOriginCommand { get; private set; } = null!;
    public RelayCommand SearchQuarantineWebCommand { get; private set; } = null!;
    public AsyncRelayCommand VirusTotalQuarantineCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshQuarantineCommand { get; private set; } = null!;

    private void InitQuarantineCommands()
    {
        RestoreQuarantineCommand = new AsyncRelayCommand(p => RestoreQuarantineAsync(ItemsFrom<QuarantineRow>(p)),
            p => !IsBusy && ItemsFrom<QuarantineRow>(p).Count > 0);
        DeleteQuarantineCommand = new RelayCommand(p => DeleteQuarantine(ItemsFrom<QuarantineRow>(p)),
            p => !IsBusy && ItemsFrom<QuarantineRow>(p).Count > 0);
        ShowQuarantineOriginCommand = new RelayCommand(p =>
        {
            if (ItemsFrom<QuarantineRow>(p).FirstOrDefault() is { } row)
                ProcessRunner.ShowInExplorer(System.IO.File.Exists(row.OriginalPath) ? row.OriginalPath : row.Folder);
        }, p => ItemsFrom<QuarantineRow>(p).Count == 1);
        SearchQuarantineWebCommand = new RelayCommand(p =>
        {
            if (ItemsFrom<QuarantineRow>(p).FirstOrDefault() is { } row)
                FileTools.SearchWeb(row.FileName);
        }, p => ItemsFrom<QuarantineRow>(p).Count == 1);
        VirusTotalQuarantineCommand = new AsyncRelayCommand(async p =>
        {
            // Only the SHA-256 of the quarantined copy is looked up; the file itself is not uploaded.
            if (ItemsFrom<QuarantineRow>(p).FirstOrDefault() is { StoredExists: true } row)
                await FileTools.OpenVirusTotalAsync(row.Item.StoredPath);
        }, p => ItemsFrom<QuarantineRow>(p) is { Count: 1 } list && list[0].StoredExists);
        RefreshQuarantineCommand = new AsyncRelayCommand(() => LoadQuarantineAsync(CancellationToken.None));
    }

    internal async Task LoadQuarantineAsync(CancellationToken ct)
    {
        var rows = await Task.Run(QuarantineActions.LoadRows, ct);
        QuarantineItems = new ObservableCollection<QuarantineRow>(rows);
        QuarantineSummary = rows.Count == 0
            ? T._("Карантин пуст")
            : $"{Format.Count(rows.Count, "файл", "файла", "файлов")} · {Format.Bytes(rows.Sum(r => r.SizeBytes))}";
    }

    private async Task RestoreQuarantineAsync(List<QuarantineRow> rows)
    {
        if (rows.Count == 0)
            return;

        var message =
            (rows.Count == 1 ? T._("Вернуть файл из карантина на прежнее место?") : T.F("Вернуть из карантина {0}?", Format.Count(rows.Count, "файл", "файла", "файлов"))) +
            "\n\n" + BulkText.BulletList(rows.Select(r => r.OriginalPath), 8) + "\n\n" + T._("Файл попал в карантин, потому что выглядел подозрительно. Восстанавливайте его, только если уверены, что он нужен и безопасен: после восстановления он снова сможет запускаться. При необходимости его можно будет снова отправить в карантин.");
        if (!Dialogs.Confirm(T._("Восстановление из карантина"), message))
            return;

        var results = new List<string>();
        var restored = 0;
        var processed = 0;
        // Moving a file back to another drive copies its data, so the moves run off the UI thread;
        // the follow-up questions are asked between them (continuations return to the UI thread).
        await RunBusyAsync(T._("Восстановление из карантина…"), async ct =>
        {
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                ReportProgress(processed * 100.0 / rows.Count, T.F("Восстановление: {0}", row.FileName));
                var item = row.Item;
                var result = await Task.Run(() => QuarantineActions.Restore(item, renameIfExists: false));
                if (result.Status == QuarantineRestoreStatus.Conflict)
                {
                    var alternative = QuarantineActions.AlternativePath(row.OriginalPath);
                    var ask =
                        T.F("По пути\n{0}\nуже есть другой файл.\n\nВосстановить файл из карантина рядом, под именем «{1}»? Существующий файл не будет изменён.", row.OriginalPath, System.IO.Path.GetFileName(alternative));
                    result = Dialogs.Confirm(T._("Файл уже существует"), ask)
                        ? await Task.Run(() => QuarantineActions.Restore(item, renameIfExists: true))
                        : result with { Message = T.F("{0}: пропущен — по исходному пути уже есть файл.", row.FileName) };
                }
                else if (result.Status == QuarantineRestoreStatus.MissingStoredFile)
                {
                    if (Dialogs.Confirm(T._("Файл не найден"),
                            T.F("Файл «{0}» отсутствует в папке карантина — возможно, его удалили вручную или антивирусом.\n\nУбрать эту запись из списка карантина?", row.FileName)))
                    {
                        var (removed, removeText) = QuarantineActions.Delete(item);
                        result = result with
                        {
                            Message = removed ? T.F("{0}: файла уже не было, запись убрана из списка.", row.FileName) : removeText,
                        };
                    }
                    else
                    {
                        result = result with { Message = T.F("{0}: файл в карантине не найден.", row.FileName) };
                    }
                }

                processed++;
                if (result.Success)
                    restored++;
                results.Add(result.Success || result.Message.StartsWith(row.FileName, StringComparison.Ordinal)
                    ? result.Message
                    : $"{row.FileName}: {result.Message}");
            }
        });

        await SafeRun(() => LoadQuarantineAsync(CancellationToken.None));
        if (results.Count == 0)
            return; // cancelled before anything was done
        var header = restored == rows.Count
            ? (restored == 1 ? T._("Файл восстановлен.") : T.F("Восстановлено файлов: {0}.", restored))
            : T.F("Восстановлено: {0} из {1}.", restored, rows.Count);
        if (processed < rows.Count)
            header += " " + T._("Остальные файлы не обработаны — операция была отменена.");
        Dialogs.Info(T._("Восстановление из карантина"), header + "\n\n" + BulkText.BulletList(results, 10));
    }

    private void DeleteQuarantine(List<QuarantineRow> rows)
    {
        if (rows.Count == 0)
            return;

        var message =
            (rows.Count == 1 ? T._("Удалить файл из карантина навсегда?") : T.F("Удалить навсегда {0} из карантина?", Format.Count(rows.Count, "файл", "файла", "файлов"))) +
            "\n\n" + BulkText.BulletList(rows.Select(r => r.OriginalPath), 8) + "\n\n" + T._("Файлы будут стёрты без помещения в корзину — восстановить их будет нельзя.");
        if (!Dialogs.Confirm(T._("Удаление из карантина"), message))
            return;

        var results = new List<string>();
        var deleted = 0;
        foreach (var row in rows)
        {
            var (ok, text) = QuarantineActions.Delete(row.Item);
            if (ok)
                deleted++;
            results.Add(text);
        }

        _ = SafeRun(() => LoadQuarantineAsync(CancellationToken.None));
        var header = deleted == rows.Count
            ? (deleted == 1 ? T._("Файл удалён навсегда.") : T.F("Удалено файлов: {0}.", deleted))
            : T.F("Удалено: {0} из {1}.", deleted, rows.Count);
        Dialogs.Info(T._("Удаление из карантина"), deleted == rows.Count ? header : header + "\n\n" + BulkText.BulletList(results, 10));
    }
}
