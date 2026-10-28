using System.IO;
using System.Security.Cryptography;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core.Account;

/// <summary>
/// The account on this computer: sign-in (password, sign-up code, password reset, browser code), the ticket and the tier, the
/// computers of the account, the promo code, «Написать нам», signing out. Thread-safe; <see cref="Changed"/> fires after every
/// change of the sign-in or the ticket, possibly on a background thread. A failed call ends its task with <see cref="ApiException"/>
/// (never throws before the task is returned); TOKEN_REVOKED signs out first (<see cref="SessionEnded"/>). A store that cannot
/// write keeps the state in memory for this run. Tokens, passwords, tickets and hw_hash never go to the log.
/// </summary>
public sealed partial class AccountService
{
    private readonly ApiClient _api;
    private readonly IAccountStore _store;
    private readonly IHwHash _hw;
    private readonly string _platform;
    private readonly string _appVersion;
    private readonly IReadOnlyList<string> _keys;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _lock = new();
    private AccountData _data;
    private LicenseTicketData? _ticket;
    private bool _sessionEnded;
    private int _hwFailureLogged;

    public AccountService(ApiClient api, IAccountStore store, IHwHash hw, string platform, string deviceName, string appVersion,
        IReadOnlyList<string> licenseKeys, Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _api = api;
        _store = store;
        _hw = hw;
        _platform = platform;
        DeviceName = DeviceFields.CleanName(deviceName);
        _appVersion = appVersion;
        _keys = licenseKeys;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _data = store.Load();
        _ticket = LicenseTicket.Verify(_data.Ticket, _data.Signature, _keys);
    }

    public event Action? Changed;

    public string DeviceName { get; }

    /// <summary>The fields every sign-in sends; the first read computes hw_hash (WMI on Windows: do it off the UI thread).</summary>
    public DeviceFields Device => new(_hw.Value, _platform, DeviceName, _appVersion);

    public bool IsSignedIn
    {
        get
        {
            lock (_lock)
                return _data.Token is not null;
        }
    }

    public string? Email
    {
        get
        {
            lock (_lock)
                return _data.Email;
        }
    }

    /// <summary>The install number of the update statistics (kept across sign-outs); null until <see cref="EnsureInstallId"/>.</summary>
    public string? InstallId
    {
        get
        {
            lock (_lock)
                return _data.InstallId;
        }
    }

    /// <summary>TOKEN_REVOKED was seen in this run: Профиль offers to sign in again (once, not as a pop-up).</summary>
    public bool SessionEnded
    {
        get
        {
            lock (_lock)
                return _sessionEnded;
        }
    }

    /// <summary>
    /// The tier now and why. The first read may query the hardware (hw_hash: WMI on Windows, up to seconds): call
    /// <see cref="WarmUpAsync"/> off the UI thread at startup. A computer id that cannot be read gives Free (WrongDevice), never an exception.
    /// </summary>
    public LicenseStatus Status
    {
        get
        {
            var hw = HwValue();
            AccountData data;
            LicenseTicketData? ticket;
            lock (_lock)
                (data, ticket) = (_data, _ticket);
            return LicenseState.Evaluate(ticket, data.Token is not null, hw ?? "", data.DeviceId, data.AccountId, _now());
        }
    }

    /// <summary>The tier of <see cref="Status"/> (the first read may query the hardware: call <see cref="WarmUpAsync"/> at startup).</summary>
    public Tier Tier => Status.Tier;

    /// <summary>Whether the feature is open at the current tier (the first read may query the hardware: see <see cref="WarmUpAsync"/>).</summary>
    public bool IsAvailable(string featureKey) => Features.IsAvailable(featureKey, Tier);

    /// <summary>Reads the computer id (hw_hash) on a background thread, so that the first <see cref="Status"/> on the UI thread is quick.</summary>
    public Task WarmUpAsync() => Task.Run(() => HwValue());

    // ----- sign-in -----

    public async Task LoginAsync(string email, string password, CancellationToken ct)
        => SignIn(await _api.LoginAsync(email.Trim(), password, Device, ct), T._("Аккаунт: вход по паролю"));

    public Task RegisterAsync(string email, string password, string lang, CancellationToken ct)
        => _api.RegisterAsync(email.Trim(), password, lang, Device, ct);

    public async Task VerifyAsync(string email, string code, CancellationToken ct)
        => SignIn(await _api.VerifyAsync(email.Trim(), code.Trim(), Device, ct), T._("Аккаунт: почта подтверждена, вход выполнен"));

    /// <summary>Sends a new code ("verify" or "reset"); returns the seconds after which «Отправить ещё раз» may be pressed.</summary>
    public async Task<int> ResendAsync(string email, string purpose, CancellationToken ct)
        => (await _api.ResendAsync(email.Trim(), purpose, ct)).RetryAfter;

    public Task ResetAsync(string email, CancellationToken ct) => _api.ResetAsync(email.Trim(), ct);

    public async Task ResetConfirmAsync(string email, string code, string password, CancellationToken ct)
        => SignIn(await _api.ResetConfirmAsync(email.Trim(), code.Trim(), password, Device, ct), T._("Аккаунт: пароль сменён, вход выполнен"));

