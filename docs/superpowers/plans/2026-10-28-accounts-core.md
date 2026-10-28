# Ядро аккаунта (часть B1, Windows) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Без интерфейса: клиент API waterca7.ru, проверка подписанного билета лицензии, вычисление уровня Гость / Free / Pro, таблица Pro-функций, защищённое хранилище входа, номер компьютера и `AccountService` со всеми сценариями входа, обновления билета и оплаты — всё покрыто тестами на поддельном сервере.

**Architecture:** Общий код — папка `src/PcCleaner/Core/Account/` (без WPF и без Windows-API; на Mac копируется без изменений в части B6). Платформенное — за интерфейсами `IAccountStore` и `IHwHash`; реализации Windows (DPAPI, WMI) — в `src/PcCleaner/Core/AccountWindows/`. `ApiClient` знает только HTTP и JSON; `LicenseTicket` и `LicenseState` — чистые функции; `AccountService` соединяет их, хранит состояние и шлёт событие `Changed`. Экраны (B2–B4) пользуются только `AccountService`.

**Tech Stack:** C# / .NET 7 SDK (`net7.0-windows`), `System.Net.Http`, `System.Text.Json`, `System.Security.Cryptography` (ECDSA P-256, DER), `System.Security.Cryptography.ProtectedData` 7.0.1 (DPAPI), `System.Management` (уже подключён), xUnit.

## Global Constraints

- Спека: `A:\WaterCa7s-pc-cleaner\docs\superpowers\specs\2026-10-27-menu-and-accounts-design.md`, разделы 2, 5, 6. Контракт: `A:\waterca7.ru\docs\api-contract-for-app.md` (он главнее спеки там, где они расходятся: опрос оплаты — раз в 5 с, не 3).
- База API `https://waterca7.ru/api/v1`, только HTTPS, JSON, `Authorization: Bearer <токен>`, `Accept-Language: ru|en`. Ветвиться по полю `error`, не по HTTP-статусу. Базовый адрес подменяется только в тестах.
- Билет: base64 → байты → ECDSA P-256 / SHA-256, формат **DER** (`DSASignatureFormat.Rfc3279DerSequence`) по любому ключу массива → только после этого JSON. Требовать `v == 1`.
- Pro, если подпись верна, `hw_hash`/`device_id`/`sub` свои, `plan ∈ {pro, family, custom}`, `issued_at ≤ now < valid_until` и (`expires_at` null или `now < expires_at`).
- Тестовый ключ сервера (`license_test_pub.pem`) в массив боевых ключей не попадает никогда — это проверяет тест по отпечатку. Билеты в тестах подписываются временным ключом, созданным в самом тесте; закрытых ключей в репозитории нет.
- `TOKEN_REVOKED` → данные входа стираются (номер установки остаётся), уровень — Гость с пометкой «нужно войти». Сбой сервера (5xx, таймаут, неверная подпись) не стирает действующий билет.
- Токен, пароль, билет и `hw_hash` в журнал не попадают.
- Файлы `Core/Account/` пользуются только `System.*`, `PcCleaner.Core.AppInfo`, `PcCleaner.Core.Log` (`Info(string)`, `Warn(string)`, `Error(string, Exception?)`) и `T._` / `T.F` из `PcCleaner.Core.Localization`.
- Новые тексты — через `T._`/`T.F` с английским в `en.json` (тест каталога требует записи и запрещает лишние).
- Тесты не меняют настоящую систему; DPAPI-хранилище в тестах пишет только во временную папку; запрос WMI в тесте только читает.
- Работа в ветке `menu-accounts` рабочей копии `A:\WaterCa7s-pc-cleaner-menu` (часть A уже там). Не пушить без «да» владельца; локальный `main` в `A:\WaterCa7s-pc-cleaner` не трогать (только читать план и спеку).
- Коммиты заканчиваются строкой `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Файлы

- Create `src/PcCleaner/Core/Account/`:
  - `ApiError.cs` — `ApiError`, `ApiException`, коды ошибок;
  - `ApiModels.cs` — записи ответов и `DeviceFields`;
  - `ApiClient.cs` — HTTP-клиент и методы API;
  - `LicenseTicket.cs` — `LicenseTicketData` и проверка подписи;
  - `LicenseKeys.cs` — боевые публичные ключи (встроенный ресурс) и отпечаток ключа;
  - `LicenseState.cs` — `Tier`, `LicenseReason`, `LicenseStatus`, `Evaluate`;
  - `Features.cs` — ключи функций как в `features.php`;
  - `HwHash.cs` — `IHwHash` и чистая функция хэша;
  - `AccountStore.cs` — `AccountData`, `IAccountStore`;
  - `AccountService.cs` — состояние входа и все сценарии.
- Create `src/PcCleaner/Core/AccountWindows/DpapiAccountStore.cs`, `WmiHwHash.cs`.
- Create (владелец) `src/PcCleaner/Core/Account/license_pub.pem` — задача 10.
- Modify `src/PcCleaner/PcCleaner.csproj` (пакет ProtectedData, встроенный ключ), `src/PcCleaner/Core/Localization/en.json`, `docs/ARCHITECTURE.md`.
- Tests `tests/PcCleaner.Tests/Account/`: `AccountTestKit.cs` (поддельный сервер, временный ключ, память вместо хранилища), `ApiClientTests.cs`, `ApiEndpointsTests.cs`, `LicenseTicketTests.cs`, `LicenseStateTests.cs`, `FeaturesTests.cs`, `HwHashTests.cs`, `DpapiAccountStoreTests.cs`, `AccountServiceTests.cs`, `AccountServicePollingTests.cs`; фикстуры `tests/PcCleaner.Tests/Account/Fixtures/license_test_pub.pem`, `features.php`.

Команды (из `A:\WaterCa7s-pc-cleaner-menu`):
- класс тестов: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~PcCleaner.Tests.Account.ApiClientTests"`
- все тесты: `dotnet test tests/PcCleaner.Tests`

---

### Task 1: Ошибки API, модели и HTTP-клиент

**Files:**
- Create: `src/PcCleaner/Core/Account/ApiError.cs`, `ApiModels.cs`, `ApiClient.cs`
- Create: `tests/PcCleaner.Tests/Account/AccountTestKit.cs`, `tests/PcCleaner.Tests/Account/ApiClientTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ApiError(string Code, string Message, int HttpStatus, JsonElement? Payload)` с константами кодов, `string? Reason`, `string? Status`, `DeviceLimit? AsDeviceLimit()`, `static ApiError NetworkError()`;
  - `public sealed class ApiException : Exception` с `ApiError Error`;
  - `public sealed class ApiClient : IDisposable`: `public ApiClient(Func<string> language)` (боевой адрес), `internal ApiClient(Func<string> language, HttpMessageHandler? handler, string baseUrl, TimeSpan? timeout = null)`, `internal Task<(int Status, string Body)> SendRawAsync(HttpMethod, string path, object? body, string? token, CancellationToken)`, `internal Task<T> SendAsync<T>(...)`;
  - модели из `ApiModels.cs` (полный список в шаге 3).
  - В тестах: `FakeApi : HttpMessageHandler` (`Base`, `On(...)`, `Requests`, `ThrowOnSend`), `MemoryAccountStore`, `TestLicense`.

- [ ] **Step 1: Помощники тестов**

Create `tests/PcCleaner.Tests/Account/AccountTestKit.cs`:

```csharp
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcCleaner.Core.Account;

namespace PcCleaner.Tests.Account;

/// <summary>A fake waterca7.ru: answers by "METHOD /path" (the query is ignored); the last answer of a route repeats.</summary>
internal sealed class FakeApi : HttpMessageHandler
{
    public const string Base = "https://api.test/api/v1";

    private readonly Dictionary<string, Queue<(int Status, string Body)>> _routes = new();

    public sealed record Recorded(string Method, string Path, string Query, string? Body, string? Bearer, string AcceptLanguage);

    public List<Recorded> Requests { get; } = new();

    /// <summary>When set, every request fails as if the network were down.</summary>
    public Exception? ThrowOnSend { get; set; }

    public FakeApi On(string route, int status, string body)
    {
        if (!_routes.TryGetValue(route, out var queue))
            _routes[route] = queue = new Queue<(int, string)>();
        queue.Enqueue((status, body));
        return this;
    }

    public FakeApi OnError(string route, int status, string code, string extraJson = "")
        => On(route, status, $"{{\"error\":\"{code}\",\"message\":\"msg {code}\"{extraJson}}}");

    public IEnumerable<Recorded> To(string route) => Requests.Where(r => $"{r.Method} {r.Path}" == route);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (ThrowOnSend is not null)
            throw ThrowOnSend;
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath["/api/v1".Length..];
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new Recorded(request.Method.Method, path, uri.Query, body, request.Headers.Authorization?.Parameter,
            request.Headers.AcceptLanguage.ToString()));
        var key = $"{request.Method.Method} {path}";
        if (!_routes.TryGetValue(key, out var queue) || queue.Count == 0)
            return Answer(request, 404, "{\"error\":\"NOT_FOUND\",\"message\":\"not found\"}");
        var (status, text) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        return Answer(request, status, text);
    }

    private static HttpResponseMessage Answer(HttpRequestMessage request, int status, string body) => new((HttpStatusCode)status)
    {
        RequestMessage = request,
        Content = new StringContent(body, Encoding.UTF8, body.StartsWith('{') ? "application/json" : "text/html"),
    };
}

/// <summary>A license key made for one test run; tickets are signed the way the server signs them (DER).</summary>
internal sealed class TestLicense : IDisposable
{
    public const string Hw = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    public const long Sub = 7;
    public const long DeviceId = 42;

    public static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicPem => Key.ExportSubjectPublicKeyInfoPem();

    public IReadOnlyList<string> Keys => new[] { PublicPem };

    public static string Json(string plan = "pro", string kind = "subscription", long sub = Sub, long deviceId = DeviceId, string hw = Hw,
        DateTimeOffset? issued = null, DateTimeOffset? validUntil = null, DateTimeOffset? expires = null, bool noExpiry = false, int v = 1,
        int devicesMax = 1)
    {
        string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var issuedAt = issued ?? Now.AddDays(-1);
        var until = validUntil ?? issuedAt.AddDays(14);
        var exp = noExpiry ? "null" : $"\"{Iso(expires ?? Now.AddDays(300))}\"";
        return $"{{\"v\":{v},\"sub\":{sub},\"email\":\"user@example.com\",\"plan\":\"{plan}\",\"kind\":\"{kind}\",\"devices_max\":{devicesMax}," +
               $"\"expires_at\":{exp},\"hw_hash\":\"{hw}\",\"device_id\":{deviceId},\"issued_at\":\"{Iso(issuedAt)}\",\"valid_until\":\"{Iso(until)}\"}}";
    }

    public SignedTicket Sign(string json, DSASignatureFormat format = DSASignatureFormat.Rfc3279DerSequence)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return new SignedTicket(Convert.ToBase64String(bytes), Convert.ToBase64String(Key.SignData(bytes, HashAlgorithmName.SHA256, format)));
    }

    /// <summary>The JSON of a successful sign-in (login / verify / reset-confirm / code poll) with a ticket of this key.</summary>
    public string LoginJson(string ticketJson, string token = "t0ken", long deviceId = DeviceId)
    {
        var t = Sign(ticketJson);
        return $"{{\"token\":\"{token}\",\"account\":{{\"email\":\"user@example.com\",\"verified\":true}},\"device\":{{\"id\":{deviceId},\"name\":\"PC\"}}," +
               $"\"license\":{{\"ticket\":\"{t.Ticket}\",\"signature\":\"{t.Signature}\"}}}}";
    }

    public string TicketJson(string ticketJson)
    {
        var t = Sign(ticketJson);
        return $"{{\"ticket\":\"{t.Ticket}\",\"signature\":\"{t.Signature}\"}}";
    }

    public void Dispose() => Key.Dispose();
}

internal static class Api
{
    public static ApiClient Client(FakeApi fake, string language = "ru") => new(() => language, fake, FakeApi.Base);

    public static DeviceFields Device => new(TestLicense.Hw, "windows", "PC", "1.5.0");

    public static JsonElement Body(FakeApi.Recorded r) => JsonDocument.Parse(r.Body!).RootElement;
}
```

(`SignedTicket` и `DeviceFields` появятся в шаге 5 этой задачи. Поддельное хранилище и номер компьютера для `AccountService` добавит задача 7 — отдельным файлом `AccountTestKit.Store.cs`, когда появятся `IAccountStore` и `IHwHash`.)

В `FakeApi` список запросов читается тестом, пока фоновая проверка (`RefreshSoon`) может дописывать в него: доступ под замком. Заменить в классе выше объявление `Requests` и строку `Requests.Add(...)` на:

```csharp
    private readonly object _gate = new();
    private readonly List<Recorded> _requests = new();

    /// <summary>A snapshot of the requests so far (background calls may add more meanwhile).</summary>
    public List<Recorded> Requests
    {
        get
        {
            lock (_gate)
                return _requests.ToList();
        }
    }
```

и в `SendAsync`:

```csharp
        lock (_gate)
            _requests.Add(new Recorded(request.Method.Method, path, uri.Query, body, request.Headers.Authorization?.Parameter,
                request.Headers.AcceptLanguage.ToString()));
```

- [ ] **Step 2: Падающие тесты клиента**

Create `tests/PcCleaner.Tests/Account/ApiClientTests.cs`:

