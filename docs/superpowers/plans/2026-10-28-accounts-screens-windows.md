# Вход и Профиль (часть B2, Windows) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Пользователь Windows-версии входит в аккаунт прямо в программе — по паролю, с регистрацией и кодом из письма, со сбросом пароля, через браузер, с отвязкой компьютера при лимите — видит в «Профиле» тариф, срок, свои компьютеры, применяет промокод, пишет в поддержку и выходит; при первом запуске и после обновления один раз видит экран «Войти / Зарегистрироваться / Продолжить без входа».

**Architecture:** Всё строится на `AccountService` из B1 (`AppServices.Account`). Экраны — обычные модели представления без WPF-зависимостей (`AccountIntroViewModel`, `SignInViewModel`, `ProfileViewModel`), их тестируют на поддельном сервере из B1 (`FakeApi`). Окно входа — отдельное модальное окно WPF (`SignInWindow`), открывается через `SignInDialog.Show(tab)`; экран при запуске — оверлей главного окна, как нынешний экран приветствия. Скриншоты получают `--tier guest|free|pro` (без сети: учётка подставляется локально) и `--signin` (снимки окна входа).

**Tech Stack:** C# / WPF, .NET (`net7.0-windows`, собирается SDK 10), xUnit. Встроенного браузера в B2 нет: документы, страница «Купить» и вход через браузер открываются во внешнем браузере (`ProcessRunner.ShellOpen`); окно покупки со встроенной оплатой — план B3, он заменит эти места.

## Global Constraints

- Спека: `A:\WaterCa7s-pc-cleaner\docs\superpowers\specs\2026-10-27-menu-and-accounts-design.md`, разделы 3.1, 3.2, 3.3, 5, 6. Контракт сервера: `A:\waterca7.ru\docs\api-contract-for-app.md` (главнее спеки, где расходятся).
- B1 выполнен: `Core/Account/*` (`AccountService`, `ApiClient`, `ApiError`, `Features`, `LicenseStatus`, `Tier`…), `Core/AccountWindows/*` (`DpapiAccountStore`, `WmiHwHash`), тестовый набор `tests/PcCleaner.Tests/Account/AccountTestKit*.cs` (`FakeApi`, `TestLicense`, `MemoryAccountStore`, `FixedHwHash`, `SyncProgress`, `Api`). Имена и сигнатуры — как в плане `2026-10-28-accounts-core.md`.
- `--screenshots` никогда не ходит в сеть и не показывает экран входа сам; уровень задаётся `--tier guest|free|pro`.
- Нынешним пользователям экран входа показывается один раз (флаг `AccountIntroShown`).
- Пароли, коды из писем, токены и билеты не пишутся в журнал и не сохраняются в файлы, кроме токена и билета в `account.bin` (B1).
- Требования к паролю — как на сайте: 8–128 символов, без обрезки пробелов. Почта ≤ 254 символа, есть «@» и точка после неё.
- `register`, `resend`, `reset` отвечают успехом и тогда, когда письмо не ушло: писать «Мы отправили код, если адрес верен», подсказывать «проверьте „Спам“», «Отправить ещё раз» — после `retry_after`.
- После `DEVICE_LIMIT` у `verify` и `reset/confirm` код уже потрачен: после отвязки входить `login` с паролем. У входа через браузер, пока открыт список отвязки, опрос стоит (это делает `AccountService.WaitBrowserLoginAsync`).
- Тексты — через `T._` / `T.F` / `{loc:T '…'}` с английским в `en.json` по `docs/localization-glossary.md`; тест каталога перечисляет недостающие записи и лишние.
- `AccountService.Changed` приходит из любого потока: модели представления переходят в поток окна через `Ui.Post`.
- Тесты не меняют настоящую систему и не ходят в сеть; ручная проверка — только в режиме `--screenshots` и в финальном живом прогоне с владельцем (пароли и коды вводит он).
- Работа в ветке `menu-accounts` рабочей копии `A:\WaterCa7s-pc-cleaner-menu`; не пушить без «да» владельца; локальный `main` в `A:\WaterCa7s-pc-cleaner` не трогать.
- Коммиты заканчиваются строкой `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Файлы

- Create `src/PcCleaner/Core/AccountWindows/AccountSetup.cs` — настоящий `AccountService` и подставной для скриншотов.
- Modify `src/PcCleaner/Core/AppServices.cs` (`Account`), `src/PcCleaner/App.xaml.cs` (`--tier`, `--signin`, фоновое обновление билета, порядок «заставка → вход → приветствие»), `src/PcCleaner/Core/AppSettings.cs` (`AccountIntroShown`).
- Create `src/PcCleaner/Modules/Profile/AccountIntroViewModel.cs` + оверлей в `src/PcCleaner/MainWindow.xaml(.cs)`, свойство в `src/PcCleaner/MainViewModel.cs`.
- Create `src/PcCleaner/Modules/Profile/SignInViewModel.cs`, `SignInErrors.cs`, `SignInWindow.xaml(.cs)`, `SignInDialog.cs`.
- Modify `src/PcCleaner/Modules/Profile/ProfileViewModel.cs`, `ProfileView.xaml(.cs)`; Create `ProfileTexts.cs`, `ProfileItems.cs`, `SupportAttachment.cs`.
- Modify `src/PcCleaner/Themes/Theme.xaml` (стиль `PasswordBox`), `src/PcCleaner/Core/ScreenshotRunner.cs`, `src/PcCleaner/Core/Localization/en.json`, `docs/ARCHITECTURE.md`.
- Tests `tests/PcCleaner.Tests/Profile/`: `AccountSetupTests.cs`, `AccountIntroViewModelTests.cs`, `SignInPasswordTests.cs`, `SignInRegisterTests.cs`, `SignInBrowserTests.cs`, `ProfileTextsTests.cs`, `ProfileViewModelTests.cs`, `ProfileDevicesTests.cs`, `ProfilePromoSupportTests.cs`; `tests/PcCleaner.Tests/Core/ParseArgsTests.cs` (дополнить).

Общий помощник тестов (создаётся в задаче 2, дальше переиспользуется) — `tests/PcCleaner.Tests/Profile/ProfileTestKit.cs`:

```csharp
using PcCleaner.Core.Account;
using PcCleaner.Tests.Account;

namespace PcCleaner.Tests.Profile;

/// <summary>An AccountService on the fake server with a clock and a delay that only moves the clock.</summary>
internal sealed class TestAccount : IDisposable
{
    public TestLicense Key { get; } = new();
    public FakeApi Fake { get; } = new();
    public MemoryAccountStore Store { get; } = new();
    public DateTimeOffset Now { get; set; } = TestLicense.Now;
    public List<string> Opened { get; } = new();

    public AccountService Service { get; }

    public TestAccount(string? signedInTicketJson = null)
    {
        if (signedInTicketJson is not null)
        {
            var t = Key.Sign(signedInTicketJson);
            Store.Data = new AccountData("tok", "user@example.com", TestLicense.DeviceId, TestLicense.Sub, t.Ticket, t.Signature, "inst-1");
        }
        Service = new AccountService(Api.Client(Fake), Store, new FixedHwHash(), "windows", "PC", "1.5.0", Key.Keys, () => Now,
            (d, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Now += d;
                return Task.CompletedTask;
            });
    }

    public void Open(string url) => Opened.Add(url);

    public string LimitJson(params (long Id, string Name)[] devices)
    {
        var list = string.Join(",", devices.Select(d =>
            $"{{\"id\":{d.Id},\"name\":\"{d.Name}\",\"platform\":\"windows\",\"bound_at\":\"2026-10-01T10:00:00Z\",\"last_seen_at\":null,\"current\":false}}"));
        return $",\"devices\":[{list}],\"bind_limit\":{devices.Length},\"unbind_token\":\"{new string('a', 64)}\"";
    }

    public void Dispose() => Key.Dispose();
}
```

Команды (из `A:\WaterCa7s-pc-cleaner-menu`): класс — `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~PcCleaner.Tests.Profile.SignInPasswordTests"`, все — `dotnet test tests/PcCleaner.Tests`, сборка — `dotnet build src/PcCleaner -c Debug`.

---

### Task 1: Подключение аккаунта, `--tier`, фоновое обновление билета

**Files:**
- Create: `src/PcCleaner/Core/AccountWindows/AccountSetup.cs`
- Modify: `src/PcCleaner/Core/AppServices.cs`, `src/PcCleaner/App.xaml.cs`
- Test: `tests/PcCleaner.Tests/Profile/AccountSetupTests.cs`, `tests/PcCleaner.Tests/Core/ParseArgsTests.cs`

**Interfaces:**
- Produces:
  - `public static AccountService AppServices.Account { get; set; }` (по умолчанию `AccountSetup.CreateReal()`, создаётся при первом обращении);
  - `public static class AccountSetup`: `AccountService CreateReal()` (`DpapiAccountStore`, `WmiHwHash`, `new ApiClient(() => T.Language)`, `LicenseKeys.Live`, "windows", `Environment.MachineName`, `AppInfo.Version`); `AccountService ForScreenshots(Tier tier)` — учётка в памяти, билет подписан ключом, созданным здесь же, сеть заменена обработчиком `ScreenshotApi` (на `GET /devices` — два примерных компьютера, на всё остальное — ошибка сети);
  - `internal static Tier? App.TierArg`, `internal static bool App.SignInArg`.

- [ ] **Step 1: Падающие тесты**

Create `tests/PcCleaner.Tests/Profile/AccountSetupTests.cs`:

```csharp
using PcCleaner.Core.Account;
using PcCleaner.Core.AccountWindows;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class AccountSetupTests
{
    [Theory]
    [InlineData(Tier.Guest)]
    [InlineData(Tier.Free)]
    [InlineData(Tier.Pro)]
    public void AScreenshotAccount_HasTheAskedTier_WithoutANetwork(Tier tier)
    {
        var account = AccountSetup.ForScreenshots(tier);

        Assert.Equal(tier, account.Tier);
        Assert.Equal(tier != Tier.Guest, account.IsSignedIn);
    }

    [Fact]
    public async Task AScreenshotAccount_ShowsSampleComputers_AndFailsEveryOtherCallAsOffline()
    {
        var account = AccountSetup.ForScreenshots(Tier.Pro);

        var devices = await account.DevicesAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<ApiException>(() => account.LoginAsync("a@b.c", "password1", CancellationToken.None));

        Assert.Equal(2, devices.Devices.Count);
        Assert.Contains(devices.Devices, d => d.Current);
        Assert.Equal(ApiError.Network, ex.Error.Code);
    }

    [Fact]
    public void ARefresh_OfAScreenshotAccount_KeepsTheTier()
    {
        var account = AccountSetup.ForScreenshots(Tier.Pro);

        Assert.False(account.RefreshTicketAsync(CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(Tier.Pro, account.Tier);
    }
}
```

В `tests/PcCleaner.Tests/Core/ParseArgsTests.cs` добавить (по образцу соседних тестов `ParseArgs`):

```csharp
    [Theory]
    [InlineData("guest", Tier.Guest)]
    [InlineData("FREE", Tier.Free)]
    [InlineData("pro", Tier.Pro)]
    [InlineData("vip", null)]
    public void Tier_IsReadForTheScreenshotRun(string value, Tier? expected)
    {
        App.ParseArgs(new[] { "--screenshots", "x", "--tier", value });
        Assert.Equal(expected, App.TierArg);
        App.ParseArgs(Array.Empty<string>());
        Assert.Null(App.TierArg);
    }

    [Fact]
    public void SignIn_AsksForPicturesOfTheSignInWindow()
    {
        App.ParseArgs(new[] { "--screenshots", "x", "--signin" });
        Assert.True(App.SignInArg);
        App.ParseArgs(Array.Empty<string>());
        Assert.False(App.SignInArg);
    }
```

(`using PcCleaner.Core.Account;` в начале файла).

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~AccountSetupTests|FullyQualifiedName~ParseArgsTests"` → ошибка сборки.

- [ ] **Step 3: `AccountSetup`**

Create `src/PcCleaner/Core/AccountWindows/AccountSetup.cs`:

```csharp
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core.AccountWindows;

/// <summary>Builds the AccountService of this run: the real one, or a local one for --screenshots (no network, any tier).</summary>
public static class AccountSetup
{
    public static AccountService CreateReal() => new(new ApiClient(() => T.Language), new DpapiAccountStore(), new WmiHwHash(), "windows",
        Environment.MachineName, AppInfo.Version, LicenseKeys.Live);

    /// <summary>
    /// A screenshot run's account: in memory, its ticket signed by a key made here (trusted by this account only), the network
    /// replaced by <see cref="ScreenshotApi"/>. Nothing is read from or written to the user's account.bin.
    /// </summary>
    public static AccountService ForScreenshots(Tier tier)
    {
        const string hw = "screenshot";
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = new MemoryStore();
        var now = DateTimeOffset.UtcNow;
        if (tier != Tier.Guest)
        {
            string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            var plan = tier == Tier.Pro ? "pro" : "free";
            var expires = tier == Tier.Pro ? $"\"{Iso(now.AddDays(300))}\"" : "null";
            var json = $"{{\"v\":1,\"sub\":7,\"email\":\"user@example.com\",\"plan\":\"{plan}\",\"kind\":\"{(tier == Tier.Pro ? "subscription" : "none")}\"," +
                       $"\"devices_max\":{(tier == Tier.Pro ? 1 : 0)},\"expires_at\":{expires},\"hw_hash\":\"{HwHash.Of(hw)}\",\"device_id\":42," +
                       $"\"issued_at\":\"{Iso(now.AddHours(-2))}\",\"valid_until\":\"{Iso(now.AddDays(13))}\"}}";
            var bytes = Encoding.UTF8.GetBytes(json);
            var signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            store.Save(new AccountData("screenshot", "user@example.com", 42, 7, Convert.ToBase64String(bytes), Convert.ToBase64String(signature)));
        }
        var api = new ApiClient(() => T.Language, new ScreenshotApi(), ApiClient.LiveBaseUrl);
        return new AccountService(api, store, new FixedHash(HwHash.Of(hw)), "windows", "PC", AppInfo.Version,
            new[] { key.ExportSubjectPublicKeyInfoPem() });
    }

    private sealed class MemoryStore : IAccountStore
    {
        private AccountData _data = new();

        public AccountData Load() => _data;

        public void Save(AccountData data) => _data = data;
    }

    private sealed class FixedHash(string value) : IHwHash
    {
        public string Value { get; } = value;
    }

    /// <summary>The «server» of a screenshot run: two sample computers; everything else fails as if offline.</summary>
    private sealed class ScreenshotApi : HttpMessageHandler
    {
        private const string Devices = "{\"devices\":[" +
            "{\"id\":42,\"name\":\"DESKTOP-HOME\",\"platform\":\"windows\",\"bound_at\":\"2026-10-24T10:00:00Z\",\"last_seen_at\":\"2026-10-28T09:00:00Z\",\"current\":true}," +
            "{\"id\":43,\"name\":\"MacBook Air\",\"platform\":\"mac\",\"bound_at\":\"2026-10-25T18:30:00Z\",\"last_seen_at\":\"2026-10-27T21:15:00Z\",\"current\":false}" +
            "],\"bind_limit\":2}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/devices", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(Devices, Encoding.UTF8, "application/json"),
                });
            throw new HttpRequestException("screenshot run: no network");
        }
    }
}
```

(Первичные конструкторы классов поддерживаются: проект собирается SDK 10, `LangVersion latest`.)

- [ ] **Step 4: `AppServices.Account` и аргументы**

`src/PcCleaner/Core/AppServices.cs` — рядом с `Updates`:

```csharp
    private static AccountService? _account;

    /// <summary>The account of this run: the real one (created on first use), or the screenshot one App sets for --tier.</summary>
    public static AccountService Account
    {
        get => _account ??= AccountSetup.CreateReal();
        set => _account = value;
    }
```

(`using PcCleaner.Core.Account; using PcCleaner.Core.AccountWindows;`).

`src/PcCleaner/App.xaml.cs`:
1. Свойства рядом с `WelcomeArg`:

