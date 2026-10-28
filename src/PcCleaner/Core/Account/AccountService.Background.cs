using PcCleaner.Core.Localization;

namespace PcCleaner.Core.Account;

// The waiting parts of the account: the browser code, the ticket refresh, the payment.
public sealed partial class AccountService
{
    /// <summary>The contract: poll a browser code no more often than every 5 s (the server refuses faster than 3 s).</summary>
    public static readonly TimeSpan BrowserPollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxBrowserPollInterval = TimeSpan.FromSeconds(60);

    /// <summary>The contract: poll a payment no more often than every 5 s, and only for a while (the program's own limit).</summary>
    public static readonly TimeSpan PaymentPollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PaymentWait = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan RefreshAfterUnfinishedPayment = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan DailyRefresh = TimeSpan.FromHours(24);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    // ----- sign-in through the browser -----

    public Task<BrowserCode> StartBrowserLoginAsync(CancellationToken ct) => _api.StartBrowserCodeAsync(Device, ct);

    /// <summary>
    /// Polls the code until the site confirms it (true, signed in) or it expires (false). While <paramref name="onDeviceLimit"/>
    /// runs (the unbind dialog) nothing is polled: every DEVICE_LIMIT answer replaces the permit the dialog holds.
    /// </summary>
    public async Task<bool> WaitBrowserLoginAsync(BrowserCode code, Func<DeviceLimitInfo, Task> onDeviceLimit, CancellationToken ct)
    {
        var deadline = _now() + TimeSpan.FromSeconds(code.ExpiresIn);
        var interval = BrowserPollInterval;
        while (_now() < deadline)
        {
            await _delay(interval, ct);
            try
            {
                if (await _api.PollBrowserCodeAsync(code.Code, ct) is { } result)
                {
                    SignIn(result, T._("Аккаунт: вход через браузер"));
                    return true;
                }
            }
            catch (ApiException ex) when (ex.Error.Code == ApiError.RateLimited)
            {
                interval = TimeSpan.FromTicks(Math.Min(interval.Ticks * 2, MaxBrowserPollInterval.Ticks));
            }
            catch (ApiException ex) when (ex.Error.Code == ApiError.DeviceLimit && ex.Error.AsDeviceLimit() is { } limit)
            {
                await onDeviceLimit(limit);
            }
            catch (ApiException ex) when (ex.Error.Code == ApiError.NotFound)
            {
                return false;
            }
            catch (ApiException ex) when (ex.Error.Code is ApiError.Network or ApiError.ServerError)
            {
                // keep polling until the code expires
            }
        }
        return false;
    }

    // ----- the ticket -----

    /// <summary>
    /// Asks for a fresh ticket. False when the server could not be reached or the ticket does not fit — the ticket in hand stays
    /// (a server failure never takes Pro away). TOKEN_REVOKED signs out.
    /// </summary>
    public async Task<bool> RefreshTicketAsync(CancellationToken ct)
    {
        // The token is read once: a TOKEN_REVOKED answer to an old sign-in must not end a newer one.
        if (Token() is not { } token)
            return false;
        try
        {
            var ticket = await WithToken(token, t => _api.TicketAsync(t, ct));
            return AcceptTicket(ticket);
        }
        catch (ApiException ex)
        {
            if (ex.Error.Code != ApiError.TokenRevoked)
                Log.Info(T.F("Аккаунт: билет лицензии не обновлён ({0}) — действует прежний", ex.Error.Code));
            return false;
        }
    }

    /// <summary>A day after a good refresh, a minute after the subscription ends if that comes sooner, 15 minutes after a failure.</summary>
    public TimeSpan NextRefreshDelay(bool lastSucceeded)
    {
        if (!IsSignedIn)
            return DailyRefresh;
        if (!lastSucceeded)
            return RetryAfterFailure;
        var delay = DailyRefresh;
        if (Status.ExpiresAt is { } end && end > _now())
        {
            var untilEnd = end - _now() + TimeSpan.FromMinutes(1);
            if (untilEnd < delay)
                delay = untilEnd;
        }
        return delay < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delay;
    }