```csharp
using System.Net.Http;
using PcCleaner.Core.Account;
using Xunit;

namespace PcCleaner.Tests.Account;

public class ApiClientTests
{
    private sealed record Pong(bool Ok);

    [Fact]
    public async Task ARequest_CarriesTheLanguageTheBearerAndJson()
    {
        var fake = new FakeApi().On("POST /ping", 200, "{\"ok\":true}");
        using var api = Api.Client(fake, "en");

        var pong = await api.SendAsync<Pong>(HttpMethod.Post, "/ping", new { email = "a@b.c" }, "tok", CancellationToken.None);

        Assert.True(pong.Ok);
        var r = Assert.Single(fake.Requests);
        Assert.Equal("en", r.AcceptLanguage);
        Assert.Equal("tok", r.Bearer);
        Assert.Equal("a@b.c", Api.Body(r).GetProperty("email").GetString());
    }

    [Fact]
    public async Task WithoutAToken_ThereIsNoAuthorizationHeader()
    {
        var fake = new FakeApi().On("GET /ping", 200, "{\"ok\":true}");
        using var api = Api.Client(fake);

        await api.SendAsync<Pong>(HttpMethod.Get, "/ping", null, null, CancellationToken.None);

        Assert.Null(fake.Requests[0].Bearer);
        Assert.Null(fake.Requests[0].Body);
    }

    [Fact]
    public async Task AnErrorAnswer_BecomesApiException_WithTheCodeMessageAndPayload()
    {
        var fake = new FakeApi().OnError("POST /promo/apply", 422, "PROMO_INVALID", ",\"reason\":7");
        using var api = Api.Client(fake);

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.SendAsync<Pong>(HttpMethod.Post, "/promo/apply", new { code = "X" }, "t", CancellationToken.None));

        Assert.Equal(ApiError.PromoInvalid, ex.Error.Code);
        Assert.Equal("msg PROMO_INVALID", ex.Error.Message);
        Assert.Equal(422, ex.Error.HttpStatus);
        Assert.Equal("7", ex.Error.Reason);
    }

    [Fact]
    public async Task ATextReason_AndAStatus_AreReadToo()
    {
        var fake = new FakeApi()
            .OnError("POST /pay/quote", 409, "BLOCKED", ",\"reason\":\"no_change\"")
            .OnError("POST /pay/link", 409, "PAYMENT_NOT_PENDING", ",\"status\":\"paid\"");
        using var api = Api.Client(fake);

        var blocked = await Assert.ThrowsAsync<ApiException>(() => api.SendAsync<Pong>(HttpMethod.Post, "/pay/quote", new { }, "t", CancellationToken.None));
        var notPending = await Assert.ThrowsAsync<ApiException>(() => api.SendAsync<Pong>(HttpMethod.Post, "/pay/link", new { }, "t", CancellationToken.None));

        Assert.Equal("no_change", blocked.Error.Reason);
        Assert.Equal("paid", notPending.Error.Status);
    }

    [Fact]
    public async Task ANonJsonAnswer_IsAServerError()
    {
        var fake = new FakeApi().On("GET /ping", 502, "<html>Bad gateway</html>");
        using var api = Api.Client(fake);

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.SendAsync<Pong>(HttpMethod.Get, "/ping", null, null, CancellationToken.None));

        Assert.Equal(ApiError.ServerError, ex.Error.Code);
        Assert.Equal(502, ex.Error.HttpStatus);
    }

    [Fact]
    public async Task ABrokenSuccessBody_IsAServerError()
    {
        var fake = new FakeApi().On("GET /ping", 200, "{not json");
        using var api = Api.Client(fake);

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.SendAsync<Pong>(HttpMethod.Get, "/ping", null, null, CancellationToken.None));

        Assert.Equal(ApiError.ServerError, ex.Error.Code);
    }

    [Fact]
    public async Task NoNetwork_IsTheNetworkCode()
    {
        var fake = new FakeApi { ThrowOnSend = new HttpRequestException("down") };
        using var api = Api.Client(fake);

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.SendAsync<Pong>(HttpMethod.Get, "/ping", null, null, CancellationToken.None));

        Assert.Equal(ApiError.Network, ex.Error.Code);
        Assert.Equal(0, ex.Error.HttpStatus);
    }

    [Fact]
    public async Task ATimeout_IsTheNetworkCode_ButACancellationIsACancellation()
    {
        var fake = new FakeApi { ThrowOnSend = new TaskCanceledException("timeout") };
        using var api = Api.Client(fake);

        var timeout = await Assert.ThrowsAsync<ApiException>(() => api.SendAsync<Pong>(HttpMethod.Get, "/ping", null, null, CancellationToken.None));
        Assert.Equal(ApiError.Network, timeout.Error.Code);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        fake.ThrowOnSend = new TaskCanceledException("cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.SendAsync<Pong>(HttpMethod.Get, "/ping", null, null, cts.Token));
    }

    [Fact]
    public void OnlyHttps_IsAccepted()
        => Assert.Throws<ArgumentException>(() => new ApiClient(() => "ru", new FakeApi(), "http://api.test/api/v1"));

    [Fact]
    public void TheRealClient_TalksToWaterca7()
        => Assert.Equal("https://waterca7.ru/api/v1", ApiClient.LiveBaseUrl);

    [Fact]
    public void ADeviceLimit_CarriesTheComputersAndTheReleasePermit()
    {
        var error = ApiClient.ParseError(409, "{\"error\":\"DEVICE_LIMIT\",\"message\":\"m\",\"bind_limit\":2,\"unbind_token\":\"" + new string('a', 64) + "\"," +
            "\"devices\":[{\"id\":5,\"name\":\"Office\",\"platform\":\"windows\",\"bound_at\":\"2026-10-01T10:00:00Z\",\"last_seen_at\":null,\"current\":false," +
            "\"whose\":\"me@x.y\",\"member\":true}]}");

        var limit = error.AsDeviceLimit()!;

        Assert.Equal(2, limit.BindLimit);
        Assert.Equal(new string('a', 64), limit.UnbindToken);
        var device = Assert.Single(limit.Devices);
        Assert.Equal(5, device.Id);
        Assert.Equal("Office", device.Name);
        Assert.Null(device.LastSeenAt);
        Assert.Equal("me@x.y", device.Whose);
        Assert.Null(new ApiError(ApiError.AuthFailed, "", 401, null).AsDeviceLimit());
    }
}
```

- [ ] **Step 3: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~PcCleaner.Tests.Account.ApiClientTests"`
Expected: ошибка сборки (`ApiClient`, `ApiError` не найдены).

- [ ] **Step 4: `ApiError`**

Create `src/PcCleaner/Core/Account/ApiError.cs`:

```csharp
using System.Text.Json;

namespace PcCleaner.Core.Account;

/// <summary>
/// An answer of waterca7.ru that is not a success: the server's <c>error</c> code (branch on it, not on the HTTP status), its
/// <c>message</c> in the request's language (it may be shown as is) and the whole JSON for the extra fields. Two codes are the
/// client's own: <see cref="Network"/> (no answer) and <see cref="ServerError"/> (an answer that is not the API's JSON).
/// </summary>
public sealed record ApiError(string Code, string Message, int HttpStatus, JsonElement? Payload)
{
    public const string Network = "NETWORK";
    public const string ServerError = "SERVER_ERROR";
    public const string NotFound = "NOT_FOUND";
    public const string InvalidInput = "INVALID_INPUT";
    public const string AuthFailed = "AUTH_FAILED";
    public const string TokenRevoked = "TOKEN_REVOKED";
    public const string EmailNotVerified = "EMAIL_NOT_VERIFIED";
    public const string DeviceLimit = "DEVICE_LIMIT";
    public const string EmailTaken = "EMAIL_TAKEN";
    public const string EmailInvalid = "EMAIL_INVALID";
    public const string WeakPassword = "WEAK_PASSWORD";
    public const string ConsentRequired = "CONSENT_REQUIRED";
    public const string CodeInvalid = "CODE_INVALID";
    public const string RateLimited = "RATE_LIMITED";
    public const string UnbindQuota = "UNBIND_QUOTA";
    public const string PromoInvalid = "PROMO_INVALID";
    public const string Blocked = "BLOCKED";
    public const string PaymentsDisabled = "PAYMENTS_DISABLED";
    public const string PayMany = "PAY_MANY";
    public const string PaymentNotPending = "PAYMENT_NOT_PENDING";

    public static ApiError NetworkError() => new(Network, "", 0, null);

    /// <summary>The <c>reason</c> field as text: 1–7 of <see cref="PromoInvalid"/>, "no_change" / "change_more" of <see cref="Blocked"/>.</summary>
    public string? Reason => Text("reason");

    /// <summary>The <c>status</c> field of <see cref="PaymentNotPending"/>: "paid" or "failed".</summary>
    public string? Status => Text("status");

    /// <summary>The computers and the one-time release permit of a <see cref="DeviceLimit"/> answer; null for any other answer.</summary>
    public DeviceLimitInfo? AsDeviceLimit()
    {
        if (Code != DeviceLimit || Payload is not { ValueKind: JsonValueKind.Object } p)
            return null;
        try
        {
            return p.Deserialize<DeviceLimitInfo>(ApiClient.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string? Text(string name)
    {
        if (Payload is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }
}

/// <summary>A failed API call (<see cref="Error"/>). Cancellation by the caller is never wrapped: it stays an OperationCanceledException.</summary>
public sealed class ApiException : Exception
{
    public ApiException(ApiError error, Exception? inner = null)
        : base(error.Code, inner)
    {
        Error = error;
    }

    public ApiError Error { get; }
}
```

(Тест шага 2 вызывает `AsDeviceLimit()` и обращается к `limit.BindLimit` и т. д. — тип называется `DeviceLimitInfo`, потому что имя `DeviceLimit` занято константой.)

- [ ] **Step 5: Модели**

Create `src/PcCleaner/Core/Account/ApiModels.cs`:

```csharp
using System.Text.Json.Serialization;

namespace PcCleaner.Core.Account;

/// <summary>The fields of this computer every sign-in sends: <c>hw_hash, platform, name, app_version</c>.</summary>
public sealed record DeviceFields(string HwHash, string Platform, string Name, string AppVersion)
{
    /// <summary>The computer's name as the server takes it: control characters removed, trimmed, at most 64 characters.</summary>
    public static string CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= 64 ? clean : clean[..64];
    }
}

/// <summary>A license ticket as the server sends it: base64 of the JSON and base64 of its DER signature.</summary>
public sealed record SignedTicket(
    [property: JsonPropertyName("ticket")] string Ticket,
    [property: JsonPropertyName("signature")] string Signature);

public sealed record AccountInfo(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("verified")] bool Verified);

public sealed record DeviceRef(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name);

/// <summary>The answer of every successful sign-in: login, verify, reset/confirm, the browser code.</summary>
public sealed record LoginResult(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("account")] AccountInfo Account,
    [property: JsonPropertyName("device")] DeviceRef Device,
    [property: JsonPropertyName("license")] SignedTicket License);

/// <summary>The code of a sign-in through the browser: open <see cref="Url"/>, show <see cref="Code"/> large, poll until <see cref="ExpiresIn"/>.</summary>
public sealed record BrowserCode(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

public sealed record DeviceEntry(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("bound_at")] DateTimeOffset BoundAt,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset? LastSeenAt,
    [property: JsonPropertyName("current")] bool Current,
    [property: JsonPropertyName("whose")] string? Whose = null,
    [property: JsonPropertyName("member")] bool? Member = null);

public sealed record DeviceList(
    [property: JsonPropertyName("devices")] IReadOnlyList<DeviceEntry> Devices,
    [property: JsonPropertyName("bind_limit")] int BindLimit);

/// <summary>The extra fields of DEVICE_LIMIT: the account's computers and a one-time permit for <c>POST /device/release</c> (10 minutes).</summary>
public sealed record DeviceLimitInfo(
    [property: JsonPropertyName("devices")] IReadOnlyList<DeviceEntry> Devices,
    [property: JsonPropertyName("bind_limit")] int BindLimit,
    [property: JsonPropertyName("unbind_token")] string UnbindToken);

public sealed record ResendResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("retry_after")] int RetryAfter);

public sealed record PriceTerms(
    [property: JsonPropertyName("month")] int Month,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("lifetime")] int Lifetime);

public sealed record FamilyPrices(
    [property: JsonPropertyName("min")] int Min,
    [property: JsonPropertyName("max")] int Max,
    [property: JsonPropertyName("default")] int Default,
    [property: JsonPropertyName("table")] IReadOnlyDictionary<string, PriceTerms> Table);

public sealed record PriceList(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("pro")] PriceTerms Pro,
    [property: JsonPropertyName("family")] FamilyPrices Family,
    [property: JsonPropertyName("payments_enabled")] bool PaymentsEnabled);

/// <summary>What the purchase window asks about: plan "pro" | "family", term "month" | "year" | "lifetime".</summary>
public sealed record QuoteRequest(string Plan, string Term, int Devices, string? Promo = null, bool Change = false);

public sealed record Quote(
    [property: JsonPropertyName("list")] int List,
    [property: JsonPropertyName("discount")] int Discount,
    [property: JsonPropertyName("credit")] int Credit,
    [property: JsonPropertyName("amount")] int Amount,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("term")] string Term,
    [property: JsonPropertyName("devices")] int Devices,
    [property: JsonPropertyName("from_devices")] int? FromDevices,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt);

public sealed record PaymentLink(
    [property: JsonPropertyName("payment_id")] long PaymentId,
    [property: JsonPropertyName("url")] string Url);

/// <summary>The state of a payment: "pending" | "paid" | "failed"; a paid one carries the new ticket.</summary>
public sealed record PaymentStatus(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("license")] SignedTicket? License);

/// <summary>«Написать нам»: topic "question" | "bug" | "other", message 10–5000, the log as strict base64 (≤ 1 MiB) or null.</summary>
public sealed record SupportRequest(string Email, string Topic, string Message, string? LogBase64);
```

- [ ] **Step 6: `ApiClient` (общая часть)**

Create `src/PcCleaner/Core/Account/ApiClient.cs` (методы API добавит задача 2):

```csharp
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcCleaner.Core.Account;

/// <summary>
/// The program's side of the waterca7.ru API (docs/api-contract-for-app.md of the site): https only, JSON both ways, the UI
/// language in Accept-Language, the device token as Bearer. Every failure is an <see cref="ApiException"/>; the caller's
/// cancellation stays an OperationCanceledException. The base address is replaced only in tests.
/// </summary>
public sealed partial class ApiClient : IDisposable
{
    public const string LiveBaseUrl = "https://waterca7.ru/api/v1";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly Func<string> _language;

    /// <param name="language">"ru" or "en", read for every request.</param>
    public ApiClient(Func<string> language)
        : this(language, null, LiveBaseUrl)
    {
    }

    internal ApiClient(Func<string> language, HttpMessageHandler? handler, string baseUrl, TimeSpan? timeout = null)
    {
        if (!baseUrl.StartsWith("https://", StringComparison.Ordinal))
            throw new ArgumentException("The API is reached over https only.", nameof(baseUrl));
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"WaterCa7sPcCleaner/{AppInfo.Version}");
        _base = baseUrl.TrimEnd('/');
        _language = language;
    }

    /// <summary>Sends one request and returns the status and the body as they came (the code poll needs 202 itself).</summary>
    internal async Task<(int Status, string Body)> SendRawAsync(HttpMethod method, string path, object? body, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, _base + path);
        request.Headers.AcceptLanguage.ParseAdd(_language());
        request.Headers.Accept.ParseAdd("application/json");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                throw new ApiException(ApiError.NetworkError());
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException(ApiError.NetworkError(), ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ApiException(ApiError.NetworkError(), ex); // the timeout of HttpClient
        }
    }

    /// <summary>Sends one request; a 2xx body is read as <typeparamref name="T"/>, anything else is an <see cref="ApiException"/>.</summary>
    internal async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string? token, CancellationToken ct)
    {
        var (status, text) = await SendRawAsync(method, path, body, token, ct);
        if (status is >= 200 and < 300)
            return Read<T>(text, status);
        throw new ApiException(ParseError(status, text));
    }

    internal static T Read<T>(string text, int status)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, JsonOptions) ?? throw new ApiException(new ApiError(ApiError.ServerError, "", status, null));
        }
        catch (JsonException ex)
        {
            throw new ApiException(new ApiError(ApiError.ServerError, "", status, null), ex);
        }
    }

    /// <summary>The API's error JSON <c>{error, message, …}</c>; anything else (an HTML page, a proxy error) is <see cref="ApiError.ServerError"/>.</summary>
    internal static ApiError ParseError(int status, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var code) && code.ValueKind == JsonValueKind.String)
            {
                var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                return new ApiError(code.GetString() ?? ApiError.ServerError, message, status, root.Clone());
            }
        }
        catch (JsonException)
        {
            // not the API's JSON
        }
        return new ApiError(ApiError.ServerError, "", status, null);
    }

    public void Dispose() => _http.Dispose();
}
```

- [ ] **Step 7: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~PcCleaner.Tests.Account.ApiClientTests"` → PASS.
Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~LocalizationCatalogTests"` → PASS (в этих файлах нет русского текста).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Accounts: API client, error codes and response models

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Методы API

**Files:**
- Create: `src/PcCleaner/Core/Account/ApiClient.Endpoints.cs`
- Test: `tests/PcCleaner.Tests/Account/ApiEndpointsTests.cs`

**Interfaces:**
- Consumes: `ApiClient.SendAsync<T>`, `SendRawAsync`, модели задачи 1.
- Produces (все `public`, у всех последний параметр `CancellationToken ct`):
  - вход: `LoginAsync(string email, string password, DeviceFields device)`, `RegisterAsync(string email, string password, string lang, DeviceFields device)` → `Task`, `VerifyAsync(string email, string code, DeviceFields device)`, `ResendAsync(string email, string purpose)` → `ResendResult`, `ResetAsync(string email)` → `Task`, `ResetConfirmAsync(string email, string code, string password, DeviceFields device)` — все входящие → `LoginResult`;
  - через браузер: `StartBrowserCodeAsync(DeviceFields device)` → `BrowserCode`, `PollBrowserCodeAsync(string code)` → `LoginResult?` (null = ещё ждём, 202);
  - с токеном: `LogoutAsync(string token)`, `TicketAsync(string token)` → `SignedTicket`, `DevicesAsync(string token)` → `DeviceList`, `UnbindAsync(string token, long? deviceId)`, `PromoApplyAsync(string token, string code)` → `SignedTicket`, `QuoteAsync(string token, QuoteRequest request)` → `Quote`, `PayStartAsync(string token, QuoteRequest request)` → `PaymentLink`, `PayLinkAsync(string token, long paymentId)` → `PaymentLink`, `PayStatusAsync(string token, long paymentId)` → `PaymentStatus`;
  - без токена: `ReleaseAsync(string unbindToken, long deviceId)`, `PricesAsync(string? token)` → `PriceList`, `SupportAsync(string? token, SupportRequest request, string appVersion)`.

