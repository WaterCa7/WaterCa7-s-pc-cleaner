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

    /// <summary>A "paid" answer whose ticket is missing or does not fit: the ticket is asked for again this soon, in the background.</summary>
    public static readonly TimeSpan RefreshAfterPaidWithoutTicket = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan DailyRefresh = TimeSpan.FromHours(24);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    // Completed (and replaced) by every change of the sign-in or the ticket: wakes the refresh loop's sleep.
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ----- sign-in through the browser -----

    public async Task<BrowserCode> StartBrowserLoginAsync(CancellationToken ct) => await _api.StartBrowserCodeAsync(Device, ct);

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
        // The token is read once: a TOKEN_REVOKED answer or a ticket for an old sign-in must not touch a newer one.
        if (Token() is not { } token)
            return false;
        try
        {
            var ticket = await WithToken(token, t => _api.TicketAsync(t, ct));
            return AcceptTicket(ticket, token);
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

    /// <summary>
    /// The background refresh of the whole run: at once (start), then by <see cref="NextRefreshDelay"/>. <see cref="Changed"/> fires
    /// after every pass and when the ticket in hand runs out by itself (expires_at or valid_until reached), so the screens follow the
    /// clock too. A sign-in, a sign-out or a new ticket wakes the sleep, and it is computed again.
    /// </summary>
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
            TellScreens();
            await SleepUntilRefreshAsync(ok, ct);
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

    /// <summary>
    /// Sleeps until the next refresh, waking at every tier boundary of the ticket in hand on the way (the screens are told then).
    /// A change of the sign-in or the ticket starts the count again from the new state.
    /// </summary>
    private async Task SleepUntilRefreshAsync(bool lastSucceeded, CancellationToken ct)
    {
        var wake = WakeSignal();
        var now = _now();
        var refreshAt = now + NextRefreshDelay(lastSucceeded);
        while (true)
        {
            // A late wake (the computer slept across the end, the clock jumped) can leave the refresh time behind: refresh at once.
            if (now >= refreshAt)
                return;
            var target = NextTierBoundary(now) is { } boundary && boundary < refreshAt ? boundary : refreshAt;
            // Never negative (Task.Delay throws for that); the checks above keep it positive, so a zero cannot spin.
            var sleep = target - now;
            if (sleep < TimeSpan.Zero)
                sleep = TimeSpan.Zero;
            if (!await SleepAsync(sleep, wake, ct))
            {
                // A sign-in, a sign-out or a new ticket: the state is fresh, so the sleep is computed from it.
                wake = WakeSignal();
                now = _now();
                refreshAt = now + NextRefreshDelay(lastSucceeded: true);
                continue;
            }
            if (target == refreshAt)
                return;
            TellScreens(); // the tier ran out by itself
            // The delay may end a little early or late by the clock: move past this boundary in any case.
            var clock = _now();
            now = clock > target ? clock : target;
        }
    }

    /// <summary>True when the time is up; false when woken first (the abandoned delay is cancelled, so it holds no timer).</summary>
    private async Task<bool> SleepAsync(TimeSpan duration, Task wake, CancellationToken ct)
    {
        using var sleep = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = _delay(duration, sleep.Token);
        if (await Task.WhenAny(delay, wake) == delay)
        {
            await delay; // the caller's cancellation goes on from here
            return true;
        }
        sleep.Cancel();
        ct.ThrowIfCancellationRequested();
        return false;
    }

    /// <summary>The nearest moment after <paramref name="now"/> at which the ticket in hand stops giving its tier: expires_at or valid_until.</summary>
    private DateTimeOffset? NextTierBoundary(DateTimeOffset now)
    {
        LicenseTicketData? ticket;
        lock (_lock)
            ticket = _ticket;
        if (ticket is null)
            return null;
        DateTimeOffset? next = null;
        foreach (var end in new[] { ticket.ExpiresAt, ticket.ValidUntil })
        {
            if (end is { } e && e > now && (next is null || e < next))
                next = e;
        }
        return next;
    }

    private Task WakeSignal()
    {
        lock (_lock)
            return _wake.Task;
    }

    private void Wake()
    {
        TaskCompletionSource woken;
        lock (_lock)
        {
            woken = _wake;
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        woken.TrySetResult();
    }

    /// <summary>Changed from the loop: a handler's failure is logged, it does not stop the loop.</summary>
    private void TellScreens()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(T._("Аккаунт: ошибка фоновой проверки лицензии"), ex);
        }
    }

    // ----- purchase -----

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

    /// <summary>
    /// The state of the payment; a paid one's ticket is taken at once. A "paid" whose ticket is missing or not taken asks for the ticket
    /// again in <see cref="RefreshAfterPaidWithoutTicket"/>. Screens decide "Pro is on" by <see cref="Tier"/> (and <see cref="Changed"/>),
    /// not by the status "paid".
    /// </summary>
    public async Task<PaymentStatus> PaymentStatusAsync(long paymentId, CancellationToken ct)
        => await PaymentStatusAsync(RequireToken(), paymentId, ct);

    private async Task<PaymentStatus> PaymentStatusAsync(string token, long paymentId, CancellationToken ct)
    {
        var status = await WithToken(token, t => _api.PayStatusAsync(t, paymentId, ct));
        if (status.Status != "paid")
            return status;
        if (status.License is { } ticket && AcceptTicket(ticket, token))
        {
            Log.Info(T.F("Покупка: оплата №{0} прошла, Pro включён", paymentId));
        }
        else
        {
            Log.Info(T.F("Покупка: оплата №{0} прошла, но билет не принят — новая проверка через минуту", paymentId));
            RefreshSoon(RefreshAfterPaidWithoutTicket);
        }
        return status;
    }

    public Task<PaymentStatus> WaitPaymentAsync(long paymentId, CancellationToken ct) => WaitPaymentAsync(paymentId, null, ct);

    /// <summary>
    /// Polls the payment every 5 s for <see cref="PaymentWait"/>. Paid → at once (its ticket as in <see cref="PaymentStatusAsync(long, CancellationToken)"/>:
    /// the screens go by <see cref="Tier"/>). Failed is not final (a late notice can still make it paid): it is reported through
    /// <paramref name="progress"/> and the poll goes on. Still unpaid at the end, or the wait cancelled → a background refresh in
    /// 5 minutes (the cancellation goes on). Network failures just wait for the next poll.
    /// </summary>
    public async Task<PaymentStatus> WaitPaymentAsync(long paymentId, IProgress<PaymentStatus>? progress, CancellationToken ct)
    {
        // One sign-in for the whole wait: the payment belongs to the account that started it.
        var token = RequireToken();
        var deadline = _now() + PaymentWait;
        var last = new PaymentStatus("pending", null);
        try
        {
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
        }
        catch (OperationCanceledException)
        {
            Log.Info(T.F("Покупка: ожидание оплаты №{0} прервано — Pro включится сам", paymentId));
            RefreshSoon(RefreshAfterUnfinishedPayment);
            throw;
        }
        Log.Info(T.F("Покупка: оплата №{0} ещё обрабатывается — Pro включится сам", paymentId));
        RefreshSoon(RefreshAfterUnfinishedPayment);
        return last;
    }
}
