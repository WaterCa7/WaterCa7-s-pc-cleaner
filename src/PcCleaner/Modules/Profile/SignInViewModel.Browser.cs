using PcCleaner.Core;
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

// The browser code: for Google accounts and as a spare way (spec 3.2).
public sealed partial class SignInViewModel
{
    private CancellationTokenSource? _browserCts;
    private TaskCompletionSource? _limitDone;
    private string? _browserCode;
    private string? _browserUrl;
    private bool _browserEnded;

    public string? BrowserCode { get => _browserCode; private set => Set(ref _browserCode, value); }

    public string? BrowserUrl { get => _browserUrl; private set => Set(ref _browserUrl, value); }

    /// <summary>
    /// The code ran out or the request failed: the view offers «Новый код» (<see cref="RestartBrowserCommand"/>). The command itself
    /// is always available, because choosing the browser tab again does not ask for a new code.
    /// </summary>
    public bool BrowserEnded { get => _browserEnded; private set => Set(ref _browserEnded, value); }

    public RelayCommand ReopenBrowserCommand { get; private set; } = null!;

    public RelayCommand RestartBrowserCommand { get; private set; } = null!;

    /// <summary>The running browser sign-in (tests await it).</summary>
    public Task BrowserTask { get; private set; } = Task.CompletedTask;

    private void InitBrowser()
    {
        ReopenBrowserCommand = new RelayCommand(() =>
        {
            if (BrowserUrl is { } url)
                _openUrl(url);
        });
        RestartBrowserCommand = new RelayCommand(StartBrowser);
    }

    private void StartBrowser()
    {
        StopBrowser();
        Error = null;
        BrowserEnded = false;
        (BrowserCode, BrowserUrl) = (null, null); // the reopen button must not open the page of a code that is gone
        var cts = _browserCts = new CancellationTokenSource();
        BrowserTask = RunBrowserAsync(cts.Token);
    }

    /// <summary>
    /// The window is closing: stops the running browser poll and the wait for a release behind the list of computers, and drops
    /// what the window still holds (the passwords). Safe to call more than once.
    /// </summary>
    public void Stop()
    {
        StopBrowser();
        Password = Password2 = Code = "";
        _retry = null;
    }

    private void StopBrowser()
    {
        _browserCts?.Cancel();
        _browserCts = null;
        CancelDeviceLimitWait();
    }

    private void CancelDeviceLimitWait() => _limitDone?.TrySetCanceled();

    private async Task RunBrowserAsync(CancellationToken ct)
    {
        try
        {
            var code = await _account.StartBrowserLoginAsync(ct);
            ct.ThrowIfCancellationRequested(); // the answer came after the window closed or the tab was left: no code, no browser
            (BrowserCode, BrowserUrl) = (code.Code, code.Url);
            _openUrl(code.Url);
            var ok = await _account.WaitBrowserLoginAsync(code, limit => WaitForReleaseAsync(limit, ct), ct);
            if (ok)
            {
                Done();
            }
            else if (!ct.IsCancellationRequested)
            {
                Error = T._("Код устарел. Нажмите «Новый код» и войдите снова.");
                BrowserEnded = true;
            }
        }
        catch (OperationCanceledException)
        {
            // another tab, «Назад» or the window closed
        }
        catch (ApiException ex) when (!ct.IsCancellationRequested)
        {
            Error = SignInErrors.Text(ex.Error);
            BrowserEnded = true;
        }
        catch (ApiException)
        {
            // the sign-in was left while the request was running
        }
    }

    /// <summary>The poll stands while the list of computers is open; a release (or «Назад») ends the wait.</summary>
    private async Task WaitForReleaseAsync(DeviceLimitInfo limit, CancellationToken ct)
    {
        // The limit of a poll that was left must not take over the wait and the retry of the flow that runs now.
        ct.ThrowIfCancellationRequested();
        var done = _limitDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _retry = () =>
        {
            done.TrySetResult();
            return Task.CompletedTask;
        };
        Ui.Post(() =>
        {
            if (!ct.IsCancellationRequested) // «Назад» or another tab came first: the list must not reappear
                ShowLimit(limit, "");
        });
        using (ct.Register(() => done.TrySetCanceled()))
            await done.Task;
        Ui.Post(() =>
        {
            if (!ct.IsCancellationRequested)
                Step = SignInStep.Browser;
        });
    }
}
