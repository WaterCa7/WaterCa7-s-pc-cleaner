using System.ComponentModel;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core.Updates;

/// <summary>
/// The startup splash (a blurred window with the logo in the middle) while the program looks for a newer version:
/// a new version is downloaded and installed at once and the program restarts into it; «Пропустить» opens the program
/// without waiting (the download goes on, the install happens at the next start). Everything else — an up-to-date
/// version, no network, a slow check, a development build, automatic updates off — just closes the splash.
/// One run per instance; <see cref="RunAsync"/> never throws.
/// </summary>
public sealed class SplashViewModel : ObservableObject
{
    public const string CheckingText = "Проверяем обновления…";
    public const string InstallingText = "Устанавливаем обновление…";

    private readonly UpdateService _updates;
    private readonly Func<bool> _installNow;
    private readonly Action _shutdown;
    private readonly TaskCompletionSource _skipRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isVisible;
    private string _statusText = "";
    private bool _showProgress;
    private double _progress;
    private bool _canSkip;

    /// <param name="installNow">Installs the downloaded version and starts it; true = the new exe is running.</param>
    /// <param name="shutdown">Ends this process after <paramref name="installNow"/> returned true.</param>
    public SplashViewModel(UpdateService updates, Func<bool> installNow, Action shutdown)
    {
        _updates = updates;
        _installNow = installNow;
        _shutdown = shutdown;
        SkipCommand = new RelayCommand(Skip);
    }

    /// <summary>The usual setup: install with the arguments this process was started with.</summary>
    public SplashViewModel(UpdateService updates, IReadOnlyList<string> args, Action shutdown)
        : this(updates, () => updates.InstallNow(args), shutdown)
    {
    }

    /// <summary>Hidden until <see cref="RunAsync"/> starts, so screenshot mode and tests never see it.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        private set => Set(ref _isVisible, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>The progress bar: while a version is downloaded and while it is installed.</summary>
    public bool ShowProgress
    {
        get => _showProgress;
        private set => Set(ref _showProgress, value);
    }

    /// <summary>Download progress 0..1.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    /// <summary>«Пропустить» exists only while a version is downloading.</summary>
    public bool CanSkip
    {
        get => _canSkip;
        private set => Set(ref _canSkip, value);
    }

    public RelayCommand SkipCommand { get; }

    /// <summary>The splash never closes sooner than this after it appeared (except on «Пропустить»), so it does not flash.</summary>
    public TimeSpan MinimumVisible { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// How long the check may take to reach the download. Only the check is limited: a download that has started is
    /// never cut short by it.
    /// </summary>
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The waits behind <see cref="MinimumVisible"/> and <see cref="CheckTimeout"/>; tests replace it.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public async Task RunAsync(CancellationToken ct)
    {
        var leaveVisible = false;
        var reachedDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(UpdateService.State))
            {
                var state = _updates.State;
                if (state == UpdateState.Downloading)
                    downloaded.TrySetResult();
                if (state is UpdateState.Downloading or UpdateState.Ready)
                    reachedDownload.TrySetResult();
            }
            else if (e.PropertyName == nameof(UpdateService.Progress))
            {
                Progress = _updates.Progress;
            }
        }

        try
        {
            StatusText = T._(CheckingText);
            Progress = 0;
            ShowProgress = false;
            CanSkip = false;
            IsVisible = true;
            var minimumTime = Delay(MinimumVisible, ct);

            // Not installed or automatic updates off: CheckAsync returns at once and the state stays None.
            _updates.PropertyChanged += Watch;
            var check = _updates.CheckAsync(manual: false, ct);
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var timeout = Delay(CheckTimeout, timeoutCts.Token);
                await Task.WhenAny(check, reachedDownload.Task, timeout);
                timeoutCts.Cancel();
            }

            if (_updates.State == UpdateState.Downloading)
            {
                StatusText = T.F("Скачиваем версию {0}…", _updates.Manifest?.Version);
                Progress = _updates.Progress;
                ShowProgress = true;
                CanSkip = true;
                await Task.WhenAny(check, _skipRequested.Task);
                CanSkip = false;
                if (_skipRequested.Task.IsCompleted)
                    return; // the download goes on, the install waits for the next start
            }

            // Only a version downloaded just now is installed here. A staged one that never passed through the download
            // is one the install at startup has just failed on: another try would use up the second allowed failure
            // in the same launch. The update strip in the window offers «Перезапустить сейчас» for it.
            if (_updates.State == UpdateState.Ready && downloaded.Task.IsCompleted)
            {
                StatusText = T._(InstallingText);
                Progress = 1;
                ShowProgress = true;
                if (await Task.Run(_installNow))
                {
                    _shutdown();
                    // Only once the shutdown is under way: if it throws, the program stays open and the splash must close.
                    leaveVisible = true; // the process is going away: keep the blurred splash up until it does
                    return;
                }
            }

            await minimumTime;
        }
        catch (OperationCanceledException)
        {
            // the program is closing
        }
        catch (Exception ex)
        {
            Log.Error(T._("Экран загрузки"), ex);
        }
        finally
        {
            _updates.PropertyChanged -= Watch;
            if (!leaveVisible)
            {
                CanSkip = false;
                ShowProgress = false;
                IsVisible = false;
            }
        }
    }

    private void Skip()
    {
        if (CanSkip)
            _skipRequested.TrySetResult();
    }
}