```csharp
    /// <summary>--tier guest|free|pro: the account tier of a screenshot run (no network, a local account); null — guest.</summary>
    internal static Tier? TierArg { get; private set; }

    /// <summary>--signin: the screenshot run also takes pictures of the sign-in window, one per step.</summary>
    internal static bool SignInArg { get; private set; }
```

2. В `ParseArgs` сброс `TierArg = null; SignInArg = false;` и случаи:

```csharp
                case "--tier" when i + 1 < args.Length:
                    TierArg = args[++i].Trim().ToLowerInvariant() switch
                    {
                        "guest" => Tier.Guest,
                        "free" => Tier.Free,
                        "pro" => Tier.Pro,
                        _ => null,
                    };
                    break;
                case "--signin":
                    SignInArg = true;
                    break;
```

3. В `OnStartup`, сразу после строки `AppPaths.DataDir = …verify…` (внутри того же `if`):

```csharp
            AppServices.Account = AccountSetup.ForScreenshots(TierArg ?? Tier.Guest);
```

(и комментарий класса: `[--tier guest|free|pro] [--signin]`).

4. Фоновое обновление билета — после `window.Show();` в ветке обычного запуска (`if (ScreenshotDir is null)`), до заставки:

```csharp
            // The ticket refresh of the whole run, off the UI thread (the first hw_hash read is a WMI query).
            _ = Task.Run(async () =>
            {
                try
                {
                    await AppServices.Account.RunRefreshLoopAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Error(T._("Аккаунт: фоновая проверка лицензии остановилась"), ex);
                }
            });
```

`en.json`: `"Аккаунт: фоновая проверка лицензии остановилась": "Account: the background license check stopped",`.

- [ ] **Step 5: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~AccountSetupTests|FullyQualifiedName~ParseArgsTests|FullyQualifiedName~LocalizationCatalogTests"` → PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Accounts: AppServices.Account, --tier for screenshots, background ticket refresh

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Экран при запуске «Войти / Зарегистрироваться / Продолжить без входа»

**Files:**
- Create: `src/PcCleaner/Modules/Profile/AccountIntroViewModel.cs`, `tests/PcCleaner.Tests/Profile/ProfileTestKit.cs` (код — в начале плана)
- Modify: `src/PcCleaner/Core/AppSettings.cs`, `src/PcCleaner/MainViewModel.cs`, `src/PcCleaner/MainWindow.xaml`, `src/PcCleaner/MainWindow.xaml.cs`, `src/PcCleaner/App.xaml.cs`, `src/PcCleaner/Core/ScreenshotRunner.cs`
- Test: `tests/PcCleaner.Tests/Profile/AccountIntroViewModelTests.cs`

**Interfaces:**
- Consumes: `AccountService.IsSignedIn`; `SignInTab` (задача 3 — здесь объявить `public enum SignInTab { Password, Register, Browser }` в `SignInViewModel.cs` заранее, одним перечислением).
- Produces:
  - `public bool AppSettings.AccountIntroShown { get; set; }` (по умолчанию `false`);
  - `public sealed class AccountIntroViewModel : ObservableObject` — конструктор `(Func<AppSettings> settings, Action<AppSettings> save, Func<bool> isSignedIn, Func<SignInTab, bool> signIn)`; `bool IsVisible`, `bool IsUpdate` (режим уже выбран — пользователь обновился); `bool ShowAtStart(bool screenshotRun, bool forced = false)`; `void Hide()`; `event Action? Closed`; `RelayCommand SignInCommand, RegisterCommand, SkipCommand`;
  - `public AccountIntroViewModel MainViewModel.AccountIntro { get; }`;
  - снимок `00-AccountIntro.png` при `--welcome` (до `00-Welcome.png`; имя константой `ScreenshotRunner.AccountIntroFile`).

Правила показа: обычный запуск — пока `AccountIntroShown == false` и не выполнен вход (вошедший пользователь помечается как видевший экран); скриншоты — только при `forced`. Закрыть экран можно тремя путями: вход/регистрация завершились успешно (окно входа вернуло `true`), «Продолжить без входа»; во всех трёх `AccountIntroShown = true` сохраняется. Если окно входа закрыли без входа, экран остаётся.

- [ ] **Step 1: Падающие тесты**

Create `tests/PcCleaner.Tests/Profile/AccountIntroViewModelTests.cs`:

```csharp
using PcCleaner.Core;
using PcCleaner.Modules.Profile;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class AccountIntroViewModelTests
{
    private readonly AppSettings _settings = new();
    private int _saves;
    private bool _signedIn;
    private readonly List<SignInTab> _windows = new();
    private bool _windowResult;

    private AccountIntroViewModel Create() => new(() => _settings, _ => _saves++, () => _signedIn, tab =>
    {
        _windows.Add(tab);
        return _windowResult;
    });

    [Fact]
    public void AFirstRun_ShowsTheScreen_NotAsAnUpdate()
    {
        var vm = Create();

        Assert.True(vm.ShowAtStart(screenshotRun: false));
        Assert.False(vm.IsUpdate);
    }

    [Fact]
    public void AnUpdatedInstall_ShowsItOnce_AsAnUpdate()
    {
        _settings.UiMode = UiMode.Expert;
        var vm = Create();

        Assert.True(vm.ShowAtStart(false));
        Assert.True(vm.IsUpdate);
        vm.SkipCommand.Execute(null);

        Assert.True(_settings.AccountIntroShown);
        Assert.False(Create().ShowAtStart(false));
    }

    [Fact]
    public void ASignedInUser_NeverSeesIt_AndIsMarked()
    {
        _signedIn = true;
        var vm = Create();

        Assert.False(vm.ShowAtStart(false));
        Assert.True(_settings.AccountIntroShown);
        Assert.Equal(1, _saves);
    }

    [Fact]
    public void AScreenshotRun_ShowsItOnlyWhenForced()
    {
        Assert.False(Create().ShowAtStart(screenshotRun: true));
        Assert.True(Create().ShowAtStart(screenshotRun: true, forced: true));
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void SigningIn_OpensTheWindow_AndClosesTheScreenOnlyOnSuccess()
    {
        var vm = Create();
        vm.ShowAtStart(false);
        var closed = 0;
        vm.Closed += () => closed++;

        vm.SignInCommand.Execute(null);
        Assert.True(vm.IsVisible); // the window was closed without signing in

        _windowResult = true;
        vm.RegisterCommand.Execute(null);

        Assert.Equal(new[] { SignInTab.Password, SignInTab.Register }, _windows);
        Assert.False(vm.IsVisible);
        Assert.True(_settings.AccountIntroShown);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Skipping_ClosesTheScreen_AndTellsTheStartToGoOn()
    {
        var vm = Create();
        vm.ShowAtStart(false);
        var closed = 0;
        vm.Closed += () => closed++;

        vm.SkipCommand.Execute(null);

        Assert.False(vm.IsVisible);
        Assert.Equal(1, closed);
        Assert.Empty(_windows);
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~AccountIntroViewModelTests"` → ошибка сборки.

- [ ] **Step 3: Настройка и модель**

`src/PcCleaner/Core/AppSettings.cs`, рядом с `UiMode`:

```csharp
    /// <summary>The start screen «Войти / Зарегистрироваться / Продолжить без входа» was shown (once per install; spec 3.1).</summary>
    public bool AccountIntroShown { get; set; }
```

Проверить, что `ResetSettings` (сброс настроек в «Настройках») не сбрасывает этот флаг заново (иначе экран вернётся): если сброс создаёт новый `AppSettings` и переносит только язык и режим (`ResetSettings_KeepsTheModeAndTheLanguage`), добавить перенос `AccountIntroShown` и проверку в тот же тест.

Create `src/PcCleaner/Modules/Profile/AccountIntroViewModel.cs`:

```csharp
using PcCleaner.Core;

namespace PcCleaner.Modules.Profile;

/// <summary>
/// The start screen over the blurred window, after the splash and before the mode/language screen: «Войти»,
/// «Зарегистрироваться», «Продолжить без входа» (spec 3.1). Once per install: a choice, a sign-in or a signed-in user
/// sets <see cref="AppSettings.AccountIntroShown"/>. For an updated install it adds «Появились аккаунты и Pro».
/// </summary>
public sealed class AccountIntroViewModel : ObservableObject
{
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _save;
    private readonly Func<bool> _isSignedIn;
    private readonly Func<SignInTab, bool> _signIn;
    private bool _isVisible;
    private bool _isUpdate;

    public AccountIntroViewModel(Func<AppSettings> settings, Action<AppSettings> save, Func<bool> isSignedIn, Func<SignInTab, bool> signIn)
    {
        _settings = settings;
        _save = save;
        _isSignedIn = isSignedIn;
        _signIn = signIn;
        SignInCommand = new RelayCommand(() => SignIn(SignInTab.Password));
        RegisterCommand = new RelayCommand(() => SignIn(SignInTab.Register));
        SkipCommand = new RelayCommand(Close);
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => Set(ref _isVisible, value);
    }

    /// <summary>The mode was chosen before: this is an install that was updated from a version without accounts.</summary>
    public bool IsUpdate
    {
        get => _isUpdate;
        private set => Set(ref _isUpdate, value);
    }

    public event Action? Closed;

    public RelayCommand SignInCommand { get; }

    public RelayCommand RegisterCommand { get; }

    public RelayCommand SkipCommand { get; }

    /// <summary>Shows the screen when it is due (see the class); returns whether it is shown now.</summary>
    public bool ShowAtStart(bool screenshotRun, bool forced = false)
    {
        var settings = _settings();
        if (screenshotRun)
        {
            if (forced)
                (IsUpdate, IsVisible) = (settings.UiMode is not null, true);
            return IsVisible;
        }
        if (settings.AccountIntroShown)
            return false;
        if (_isSignedIn())
        {
            settings.AccountIntroShown = true;
            _save(settings);
            return false;
        }
        IsUpdate = settings.UiMode is not null;
        IsVisible = true;
        return true;
    }

    /// <summary>Closes the screen without a choice (the screenshot run, after its picture).</summary>
    public void Hide() => IsVisible = false;

    private void SignIn(SignInTab tab)
    {
        if (_signIn(tab))
            Close();
    }

    private void Close()
    {
        var settings = _settings();
        settings.AccountIntroShown = true;
        _save(settings);
        IsVisible = false;
        Closed?.Invoke();
    }
}
```

- [ ] **Step 4: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~AccountIntroViewModelTests"` → PASS.

- [ ] **Step 5: Оболочка, окно и порядок запуска**

`MainViewModel` — свойство и создание в конструкторе рядом с `Welcome`:

```csharp
    /// <summary>The start screen «Войти / Зарегистрироваться / Продолжить без входа»; App shows it after the splash, before Welcome.</summary>
    public AccountIntroViewModel AccountIntro { get; }
```

```csharp
        AccountIntro = new AccountIntroViewModel(_settings, _save, () => AppServices.Account.IsSignedIn,
            pages is null ? SignInDialog.Show : _ => false);
```

(`SignInDialog.Show(SignInTab)` появится в задаче 6; до неё временно `_ => false` в обоих случаях — заменить в задаче 6, это отмечено там.)

`MainWindow.xaml` — новый оверлей **перед** `WelcomeOverlay` (тот же фон и разметка карточек):

```xml
        <!-- ===== Start screen: accounts (Modules/Profile/AccountIntroViewModel) =====
             After the splash, once per install: sign in, sign up or go on without an account. Then Welcome, if no mode is chosen. -->
        <Grid x:Name="AccountIntroOverlay" Visibility="Collapsed" Focusable="True" FocusVisualStyle="{x:Null}"
              KeyboardNavigation.TabNavigation="Cycle" KeyboardNavigation.ControlTabNavigation="Cycle"
              KeyboardNavigation.DirectionalNavigation="Cycle">
            <Grid.Background>
                <SolidColorBrush Color="{StaticResource BackgroundColor}" Opacity="0.45" />
            </Grid.Background>
            <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center" Width="560">
                <Image Source="{StaticResource AppLogo}" Width="96" Height="96" />
                <TextBlock FontSize="28" FontWeight="SemiBold" HorizontalAlignment="Center" TextAlignment="Center" TextWrapping="Wrap" Margin="0,22,0,0">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock">
                            <Setter Property="Text" Value="{loc:T 'Аккаунт WaterCa7'}" />
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding AccountIntro.IsUpdate}" Value="True">
                                    <Setter Property="Text" Value="{loc:T 'Появились аккаунты и Pro'}" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
                <TextBlock Style="{StaticResource Muted}" FontSize="14" TextAlignment="Center" TextWrapping="Wrap" Margin="0,12,0,0"
                           Text="{loc:T 'Войдите, чтобы включить Pro на этом компьютере, применить промокод и видеть свои компьютеры. Без входа программа работает бесплатно, как раньше.'}" />
                <Button Style="{StaticResource AccentButton}" Margin="0,28,0,0" Padding="18,10" HorizontalAlignment="Stretch"
                        Content="{loc:T 'Войти'}" Command="{Binding AccountIntro.SignInCommand}" />
                <Button Margin="0,10,0,0" Padding="18,10" HorizontalAlignment="Stretch"
                        Content="{loc:T 'Зарегистрироваться'}" Command="{Binding AccountIntro.RegisterCommand}" />
                <Button Style="{StaticResource LinkButton}" Margin="0,16,0,0" HorizontalAlignment="Center"
                        Content="{loc:T 'Продолжить без входа'}" Command="{Binding AccountIntro.SkipCommand}" />
            </StackPanel>
        </Grid>
```

`MainWindow.xaml.cs` — по образцу `_welcome`: поле `private AccountIntroViewModel? _intro;`, подписка/отписка в `OnDataContextChanged`, начальный `ShowOverlay(AccountIntroOverlay, _intro?.IsVisible == true, animate: false, () => _intro?.IsVisible == true, fadeIn: true);` и обработчик:

```csharp
    private void OnIntroChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountIntroViewModel.IsVisible))
            OnUi(() => ShowOverlay(AccountIntroOverlay, _intro?.IsVisible == true, animate: true, () => _intro?.IsVisible == true, fadeIn: true));
    }
```

Если в `ShowOverlay`/соседних методах есть список «других» оверлеев (строка с `!ReferenceEquals(overlay, SplashOverlay) && SplashOverlay.Visibility == Visibility.Visible`), добавить в него `AccountIntroOverlay` так же, как там учтён `WelcomeOverlay` (размытие снимается, только когда закрыты все оверлеи).

`App.xaml.cs` — экран входа между заставкой и приветствием:

```csharp
    /// <summary>The start screen of accounts if due, then the first-run screen if no mode is chosen yet.</summary>
    private static void ShowIntroThenWelcome(MainViewModel vm)
    {
        if (!vm.AccountIntro.ShowAtStart(screenshotRun: false))
        {
            vm.Welcome.ShowAtStart(screenshotRun: false);
            return;
        }
        void OnClosed()
        {
            vm.AccountIntro.Closed -= OnClosed;
            vm.Welcome.ShowAtStart(screenshotRun: false);
        }
        vm.AccountIntro.Closed += OnClosed;
    }
```

и заменить оба места, где сейчас зовётся `vm.Welcome.ShowAtStart(screenshotRun: false)` (ветка `UpdatedFrom` и `RunSplashThenWelcomeAsync`), на `ShowIntroThenWelcome(vm)`.

`ScreenshotRunner`: константа `internal const string AccountIntroFile = "00-AccountIntro.png";` и в `RunAsync` при `welcome` — перед `ShootWelcomeAsync` такой же снимок экрана входа (`vm.AccountIntro.ShowAtStart(screenshotRun: true, forced: true)`, пауза 700 мс, `Save`, `vm.AccountIntro.Hide()`, пауза 700 мс; метод `ShootAccountIntroAsync` по образцу `ShootWelcomeAsync`).

- [ ] **Step 6: Сборка, тесты, снимок**

Run: `dotnet build src/PcCleaner -c Debug` → `Build succeeded`; `dotnet test tests/PcCleaner.Tests` → PASS (тест каталога потребует английские записи для новых текстов — добавить).

