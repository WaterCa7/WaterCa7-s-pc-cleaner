namespace PcCleaner.Core;

/// <summary>
/// Base class for every page shown in the sidebar.
/// </summary>
public abstract class PageViewModel : ObservableObject
{
    private bool _isBusy;
    private string _busyText = "";
    private double _progress = -1;
    private CancellationTokenSource? _cts;

    protected PageViewModel()
    {
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy && _cts is not null);
    }

    /// <summary>Short name shown in the sidebar and page header.</summary>
    public abstract string Title { get; }

    /// <summary>One-line description shown under the page header.</summary>
    public abstract string Subtitle { get; }

    /// <summary>Segoe MDL2 Assets glyph, e.g. "".</summary>
    public abstract string IconGlyph { get; }

    /// <summary>Stable key used by --page and navigation (defaults to the type name without "ViewModel").</summary>
    public virtual string Key => GetType().Name.Replace("ViewModel", "");

    public bool IsBusy
    {
        get => _isBusy;
        protected set
        {
            if (Set(ref _isBusy, value))
                RelayCommand.Refresh();
        }
    }

    public string BusyText
    {
        get => _busyText;
        protected set => Set(ref _busyText, value);
    }

    /// <summary>0..100, or a negative value for indeterminate progress.</summary>
    public double Progress
    {
        get => _progress;
        protected set
        {
            if (Set(ref _progress, value))
                OnPropertyChanged(nameof(IsProgressIndeterminate));
        }
    }

    public bool IsProgressIndeterminate => Progress < 0;

    public RelayCommand CancelCommand { get; }

    /// <summary>Called every time the page becomes visible. The first call should load data.</summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    /// <summary>Called when the user leaves the page (stop timers / live refresh here).</summary>
    public virtual void OnNavigatedFrom() { }

    /// <summary>
    /// Read-only scan used by screenshot mode and the dashboard smart-scan.
    /// MUST NOT change anything on the system.
    /// </summary>
    public virtual Task PreviewScanAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Runs work with the busy overlay, cancellation and progress handling.</summary>
    protected async Task RunBusyAsync(string text, Func<CancellationToken, Task> work)
    {
        if (IsBusy)
            return;
        _cts = new CancellationTokenSource();
        BusyText = text;
        Progress = -1;
        IsBusy = true;
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Info($"{Title}: операция отменена");
        }
        catch (Exception ex)
        {
            Log.Error($"{Title}: {text}", ex);
            Dialogs.Error(Title, ErrorText.Sentence(ex));
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            Progress = -1;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Reports progress from any thread.</summary>
    protected void ReportProgress(double percent, string? text = null)
    {
        Ui.Post(() =>
        {
            Progress = percent;
            if (text is not null)
                BusyText = text;
        });
    }
}