- [ ] **Step 1: Падающие тесты**

Create `tests/PcCleaner.Tests/Account/ApiEndpointsTests.cs`:

```csharp
using PcCleaner.Core.Account;
using Xunit;

namespace PcCleaner.Tests.Account;

public class ApiEndpointsTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static void HasDevice(System.Text.Json.JsonElement body)
    {
        Assert.Equal(TestLicense.Hw, body.GetProperty("hw_hash").GetString());
        Assert.Equal("windows", body.GetProperty("platform").GetString());
        Assert.Equal("PC", body.GetProperty("name").GetString());
        Assert.Equal("1.5.0", body.GetProperty("app_version").GetString());
    }

    [Fact]
    public async Task Login_SendsTheEmailPasswordAndDevice_AndReadsTheAnswer()
    {
        using var key = new TestLicense();
        var fake = new FakeApi().On("POST /auth/login", 200, key.LoginJson(TestLicense.Json()));
        using var api = Api.Client(fake);

        var result = await api.LoginAsync("user@example.com", "secret-pass", Api.Device, None);

        Assert.Equal("t0ken", result.Token);
        Assert.Equal(TestLicense.DeviceId, result.Device.Id);
        var body = Api.Body(fake.Requests[0]);
        Assert.Equal("user@example.com", body.GetProperty("email").GetString());
        Assert.Equal("secret-pass", body.GetProperty("password").GetString());
        HasDevice(body);
    }

    [Fact]
    public async Task Register_SendsConsentAsJsonTrue_AndTheLanguage()
    {
        var fake = new FakeApi().On("POST /auth/register", 200, "{\"pending\":true}");
        using var api = Api.Client(fake);

        await api.RegisterAsync("a@b.c", "password1", "en", Api.Device, None);

        var body = Api.Body(fake.Requests[0]);
        Assert.Equal(System.Text.Json.JsonValueKind.True, body.GetProperty("consent").ValueKind);
        Assert.Equal("en", body.GetProperty("lang").GetString());
        HasDevice(body);
    }

    [Fact]
    public async Task VerifyAndResetConfirm_SendTheCodeAndDevice()
    {
        using var key = new TestLicense();
        var fake = new FakeApi()
            .On("POST /auth/verify", 200, key.LoginJson(TestLicense.Json()))
            .On("POST /auth/reset/confirm", 200, key.LoginJson(TestLicense.Json()));
        using var api = Api.Client(fake);

        await api.VerifyAsync("a@b.c", "123456", Api.Device, None);
        await api.ResetConfirmAsync("a@b.c", "654321", "new-password", Api.Device, None);

        var verify = Api.Body(fake.To("POST /auth/verify").Single());
        Assert.Equal("123456", verify.GetProperty("code").GetString());
        HasDevice(verify);
        var confirm = Api.Body(fake.To("POST /auth/reset/confirm").Single());
        Assert.Equal("654321", confirm.GetProperty("code").GetString());
        Assert.Equal("new-password", confirm.GetProperty("password").GetString());
        HasDevice(confirm);
    }

    [Fact]
    public async Task ResendAndReset_AlwaysAnswerOk()
    {
        var fake = new FakeApi()
            .On("POST /auth/resend", 200, "{\"ok\":true,\"retry_after\":60}")
            .On("POST /auth/reset", 200, "{\"ok\":true}");
        using var api = Api.Client(fake);

        var resend = await api.ResendAsync("a@b.c", "verify", None);
        await api.ResetAsync("a@b.c", None);

        Assert.Equal(60, resend.RetryAfter);
        Assert.Equal("verify", Api.Body(fake.To("POST /auth/resend").Single()).GetProperty("purpose").GetString());
        Assert.Equal("a@b.c", Api.Body(fake.To("POST /auth/reset").Single()).GetProperty("email").GetString());
    }

    [Fact]
    public async Task TheBrowserCode_Starts_AndItsPollTellsWaitingFromDone()
    {
        using var key = new TestLicense();
        var fake = new FakeApi()
            .On("POST /auth/code", 200, "{\"code\":\"ABCD-1234\",\"url\":\"https://waterca7.ru/auth/device?code=ABCD-1234\",\"expires_in\":600}")
            .On("GET /auth/code/ABCD-1234", 202, "{\"status\":\"pending\"}")
            .On("GET /auth/code/ABCD-1234", 200, key.LoginJson(TestLicense.Json()));
        using var api = Api.Client(fake);

        var code = await api.StartBrowserCodeAsync(Api.Device, None);
        var first = await api.PollBrowserCodeAsync(code.Code, None);
        var second = await api.PollBrowserCodeAsync(code.Code, None);

        Assert.Equal(600, code.ExpiresIn);
        Assert.Null(first);
        Assert.Equal("t0ken", second!.Token);
        HasDevice(Api.Body(fake.To("POST /auth/code").Single()));
    }

    [Fact]
    public async Task AGoneCode_IsNotFound()
    {
        var fake = new FakeApi().OnError("GET /auth/code/X", 410, "NOT_FOUND");
        using var api = Api.Client(fake);

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.PollBrowserCodeAsync("X", None));

        Assert.Equal(ApiError.NotFound, ex.Error.Code);
        Assert.Equal(410, ex.Error.HttpStatus);
    }

    [Fact]
    public async Task TheTokenMethods_SendTheBearer_AndTheirBodies()
    {
        using var key = new TestLicense();
        var ticket = key.TicketJson(TestLicense.Json());
        var fake = new FakeApi()
            .On("POST /auth/logout", 200, "{\"ok\":true}")
            .On("GET /license/ticket", 200, ticket)
            .On("GET /devices", 200, "{\"devices\":[],\"bind_limit\":1}")
            .On("POST /device/unbind", 200, "{\"ok\":true}")
            .On("POST /promo/apply", 200, ticket);
        using var api = Api.Client(fake);

        await api.LogoutAsync("tok", None);
        var t = await api.TicketAsync("tok", None);
        var devices = await api.DevicesAsync("tok", None);
        await api.UnbindAsync("tok", 5, None);
        await api.UnbindAsync("tok", null, None);
        await api.PromoApplyAsync("tok", "FRIEND-1", None);

        Assert.All(fake.Requests, r => Assert.Equal("tok", r.Bearer));
        Assert.False(string.IsNullOrEmpty(t.Signature));
        Assert.Equal(1, devices.BindLimit);
        var unbinds = fake.To("POST /device/unbind").ToList();
        Assert.Equal(5, Api.Body(unbinds[0]).GetProperty("device_id").GetInt64());
        Assert.False(Api.Body(unbinds[1]).TryGetProperty("device_id", out _)); // without an id: this computer
        Assert.Equal("FRIEND-1", Api.Body(fake.To("POST /promo/apply").Single()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Release_HasNoBearer_ButThePermitAndTheComputer()
    {
        var fake = new FakeApi().On("POST /device/release", 200, "{\"ok\":true}");
        using var api = Api.Client(fake);

        await api.ReleaseAsync(new string('b', 64), 5, None);

        var r = fake.Requests.Single();
        Assert.Null(r.Bearer);
        Assert.Equal(new string('b', 64), Api.Body(r).GetProperty("unbind_token").GetString());
        Assert.Equal(5, Api.Body(r).GetProperty("device_id").GetInt64());
    }

    [Fact]
    public async Task Prices_QuoteStartLinkAndStatus()
    {
        using var key = new TestLicense();
        var t = key.Sign(TestLicense.Json());
        var fake = new FakeApi()
            .On("GET /prices", 200, "{\"currency\":\"RUB\",\"pro\":{\"month\":199,\"year\":1490,\"lifetime\":3990}," +
                                    "\"family\":{\"min\":2,\"max\":5,\"default\":3,\"table\":{\"2\":{\"month\":299,\"year\":2290,\"lifetime\":5990}}},\"payments_enabled\":true}")
            .On("POST /pay/quote", 200, "{\"list\":1490,\"discount\":149,\"credit\":0,\"amount\":1341,\"action\":\"new\",\"term\":\"year\",\"devices\":1,\"from_devices\":null,\"expires_at\":\"2027-11-01T00:00:00Z\"}")
            .On("POST /pay/start", 200, "{\"payment_id\":91,\"url\":\"https://waterca7.ru/pay/app/91?t=" + new string('c', 64) + "\"}")
            .On("POST /pay/link", 200, "{\"payment_id\":91,\"url\":\"https://waterca7.ru/pay/app/91?t=" + new string('d', 64) + "\"}")
            .On("GET /pay/91", 200, $"{{\"status\":\"paid\",\"license\":{{\"ticket\":\"{t.Ticket}\",\"signature\":\"{t.Signature}\"}}}}");
        using var api = Api.Client(fake);
        var request = new QuoteRequest("pro", "year", 1, "SALE10");

        var prices = await api.PricesAsync(null, None);
        var quote = await api.QuoteAsync("tok", request, None);
        var start = await api.PayStartAsync("tok", request, None);
        var link = await api.PayLinkAsync("tok", start.PaymentId, None);
        var status = await api.PayStatusAsync("tok", start.PaymentId, None);

        Assert.Equal(1490, prices.Pro.Year);
        Assert.Equal(299, prices.Family.Table["2"].Month);
        Assert.Null(fake.To("GET /prices").Single().Bearer);
        Assert.Equal(1341, quote.Amount);
        var quoteBody = Api.Body(fake.To("POST /pay/quote").Single());
        Assert.Equal("pro", quoteBody.GetProperty("plan").GetString());
        Assert.Equal("year", quoteBody.GetProperty("term").GetString());
        Assert.Equal(1, quoteBody.GetProperty("devices").GetInt32());
        Assert.Equal("SALE10", quoteBody.GetProperty("promo").GetString());
        Assert.False(quoteBody.TryGetProperty("change", out _)); // sent only when true
        Assert.Equal(91, start.PaymentId);
        Assert.Equal(91, Api.Body(fake.To("POST /pay/link").Single()).GetProperty("payment_id").GetInt64());
        Assert.NotEqual(start.Url, link.Url);
        Assert.Equal("paid", status.Status);
        Assert.NotNull(status.License);
    }

    [Fact]
    public async Task Support_SendsTheTopicTextVersionAndLog_WithAnOptionalBearer()
    {
        var fake = new FakeApi().On("POST /ticket", 200, "{\"id\":12}");
        using var api = Api.Client(fake);

        await api.SupportAsync(null, new SupportRequest("a@b.c", "bug", "Не открывается окно", "bG9n"), "1.5.0", None);

        var r = fake.Requests.Single();
        Assert.Null(r.Bearer);
        var body = Api.Body(r);
        Assert.Equal("a@b.c", body.GetProperty("email").GetString());
        Assert.Equal("bug", body.GetProperty("topic").GetString());
        Assert.Equal("1.5.0", body.GetProperty("app_version").GetString());
        Assert.Equal("bG9n", body.GetProperty("log").GetString());
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~ApiEndpointsTests"` → ошибка сборки.

- [ ] **Step 3: Методы**

Create `src/PcCleaner/Core/Account/ApiClient.Endpoints.cs`:

```csharp
using System.Net.Http;

namespace PcCleaner.Core.Account;

// The methods of /api/v1 used by the program (the site's docs/api-contract-for-app.md).
public sealed partial class ApiClient
{
    private sealed record Ok(bool? ok);

    private static Dictionary<string, object?> WithDevice(DeviceFields device, params (string Name, object? Value)[] fields)
    {
        var body = new Dictionary<string, object?>();
        foreach (var (name, value) in fields)
            body[name] = value;
        body["hw_hash"] = device.HwHash;
        body["platform"] = device.Platform;
        body["name"] = device.Name;
        body["app_version"] = device.AppVersion;
        return body;
    }

    private static Dictionary<string, object?> Quote(QuoteRequest r)
    {
        var body = new Dictionary<string, object?> { ["plan"] = r.Plan, ["term"] = r.Term, ["devices"] = r.Devices };
        if (!string.IsNullOrEmpty(r.Promo))
            body["promo"] = r.Promo;
        if (r.Change)
            body["change"] = true;
        return body;
    }

    // ----- sign-in -----

    public Task<LoginResult> LoginAsync(string email, string password, DeviceFields device, CancellationToken ct)
        => SendAsync<LoginResult>(HttpMethod.Post, "/auth/login", WithDevice(device, ("email", email), ("password", password)), null, ct);

    public Task RegisterAsync(string email, string password, string lang, DeviceFields device, CancellationToken ct)
        => SendAsync<Ok>(HttpMethod.Post, "/auth/register",
            WithDevice(device, ("email", email), ("password", password), ("consent", true), ("lang", lang)), null, ct);

    public Task<LoginResult> VerifyAsync(string email, string code, DeviceFields device, CancellationToken ct)
        => SendAsync<LoginResult>(HttpMethod.Post, "/auth/verify", WithDevice(device, ("email", email), ("code", code)), null, ct);

    /// <summary>A new code by mail; purpose "verify" or "reset". Always ok: it never tells whether the account exists.</summary>
    public Task<ResendResult> ResendAsync(string email, string purpose, CancellationToken ct)
        => SendAsync<ResendResult>(HttpMethod.Post, "/auth/resend", new { email, purpose }, null, ct);

    public Task ResetAsync(string email, CancellationToken ct)
        => SendAsync<Ok>(HttpMethod.Post, "/auth/reset", new { email }, null, ct);

    public Task<LoginResult> ResetConfirmAsync(string email, string code, string password, DeviceFields device, CancellationToken ct)
        => SendAsync<LoginResult>(HttpMethod.Post, "/auth/reset/confirm",
            WithDevice(device, ("email", email), ("code", code), ("password", password)), null, ct);

    public Task<BrowserCode> StartBrowserCodeAsync(DeviceFields device, CancellationToken ct)
        => SendAsync<BrowserCode>(HttpMethod.Post, "/auth/code", WithDevice(device), null, ct);

    /// <summary>One poll of a browser code: null while the site has not confirmed it yet (202), the sign-in when it has.</summary>
    public async Task<LoginResult?> PollBrowserCodeAsync(string code, CancellationToken ct)
    {
        var (status, text) = await SendRawAsync(HttpMethod.Get, "/auth/code/" + Uri.EscapeDataString(code), null, null, ct);
        if (status == 202)
            return null;
        if (status is >= 200 and < 300)
            return Read<LoginResult>(text, status);
        throw new ApiException(ParseError(status, text));
    }

    // ----- with the device token -----

    public Task LogoutAsync(string token, CancellationToken ct)
        => SendAsync<Ok>(HttpMethod.Post, "/auth/logout", new { }, token, ct);

    public Task<SignedTicket> TicketAsync(string token, CancellationToken ct)
        => SendAsync<SignedTicket>(HttpMethod.Get, "/license/ticket", null, token, ct);

    public Task<DeviceList> DevicesAsync(string token, CancellationToken ct)
        => SendAsync<DeviceList>(HttpMethod.Get, "/devices", null, token, ct);

    /// <summary>Unbinds a computer of the account; without an id — this one (its token dies).</summary>
    public Task UnbindAsync(string token, long? deviceId, CancellationToken ct)
        => SendAsync<Ok>(HttpMethod.Post, "/device/unbind",
            deviceId is { } id ? new Dictionary<string, object?> { ["device_id"] = id } : new Dictionary<string, object?>(), token, ct);

    public Task<SignedTicket> PromoApplyAsync(string token, string code, CancellationToken ct)
        => SendAsync<SignedTicket>(HttpMethod.Post, "/promo/apply", new { code }, token, ct);

    public Task<Quote> QuoteAsync(string token, QuoteRequest request, CancellationToken ct)
        => SendAsync<Quote>(HttpMethod.Post, "/pay/quote", Quote(request), token, ct);

    public Task<PaymentLink> PayStartAsync(string token, QuoteRequest request, CancellationToken ct)
        => SendAsync<PaymentLink>(HttpMethod.Post, "/pay/start", Quote(request), token, ct);

    public Task<PaymentLink> PayLinkAsync(string token, long paymentId, CancellationToken ct)
        => SendAsync<PaymentLink>(HttpMethod.Post, "/pay/link", new { payment_id = paymentId }, token, ct);

    public Task<PaymentStatus> PayStatusAsync(string token, long paymentId, CancellationToken ct)
        => SendAsync<PaymentStatus>(HttpMethod.Get, "/pay/" + paymentId, null, token, ct);

    // ----- without a token -----

    /// <summary>Releases a computer when a sign-in hit DEVICE_LIMIT: the one-time permit of the last DEVICE_LIMIT answer.</summary>
    public Task ReleaseAsync(string unbindToken, long deviceId, CancellationToken ct)
        => SendAsync<Ok>(HttpMethod.Post, "/device/release", new { unbind_token = unbindToken, device_id = deviceId }, null, ct);

    /// <summary>The price list; with the token <c>payments_enabled</c> tells whether this account may buy.</summary>
    public Task<PriceList> PricesAsync(string? token, CancellationToken ct)
        => SendAsync<PriceList>(HttpMethod.Get, "/prices", null, token, ct);

    /// <summary>«Написать нам»; the token (optional) ties the request to the account.</summary>
    public Task SupportAsync(string? token, SupportRequest request, string appVersion, CancellationToken ct)
        => SendAsync<Ok>(HttpMethod.Post, "/ticket", new
        {
            email = request.Email,
            topic = request.Topic,
            message = request.Message,
            app_version = appVersion,
            log = request.LogBase64,
        }, token, ct);
}
```