    /// <summary>
    /// Unbinds a computer with the permit of the last DEVICE_LIMIT, then the caller signs in again:
    /// after <see cref="LoginAsync"/> — the same call again, the same way;
    /// after <see cref="VerifyAsync"/> or <see cref="ResetConfirmAsync"/> — their code is already spent, so <see cref="LoginAsync"/> with the
    /// password (the new one after a reset), not the same call again;
    /// during <see cref="WaitBrowserLoginAsync"/> — nothing: the browser code is not spent and the poll goes on by itself.
    /// </summary>
    public async Task ReleaseAsync(string unbindToken, long deviceId, CancellationToken ct)
    {
        await _api.ReleaseAsync(unbindToken, deviceId, ct);
        Log.Info(T._("Аккаунт: компьютер отвязан, чтобы войти на этом"));
    }

    // ----- signed in -----

    /// <summary>
    /// Signs out here: the server revokes this token (if it can be reached), the local data of this sign-in is forgotten either way
    /// (also when cancelled; the cancellation goes on). A sign-in made while the server was answering stays. Not signed in: nothing.
    /// </summary>
    public async Task LogoutAsync(CancellationToken ct)
    {
        if (Token() is not { } token)
            return;
        try
        {
            await _api.LogoutAsync(token, ct);
        }
        catch (ApiException ex)
        {
            Log.Info(T.F("Аккаунт: сервер не подтвердил выход ({0}) — данные входа всё равно удалены", ex.Error.Code));
        }
        finally
        {
            if (SignOutIfCurrent(token, revoked: false))
                Log.Info(T._("Аккаунт: выход"));
        }
    }

    public Task<DeviceList> DevicesAsync(CancellationToken ct) => WithToken(t => _api.DevicesAsync(t, ct));

    /// <summary>Unbinds a computer of the account; this computer (its id or null) means signing out.</summary>
    public async Task UnbindAsync(long? deviceId, CancellationToken ct)
    {
        long? own;
        lock (_lock)
            own = _data.DeviceId;
        string? used = null;
        await WithToken(async t =>
        {
            used = t;
            await _api.UnbindAsync(t, deviceId, ct);
            return true;
        });
        if (deviceId is null || deviceId == own)
        {
            // Only the sign-in the call was made with: the user may have signed in again while the server was answering.
            if (SignOutIfCurrent(used!, revoked: false))
                Log.Info(T._("Аккаунт: этот компьютер отвязан — выход"));
        }
        else
        {
            Log.Info(T._("Аккаунт: отвязан другой компьютер"));
        }
    }

    /// <summary>
    /// Applies a promo code; true when its ticket was taken. False: the server took the code but its ticket does not fit this sign-in
    /// (screens show <see cref="Tier"/>, which stays as it was).
    /// </summary>
    public async Task<bool> ApplyPromoAsync(string code, CancellationToken ct)
    {
        var token = RequireToken();
        var ticket = await WithToken(token, t => _api.PromoApplyAsync(t, code.Trim(), ct));
        if (!AcceptTicket(ticket, token))
            return false;
        Log.Info(T._("Аккаунт: промокод применён"));
        return true;
    }

    /// <summary>«Написать нам»: the account's e-mail when signed in, otherwise the typed one (required).</summary>
    public async Task SendSupportAsync(string? email, string topic, string message, string? logBase64, CancellationToken ct)
    {
        var token = Token();
        var address = token is not null ? Email : email?.Trim();
        if (string.IsNullOrEmpty(address))
            throw new ArgumentException("An e-mail is needed to answer.", nameof(email));
        var request = new SupportRequest(address, topic, message, logBase64);
        if (token is null)
        {
            await _api.SupportAsync(null, request, _appVersion, ct);
            return;
        }
        await WithToken(token, async t =>
        {
            await _api.SupportAsync(t, request, _appVersion, ct);
            return true;
        });
    }

    /// <summary>
    /// Takes a new ticket (purchase, promo, refresh) for the current sign-in if it is signed by a known key, belongs to this sign-in and
    /// is not older than the ticket in hand (a later-issued downgrade is taken); true when taken.
    /// </summary>
    public bool AcceptTicket(SignedTicket ticket) => AcceptTicket(ticket, null);