```powershell
Start-Process -Wait src\PcCleaner\bin\Debug\net7.0-windows\WaterCa7sPcCleaner.exe -ArgumentList "--screenshots", "$env:TEMP\b2-intro", "--page", "Dashboard", "--no-scan", "--welcome"
```

Открыть `00-AccountIntro.png`: логотип, заголовок, три кнопки по центру, окно позади размыто; `00-Welcome.png` — как раньше.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Accounts: the start screen (sign in / sign up / go on without an account), once per install

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Окно входа — вход по паролю и сброс пароля (модель)

**Files:**
- Create: `src/PcCleaner/Modules/Profile/SignInViewModel.cs`, `src/PcCleaner/Modules/Profile/SignInErrors.cs`
- Test: `tests/PcCleaner.Tests/Profile/SignInPasswordTests.cs`

**Interfaces:**
- Consumes: `AccountService` (`LoginAsync`, `ResetAsync`, `ResetConfirmAsync`, `ResendAsync`, `ReleaseAsync`), `ApiException`, `ApiError`, `DeviceLimitInfo`.
- Produces:
  - `public enum SignInTab { Password, Register, Browser }` (если объявлен в задаче 2 — оставить там);
  - `public enum SignInStep { Password, ResetEmail, ResetCode, Register, VerifyCode, Browser, DeviceLimit }`;
  - `public sealed partial class SignInViewModel : ObservableObject` — конструктор `(AccountService account, Action<string> openUrl, SignInTab tab = SignInTab.Password, Func<DateTimeOffset>? now = null)`; свойства `SignInStep Step`, `SignInTab Tab` (запись переключает шаг), `string Email`, `string Password`, `string Password2`, `string Code`, `bool Consent`, `bool IsBusy`, `string? Error`, `string? Info`, булевы `IsPasswordStep`… `IsDeviceLimitStep`, `bool ShowTabs`; команды `SignInCommand`, `ForgotCommand`, `SendResetCommand`, `ConfirmResetCommand`, `BackCommand`; событие `event Action? Completed`; `Task RunAsync(Func<Task> action)` (внутренний общий обработчик: `IsBusy`, `Error`, перевод `DEVICE_LIMIT` и `EMAIL_NOT_VERIFIED` в свои шаги);
  - `public static class SignInErrors` с `string Text(ApiError error)`, `bool IsValidEmail(string)`, `bool IsValidPassword(string)`.

Тексты ошибок (`SignInErrors.Text`):

| Код | Текст |
|---|---|
| `AUTH_FAILED` | «Неверная почта или пароль. Аккаунт создан через Google? Войдите через браузер.» |
| `EMAIL_TAKEN` | «Аккаунт с этой почтой уже есть — войдите в него.» |
| `CODE_INVALID` | «Неверный или устаревший код. Проверьте письмо или отправьте код ещё раз.» |
| `WEAK_PASSWORD` | «Пароль должен быть от 8 до 128 символов.» |
| `EMAIL_INVALID`, `INVALID_INPUT` | «Проверьте адрес почты.» |
| `CONSENT_REQUIRED` | «Нужно согласие с офертой и политикой конфиденциальности.» |
| `RATE_LIMITED` | «Слишком много попыток. Подождите несколько минут и попробуйте снова.» |
| `UNBIND_QUOTA` | `message` сервера (в нём срок), иначе «Отвязать компьютер можно раз в 7 дней.» |
| `NETWORK` | «Нет связи с waterca7.ru. Проверьте интернет.» |
| `SERVER_ERROR` | «Сервер не ответил. Попробуйте позже.» |
| `NOT_FOUND` | «Код устарел. Начните вход заново.» |
| прочие | `message` сервера, если есть, иначе `T.F("Не получилось ({0}). Попробуйте ещё раз.", code)` |

- [ ] **Step 1: Падающие тесты**

Create `tests/PcCleaner.Tests/Profile/SignInPasswordTests.cs`:

```csharp
using PcCleaner.Core.Account;
using PcCleaner.Modules.Profile;
using PcCleaner.Tests.Account;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class SignInPasswordTests : IDisposable
{
    private readonly TestAccount _a = new();
    private int _completed;

    public SignInPasswordTests() => TestEnvironment.Init();

    public void Dispose() => _a.Dispose();

    private SignInViewModel Create(SignInTab tab = SignInTab.Password)
    {
        var vm = new SignInViewModel(_a.Service, _a.Open, tab, () => _a.Now);
        vm.Completed += () => _completed++;
        return vm;
    }

    private static async Task Run(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
            await async.LastTask;
    }

    [Fact]
    public async Task AGoodPassword_SignsIn_AndCompletes()
    {
        _a.Fake.On("POST /auth/login", 200, _a.Key.LoginJson(TestLicense.Json()));
        var vm = Create();
        vm.Email = " user@example.com ";
        vm.Password = "password1";

        await Run(vm.SignInCommand);

        Assert.Equal(1, _completed);
        Assert.True(_a.Service.IsSignedIn);
        Assert.Equal("user@example.com", Api.Body(_a.Fake.Requests[0]).GetProperty("email").GetString());
    }

    [Theory]
    [InlineData("not-an-email", "password1")]
    [InlineData("a@b", "password1")]
    [InlineData("user@example.com", "short")]
    public async Task BadInput_IsCaughtBeforeTheServer(string email, string password)
    {
        var vm = Create();
        vm.Email = email;
        vm.Password = password;

        await Run(vm.SignInCommand);

        Assert.NotNull(vm.Error);
        Assert.Empty(_a.Fake.Requests);
    }

    [Fact]
    public async Task AWrongPassword_SaysSo_AndPointsToTheBrowser()
    {
        _a.Fake.OnError("POST /auth/login", 401, "AUTH_FAILED");
        var vm = Create();
        vm.Email = "user@example.com";
        vm.Password = "password1";

        await Run(vm.SignInCommand);

        Assert.Equal(SignInStep.Password, vm.Step);
        Assert.Contains("браузер", vm.Error);
        Assert.Equal(0, _completed);
    }

    [Fact]
    public async Task AnUnverifiedEmail_GoesToTheCodeStep_AndSendsANewCode()
    {
        _a.Fake.OnError("POST /auth/login", 403, "EMAIL_NOT_VERIFIED")
            .On("POST /auth/resend", 200, "{\"ok\":true,\"retry_after\":60}")
            .On("POST /auth/verify", 200, _a.Key.LoginJson(TestLicense.Json()));
        var vm = Create();
        vm.Email = "user@example.com";
        vm.Password = "password1";

        await Run(vm.SignInCommand);

        Assert.Equal(SignInStep.VerifyCode, vm.Step);
        Assert.Equal("verify", Api.Body(_a.Fake.To("POST /auth/resend").Single()).GetProperty("purpose").GetString());
        vm.Code = "123456";
        await Run(vm.VerifyCommand);
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task TheReset_SendsACode_ThenSetsTheNewPassword_AndSignsIn()
    {
        _a.Fake.On("POST /auth/reset", 200, "{\"ok\":true}")
            .On("POST /auth/reset/confirm", 200, _a.Key.LoginJson(TestLicense.Json()));
        var vm = Create();
        vm.Email = "user@example.com";

        vm.ForgotCommand.Execute(null);
        Assert.Equal(SignInStep.ResetEmail, vm.Step);
        await Run(vm.SendResetCommand);
        Assert.Equal(SignInStep.ResetCode, vm.Step);
        Assert.Contains("если адрес верен", vm.Info);

        vm.Code = "111111";
        vm.Password = "new-password";
        vm.Password2 = "new-passwrd";
        await Run(vm.ConfirmResetCommand);
        Assert.NotNull(vm.Error); // the two passwords differ
        Assert.Empty(_a.Fake.To("POST /auth/reset/confirm"));

        vm.Password2 = "new-password";
        await Run(vm.ConfirmResetCommand);
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task ADeviceLimit_ShowsTheComputers_AndAfterReleaseSignsInWithThePassword()
    {
        _a.Fake.OnError("POST /auth/login", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office"), (6, "Laptop")))
            .On("POST /auth/login", 200, _a.Key.LoginJson(TestLicense.Json()))
            .On("POST /device/release", 200, "{\"ok\":true}");
        var vm = Create();
        vm.Email = "user@example.com";
        vm.Password = "password1";

        await Run(vm.SignInCommand);
        Assert.Equal(SignInStep.DeviceLimit, vm.Step);
        Assert.Equal(new[] { "Office", "Laptop" }, vm.Devices.Select(d => d.Name));

        await vm.ReleaseAsync(vm.Devices[0]);

        Assert.Equal(5, Api.Body(_a.Fake.To("POST /device/release").Single()).GetProperty("device_id").GetInt64());
        Assert.Equal(2, _a.Fake.To("POST /auth/login").Count());
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task ABurntPermit_AsksAgain_AndShowsTheNewList()
    {
        _a.Fake.OnError("POST /auth/login", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")))
            .OnError("POST /device/release", 429, "UNBIND_QUOTA");
        var vm = Create();
        vm.Email = "user@example.com";
        vm.Password = "password1";
        await Run(vm.SignInCommand);

        await vm.ReleaseAsync(vm.Devices[0]);

        Assert.Equal(SignInStep.DeviceLimit, vm.Step);
        Assert.Equal("msg UNBIND_QUOTA", vm.Error);
        Assert.Equal(2, _a.Fake.To("POST /auth/login").Count()); // a fresh DEVICE_LIMIT with a new permit
    }

    [Fact]
    public async Task NoNetwork_SaysSo()
    {
        _a.Fake.ThrowOnSend = new System.Net.Http.HttpRequestException("down");
        var vm = Create();
        vm.Email = "user@example.com";
        vm.Password = "password1";

        await Run(vm.SignInCommand);

        Assert.Contains("waterca7.ru", vm.Error);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Back_FromTheReset_ReturnsToThePassword()
    {
        var vm = Create();
        vm.ForgotCommand.Execute(null);

        vm.BackCommand.Execute(null);

        Assert.Equal(SignInStep.Password, vm.Step);
        Assert.True(vm.ShowTabs);
    }

    [Fact]
    public void ATab_PicksItsStep()
    {
        var vm = Create(SignInTab.Register);
        Assert.Equal(SignInStep.Register, vm.Step);

        vm.Tab = SignInTab.Password;
        Assert.Equal(SignInStep.Password, vm.Step);
        Assert.True(vm.IsPasswordStep);
    }
}
```

`AsyncRelayCommand.LastTask` — если у нынешнего `AsyncRelayCommand` (`src/PcCleaner/Core/Commands.cs`) нет способа дождаться выполнения, добавить свойство `public Task LastTask { get; private set; } = Task.CompletedTask;`, присваиваемое в `Execute`, с тестом в существующих тестах команд. Тесты модели вызывают методы `…Async` напрямую, где они публичны (`ReleaseAsync`).

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~SignInPasswordTests"` → ошибка сборки.

- [ ] **Step 3: `SignInErrors`**

Create `src/PcCleaner/Modules/Profile/SignInErrors.cs`:

```csharp
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

/// <summary>What the sign-in window says for an API error (spec 3.2), and the checks the site's form makes too.</summary>
public static class SignInErrors
{
    public static string Text(ApiError error) => error.Code switch
    {
        ApiError.AuthFailed => T._("Неверная почта или пароль. Аккаунт создан через Google? Войдите через браузер."),
        ApiError.EmailTaken => T._("Аккаунт с этой почтой уже есть — войдите в него."),
        ApiError.CodeInvalid => T._("Неверный или устаревший код. Проверьте письмо или отправьте код ещё раз."),
        ApiError.WeakPassword => T._("Пароль должен быть от 8 до 128 символов."),
        ApiError.EmailInvalid or ApiError.InvalidInput => T._("Проверьте адрес почты."),
        ApiError.ConsentRequired => T._("Нужно согласие с офертой и политикой конфиденциальности."),
        ApiError.RateLimited => T._("Слишком много попыток. Подождите несколько минут и попробуйте снова."),
        ApiError.UnbindQuota => error.Message.Length > 0 ? error.Message : T._("Отвязать компьютер можно раз в 7 дней."),
        ApiError.Network => T._("Нет связи с waterca7.ru. Проверьте интернет."),
        ApiError.ServerError => T._("Сервер не ответил. Попробуйте позже."),
        ApiError.NotFound => T._("Код устарел. Начните вход заново."),
        _ => error.Message.Length > 0 ? error.Message : T.F("Не получилось ({0}). Попробуйте ещё раз.", error.Code),
    };

    /// <summary>The form's check: up to 254 characters, an «@» and a dot after it with something on both sides.</summary>
    public static bool IsValidEmail(string? email)
    {
        var e = (email ?? "").Trim();
        var at = e.IndexOf('@');
        var dot = at < 0 ? -1 : e.IndexOf('.', at + 2);
        return e.Length is > 0 and <= 254 && at > 0 && dot > at + 1 && dot < e.Length - 1 && !e.Contains(' ');
    }

    /// <summary>8–128 characters, spaces count (the site does not trim passwords).</summary>
    public static bool IsValidPassword(string? password) => password is { Length: >= 8 and <= 128 };
}
```

- [ ] **Step 4: `SignInViewModel` (общая часть, пароль, сброс, лимит)**