- [ ] **Step 4: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~PcCleaner.Tests.Account"` → PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Accounts: the API methods of the program

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Билет лицензии и ключи

**Files:**
- Create: `src/PcCleaner/Core/Account/LicenseTicket.cs`, `src/PcCleaner/Core/Account/LicenseKeys.cs`
- Create: `tests/PcCleaner.Tests/Account/Fixtures/license_test_pub.pem` (копия `A:\waterca7.ru\tests\fixtures\license_test_pub.pem`)
- Modify: `src/PcCleaner/PcCleaner.csproj`, `tests/PcCleaner.Tests/PcCleaner.Tests.csproj`
- Test: `tests/PcCleaner.Tests/Account/LicenseTicketTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record LicenseTicketData(int V, long Sub, string Email, string Plan, string Kind, int DevicesMax, DateTimeOffset? ExpiresAt, string HwHash, long DeviceId, DateTimeOffset IssuedAt, DateTimeOffset ValidUntil)`;
  - `public static LicenseTicketData? LicenseTicket.Verify(string? ticketB64, string? signatureB64, IReadOnlyList<string> publicKeysPem)`;
  - `public static IReadOnlyList<string> LicenseKeys.Live`; `public static string LicenseKeys.Fingerprint(string publicKeyPem)` (SHA-256 строчный hex от SubjectPublicKeyInfo); `internal static IReadOnlyList<string> LicenseKeys.Load(Stream?)`.

- [ ] **Step 1: Фикстура и ресурсы**

Скопировать публичный тестовый ключ сервера:

```powershell
New-Item -ItemType Directory -Force tests\PcCleaner.Tests\Account\Fixtures | Out-Null
Copy-Item A:\waterca7.ru\tests\fixtures\license_test_pub.pem tests\PcCleaner.Tests\Account\Fixtures\license_test_pub.pem
```

(закрытый `license_test_key.pem` **не копировать**). В `tests/PcCleaner.Tests/PcCleaner.Tests.csproj`, в `ItemGroup` с `update-signing-public.pem`, добавить:

```xml
    <!-- The server's TEST license key (public half): LicenseKeys must never contain it. -->
    <EmbeddedResource Include="Account\Fixtures\license_test_pub.pem" LogicalName="license_test_pub.pem" />
```

В `src/PcCleaner/PcCleaner.csproj`, в `ItemGroup` с `en.json`, добавить (файл появится в задаче 10; пока его нет, ресурс не встраивается):

```xml
    <!-- The live public license key (Core/Account/LicenseKeys.cs); provided by the owner. -->
    <None Remove="Core\Account\license_pub.pem" />
    <EmbeddedResource Include="Core\Account\license_pub.pem" LogicalName="PcCleaner.Account.license_pub.pem" Condition="Exists('Core\Account\license_pub.pem')" />
```

- [ ] **Step 2: Падающие тесты**

Create `tests/PcCleaner.Tests/Account/LicenseTicketTests.cs`:

```csharp
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PcCleaner.Core.Account;
using Xunit;

namespace PcCleaner.Tests.Account;

public class LicenseTicketTests
{
    private static string ServerTestKey()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("license_test_pub.pem")!;
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public void AGoodTicket_IsRead()
    {
        using var key = new TestLicense();
        var t = key.Sign(TestLicense.Json(plan: "family", devicesMax: 3));

        var data = LicenseTicket.Verify(t.Ticket, t.Signature, key.Keys)!;

        Assert.Equal(1, data.V);
        Assert.Equal(TestLicense.Sub, data.Sub);
        Assert.Equal("family", data.Plan);
        Assert.Equal(3, data.DevicesMax);
        Assert.Equal(TestLicense.Hw, data.HwHash);
        Assert.Equal(TestLicense.DeviceId, data.DeviceId);
        Assert.Equal(TestLicense.Now.AddDays(-1), data.IssuedAt);
        Assert.Equal(TestLicense.Now.AddDays(13), data.ValidUntil);
        Assert.NotNull(data.ExpiresAt);
    }

    [Fact]
    public void AnyKeyOfTheList_MaySign_ButAForeignKeyMayNot()
    {
        using var key = new TestLicense();
        using var other = new TestLicense();
        var t = key.Sign(TestLicense.Json());

        Assert.NotNull(LicenseTicket.Verify(t.Ticket, t.Signature, new[] { other.PublicPem, key.PublicPem }));
        Assert.Null(LicenseTicket.Verify(t.Ticket, t.Signature, other.Keys));
        Assert.Null(LicenseTicket.Verify(t.Ticket, t.Signature, Array.Empty<string>()));
    }

    [Fact]
    public void AP1363Signature_IsRejected()
    {
        using var key = new TestLicense();
        var t = key.Sign(TestLicense.Json(), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.Null(LicenseTicket.Verify(t.Ticket, t.Signature, key.Keys));
    }

    [Fact]
    public void AChangedByte_IsRejected()
    {
        using var key = new TestLicense();
        var t = key.Sign(TestLicense.Json(plan: "free"));
        var bytes = Convert.FromBase64String(t.Ticket);
        var text = Encoding.UTF8.GetString(bytes).Replace("\"free\"", "\"pro_\"");

        Assert.Null(LicenseTicket.Verify(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), t.Signature, key.Keys));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("")]
    [InlineData(null)]
    public void GarbageIsRejected(string? ticket)
    {
        using var key = new TestLicense();
        var t = key.Sign(TestLicense.Json());

        Assert.Null(LicenseTicket.Verify(ticket, t.Signature, key.Keys));
        Assert.Null(LicenseTicket.Verify(t.Ticket, ticket, key.Keys));
    }

    [Fact]
    public void AnotherVersion_IsRejected_EvenWhenSigned()
    {
        using var key = new TestLicense();
        var t = key.Sign(TestLicense.Json(v: 2));

        Assert.Null(LicenseTicket.Verify(t.Ticket, t.Signature, key.Keys));
    }

    [Fact]
    public void ASignedTicketWithoutAField_IsRejected()
    {
        using var key = new TestLicense();
        var t = key.Sign("{\"v\":1,\"sub\":7,\"plan\":\"pro\"}");

        Assert.Null(LicenseTicket.Verify(t.Ticket, t.Signature, key.Keys));
    }

    [Fact]
    public void ALifetimeTicket_HasNoEnd()
    {
        using var key = new TestLicense();
        var t = key.Sign(TestLicense.Json(kind: "lifetime", noExpiry: true));

        Assert.Null(LicenseTicket.Verify(t.Ticket, t.Signature, key.Keys)!.ExpiresAt);
    }

    [Fact]
    public void TheLiveKeys_NeverContainTheServersTestKey()
    {
        var test = LicenseKeys.Fingerprint(ServerTestKey());

        Assert.DoesNotContain(LicenseKeys.Live, k => LicenseKeys.Fingerprint(k) == test);
    }

    [Fact]
    public void ATicketSignedWithTheServersTestKey_IsNotPro_InTheProgram()
    {
        // The server signs test tickets with license_test_key.pem; the program only trusts LicenseKeys.Live.
        Assert.DoesNotContain(ServerTestKey().Trim(), LicenseKeys.Live.Select(k => k.Trim()));
    }

    [Fact]
    public void Load_ReadsEveryPemBlock_AndNothingWithoutAResource()
    {
        using var a = new TestLicense();
        using var b = new TestLicense();
        var pem = a.PublicPem + "\n" + b.PublicPem;

        var keys = LicenseKeys.Load(new MemoryStream(Encoding.ASCII.GetBytes(pem)));

        Assert.Equal(new[] { LicenseKeys.Fingerprint(a.PublicPem), LicenseKeys.Fingerprint(b.PublicPem) }, keys.Select(LicenseKeys.Fingerprint));
        Assert.Empty(LicenseKeys.Load(null));
    }

    [Fact]
    public void Fingerprint_IsTheSameForTheSameKey_InAnyLineEndings()
    {
        using var key = new TestLicense();

        Assert.Equal(LicenseKeys.Fingerprint(key.PublicPem), LicenseKeys.Fingerprint(key.PublicPem.Replace("\n", "\r\n")));
        Assert.Matches("^[0-9a-f]{64}$", LicenseKeys.Fingerprint(key.PublicPem));
    }
}
```

- [ ] **Step 3: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~LicenseTicketTests"` → ошибка сборки.

- [ ] **Step 4: `LicenseTicket`**

Create `src/PcCleaner/Core/Account/LicenseTicket.cs`:

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace PcCleaner.Core.Account;

/// <summary>The fields of a license ticket (the site's License/Ticket.php), read only after its signature was checked.</summary>
public sealed record LicenseTicketData(int V, long Sub, string Email, string Plan, string Kind, int DevicesMax, DateTimeOffset? ExpiresAt,
    string HwHash, long DeviceId, DateTimeOffset IssuedAt, DateTimeOffset ValidUntil);

/// <summary>
/// Checks a ticket the way the contract says: base64 → bytes → ECDSA P-256 / SHA-256 signature in DER over exactly these bytes by
/// any of the keys → only then the JSON, which must say <c>v: 1</c> and have every field. Anything else is null: an unsigned,
/// foreign, changed or broken ticket is no ticket.
/// </summary>
public static class LicenseTicket
{
    private const string P256 = "1.2.840.10045.3.1.7";

    public static LicenseTicketData? Verify(string? ticketB64, string? signatureB64, IReadOnlyList<string> publicKeysPem)
    {
        if (string.IsNullOrEmpty(ticketB64) || string.IsNullOrEmpty(signatureB64))
            return null;
        byte[] bytes, signature;
        try
        {
            bytes = Convert.FromBase64String(ticketB64);
            signature = Convert.FromBase64String(signatureB64);
        }
        catch (FormatException)
        {
            return null;
        }
        return publicKeysPem.Any(pem => SignedBy(pem, bytes, signature)) ? Parse(bytes) : null;
    }

