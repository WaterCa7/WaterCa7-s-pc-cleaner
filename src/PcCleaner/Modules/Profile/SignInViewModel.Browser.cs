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

    public string? BrowserCode { get => _browserCode; private set => Set(ref _browserCode, value); }

    public string? BrowserUrl { get => _browserUrl; private set => Set(ref _browserUrl, value); }

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
        BrowserCode = null;
        var cts = _browserCts = new CancellationTokenSource();
        BrowserTask = RunBrowserAsync(cts.Token);
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
            (BrowserCode, BrowserUrl) = (code.Code, code.Url);
            _openUrl(code.Url);
            var ok = await _account.WaitBrowserLoginAsync(code, limit => WaitForReleaseAsync(limit, ct), ct);
            if (ok)
                Done();
            else
                Error = T._("Код устарел. Нажмите «Новый код» и войдите снова.");
        }
        catch (OperationCanceledException)
        {
            // another tab, «Назад» or the window closed
        }
        catch (ApiException ex)
        {
            Error = SignInErrors.Text(ex.Error);
        }
    }

    /// <summary>The poll stands while the list of computers is open; a release (or «Назад») ends the wait.</summary>
    private async Task WaitForReleaseAsync(DeviceLimitInfo limit, CancellationToken ct)
    {
        var done = _limitDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _retry = () =>
        {
            done.TrySetResult();
            return Task.CompletedTask;
        };
        Ui.Post(() => ShowLimit(limit, ""));
        using (ct.Register(() => done.TrySetCanceled()))
            await done.Task;
        Ui.Post(() => Step = SignInStep.Browser);
    }
}