Create `src/PcCleaner/Modules/Profile/SignInViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using PcCleaner.Core;
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

public enum SignInTab
{
    Password,
    Register,
    Browser,
}

public enum SignInStep
{
    Password,
    ResetEmail,
    ResetCode,
    Register,
    VerifyCode,
    Browser,
    DeviceLimit,
}

/// <summary>A computer of the account in the DEVICE_LIMIT list.</summary>
public sealed record LimitDevice(long Id, string Name, string Details);

/// <summary>
/// The sign-in window (spec 3.2): password, sign-up with the mail code, password reset, the browser code, and the list of the
/// account's computers when DEVICE_LIMIT stops a sign-in. Everything happens here, no browser except for the browser code
/// and the documents. <see cref="Completed"/> fires once a sign-in succeeded; the window then closes.
/// </summary>
public sealed partial class SignInViewModel : ObservableObject
{
    private readonly AccountService _account;
    private readonly Action<string> _openUrl;
    private readonly Func<DateTimeOffset> _now;
    private SignInStep _step;
    private SignInTab _tab;
    private string _email = "";
    private string _password = "";
    private string _password2 = "";
    private string _code = "";
    private bool _consent;
    private bool _isBusy;
    private string? _error;
    private string? _info;
    private string? _limitText;
    private DeviceLimitInfo? _limit;
    private Func<Task>? _retry;

    public SignInViewModel(AccountService account, Action<string> openUrl, SignInTab tab = SignInTab.Password, Func<DateTimeOffset>? now = null)
    {
        _account = account;
        _openUrl = openUrl;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        SignInCommand = new AsyncRelayCommand(() => RunAsync(SignInAsync), () => !IsBusy);
        ForgotCommand = new RelayCommand(() => GoTo(SignInStep.ResetEmail));
        SendResetCommand = new AsyncRelayCommand(() => RunAsync(SendResetAsync), () => !IsBusy);
        ConfirmResetCommand = new AsyncRelayCommand(() => RunAsync(ConfirmResetAsync), () => !IsBusy);
        BackCommand = new RelayCommand(Back);
        InitRegister();
        InitBrowser();
        Tab = tab;
    }

    public event Action? Completed;

    public SignInStep Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, value))
                return;
            foreach (var name in new[] { nameof(IsPasswordStep), nameof(IsResetEmailStep), nameof(IsResetCodeStep), nameof(IsRegisterStep),
                         nameof(IsVerifyStep), nameof(IsBrowserStep), nameof(IsDeviceLimitStep), nameof(ShowTabs) })
                OnPropertyChanged(name);
        }
    }

    /// <summary>The three tabs at the top; choosing one starts its step (the browser tab asks for a code at once).</summary>
    public SignInTab Tab
    {
        get => _tab;
        set
        {
            Set(ref _tab, value);
            Error = null;
            Info = null;
            switch (value)
            {
                case SignInTab.Password:
                    GoTo(SignInStep.Password);
                    break;
                case SignInTab.Register:
                    GoTo(SignInStep.Register);
                    break;
                case SignInTab.Browser:
                    GoTo(SignInStep.Browser);
                    break;
            }
        }
    }

    public bool IsPasswordStep => Step == SignInStep.Password;
    public bool IsResetEmailStep => Step == SignInStep.ResetEmail;
    public bool IsResetCodeStep => Step == SignInStep.ResetCode;
    public bool IsRegisterStep => Step == SignInStep.Register;
    public bool IsVerifyStep => Step == SignInStep.VerifyCode;
    public bool IsBrowserStep => Step == SignInStep.Browser;
    public bool IsDeviceLimitStep => Step == SignInStep.DeviceLimit;

    /// <summary>The tabs are shown on the first step of each tab only.</summary>
    public bool ShowTabs => Step is SignInStep.Password or SignInStep.Register or SignInStep.Browser;

    public string Email { get => _email; set => Set(ref _email, value); }

    public string Password { get => _password; set => Set(ref _password, value); }

    public string Password2 { get => _password2; set => Set(ref _password2, value); }

    public string Code { get => _code; set => Set(ref _code, value); }

    public bool Consent { get => _consent; set => Set(ref _consent, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                RelayCommand.Refresh();
        }
    }

    public string? Error { get => _error; private set => Set(ref _error, value); }

    public string? Info { get => _info; private set => Set(ref _info, value); }

    /// <summary>The server's words about the limit (the plain limit, the family owner, a family member).</summary>
    public string? LimitText { get => _limitText; private set => Set(ref _limitText, value); }

    public ObservableCollection<LimitDevice> Devices { get; } = new();

    public AsyncRelayCommand SignInCommand { get; }
    public RelayCommand ForgotCommand { get; }
    public AsyncRelayCommand SendResetCommand { get; }
    public AsyncRelayCommand ConfirmResetCommand { get; }
    public RelayCommand BackCommand { get; }

    // ----- password -----

    private async Task SignInAsync()
    {
        if (!CheckEmail() || !CheckPassword(Password))
            return;
        var (email, password) = (Email.Trim(), Password);
        _retry = () => _account.LoginAsync(email, password, CancellationToken.None);
        await _retry();
        Done();
    }

    // ----- reset -----

    private async Task SendResetAsync()
    {
        if (!CheckEmail())
            return;
        await _account.ResetAsync(Email.Trim(), CancellationToken.None);
        StartResendCountdown(60);
        GoTo(SignInStep.ResetCode);
        Info = T._("Мы отправили код на почту, если адрес верен. Не пришло — проверьте «Спам».");
    }

    private async Task ConfirmResetAsync()
    {
        if (!CheckCode() || !CheckPassword(Password) || !CheckSame())
            return;
        var (email, password) = (Email.Trim(), Password);
        // DEVICE_LIMIT after reset/confirm: the code is spent, the password changed — after a release, log in with the new password.
        _retry = () => _account.LoginAsync(email, password, CancellationToken.None);
        await _account.ResetConfirmAsync(email, Code, password, CancellationToken.None);
        Done();
    }

    // ----- DEVICE_LIMIT -----

    /// <summary>«Отвязать» in the list: releases the computer with the last permit, then repeats the sign-in the right way.</summary>
    public async Task ReleaseAsync(LimitDevice device)
    {
        if (_limit is null || IsBusy)
            return;
        await RunAsync(async () =>
        {
            try
            {
                await _account.ReleaseAsync(_limit.UnbindToken, device.Id, CancellationToken.None);
            }
            catch (ApiException ex) when (ex.Error.Code is ApiError.NotFound or ApiError.UnbindQuota or ApiError.AuthFailed)
            {
                // The permit is spent: show why, and ask again for a fresh DEVICE_LIMIT (a new list and permit).
                var reason = SignInErrors.Text(ex.Error);
                if (_retry is not null)
                {
                    try
                    {
                        await _retry();
                        Done();
                        return;
                    }
                    catch (ApiException again) when (again.Error.Code == ApiError.DeviceLimit && again.Error.AsDeviceLimit() is { } limit)
                    {
                        ShowLimit(limit, again.Error.Message);
                    }
                }
                Error = reason;
                return;
            }
            if (_retry is not null)
                await _retry();
            Done();
        });
    }

    private void ShowLimit(DeviceLimitInfo limit, string message)
    {
        _limit = limit;
        LimitText = message.Length > 0 ? message : T.F("Заняты все компьютеры аккаунта ({0}). Отвяжите один, чтобы войти на этом.", limit.BindLimit);
        Devices.Clear();
        foreach (var d in limit.Devices)
            Devices.Add(new LimitDevice(d.Id, d.Name, ProfileTexts.DeviceDetails(d, _now())));
        GoTo(SignInStep.DeviceLimit);
    }

    // ----- common -----

    /// <summary>Runs one action of the window: busy while it runs, errors become <see cref="Error"/>, DEVICE_LIMIT and an unverified e-mail open their steps.</summary>
    internal async Task RunAsync(Func<Task> action)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Error = null;
        try
        {
            await action();
        }
        catch (ApiException ex) when (ex.Error.Code == ApiError.DeviceLimit && ex.Error.AsDeviceLimit() is { } limit)
        {
            ShowLimit(limit, ex.Error.Message);
        }
        catch (ApiException ex) when (ex.Error.Code == ApiError.EmailNotVerified)
        {
            await GoVerifyAsync();
        }
        catch (ApiException ex)
        {
            Error = SignInErrors.Text(ex.Error);
            if (ex.Error.Code == ApiError.EmailTaken)
                GoTo(SignInStep.Password);
        }
        catch (OperationCanceledException)
        {
            // the user cancelled
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Done()
    {
        if (!_account.IsSignedIn)
            return;
        Password = Password2 = Code = "";
        Completed?.Invoke();
    }

    private void GoTo(SignInStep step)
    {
        if (step != SignInStep.Browser)
            StopBrowser();
        Step = step;
        if (step == SignInStep.Browser)
            StartBrowser();
    }

    private void Back()
    {
        Error = null;
        Info = null;
        GoTo(Step switch
        {
            SignInStep.ResetEmail or SignInStep.ResetCode => SignInStep.Password,
            SignInStep.VerifyCode => _tab == SignInTab.Register ? SignInStep.Register : SignInStep.Password,
            SignInStep.DeviceLimit => _tab switch
            {
                SignInTab.Register => SignInStep.Register,
                SignInTab.Browser => SignInStep.Browser,
                _ => SignInStep.Password,
            },
            _ => Step,
        });
        CancelDeviceLimitWait();
    }

    private bool CheckEmail()
    {
        if (SignInErrors.IsValidEmail(Email))
            return true;
        Error = T._("Проверьте адрес почты.");
        return false;
    }

    private bool CheckPassword(string password)
    {
        if (SignInErrors.IsValidPassword(password))
            return true;
        Error = T._("Пароль должен быть от 8 до 128 символов.");
        return false;
    }

    private bool CheckSame()
    {
        if (Password == Password2)
            return true;
        Error = T._("Пароли не совпадают.");
        return false;
    }

    private bool CheckCode()
    {
        Code = new string(Code.Where(char.IsDigit).ToArray());
        if (Code.Length == 6)
            return true;
        Error = T._("Введите 6 цифр из письма.");
        return false;
    }
}
```

`InitRegister`, `GoVerifyAsync`, `StartResendCountdown`, `VerifyCommand` появятся в задаче 4; `InitBrowser`, `StartBrowser`, `StopBrowser`, `CancelDeviceLimitWait` — в задаче 5. Чтобы задача 3 собиралась и проходила свои тесты, создать сразу `SignInViewModel.Register.cs` и `SignInViewModel.Browser.cs` с заготовками этих членов из задач 4 и 5 — **то есть выполнить шаг 3 задачи 4 и шаг 3 задачи 5 в этой задаче** (их тесты добавятся позже); если исполнитель делит работу строго по задачам — пустые тела (`private void InitRegister() { }` и т. д.) и `public AsyncRelayCommand VerifyCommand { get; } = null!;` недопустимы: тест `AnUnverifiedEmail_GoesToTheCodeStep` их использует. Поэтому задачи 3–5 выполнять подряд одним исполнителем, коммит — после каждой.

`ProfileTexts.DeviceDetails` появится в задаче 7; до неё в `ShowLimit` использовать `ProfileTexts.DeviceDetails`, создав в этой задаче файл `ProfileTexts.cs` с одним этим методом из задачи 7 (код — там, шаг 3).

- [ ] **Step 5: Тесты проходят** (после задач 4–5 в той же сборке)

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~SignInPasswordTests"` → PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Accounts: sign-in window model — password, reset, DEVICE_LIMIT release

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Окно входа — регистрация и код из письма

**Files:**
- Create: `src/PcCleaner/Modules/Profile/SignInViewModel.Register.cs`
- Test: `tests/PcCleaner.Tests/Profile/SignInRegisterTests.cs`

**Interfaces:**
- Produces (в `SignInViewModel`): `AsyncRelayCommand RegisterCommand`, `AsyncRelayCommand VerifyCommand`, `AsyncRelayCommand ResendCommand`, `RelayCommand OpenOfferCommand`, `RelayCommand OpenPrivacyCommand`; `bool CanResend`, `string ResendText`; `void Tick()` (окно зовёт раз в секунду — обновляет отсчёт); `const string OfferUrl = "https://waterca7.ru/legal/offer"`, `PrivacyUrl = "https://waterca7.ru/legal/privacy"` (адреса проверить по `A:\waterca7.ru\app\lib\Router.php` / подвалу сайта и взять настоящие).

- [ ] **Step 1: Падающие тесты**

Create `tests/PcCleaner.Tests/Profile/SignInRegisterTests.cs`:

```csharp
using PcCleaner.Modules.Profile;
using PcCleaner.Tests.Account;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class SignInRegisterTests : IDisposable
{
    private readonly TestAccount _a = new();
    private int _completed;

    public SignInRegisterTests() => TestEnvironment.Init();

    public void Dispose() => _a.Dispose();

    private SignInViewModel Create()
    {
        var vm = new SignInViewModel(_a.Service, _a.Open, SignInTab.Register, () => _a.Now);
        vm.Completed += () => _completed++;
        vm.Email = "new@example.com";
        vm.Password = vm.Password2 = "password1";
        vm.Consent = true;
        return vm;
    }

    [Fact]
    public async Task Register_ThenTheCode_SignsIn()
    {
        _a.Fake.On("POST /auth/register", 200, "{\"pending\":true}")
            .On("POST /auth/verify", 200, _a.Key.LoginJson(TestLicense.Json(plan: "free", noExpiry: true)));
        var vm = Create();

        await vm.RunAsync(() => vm.RegisterForTestAsync());
        Assert.Equal(SignInStep.VerifyCode, vm.Step);
        Assert.Contains("если адрес верен", vm.Info);

        vm.Code = "12 34 56";
        await vm.RunAsync(() => vm.VerifyForTestAsync());

        Assert.Equal("123456", Api.Body(_a.Fake.To("POST /auth/verify").Single()).GetProperty("code").GetString());
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task WithoutConsent_OrDifferentPasswords_NothingIsSent()
    {
        var vm = Create();
        vm.Consent = false;
        await vm.RunAsync(() => vm.RegisterForTestAsync());
        Assert.NotNull(vm.Error);

        vm.Consent = true;
        vm.Password2 = "password2";
        await vm.RunAsync(() => vm.RegisterForTestAsync());
        Assert.NotNull(vm.Error);

        Assert.Empty(_a.Fake.Requests);
    }

    [Fact]
    public async Task ATakenEmail_SendsTheUserToSignIn()
    {
        _a.Fake.OnError("POST /auth/register", 409, "EMAIL_TAKEN");
        var vm = Create();

        await vm.RunAsync(() => vm.RegisterForTestAsync());

        Assert.Equal(SignInStep.Password, vm.Step);
        Assert.Contains("уже есть", vm.Error);
    }

    [Fact]
    public async Task Resend_WaitsItsMinute_ThenSendsAgain()
    {
        _a.Fake.On("POST /auth/register", 200, "{\"pending\":true}")
            .On("POST /auth/resend", 200, "{\"ok\":true,\"retry_after\":60}");
        var vm = Create();
        await vm.RunAsync(() => vm.RegisterForTestAsync());

        Assert.False(vm.CanResend);
        Assert.Contains("60", vm.ResendText);

        _a.Now += TimeSpan.FromSeconds(59);
        vm.Tick();
        Assert.False(vm.CanResend);
        Assert.Contains("1", vm.ResendText);

        _a.Now += TimeSpan.FromSeconds(1);
        vm.Tick();
        Assert.True(vm.CanResend);
        await vm.RunAsync(() => vm.ResendForTestAsync());

        Assert.Equal("verify", Api.Body(_a.Fake.To("POST /auth/resend").Single()).GetProperty("purpose").GetString());
        Assert.False(vm.CanResend);
    }

    [Fact]
    public async Task ADeviceLimitAfterVerify_SignsInWithThePassword_AfterTheRelease()
    {
        _a.Fake.On("POST /auth/register", 200, "{\"pending\":true}")
            .OnError("POST /auth/verify", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")))
            .On("POST /device/release", 200, "{\"ok\":true}")
            .On("POST /auth/login", 200, _a.Key.LoginJson(TestLicense.Json()));
        var vm = Create();
        await vm.RunAsync(() => vm.RegisterForTestAsync());
        vm.Code = "123456";
        await vm.RunAsync(() => vm.VerifyForTestAsync());
        Assert.Equal(SignInStep.DeviceLimit, vm.Step);

        await vm.ReleaseAsync(vm.Devices[0]);

        Assert.Single(_a.Fake.To("POST /auth/verify")); // the code is spent: not repeated
        Assert.Equal("password1", Api.Body(_a.Fake.To("POST /auth/login").Single()).GetProperty("password").GetString());
        Assert.Equal(1, _completed);
    }

    [Fact]
    public void TheDocuments_OpenInTheBrowser()
    {
        var vm = Create();

        vm.OpenOfferCommand.Execute(null);
        vm.OpenPrivacyCommand.Execute(null);

        Assert.Equal(new[] { SignInViewModel.OfferUrl, SignInViewModel.PrivacyUrl }, _a.Opened);
    }
}
```

(Методы `RegisterForTestAsync`, `VerifyForTestAsync`, `ResendForTestAsync` — `internal` обёртки над закрытыми `RegisterAsync`, `VerifyAsync`, `ResendAsync`: тесты зовут их через `RunAsync`, как команда. Вместо них можно использовать `LastTask` команд из задачи 3 — выбрать один способ на весь файл.)

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~SignInRegisterTests"` → ошибка сборки.

- [ ] **Step 3: Код**

Create `src/PcCleaner/Modules/Profile/SignInViewModel.Register.cs`:

```csharp
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

// Sign-up, the mail code and «Отправить ещё раз».
public sealed partial class SignInViewModel
{
    public const string OfferUrl = "https://waterca7.ru/legal/offer";
    public const string PrivacyUrl = "https://waterca7.ru/legal/privacy";

    private DateTimeOffset _resendAt;

    public AsyncRelayCommand RegisterCommand { get; private set; } = null!;
    public AsyncRelayCommand VerifyCommand { get; private set; } = null!;
    public AsyncRelayCommand ResendCommand { get; private set; } = null!;
    public RelayCommand OpenOfferCommand { get; private set; } = null!;
    public RelayCommand OpenPrivacyCommand { get; private set; } = null!;

    public bool CanResend => _now() >= _resendAt;

    public string ResendText => CanResend
        ? T._("Отправить код ещё раз")
        : T.F("Отправить ещё раз через {0} с", (int)Math.Ceiling((_resendAt - _now()).TotalSeconds));

    /// <summary>The window calls it every second: the countdown of «Отправить ещё раз».</summary>
    public void Tick()
    {
        OnPropertyChanged(nameof(CanResend));
        OnPropertyChanged(nameof(ResendText));
        ResendCommand.RaiseCanExecuteChanged();
    }

    private void InitRegister()
    {
        RegisterCommand = new AsyncRelayCommand(() => RunAsync(RegisterAsync), () => !IsBusy);
        VerifyCommand = new AsyncRelayCommand(() => RunAsync(VerifyAsync), () => !IsBusy);
        ResendCommand = new AsyncRelayCommand(() => RunAsync(ResendAsync), () => !IsBusy && CanResend);
        OpenOfferCommand = new RelayCommand(() => _openUrl(OfferUrl));
        OpenPrivacyCommand = new RelayCommand(() => _openUrl(PrivacyUrl));
    }

    internal Task RegisterForTestAsync() => RegisterAsync();
    internal Task VerifyForTestAsync() => VerifyAsync();
    internal Task ResendForTestAsync() => ResendAsync();

    private async Task RegisterAsync()
    {
        if (!CheckEmail() || !CheckPassword(Password) || !CheckSame())
            return;
        if (!Consent)
        {
            Error = T._("Нужно согласие с офертой и политикой конфиденциальности.");
            return;
        }
        await _account.RegisterAsync(Email.Trim(), Password, T.Language, CancellationToken.None);
        StartResendCountdown(60);
        GoTo(SignInStep.VerifyCode);
        Info = T._("Мы отправили код на почту, если адрес верен. Не пришло — проверьте «Спам».");
    }

    private async Task VerifyAsync()
    {
        if (!CheckCode())
            return;
        var (email, password) = (Email.Trim(), Password);
        // DEVICE_LIMIT after verify: the code is spent and the e-mail confirmed — after a release, log in with the password.
        _retry = () => _account.LoginAsync(email, password, CancellationToken.None);
        await _account.VerifyAsync(email, Code, CancellationToken.None);
        Done();
    }

    private async Task ResendAsync()
    {
        var purpose = Step == SignInStep.ResetCode ? "reset" : "verify";
        StartResendCountdown(await _account.ResendAsync(Email.Trim(), purpose, CancellationToken.None));
        Info = T._("Мы отправили новый код, если адрес верен. Не пришло — проверьте «Спам».");
    }

    /// <summary>The login of an unverified e-mail: the code step, with a new code sent at once.</summary>
    private async Task GoVerifyAsync()
    {
        var (email, password) = (Email.Trim(), Password);
        _retry = () => _account.LoginAsync(email, password, CancellationToken.None);
        GoTo(SignInStep.VerifyCode);
        Info = T._("Почта ещё не подтверждена. Мы отправили код, если адрес верен.");
        try
        {
            StartResendCountdown(await _account.ResendAsync(email, "verify", CancellationToken.None));
        }
        catch (Core.Account.ApiException ex)
        {
            Error = SignInErrors.Text(ex.Error);
        }
    }

    private void StartResendCountdown(int seconds)
    {
        _resendAt = _now() + TimeSpan.FromSeconds(Math.Max(seconds, 1));
        Tick();
    }
}
```

- [ ] **Step 4: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~SignInRegisterTests|FullyQualifiedName~SignInPasswordTests"` → PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Accounts: sign-up with the mail code and the resend countdown

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Окно входа — вход через браузер

**Files:**
- Create: `src/PcCleaner/Modules/Profile/SignInViewModel.Browser.cs`
- Test: `tests/PcCleaner.Tests/Profile/SignInBrowserTests.cs`

**Interfaces:**
- Produces (в `SignInViewModel`): `string? BrowserCode`, `string? BrowserUrl`, `RelayCommand ReopenBrowserCommand`, `RelayCommand RestartBrowserCommand`; `Task BrowserTask` (текущий опрос, для тестов); при `DEVICE_LIMIT` опрос ждёт, пока пользователь отвяжет компьютер (`ReleaseAsync` → опрос продолжается) или нажмёт «Назад» (опрос отменяется).

- [ ] **Step 1: Падающие тесты**

Create `tests/PcCleaner.Tests/Profile/SignInBrowserTests.cs`:

```csharp
using PcCleaner.Modules.Profile;
using PcCleaner.Tests.Account;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class SignInBrowserTests : IDisposable
{
    private readonly TestAccount _a = new();
    private int _completed;

    public SignInBrowserTests() => TestEnvironment.Init();

    public void Dispose() => _a.Dispose();

    private const string Start = "{\"code\":\"ABCD-1234\",\"url\":\"https://waterca7.ru/auth/device?code=ABCD-1234\",\"expires_in\":600}";

    private SignInViewModel Create()
    {
        var vm = new SignInViewModel(_a.Service, _a.Open, SignInTab.Browser, () => _a.Now);
        vm.Completed += () => _completed++;
        return vm;
    }

    [Fact]
    public async Task TheTab_GetsACode_OpensTheSite_AndSignsInWhenConfirmed()
    {
        _a.Fake.On("POST /auth/code", 200, Start)
            .On("GET /auth/code/ABCD-1234", 202, "{\"status\":\"pending\"}")
            .On("GET /auth/code/ABCD-1234", 200, _a.Key.LoginJson(TestLicense.Json()));

        var vm = Create();
        await vm.BrowserTask;

        Assert.Equal("ABCD-1234", vm.BrowserCode);
        Assert.Equal(new[] { "https://waterca7.ru/auth/device?code=ABCD-1234" }, _a.Opened);
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task AnExpiredCode_SaysSo_AndOffersANewOne()
    {
        _a.Fake.On("POST /auth/code", 200, Start).OnError("GET /auth/code/ABCD-1234", 410, "NOT_FOUND");

        var vm = Create();
        await vm.BrowserTask;

        Assert.Contains("устарел", vm.Error);
        Assert.Equal(0, _completed);
        vm.RestartBrowserCommand.Execute(null);
        await vm.BrowserTask;
        Assert.Equal(2, _a.Fake.To("POST /auth/code").Count());
    }

    [Fact]
    public async Task ADeviceLimit_PausesThePoll_UntilAComputerIsReleased()
    {
        _a.Fake.On("POST /auth/code", 200, Start)
            .OnError("GET /auth/code/ABCD-1234", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")))
            .On("GET /auth/code/ABCD-1234", 200, _a.Key.LoginJson(TestLicense.Json()))
            .On("POST /device/release", 200, "{\"ok\":true}");

        var vm = Create();
        while (vm.Step != SignInStep.DeviceLimit && !vm.BrowserTask.IsCompleted)
            await Task.Delay(10);
        Assert.Equal(SignInStep.DeviceLimit, vm.Step);
        var polls = _a.Fake.To("GET /auth/code/ABCD-1234").Count();

        await vm.ReleaseAsync(vm.Devices[0]);
        await vm.BrowserTask;

        Assert.Equal(1, polls);
        Assert.Equal(1, _completed);
    }

    [Fact]
    public async Task Back_FromTheLimit_StopsThePoll()
    {
        _a.Fake.On("POST /auth/code", 200, Start)
            .OnError("GET /auth/code/ABCD-1234", 409, "DEVICE_LIMIT", _a.LimitJson((5, "Office")));

        var vm = Create();
        while (vm.Step != SignInStep.DeviceLimit && !vm.BrowserTask.IsCompleted)
            await Task.Delay(10);
        vm.BackCommand.Execute(null);
        vm.Tab = SignInTab.Password;
        await vm.BrowserTask;

        Assert.Equal(SignInStep.Password, vm.Step);
        Assert.Equal(0, _completed);
    }

    [Fact]
    public async Task ReopenBrowser_OpensTheSamePageAgain()
    {
        _a.Fake.On("POST /auth/code", 200, Start).On("GET /auth/code/ABCD-1234", 202, "{\"status\":\"pending\"}");
        var vm = Create();
        while (vm.BrowserCode is null)
            await Task.Delay(10);

        vm.ReopenBrowserCommand.Execute(null);
        vm.Tab = SignInTab.Password; // stops the poll

        Assert.Equal(2, _a.Opened.Count);
        Assert.Equal(_a.Opened[0], _a.Opened[1]);
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~SignInBrowserTests"` → ошибка сборки.

- [ ] **Step 3: Код**

Create `src/PcCleaner/Modules/Profile/SignInViewModel.Browser.cs`:

```csharp
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
```

Заметки:
- `ShowLimit` в окне браузерного входа вызывается без текста сервера (у опроса кода `message` тоже есть, но `AccountService` его не передаёт) — это допустимо: показывается общий текст «Заняты все компьютеры аккаунта…».
- В `ReleaseAsync` (задача 3) после успешной отвязки зовётся `_retry()` и `Done()`: для браузера `_retry` только снимает паузу, `Done()` ничего не делает (вход ещё не выполнен — проверка `IsSignedIn`); вход завершит опрос.
- В тестах `Ui.Post` выполняется сразу (поток без диспетчера) — проверить это по `src/PcCleaner/Core/Ui.cs`; если `Post` откладывает без диспетчера, в тестах вызывать `ShowLimit` напрямую через внутренний флаг `UiPostsSynchronously`.

- [ ] **Step 4: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~PcCleaner.Tests.Profile.SignIn"` → PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Accounts: sign-in through the browser code, paused while a computer is released

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Окно входа (WPF) и снимки `--signin`

**Files:**
- Create: `src/PcCleaner/Modules/Profile/SignInWindow.xaml`, `SignInWindow.xaml.cs`, `SignInDialog.cs`
- Modify: `src/PcCleaner/Themes/Theme.xaml` (стиль `PasswordBox`), `src/PcCleaner/MainViewModel.cs` (`SignInDialog.Show` вместо заглушки задачи 2), `src/PcCleaner/Core/ScreenshotRunner.cs`, `src/PcCleaner/App.xaml.cs`

**Interfaces:**
- Consumes: `SignInViewModel` (задачи 3–5).
- Produces: `public static class SignInDialog` с `public static bool Show(SignInTab tab)` (модальное окно над главным; `true` — вход выполнен) и `internal static SignInWindow Create(SignInViewModel vm)`; снимки `90-SignIn-Password.png`, `91-SignIn-Register.png`, `92-SignIn-Verify.png`, `93-SignIn-Browser.png`, `94-SignIn-DeviceLimit.png` при `--signin`.

- [ ] **Step 1: Стиль поля пароля**

В `src/PcCleaner/Themes/Theme.xaml` сразу после стиля `TextBox` — такой же для `PasswordBox` (тот же шаблон, `TargetType="PasswordBox"`, `PART_ContentHost`):

```xml
    <Style TargetType="PasswordBox">
        <Setter Property="Foreground" Value="{StaticResource TextBrush}" />
        <Setter Property="Background" Value="{StaticResource Surface2Brush}" />
        <Setter Property="BorderBrush" Value="{StaticResource LineBrush}" />
        <Setter Property="CaretBrush" Value="{StaticResource TextBrush}" />
        <Setter Property="SelectionBrush" Value="{StaticResource AccentBrush}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Padding" Value="8,6" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="PasswordBox">
                    <Border x:Name="Bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6">
                        <ScrollViewer x:Name="PART_ContentHost" Focusable="False" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                            <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource AccentBrush}" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.5" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

- [ ] **Step 2: Окно**

Create `src/PcCleaner/Modules/Profile/SignInWindow.xaml` — окно 460×600, `WindowStartupLocation="CenterOwner"`, `ResizeMode="NoResize"`, тёмный фон как у главного окна (`Background="{StaticResource BackgroundBrush}"`, `Foreground="{StaticResource TextBrush}"`, `FontFamily="{StaticResource UiFont}"`), заголовок `Title="{loc:T 'Вход в аккаунт WaterCa7'}"`. Содержимое — `StackPanel Margin="28"`:

1. Полоса вкладок (`Visibility` по `ShowTabs`): три `RadioButton` в стиле вкладок (`GroupName="Tabs"`), `IsChecked` привязан через конвертер к `Tab` — проще: три кнопки `Button` с `Command` `SelectTabCommand` и параметром; добавить в модель `RelayCommand SelectTabCommand` (`p => Tab = (SignInTab)p`) и свойства `IsPasswordTab`, `IsRegisterTab`, `IsBrowserTab` для выделения (стиль `TabStripItem`-подобный: выбранная — фон `Surface3Brush`). Подписи: «Почта и пароль», «Регистрация», «Через браузер».
2. Блок `Error` (красный текст `DangerBrush`, `Visibility` по `HasValueToVis`) и блок `Info` (`MutedBrush`).
3. По одному `StackPanel` на шаг, `Visibility` по `Is…Step`:
   - **Password:** «Почта» `TextBox Text="{Binding Email, UpdateSourceTrigger=PropertyChanged}"`; «Пароль» `PasswordBox x:Name="PasswordBox"`; кнопка «Войти» (`AccentButton`, `SignInCommand`, `IsDefault="True"`); `LinkButton` «Забыли пароль?» (`ForgotCommand`).
   - **ResetEmail:** «Почта» + «Отправить код» (`SendResetCommand`) + «Назад».
   - **ResetCode:** `Info`, «Код из письма» `TextBox Text="{Binding Code}" MaxLength="12"`, «Новый пароль» `PasswordBox x:Name="NewPasswordBox"`, «Ещё раз» `PasswordBox x:Name="NewPasswordBox2"`, «Сменить пароль и войти» (`ConfirmResetCommand`), кнопка `ResendCommand` с текстом `ResendText`, «Назад».
   - **Register:** «Почта», «Пароль» `PasswordBox x:Name="RegisterPasswordBox"`, «Ещё раз» `PasswordBox x:Name="RegisterPasswordBox2"`, `CheckBox IsChecked="{Binding Consent}"` с текстом «Принимаю» и двумя `LinkButton` «оферту» (`OpenOfferCommand`) и «политику конфиденциальности» (`OpenPrivacyCommand`), «Зарегистрироваться» (`RegisterCommand`). Подсказка `Muted`: «Пароль — от 8 до 128 символов».
   - **VerifyCode:** «Введите код из письма» (`H3`), `Info`, поле кода (крупный шрифт 22, `MaxLength="12"`), «Подтвердить» (`VerifyCommand`), кнопка `ResendCommand`/`ResendText`, «Назад».
   - **Browser:** текст «Откроется сайт waterca7.ru. Войдите там и сверьте код с этим:», код `BrowserCode` крупно (шрифт 34, `Consolas`, по центру), `Muted` «Ждём подтверждения на сайте… (до 10 минут)», кнопки «Открыть сайт ещё раз» (`ReopenBrowserCommand`), «Новый код» (`RestartBrowserCommand`).
   - **DeviceLimit:** `H3` «Все компьютеры заняты», `LimitText`, `ItemsControl ItemsSource="{Binding Devices}"`: строка — `Name` (жирно), `Details` (`Muted`), кнопка «Отвязать» с `Command="{Binding DataContext.ReleaseCommand, RelativeSource={RelativeSource AncestorType=Window}}" CommandParameter="{Binding}"`; добавить в модель `AsyncRelayCommand ReleaseCommand` (`p => ReleaseAsync((LimitDevice)p!)`); «Назад».
4. Внизу (`DockPanel.Dock="Bottom"`) — индикатор `ProgressBar IsIndeterminate="True"` при `IsBusy`.

Create `src/PcCleaner/Modules/Profile/SignInWindow.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PcCleaner.Modules.Profile;

public partial class SignInWindow : Window
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public SignInWindow(SignInViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // PasswordBox.Password is not bindable (on purpose): copy each box into the model.
        PasswordBox.PasswordChanged += (_, _) => vm.Password = PasswordBox.Password;
        NewPasswordBox.PasswordChanged += (_, _) => vm.Password = NewPasswordBox.Password;
        NewPasswordBox2.PasswordChanged += (_, _) => vm.Password2 = NewPasswordBox2.Password;
        RegisterPasswordBox.PasswordChanged += (_, _) => vm.Password = RegisterPasswordBox.Password;
        RegisterPasswordBox2.PasswordChanged += (_, _) => vm.Password2 = RegisterPasswordBox2.Password;
        vm.Completed += () => Dispatcher.BeginInvoke(() =>
        {
            DialogResult = true;
        });
        _tick.Tick += (_, _) => vm.Tick();
        _tick.Start();
        Closed += (_, _) =>
        {
            _tick.Stop();
            vm.Tab = SignInTab.Password; // stops a running browser poll
        };
    }
}
```

(Если окно показано не через `ShowDialog` — в режиме скриншотов, — `DialogResult` бросает исключение: там `Completed` не наступает.)

Create `src/PcCleaner/Modules/Profile/SignInDialog.cs`:

```csharp
using System.Windows;
using PcCleaner.Core;