    private static bool SignedBy(string pem, byte[] data, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            return key.ExportParameters(false).Curve.Oid.Value == P256 &&
                   key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    internal static LicenseTicketData? Parse(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.GetProperty("v").GetInt32() != 1)
                return null;
            var expires = r.GetProperty("expires_at");
            return new LicenseTicketData(
                1,
                r.GetProperty("sub").GetInt64(),
                r.GetProperty("email").GetString() ?? "",
                r.GetProperty("plan").GetString() ?? "",
                r.GetProperty("kind").GetString() ?? "",
                r.GetProperty("devices_max").GetInt32(),
                expires.ValueKind == JsonValueKind.Null ? null : Date(expires.GetString()),
                r.GetProperty("hw_hash").GetString() ?? "",
                r.GetProperty("device_id").GetInt64(),
                Date(r.GetProperty("issued_at").GetString()),
                Date(r.GetProperty("valid_until").GetString()));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static DateTimeOffset Date(string? iso)
        => DateTimeOffset.Parse(iso ?? throw new FormatException("no date"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
```

- [ ] **Step 5: `LicenseKeys`**

Create `src/PcCleaner/Core/Account/LicenseKeys.cs`:

```csharp
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PcCleaner.Core.Account;

/// <summary>
/// The public keys a license ticket may be signed with: the live key of waterca7.ru (Core/Account/license_pub.pem, embedded),
/// and a second one only while the server's key is being changed. Never the server's test key (a test checks the fingerprint).
/// A build without the file trusts no ticket: everyone stays free.
/// </summary>
public static class LicenseKeys
{
    internal const string ResourceName = "PcCleaner.Account.license_pub.pem";

    private static readonly Regex PemBlock = new("-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Live { get; } = Load(typeof(LicenseKeys).Assembly.GetManifestResourceStream(ResourceName));

    /// <summary>Every PEM public key block of the stream (null — no resource: none).</summary>
    internal static IReadOnlyList<string> Load(Stream? stream)
    {
        if (stream is null)
            return Array.Empty<string>();
        using (stream)
        {
            var text = new StreamReader(stream).ReadToEnd();
            return PemBlock.Matches(text).Select(m => m.Value).ToList();
        }
    }

    /// <summary>SHA-256 (lowercase hex) of the key's SubjectPublicKeyInfo: the same for any formatting of the PEM.</summary>
    public static string Fingerprint(string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }
}
```

- [ ] **Step 6: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~LicenseTicketTests"` → PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Accounts: license ticket verification (P-256, DER) and the live key list

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Уровень Гость / Free / Pro

**Files:**
- Create: `src/PcCleaner/Core/Account/LicenseState.cs`
- Test: `tests/PcCleaner.Tests/Account/LicenseStateTests.cs`

**Interfaces:**
- Consumes: `LicenseTicketData` (задача 3).
- Produces:
  - `public enum Tier { Guest, Free, Pro }`;
  - `public enum LicenseReason { None, NotSignedIn, NoTicket, WrongDevice, ClockBack, Stale, NoPlan, Expired }`;
  - `public sealed record LicenseStatus(Tier Tier, LicenseReason Reason, string? Plan, string? Kind, int DevicesMax, DateTimeOffset? ExpiresAt, DateTimeOffset? IssuedAt, DateTimeOffset? ValidUntil, int? OfflineDaysLeft)`;
  - `public static LicenseStatus LicenseState.Evaluate(LicenseTicketData? ticket, bool signedIn, string hwHash, long? deviceId, long? accountId, DateTimeOffset now)`;
  - `public static readonly IReadOnlySet<string> LicenseState.ProPlans` = `{ "pro", "family", "custom" }`.

Порядок причин: не вошёл → `NotSignedIn` (Гость); нет билета → `NoTicket`; чужие `hw_hash`/`device_id`/`sub` → `WrongDevice`; `now < issued_at` → `ClockBack`; `now ≥ valid_until` → `Stale`; план не из `ProPlans` → `NoPlan`; `expires_at` прошёл → `Expired` (всё это — Free); иначе Pro с `None`. Поля плана и дат заполняются из билета, если он есть (Профиль показывает тариф и при Free). `OfflineDaysLeft` — только у Pro: целое вверх от `(valid_until − now)` в днях.

- [ ] **Step 1: Падающие тесты**

Create `tests/PcCleaner.Tests/Account/LicenseStateTests.cs`:

```csharp
using System.Text;
using PcCleaner.Core.Account;
using Xunit;

namespace PcCleaner.Tests.Account;

public class LicenseStateTests
{
    private static readonly DateTimeOffset Now = TestLicense.Now;

    private static LicenseTicketData Ticket(string json) => LicenseTicket.Parse(Encoding.UTF8.GetBytes(json))!;

    private static LicenseStatus Evaluate(string json, DateTimeOffset? now = null, string hw = TestLicense.Hw, long? device = TestLicense.DeviceId,
        long? account = TestLicense.Sub)
        => LicenseState.Evaluate(Ticket(json), true, hw, device, account, now ?? Now);

    [Fact]
    public void NotSignedIn_IsAGuest_WhateverTheTicket()
    {
        var status = LicenseState.Evaluate(Ticket(TestLicense.Json()), false, TestLicense.Hw, TestLicense.DeviceId, TestLicense.Sub, Now);

        Assert.Equal(Tier.Guest, status.Tier);
        Assert.Equal(LicenseReason.NotSignedIn, status.Reason);
    }

    [Fact]
    public void SignedInWithoutATicket_IsFree()
    {
        var status = LicenseState.Evaluate(null, true, TestLicense.Hw, TestLicense.DeviceId, TestLicense.Sub, Now);

        Assert.Equal(Tier.Free, status.Tier);
        Assert.Equal(LicenseReason.NoTicket, status.Reason);
    }

    [Theory]
    [InlineData("pro")]
    [InlineData("family")]
    [InlineData("custom")]
    public void APaidPlan_WithAFreshTicketOfThisComputer_IsPro(string plan)
    {
        var status = Evaluate(TestLicense.Json(plan: plan));

        Assert.Equal(Tier.Pro, status.Tier);
        Assert.Equal(LicenseReason.None, status.Reason);
        Assert.Equal(plan, status.Plan);
        Assert.Equal(13, status.OfflineDaysLeft);
    }

    [Fact]
    public void TheFreePlan_IsFree_ButKeepsItsDates()
    {
        var status = Evaluate(TestLicense.Json(plan: "free", kind: "none", noExpiry: true, devicesMax: 0));

        Assert.Equal(Tier.Free, status.Tier);
        Assert.Equal(LicenseReason.NoPlan, status.Reason);
        Assert.Equal("free", status.Plan);
        Assert.NotNull(status.ValidUntil);
        Assert.Null(status.OfflineDaysLeft);
    }

    [Fact]
    public void AnotherComputersTicket_IsFree()
    {
        Assert.Equal(LicenseReason.WrongDevice, Evaluate(TestLicense.Json(), hw: new string('f', 64)).Reason);
        Assert.Equal(LicenseReason.WrongDevice, Evaluate(TestLicense.Json(), device: 43).Reason);
        Assert.Equal(LicenseReason.WrongDevice, Evaluate(TestLicense.Json(), device: null).Reason);
        Assert.Equal(LicenseReason.WrongDevice, Evaluate(TestLicense.Json(), account: 8).Reason);
    }

    [Fact]
    public void TheHwHash_IsComparedIgnoringCase()
        => Assert.Equal(Tier.Pro, Evaluate(TestLicense.Json(hw: TestLicense.Hw.ToUpperInvariant())).Tier);

    [Fact]
    public void ClockTurnedBack_IsFree()
    {
        var status = Evaluate(TestLicense.Json(issued: Now.AddMinutes(5)));

        Assert.Equal(Tier.Free, status.Tier);
        Assert.Equal(LicenseReason.ClockBack, status.Reason);
    }

    [Fact]
    public void AStaleTicket_IsFree_FromTheMomentOfValidUntil()
    {
        var json = TestLicense.Json(issued: Now.AddDays(-14), validUntil: Now);

        Assert.Equal(LicenseReason.Stale, Evaluate(json).Reason);
        Assert.Equal(Tier.Pro, Evaluate(json, Now.AddSeconds(-1)).Tier);
    }

    [Fact]
    public void AnEndedSubscription_IsFree_FromTheMomentOfExpiresAt()
    {
        var json = TestLicense.Json(expires: Now);

        Assert.Equal(LicenseReason.Expired, Evaluate(json).Reason);
        Assert.Equal(Tier.Pro, Evaluate(json, Now.AddSeconds(-1)).Tier);
    }

    [Fact]
    public void ALifetimeLicense_NeverExpires_ButStillNeedsAFreshTicket()
    {
        var json = TestLicense.Json(kind: "lifetime", noExpiry: true);

        Assert.Equal(Tier.Pro, Evaluate(json).Tier);
        Assert.Equal(LicenseReason.Stale, Evaluate(json, Now.AddDays(30)).Reason);
    }

    [Fact]
    public void OfflineDays_AreRoundedUp()
    {
        var json = TestLicense.Json(issued: Now.AddDays(-13).AddHours(-1), validUntil: Now.AddHours(23));

        Assert.Equal(1, Evaluate(json).OfflineDaysLeft);
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~LicenseStateTests"` → ошибка сборки.

- [ ] **Step 3: `LicenseState`**

Create `src/PcCleaner/Core/Account/LicenseState.cs`:

```csharp
namespace PcCleaner.Core.Account;

/// <summary>What the program may do: a guest and a free account see exactly the same; Pro unlocks the Pro features.</summary>
public enum Tier
{
    Guest,
    Free,
    Pro,
}

/// <summary>Why the tier is what it is (Профиль shows a line for some of them).</summary>
public enum LicenseReason
{
    None,
    NotSignedIn,
    NoTicket,
    WrongDevice,
    ClockBack,
    Stale,
    NoPlan,
    Expired,
}

/// <summary>The tier and what Профиль shows about the license (from the ticket, when there is one).</summary>
public sealed record LicenseStatus(Tier Tier, LicenseReason Reason, string? Plan, string? Kind, int DevicesMax, DateTimeOffset? ExpiresAt,
    DateTimeOffset? IssuedAt, DateTimeOffset? ValidUntil, int? OfflineDaysLeft);

/// <summary>The rule of the spec (section 2), as a pure function.</summary>
public static class LicenseState
{
    public static readonly IReadOnlySet<string> ProPlans = new HashSet<string>(StringComparer.Ordinal) { "pro", "family", "custom" };

    public static LicenseStatus Evaluate(LicenseTicketData? ticket, bool signedIn, string hwHash, long? deviceId, long? accountId, DateTimeOffset now)
    {
        if (!signedIn)
            return new LicenseStatus(Tier.Guest, LicenseReason.NotSignedIn, null, null, 0, null, null, null, null);
        if (ticket is null)
            return new LicenseStatus(Tier.Free, LicenseReason.NoTicket, null, null, 0, null, null, null, null);

        LicenseStatus Free(LicenseReason reason)
            => new(Tier.Free, reason, ticket.Plan, ticket.Kind, ticket.DevicesMax, ticket.ExpiresAt, ticket.IssuedAt, ticket.ValidUntil, null);

        if (!string.Equals(ticket.HwHash, hwHash, StringComparison.OrdinalIgnoreCase) || ticket.DeviceId != deviceId || ticket.Sub != accountId)
            return Free(LicenseReason.WrongDevice);
        if (now < ticket.IssuedAt)
            return Free(LicenseReason.ClockBack);
        if (now >= ticket.ValidUntil)
            return Free(LicenseReason.Stale);
        if (!ProPlans.Contains(ticket.Plan))
            return Free(LicenseReason.NoPlan);
        if (ticket.ExpiresAt is { } end && now >= end)
            return Free(LicenseReason.Expired);

        var offline = (int)Math.Ceiling((ticket.ValidUntil - now).TotalDays);
        return new LicenseStatus(Tier.Pro, LicenseReason.None, ticket.Plan, ticket.Kind, ticket.DevicesMax, ticket.ExpiresAt, ticket.IssuedAt,
            ticket.ValidUntil, offline);
    }
}
```

- [ ] **Step 4: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~LicenseStateTests"` → PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Accounts: Guest / Free / Pro from the ticket

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Таблица функций

**Files:**
- Create: `src/PcCleaner/Core/Account/Features.cs`
- Create: `tests/PcCleaner.Tests/Account/Fixtures/features.php` (копия `A:\waterca7.ru\app\data\features.php`)
- Modify: `tests/PcCleaner.Tests/PcCleaner.Tests.csproj`
- Test: `tests/PcCleaner.Tests/Account/FeaturesTests.cs`

**Interfaces:**
- Consumes: `Tier` (задача 4).
- Produces: `public enum FeatureLevel { Free, Pro }`; `public sealed record Feature(string Key, FeatureLevel Level, bool OnWindows, bool OnMac)`; `public static class Features` с константами ключей (`HomeFull = "f.home.full"` и т. д. для всех ключей с `OnWindows || OnMac`), `IReadOnlyList<Feature> All`, `Feature Get(string key)` (неизвестный ключ — `KeyNotFoundException`), `bool IsAvailable(string key, Tier tier)`.

Флаги платформ (из исследования модулей, обзор плана B): «есть на Windows» = `true` у всех ключей, у которых в `features.php` третий столбец `true`; «есть на Mac» = так же, кроме `f.clean.dism`, `f.comp.specs`, `f.comp.speed` (на Mac раздела «Компьютер» и DISM нет). Ключи с `false` в `features.php` (расписание, история, обновления программ, дубликаты, проверка файлов, Wi-Fi, загрузки, утечки, телефон) — `false` на обеих.

- [ ] **Step 1: Фикстура**

```powershell
Copy-Item A:\waterca7.ru\app\data\features.php tests\PcCleaner.Tests\Account\Fixtures\features.php
```

В тестовый csproj, рядом с ключом из задачи 3:

```xml
    <!-- A copy of the site's app/data/features.php: Features must list the same keys (update both when the site's table changes). -->
    <EmbeddedResource Include="Account\Fixtures\features.php" LogicalName="features.php" />
```

- [ ] **Step 2: Падающие тесты**

Create `tests/PcCleaner.Tests/Account/FeaturesTests.cs`:

```csharp
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using PcCleaner.Core.Account;
using Xunit;

namespace PcCleaner.Tests.Account;

public class FeaturesTests
{
    /// <summary>[key, level, in the program now] of the site's table.</summary>
    private static List<(string Key, string Level, bool Now)> SiteTable()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("features.php")!;
        var php = new StreamReader(stream).ReadToEnd();
        return Regex.Matches(php, @"\['(f\.[a-z_.]+)',\s*'(free|pro)',\s*(true|false)\]")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value == "true"))
            .ToList();
    }

    [Fact]
    public void TheKeys_AreTheSiteTable_InItsOrder_WithTheSameLevels()
    {
        var site = SiteTable();

        Assert.NotEmpty(site);
        Assert.Equal(site.Select(s => s.Key), Features.All.Select(f => f.Key));
        Assert.Equal(site.Select(s => s.Level), Features.All.Select(f => f.Level == FeatureLevel.Pro ? "pro" : "free"));
    }

    [Fact]
    public void AFeatureNotInTheProgramYet_IsOnNoPlatform()
    {
        foreach (var (key, _, now) in SiteTable().Where(s => !s.Now))
        {
            var f = Features.Get(key);
            Assert.False(f.OnWindows, key);
            Assert.False(f.OnMac, key);
        }
    }

    [Fact]
    public void TheMac_HasNoDismAndNoComputerSection()
    {
        Assert.False(Features.Get(Features.CleanDism).OnMac);
        Assert.False(Features.Get(Features.CompSpecs).OnMac);
        Assert.False(Features.Get(Features.CompSpeed).OnMac);
        Assert.True(Features.Get(Features.ProtNet).OnMac);
        Assert.True(Features.Get(Features.CleanDism).OnWindows);
    }

    [Theory]
    [InlineData(Tier.Guest, false)]
    [InlineData(Tier.Free, false)]
    [InlineData(Tier.Pro, true)]
    public void EveryProKey_IsOpenOnlyForPro_AndEveryFreeKeyForEveryone(Tier tier, bool pro)
    {
        foreach (var f in Features.All)
            Assert.Equal(f.Level == FeatureLevel.Free || pro, Features.IsAvailable(f.Key, tier));
    }

    [Fact]
    public void AnUnknownKey_IsAnError()
        => Assert.Throws<KeyNotFoundException>(() => Features.IsAvailable("f.nope", Tier.Pro));

    [Fact]
    public void TheLockedKeysOfTheSpec_ArePro()
    {
        var keys = new[]
        {
            Features.HomeFull, Features.CleanDeep, Features.CleanDism, Features.SysProcDeep, Features.SysStartupDeep, Features.SysBloat,
            Features.SysDiskDeep, Features.ProtDeep, Features.CompSpeed, Features.ProtNet,
        };
        Assert.All(keys, k => Assert.Equal(FeatureLevel.Pro, Features.Get(k).Level));
    }
}
```

- [ ] **Step 3: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~FeaturesTests"` → ошибка сборки.

- [ ] **Step 4: `Features`**

Create `src/PcCleaner/Core/Account/Features.cs`:

```csharp
namespace PcCleaner.Core.Account;

public enum FeatureLevel
{
    Free,
    Pro,
}

/// <summary>A row of the site's feature table (app/data/features.php) and whether this program has it on each platform.</summary>
public sealed record Feature(string Key, FeatureLevel Level, bool OnWindows, bool OnMac);

/// <summary>
/// The keys of the site's feature table, in its order (a test compares them with a copy of features.php). A guest and a free
/// account get the free ones; Pro gets everything. The program asks <see cref="IsAvailable"/> before it runs a Pro check.
/// </summary>
public static class Features
{
    public const string HomeScore = "f.home.score";
    public const string HomeQuick = "f.home.quick";
    public const string HomeUndo = "f.home.undo";
    public const string HomeFull = "f.home.full";
    public const string CleanBasic = "f.clean.basic";
    public const string CleanDeep = "f.clean.deep";
    public const string CleanDism = "f.clean.dism";
    public const string CompSpecs = "f.comp.specs";
    public const string CompSpeed = "f.comp.speed";
    public const string SysProc = "f.sys.proc";
    public const string SysProcDeep = "f.sys.proc_deep";
    public const string SysStartup = "f.sys.startup";
    public const string SysStartupDeep = "f.sys.startup_deep";
    public const string SysPrograms = "f.sys.programs";
    public const string SysBloat = "f.sys.bloat";
    public const string SysDisks = "f.sys.disks";
    public const string SysDiskDeep = "f.sys.disk_deep";
    public const string ProtBasic = "f.prot.basic";
    public const string ProtDeep = "f.prot.deep";
    public const string ProtNet = "f.prot.net";

    private static Feature Both(string key, FeatureLevel level) => new(key, level, true, true);

    private static Feature WindowsOnly(string key, FeatureLevel level) => new(key, level, true, false);

    private static Feature Later(string key, FeatureLevel level) => new(key, level, false, false);

    public static IReadOnlyList<Feature> All { get; } = new[]
    {
        Both(HomeScore, FeatureLevel.Free),
        Both(HomeQuick, FeatureLevel.Free),
        Both(HomeUndo, FeatureLevel.Free),
        Both(HomeFull, FeatureLevel.Pro),
        Later("f.home.schedule", FeatureLevel.Pro),
        Both(CleanBasic, FeatureLevel.Free),
        Both(CleanDeep, FeatureLevel.Pro),
        WindowsOnly(CleanDism, FeatureLevel.Pro),
        Later("f.clean.schedule", FeatureLevel.Pro),
        WindowsOnly(CompSpecs, FeatureLevel.Free),
        WindowsOnly(CompSpeed, FeatureLevel.Pro),
        Later("f.comp.history", FeatureLevel.Pro),
        Both(SysProc, FeatureLevel.Free),
        Both(SysProcDeep, FeatureLevel.Pro),
        Both(SysStartup, FeatureLevel.Free),
        Both(SysStartupDeep, FeatureLevel.Pro),
        Both(SysPrograms, FeatureLevel.Free),
        Both(SysBloat, FeatureLevel.Pro),
        Later("f.sys.updates", FeatureLevel.Pro),
        Both(SysDisks, FeatureLevel.Free),
        Both(SysDiskDeep, FeatureLevel.Pro),
        Later("f.sys.dupes", FeatureLevel.Pro),
        Both(ProtBasic, FeatureLevel.Free),
        Both(ProtDeep, FeatureLevel.Pro),
        Both(ProtNet, FeatureLevel.Pro),
        Later("f.prot.files", FeatureLevel.Free),
        Later("f.prot.wifi", FeatureLevel.Pro),
        Later("f.prot.downloads", FeatureLevel.Pro),
        Later("f.prot.leaks", FeatureLevel.Pro),
        Later("f.phone.view", FeatureLevel.Free),
        Later("f.phone.backup", FeatureLevel.Pro),
    };

    private static readonly Dictionary<string, Feature> ByKey = All.ToDictionary(f => f.Key, StringComparer.Ordinal);

    public static Feature Get(string key)
        => ByKey.TryGetValue(key, out var feature) ? feature : throw new KeyNotFoundException("Unknown feature key: " + key);

    public static bool IsAvailable(string key, Tier tier) => Get(key).Level == FeatureLevel.Free || tier == Tier.Pro;
}
```

- [ ] **Step 5: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~FeaturesTests"` → PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Accounts: the feature table of the site, per platform

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Номер компьютера

**Files:**
- Create: `src/PcCleaner/Core/Account/HwHash.cs`, `src/PcCleaner/Core/AccountWindows/WmiHwHash.cs`
- Test: `tests/PcCleaner.Tests/Account/HwHashTests.cs`

**Interfaces:**
- Produces: `public interface IHwHash { string Value { get; } }`; `public static string HwHash.Of(params string?[] parts)` (части через «|», пустая → «-», SHA-256 строчный hex); `public sealed class WmiHwHash : IHwHash` (`PcCleaner.Core.AccountWindows`): `serial платы|ProcessorId|serial системного диска`, вычисляется один раз, ошибки WMI дают «-» у своей части.

- [ ] **Step 1: Падающие тесты**

Create `tests/PcCleaner.Tests/Account/HwHashTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using PcCleaner.Core.Account;
using PcCleaner.Core.AccountWindows;
using Xunit;

namespace PcCleaner.Tests.Account;

public class HwHashTests
{
    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public void ThePartsAreJoinedByABar_AndHashed()
        => Assert.Equal(Sha("BOARD1|BFEBFBFF000906EA|S3EVNX0K"), HwHash.Of("BOARD1", "BFEBFBFF000906EA", "S3EVNX0K"));

    [Fact]
    public void AnEmptyPart_IsADash_AndSpacesAreTrimmed()
        => Assert.Equal(Sha("-|CPU|-"), HwHash.Of("  ", " CPU ", null));

    [Fact]
    public void TheMacForm_IsTheSerialAlone()
        => Assert.Equal(Sha("C02XK0AAJGH5"), HwHash.Of("C02XK0AAJGH5"));

    [Fact]
    public void ThisPc_HasAStableHash()
    {
        // Read-only WMI queries of this machine.
        var a = new WmiHwHash().Value;
        var b = new WmiHwHash().Value;

        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.Equal(a, b);
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~HwHashTests"` → ошибка сборки.

- [ ] **Step 3: Код**

Create `src/PcCleaner/Core/Account/HwHash.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace PcCleaner.Core.Account;

/// <summary>This computer's number for the license (<c>hw_hash</c>); the platform says what goes into it.</summary>
public interface IHwHash
{
    string Value { get; }
}

public static class HwHash
{
    /// <summary>SHA-256 (lowercase hex) of the parts joined by «|»; an empty part is «-». Windows: board serial|ProcessorId|system disk serial; Mac: the serial.</summary>
    public static string Of(params string?[] parts)
    {
        var text = string.Join("|", parts.Select(p => string.IsNullOrWhiteSpace(p) ? "-" : p.Trim()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
```

Create `src/PcCleaner/Core/AccountWindows/WmiHwHash.cs`:

```csharp
using System.Management;
using PcCleaner.Core.Account;

namespace PcCleaner.Core.AccountWindows;

/// <summary>
/// Windows: <c>serial of the board | ProcessorId | serial of the disk with the Windows folder</c> (contract «Номер компьютера»),
/// read once by WMI (it takes up to a second: the first use should be off the UI thread). A part WMI cannot give is «-».
/// </summary>
public sealed class WmiHwHash : IHwHash
{
    private readonly Lazy<string> _value = new(Compute, LazyThreadSafetyMode.ExecutionAndPublication);

    public string Value => _value.Value;

    private static string Compute() => HwHash.Of(
        First("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber"),
        First("SELECT ProcessorId FROM Win32_Processor", "ProcessorId"),
        SystemDiskSerial());

    private static string? First(string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using (o)
                {
                    if (o[property]?.ToString() is { Length: > 0 } value)
                        return value;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // the part is «-»
        }
        return null;
    }

    private static string? SystemDiskSerial()
    {
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\') ?? "C:";
        try
        {
            using var partitions = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{drive}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
            foreach (ManagementBaseObject partition in partitions.Get())
            {
                using (partition)
                {
                    using var disks = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                    foreach (ManagementBaseObject disk in disks.Get())
                    {
                        using (disk)
                        {
                            if (disk["SerialNumber"]?.ToString() is { Length: > 0 } serial)
                                return serial;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // the part is «-»
        }
        return null;
    }
}
```

(`using System.IO;` — если `Path` не подхватился неявными `using`.)

- [ ] **Step 4: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~HwHashTests"` → PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Accounts: the computer's number (hw_hash) from WMI

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Хранилище входа

**Files:**
- Create: `src/PcCleaner/Core/Account/AccountStore.cs`, `src/PcCleaner/Core/AccountWindows/DpapiAccountStore.cs`
- Create: `tests/PcCleaner.Tests/Account/AccountTestKit.Store.cs` (перенести сюда `MemoryAccountStore` и `FixedHwHash` из шага 1 задачи 1)
- Modify: `src/PcCleaner/PcCleaner.csproj` (пакет)
- Test: `tests/PcCleaner.Tests/Account/DpapiAccountStoreTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record AccountData(string? Token = null, string? Email = null, long? DeviceId = null, long? AccountId = null, string? Ticket = null, string? Signature = null, string? InstallId = null)` с `AccountData SignedOut()` (оставляет только `InstallId`);
  - `public interface IAccountStore { AccountData Load(); void Save(AccountData data); }`;
  - `public sealed class DpapiAccountStore : IAccountStore` (`PcCleaner.Core.AccountWindows`), конструктор `(string? file = null)` — по умолчанию `Path.Combine(AppPaths.DataDir, "account.bin")`; DPAPI `CurrentUser` с собственной энтропией; повреждённый или чужой файл читается как пустой `AccountData`.

- [ ] **Step 1: Пакет**

В `src/PcCleaner/PcCleaner.csproj`, в `ItemGroup` с пакетами:

```xml
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="7.0.1" />
```

- [ ] **Step 2: Перенести помощники тестов**

Create `tests/PcCleaner.Tests/Account/AccountTestKit.Store.cs` (`IHwHash` уже есть с задачи 6, `IAccountStore` появится в шаге 5 этой задачи):

```csharp
using PcCleaner.Core.Account;

namespace PcCleaner.Tests.Account;

/// <summary>The account store in memory.</summary>
internal sealed class MemoryAccountStore : IAccountStore
{
    public AccountData Data { get; set; } = new();

    public int Saves { get; private set; }

    public AccountData Load() => Data;

    public void Save(AccountData data)
    {
        Data = data;
        Saves++;
    }
}

internal sealed class FixedHwHash : IHwHash
{
    public string Value { get; init; } = TestLicense.Hw;
}

/// <summary>An IProgress that reports at once, on the caller's thread (Progress&lt;T&gt; posts later).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
```

- [ ] **Step 3: Падающие тесты**

Create `tests/PcCleaner.Tests/Account/DpapiAccountStoreTests.cs`:

```csharp
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PcCleaner.Core.Account;
using PcCleaner.Core.AccountWindows;
using Xunit;

namespace PcCleaner.Tests.Account;

public class DpapiAccountStoreTests
{
    private static string NewFile() => Path.Combine(TestEnvironment.NewTempDir("account"), "account.bin");

    private static readonly AccountData Full = new("tok-123", "user@example.com", 42, 7, "dGlja2V0", "c2ln", "11111111-2222-3333-4444-555555555555");

    [Fact]
    public void WhatIsSaved_IsReadBack()
    {
        TestEnvironment.Init();
        var file = NewFile();

        new DpapiAccountStore(file).Save(Full);

        Assert.Equal(Full, new DpapiAccountStore(file).Load());
    }

    [Fact]
    public void TheFile_IsNotPlainText()
    {
        TestEnvironment.Init();
        var file = NewFile();

        new DpapiAccountStore(file).Save(Full);
        var raw = File.ReadAllBytes(file);

        Assert.DoesNotContain("tok-123", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("tok-123", Encoding.Unicode.GetString(raw));
    }

    [Fact]
    public void TheFile_IsProtectedForThisUser_WithTheStoresOwnEntropy()
    {
        TestEnvironment.Init();
        var file = NewFile();
        new DpapiAccountStore(file).Save(Full);
        var raw = File.ReadAllBytes(file);

        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser));
        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(raw, DpapiAccountStore.Entropy, DataProtectionScope.LocalMachine));
        Assert.NotEmpty(ProtectedData.Unprotect(raw, DpapiAccountStore.Entropy, DataProtectionScope.CurrentUser));
    }

    [Fact]
    public void NoFile_AndABrokenFile_AreEmpty()
    {
        TestEnvironment.Init();
        var file = NewFile();

        Assert.Equal(new AccountData(), new DpapiAccountStore(file).Load());
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        Assert.Equal(new AccountData(), new DpapiAccountStore(file).Load());
    }

    [Fact]
    public void SigningOut_KeepsOnlyTheInstallNumber()
        => Assert.Equal(new AccountData(InstallId: Full.InstallId), Full.SignedOut());
}
```

- [ ] **Step 4: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~DpapiAccountStoreTests"` → ошибка сборки.

- [ ] **Step 5: Код**

Create `src/PcCleaner/Core/Account/AccountStore.cs`:

```csharp
namespace PcCleaner.Core.Account;

/// <summary>
/// What a sign-in leaves on this computer: the device token, the account's e-mail, the device and account numbers, the last
/// ticket with its signature — and the install number of the update statistics, which outlives signing out.
/// </summary>
public sealed record AccountData(string? Token = null, string? Email = null, long? DeviceId = null, long? AccountId = null,
    string? Ticket = null, string? Signature = null, string? InstallId = null)
{
    /// <summary>The data after signing out or TOKEN_REVOKED: only the install number stays.</summary>
    public AccountData SignedOut() => new(InstallId: InstallId);
}

/// <summary>Where <see cref="AccountData"/> is kept: Windows — a DPAPI file of this user; Mac — the Keychain.</summary>
public interface IAccountStore
{
    /// <summary>The saved data; empty when there is none or it cannot be read.</summary>
    AccountData Load();

    void Save(AccountData data);
}
```

Create `src/PcCleaner/Core/AccountWindows/DpapiAccountStore.cs`:

```csharp
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core.AccountWindows;

/// <summary>
/// %LOCALAPPDATA%\WaterCa7sPcCleaner\account.bin: the JSON of <see cref="AccountData"/> encrypted by DPAPI for the current user
/// (another account of the PC and another PC cannot read it). Written through a temp file; a file that cannot be read is empty.
/// </summary>
public sealed class DpapiAccountStore : IAccountStore
{
    internal static readonly byte[] Entropy = Encoding.ASCII.GetBytes("WaterCa7sPcCleaner.account.v1");

    private readonly string _file;
    private readonly object _lock = new();

    public DpapiAccountStore(string? file = null) => _file = file ?? Path.Combine(AppPaths.DataDir, "account.bin");

    public AccountData Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_file))
                    return new AccountData();
                var json = ProtectedData.Unprotect(File.ReadAllBytes(_file), Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<AccountData>(json) ?? new AccountData();
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Warn(T.F("Аккаунт: сохранённый вход не прочитан ({0}) — нужно войти снова", ex.GetType().Name));
                return new AccountData();
            }
        }
    }

    public void Save(AccountData data)
    {
        lock (_lock)
        {
            AppPaths.EnsureDir(Path.GetDirectoryName(_file)!);
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(data), Entropy, DataProtectionScope.CurrentUser);
            var temp = _file + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _file, overwrite: true);
        }
    }
}
```

`en.json`: `"Аккаунт: сохранённый вход не прочитан ({0}) — нужно войти снова": "Account: the saved sign-in could not be read ({0}) — you need to sign in again",`.

- [ ] **Step 6: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~DpapiAccountStoreTests|FullyQualifiedName~LocalizationCatalogTests"` → PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Accounts: the sign-in store (DPAPI, this user only)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: `AccountService` — вход, выход, устройства, промокод, обращение

**Files:**
- Create: `src/PcCleaner/Core/Account/AccountService.cs`
- Modify: `src/PcCleaner/Core/Localization/en.json`
- Test: `tests/PcCleaner.Tests/Account/AccountServiceTests.cs`

**Interfaces:**
- Consumes: `ApiClient` (задачи 1–2), `LicenseTicket`, `LicenseState`, `Features`, `IHwHash`, `IAccountStore`, `AccountData`.
- Produces: `public sealed partial class AccountService`:
  - конструктор `public AccountService(ApiClient api, IAccountStore store, IHwHash hw, string platform, string deviceName, string appVersion, IReadOnlyList<string> licenseKeys, Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)`;
  - `event Action? Changed` (может прийти из фонового потока — экран сам переходит в поток окна);
  - `bool IsSignedIn`, `string? Email`, `LicenseStatus Status`, `Tier Tier`, `bool IsAvailable(string featureKey)`, `bool SessionEnded` (в этом запуске был `TOKEN_REVOKED`), `DeviceFields Device`;
  - `Task LoginAsync(string email, string password, CancellationToken ct)`, `Task RegisterAsync(string email, string password, string lang, CancellationToken ct)`, `Task VerifyAsync(string email, string code, CancellationToken ct)`, `Task<int> ResendAsync(string email, string purpose, CancellationToken ct)` (секунды до «Отправить ещё раз»), `Task ResetAsync(string email, CancellationToken ct)`, `Task ResetConfirmAsync(string email, string code, string password, CancellationToken ct)`, `Task ReleaseAsync(string unbindToken, long deviceId, CancellationToken ct)`;
  - `Task LogoutAsync(CancellationToken ct)`, `Task<DeviceList> DevicesAsync(CancellationToken ct)`, `Task UnbindAsync(long? deviceId, CancellationToken ct)` (свой компьютер = выход), `Task ApplyPromoAsync(string code, CancellationToken ct)`, `Task SendSupportAsync(string? email, string topic, string message, string? logBase64, CancellationToken ct)`;
  - `bool AcceptTicket(SignedTicket ticket)` — проверяет и сохраняет билет этого компьютера (покупка, промокод, обновление);
  - ошибки — `ApiException` (экраны показывают `Error.Message` или свой текст по `Error.Code`); `TOKEN_REVOKED` в любом методе с токеном — выход + `SessionEnded = true` + `Changed`, затем исключение.

- [ ] **Step 1: Падающие тесты**

Create `tests/PcCleaner.Tests/Account/AccountServiceTests.cs`:

```csharp
using PcCleaner.Core.Account;
using Xunit;

namespace PcCleaner.Tests.Account;

public class AccountServiceTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TestLicense _key = new();
    private readonly FakeApi _fake = new();
    private readonly MemoryAccountStore _store = new();
    private DateTimeOffset _now = TestLicense.Now;
    private int _changes;

    public AccountServiceTests() => TestEnvironment.Init();

    public void Dispose() => _key.Dispose();

    private AccountService Service()
    {
        var service = new AccountService(Api.Client(_fake), _store, new FixedHwHash(), "windows", "PC", "1.5.0", _key.Keys, () => _now,
            (_, _) => Task.CompletedTask);
        service.Changed += () => _changes++;
        return service;
    }

    private AccountService SignedIn(string plan = "pro")
    {
        var t = _key.Sign(TestLicense.Json(plan: plan));
        _store.Data = new AccountData("tok", "user@example.com", TestLicense.DeviceId, TestLicense.Sub, t.Ticket, t.Signature, "inst-1");
        return Service();
    }

    [Fact]
    public void Nothing_Saved_IsAGuest()
    {
        var service = Service();

        Assert.False(service.IsSignedIn);
        Assert.Equal(Tier.Guest, service.Tier);
        Assert.False(service.IsAvailable(Features.HomeFull));
        Assert.True(service.IsAvailable(Features.HomeQuick));
    }

    [Fact]
    public void ASavedProSignIn_IsPro_WithoutTheNetwork()
    {
        var service = SignedIn();

        Assert.True(service.IsSignedIn);
        Assert.Equal("user@example.com", service.Email);
        Assert.Equal(Tier.Pro, service.Tier);
        Assert.Empty(_fake.Requests);
    }

    [Fact]
    public void ASavedTicketWithAForeignSignature_IsFree()
    {
        using var other = new TestLicense();
        var t = other.Sign(TestLicense.Json());
        _store.Data = new AccountData("tok", "u@x.y", TestLicense.DeviceId, TestLicense.Sub, t.Ticket, t.Signature);

        Assert.Equal(Tier.Free, Service().Tier);
    }

    [Fact]
    public async Task Login_SavesTheTokenDeviceAccountAndTicket_AndTellsTheScreens()
    {
        _fake.On("POST /auth/login", 200, _key.LoginJson(TestLicense.Json()));
        _store.Data = new AccountData(InstallId: "inst-1");
        var service = Service();

        await service.LoginAsync("user@example.com", "password1", None);

        Assert.Equal(Tier.Pro, service.Tier);
        Assert.Equal("t0ken", _store.Data.Token);
        Assert.Equal(TestLicense.DeviceId, _store.Data.DeviceId);
        Assert.Equal(TestLicense.Sub, _store.Data.AccountId);
        Assert.Equal("user@example.com", _store.Data.Email);
        Assert.Equal("inst-1", _store.Data.InstallId);
        Assert.Equal(1, _changes);
        Assert.Equal(TestLicense.Hw, Api.Body(_fake.Requests[0]).GetProperty("hw_hash").GetString());
    }

    [Fact]
    public async Task AFreeAccount_IsSignedIn_AndFree()
    {
        _fake.On("POST /auth/login", 200, _key.LoginJson(TestLicense.Json(plan: "free", kind: "none", noExpiry: true, devicesMax: 0)));
        var service = Service();

        await service.LoginAsync("user@example.com", "password1", None);

        Assert.True(service.IsSignedIn);
        Assert.Equal(Tier.Free, service.Tier);
    }

    [Theory]
    [InlineData("AUTH_FAILED", 401)]
    [InlineData("EMAIL_NOT_VERIFIED", 403)]
    [InlineData("RATE_LIMITED", 429)]
    public async Task AFailedLogin_SavesNothing_AndThrowsTheCode(string code, int status)
    {
        _fake.OnError("POST /auth/login", status, code);
        var service = Service();

        var ex = await Assert.ThrowsAsync<ApiException>(() => service.LoginAsync("u@x.y", "password1", None));

        Assert.Equal(code, ex.Error.Code);
        Assert.False(service.IsSignedIn);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task ADeviceLimit_GivesTheComputers_ThenReleaseAndLoginAgain()
    {
        var limit = $",\"devices\":[{{\"id\":5,\"name\":\"Old\",\"platform\":\"windows\",\"bound_at\":\"2026-01-01T00:00:00Z\",\"last_seen_at\":null,\"current\":false}}]," +
                    $"\"bind_limit\":1,\"unbind_token\":\"{new string('a', 64)}\"";
        _fake.OnError("POST /auth/login", 409, "DEVICE_LIMIT", limit)
            .On("POST /auth/login", 200, _key.LoginJson(TestLicense.Json()))
            .On("POST /device/release", 200, "{\"ok\":true}");
        var service = Service();

        var ex = await Assert.ThrowsAsync<ApiException>(() => service.LoginAsync("u@x.y", "password1", None));
        var info = ex.Error.AsDeviceLimit()!;
        await service.ReleaseAsync(info.UnbindToken, info.Devices[0].Id, None);
        await service.LoginAsync("u@x.y", "password1", None);

        Assert.Equal(Tier.Pro, service.Tier);
        Assert.Equal(5, Api.Body(_fake.To("POST /device/release").Single()).GetProperty("device_id").GetInt64());
    }

    [Fact]
    public async Task RegisterThenVerify_SignsIn()
    {
        _fake.On("POST /auth/register", 200, "{\"pending\":true}")
            .On("POST /auth/verify", 200, _key.LoginJson(TestLicense.Json(plan: "free", noExpiry: true)));
        var service = Service();

        await service.RegisterAsync("new@x.y", "password1", "ru", None);
        Assert.False(service.IsSignedIn);
        await service.VerifyAsync("new@x.y", "123456", None);

        Assert.True(service.IsSignedIn);
        Assert.Equal("ru", Api.Body(_fake.To("POST /auth/register").Single()).GetProperty("lang").GetString());
    }

    [Fact]
    public async Task Resend_GivesTheSecondsToWait()
    {
        _fake.On("POST /auth/resend", 200, "{\"ok\":true,\"retry_after\":60}");

        Assert.Equal(60, await Service().ResendAsync("u@x.y", "reset", None));
    }

    [Fact]
    public async Task ResetThenConfirm_SignsInWithTheNewPassword()
    {
        _fake.On("POST /auth/reset", 200, "{\"ok\":true}")
            .On("POST /auth/reset/confirm", 200, _key.LoginJson(TestLicense.Json()));
        var service = Service();

        await service.ResetAsync("u@x.y", None);
        await service.ResetConfirmAsync("u@x.y", "111111", "new-password", None);

        Assert.Equal(Tier.Pro, service.Tier);
    }

    [Fact]
    public async Task Logout_RevokesTheToken_AndForgetsEverythingButTheInstallNumber()
    {
        _fake.On("POST /auth/logout", 200, "{\"ok\":true}");
        var service = SignedIn();

        await service.LogoutAsync(None);

        Assert.Equal("tok", _fake.Requests.Single().Bearer);
        Assert.Equal(new AccountData(InstallId: "inst-1"), _store.Data);
        Assert.Equal(Tier.Guest, service.Tier);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task Logout_WithoutTheNetwork_StillSignsOutHere()
    {
        _fake.ThrowOnSend = new System.Net.Http.HttpRequestException("down");
        var service = SignedIn();

        await service.LogoutAsync(None);

        Assert.False(service.IsSignedIn);
    }

    [Fact]
    public async Task TokenRevoked_SignsOut_RemembersIt_AndThrows()
    {
        _fake.OnError("GET /devices", 401, "TOKEN_REVOKED");
        var service = SignedIn();

        var ex = await Assert.ThrowsAsync<ApiException>(() => service.DevicesAsync(None));

        Assert.Equal(ApiError.TokenRevoked, ex.Error.Code);
        Assert.False(service.IsSignedIn);
        Assert.True(service.SessionEnded);
        Assert.Equal("inst-1", _store.Data.InstallId);
        Assert.Null(_store.Data.Token);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task UnbindingAnotherComputer_KeepsThisOne_UnbindingThisOne_SignsOut()
    {
        _fake.On("POST /device/unbind", 200, "{\"ok\":true}");
        var service = SignedIn();

        await service.UnbindAsync(5, None);
        Assert.True(service.IsSignedIn);

        await service.UnbindAsync(TestLicense.DeviceId, None);
        Assert.False(service.IsSignedIn);
        Assert.False(service.SessionEnded); // the user's own choice, not a revoked token
    }

    [Fact]
    public async Task APromoCode_GivesANewTicket_AtOnce()
    {
        _fake.On("POST /promo/apply", 200, _key.TicketJson(TestLicense.Json(plan: "pro", kind: "promo")));
        var service = SignedIn(plan: "free");
        Assert.Equal(Tier.Free, service.Tier);

        await service.ApplyPromoAsync("FRIEND-1", None);

        Assert.Equal(Tier.Pro, service.Tier);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void AcceptTicket_TakesOnlyAGoodTicketOfThisComputer()
    {
        var service = SignedIn(plan: "free");
        using var other = new TestLicense();

        Assert.False(service.AcceptTicket(other.Sign(TestLicense.Json())));
        Assert.False(service.AcceptTicket(_key.Sign(TestLicense.Json(deviceId: 99))));
        Assert.Equal(Tier.Free, service.Tier);
        Assert.True(service.AcceptTicket(_key.Sign(TestLicense.Json())));
        Assert.Equal(Tier.Pro, service.Tier);
    }

    [Fact]
    public async Task Support_UsesTheAccountsEmail_AndToken_WhenSignedIn()
    {
        _fake.On("POST /ticket", 200, "{\"id\":1}");
        var service = SignedIn();

        await service.SendSupportAsync(null, "question", "Как включить Pro на втором ПК?", null, None);

        var r = _fake.Requests.Single();
        Assert.Equal("tok", r.Bearer);
        Assert.Equal("user@example.com", Api.Body(r).GetProperty("email").GetString());
    }

    [Fact]
    public async Task Support_AsAGuest_NeedsTheEmailTyped()
    {
        _fake.On("POST /ticket", 200, "{\"id\":1}");
        var service = Service();

        await Assert.ThrowsAsync<ArgumentException>(() => service.SendSupportAsync(null, "bug", "Окно не открывается", null, None));
        await service.SendSupportAsync("guest@x.y", "bug", "Окно не открывается", null, None);

        Assert.Null(_fake.Requests.Single().Bearer);
    }

    [Fact]
    public void TheDeviceName_IsCleanedAndCut()
    {
        var service = new AccountService(Api.Client(_fake), _store, new FixedHwHash(), "windows", "  My\tPC " + new string('x', 80), "1.5.0", _key.Keys);

        Assert.Equal(64, service.Device.Name.Length);
        Assert.StartsWith("MyPC", service.Device.Name);
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~AccountServiceTests"` → ошибка сборки.

- [ ] **Step 3: `AccountService`**

Create `src/PcCleaner/Core/Account/AccountService.cs`:

```csharp
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
```

`en.json` — добавить:

```json
  "Аккаунт: вход по паролю": "Account: signed in with a password",
  "Аккаунт: почта подтверждена, вход выполнен": "Account: e-mail confirmed, signed in",
  "Аккаунт: пароль сменён, вход выполнен": "Account: password changed, signed in",
  "Аккаунт: компьютер отвязан, чтобы войти на этом": "Account: a computer was unbound to sign in on this one",
  "Аккаунт: сервер не подтвердил выход ({0}) — данные входа всё равно удалены": "Account: the server did not confirm signing out ({0}) — the sign-in data was removed anyway",
  "Аккаунт: выход": "Account: signed out",
  "Аккаунт: этот компьютер отвязан — выход": "Account: this computer was unbound — signed out",
  "Аккаунт: отвязан другой компьютер": "Account: another computer was unbound",
  "Аккаунт: промокод применён": "Account: promo code applied",
  "Аккаунт: полученный билет лицензии не подходит этому компьютеру — оставлен прежний": "Account: the license ticket received does not match this computer — the previous one is kept",
  "Аккаунт: вход больше не действует (компьютер отвязан или пароль сменён) — программа в бесплатном режиме": "Account: the sign-in is no longer valid (the computer was unbound or the password changed) — the program is in free mode",
  "Аккаунт: подпись билета лицензии не проверена — до следующей проверки программа в бесплатном режиме": "Account: the license ticket signature could not be verified — the program is in free mode until the next check",
```

- [ ] **Step 4: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~AccountServiceTests|FullyQualifiedName~LocalizationCatalogTests"` → PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Accounts: AccountService — sign-in, sign-out, computers, promo, support

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: `AccountService` — вход через браузер, обновление билета, оплата

**Files:**
- Create: `src/PcCleaner/Core/Account/AccountService.Background.cs`
- Modify: `src/PcCleaner/Core/Localization/en.json`
- Test: `tests/PcCleaner.Tests/Account/AccountServicePollingTests.cs`

**Interfaces:**
- Consumes: `AccountService` (задача 8), `ApiClient` (опрос кода, билет, оплата).
- Produces (в `AccountService`):
  - `static readonly TimeSpan BrowserPollInterval = 5 s`, `MaxBrowserPollInterval = 60 s`, `PaymentPollInterval = 5 s`, `PaymentWait = 3 min`, `RetryAfterFailure = 15 min`, `DailyRefresh = 24 h`;
  - `Task<BrowserCode> StartBrowserLoginAsync(CancellationToken ct)`;
  - `Task<bool> WaitBrowserLoginAsync(BrowserCode code, Func<DeviceLimitInfo, Task> onDeviceLimit, CancellationToken ct)` — true = вошёл, false = код истёк (`expires_in` или 410); 429 удваивает паузу (до 60 с); `DEVICE_LIMIT` — опрос стоит, пока `onDeviceLimit` не завершится; нет сети — опрос продолжается; другие ошибки — `ApiException`;
  - `Task<bool> RefreshTicketAsync(CancellationToken ct)` — false при сбое сети/сервера (билет не трогается);
  - `TimeSpan NextRefreshDelay(bool lastSucceeded)`;
  - `Task RunRefreshLoopAsync(CancellationToken ct)` — сразу, затем по `NextRefreshDelay`;
  - `void RefreshSoon(TimeSpan after)` — однократная фоновая проверка (неоконченная оплата: через 5 минут);
  - оплата с токеном: `Task<PriceList> PricesAsync(CancellationToken ct)` (с токеном, если вошёл), `Task<Quote> QuoteAsync(QuoteRequest r, CancellationToken ct)`, `Task<PaymentLink> StartPaymentAsync(QuoteRequest r, CancellationToken ct)`, `Task<PaymentLink> PaymentLinkAsync(long paymentId, CancellationToken ct)`, `Task<PaymentStatus> PaymentStatusAsync(long paymentId, CancellationToken ct)` (при `paid` билет принимается сам);
  - `Task<PaymentStatus> WaitPaymentAsync(long paymentId, CancellationToken ct)` — опрос раз в 5 с до `PaymentWait`; возвращает `paid` сразу; `failed` не окончательный — опрос продолжается до конца срока (экран может показать «Оплата не прошла» по промежуточному статусу через `IProgress<PaymentStatus>`, перегрузка ниже); по истечении — последний статус и `RefreshSoon(5 min)`.

- [ ] **Step 1: Падающие тесты**

Create `tests/PcCleaner.Tests/Account/AccountServicePollingTests.cs`:

```csharp
using PcCleaner.Core.Account;
using Xunit;

namespace PcCleaner.Tests.Account;

public class AccountServicePollingTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TestLicense _key = new();
    private readonly FakeApi _fake = new();
    private readonly MemoryAccountStore _store = new();
    private readonly List<TimeSpan> _waits = new();
    private DateTimeOffset _now = TestLicense.Now;

    public AccountServicePollingTests() => TestEnvironment.Init();

    public void Dispose() => _key.Dispose();

    /// <summary>The delay of the service moves the clock instead of waiting.</summary>
    private AccountService Service() => new(Api.Client(_fake), _store, new FixedHwHash(), "windows", "PC", "1.5.0", _key.Keys, () => _now,
        (d, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            _waits.Add(d);
            _now += d;
            return Task.CompletedTask;
        });

    private AccountService SignedIn(string json)
    {
        var t = _key.Sign(json);
        _store.Data = new AccountData("tok", "user@example.com", TestLicense.DeviceId, TestLicense.Sub, t.Ticket, t.Signature);
        return Service();
    }

    private static readonly BrowserCode Code = new("ABCD", "https://waterca7.ru/auth/device?code=ABCD", 600);

    [Fact]
    public async Task TheBrowserCode_IsPolledEveryFiveSeconds_UntilTheSiteConfirms()
    {
        _fake.On("GET /auth/code/ABCD", 202, "{\"status\":\"pending\"}")
            .On("GET /auth/code/ABCD", 202, "{\"status\":\"pending\"}")
            .On("GET /auth/code/ABCD", 200, _key.LoginJson(TestLicense.Json()));
        var service = Service();

        var ok = await service.WaitBrowserLoginAsync(Code, _ => Task.CompletedTask, None);

        Assert.True(ok);
        Assert.Equal(Tier.Pro, service.Tier);
        Assert.All(_waits, w => Assert.Equal(TimeSpan.FromSeconds(5), w));
        Assert.Equal(3, _fake.Requests.Count);
    }

    [Fact]
    public async Task ThePoll_StopsAtExpiresIn()
    {
        _fake.On("GET /auth/code/ABCD", 202, "{\"status\":\"pending\"}");

        var ok = await Service().WaitBrowserLoginAsync(Code with { ExpiresIn = 20 }, _ => Task.CompletedTask, None);

        Assert.False(ok);
        Assert.InRange(_fake.Requests.Count, 3, 4);
    }

    [Fact]
    public async Task AGoneCode_EndsThePoll()
    {
        _fake.OnError("GET /auth/code/ABCD", 410, "NOT_FOUND");

        Assert.False(await Service().WaitBrowserLoginAsync(Code, _ => Task.CompletedTask, None));
        Assert.Single(_fake.Requests);
    }

    [Fact]
    public async Task RateLimited_DoublesThePause_UpToAMinute()
    {
        for (var i = 0; i < 6; i++)
            _fake.OnError("GET /auth/code/ABCD", 429, "RATE_LIMITED");
        _fake.On("GET /auth/code/ABCD", 200, _key.LoginJson(TestLicense.Json()));

        await Service().WaitBrowserLoginAsync(Code, _ => Task.CompletedTask, None);

        Assert.Equal(new[] { 5, 10, 20, 40, 60, 60, 60 }, _waits.Select(w => (int)w.TotalSeconds));
    }

    [Fact]
    public async Task ADeviceLimit_PausesThePoll_UntilTheDialogIsDone()
    {
        var limit = $",\"devices\":[],\"bind_limit\":1,\"unbind_token\":\"{new string('a', 64)}\"";
        _fake.OnError("GET /auth/code/ABCD", 409, "DEVICE_LIMIT", limit)
            .On("GET /auth/code/ABCD", 200, _key.LoginJson(TestLicense.Json()));
        var service = Service();
        var requestsWhileOpen = -1;

        var ok = await service.WaitBrowserLoginAsync(Code, async info =>
        {
            Assert.Equal(new string('a', 64), info.UnbindToken);
            await Task.Yield();
            requestsWhileOpen = _fake.Requests.Count;
        }, None);

        Assert.True(ok);
        Assert.Equal(1, requestsWhileOpen); // nothing was polled while the dialog was open
    }

    [Fact]
    public async Task NoNetwork_KeepsPolling()
    {
        var service = Service();
        _fake.ThrowOnSend = new System.Net.Http.HttpRequestException("down");
        var task = service.WaitBrowserLoginAsync(Code with { ExpiresIn = 15 }, _ => Task.CompletedTask, None);

        Assert.False(await task);
        Assert.True(_waits.Count >= 3);
    }

    [Fact]
    public async Task Cancelling_StopsThePoll()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().WaitBrowserLoginAsync(Code, _ => Task.CompletedTask, cts.Token));
    }

    [Fact]
    public async Task Refresh_TakesTheNewTicket()
    {
        var service = SignedIn(TestLicense.Json(plan: "free", noExpiry: true));
        _fake.On("GET /license/ticket", 200, _key.TicketJson(TestLicense.Json(plan: "pro")));

        Assert.True(await service.RefreshTicketAsync(None));
        Assert.Equal(Tier.Pro, service.Tier);
    }

    [Theory]
    [InlineData(502, "<html>bad gateway</html>")]
    [InlineData(500, "{\"error\":\"SERVER_ERROR\",\"message\":\"x\"}")]
    public async Task AFailedRefresh_KeepsTheTicket(int status, string body)
    {
        var service = SignedIn(TestLicense.Json(plan: "pro"));
        _fake.On("GET /license/ticket", status, body);

        Assert.False(await service.RefreshTicketAsync(None));
        Assert.Equal(Tier.Pro, service.Tier);
    }

    [Fact]
    public async Task ABadlySignedRefresh_KeepsTheTicket()
    {
        using var other = new TestLicense();
        var service = SignedIn(TestLicense.Json(plan: "pro"));
        _fake.On("GET /license/ticket", 200, other.TicketJson(TestLicense.Json(plan: "free")));

        Assert.False(await service.RefreshTicketAsync(None));
        Assert.Equal(Tier.Pro, service.Tier);
    }

    [Fact]
    public async Task ARevokedTokenOnRefresh_SignsOut()
    {
        var service = SignedIn(TestLicense.Json());
        _fake.OnError("GET /license/ticket", 401, "TOKEN_REVOKED");

        Assert.False(await service.RefreshTicketAsync(None));
        Assert.False(service.IsSignedIn);
        Assert.True(service.SessionEnded);
    }

    [Fact]
    public void TheNextRefresh_IsADayLater_OrJustAfterTheSubscriptionEnds_OrSoonAfterAFailure()
    {
        Assert.Equal(TimeSpan.FromHours(24), SignedIn(TestLicense.Json(expires: TestLicense.Now.AddDays(30))).NextRefreshDelay(true));
        Assert.Equal(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1),
            SignedIn(TestLicense.Json(expires: TestLicense.Now.AddHours(3))).NextRefreshDelay(true));
        Assert.Equal(TimeSpan.FromMinutes(15), SignedIn(TestLicense.Json()).NextRefreshDelay(false));
        Assert.Equal(TimeSpan.FromHours(24), Service().NextRefreshDelay(true)); // not signed in: nothing to refresh soon
    }

    [Fact]
    public async Task TheRefreshLoop_RefreshesAtOnce_ThenWaits()
    {
        SignedIn(TestLicense.Json(plan: "free", noExpiry: true)); // fills the store
        _fake.On("GET /license/ticket", 200, _key.TicketJson(TestLicense.Json(plan: "pro", expires: TestLicense.Now.AddDays(300))));
        using var cts = new CancellationTokenSource();
        var service2 = new AccountService(Api.Client(_fake), _store, new FixedHwHash(), "windows", "PC", "1.5.0", _key.Keys, () => _now,
            (d, ct) =>
            {
                _waits.Add(d);
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service2.RunRefreshLoopAsync(cts.Token));

        Assert.Single(_fake.To("GET /license/ticket"));
        Assert.Equal(TimeSpan.FromHours(24), Assert.Single(_waits));
        Assert.Equal(Tier.Pro, service2.Tier);
    }

    [Fact]
    public async Task APaidPayment_GivesTheTicket_AndPro()
    {
        var service = SignedIn(TestLicense.Json(plan: "free", noExpiry: true));
        var t = _key.Sign(TestLicense.Json(plan: "pro"));
        _fake.On("GET /pay/91", 200, "{\"status\":\"pending\"}")
            .On("GET /pay/91", 200, $"{{\"status\":\"paid\",\"license\":{{\"ticket\":\"{t.Ticket}\",\"signature\":\"{t.Signature}\"}}}}");

        var status = await service.WaitPaymentAsync(91, None);

        Assert.Equal("paid", status.Status);
        Assert.Equal(Tier.Pro, service.Tier);
        Assert.All(_waits, w => Assert.Equal(TimeSpan.FromSeconds(5), w));
    }

    [Fact]
    public async Task AFailedPayment_IsWatchedUntilTheEnd_BecauseItMayStillBePaid()
    {
        var service = SignedIn(TestLicense.Json(plan: "free", noExpiry: true));
        var t = _key.Sign(TestLicense.Json(plan: "pro"));
        _fake.On("GET /pay/91", 200, "{\"status\":\"failed\"}")
            .On("GET /pay/91", 200, "{\"status\":\"failed\"}")
            .On("GET /pay/91", 200, $"{{\"status\":\"paid\",\"license\":{{\"ticket\":\"{t.Ticket}\",\"signature\":\"{t.Signature}\"}}}}");
        var seen = new List<string>();

        var status = await service.WaitPaymentAsync(91, new SyncProgress<PaymentStatus>(s => seen.Add(s.Status)), None);

        Assert.Equal("paid", status.Status);
        Assert.Equal(new[] { "failed", "failed" }, seen); // the screen may say «Оплата не прошла» meanwhile
        Assert.Equal(Tier.Pro, service.Tier);
    }

    [Fact]
    public async Task AStillPendingPayment_StopsAfterTheWait()
    {
        var service = SignedIn(TestLicense.Json(plan: "free", noExpiry: true));
        _fake.On("GET /pay/91", 200, "{\"status\":\"pending\"}");

        var status = await service.WaitPaymentAsync(91, None);

        Assert.Equal("pending", status.Status);
        // 3 minutes every 5 seconds; the refresh RefreshSoon starts afterwards runs in the background and is not counted here.
        Assert.Equal(36, _fake.To("GET /pay/91").Count());
    }

    [Fact]
    public async Task ThePaymentMethods_SendTheToken()
    {
        var service = SignedIn(TestLicense.Json());
        _fake.On("GET /prices", 200, "{\"currency\":\"RUB\",\"pro\":{\"month\":1,\"year\":2,\"lifetime\":3},\"family\":{\"min\":2,\"max\":5,\"default\":3,\"table\":{}},\"payments_enabled\":true}")
            .On("POST /pay/quote", 200, "{\"list\":2,\"discount\":0,\"credit\":0,\"amount\":2,\"action\":\"new\",\"term\":\"year\",\"devices\":1,\"from_devices\":null,\"expires_at\":null}")
            .On("POST /pay/start", 200, "{\"payment_id\":91,\"url\":\"https://waterca7.ru/pay/app/91?t=x\"}")
            .On("POST /pay/link", 200, "{\"payment_id\":91,\"url\":\"https://waterca7.ru/pay/app/91?t=y\"}");
        var request = new QuoteRequest("pro", "year", 1);

        await service.PricesAsync(None);
        await service.QuoteAsync(request, None);
        await service.StartPaymentAsync(request, None);
        await service.PaymentLinkAsync(91, None);

        Assert.All(_fake.Requests, r => Assert.Equal("tok", r.Bearer));
    }

    [Fact]
    public async Task PricesForAGuest_GoWithoutAToken()
    {
        _fake.On("GET /prices", 200, "{\"currency\":\"RUB\",\"pro\":{\"month\":1,\"year\":2,\"lifetime\":3},\"family\":{\"min\":2,\"max\":5,\"default\":3,\"table\":{}},\"payments_enabled\":false}");

        await Service().PricesAsync(None);

        Assert.Null(_fake.Requests.Single().Bearer);
    }

    [Fact]
    public void TheInstallNumber_IsMadeOnce_AndCanBeReset()
    {
        var service = Service();

        var first = service.EnsureInstallId();
        Assert.True(Guid.TryParse(first, out _));
        Assert.Equal(first, service.EnsureInstallId());
        service.ResetInstallId();
        Assert.Null(service.InstallId);
        Assert.NotEqual(first, service.EnsureInstallId());
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~AccountServicePollingTests"` → ошибка сборки.

- [ ] **Step 3: Код**

Create `src/PcCleaner/Core/Account/AccountService.Background.cs`:

```csharp
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
        if (!IsSignedIn)
            return false;
        try
        {
            var ticket = await WithToken(t => _api.TicketAsync(t, ct));
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
            var ok = await RefreshTicketAsync(ct);
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
    public async Task<PaymentStatus> PaymentStatusAsync(long paymentId, CancellationToken ct)
    {
        var status = await WithToken(t => _api.PayStatusAsync(t, paymentId, ct));
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
        var deadline = _now() + PaymentWait;
        var last = new PaymentStatus("pending", null);
        while (_now() < deadline)
        {
            await _delay(PaymentPollInterval, ct);
            try
            {
                last = await PaymentStatusAsync(paymentId, ct);
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
```

`en.json` — добавить:

```json
  "Аккаунт: вход через браузер": "Account: signed in through the browser",
  "Аккаунт: билет лицензии не обновлён ({0}) — действует прежний": "Account: the license ticket was not refreshed ({0}) — the previous one stays valid",
  "Аккаунт: ошибка фоновой проверки лицензии": "Account: error of the background license check",
  "Покупка: начата оплата №{0}": "Purchase: payment #{0} started",
  "Покупка: оплата №{0} прошла, Pro включён": "Purchase: payment #{0} went through, Pro is on",
  "Покупка: оплата №{0} ещё обрабатывается — Pro включится сам": "Purchase: payment #{0} is still being processed — Pro will turn on by itself",
```

- [ ] **Step 4: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~PcCleaner.Tests.Account|FullyQualifiedName~LocalizationCatalogTests"` → PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Accounts: browser sign-in poll, ticket refresh loop, payment wait

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Боевой ключ, документация, полная проверка

**Files:**
- Create (от владельца): `src/PcCleaner/Core/Account/license_pub.pem`
- Modify: `docs/ARCHITECTURE.md`
- Test: `tests/PcCleaner.Tests/Account/LicenseTicketTests.cs` (тест наличия ключа)

- [ ] **Step 1: Ключ от владельца**

Спросить владельца (по-русски): «Пришлите, пожалуйста, файл `license_pub.pem` — публичный ключ лицензий, который создал `scripts/make-license-key.ps1` (секрета в нём нет). Если его не осталось, его можно получить из закрытого ключа командой `openssl ec -in license_key.pem -pubout -out license_pub.pem` у вас на компьютере; закрытый ключ присылать не нужно». Положить присланный файл в `src/PcCleaner/Core/Account/license_pub.pem`.

Проверить, что это не тестовый ключ сервера:

```powershell
& "C:\Program Files\Git\usr\bin\openssl.exe" pkey -pubin -in src\PcCleaner\Core\Account\license_pub.pem -outform DER | & "C:\Program Files\Git\usr\bin\openssl.exe" dgst -sha256
& "C:\Program Files\Git\usr\bin\openssl.exe" pkey -pubin -in tests\PcCleaner.Tests\Account\Fixtures\license_test_pub.pem -outform DER | & "C:\Program Files\Git\usr\bin\openssl.exe" dgst -sha256
```

Отпечатки должны различаться. Если владелец пока не прислал ключ — задачу не блокировать: пропустить шаги 1–2, оставить пункт открытым в отчёте (без ключа любой билет считается неподписанным, программа бесплатна; часть E без ключа не выпускается).

- [ ] **Step 2: Тест наличия ключа**

Когда файл есть, добавить в `LicenseTicketTests`:

```csharp
    [Fact]
    public void TheBuild_HasALiveP256Key()
    {
        Assert.NotEmpty(LicenseKeys.Live);
        Assert.All(LicenseKeys.Live, k => Assert.Matches("^[0-9a-f]{64}$", LicenseKeys.Fingerprint(k)));
    }
```

- [ ] **Step 3: Документация**

В `docs/ARCHITECTURE.md` добавить раздел:

> **Аккаунт (`Core/Account/`, `Core/AccountWindows/`).** `ApiClient` — HTTP к `https://waterca7.ru/api/v1` (ошибки — `ApiException` с кодом `error`). `LicenseTicket.Verify` проверяет подпись билета (ECDSA P-256, DER) ключами `LicenseKeys.Live` (встроенный `license_pub.pem`), `LicenseState.Evaluate` даёт уровень Гость / Free / Pro, `Features` — таблица Pro-функций (копия `features.php` сайта; тест сверяет). `AccountService` — вход, выход, билет, устройства, промокод, оплата, обращение; хранит данные в `IAccountStore` (Windows — `DpapiAccountStore`, файл `account.bin` под DPAPI текущего пользователя), номер компьютера — `IHwHash` (Windows — `WmiHwHash`). Файлы `Core/Account/` копируются в Mac-версию без изменений; платформенные реализации — нет.

- [ ] **Step 4: Полная проверка**

Run: `dotnet test tests/PcCleaner.Tests` → PASS, число тестов больше, чем до B1.
Run: `dotnet build src/PcCleaner -c Release` → `Build succeeded`, без новых предупреждений.
Проверить, что в журнал не пишется ничего секретного: `git grep -n "Log\.\(Info\|Warn\|Error\)" -- src/PcCleaner/Core/Account src/PcCleaner/Core/AccountWindows` — ни одна строка не подставляет `Token`, `password`, `Ticket`, `Signature`, `HwHash`, `UnbindToken`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Accounts: live license key, architecture notes

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 6: Отчёт**

Коротко по-русски владельцу: что сделано (ядро без экранов), сколько тестов, есть ли боевой ключ; ветка `menu-accounts` не запушена. Дальше — B2 (экраны).
