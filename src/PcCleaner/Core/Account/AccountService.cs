using PcCleaner.Core.Localization;

namespace PcCleaner.Core.Account;

/// <summary>
/// The account on this computer: sign-in (password, sign-up code, password reset, browser code), the ticket and the tier, the
/// computers of the account, the promo code, «Написать нам», signing out. Thread-safe; <see cref="Changed"/> fires after every
/// change of the sign-in or the ticket, possibly on a background thread. A failed call throws <see cref="ApiException"/>;
/// TOKEN_REVOKED signs out first (<see cref="SessionEnded"/>). Tokens, passwords, tickets and hw_hash never go to the log.
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
    public bool SessionEnded { get; private set; }

    public LicenseStatus Status
    {
        get
        {
            AccountData data;
            LicenseTicketData? ticket;
            lock (_lock)
                (data, ticket) = (_data, _ticket);
            return LicenseState.Evaluate(ticket, data.Token is not null, _hw.Value, data.DeviceId, data.AccountId, _now());
        }
    }

    public Tier Tier => Status.Tier;

    public bool IsAvailable(string featureKey) => Features.IsAvailable(featureKey, Tier);

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

    /// <summary>Unbinds a computer with the permit of the last DEVICE_LIMIT; then the caller signs in again the same way.</summary>
    public async Task ReleaseAsync(string unbindToken, long deviceId, CancellationToken ct)
    {
        await _api.ReleaseAsync(unbindToken, deviceId, ct);
        Log.Info(T._("Аккаунт: компьютер отвязан, чтобы войти на этом"));
    }

    // ----- signed in -----

    /// <summary>Signs out here: the server revokes this token (if it can be reached), the local data is forgotten either way.</summary>
    public async Task LogoutAsync(CancellationToken ct)
    {
        var token = Token();
        if (token is not null)
        {
            try
            {
                await _api.LogoutAsync(token, ct);
            }
            catch (ApiException ex)
            {
                Log.Info(T.F("Аккаунт: сервер не подтвердил выход ({0}) — данные входа всё равно удалены", ex.Error.Code));
            }
        }
        SignOut(revoked: false);
        Log.Info(T._("Аккаунт: выход"));
    }

    public Task<DeviceList> DevicesAsync(CancellationToken ct) => WithToken(t => _api.DevicesAsync(t, ct));

    /// <summary>Unbinds a computer of the account; this computer (its id or null) means signing out.</summary>
    public async Task UnbindAsync(long? deviceId, CancellationToken ct)
    {
        long? own;
        lock (_lock)
            own = _data.DeviceId;
        await WithToken(async t =>
        {
            await _api.UnbindAsync(t, deviceId, ct);
            return true;
        });
        if (deviceId is null || deviceId == own)
        {
            SignOut(revoked: false);
            Log.Info(T._("Аккаунт: этот компьютер отвязан — выход"));
        }
        else
        {
            Log.Info(T._("Аккаунт: отвязан другой компьютер"));
        }
    }

    public async Task ApplyPromoAsync(string code, CancellationToken ct)
    {
        var ticket = await WithToken(t => _api.PromoApplyAsync(t, code.Trim(), ct));
        if (AcceptTicket(ticket))
            Log.Info(T._("Аккаунт: промокод применён"));
    }

    /// <summary>«Написать нам»: the account's e-mail when signed in, otherwise the typed one (required).</summary>
    public Task SendSupportAsync(string? email, string topic, string message, string? logBase64, CancellationToken ct)
    {
        var token = Token();
        var address = token is not null ? Email : email?.Trim();
        if (string.IsNullOrEmpty(address))
            throw new ArgumentException("An e-mail is needed to answer.", nameof(email));
        return _api.SupportAsync(token, new SupportRequest(address, topic, message, logBase64), _appVersion, ct);
    }

    /// <summary>Takes a new ticket (purchase, promo, refresh) if it is signed by a known key and belongs to this sign-in; true when taken.</summary>
    public bool AcceptTicket(SignedTicket ticket)
    {
        var data = LicenseTicket.Verify(ticket.Ticket, ticket.Signature, _keys);
        lock (_lock)
        {
            if (data is null || _data.Token is null || data.DeviceId != _data.DeviceId || data.Sub != _data.AccountId ||
                !string.Equals(data.HwHash, _hw.Value, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn(T._("Аккаунт: полученный билет лицензии не подходит этому компьютеру — оставлен прежний"));
                return false;
            }
            _ticket = data;
            _data = _data with { Ticket = ticket.Ticket, Signature = ticket.Signature };
            _store.Save(_data);
        }
        Changed?.Invoke();
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
            _store.Save(_data);
            return _data.InstallId!;
        }
    }

    /// <summary>«Сбросить номер установки»: the next update check creates a new one.</summary>
    public void ResetInstallId()
    {
        lock (_lock)
        {
            _data = _data with { InstallId = null };
            _store.Save(_data);
        }
    }

    // ----- inside -----

    private string? Token()
    {
        lock (_lock)
            return _data.Token;
    }

    /// <summary>A call with the token; TOKEN_REVOKED signs out (and is rethrown). Not signed in → TOKEN_REVOKED at once.</summary>
    private async Task<TResult> WithToken<TResult>(Func<string, Task<TResult>> call)
    {
        var token = Token() ?? throw new ApiException(new ApiError(ApiError.TokenRevoked, "", 401, null));
        try
        {
            return await call(token);
        }
        catch (ApiException ex) when (ex.Error.Code == ApiError.TokenRevoked)
        {
            SignOut(revoked: true);
            Log.Info(T._("Аккаунт: вход больше не действует (компьютер отвязан или пароль сменён) — программа в бесплатном режиме"));
            throw;
        }
    }

    private void SignIn(LoginResult result, string logText)
    {
        var ticket = LicenseTicket.Verify(result.License.Ticket, result.License.Signature, _keys);
        lock (_lock)
        {
            _data = new AccountData(result.Token, result.Account.Email, result.Device.Id, ticket?.Sub, result.License.Ticket,
                result.License.Signature, _data.InstallId);
            _ticket = ticket;
            _store.Save(_data);
            SessionEnded = false;
        }
        if (ticket is null)
            Log.Warn(T._("Аккаунт: подпись билета лицензии не проверена — до следующей проверки программа в бесплатном режиме"));
        Log.Info(logText);
        Changed?.Invoke();
    }

    private void SignOut(bool revoked)
    {
        lock (_lock)
        {
            _data = _data.SignedOut();
            _ticket = null;
            _store.Save(_data);
            if (revoked)
                SessionEnded = true;
        }
        Changed?.Invoke();
    }
}