namespace PcCleaner.Modules.Profile;

/// <summary>Opens the sign-in window over the main window; true when the user signed in.</summary>
public static class SignInDialog
{
    public static bool Show(SignInTab tab)
    {
        var vm = new SignInViewModel(AppServices.Account, ProcessRunner.ShellOpen, tab);
        var window = Create(vm);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
            window.Owner = owner;
        return window.ShowDialog() == true;
    }

    internal static SignInWindow Create(SignInViewModel vm) => new(vm);
}
```

(Если у `ProcessRunner.ShellOpen` другая сигнатура — обернуть лямбдой `url => ProcessRunner.ShellOpen(url)`.)

В `MainViewModel` заменить временное `_ => false` задачи 2 на `SignInDialog.Show` для настоящей оболочки (`pages is null`); тестовая оболочка оставляет `_ => false`.

- [ ] **Step 3: Снимки окна входа**

`ScreenshotRunner.RunAsync` получает параметр `bool signIn = false` (из `App.SignInArg`); после страниц, если `signIn`:

```csharp
    /// <summary>--signin: the sign-in window on each of its steps, shown without a network (the screenshot account).</summary>
    private static async Task ShootSignInAsync(string outDir)
    {
        var shots = new (string File, Action<SignInViewModel> Setup)[]
        {
            ("90-SignIn-Password.png", vm => vm.Email = "user@example.com"),
            ("91-SignIn-Register.png", vm => vm.Tab = SignInTab.Register),
            ("92-SignIn-Verify.png", vm => vm.ShowStepForScreenshot(SignInStep.VerifyCode)),
            ("93-SignIn-Browser.png", vm => vm.ShowBrowserCodeForScreenshot("ABCD-1234")),
            ("94-SignIn-DeviceLimit.png", vm => vm.ShowLimitForScreenshot()),
        };
        foreach (var (file, setup) in shots)
        {
            var vm = new SignInViewModel(AppServices.Account, _ => { });
            setup(vm);
            var window = SignInDialog.Create(vm);
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.Show();
            await Task.Delay(400);
            await WaitIdleAsync();
            Save(window, Path.Combine(outDir, file));
            window.Close();
        }
    }
```

В `SignInViewModel` — три `internal` метода только для снимков: `ShowStepForScreenshot(SignInStep step)` (`Email = "user@example.com"; Step = step; Info = …текст отправки кода…`), `ShowBrowserCodeForScreenshot(string code)` (останавливает опрос, `Step = Browser`, `BrowserCode = code`), `ShowLimitForScreenshot()` (`ShowLimit` с двумя примерными компьютерами `DESKTOP-OFFICE` и `LAPTOP` и `bind_limit` 2). Вкладка «Через браузер» в снимке не запускает настоящий опрос (у учётки скриншотов нет сети, но вызов `StartBrowser` всё равно не нужен).

`App.xaml.cs`: передать `SignInArg` в `ScreenshotRunner.RunAsync(…, WelcomeArg, SignInArg)`.

- [ ] **Step 4: Сборка, тесты, снимки**

Run: `dotnet build src/PcCleaner -c Debug` → OK; `dotnet test tests/PcCleaner.Tests` → PASS (английские записи для новых текстов).

```powershell
Start-Process -Wait src\PcCleaner\bin\Debug\net7.0-windows\WaterCa7sPcCleaner.exe -ArgumentList "--screenshots", "$env:TEMP\b2-signin", "--page", "Profile", "--no-scan", "--signin"
Start-Process -Wait src\PcCleaner\bin\Debug\net7.0-windows\WaterCa7sPcCleaner.exe -ArgumentList "--screenshots", "$env:TEMP\b2-signin-en", "--page", "Profile", "--no-scan", "--signin", "--lang", "en"
```

Проверить пять PNG окна в обоих языках: поля и кнопки на месте, ничего не обрезано, код браузера крупно, список компьютеров с кнопками «Отвязать», тёмная тема.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Accounts: the sign-in window (WPF) and its screenshots

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Профиль — тексты лицензии, гость и вошедший, «Обновить», «Выйти»

**Files:**
- Create: `src/PcCleaner/Modules/Profile/ProfileTexts.cs` (если `DeviceDetails` уже создан в задаче 3 — дополнить)
- Modify: `src/PcCleaner/Modules/Profile/ProfileViewModel.cs`, `src/PcCleaner/MainViewModel.cs` (`CreatePages` — конструктор Профиля)
- Test: `tests/PcCleaner.Tests/Profile/ProfileTextsTests.cs`, `tests/PcCleaner.Tests/Profile/ProfileViewModelTests.cs`

**Interfaces:**
- Consumes: `AccountService` (`Status`, `IsSignedIn`, `Email`, `SessionEnded`, `RefreshTicketAsync`, `LogoutAsync`, `Changed`), `SignInTab`.
- Produces:
  - `public static class ProfileTexts`: `string PlanName(string? plan)`, `string? Term(LicenseStatus s)`, `string? Checked(LicenseStatus s, DateTimeOffset now)`, `string? Note(LicenseStatus s, DateTimeOffset now)`, `string DeviceDetails(DeviceEntry d, DateTimeOffset now)`;
  - `ProfileViewModel` — конструктор `(AccountService account, Func<SignInTab, bool> signIn, Action<string> openUrl, Func<string, string, bool> confirm, Func<DateTimeOffset>? now = null)`; свойства `IsGuest`, `IsSignedIn`, `Email`, `PlanText`, `TermText`, `CheckedText`, `NoteText`, `SessionEndedText`, `BuyText` («Купить Pro» / «Продлить»), `ProLines` (три строки «Что даёт Pro»); команды `SignInCommand`, `RegisterCommand`, `BuyCommand`, `RefreshCommand`, `LogoutCommand`; `const string BuyUrl = "https://waterca7.ru/pricing"` (B3 заменит окно покупки).

Правила текстов:
- `PlanName`: `free` → «Free», `pro` → «Pro», `family` → «Pro Семья», `custom` → «Особый», прочее/нет → «Free».
- `Term`: Pro с `ExpiresAt` → «до dd.MM.yyyy» (местная дата, `T.Culture`); Pro без `ExpiresAt` (`kind` lifetime) → «навсегда»; Free → null.
- `Checked`: «проверено {относительное время}» по `IssuedAt` (`HealthScoring.RelativeTime(local, DateTime.Now-аналог из now)`); нет билета → null.
- `Note` по причине: `Stale` → «Нужна проверка лицензии через интернет — нажмите «Обновить»»; `ClockBack` → «Часы компьютера отстают — проверьте дату и время Windows»; `WrongDevice` → «Лицензия выдана другому компьютеру — нажмите «Обновить»»; `Expired` → «Подписка закончилась dd.MM.yyyy»; Pro и `IssuedAt` старше суток → «Pro без интернета ещё {N} дн.»; иначе null.
- `DeviceDetails`: «Windows · привязан dd.MM.yyyy · в сети {относительное время}» (`mac` → «Mac»; `LastSeenAt` null → без последней части).
- Сессия кончилась (`SessionEnded`): «Вход больше не действует — компьютер отвязан или пароль сменён. Войдите снова.»

- [ ] **Step 1: Падающие тесты**

Create `tests/PcCleaner.Tests/Profile/ProfileTextsTests.cs`:

```csharp
using PcCleaner.Core.Account;
using PcCleaner.Modules.Profile;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class ProfileTextsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static LicenseStatus Pro(DateTimeOffset? expires, DateTimeOffset? issued = null, string plan = "pro", string kind = "subscription")
        => new(Tier.Pro, LicenseReason.None, plan, kind, 1, expires, issued ?? Now.AddHours(-1), Now.AddDays(13), 13);

    [Theory]
    [InlineData("free", "Free")]
    [InlineData("pro", "Pro")]
    [InlineData("family", "Pro Семья")]
    [InlineData("custom", "Особый")]
    [InlineData(null, "Free")]
    public void ThePlan_HasItsName(string? plan, string name) => Assert.Equal(name, ProfileTexts.PlanName(plan));

    [Fact]
    public void TheTerm_IsADate_OrForever_OrNothingForFree()
    {
        Assert.Equal("до 24.10.2027", ProfileTexts.Term(Pro(new DateTimeOffset(2027, 10, 24, 10, 0, 0, TimeSpan.Zero))));
        Assert.Equal("навсегда", ProfileTexts.Term(Pro(null, kind: "lifetime")));
        Assert.Null(ProfileTexts.Term(new LicenseStatus(Tier.Free, LicenseReason.NoPlan, "free", "none", 0, null, Now, Now.AddDays(13), null)));
    }

    [Theory]
    [InlineData(LicenseReason.Stale, "Обновить")]
    [InlineData(LicenseReason.ClockBack, "Часы")]
    [InlineData(LicenseReason.WrongDevice, "другому компьютеру")]
    [InlineData(LicenseReason.Expired, "закончилась")]
    public void TheNote_ExplainsAFreeTicket(LicenseReason reason, string part)
    {
        var s = new LicenseStatus(Tier.Free, reason, "pro", "subscription", 1, Now.AddDays(-1), Now.AddDays(-15), Now.AddDays(-1), null);

        Assert.Contains(part, ProfileTexts.Note(s, Now));
    }

    [Fact]
    public void AnOldButValidProTicket_CountsTheOfflineDays()
    {
        Assert.Null(ProfileTexts.Note(Pro(Now.AddDays(100)), Now));
        Assert.Contains("13", ProfileTexts.Note(Pro(Now.AddDays(100), issued: Now.AddDays(-2)), Now));
    }

    [Fact]
    public void AComputer_HasItsSystemDateAndLastSeen()
    {
        var d = new DeviceEntry(5, "Office", "mac", new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), null, false);

        Assert.Equal("Mac · привязан 01.10.2026", ProfileTexts.DeviceDetails(d, Now));
        Assert.StartsWith("Windows · привязан 01.10.2026 · в сети", ProfileTexts.DeviceDetails(d with { Platform = "windows", LastSeenAt = Now.AddHours(-3) }, Now));
    }
}
```

Create `tests/PcCleaner.Tests/Profile/ProfileViewModelTests.cs`:

```csharp
using PcCleaner.Modules.Profile;
using PcCleaner.Tests.Account;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class ProfileViewModelTests
{
    private readonly List<SignInTab> _windows = new();
    private bool _confirm;

    public ProfileViewModelTests() => TestEnvironment.Init();

    private ProfileViewModel Create(TestAccount a)
        => new(a.Service, tab =>
        {
            _windows.Add(tab);
            return false;
        }, a.Open, (_, _) => _confirm, () => a.Now);

    [Fact]
    public void AGuest_SeesTheSignInButtons_AndWhatProGives()
    {
        using var a = new TestAccount();
        var vm = Create(a);

        Assert.True(vm.IsGuest);
        Assert.Equal(3, vm.ProLines.Count);
        vm.SignInCommand.Execute(null);
        vm.RegisterCommand.Execute(null);
        Assert.Equal(new[] { SignInTab.Password, SignInTab.Register }, _windows);
    }

    [Fact]
    public void APro_SeesTheEmailPlanAndTerm()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "family", expires: TestLicense.Now.AddDays(300)));
        var vm = Create(a);

        Assert.False(vm.IsGuest);
        Assert.Equal("user@example.com", vm.Email);
        Assert.Equal("Pro Семья", vm.PlanText);
        Assert.StartsWith("до ", vm.TermText);
        Assert.Equal("Продлить", vm.BuyText);
    }

    [Fact]
    public void AFreeAccount_IsOfferedToBuy()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", kind: "none", noExpiry: true));
        var vm = Create(a);

        Assert.Equal("Free", vm.PlanText);
        Assert.Equal("Купить Pro", vm.BuyText);
        vm.BuyCommand.Execute(null);
        Assert.Equal(new[] { ProfileViewModel.BuyUrl }, a.Opened);
    }

    [Fact]
    public async Task Refresh_TakesTheNewTicket_AndThePageFollows()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", noExpiry: true));
        a.Fake.On("GET /license/ticket", 200, a.Key.TicketJson(TestLicense.Json(plan: "pro")));
        var vm = Create(a);

        await vm.RefreshForTestAsync();

        Assert.Equal("Pro", vm.PlanText);
    }

    [Fact]
    public async Task Logout_AsksFirst()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("POST /auth/logout", 200, "{\"ok\":true}");
        var vm = Create(a);

        await vm.LogoutForTestAsync();
        Assert.False(vm.IsGuest);

        _confirm = true;
        await vm.LogoutForTestAsync();
        Assert.True(vm.IsGuest);
    }

    [Fact]
    public async Task ARevokedSession_IsExplained_OnThePage()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.OnError("GET /license/ticket", 401, "TOKEN_REVOKED");
        var vm = Create(a);

        await vm.RefreshForTestAsync();

        Assert.True(vm.IsGuest);
        Assert.Contains("Войдите снова", vm.SessionEndedText);
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~ProfileTextsTests|FullyQualifiedName~ProfileViewModelTests"` → ошибка сборки.

- [ ] **Step 3: `ProfileTexts`**

Create (или дополнить) `src/PcCleaner/Modules/Profile/ProfileTexts.cs`:

```csharp
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Dashboard;

namespace PcCleaner.Modules.Profile;

/// <summary>The lines of Профиль about the license and the computers (spec 3.3), as pure functions.</summary>
public static class ProfileTexts
{
    public static string PlanName(string? plan) => plan switch
    {
        "pro" => "Pro",
        "family" => T._("Pro Семья"),
        "custom" => T._("Особый"),
        _ => "Free",
    };

    public static string? Term(LicenseStatus s) => s.Tier != Tier.Pro
        ? null
        : s.ExpiresAt is { } end
            ? T.F("до {0}", Date(end))
            : T._("навсегда");

    public static string? Checked(LicenseStatus s, DateTimeOffset now)
        => s.IssuedAt is { } t ? T.F("проверено {0}", HealthScoring.RelativeTime(t.LocalDateTime, now.LocalDateTime)) : null;

    public static string? Note(LicenseStatus s, DateTimeOffset now) => s.Reason switch
    {
        LicenseReason.Stale => T._("Нужна проверка лицензии через интернет — нажмите «Обновить»"),
        LicenseReason.ClockBack => T._("Часы компьютера отстают — проверьте дату и время Windows"),
        LicenseReason.WrongDevice => T._("Лицензия выдана другому компьютеру — нажмите «Обновить»"),
        LicenseReason.Expired when s.ExpiresAt is { } end => T.F("Подписка закончилась {0}", Date(end)),
        LicenseReason.None when s.Tier == Tier.Pro && s.IssuedAt is { } t && now - t > TimeSpan.FromDays(1)
            => T.F("Pro без интернета ещё {0} дн.", s.OfflineDaysLeft ?? 0),
        _ => null,
    };

    public static string DeviceDetails(DeviceEntry d, DateTimeOffset now)
    {
        var system = d.Platform == "mac" ? "Mac" : "Windows";
        var text = T.F("{0} · привязан {1}", system, Date(d.BoundAt));
        return d.LastSeenAt is { } seen
            ? text + " · " + T.F("в сети {0}", HealthScoring.RelativeTime(seen.LocalDateTime, now.LocalDateTime))
            : text;
    }

    private static string Date(DateTimeOffset t) => t.LocalDateTime.ToString("dd.MM.yyyy", T.Culture);
}
```

(Если сигнатура `HealthScoring.RelativeTime` иная или она не публична — взять её как есть, подстроив вызов; не писать вторую функцию относительного времени.)

- [ ] **Step 4: `ProfileViewModel` (основная часть)**