    /// <summary>The background refresh of the whole run: at once (start), then by <see cref="NextRefreshDelay"/>.</summary>
    public async Task RunRefreshLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var ok = false;
            try
            {
                ok = await RefreshTicketAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Anything but a failed call (RefreshTicketAsync handles those): the loop must outlive it.
                Log.Error(T._("Аккаунт: ошибка фоновой проверки лицензии"), ex);
            }
            await _delay(NextRefreshDelay(ok), ct);
        }
    }

    /// <summary>One more refresh later in the background (a payment that was still being processed).</summary>
    public void RefreshSoon(TimeSpan after) => _ = Task.Run(async () =>
    {
        try
        {
            await _delay(after, CancellationToken.None);
            await RefreshTicketAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(T._("Аккаунт: ошибка фоновой проверки лицензии"), ex);
        }
    });

    // ----- purchase -----

    /// <summary>The current token; not signed in → TOKEN_REVOKED at once (the same answer <c>WithToken</c> gives).</summary>
    private string RequireToken() => Token() ?? throw new ApiException(new ApiError(ApiError.TokenRevoked, "", 401, null));

    public Task<PriceList> PricesAsync(CancellationToken ct) => _api.PricesAsync(Token(), ct);

    public Task<Quote> QuoteAsync(QuoteRequest request, CancellationToken ct) => WithToken(t => _api.QuoteAsync(t, request, ct));

    /// <summary>Creates the payment and its one-time page link; never call it again while that payment may still be paid.</summary>
    public async Task<PaymentLink> StartPaymentAsync(QuoteRequest request, CancellationToken ct)
    {
        var link = await WithToken(t => _api.PayStartAsync(t, request, ct));
        Log.Info(T.F("Покупка: начата оплата №{0}", link.PaymentId));
        return link;
    }

    /// <summary>A new one-time link to the same payment (the old one is used up or expired).</summary>
    public Task<PaymentLink> PaymentLinkAsync(long paymentId, CancellationToken ct) => WithToken(t => _api.PayLinkAsync(t, paymentId, ct));

    /// <summary>The state of the payment; a paid one's ticket is taken at once.</summary>
    public Task<PaymentStatus> PaymentStatusAsync(long paymentId, CancellationToken ct)
        => PaymentStatusAsync(RequireToken(), paymentId, ct);

    private async Task<PaymentStatus> PaymentStatusAsync(string token, long paymentId, CancellationToken ct)
    {
        var status = await WithToken(token, t => _api.PayStatusAsync(t, paymentId, ct));
        if (status is { Status: "paid", License: { } ticket } && AcceptTicket(ticket))
            Log.Info(T.F("Покупка: оплата №{0} прошла, Pro включён", paymentId));
        return status;
    }

    public Task<PaymentStatus> WaitPaymentAsync(long paymentId, CancellationToken ct) => WaitPaymentAsync(paymentId, null, ct);

    /// <summary>
    /// Polls the payment every 5 s for <see cref="PaymentWait"/>. Paid → at once. Failed is not final (a late notice can still make it
    /// paid): it is reported through <paramref name="progress"/> and the poll goes on. Still unpaid at the end → the last state and a
    /// background refresh in 5 minutes. Network failures just wait for the next poll.
    /// </summary>
    public async Task<PaymentStatus> WaitPaymentAsync(long paymentId, IProgress<PaymentStatus>? progress, CancellationToken ct)
    {
        // One sign-in for the whole wait: the payment belongs to the account that started it.
        var token = RequireToken();
        var deadline = _now() + PaymentWait;
        var last = new PaymentStatus("pending", null);
        while (_now() < deadline)
        {
            await _delay(PaymentPollInterval, ct);
            try
            {
                last = await PaymentStatusAsync(token, paymentId, ct);
                if (last.Status == "paid")
                    return last;
                progress?.Report(last);
            }
            catch (ApiException ex) when (ex.Error.Code is ApiError.Network or ApiError.ServerError or ApiError.RateLimited)
            {
                // the next poll
            }
        }
        Log.Info(T.F("Покупка: оплата №{0} ещё обрабатывается — Pro включится сам", paymentId));
        RefreshSoon(RefreshAfterUnfinishedPayment);
        return last;
    }
}