    /// <summary>
    /// The same for a ticket asked for with <paramref name="token"/> (null: whatever sign-in is current): when the user signed out or
    /// in again while the server was answering, the ticket belongs to the old sign-in and is not taken.
    /// </summary>
    internal bool AcceptTicket(SignedTicket ticket, string? token)
    {
        var data = LicenseTicket.Verify(ticket.Ticket, ticket.Signature, _keys);
        var hw = HwValue(); // before the lock: the first read may query the hardware
        string? refusal = null;
        lock (_lock)
        {
            if (token is not null && _data.Token != token)
            {
                refusal = T._("Аккаунт: билет лицензии пришёл для прежнего входа — не принят");
            }
            // A sign-in whose first ticket could not be verified has no account number yet: the first good ticket of this computer gives it.
            else if (data is null || _data.Token is null || data.DeviceId != _data.DeviceId || (_data.AccountId is not null && data.Sub != _data.AccountId) ||
                     hw is null || !string.Equals(data.HwHash, hw, StringComparison.OrdinalIgnoreCase))
            {
                refusal = T._("Аккаунт: полученный билет лицензии не подходит этому компьютеру — оставлен прежний");
            }
            else if (_ticket is not null && data.IssuedAt < _ticket.IssuedAt)
            {
                refusal = T._("Аккаунт: полученный билет лицензии старше действующего — оставлен прежний");
            }
            else
            {
                _ticket = data;
                _data = _data with { Ticket = ticket.Ticket, Signature = ticket.Signature, AccountId = _data.AccountId ?? data.Sub };
                Persist(_data);
            }
        }
        if (refusal is not null)
        {
            Log.Warn(refusal);
            return false;
        }
        Notify();
        return true;
    }

    /// <summary>Creates the install number of the update statistics once (a random UUID) and returns it.</summary>
    public string EnsureInstallId()
    {
        lock (_lock)
        {
            if (_data.InstallId is { Length: > 0 } id)
                return id;
            _data = _data with { InstallId = Guid.NewGuid().ToString() };
            Persist(_data);
            return _data.InstallId!;
        }
    }

    /// <summary>«Сбросить номер установки»: the next update check creates a new one.</summary>
    public void ResetInstallId()
    {
        lock (_lock)
        {
            _data = _data with { InstallId = null };
            Persist(_data);
        }
    }

    // ----- inside -----

    private string? Token()
    {
        lock (_lock)
            return _data.Token;
    }

    /// <summary>The current token; not signed in → TOKEN_REVOKED (the one place that makes this answer; inside an async method, so it ends the task).</summary>
    private string RequireToken() => Token() ?? throw new ApiException(new ApiError(ApiError.TokenRevoked, "", 401, null));

    /// <summary>A call with the current token; TOKEN_REVOKED signs out (and is rethrown). Not signed in → a task failed with TOKEN_REVOKED.</summary>
    private async Task<TResult> WithToken<TResult>(Func<string, Task<TResult>> call) => await WithToken(RequireToken(), call);

    /// <summary>
    /// The same with the token the caller already read. TOKEN_REVOKED ends only the sign-in this token belongs to: when the user signed
    /// out (or in again) while the call was away, the answer is old news and the current session stays.
    /// </summary>
    private async Task<TResult> WithToken<TResult>(string token, Func<string, Task<TResult>> call)
    {
        try
        {
            return await call(token);
        }
        catch (ApiException ex) when (ex.Error.Code == ApiError.TokenRevoked)
        {
            if (SignOutIfCurrent(token, revoked: true))
                Log.Info(T._("Аккаунт: вход больше не действует (компьютер отвязан или пароль сменён) — программа в бесплатном режиме"));
            throw;
        }
    }

    /// <summary>The computer id, or null when it cannot be read (logged once, only the kind of the failure).</summary>
    private string? HwValue()
    {
        try
        {
            return _hw.Value;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _hwFailureLogged, 1) == 0)
                Log.Warn(T.F("Аккаунт: номер компьютера не прочитан ({0}) — Pro на этом компьютере не включится", ex.GetType().Name));
            return null;
        }
    }

    /// <summary>
    /// Saves the data (under <c>_lock</c>, so the disk follows the memory in order). A store that cannot write (a full disk, a broken
    /// DPAPI profile, a locked file) is logged — only the kind of the failure — and the state stays in memory for this run.
    /// </summary>
    private void Persist(AccountData data)
    {
        try
        {
            _store.Save(data);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            Log.Warn(T.F("Аккаунт: данные входа не сохранены ({0}) — действуют до закрытия программы", ex.GetType().Name));
        }
    }

    /// <summary>After a change of the sign-in or the ticket: the refresh loop computes its sleep again, the screens are told.</summary>
    private void Notify()
    {
        Wake();
        Changed?.Invoke();
    }

    private void SignIn(LoginResult result, string logText)
    {
        var ticket = LicenseTicket.Verify(result.License.Ticket, result.License.Signature, _keys);
        lock (_lock)
        {
            _data = new AccountData(result.Token, result.Account.Email, result.Device.Id, ticket?.Sub, result.License.Ticket,
                result.License.Signature, _data.InstallId);
            _ticket = ticket;
            Persist(_data);
            _sessionEnded = false;
        }
        if (ticket is null)
            Log.Warn(T._("Аккаунт: подпись билета лицензии не проверена — до следующей проверки программа в бесплатном режиме"));
        Log.Info(logText);
        Notify();
    }

    /// <summary>Signs out if the current sign-in uses this token; false (nothing changes) when the session is another one or none.</summary>
    private bool SignOutIfCurrent(string token, bool revoked)
    {
        lock (_lock)
        {
            if (_data.Token != token)
                return false;
            _data = _data.SignedOut();
            _ticket = null;
            Persist(_data);
            if (revoked)
                _sessionEnded = true;
        }
        Notify();
        return true;
    }
}