Заменить содержимое `src/PcCleaner/Modules/Profile/ProfileViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using PcCleaner.Core;
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

/// <summary>
/// «Профиль» (spec 3.3): a guest gets «Войти», «Зарегистрироваться» and what Pro gives; a signed-in user — the e-mail, the plan,
/// the term, the last check, the computers, the promo code, «Написать нам» and «Выйти». Follows <see cref="AccountService.Changed"/>.
/// </summary>
public sealed partial class ProfileViewModel : PageViewModel
{
    public const string BuyUrl = "https://waterca7.ru/pricing";

    private readonly AccountService _account;
    private readonly Func<SignInTab, bool> _signIn;
    private readonly Action<string> _openUrl;
    private readonly Func<string, string, bool> _confirm;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>The real page: the app's account, the sign-in window, the browser, the app's questions.</summary>
    public ProfileViewModel()
        : this(AppServices.Account, SignInDialog.Show, url => ProcessRunner.ShellOpen(url), Dialogs.Confirm)
    {
    }

    public ProfileViewModel(AccountService account, Func<SignInTab, bool> signIn, Action<string> openUrl, Func<string, string, bool> confirm,
        Func<DateTimeOffset>? now = null)
    {
        _account = account;
        _signIn = signIn;
        _openUrl = openUrl;
        _confirm = confirm;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        SignInCommand = new RelayCommand(() => _signIn(SignInTab.Password));
        RegisterCommand = new RelayCommand(() => _signIn(SignInTab.Register));
        BuyCommand = new RelayCommand(() => _openUrl(BuyUrl));
        RefreshCommand = new AsyncRelayCommand(() => RunBusyAsync(T._("Проверяем лицензию…"), RefreshAsync), () => !IsBusy && IsSignedIn);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync, () => !IsBusy && IsSignedIn);
        InitDevices();
        InitPromoSupport();
        _account.Changed += () => Ui.Post(Refresh);
    }

    public override string Title => T._("Профиль");

    public override string Subtitle => T._("Аккаунт WaterCa7, Pro и ваши компьютеры");

    public override string IconGlyph => "\uE77B";

    public bool IsSignedIn => _account.IsSignedIn;

    public bool IsGuest => !IsSignedIn;

    public string? Email => _account.Email;

    public string PlanText => ProfileTexts.PlanName(_account.Status.Plan);

    public string? TermText => ProfileTexts.Term(_account.Status);

    public string? CheckedText => ProfileTexts.Checked(_account.Status, _now());

    public string? NoteText => ProfileTexts.Note(_account.Status, _now());

    public string? SessionEndedText => _account.SessionEnded && !IsSignedIn
        ? T._("Вход больше не действует — компьютер отвязан или пароль сменён. Войдите снова.")
        : null;

    public string BuyText => _account.Tier == Tier.Pro ? T._("Продлить") : T._("Купить Pro");

    /// <summary>«Что даёт Pro» for a guest and a free account.</summary>
    public IReadOnlyList<string> ProLines { get; } = new[]
    {
        T._("Полная проверка и глубокая очистка: кэши игр, обновлений, дампы, DISM"),
        T._("Ускорение компьютера и проверка сети"),
        T._("Глубокие проверки процессов, автозагрузки, программ, дисков и защиты"),
    };

    public RelayCommand SignInCommand { get; }
    public RelayCommand RegisterCommand { get; }
    public RelayCommand BuyCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand LogoutCommand { get; }

    public override Task OnNavigatedToAsync() => IsSignedIn ? LoadDevicesAsync() : Task.CompletedTask;

    internal Task RefreshForTestAsync() => RefreshAsync(CancellationToken.None);

    internal Task LogoutForTestAsync() => LogoutAsync();

    private async Task RefreshAsync(CancellationToken ct)
    {
        await _account.RefreshTicketAsync(ct);
        Refresh();
    }

    private async Task LogoutAsync()
    {
        if (!_confirm(T._("Выход из аккаунта"), T._("Выйти из аккаунта на этом компьютере? Настройки останутся, программа перейдёт в бесплатный режим.")))
            return;
        await _account.LogoutAsync(CancellationToken.None);
        Refresh();
    }

    /// <summary>Re-reads everything from the account (after a sign-in, a refresh, a purchase, a sign-out).</summary>
    private void Refresh()
    {
        foreach (var name in new[] { nameof(IsSignedIn), nameof(IsGuest), nameof(Email), nameof(PlanText), nameof(TermText), nameof(CheckedText),
                     nameof(NoteText), nameof(SessionEndedText), nameof(BuyText), nameof(CanApplyPromo) })
            OnPropertyChanged(name);
        RelayCommand.Refresh();
        if (!IsSignedIn)
            Devices.Clear();
    }
}
```

`InitDevices`, `LoadDevicesAsync`, `Devices` — задача 8; `InitPromoSupport`, `CanApplyPromo` — задача 9. Для сборки этой задачи создать в ней же оба `partial`-файла по коду задач 8 и 9 (их тесты добавятся там), либо выполнять задачи 7–9 одним исполнителем подряд.

`MainViewModel.CreatePages()`: `new ProfileViewModel()` уже вызывает настоящий конструктор — ничего не менять. Тесты оболочки, где Профиль создаётся без аккаунта (`ShellEnglishTests.RealPages`), продолжают работать: `AppServices.Account` в тестах — настоящий `CreateReal()`, он читает `account.bin` из папки данных тестов (`TestEnvironment.DataDir`) и сеть не трогает до первого вызова. Если тестовый запуск создаёт `WmiHwHash` при старте страницы — нет: хэш вычисляется лениво.

- [ ] **Step 5: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~PcCleaner.Tests.Profile"` → PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Accounts: Профиль — plan, term, last check, notes, refresh, sign-out

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Профиль — «Мои компьютеры»

**Files:**
- Create: `src/PcCleaner/Modules/Profile/ProfileViewModel.Devices.cs`, `src/PcCleaner/Modules/Profile/ProfileItems.cs`
- Test: `tests/PcCleaner.Tests/Profile/ProfileDevicesTests.cs`

**Interfaces:**
- Produces: `public sealed record DeviceItem(long Id, string Name, string Details, bool IsCurrent, string? Whose)`; в `ProfileViewModel`: `ObservableCollection<DeviceItem> Devices`, `string? DevicesText` (ошибка загрузки или «Компьютеров: N из M»), `AsyncRelayCommand UnbindCommand` (параметр — `DeviceItem`), `Task LoadDevicesAsync()`.

Отвязка своего компьютера = выход (вопрос это говорит); `UNBIND_QUOTA` — `message` сервера.

- [ ] **Step 1: Падающие тесты**

Create `tests/PcCleaner.Tests/Profile/ProfileDevicesTests.cs`:

```csharp
using PcCleaner.Modules.Profile;
using PcCleaner.Tests.Account;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class ProfileDevicesTests
{
    private const string Devices = "{\"devices\":[" +
        "{\"id\":42,\"name\":\"HOME\",\"platform\":\"windows\",\"bound_at\":\"2026-10-24T10:00:00Z\",\"last_seen_at\":\"2026-11-01T09:00:00Z\",\"current\":true}," +
        "{\"id\":43,\"name\":\"Mac\",\"platform\":\"mac\",\"bound_at\":\"2026-10-25T10:00:00Z\",\"last_seen_at\":null,\"current\":false,\"whose\":\"son@x.y\",\"member\":true}" +
        "],\"bind_limit\":3}";

    private readonly List<string> _questions = new();
    private bool _confirm = true;

    public ProfileDevicesTests() => TestEnvironment.Init();

    private ProfileViewModel Create(TestAccount a) => new(a.Service, _ => false, a.Open, (t, m) =>
    {
        _questions.Add(m);
        return _confirm;
    }, () => a.Now);

    [Fact]
    public async Task TheComputers_AreListed_WithThisOneMarked_AndWhose()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("GET /devices", 200, Devices);
        var vm = Create(a);

        await vm.LoadDevicesAsync();

        Assert.Equal(new[] { "HOME", "Mac" }, vm.Devices.Select(d => d.Name));
        Assert.True(vm.Devices[0].IsCurrent);
        Assert.Equal("son@x.y", vm.Devices[1].Whose);
        Assert.Contains("2", vm.DevicesText);
        Assert.Contains("3", vm.DevicesText);
    }

    [Fact]
    public async Task AnotherComputer_IsUnbound_AfterYes_AndTheListReloads()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("GET /devices", 200, Devices).On("POST /device/unbind", 200, "{\"ok\":true}");
        var vm = Create(a);
        await vm.LoadDevicesAsync();

        await vm.UnbindForTestAsync(vm.Devices[1]);

        Assert.Equal(43, Api.Body(a.Fake.To("POST /device/unbind").Single()).GetProperty("device_id").GetInt64());
        Assert.Equal(2, a.Fake.To("GET /devices").Count());
        Assert.True(a.Service.IsSignedIn);
    }

    [Fact]
    public async Task ThisComputer_IsUnbound_AsASignOut_AndTheQuestionSaysSo()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("GET /devices", 200, Devices).On("POST /device/unbind", 200, "{\"ok\":true}");
        var vm = Create(a);
        await vm.LoadDevicesAsync();

        await vm.UnbindForTestAsync(vm.Devices[0]);

        Assert.Contains("выйдете", _questions.Single());
        Assert.False(a.Service.IsSignedIn);
        Assert.Empty(vm.Devices);
    }

    [Fact]
    public async Task No_UnbindsNothing_AndAQuotaShowsTheServersWords()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("GET /devices", 200, Devices).OnError("POST /device/unbind", 429, "UNBIND_QUOTA");
        var vm = Create(a);
        await vm.LoadDevicesAsync();

        _confirm = false;
        await vm.UnbindForTestAsync(vm.Devices[1]);
        Assert.Empty(a.Fake.To("POST /device/unbind"));

        _confirm = true;
        await vm.UnbindForTestAsync(vm.Devices[1]);
        Assert.Equal("msg UNBIND_QUOTA", vm.DevicesText);
    }

    [Fact]
    public async Task Offline_TheListSaysSo()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.ThrowOnSend = new System.Net.Http.HttpRequestException("down");
        var vm = Create(a);

        await vm.LoadDevicesAsync();

        Assert.Contains("waterca7.ru", vm.DevicesText);
        Assert.Empty(vm.Devices);
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~ProfileDevicesTests"` → ошибка сборки.

- [ ] **Step 3: Код**

Create `src/PcCleaner/Modules/Profile/ProfileItems.cs`:

```csharp
namespace PcCleaner.Modules.Profile;

/// <summary>A row of «Мои компьютеры».</summary>
public sealed record DeviceItem(long Id, string Name, string Details, bool IsCurrent, string? Whose);
```

Create `src/PcCleaner/Modules/Profile/ProfileViewModel.Devices.cs`:

```csharp
using System.Collections.ObjectModel;
using PcCleaner.Core;
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

// «Мои компьютеры»: the list of GET /devices and «Отвязать».
public sealed partial class ProfileViewModel
{
    private string? _devicesText;

    public ObservableCollection<DeviceItem> Devices { get; } = new();

    public string? DevicesText { get => _devicesText; private set => Set(ref _devicesText, value); }

    public AsyncRelayCommand UnbindCommand { get; private set; } = null!;

    private void InitDevices()
        => UnbindCommand = new AsyncRelayCommand(p => p is DeviceItem d ? UnbindAsync(d) : Task.CompletedTask, p => !IsBusy && p is DeviceItem);

    public async Task LoadDevicesAsync()
    {
        if (!IsSignedIn)
            return;
        try
        {
            var list = await _account.DevicesAsync(CancellationToken.None);
            Devices.Clear();
            foreach (var d in list.Devices)
                Devices.Add(new DeviceItem(d.Id, d.Name, ProfileTexts.DeviceDetails(d, _now()), d.Current, d.Whose));
            DevicesText = T.F("Компьютеров: {0} из {1}", list.Devices.Count, list.BindLimit);
        }
        catch (ApiException ex)
        {
            DevicesText = SignInErrors.Text(ex.Error);
            Refresh(); // TOKEN_REVOKED signed out
        }
    }

    internal Task UnbindForTestAsync(DeviceItem device) => UnbindAsync(device);

    private async Task UnbindAsync(DeviceItem device)
    {
        var question = device.IsCurrent
            ? T._("Отвязать этот компьютер? Вы выйдете из аккаунта, программа перейдёт в бесплатный режим.")
            : T.F("Отвязать компьютер «{0}»? На нём программа перейдёт в бесплатный режим. Отвязывать можно раз в 7 дней.", device.Name);
        if (!_confirm(T._("Отвязать компьютер"), question))
            return;
        try
        {
            await _account.UnbindAsync(device.IsCurrent ? null : device.Id, CancellationToken.None);
        }
        catch (ApiException ex)
        {
            DevicesText = SignInErrors.Text(ex.Error);
            Refresh();
            return;
        }
        Refresh();
        await LoadDevicesAsync();
    }
}
```

- [ ] **Step 4: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~ProfileDevicesTests"` → PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Accounts: Профиль — my computers and unbinding

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Профиль — промокод и «Написать нам»

**Files:**
- Create: `src/PcCleaner/Modules/Profile/ProfileViewModel.PromoSupport.cs`, `src/PcCleaner/Modules/Profile/SupportAttachment.cs`
- Test: `tests/PcCleaner.Tests/Profile/ProfilePromoSupportTests.cs`

**Interfaces:**
- Produces (в `ProfileViewModel`): `string PromoCode`, `string? PromoText`, `bool PromoIsDiscount` («Это код скидки» — кнопка «Открыть покупку»), `bool CanApplyPromo` (только вошедший), `AsyncRelayCommand ApplyPromoCommand`, `RelayCommand OpenBuyWithPromoCommand` (B2: страница цен; B3 заменит окном покупки с этим кодом); `string SupportTopic` (`question` | `bug` | `other`), `string SupportText`, `string SupportEmail`, `bool SupportAttach`, `string? SupportResult`, `bool NeedsSupportEmail` (гость), `AsyncRelayCommand SendSupportCommand`;
- `public static class SupportAttachment` с `string Build(int maxBytes = 700 * 1024)` — base64 текста: строка о системе (версия программы, версия Windows, язык) + конец самого нового файла `AppPaths.LogsDir\*.log`, обрезанный до `maxBytes` байт (base64 тогда ≤ 1 МиБ).

Тексты промокода — как на сайте (`A:\waterca7.ru\app\lang\ru.php`, `account.promo_err.*`):

| reason | Текст |
|---|---|
| 1 | «Такого промокода нет» |
| 2 | «Срок промокода истёк» |
| 3 | «Промокод уже использован максимальное число раз» |
| 4 | «Вы уже использовали этот промокод» |
| 5 | «На этом компьютере промокод уже применяли» |
| 6 | «Сначала войдите в аккаунт в программе на этом компьютере» |
| 7 | «Это код скидки — введите его в окне покупки» (+ `PromoIsDiscount`) |

Успех — «Промокод применён: {тариф} {срок}». `RATE_LIMITED` — «Слишком много попыток. Попробуйте через час.»

- [ ] **Step 1: Падающие тесты**

Create `tests/PcCleaner.Tests/Profile/ProfilePromoSupportTests.cs`:

```csharp
using System.IO;
using System.Text;
using PcCleaner.Core;
using PcCleaner.Modules.Profile;
using PcCleaner.Tests.Account;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class ProfilePromoSupportTests
{
    public ProfilePromoSupportTests() => TestEnvironment.Init();

    private static ProfileViewModel Create(TestAccount a) => new(a.Service, _ => false, a.Open, (_, _) => true, () => a.Now);

    [Fact]
    public async Task AGoodCode_TurnsProOn_AtOnce()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", noExpiry: true));
        a.Fake.On("POST /promo/apply", 200, a.Key.TicketJson(TestLicense.Json(plan: "pro", kind: "promo")));
        var vm = Create(a);
        vm.PromoCode = " WELCOME ";

        await vm.ApplyPromoForTestAsync();

        Assert.Equal("Pro", vm.PlanText);
        Assert.StartsWith("Промокод применён", vm.PromoText);
        Assert.Equal("WELCOME", Api.Body(a.Fake.Requests.Single()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(1, "Такого промокода нет")]
    [InlineData(5, "На этом компьютере")]
    [InlineData(6, "Сначала войдите")]
    public async Task AWrongCode_SaysWhy(int reason, string text)
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", noExpiry: true));
        a.Fake.OnError("POST /promo/apply", 422, "PROMO_INVALID", $",\"reason\":{reason}");
        var vm = Create(a);
        vm.PromoCode = "X";

        await vm.ApplyPromoForTestAsync();

        Assert.StartsWith(text, vm.PromoText);
        Assert.False(vm.PromoIsDiscount);
    }

    [Fact]
    public async Task ADiscountCode_PointsToThePurchase()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", noExpiry: true));
        a.Fake.OnError("POST /promo/apply", 422, "PROMO_INVALID", ",\"reason\":7");
        var vm = Create(a);
        vm.PromoCode = "SALE10";

        await vm.ApplyPromoForTestAsync();

        Assert.True(vm.PromoIsDiscount);
        vm.OpenBuyWithPromoCommand.Execute(null);
        Assert.Equal(new[] { ProfileViewModel.BuyUrl }, a.Opened);
    }

    [Fact]
    public void AGuest_CannotApplyACode()
    {
        using var a = new TestAccount();

        Assert.False(Create(a).CanApplyPromo);
    }

    [Fact]
    public async Task Support_FromASignedInUser_GoesWithTheAccount_AndTheLog()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("POST /ticket", 200, "{\"id\":3}");
        var vm = Create(a);
        vm.SupportTopic = "bug";
        vm.SupportText = "Не открывается раздел Сеть после обновления";
        vm.SupportAttach = true;

        await vm.SendSupportForTestAsync();

        var body = Api.Body(a.Fake.Requests.Single());
        Assert.Equal("user@example.com", body.GetProperty("email").GetString());
        Assert.Equal("bug", body.GetProperty("topic").GetString());
        Assert.True(body.TryGetProperty("log", out var log) && log.GetString()!.Length > 0);
        Assert.Contains("почту", vm.SupportResult);
        Assert.Equal("", vm.SupportText);
    }

    [Fact]
    public async Task Support_AsAGuest_NeedsAnEmail_AndATextOfTenCharacters()
    {
        using var a = new TestAccount();
        a.Fake.On("POST /ticket", 200, "{\"id\":3}");
        var vm = Create(a);
        Assert.True(vm.NeedsSupportEmail);
        vm.SupportText = "Коротко";

        await vm.SendSupportForTestAsync();
        Assert.Empty(a.Fake.Requests);

        vm.SupportText = "Подробное описание вопроса";
        vm.SupportEmail = "guest@example.com";
        await vm.SendSupportForTestAsync();

        Assert.Null(a.Fake.Requests.Single().Bearer);
    }

    [Fact]
    public void TheAttachment_IsTheEndOfTheNewestLog_InBase64_WithinTheLimit()
    {
        Directory.CreateDirectory(AppPaths.LogsDir);
        File.WriteAllText(Path.Combine(AppPaths.LogsDir, "2026-11-01.log"), new string('x', 5000) + "END-OF-LOG");

        var text = Encoding.UTF8.GetString(Convert.FromBase64String(SupportAttachment.Build(maxBytes: 1000)));

        Assert.True(Encoding.UTF8.GetByteCount(text) <= 1000);
        Assert.EndsWith("END-OF-LOG", text);
        Assert.Contains("WaterCa7s PC Cleaner", text);
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~ProfilePromoSupportTests"` → ошибка сборки.

- [ ] **Step 3: Код**

Create `src/PcCleaner/Modules/Profile/SupportAttachment.cs`:

```csharp
using System.IO;
using System.Text;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

/// <summary>«Приложить сведения о системе и журнал»: one line about the system and the end of the newest log, as base64.</summary>
public static class SupportAttachment
{
    /// <param name="maxBytes">The limit of the text before base64 (700 KiB → base64 under the API's 1 MiB).</param>
    public static string Build(int maxBytes = 700 * 1024)
    {
        var head = $"{AppInfo.Name} {AppInfo.Version}; {Environment.OSVersion.VersionString}; {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}; {T.Language}\n";
        var log = "";
        try
        {
            var newest = new DirectoryInfo(AppPaths.LogsDir).Exists
                ? new DirectoryInfo(AppPaths.LogsDir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
            if (newest is not null)
            {
                using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                log = reader.ReadToEnd();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log = T.F("(журнал не прочитан: {0})", ex.Message);
        }
        var room = Math.Max(0, maxBytes - Encoding.UTF8.GetByteCount(head));
        var bytes = Encoding.UTF8.GetBytes(log);
        var tail = bytes.Length <= room ? log : Encoding.UTF8.GetString(bytes, bytes.Length - room, room).TrimStart('\uFFFD');
        var text = head + tail;
        while (Encoding.UTF8.GetByteCount(text) > maxBytes)
            text = text[1..];
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    }
}
```

(Тест проверяет, что строка о системе на месте при маленьком лимите: если обрезка съедает её — сначала обрезать журнал, а не голову; цикл `while` в конце срабатывает только когда голова одна длиннее лимита.)

Create `src/PcCleaner/Modules/Profile/ProfileViewModel.PromoSupport.cs`:

```csharp
using PcCleaner.Core;
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

// The promo code and «Написать нам».
public sealed partial class ProfileViewModel
{
    private string _promoCode = "";
    private string? _promoText;
    private bool _promoIsDiscount;
    private string _supportTopic = "question";
    private string _supportText = "";
    private string _supportEmail = "";
    private bool _supportAttach = true;
    private string? _supportResult;

    public string PromoCode { get => _promoCode; set => Set(ref _promoCode, value); }
    public string? PromoText { get => _promoText; private set => Set(ref _promoText, value); }
    public bool PromoIsDiscount { get => _promoIsDiscount; private set => Set(ref _promoIsDiscount, value); }
    public bool CanApplyPromo => IsSignedIn;

    public string SupportTopic { get => _supportTopic; set => Set(ref _supportTopic, value); }
    public string SupportText { get => _supportText; set => Set(ref _supportText, value); }
    public string SupportEmail { get => _supportEmail; set => Set(ref _supportEmail, value); }
    public bool SupportAttach { get => _supportAttach; set => Set(ref _supportAttach, value); }
    public string? SupportResult { get => _supportResult; private set => Set(ref _supportResult, value); }
    public bool NeedsSupportEmail => !IsSignedIn;

    public AsyncRelayCommand ApplyPromoCommand { get; private set; } = null!;
    public RelayCommand OpenBuyWithPromoCommand { get; private set; } = null!;
    public AsyncRelayCommand SendSupportCommand { get; private set; } = null!;

    private void InitPromoSupport()
    {
        ApplyPromoCommand = new AsyncRelayCommand(ApplyPromoAsync, () => !IsBusy && CanApplyPromo);
        OpenBuyWithPromoCommand = new RelayCommand(() => _openUrl(BuyUrl));
        SendSupportCommand = new AsyncRelayCommand(SendSupportAsync, () => !IsBusy);
    }

    internal Task ApplyPromoForTestAsync() => ApplyPromoAsync();
    internal Task SendSupportForTestAsync() => SendSupportAsync();

    private async Task ApplyPromoAsync()
    {
        var code = PromoCode.Trim();
        if (code.Length == 0)
            return;
        PromoIsDiscount = false;
        try
        {
            await _account.ApplyPromoAsync(code, CancellationToken.None);
            PromoText = T.F("Промокод применён: {0} {1}", PlanText, TermText ?? "").Trim();
            PromoCode = "";
        }
        catch (ApiException ex) when (ex.Error.Code == ApiError.PromoInvalid)
        {
            PromoIsDiscount = ex.Error.Reason == "7";
            PromoText = ex.Error.Reason switch
            {
                "2" => T._("Срок промокода истёк"),
                "3" => T._("Промокод уже использован максимальное число раз"),
                "4" => T._("Вы уже использовали этот промокод"),
                "5" => T._("На этом компьютере промокод уже применяли"),
                "6" => T._("Сначала войдите в аккаунт в программе на этом компьютере"),
                "7" => T._("Это код скидки — введите его в окне покупки"),
                _ => T._("Такого промокода нет"),
            };
        }
        catch (ApiException ex)
        {
            PromoText = ex.Error.Code == ApiError.RateLimited ? T._("Слишком много попыток. Попробуйте через час.") : SignInErrors.Text(ex.Error);
        }
        Refresh();
    }

    private async Task SendSupportAsync()
    {
        var text = SupportText.Trim();
        if (text.Length is < 10 or > 5000)
        {
            SupportResult = T._("Опишите вопрос: от 10 до 5000 символов.");
            return;
        }
        if (NeedsSupportEmail && !SignInErrors.IsValidEmail(SupportEmail))
        {
            SupportResult = T._("Укажите почту — ответ придёт на неё.");
            return;
        }
        try
        {
            await _account.SendSupportAsync(NeedsSupportEmail ? SupportEmail.Trim() : null, SupportTopic, text,
                SupportAttach ? SupportAttachment.Build() : null, CancellationToken.None);
            SupportResult = T._("Отправлено. Ответ придёт на почту.");
            SupportText = "";
        }
        catch (ApiException ex)
        {
            SupportResult = SignInErrors.Text(ex.Error);
        }
    }
}
```

В `Refresh()` задачи 7 добавить `nameof(NeedsSupportEmail)` в список свойств.

- [ ] **Step 4: Тесты проходят**

Run: `dotnet test tests/PcCleaner.Tests --filter "FullyQualifiedName~PcCleaner.Tests.Profile"` → PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Accounts: Профиль — promo code and «Написать нам» with the log

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Вид Профиля и снимки трёх уровней

**Files:**
- Modify: `src/PcCleaner/Modules/Profile/ProfileView.xaml` (заменить заготовку целиком)
- Modify: `tests/PcCleaner.Tests/Core/ShellEnglishTests.cs` (если там проверяется подзаголовок Профиля — он прежний)

- [ ] **Step 1: Вид**

`ProfileView.xaml` — `ScrollViewer` → `StackPanel Margin="0,0,10,8"`, карточки (`Style="{StaticResource Card}"`, отступ между ними 12):

1. **Сессия кончилась** — строка `WarningSoftBrush` с `SessionEndedText` (видна, когда текст не пуст) и кнопкой «Войти».
2. **Гость** (`Visibility` по `IsGuest`): `H3` «Вы не вошли»; `Muted` «Войдите, чтобы включить Pro на этом компьютере, применить промокод и видеть свои компьютеры.»; кнопки «Войти» (`AccentButton`, `SignInCommand`), «Зарегистрироваться» (`RegisterCommand`); подзаголовок «Что даёт Pro» и `ItemsControl ItemsSource="{Binding ProLines}"` (значок-галочка + текст); кнопка «Купить Pro» (`BuyCommand`).
3. **Аккаунт** (по `IsSignedIn`): `Email` (`H3`), строка «Тариф: {PlanText}» + `TermText` + `CheckedText` (`Muted`), `NoteText` (`WarningBrush`, если не пуст); кнопки `BuyText` (`AccentButton`, `BuyCommand`), «Обновить» (`RefreshCommand`), справа `LinkButton` «Выйти» (`LogoutCommand`).
4. **Мои компьютеры** (по `IsSignedIn`): `H3` + `DevicesText`; `ItemsControl ItemsSource="{Binding Devices}"`: имя (жирно) + бейдж «этот компьютер» (`Badge`, по `IsCurrent`) + `Whose` (`Muted`, «чей: …», если не пуст) + `Details`; кнопка «Отвязать» (`Command="{Binding DataContext.UnbindCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" CommandParameter="{Binding}"`).
5. **Промокод**: `TextBox Text="{Binding PromoCode, UpdateSourceTrigger=PropertyChanged}"` + «Применить» (`ApplyPromoCommand`, `IsDefault` не ставить); `PromoText`; кнопка «Открыть покупку» (`OpenBuyWithPromoCommand`, по `PromoIsDiscount`); у гостя поле выключено (`IsEnabled="{Binding CanApplyPromo}"`) и подсказка «Войдите, чтобы применить промокод».
6. **Написать нам**: три `RadioButton` темы (`IsChecked` через конвертер-сравнение с `SupportTopic`, параметры `question`/`bug`/`other`; если конвертера сравнения в `Core` нет — три булевых свойства-обёртки `IsTopicQuestion`… в модели), «Вопрос» / «Ошибка» / «Другое»; поле почты (по `NeedsSupportEmail`); `TextBox` текста (`AcceptsReturn="True"`, высота 120, `TextWrapping="Wrap"`, `MaxLength="5000"`); `CheckBox` «Приложить сведения о системе и журнал» (`SupportAttach`); «Отправить» (`SendSupportCommand`); `SupportResult`.

Все тексты — `{loc:T '…'}`.

- [ ] **Step 2: Сборка, тесты, снимки**

Run: `dotnet build src/PcCleaner -c Debug`; `dotnet test tests/PcCleaner.Tests` → PASS.

```powershell
$exe = "src\PcCleaner\bin\Debug\net7.0-windows\WaterCa7sPcCleaner.exe"
foreach ($tier in "guest","free","pro") { foreach ($lang in "ru","en") {
  Start-Process -Wait $exe -ArgumentList "--screenshots", "$env:TEMP\b2-profile\$tier-$lang", "--page", "Profile", "--no-scan", "--tier", $tier, "--lang", $lang
} }
```

Проверить шесть снимков `11-Profile.png`: гость — кнопки входа и «Что даёт Pro»; free — тариф Free, «Купить Pro», два компьютера (примерные), промокод активен; pro — «Pro до …», «Продлить», «проверено … назад»; английские — без кириллицы (кроме имён компьютеров); в `log.txt` нет `[Error]`.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "Accounts: the Профиль page (guest / free / pro) and its screenshots

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Живой прогон с владельцем и документация

- [ ] **Step 1: Сборка для прогона**

`dotnet build src/PcCleaner -c Release`. Боевой ключ лицензий (`license_pub.pem`, B1 задача 10) должен быть встроен — иначе вход проходит, но программа остаётся Free (это видно в Профиле: «Free» у владельца с Pro). Если ключа нет — прогон делается, пункт про Pro отмечается непроверенным.

- [ ] **Step 2: Прогон (владелец за компьютером, сам вводит почту, пароли и коды; в чат они не попадают)**

Запустить `src\PcCleaner\bin\Release\net7.0-windows\WaterCa7sPcCleaner.exe` и пройти вместе с владельцем:
1. Экран при запуске появился (флаг ещё не стоит) → «Зарегистрироваться» → новая тестовая почта владельца → код из письма → вход, Профиль: Free.
2. «Выйти» → «Войти» → «Забыли пароль?» → код → новый пароль → вход.
3. «Выйти» → вход по паролю основного аккаунта владельца (с Pro) → Профиль: Pro, срок, компьютеры (этот отмечен).
4. «Выйти» → «Через браузер» → код на сайте совпадает → вход.
5. Промокод: неверный код → «Такого промокода нет»; код скидки (если есть) → «Это код скидки…».
6. «Написать нам» с журналом → обращение видно в админке сайта (`/admin/tickets`), журнал открывается.
7. Отвязка: если у тестового аккаунта лимит 1 и уже есть другой компьютер — `DEVICE_LIMIT` → «Отвязать» в окне входа → вход (квота раз в 7 дней — делать, только если владелец согласен её потратить).
8. Перезапуск программы — вход запомнен, экран при запуске больше не показывается.

Записать, что прошло и что нет; найденные ошибки исправить отдельными коммитами с тестами.

- [ ] **Step 3: Документация**

`docs/ARCHITECTURE.md`, раздел «Аккаунт» (B1) — дополнить:

> Экраны: `Modules/Profile/AccountIntroViewModel` (экран «Войти / Зарегистрироваться / Продолжить без входа» после заставки, один раз — `AppSettings.AccountIntroShown`), `SignInViewModel` + `SignInWindow` (пароль, регистрация с кодом, сброс, через браузер, отвязка при `DEVICE_LIMIT`; открывается `SignInDialog.Show`), `ProfileViewModel` (тариф, компьютеры, промокод, «Написать нам», выход). Скриншоты: `--tier guest|free|pro` подставляет локальную учётку без сети (`AccountSetup.ForScreenshots`), `--signin` снимает окно входа.

- [ ] **Step 4: Commit и отчёт**

```bash
git add -A
git commit -m "Accounts: screens documented after the live run

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

Отчёт владельцу по-русски: что сделано, итоги живого прогона по пунктам, несколько снимков (окно входа, Профиль Pro и гость), что дальше — B3 (покупка) и B4 (замки Pro).
