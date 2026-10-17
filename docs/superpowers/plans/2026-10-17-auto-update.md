# Автообновление Windows-версии — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Программа сама находит, скачивает, проверяет по подписи и ставит новую версию; релизы выпускаются из CI по тегу; выходит 1.1.0.

**Architecture:** Новый код в `src/PcCleaner/Core/Updates/` из маленьких частей (манифест, подпись, HTTP, хранилище, установщик, сервис), каждая тестируется без сети на временных папках. `App.OnStartup` ставит подготовленное обновление до окна и запускает фоновую проверку; `MainWindow` показывает полосу, «Настройки» — раздел «Обновления». CI подписывает `update.json` ключом из секрета.

**Tech Stack:** C# / .NET 7 (WPF), `System.Security.Cryptography.ECDsa` (P-256, DER), `HttpClient`, xUnit; PowerShell 7 в CI.

**Спецификация:** `docs/superpowers/specs/2026-10-17-auto-update-design.md`.

## Global Constraints

- Тесты никогда не меняют систему: только временные папки (`TestEnvironment.NewTempDir`) и поддельный HTTP; никакой сети.
- Весь текст для пользователя — по-русски; код, имена и комментарии — по-английски, в стиле окружающего кода.
- Репозиторий: `https://github.com/WaterCa7/WaterCa7-s-pc-cleaner` (`AppInfo.RepoUrl`); ссылка на файл в манифесте строго `{RepoUrl}/releases/download/v{версия}/{имя}`.
- Платформа в манифесте: `win-x64`; имя файла релиза `WaterCa7sPcCleaner.exe`.
- Подпись: ECDSA P-256, SHA-256, формат DER (`DSASignatureFormat.Rfc3279DerSequence`).
- Размер exe в манифесте: от 1 МБ до 500 МБ; SHA-256 — 64 строчных hex-символа.
- Не более 2 неудачных установок одной версии, потом — «Скачать вручную».
- Режим `--screenshots` и сборка для разработки ничего не проверяют и не ставят.
- Каждый коммит заканчивается строкой `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; локальная ветка `main`, отправка `git push origin main:master`.
- Команды проверки: `dotnet build WaterCa7sPcCleaner.sln -c Release` и `dotnet test WaterCa7sPcCleaner.sln -c Release --no-build` (для одного класса — `dotnet test tests/PcCleaner.Tests -c Release --no-build --filter "FullyQualifiedName~<Класс>"`). WPF-ресурсы после правки `.csproj` пересобирать с `--no-incremental`.

## Файлы

| Файл | Ответственность |
|---|---|
| `src/PcCleaner/Core/Updates/UpdateManifest.cs` | `UpdateFile`, `UpdateManifest`: разбор и проверка `update.json`, сравнение версий |
| `src/PcCleaner/Core/Updates/UpdateSignature.cs` | проверка подписи ECDSA |
| `src/PcCleaner/Core/Updates/UpdateKeys.cs` | вшитый открытый ключ |
| `src/PcCleaner/Core/Updates/UpdateClient.cs` | `UpdateException`, HTTP: манифест, подпись, скачивание с проверкой |
| `src/PcCleaner/Core/Updates/UpdateStore.cs` | `StagedUpdate`, папка `Updates`, `state.json` |
| `src/PcCleaner/Core/Updates/UpdateInstaller.cs` | замена exe с проверкой, откат, удаление `.old` |
| `src/PcCleaner/Core/Updates/UpdateEnvironment.cs` | пути, версия, ключ, HTTP, запуск процесса (подменяется в тестах) |
| `src/PcCleaner/Core/Updates/UpdateService.cs` | `UpdateState`, весь сценарий, состояние для интерфейса |
| `src/PcCleaner/Core/Updates/UpdateBanner.cs` | тексты полосы и предупреждения о перезапуске |
| `tools/update-signing-public.pem` | открытый ключ (закрытый — вне репозитория) |
| `tools/make-update-manifest.ps1` | `update.json` + подпись в CI |
| `.github/workflows/release.yml` | релиз по тегу |
| `docs/release-notes/1.1.0.md` | заметки к релизу |
| `LICENSE` | MIT |
| изменения | `AppSettings.cs`, `AppServices.cs`, `App.xaml.cs`, `MainViewModel.cs`, `MainWindow.xaml`, `SettingsViewModel.*`, `SettingsView.xaml`, `PcCleaner.csproj`, `PcCleaner.Tests.csproj`, `ci.yml`, `README.md`, `docs/ARCHITECTURE.md` |
| тесты | `tests/PcCleaner.Tests/Updates/*.cs` |

---

### Task 1: Манифест обновления

**Files:**
- Create: `src/PcCleaner/Core/Updates/UpdateManifest.cs`
- Create: `tests/PcCleaner.Tests/Updates/UpdateTestKit.cs`
- Test: `tests/PcCleaner.Tests/Updates/UpdateManifestTests.cs`

**Interfaces:**
- Produces: `UpdateFile(string Name, string Url, long Size, string Sha256)`; `UpdateManifest(Version Version, DateTimeOffset Published, string Notes, UpdateFile Download)` с `static UpdateManifest? Parse(byte[] json, out string? problem)`, `static bool TryParseVersion(string?, out Version)`, `static Version Normalize(Version)`, `bool IsNewerThan(Version current)`, константами `Platform`, `DownloadPrefix`, `MinSize`, `MaxSize`. `UpdateTestKit.ManifestJson(string version, long size, string sha256, string? url = null, string notes = "Что нового")` → `byte[]`.

- [ ] **Step 1: тестовый набор** — `UpdateTestKit.cs`:

```csharp
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcCleaner.Core;

namespace PcCleaner.Tests.Updates;

/// <summary>Test key pair, manifests, fake exe files and a fake HTTP server for the updater tests (no network).</summary>
internal sealed class UpdateTestKit : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicKeyPem => _key.ExportSubjectPublicKeyInfoPem();

    public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public static string Url(string version, string name = "WaterCa7sPcCleaner.exe")
        => $"{AppInfo.RepoUrl}/releases/download/v{version}/{name}";

    public static byte[] ManifestJson(string version, long size, string sha256, string? url = null, string notes = "Что нового")
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            version,
            published = "2026-10-20T12:00:00Z",
            notes,
            files = new Dictionary<string, object>
            {
                ["win-x64"] = new { name = "WaterCa7sPcCleaner.exe", url = url ?? Url(version), size, sha256 },
            },
        });

    /// <summary>A fake "exe" of at least 1 MB (the manifest minimum) filled with <paramref name="fill"/>.</summary>
    public static byte[] FakeExe(byte fill, int extra = 0)
    {
        var bytes = new byte[(1 << 20) + 4096 + extra];
        Array.Fill(bytes, fill);
        return bytes;
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public void Dispose() => _key.Dispose();
}

/// <summary>In-memory HTTP server: url → bytes; anything else is 404. Records every requested url.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = new();

    public FakeHttp Add(string url, byte[] content)
    {
        _files[url] = content;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        lock (Requests)
            Requests.Add(url);
        var response = _files.TryGetValue(url, out var content)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("Not Found")) };
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}
```

- [ ] **Step 2: падающие тесты** — `UpdateManifestTests.cs`:

```csharp
using System.Text;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

public class UpdateManifestTests
{
    private const string Sha = "4deddbd82f23951e3275de9572c6e70452dd8cf2353c93f1c2638ab57558659a";

    [Fact]
    public void Parse_AcceptsAWellFormedManifest()
    {
        var m = UpdateManifest.Parse(UpdateTestKit.ManifestJson("1.2.0", 66_617_845, Sha, notes: "Новая иконка"), out var problem);

        Assert.Null(problem);
        Assert.NotNull(m);
        Assert.Equal(new Version(1, 2, 0), m!.Version);
        Assert.Equal("Новая иконка", m.Notes);
        Assert.Equal("WaterCa7sPcCleaner.exe", m.Download.Name);
        Assert.Equal(UpdateTestKit.Url("1.2.0"), m.Download.Url);
        Assert.Equal(66_617_845, m.Download.Size);
        Assert.Equal(Sha, m.Download.Sha256);
    }

    [Theory]
    [InlineData("https://github.com/Evil/WaterCa7-s-pc-cleaner/releases/download/v1.2.0/WaterCa7sPcCleaner.exe")]
    [InlineData("https://github.com/WaterCa7/WaterCa7-s-pc-cleaner/releases/download/v1.1.0/WaterCa7sPcCleaner.exe")]
    [InlineData("http://github.com/WaterCa7/WaterCa7-s-pc-cleaner/releases/download/v1.2.0/WaterCa7sPcCleaner.exe")]
    [InlineData("https://example.com/WaterCa7sPcCleaner.exe")]
    public void Parse_RejectsALinkThatIsNotThisVersionOfThisRepository(string url)
    {
        Assert.Null(UpdateManifest.Parse(UpdateTestKit.ManifestJson("1.2.0", 66_617_845, Sha, url), out var problem));
        Assert.Contains("ссылка", problem);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.0.0")]
    [InlineData("v1.2.0")]
    [InlineData("1.2.0-beta")]
    [InlineData("")]
    public void Parse_RejectsBadVersions(string version)
        => Assert.Null(UpdateManifest.Parse(UpdateTestKit.ManifestJson(version, 66_617_845, Sha), out _));

    [Theory]
    [InlineData(1000)]
    [InlineData(600L * 1024 * 1024)]
    public void Parse_RejectsImplausibleSizes(long size)
        => Assert.Null(UpdateManifest.Parse(UpdateTestKit.ManifestJson("1.2.0", size, Sha), out _));

    [Theory]
    [InlineData("abc")]
    [InlineData("zzzzbd82f23951e3275de9572c6e70452dd8cf2353c93f1c2638ab57558659a")]
    public void Parse_RejectsBadSha256(string sha)
        => Assert.Null(UpdateManifest.Parse(UpdateTestKit.ManifestJson("1.2.0", 66_617_845, sha), out _));

    [Fact]
    public void Parse_NormalizesUpperCaseSha256()
        => Assert.Equal(Sha, UpdateManifest.Parse(UpdateTestKit.ManifestJson("1.2.0", 66_617_845, Sha.ToUpperInvariant()), out _)!.Download.Sha256);

    [Fact]
    public void Parse_RejectsGarbageAndMissingPlatform()
    {
        Assert.Null(UpdateManifest.Parse(Encoding.UTF8.GetBytes("{not json"), out var p1));
        Assert.Contains("повреждён", p1);
        Assert.Null(UpdateManifest.Parse(Encoding.UTF8.GetBytes("{\"version\":\"1.2.0\",\"files\":{}}"), out var p2));
        Assert.Contains("win-x64", p2);
    }

    [Theory]
    [InlineData("1.2.0", "1.1.0.0", true)]
    [InlineData("1.1.1", "1.1.0.0", true)]
    [InlineData("1.1.0", "1.1.0.0", false)]
    [InlineData("1.0.9", "1.1.0.0", false)]
    [InlineData("2.0.0", "1.99.99", true)]
    public void IsNewerThan_ComparesThreePartVersions(string manifest, string current, bool newer)
        => Assert.Equal(newer, UpdateManifest.Parse(UpdateTestKit.ManifestJson(manifest, 66_617_845, Sha), out _)!
            .IsNewerThan(Version.Parse(current)));
}
```

- [ ] **Step 3: убедиться, что не собирается** — `dotnet build tests/PcCleaner.Tests -c Release` → ошибка «UpdateManifest не найден».

- [ ] **Step 4: реализация** — `UpdateManifest.cs`:

```csharp
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PcCleaner.Core.Updates;

/// <summary>A downloadable file of a release as listed in update.json.</summary>
public sealed record UpdateFile(string Name, string Url, long Size, string Sha256);

/// <summary>
/// update.json of a release (docs/superpowers/specs/2026-10-17-auto-update-design.md). <see cref="Parse"/> accepts only a
/// Windows file that is a download of this very version from this repository's releases; the signature is checked
/// separately (<see cref="UpdateSignature"/>).
/// </summary>
public sealed record UpdateManifest(Version Version, DateTimeOffset Published, string Notes, UpdateFile Download)
{
    public const string Platform = "win-x64";
    public const string DownloadPrefix = AppInfo.RepoUrl + "/releases/download/";
    public const long MinSize = 1L << 20;
    public const long MaxSize = 500L << 20;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionText = new(@"^\d{1,4}\.\d{1,4}\.\d{1,5}$", RegexOptions.CultureInvariant);

    /// <summary>The manifest, or null with the reason (Russian, for the log) in <paramref name="problem"/>.</summary>
    public static UpdateManifest? Parse(byte[] json, out string? problem)
    {
        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            problem = $"update.json повреждён: {ex.Message}";
            return null;
        }
        if (dto is null)
        {
            problem = "update.json пуст";
            return null;
        }
        if (!TryParseVersion(dto.Version, out var version))
        {
            problem = $"неверная версия «{dto.Version}»";
            return null;
        }
        if (dto.Files is null || !dto.Files.TryGetValue(Platform, out var file) || file is null)
        {
            problem = $"в update.json нет файла для {Platform}";
            return null;
        }
        var name = file.Name ?? "";
        if (name.Length == 0 || name != Path.GetFileName(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            problem = $"неверное имя файла «{name}»";
            return null;
        }
        var url = $"{DownloadPrefix}v{version}/{name}";
        if (!string.Equals(file.Url, url, StringComparison.Ordinal))
        {
            problem = $"ссылка «{file.Url}» не ведёт на релиз v{version} этого репозитория";
            return null;
        }
        if (file.Size < MinSize || file.Size > MaxSize)
        {
            problem = $"неправдоподобный размер файла: {file.Size} байт";
            return null;
        }
        var sha = (file.Sha256 ?? "").ToLowerInvariant();
        if (!Sha256Hex.IsMatch(sha))
        {
            problem = "неверная контрольная сумма SHA-256";
            return null;
        }
        problem = null;
        return new UpdateManifest(version, dto.Published ?? DateTimeOffset.MinValue, dto.Notes?.Trim() ?? "",
            new UpdateFile(name, url, file.Size, sha));
    }

    /// <summary>"1.2.3" only — the assembly's 1.2.3.0 is compared through <see cref="Normalize"/>.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (text is null || !VersionText.IsMatch(text) || !Version.TryParse(text, out var parsed))
            return false;
        version = parsed;
        return true;
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public bool IsNewerThan(Version current) => Version > Normalize(current);

    private sealed class Dto
    {
        public string? Version { get; set; }
        public DateTimeOffset? Published { get; set; }
        public string? Notes { get; set; }
        public Dictionary<string, FileDto?>? Files { get; set; }
    }

    private sealed class FileDto
    {
        public string? Name { get; set; }
        public string? Url { get; set; }
        public long Size { get; set; }
        public string? Sha256 { get; set; }
    }
}
```

- [ ] **Step 5: тесты проходят** — `--filter "FullyQualifiedName~UpdateManifestTests"` → все PASS.
- [ ] **Step 6: коммит** — `git add src/PcCleaner/Core/Updates/UpdateManifest.cs tests/PcCleaner.Tests/Updates/UpdateTestKit.cs tests/PcCleaner.Tests/Updates/UpdateManifestTests.cs` и `git commit` («Updates: update.json manifest»).

---

### Task 2: Подпись и ключ

**Files:**
- Create: `src/PcCleaner/Core/Updates/UpdateSignature.cs`, `src/PcCleaner/Core/Updates/UpdateKeys.cs`, `tools/update-signing-public.pem`
- Modify: `tests/PcCleaner.Tests/PcCleaner.Tests.csproj`
- Test: `tests/PcCleaner.Tests/Updates/UpdateSignatureTests.cs`

**Interfaces:**
- Consumes: `UpdateTestKit.Sign`, `UpdateTestKit.PublicKeyPem`.
- Produces: `UpdateSignature.Verify(byte[] data, byte[] signature, string publicKeyPem)` → `bool`; `UpdateKeys.PublicKeyPem` (`const string`).

- [ ] **Step 1: создать ключ (один раз, вне репозитория)** — в Git Bash:

```bash
mkdir -p "$USERPROFILE/WaterCa7sPcCleaner-signing"
openssl ecparam -name prime256v1 -genkey -noout -out "$USERPROFILE/WaterCa7sPcCleaner-signing/update-signing-key.pem"
openssl ec -in "$USERPROFILE/WaterCa7sPcCleaner-signing/update-signing-key.pem" -pubout -out tools/update-signing-public.pem
```

Закрытый ключ остаётся только в `%USERPROFILE%\WaterCa7sPcCleaner-signing\` (позже — в секрет GitHub и в резервную копию владельца). Проверить: `git status` не показывает закрытый ключ.

- [ ] **Step 2: падающие тесты** — `UpdateSignatureTests.cs`:

```csharp
using System.IO;
using System.Text;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

public class UpdateSignatureTests
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("{\"version\":\"1.2.0\"}");

    [Fact]
    public void Verify_AcceptsTheSignatureOfTheSameBytes()
    {
        using var kit = new UpdateTestKit();
        Assert.True(UpdateSignature.Verify(Data, kit.Sign(Data), kit.PublicKeyPem));
    }

    [Fact]
    public void Verify_RejectsChangedBytesAnotherKeyAndGarbage()
    {
        using var kit = new UpdateTestKit();
        using var other = new UpdateTestKit();
        var signature = kit.Sign(Data);
        var changed = Encoding.UTF8.GetBytes("{\"version\":\"9.9.9\"}");

        Assert.False(UpdateSignature.Verify(changed, signature, kit.PublicKeyPem));
        Assert.False(UpdateSignature.Verify(Data, signature, other.PublicKeyPem));
        Assert.False(UpdateSignature.Verify(Data, new byte[] { 1, 2, 3 }, kit.PublicKeyPem));
        Assert.False(UpdateSignature.Verify(Data, signature, "not a key"));
        Assert.False(UpdateSignature.Verify(Data, Array.Empty<byte>(), kit.PublicKeyPem));
    }

    [Fact]
    public void EmbeddedKey_IsTheOneInTools()
    {
        using var stream = typeof(UpdateSignatureTests).Assembly.GetManifestResourceStream("update-signing-public.pem");
        Assert.NotNull(stream);
        var file = new StreamReader(stream!).ReadToEnd();
        Assert.Equal(Normalize(file), Normalize(UpdateKeys.PublicKeyPem));
        Assert.DoesNotContain("PRIVATE", UpdateKeys.PublicKeyPem);
    }

    private static string Normalize(string pem) => string.Join("\n", pem.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
}
```

В `PcCleaner.Tests.csproj` добавить:

```xml
  <ItemGroup>
    <!-- The public update-signing key; EmbeddedKey_IsTheOneInTools compares it with UpdateKeys. -->
    <EmbeddedResource Include="..\..\tools\update-signing-public.pem" LogicalName="update-signing-public.pem" />
  </ItemGroup>
```

- [ ] **Step 3: не собирается** — `dotnet build tests/PcCleaner.Tests -c Release` → «UpdateSignature не найден».

- [ ] **Step 4: реализация** — `UpdateSignature.cs`:

```csharp
using System.Security.Cryptography;

namespace PcCleaner.Core.Updates;

/// <summary>ECDSA P-256 / SHA-256 signatures in DER form — what `openssl dgst -sha256 -sign` and tools/make-update-manifest.ps1 produce.</summary>
public static class UpdateSignature
{
    public static bool Verify(byte[] data, byte[] signature, string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.KeySize == 256 &&
                   key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
```

`UpdateKeys.cs` — вставить содержимое `tools/update-signing-public.pem` целиком:

```csharp
namespace PcCleaner.Core.Updates;

/// <summary>
/// Public half of the release signing key — the same as tools/update-signing-public.pem (a test compares them). The private
/// half exists only as the GitHub Actions secret UPDATE_SIGNING_KEY and in the owner's backup.
/// </summary>
public static class UpdateKeys
{
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        <две строки base64 из tools/update-signing-public.pem>
        -----END PUBLIC KEY-----
        """;
}
```

- [ ] **Step 5: тесты проходят** — `--filter "FullyQualifiedName~UpdateSignatureTests"` → PASS.
- [ ] **Step 6: коммит** — добавить `UpdateSignature.cs`, `UpdateKeys.cs`, `tools/update-signing-public.pem`, csproj тестов, тест («Updates: signature check and the public key»).

---

### Task 3: HTTP-клиент

**Files:**
- Create: `src/PcCleaner/Core/Updates/UpdateClient.cs`
- Test: `tests/PcCleaner.Tests/Updates/UpdateClientTests.cs`

**Interfaces:**
- Consumes: `UpdateFile`, `FakeHttp`, `UpdateTestKit.FakeExe/Sha256/Url`.
- Produces: `UpdateException(string message, Exception? inner = null)`; `UpdateClient(HttpMessageHandler? handler = null) : IDisposable` с `Task<(byte[] Json, byte[] Signature)> GetManifestAsync(string manifestUrl, CancellationToken ct)`, `Task DownloadAsync(UpdateFile file, string targetPath, IProgress<double>? progress, CancellationToken ct)`, `const int MaxManifestBytes = 65536`.

- [ ] **Step 1: падающие тесты** — `UpdateClientTests.cs`:

```csharp
using System.IO;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

public class UpdateClientTests
{
    private const string ManifestUrl = "https://github.com/WaterCa7/WaterCa7-s-pc-cleaner/releases/latest/download/update.json";

    public UpdateClientTests() => TestEnvironment.Init();

    [Fact]
    public async Task GetManifest_ReadsBothFiles()
    {
        var http = new FakeHttp().Add(ManifestUrl, new byte[] { 1 }).Add(ManifestUrl + ".sig", new byte[] { 2, 3 });
        using var client = new UpdateClient(http);

        var (json, sig) = await client.GetManifestAsync(ManifestUrl, CancellationToken.None);

        Assert.Equal(new byte[] { 1 }, json);
        Assert.Equal(new byte[] { 2, 3 }, sig);
    }

    [Fact]
    public async Task GetManifest_FailsOn404TooBigAndHttp()
    {
        using var client = new UpdateClient(new FakeHttp().Add(ManifestUrl, new byte[UpdateClient.MaxManifestBytes + 1]));
        await Assert.ThrowsAsync<UpdateException>(() => client.GetManifestAsync(ManifestUrl, CancellationToken.None));
        await Assert.ThrowsAsync<UpdateException>(() => client.GetManifestAsync(ManifestUrl + "x", CancellationToken.None));
        await Assert.ThrowsAsync<UpdateException>(() => client.GetManifestAsync("http://github.com/update.json", CancellationToken.None));
    }

    [Fact]
    public async Task Download_WritesTheFileWhenSizeAndHashMatch()
    {
        var exe = UpdateTestKit.FakeExe(7);
        var file = new UpdateFile("WaterCa7sPcCleaner.exe", UpdateTestKit.Url("1.2.0"), exe.Length, UpdateTestKit.Sha256(exe));
        var target = Path.Combine(TestEnvironment.NewTempDir("upd-client"), "WaterCa7sPcCleaner.exe");
        var reported = new List<double>();
        using var client = new UpdateClient(new FakeHttp().Add(file.Url, exe));

        await client.DownloadAsync(file, target, new SyncProgress(reported.Add), CancellationToken.None);

        Assert.Equal(exe, File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".partial"));
        Assert.Equal(1.0, reported.Last(), 3);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("short")]
    [InlineData("long")]
    public async Task Download_RejectsAFileThatDoesNotMatch_AndLeavesNothing(string kind)
    {
        var exe = UpdateTestKit.FakeExe(7);
        var served = kind switch
        {
            "hash" => UpdateTestKit.FakeExe(8),
            "short" => exe[..^10],
            _ => UpdateTestKit.FakeExe(7, extra: 10),
        };
        var file = new UpdateFile("WaterCa7sPcCleaner.exe", UpdateTestKit.Url("1.2.0"), exe.Length, UpdateTestKit.Sha256(exe));
        var target = Path.Combine(TestEnvironment.NewTempDir("upd-client"), "WaterCa7sPcCleaner.exe");
        using var client = new UpdateClient(new FakeHttp().Add(file.Url, served));

        await Assert.ThrowsAsync<UpdateException>(() => client.DownloadAsync(file, target, null, CancellationToken.None));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests need the values synchronously.</summary>
    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _report;
        public SyncProgress(Action<double> report) => _report = report;
        public void Report(double value) => _report(value);
    }
}
```

- [ ] **Step 2: не собирается** — `UpdateClient` не найден.

- [ ] **Step 3: реализация** — `UpdateClient.cs`:

```csharp
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace PcCleaner.Core.Updates;

/// <summary>A failed check or download; the message is Russian and goes to the log and the Settings page.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The HTTP side of the updater: update.json and its signature of the latest release, then the exe.
/// Only https; GitHub's redirects to its download servers are followed. Callers pass tokens with their own timeouts.
/// </summary>
public sealed class UpdateClient : IDisposable
{
    public const int MaxManifestBytes = 64 * 1024;

    private readonly HttpClient _http;

    public UpdateClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"WaterCa7sPcCleaner/{AppInfo.Version}");
    }

    public async Task<(byte[] Json, byte[] Signature)> GetManifestAsync(string manifestUrl, CancellationToken ct)
        => (await GetSmallAsync(manifestUrl, ct), await GetSmallAsync(manifestUrl + ".sig", ct));

    /// <summary>
    /// Downloads <paramref name="file"/> to <paramref name="targetPath"/> through "<paramref name="targetPath"/>.partial";
    /// the size and SHA-256 must match the (signed) manifest, otherwise nothing is left on disk.
    /// </summary>
    public async Task DownloadAsync(UpdateFile file, string targetPath, IProgress<double>? progress, CancellationToken ct)
    {
        var partial = targetPath + ".partial";
        try
        {
            using (var response = await SendAsync(file.Url, ct))
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > file.Size)
                        throw new UpdateException("скачанный файл больше, чем указано в update.json");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report((double)total / file.Size);
                }
                if (total != file.Size)
                    throw new UpdateException($"скачано {total} байт из {file.Size}");
                if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != file.Sha256)
                    throw new UpdateException("контрольная сумма скачанного файла не совпадает с update.json");
            }
            File.Move(partial, targetPath, overwrite: true);
        }
        catch
        {
            UpdateInstaller.TryDelete(partial);
            throw;
        }
    }

    private async Task<byte[]> GetSmallAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxManifestBytes)
                throw new UpdateException($"{url}: файл больше {MaxManifestBytes} байт");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        if (!url.StartsWith("https://", StringComparison.Ordinal))
            throw new UpdateException($"разрешены только https-адреса: {url}");
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            response.Dispose();
            throw new UpdateException($"{url}: перенаправление не на https");
        }
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new UpdateException($"{url}: ответ сервера {code}");
        }
        return response;
    }

    public void Dispose() => _http.Dispose();
}
```

`UpdateInstaller.TryDelete` появится в Task 4; до него временно — локальный `try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }`, заменить в Task 4.

- [ ] **Step 4: тесты проходят** — `--filter "FullyQualifiedName~UpdateClientTests"` → PASS.
- [ ] **Step 5: коммит** («Updates: HTTP client with size and hash checks»).

---

### Task 4: Хранилище и установщик

**Files:**
- Create: `src/PcCleaner/Core/Updates/UpdateStore.cs`, `src/PcCleaner/Core/Updates/UpdateInstaller.cs`
- Modify: `src/PcCleaner/Core/Updates/UpdateClient.cs` (использовать `UpdateInstaller.TryDelete`)
- Test: `tests/PcCleaner.Tests/Updates/UpdateStoreTests.cs`, `tests/PcCleaner.Tests/Updates/UpdateInstallerTests.cs`

**Interfaces:**
- Consumes: `UpdateManifest`, `UpdateSignature`, `UpdateException`, `UpdateTestKit`.
- Produces: `StagedUpdate(UpdateManifest Manifest, string ExePath)`; `UpdateStore(string root)` с `Root`, `DirFor(Version)`, `ExePathFor(UpdateManifest)`, `SaveManifest(UpdateManifest, byte[] json, byte[] signature)`, `StagedUpdate? FindStaged(Version current, string publicKeyPem)`, `UpdateManifest? LoadVerified(Version version, string publicKeyPem)`, `int FailuresFor(Version)`, `void RecordFailure(Version)`, `void Clean(Version? keep = null)`, константы `ManifestName`, `SignatureName`, `MaxInstallFailures = 2`. `UpdateInstaller.Install(StagedUpdate staged, string exePath)`, `Task CleanupAsync(string exePath, TimeSpan timeout, CancellationToken ct = default)`, `internal static bool TryDelete(string path)`, `NewSuffix = ".new"`, `OldSuffix = ".old"`.

- [ ] **Step 1: падающие тесты хранилища** — `UpdateStoreTests.cs`:

```csharp
using System.IO;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

public class UpdateStoreTests
{
    public UpdateStoreTests() => TestEnvironment.Init();

    /// <summary>Stages <paramref name="version"/> like UpdateService does: exe, update.json, update.json.sig.</summary>
    internal static UpdateManifest Stage(UpdateStore store, UpdateTestKit kit, string version, byte[] exe)
    {
        var json = UpdateTestKit.ManifestJson(version, exe.Length, UpdateTestKit.Sha256(exe), notes: $"Версия {version}");
        var manifest = UpdateManifest.Parse(json, out _)!;
        Directory.CreateDirectory(store.DirFor(manifest.Version));
        File.WriteAllBytes(store.ExePathFor(manifest), exe);
        store.SaveManifest(manifest, json, kit.Sign(json));
        return manifest;
    }

    [Fact]
    public void FindStaged_ReturnsTheNewestCompleteSignedVersionAboveCurrent()
    {
        using var kit = new UpdateTestKit();
        var store = new UpdateStore(TestEnvironment.NewTempDir("upd-store"));
        Stage(store, kit, "1.1.0", UpdateTestKit.FakeExe(1));
        Stage(store, kit, "1.3.0", UpdateTestKit.FakeExe(3));
        Stage(store, kit, "1.2.0", UpdateTestKit.FakeExe(2));

        var staged = store.FindStaged(new Version(1, 1, 0, 0), kit.PublicKeyPem);

        Assert.Equal(new Version(1, 3, 0), staged!.Manifest.Version);
        Assert.Equal(store.ExePathFor(staged.Manifest), staged.ExePath);
        Assert.Null(store.FindStaged(new Version(1, 3, 0), kit.PublicKeyPem));
    }

    [Fact]
    public void FindStaged_SkipsUnsignedIncompleteAndMisplacedVersions()
    {
        using var kit = new UpdateTestKit();
        using var other = new UpdateTestKit();
        var store = new UpdateStore(TestEnvironment.NewTempDir("upd-store"));
        var unsigned = Stage(store, kit, "1.2.0", UpdateTestKit.FakeExe(2));
        File.WriteAllBytes(Path.Combine(store.DirFor(unsigned.Version), UpdateStore.SignatureName), other.Sign(new byte[] { 1 }));
        var incomplete = Stage(store, kit, "1.3.0", UpdateTestKit.FakeExe(3));
        File.WriteAllBytes(store.ExePathFor(incomplete), new byte[10]);
        var misplaced = Stage(store, kit, "1.4.0", UpdateTestKit.FakeExe(4));
        Directory.Move(store.DirFor(misplaced.Version), Path.Combine(store.Root, "9.9.9"));

        Assert.Null(store.FindStaged(new Version(1, 1, 0), kit.PublicKeyPem));
    }

    [Fact]
    public void Failures_AreCountedPerVersion()
    {
        var store = new UpdateStore(TestEnvironment.NewTempDir("upd-store"));
        var v = new Version(1, 2, 0);
        Assert.Equal(0, store.FailuresFor(v));
        store.RecordFailure(v);
        store.RecordFailure(v);
        Assert.Equal(2, store.FailuresFor(v));
        store.RecordFailure(new Version(1, 3, 0));
        Assert.Equal(0, store.FailuresFor(v));
        Assert.Equal(1, store.FailuresFor(new Version(1, 3, 0)));
    }

    [Fact]
    public void Clean_KeepsOnlyTheGivenVersion_OrRemovesEverything()
    {
        using var kit = new UpdateTestKit();
        var store = new UpdateStore(TestEnvironment.NewTempDir("upd-store"));
        Stage(store, kit, "1.2.0", UpdateTestKit.FakeExe(2));
        Stage(store, kit, "1.3.0", UpdateTestKit.FakeExe(3));
        store.RecordFailure(new Version(1, 3, 0));

        store.Clean(keep: new Version(1, 3, 0));
        Assert.False(Directory.Exists(store.DirFor(new Version(1, 2, 0))));
        Assert.True(Directory.Exists(store.DirFor(new Version(1, 3, 0))));
        Assert.Equal(1, store.FailuresFor(new Version(1, 3, 0)));

        store.Clean();
        Assert.Empty(Directory.EnumerateFileSystemEntries(store.Root));
    }
}
```

- [ ] **Step 2: падающие тесты установщика** — `UpdateInstallerTests.cs`:

```csharp
using System.Diagnostics;
using System.IO;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

public class UpdateInstallerTests
{
    public UpdateInstallerTests() => TestEnvironment.Init();

    private static (string Exe, StagedUpdate Staged, byte[] NewBytes) Setup(UpdateTestKit kit, byte[]? oldBytes = null)
    {
        var app = TestEnvironment.NewTempDir("upd-app");
        var exe = Path.Combine(app, "WaterCa7sPcCleaner.exe");
        File.WriteAllBytes(exe, oldBytes ?? UpdateTestKit.FakeExe(1));
        var store = new UpdateStore(TestEnvironment.NewTempDir("upd-store"));
        var newBytes = UpdateTestKit.FakeExe(2);
        var manifest = UpdateStoreTests.Stage(store, kit, "1.2.0", newBytes);
        return (exe, new StagedUpdate(manifest, store.ExePathFor(manifest)), newBytes);
    }

    [Fact]
    public async Task Install_SwapsTheExe_AndCleanupRemovesTheOldOne()
    {
        using var kit = new UpdateTestKit();
        var (exe, staged, newBytes) = Setup(kit);

        UpdateInstaller.Install(staged, exe);

        Assert.Equal(newBytes, File.ReadAllBytes(exe));
        Assert.True(File.Exists(exe + UpdateInstaller.OldSuffix));
        Assert.False(File.Exists(exe + UpdateInstaller.NewSuffix));

        await UpdateInstaller.CleanupAsync(exe, TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(exe + UpdateInstaller.OldSuffix));
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public void Install_RefusesAStagedFileChangedAfterTheCheck_AndKeepsTheOldExe()
    {
        using var kit = new UpdateTestKit();
        var old = UpdateTestKit.FakeExe(1);
        var (exe, staged, _) = Setup(kit, old);
        File.WriteAllBytes(staged.ExePath, UpdateTestKit.FakeExe(9)); // same size, other bytes

        Assert.Throws<UpdateException>(() => UpdateInstaller.Install(staged, exe));

        Assert.Equal(old, File.ReadAllBytes(exe));
        Assert.False(File.Exists(exe + UpdateInstaller.NewSuffix));
        Assert.False(File.Exists(exe + UpdateInstaller.OldSuffix));
    }

    [Fact]
    public void Install_WhenTheExeCannotBeMoved_LeavesEverythingAsItWas()
    {
        using var kit = new UpdateTestKit();
        var old = UpdateTestKit.FakeExe(1);
        var (exe, staged, _) = Setup(kit, old);
        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read)) // no FILE_SHARE_DELETE: rename fails
            Assert.ThrowsAny<IOException>(() => UpdateInstaller.Install(staged, exe));

        Assert.Equal(old, File.ReadAllBytes(exe));
        Assert.False(File.Exists(exe + UpdateInstaller.NewSuffix));
        Assert.False(File.Exists(exe + UpdateInstaller.OldSuffix));
    }

    [Fact]
    public async Task Install_ReplacesARunningExe()
    {
        // Windows lets a running exe be renamed: the updater relies on this. A copy of ping.exe stands in for the app.
        using var kit = new UpdateTestKit();
        var (exe, staged, newBytes) = Setup(kit);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe, overwrite: true);
        using var running = Process.Start(new ProcessStartInfo(exe, "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            UpdateInstaller.Install(staged, exe);
            Assert.Equal(newBytes, File.ReadAllBytes(exe));
        }
        finally
        {
            running.Kill();
            running.WaitForExit();
        }
        await UpdateInstaller.CleanupAsync(exe, TimeSpan.FromSeconds(10));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, "*" + UpdateInstaller.OldSuffix));
    }
}
```

- [ ] **Step 3: не собирается** — `UpdateStore`/`UpdateInstaller` не найдены.

- [ ] **Step 4: реализация** — `UpdateStore.cs`:

```csharp
using System.IO;
using System.Text.Json;

namespace PcCleaner.Core.Updates;

/// <summary>A downloaded update with a valid signature, waiting to be installed.</summary>
public sealed record StagedUpdate(UpdateManifest Manifest, string ExePath);

/// <summary>
/// %LOCALAPPDATA%\WaterCa7sPcCleaner\Updates: a folder per version (exe, update.json, update.json.sig) and state.json with
/// the count of failed installs. Everything here is user-writable, so it is verified again before use.
/// </summary>
public sealed class UpdateStore
{
    public const string ManifestName = "update.json";
    public const string SignatureName = "update.json.sig";
    public const int MaxInstallFailures = 2;

    public UpdateStore(string root) => Root = root;

    public string Root { get; }

    private string StatePath => Path.Combine(Root, "state.json");

    public string DirFor(Version version) => Path.Combine(Root, version.ToString(3));

    public string ExePathFor(UpdateManifest manifest) => Path.Combine(DirFor(manifest.Version), manifest.Download.Name);

    public void SaveManifest(UpdateManifest manifest, byte[] json, byte[] signature)
    {
        var dir = AppPaths.EnsureDir(DirFor(manifest.Version));
        File.WriteAllBytes(Path.Combine(dir, ManifestName), json);
        File.WriteAllBytes(Path.Combine(dir, SignatureName), signature);
    }

    /// <summary>The signed manifest stored for <paramref name="version"/>, or null.</summary>
    public UpdateManifest? LoadVerified(Version version, string publicKeyPem)
    {
        try
        {
            var dir = DirFor(version);
            var json = File.ReadAllBytes(Path.Combine(dir, ManifestName));
            if (!UpdateSignature.Verify(json, File.ReadAllBytes(Path.Combine(dir, SignatureName)), publicKeyPem))
                return null;
            var manifest = UpdateManifest.Parse(json, out _);
            return manifest?.Version == UpdateManifest.Normalize(version) ? manifest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The newest version above <paramref name="current"/> whose files are complete and whose update.json is signed.</summary>
    public StagedUpdate? FindStaged(Version current, string publicKeyPem)
    {
        if (!Directory.Exists(Root))
            return null;
        StagedUpdate? best = null;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (!UpdateManifest.TryParseVersion(Path.GetFileName(dir), out var version))
                continue;
            var manifest = LoadVerified(version, publicKeyPem);
            if (manifest is null || !manifest.IsNewerThan(current))
                continue;
            var exe = ExePathFor(manifest);
            if (!File.Exists(exe) || new FileInfo(exe).Length != manifest.Download.Size)
                continue;
            if (best is null || manifest.Version > best.Manifest.Version)
                best = new StagedUpdate(manifest, exe);
        }
        return best;
    }

    public int FailuresFor(Version version)
    {
        var state = ReadState();
        return state.Version == version.ToString(3) ? state.Failures : 0;
    }

    public void RecordFailure(Version version)
    {
        var state = ReadState();
        var v = version.ToString(3);
        AppPaths.EnsureDir(Root);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(new State { Version = v, Failures = state.Version == v ? state.Failures + 1 : 1 }));
    }

    /// <summary>Removes every version folder except <paramref name="keep"/>; with no <paramref name="keep"/> also state.json.</summary>
    public void Clean(Version? keep = null)
    {
        if (!Directory.Exists(Root))
            return;
        var keepDir = keep is null ? null : DirFor(keep);
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (keepDir is not null && string.Equals(dir, keepDir, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Обновление: не удалось удалить {dir}: {ex.Message}");
            }
        }
        if (keep is null)
            UpdateInstaller.TryDelete(StatePath);
    }

    private State ReadState()
    {
        try
        {
            return JsonSerializer.Deserialize<State>(File.ReadAllBytes(StatePath)) ?? new State();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new State();
        }
    }

    private sealed class State
    {
        public string? Version { get; set; }
        public int Failures { get; set; }
    }
}
```

`UpdateInstaller.cs`:

```csharp
using System.IO;
using System.Security.Cryptography;

namespace PcCleaner.Core.Updates;

/// <summary>
/// Puts a staged exe in place of the running one. The staged file lives in a user-writable folder, so it is opened with
/// writes denied and the very bytes that are copied are the bytes that are hashed: nothing can swap the file between the
/// check and the install, even when the program runs as administrator.
/// </summary>
public static class UpdateInstaller
{
    public const string NewSuffix = ".new";
    public const string OldSuffix = ".old";

    /// <summary>
    /// Replaces <paramref name="exePath"/> with the staged file; the current exe becomes "*.old" (Windows allows renaming a
    /// running exe). On any failure the original exe stays in place or is put back.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The program folder is not writable.</exception>
    /// <exception cref="UpdateException">The staged file does not match update.json.</exception>
    /// <exception cref="IOException">A file is locked.</exception>
    public static void Install(StagedUpdate staged, string exePath)
    {
        var newPath = exePath + NewSuffix;
        var oldPath = exePath + OldSuffix;
        var movedAway = false;
        try
        {
            if (File.Exists(newPath))
                File.Delete(newPath);
            CopyVerified(staged, newPath);
            if (!TryDelete(oldPath))
                oldPath = $"{exePath}.{Guid.NewGuid():N}{OldSuffix}";
            File.Move(exePath, oldPath);
            movedAway = true;
            File.Move(newPath, exePath);
        }
        catch
        {
            if (movedAway && !File.Exists(exePath))
                File.Move(oldPath, exePath);
            TryDelete(newPath);
            throw;
        }
    }

    /// <summary>Run by the new version: deletes the previous exe(s) next to it, retrying while the old process exits.</summary>
    public static async Task CleanupAsync(string exePath, TimeSpan timeout, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(exePath)!;
        var pattern = Path.GetFileName(exePath) + "*" + OldSuffix;
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            TryDelete(exePath + NewSuffix);
            var left = Directory.EnumerateFiles(dir, pattern).Count(file => !TryDelete(file));
            if (left == 0 || DateTime.UtcNow >= deadline)
                return;
            await Task.Delay(500, ct);
        }
    }

    internal static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CopyVerified(StagedUpdate staged, string newPath)
    {
        var expected = staged.Manifest.Download;
        using var source = new FileStream(staged.ExePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var target = new FileStream(newPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > expected.Size)
                throw new UpdateException("подготовленный файл больше, чем указано в update.json");
            hash.AppendData(buffer, 0, read);
            target.Write(buffer, 0, read);
        }
        if (total != expected.Size || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != expected.Sha256)
            throw new UpdateException("подготовленный файл не совпадает с update.json");
        target.Flush(flushToDisk: true);
    }
}
```

В `UpdateClient.DownloadAsync` заменить временное удаление на `UpdateInstaller.TryDelete(partial);`.

- [ ] **Step 5: тесты проходят** — `--filter "FullyQualifiedName~UpdateStoreTests|FullyQualifiedName~UpdateInstallerTests|FullyQualifiedName~UpdateClientTests"` → PASS.
- [ ] **Step 6: коммит** («Updates: staged updates and a verified exe swap»).

---

### Task 5: Сервис обновлений и настройки

**Files:**
- Create: `src/PcCleaner/Core/Updates/UpdateEnvironment.cs`, `src/PcCleaner/Core/Updates/UpdateService.cs`
- Modify: `src/PcCleaner/Core/AppSettings.cs` (после `TrustedItems`), `src/PcCleaner/Core/AppServices.cs`
- Test: `tests/PcCleaner.Tests/Updates/UpdateServiceTests.cs`

**Interfaces:**
- Consumes: всё из Task 1–4.
- Produces: `AppSettings.AutoUpdate` (`bool`, по умолчанию `true`), `AppSettings.LastUpdateCheckUtc` (`DateTime?`); `UpdateEnvironment` (`ExeName`, `required ExePath`, `required UpdatesDir`, `required CurrentVersion`, `PublicKeyPem`, `ManifestUrl`, `HttpHandler`, `StartProcess: Func<string, IReadOnlyList<string>, bool>`, `CheckTimeout`, `SaveSettings: Action<AppSettings>`, `IsInstalledBuild`, `static ForCurrentProcess()`); `enum UpdateState { None, Checking, UpToDate, Downloading, Ready, Updated, ManualRequired, Failed }`; `UpdateService(UpdateEnvironment env, Func<AppSettings> settings) : ObservableObject` с `State`, `StatusText`, `Manifest`, `Progress`, `CurrentVersion`, `CanUpdate`, `static ReleasesUrl`, `const UpdatedFromArg = "--updated-from"`, `bool TryInstallStaged(IReadOnlyList<string> args)`, `bool InstallNow(IReadOnlyList<string> args)`, `Task CompleteUpdateAsync(string fromVersion)`, `Task CheckAsync(bool manual, CancellationToken ct)`, `internal static List<string> RestartArgs(IReadOnlyList<string> args, Version from)`; `AppServices.Updates`.

- [ ] **Step 1: падающие тесты** — `UpdateServiceTests.cs`:

```csharp
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using PcCleaner.Core;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

/// <summary>The whole flow on temp folders with a fake GitHub (no network, no real process starts).</summary>
public sealed class UpdateServiceTests : IDisposable
{
    private const string ManifestUrl = "https://github.com/WaterCa7/WaterCa7-s-pc-cleaner/releases/latest/download/update.json";

    private readonly UpdateTestKit _kit = new();
    private readonly FakeHttp _http = new();
    private readonly string _appDir;
    private readonly string _updatesDir;
    private readonly byte[] _oldExe = UpdateTestKit.FakeExe(1);
    private readonly byte[] _newExe = UpdateTestKit.FakeExe(2);
    private readonly AppSettings _settings = new();
    private readonly List<(string Exe, IReadOnlyList<string> Args)> _started = new();

    public UpdateServiceTests()
    {
        TestEnvironment.Init();
        _appDir = TestEnvironment.NewTempDir("upd-app");
        _updatesDir = TestEnvironment.NewTempDir("upd-data");
        File.WriteAllBytes(ExePath, _oldExe);
    }

    private string ExePath => Path.Combine(_appDir, UpdateEnvironment.ExeName);

    private UpdateService Service(string current = "1.1.0") => new(new UpdateEnvironment
    {
        ExePath = ExePath,
        UpdatesDir = _updatesDir,
        CurrentVersion = Version.Parse(current + ".0"),
        PublicKeyPem = _kit.PublicKeyPem,
        ManifestUrl = ManifestUrl,
        HttpHandler = _http,
        StartProcess = (exe, args) =>
        {
            _started.Add((exe, args));
            return true;
        },
        SaveSettings = _ => { },
    }, () => _settings);

    private void Publish(string version, byte[]? exe = null, bool badSignature = false)
    {
        exe ??= _newExe;
        var json = UpdateTestKit.ManifestJson(version, exe.Length, UpdateTestKit.Sha256(exe), notes: "Новая иконка");
        _http.Add(ManifestUrl, json)
             .Add(ManifestUrl + ".sig", badSignature ? _kit.Sign(new byte[] { 0 }) : _kit.Sign(json))
             .Add(UpdateTestKit.Url(version), exe);
    }

    [Fact]
    public async Task FullCycle_CheckDownloadsThenTheNextStartInstallsAndTheNewVersionCleansUp()
    {
        Publish("1.2.0");
        var first = Service();

        await first.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(UpdateState.Ready, first.State);
        Assert.Equal(new Version(1, 2, 0), first.Manifest!.Version);
        Assert.NotNull(_settings.LastUpdateCheckUtc);
        Assert.Equal(_oldExe, File.ReadAllBytes(ExePath)); // nothing installed while running

        var second = Service();
        Assert.True(second.TryInstallStaged(new[] { "--page", "Cleaner" }));
        Assert.Equal(_newExe, File.ReadAllBytes(ExePath));
        var (startedExe, args) = Assert.Single(_started);
        Assert.Equal(ExePath, startedExe);
        Assert.Equal(new[] { "--page", "Cleaner", UpdateService.UpdatedFromArg, "1.1.0" }, args);

        var updated = Service("1.2.0");
        await updated.CompleteUpdateAsync("1.1.0");
        Assert.Equal(UpdateState.Updated, updated.State);
        Assert.Equal("Новая иконка", updated.Manifest!.Notes);
        Assert.False(File.Exists(ExePath + UpdateInstaller.OldSuffix));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_updatesDir));
    }

    [Fact]
    public async Task SameVersion_IsUpToDate_AndNothingIsDownloaded()
    {
        Publish("1.1.0");
        var service = Service();

        await service.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(UpdateState.UpToDate, service.State);
        Assert.DoesNotContain(UpdateTestKit.Url("1.1.0"), _http.Requests);
    }

    [Fact]
    public async Task BadSignature_DownloadsNothing()
    {
        Publish("1.2.0", badSignature: true);
        var service = Service();

        await service.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(UpdateState.Failed, service.State);
        Assert.Contains("подпись", service.StatusText);
        Assert.DoesNotContain(UpdateTestKit.Url("1.2.0"), _http.Requests);
        Assert.False(service.TryInstallStaged(Array.Empty<string>()));
    }

    [Fact]
    public async Task CorruptDownload_IsNotStaged()
    {
        Publish("1.2.0");
        _http.Add(UpdateTestKit.Url("1.2.0"), UpdateTestKit.FakeExe(5)); // served bytes differ from the manifest
        var service = Service();

        await service.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(UpdateState.Failed, service.State);
        Assert.False(Service().TryInstallStaged(Array.Empty<string>()));
        Assert.Equal(_oldExe, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public async Task NoNetwork_FailsQuietly()
    {
        var service = Service(); // FakeHttp answers 404 to everything

        await service.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(UpdateState.Failed, service.State);
        Assert.Equal(_oldExe, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public async Task AutoUpdateOff_NoAutomaticCheckOrInstall_ButManualCheckWorks()
    {
        Publish("1.2.0");
        _settings.AutoUpdate = false;
        var service = Service();

        await service.CheckAsync(manual: false, CancellationToken.None);
        Assert.Empty(_http.Requests);

        await service.CheckAsync(manual: true, CancellationToken.None);
        Assert.Equal(UpdateState.Ready, service.State);
        Assert.False(Service().TryInstallStaged(Array.Empty<string>()));
        Assert.True(Service().InstallNow(Array.Empty<string>()));
        Assert.Equal(_newExe, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public async Task DevelopmentBuild_NeverChecksOrInstalls()
    {
        Publish("1.2.0");
        File.WriteAllBytes(Path.Combine(_appDir, "WaterCa7sPcCleaner.dll"), new byte[] { 1 });
        var service = Service();

        await service.CheckAsync(manual: false, CancellationToken.None);
        await service.CheckAsync(manual: true, CancellationToken.None);

        Assert.Empty(_http.Requests);
        Assert.False(service.CanUpdate);
        Assert.Equal(UpdateState.Failed, service.State);
        Assert.Contains("разработ", service.StatusText);
    }

    [Fact]
    public async Task LockedExe_TwoFailedInstalls_ThenManualDownload()
    {
        Publish("1.2.0");
        await Service().CheckAsync(manual: false, CancellationToken.None);

        using (new FileStream(ExePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(Service().TryInstallStaged(Array.Empty<string>()));
            Assert.False(Service().TryInstallStaged(Array.Empty<string>()));
        }
        var third = Service();
        Assert.False(third.TryInstallStaged(Array.Empty<string>()));

        Assert.Equal(UpdateState.ManualRequired, third.State);
        Assert.Equal(_oldExe, File.ReadAllBytes(ExePath));
        Assert.Empty(_started);
    }

    [Fact]
    public async Task ReadOnlyProgramFolder_AsksForAManualDownload()
    {
        Publish("1.2.0");
        await Service().CheckAsync(manual: false, CancellationToken.None);
        var dir = new DirectoryInfo(_appDir);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles, AccessControlType.Deny);
        var acl = dir.GetAccessControl();
        acl.AddAccessRule(deny);
        dir.SetAccessControl(acl);
        try
        {
            var service = Service();
            Assert.False(service.TryInstallStaged(Array.Empty<string>()));
            Assert.Equal(UpdateState.ManualRequired, service.State);
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            dir.SetAccessControl(acl);
        }
        Assert.Equal(_oldExe, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public void RestartArgs_ReplaceAnEarlierUpdatedFrom()
        => Assert.Equal(new[] { "--no-scan", UpdateService.UpdatedFromArg, "1.2.0" },
            UpdateService.RestartArgs(new[] { UpdateService.UpdatedFromArg, "1.1.0", "--no-scan" }, new Version(1, 2, 0)));

    [Fact]
    public void Settings_AutoUpdateIsOnByDefault_AndSurvivesSaving()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { AutoUpdate = false, LastUpdateCheckUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc) });
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.True(new AppSettings().AutoUpdate);
        Assert.False(back.AutoUpdate);
        Assert.Equal(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), back.LastUpdateCheckUtc);
    }

    public void Dispose()
    {
        _kit.Dispose();
        _http.Dispose();
    }
}
```

- [ ] **Step 2: не собирается** — `UpdateService`/`UpdateEnvironment`/`AutoUpdate` не найдены.

- [ ] **Step 3: настройки** — в `AppSettings.cs` после `TrustedItems`:

```csharp
    /// <summary>Download and install new versions by itself (Settings → «Обновления»).</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>When update.json was last read successfully (UTC).</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }
```

- [ ] **Step 4: окружение** — `UpdateEnvironment.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace PcCleaner.Core.Updates;

/// <summary>Everything the updater depends on outside itself; tests replace every part.</summary>
public sealed class UpdateEnvironment
{
    public const string ExeName = "WaterCa7sPcCleaner.exe";

    public required string ExePath { get; init; }

    public required string UpdatesDir { get; init; }

    public required Version CurrentVersion { get; init; }

    public string PublicKeyPem { get; init; } = UpdateKeys.PublicKeyPem;

    public string ManifestUrl { get; init; } = AppInfo.RepoUrl + "/releases/latest/download/update.json";

    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>Starts the exe with the arguments; false when it could not be started.</summary>
    public Func<string, IReadOnlyList<string>, bool> StartProcess { get; init; } = Start;

    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Saves settings.json (tests keep the shared test settings file untouched).</summary>
    public Action<AppSettings> SaveSettings { get; init; } = settings => settings.Save();

    /// <summary>
    /// Only the published single-file exe updates itself. A development build (WaterCa7sPcCleaner.dll next to the exe,
    /// or a renamed exe) never does — an update would replace it with a release build.
    /// </summary>
    public bool IsInstalledBuild
        => string.Equals(Path.GetFileName(ExePath), ExeName, StringComparison.OrdinalIgnoreCase) &&
           File.Exists(ExePath) &&
           !File.Exists(Path.Combine(Path.GetDirectoryName(ExePath)!, Path.GetFileNameWithoutExtension(ExeName) + ".dll"));

    public static UpdateEnvironment ForCurrentProcess() => new()
    {
        ExePath = Environment.ProcessPath ?? "",
        UpdatesDir = Path.Combine(AppPaths.DataDir, "Updates"),
        CurrentVersion = typeof(AppInfo).Assembly.GetName().Version ?? new Version(1, 0, 0),
    };

    private static bool Start(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        try
        {
            return Process.Start(psi) is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Обновление: не удалось запустить {exe}: {ex.Message}");
            return false;
        }
    }
}
```

- [ ] **Step 5: сервис** — `UpdateService.cs`:

```csharp
using System.IO;
using System.Net.Http;

namespace PcCleaner.Core.Updates;

public enum UpdateState
{
    None,
    Checking,
    UpToDate,
    Downloading,
    Ready,
    Updated,
    ManualRequired,
    Failed,
}

/// <summary>
/// Automatic updates: at startup (before the window) install a staged update; in the background check, download and stage
/// a newer version; report the state for the banner and the Settings page. Screenshot mode never creates the flow
/// (App skips it); a development build never checks or installs.
/// </summary>
public sealed class UpdateService : ObservableObject
{
    public const string UpdatedFromArg = "--updated-from";

    private readonly UpdateEnvironment _env;
    private readonly Func<AppSettings> _settings;
    private readonly UpdateStore _store;
    private UpdateState _state;
    private string _statusText = "";
    private UpdateManifest? _manifest;
    private double _progress;
    private int _checking;

    public UpdateService(UpdateEnvironment env, Func<AppSettings> settings)
    {
        _env = env;
        _settings = settings;
        _store = new UpdateStore(env.UpdatesDir);
    }

    public static string ReleasesUrl => AppInfo.RepoUrl + "/releases/latest";

    public UpdateState State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>The version found or downloaded (Ready, ManualRequired) or just installed (Updated).</summary>
    public UpdateManifest? Manifest
    {
        get => _manifest;
        private set => Set(ref _manifest, value);
    }

    /// <summary>Download progress 0..1.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public Version CurrentVersion => UpdateManifest.Normalize(_env.CurrentVersion);

    public bool CanUpdate => _env.IsInstalledBuild;

    /// <summary>
    /// At startup, before the window: if a verified update is staged and automatic updates are on, put it in place and
    /// start it. True = the new version is starting and this process must exit now.
    /// </summary>
    public bool TryInstallStaged(IReadOnlyList<string> args)
        => CanUpdate && _settings().AutoUpdate && Install(_store.FindStaged(_env.CurrentVersion, _env.PublicKeyPem), args);

    /// <summary>«Перезапустить сейчас»: installs the staged update whatever the setting says (the user asked for it).</summary>
    public bool InstallNow(IReadOnlyList<string> args)
        => CanUpdate && Install(_store.FindStaged(_env.CurrentVersion, _env.PublicKeyPem), args);

    /// <summary>The new version, right after the restart: removes the old exe and the staged files.</summary>
    public async Task CompleteUpdateAsync(string fromVersion)
    {
        Manifest = _store.LoadVerified(CurrentVersion, _env.PublicKeyPem);
        _store.Clean();
        await UpdateInstaller.CleanupAsync(_env.ExePath, TimeSpan.FromSeconds(30));
        State = UpdateState.Updated;
        StatusText = $"Программа обновлена с версии {fromVersion} до {CurrentVersion.ToString(3)}.";
        Log.Action($"Обновление: работает версия {CurrentVersion.ToString(3)} (была {fromVersion})");
    }

    /// <summary>
    /// Finds, downloads and stages a newer version. Automatic checks (<paramref name="manual"/> = false) run only when
    /// AutoUpdate is on; «Проверить сейчас» always runs. Never throws: problems end in <see cref="UpdateState.Failed"/>.
    /// </summary>
    public async Task CheckAsync(bool manual, CancellationToken ct)
    {
        if (!CanUpdate)
        {
            if (manual)
            {
                State = UpdateState.Failed;
                StatusText = "Эта копия программы — сборка для разработки, она не обновляется автоматически.";
            }
            return;
        }
        if (!manual && !_settings().AutoUpdate)
            return;
        if (Interlocked.Exchange(ref _checking, 1) == 1)
            return;
        try
        {
            State = UpdateState.Checking;
            StatusText = "Проверка обновлений…";
            using var client = new UpdateClient(_env.HttpHandler);
            byte[] json, signature;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(_env.CheckTimeout);
                (json, signature) = await client.GetManifestAsync(_env.ManifestUrl, timeout.Token);
            }
            var settings = _settings();
            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _env.SaveSettings(settings);

            if (!UpdateSignature.Verify(json, signature, _env.PublicKeyPem))
                throw new UpdateException("цифровая подпись update.json не сходится — файл не от автора программы");
            var manifest = UpdateManifest.Parse(json, out var problem) ?? throw new UpdateException(problem ?? "update.json не прочитан");
            if (!manifest.IsNewerThan(_env.CurrentVersion))
            {
                _store.Clean();
                State = UpdateState.UpToDate;
                StatusText = $"Установлена последняя версия ({CurrentVersion.ToString(3)}).";
                Log.Info($"Обновления: последняя версия {manifest.Version} — обновлять нечего");
                return;
            }

            Manifest = manifest;
            if (_store.FailuresFor(manifest.Version) >= UpdateStore.MaxInstallFailures)
            {
                RequireManual(manifest, "автоматическая установка уже дважды не удалась");
                return;
            }
            if (_store.FindStaged(_env.CurrentVersion, _env.PublicKeyPem)?.Manifest.Version != manifest.Version)
            {
                State = UpdateState.Downloading;
                StatusText = $"Скачивается версия {manifest.Version}…";
                _store.Clean(keep: manifest.Version);
                AppPaths.EnsureDir(_store.DirFor(manifest.Version));
                await client.DownloadAsync(manifest.Download, _store.ExePathFor(manifest), new Progress<double>(p => Progress = p), ct);
                _store.SaveManifest(manifest, json, signature);
                Log.Info($"Обновления: скачана версия {manifest.Version}, установится при следующем запуске");
            }
            State = UpdateState.Ready;
            StatusText = $"Версия {manifest.Version} скачана — установится при следующем запуске.";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or
                                       UnauthorizedAccessException or UpdateException)
        {
            var offline = ex is HttpRequestException or OperationCanceledException;
            Log.Info($"Обновления: {(offline ? "нет связи с GitHub" : "ошибка")} — {ex.Message}");
            State = UpdateState.Failed;
            StatusText = offline ? "Не удалось проверить обновления: нет связи с GitHub." : $"Не удалось обновиться: {ex.Message}.";
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    internal static List<string> RestartArgs(IReadOnlyList<string> args, Version from)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], UpdatedFromArg, StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }
            result.Add(args[i]);
        }
        result.Add(UpdatedFromArg);
        result.Add(from.ToString(3));
        return result;
    }

    private bool Install(StagedUpdate? staged, IReadOnlyList<string> args)
    {
        if (staged is null)
            return false;
        var version = staged.Manifest.Version;
        Manifest = staged.Manifest;
        if (_store.FailuresFor(version) >= UpdateStore.MaxInstallFailures)
        {
            RequireManual(staged.Manifest, "автоматическая установка уже дважды не удалась");
            return false;
        }
        try
        {
            UpdateInstaller.Install(staged, _env.ExePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn($"Обновление до {version}: нет прав на запись в папку {Path.GetDirectoryName(_env.ExePath)} — {ex.Message}");
            RequireManual(staged.Manifest, "нет прав на запись в папку программы");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UpdateException)
        {
            _store.RecordFailure(version);
            Log.Warn($"Обновление до {version} не установлено: {ex.Message}");
            State = UpdateState.Failed;
            StatusText = $"Не удалось установить версию {version}: {ex.Message}. Попробуем при следующем запуске.";
            return false;
        }
        if (!_env.StartProcess(_env.ExePath, RestartArgs(args, CurrentVersion)))
        {
            State = UpdateState.Failed;
            StatusText = $"Версия {version} установлена — запустите программу заново.";
            return false;
        }
        Log.Action($"Обновление: установлена версия {version}, перезапуск");
        return true;
    }

    private void RequireManual(UpdateManifest manifest, string reason)
    {
        Manifest = manifest;
        State = UpdateState.ManualRequired;
        StatusText = $"Версию {manifest.Version} не получилось установить автоматически ({reason}). Скачайте её со страницы релизов.";
        Log.Warn($"Обновление до {manifest.Version}: {reason} — нужно скачать вручную");
    }
}
```

- [ ] **Step 6: доступ из приложения** — в `AppServices.cs` рядом с остальными сервисами:

```csharp
    private static readonly Lazy<UpdateService> LazyUpdates =
        new(() => new UpdateService(UpdateEnvironment.ForCurrentProcess(), () => Settings));

    /// <summary>Created on first use, after App has set AppPaths.DataDir.</summary>
    public static UpdateService Updates => LazyUpdates.Value;
```

(и `using PcCleaner.Core.Updates;`).

- [ ] **Step 7: тесты проходят** — `--filter "FullyQualifiedName~PcCleaner.Tests.Updates"` → PASS.
- [ ] **Step 8: коммит** («Updates: service, settings and environment»).

---

### Task 6: Интерфейс — запуск, полоса, «Настройки»

**Files:**
- Create: `src/PcCleaner/Core/Updates/UpdateBanner.cs`
- Modify: `src/PcCleaner/App.xaml.cs`, `src/PcCleaner/MainViewModel.cs`, `src/PcCleaner/MainWindow.xaml:100-113`, `src/PcCleaner/Modules/Settings/SettingsViewModel.General.cs` (`LoadGeneral`), `src/PcCleaner/Modules/Settings/SettingsView.xaml` (вкладка «Основные», левая колонка после карточки «Временные файлы»)
- Create: `src/PcCleaner/Modules/Settings/SettingsViewModel.Updates.cs`
- Test: `tests/PcCleaner.Tests/Updates/UpdateBannerTests.cs`

**Interfaces:**
- Consumes: `UpdateService`, `UpdateState`, `UpdateManifest`, `AppServices.Updates`, `ProcessRunner.ShellOpen(string)`, `Dialogs.Confirm/Info`.
- Produces: `UpdateBanner(string Text, string Action)` с `static UpdateBanner? For(UpdateState state, UpdateManifest? manifest, Version current)` и `static string BusyWarning(IReadOnlyList<string> busyTitles)`; `MainViewModel.ShowUpdateBanner`, `UpdateBannerText`, `UpdateBannerAction`, `UpdateBannerCommand`, `DismissUpdateBannerCommand`; `SettingsViewModel.Updates`, `AutoUpdate`, `VersionLine`, `LastCheckText`, `CheckUpdatesCommand`; `App.UpdatedFrom`.

- [ ] **Step 1: падающие тесты** — `UpdateBannerTests.cs`:

```csharp
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

public class UpdateBannerTests
{
    private static readonly UpdateManifest M = UpdateManifest.Parse(
        UpdateTestKit.ManifestJson("1.2.0", 2_000_000, new string('a', 64)), out _)!;

    [Fact]
    public void Ready_OffersARestart()
    {
        var b = UpdateBanner.For(UpdateState.Ready, M, new Version(1, 1, 0))!;
        Assert.Contains("1.2.0", b.Text);
        Assert.Equal("Перезапустить сейчас", b.Action);
    }

    [Fact]
    public void Updated_OffersTheNotes()
    {
        var b = UpdateBanner.For(UpdateState.Updated, M, new Version(1, 2, 0))!;
        Assert.Contains("обновлена до версии 1.2.0", b.Text);
        Assert.Equal("Что нового", b.Action);
    }

    [Fact]
    public void ManualRequired_OffersTheDownloadPage()
        => Assert.Equal("Скачать вручную", UpdateBanner.For(UpdateState.ManualRequired, M, new Version(1, 1, 0))!.Action);

    [Theory]
    [InlineData(UpdateState.None)]
    [InlineData(UpdateState.Checking)]
    [InlineData(UpdateState.UpToDate)]
    [InlineData(UpdateState.Downloading)]
    [InlineData(UpdateState.Failed)]
    public void QuietStates_ShowNothing(UpdateState state)
        => Assert.Null(UpdateBanner.For(state, M, new Version(1, 1, 0)));

    [Fact]
    public void Updated_WithoutNotes_StillShowsTheVersion()
        => Assert.Contains("1.2.0", UpdateBanner.For(UpdateState.Updated, null, new Version(1, 2, 0))!.Text);

    [Fact]
    public void BusyWarning_NamesTheSections()
    {
        var text = UpdateBanner.BusyWarning(new[] { "Очистка", "Диски" });
        Assert.Contains("«Очистка»", text);
        Assert.Contains("«Диски»", text);
        Assert.Contains("Перезапустить", text);
    }
}
```

- [ ] **Step 2: не собирается** — `UpdateBanner` не найден.

- [ ] **Step 3: тексты** — `UpdateBanner.cs`:

```csharp
namespace PcCleaner.Core.Updates;

/// <summary>The strip above the page: what it says and what its button does (pure, unit-tested).</summary>
public sealed record UpdateBanner(string Text, string Action)
{
    public static UpdateBanner? For(UpdateState state, UpdateManifest? manifest, Version current) => state switch
    {
        UpdateState.Ready when manifest is not null => new(
            $"Скачана новая версия {manifest.Version} — она установится при следующем запуске программы.", "Перезапустить сейчас"),
        UpdateState.Updated => new(
            $"Программа обновлена до версии {(manifest?.Version ?? UpdateManifest.Normalize(current)).ToString(3)}.", "Что нового"),
        UpdateState.ManualRequired when manifest is not null => new(
            $"Версию {manifest.Version} не получилось установить автоматически.", "Скачать вручную"),
        _ => null,
    };

    public static string BusyWarning(IReadOnlyList<string> busyTitles)
        => (busyTitles.Count == 1 ? "Сейчас ещё не закончена работа в разделе " : "Сейчас ещё не закончена работа в разделах ") +
           string.Join(", ", busyTitles.Select(t => $"«{t}»")) + ".\n\n" +
           "При перезапуске эта работа прервётся: то, что уже сделано, сохранится, остальное придётся запустить заново.\n\n" +
           "Перезапустить программу и установить обновление сейчас?";
}
```

- [ ] **Step 4: запуск** — `App.xaml.cs`:
  - свойство `internal static string? UpdatedFrom { get; private set; }` рядом с остальными;
  - в `ParseArgs`: `case UpdateService.UpdatedFromArg when i + 1 < args.Length: UpdatedFrom = args[++i]; break;`
  - после `Log.Info($"Запуск …")` и до `var vm = new MainViewModel();`:

```csharp
        // Install an already downloaded update before any window appears; the new version starts and this one exits.
        if (ScreenshotDir is null && UpdatedFrom is null && AppServices.Updates.TryInstallStaged(e.Args))
        {
            Shutdown();
            return;
        }
```

  - после `window.Show();`:

```csharp
        if (ScreenshotDir is null)
        {
            _ = UpdatedFrom is not null
                ? AppServices.Updates.CompleteUpdateAsync(UpdatedFrom)
                : AppServices.Updates.CheckAsync(manual: false, CancellationToken.None);
        }
```

  (`using PcCleaner.Core.Updates;`).

- [ ] **Step 5: полоса в окне** — `MainViewModel.cs`: в конструкторе после `RestartAsAdminCommand`:

```csharp
        Updates = AppServices.Updates;
        Updates.PropertyChanged += (_, _) => OnUpdateBannerChanged();
        UpdateBannerCommand = new RelayCommand(RunUpdateBannerAction);
        DismissUpdateBannerCommand = new RelayCommand(() =>
        {
            _updateBannerDismissed = true;
            OnUpdateBannerChanged();
        });
```

и члены класса:

```csharp
    private bool _updateBannerDismissed;

    public UpdateService Updates { get; }

    private UpdateBanner? Banner => _updateBannerDismissed ? null : UpdateBanner.For(Updates.State, Updates.Manifest, Updates.CurrentVersion);

    public bool ShowUpdateBanner => Banner is not null;

    public string UpdateBannerText => Banner?.Text ?? "";

    public string UpdateBannerAction => Banner?.Action ?? "";

    public RelayCommand UpdateBannerCommand { get; }

    public RelayCommand DismissUpdateBannerCommand { get; }

    private void OnUpdateBannerChanged()
    {
        OnPropertyChanged(nameof(ShowUpdateBanner));
        OnPropertyChanged(nameof(UpdateBannerText));
        OnPropertyChanged(nameof(UpdateBannerAction));
    }

    private void RunUpdateBannerAction()
    {
        switch (Updates.State)
        {
            case UpdateState.Ready:
                var busy = Pages.Where(p => p.IsBusy).Select(p => p.Title).ToList();
                if (busy.Count > 0 && !Dialogs.Confirm("Установка обновления", UpdateBanner.BusyWarning(busy)))
                    return;
                if (Updates.InstallNow(Environment.GetCommandLineArgs().Skip(1).ToList()))
                    System.Windows.Application.Current.Shutdown();
                break;
            case UpdateState.Updated:
                Dialogs.Info($"Что нового в версии {Updates.CurrentVersion.ToString(3)}",
                    string.IsNullOrWhiteSpace(Updates.Manifest?.Notes) ? "Описание изменений есть на странице релиза." : Updates.Manifest!.Notes);
                break;
            case UpdateState.ManualRequired:
                ProcessRunner.ShellOpen(UpdateService.ReleasesUrl);
                break;
        }
    }
```

`MainWindow.xaml` — в верхнем `StackPanel` после `Border` с `OtherAccountWarning`:

```xml
                <!-- Update strip: downloaded / installed / needs a manual download (Core/Updates/UpdateBanner). -->
                <Border Margin="0,10,0,0" Padding="10,6" CornerRadius="6" Background="{StaticResource AccentSoftBrush}"
                        Visibility="{Binding ShowUpdateBanner, Converter={StaticResource BoolToVis}}">
                    <DockPanel>
                        <Button DockPanel.Dock="Right" Content="&#xE711;" FontFamily="Segoe MDL2 Assets" FontSize="10" Padding="8,5"
                                Margin="6,0,0,0" ToolTip="Скрыть" Command="{Binding DismissUpdateBannerCommand}" />
                        <Button DockPanel.Dock="Right" Content="{Binding UpdateBannerAction}" Padding="12,4" Margin="12,0,0,0"
                                Command="{Binding UpdateBannerCommand}" />
                        <TextBlock Style="{StaticResource Icon}" Text="&#xE895;" FontSize="14" Foreground="{StaticResource AccentBrush}"
                                   VerticalAlignment="Center" Margin="0,0,8,0" />
                        <TextBlock Text="{Binding UpdateBannerText}" TextWrapping="Wrap" FontSize="12.5" VerticalAlignment="Center" />
                    </DockPanel>
                </Border>
```

- [ ] **Step 6: «Настройки»** — `SettingsViewModel.Updates.cs`:

```csharp
using PcCleaner.Core;
using PcCleaner.Core.Updates;

namespace PcCleaner.Modules.Settings;

// Card «Обновления» on the «Основные» tab: version, last check, «Проверить сейчас», the AutoUpdate switch.
public sealed partial class SettingsViewModel
{
    private bool _autoUpdate = true;
    private AsyncRelayCommand? _checkUpdatesCommand;

    public UpdateService Updates => AppServices.Updates;

    public bool AutoUpdate
    {
        get => _autoUpdate;
        set
        {
            if (!Set(ref _autoUpdate, value) || _loadingGeneral)
                return;
            Settings.AutoUpdate = value;
            SaveSettings(value ? "Настройки: автоматическое обновление — включено" : "Настройки: автоматическое обновление — выключено");
        }
    }

    public string VersionLine => $"Установлена версия {AppInfo.Version}";

    public string LastCheckText => Settings.LastUpdateCheckUtc is { } t
        ? $"Последняя проверка: {t.ToLocalTime():dd.MM.yyyy HH:mm}"
        : "Обновления ещё не проверялись";

    public AsyncRelayCommand CheckUpdatesCommand => _checkUpdatesCommand ??= new AsyncRelayCommand(async () =>
    {
        await Updates.CheckAsync(manual: true, CancellationToken.None);
        OnPropertyChanged(nameof(LastCheckText));
    });
}
```

В `LoadGeneral()` внутри блока `_loadingGeneral = true … finally`: `AutoUpdate = Settings.AutoUpdate;` и `OnPropertyChanged(nameof(LastCheckText));`.

`SettingsView.xaml` — после карточки «Временные файлы» в левой колонке:

```xml
                        <!-- Updates -->
                        <Border Style="{StaticResource Card}" Margin="0,12,0,0">
                            <StackPanel>
                                <DockPanel Margin="0,0,0,12">
                                    <TextBlock Style="{StaticResource SectionIcon}" Text="&#xE895;" />
                                    <TextBlock Style="{StaticResource H3}" Text="Обновления" VerticalAlignment="Center" />
                                </DockPanel>
                                <CheckBox Style="{StaticResource ToggleSwitch}" IsChecked="{Binding AutoUpdate}">
                                    <TextBlock Text="Обновляться автоматически" TextWrapping="Wrap" />
                                </CheckBox>
                                <TextBlock Style="{StaticResource Muted}" Margin="50,8,0,0" TextWrapping="Wrap"
                                           Text="Новая версия скачивается в фоне, проверяется по цифровой подписи и ставится при следующем запуске программы." />
                                <TextBlock Margin="0,12,0,0" Text="{Binding VersionLine}" />
                                <TextBlock Style="{StaticResource Muted}" Margin="0,4,0,0" Text="{Binding LastCheckText}" />
                                <TextBlock Margin="0,6,0,0" TextWrapping="Wrap" Text="{Binding Updates.StatusText}"
                                           Visibility="{Binding Updates.StatusText, Converter={StaticResource HasValueToVis}}" />
                                <Button Margin="0,10,0,0" Padding="14,6" HorizontalAlignment="Left" Content="Проверить сейчас"
                                        Command="{Binding CheckUpdatesCommand}" />
                            </StackPanel>
                        </Border>
```

- [ ] **Step 7: проверка** — `dotnet build … --no-incremental`, все тесты; `--screenshots` на собранном exe (страница «Настройки»): карточка «Обновления» видна, полосы нет; временно (не коммитить) выставить в `MainViewModel` состояние `Ready` и снять скриншот главной — полоса читается, кнопки помещаются; вернуть.
- [ ] **Step 8: коммит** («Updates: startup install, banner and the Settings card»).

---

### Task 7: Выпуск через CI

**Files:**
- Create: `tools/make-update-manifest.ps1`, `.github/workflows/release.yml`
- Modify: `.github/workflows/ci.yml` (шаг пробного манифеста в `windows-app` после publish), `tests/PcCleaner.Tests/Updates/UpdateSignatureTests.cs`

**Interfaces:**
- Consumes: формат манифеста Task 1, подпись Task 2.
- Produces: `tools/make-update-manifest.ps1 -Version <x.y.z> -Exe <path> -NotesFile <md> -OutDir <dir> [-PublicKeyPem <pem>]`, ключ из `$env:UPDATE_SIGNING_KEY`; тест `ScriptManifest_IsAcceptedByTheApp` (запускается, когда задана `UPDATE_MANIFEST_DIR`).

- [ ] **Step 1: тест на вывод скрипта** — в `UpdateSignatureTests.cs`:

```csharp
    /// <summary>Runs when UPDATE_MANIFEST_DIR points to the output of tools/make-update-manifest.ps1 (CI dry run, release check).</summary>
    [ManifestDirFact]
    public void ScriptManifest_IsAcceptedByTheApp()
    {
        var dir = Environment.GetEnvironmentVariable(ManifestDirFact.Variable)!;
        var json = File.ReadAllBytes(Path.Combine(dir, "update.json"));
        var keyFile = Path.Combine(dir, "public.pem");
        var key = File.Exists(keyFile) ? File.ReadAllText(keyFile) : UpdateKeys.PublicKeyPem;

        Assert.True(UpdateSignature.Verify(json, File.ReadAllBytes(Path.Combine(dir, "update.json.sig")), key));
        var manifest = UpdateManifest.Parse(json, out var problem);
        Assert.True(manifest is not null, problem);
        Assert.False(string.IsNullOrWhiteSpace(manifest!.Notes));
    }

    private sealed class ManifestDirFactAttribute : FactAttribute
    {
        public const string Variable = "UPDATE_MANIFEST_DIR";

        public ManifestDirFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
                Skip = $"Нужна папка с update.json из tools/make-update-manifest.ps1 ({Variable}); её задают шаги CI.";
        }
    }
```

- [ ] **Step 2: скрипт** — `tools/make-update-manifest.ps1`:

```powershell
# Writes update.json and update.json.sig for a release (docs/superpowers/specs/2026-10-17-auto-update-design.md).
# The private key (PEM) comes from $env:UPDATE_SIGNING_KEY; the signature is checked against the public key before exit.
# Needs PowerShell 7 (.NET ECDsa PEM import).
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$NotesFile,
    [Parameter(Mandatory)] [string]$OutDir,
    [string]$PublicKeyPem
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required' }
if (-not $env:UPDATE_SIGNING_KEY) { throw 'UPDATE_SIGNING_KEY is not set' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Bad version: $Version" }
if (-not $PublicKeyPem) { $PublicKeyPem = Get-Content -Raw (Join-Path $PSScriptRoot 'update-signing-public.pem') }

$name = Split-Path $Exe -Leaf
$size = (Get-Item $Exe).Length
$manifest = [ordered]@{
    version   = $Version
    published = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    notes     = (Get-Content -Raw -Encoding utf8 $NotesFile).Trim()
    files     = [ordered]@{
        'win-x64' = [ordered]@{
            name   = $name
            url    = "https://github.com/WaterCa7/WaterCa7-s-pc-cleaner/releases/download/v$Version/$name"
            size   = $size
            sha256 = (Get-FileHash -Algorithm SHA256 $Exe).Hash.ToLowerInvariant()
        }
    }
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$json = [System.Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 5))
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'update.json'), $json)

$sha256 = [System.Security.Cryptography.HashAlgorithmName]::SHA256
$der = [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence
$key = [System.Security.Cryptography.ECDsa]::Create()
$key.ImportFromPem($env:UPDATE_SIGNING_KEY)
$signature = $key.SignData($json, $sha256, $der)
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'update.json.sig'), $signature)

$public = [System.Security.Cryptography.ECDsa]::Create()
$public.ImportFromPem($PublicKeyPem)
if (-not $public.VerifyData($json, $signature, $sha256, $der)) { throw 'The signature does not match the public key' }
"update.json: version $Version, $name, $size bytes"
```

- [ ] **Step 3: пробный манифест в CI** — в `ci.yml`, job `windows-app`, после `Publish single-file exe`:

```yaml
      - name: Update manifest dry run (throwaway key)
        shell: pwsh
        run: |
          $key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
          $env:UPDATE_SIGNING_KEY = $key.ExportECPrivateKeyPem()
          $public = $key.ExportSubjectPublicKeyInfoPem()
          ./tools/make-update-manifest.ps1 -Version 9.9.9 -Exe publish/WaterCa7sPcCleaner.exe -NotesFile docs/release-notes/1.1.0.md -OutDir dryrun -PublicKeyPem $public
          Set-Content dryrun/public.pem $public
          $env:UPDATE_MANIFEST_DIR = (Resolve-Path dryrun).Path
          dotnet test tests/PcCleaner.Tests -c Release --no-build --filter "FullyQualifiedName~ScriptManifest_IsAcceptedByTheApp"
```

- [ ] **Step 4: релиз по тегу** — `.github/workflows/release.yml`:

```yaml
name: Release
on:
  push:
    tags: ["v*"]
permissions:
  contents: write
jobs:
  release:
    name: Build, sign and publish
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "8.0.x"
      - name: Check version and notes
        shell: pwsh
        run: |
          $version = '${{ github.ref_name }}'.TrimStart('v')
          [xml]$project = Get-Content src/PcCleaner/PcCleaner.csproj
          $projectVersion = ($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
          if ($projectVersion -ne $version) { throw "Tag v$version does not match <Version>$projectVersion</Version>" }
          if (-not (Test-Path "docs/release-notes/$version.md")) { throw "docs/release-notes/$version.md is missing" }
          "VERSION=$version" >> $env:GITHUB_ENV
      - run: dotnet build WaterCa7sPcCleaner.sln -c Release
      - run: dotnet test WaterCa7sPcCleaner.sln -c Release --no-build
      - name: Publish single-file exe
        run: dotnet publish src/PcCleaner/PcCleaner.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
      - name: Sign update.json
        shell: pwsh
        env:
          UPDATE_SIGNING_KEY: ${{ secrets.UPDATE_SIGNING_KEY }}
        run: |
          ./tools/make-update-manifest.ps1 -Version $env:VERSION -Exe publish/WaterCa7sPcCleaner.exe -NotesFile "docs/release-notes/$env:VERSION.md" -OutDir publish
          $env:UPDATE_MANIFEST_DIR = (Resolve-Path publish).Path
          dotnet test tests/PcCleaner.Tests -c Release --no-build --filter "FullyQualifiedName~ScriptManifest_IsAcceptedByTheApp"
      - name: Create release
        shell: pwsh
        env:
          GH_TOKEN: ${{ github.token }}
        run: gh release create "v$env:VERSION" publish/WaterCa7sPcCleaner.exe publish/update.json publish/update.json.sig --title "WaterCa7s PC Cleaner $env:VERSION" --notes-file "docs/release-notes/$env:VERSION.md"
```

(Шаг «Sign» проверяет подпись вшитым ключом приложения: в `publish` нет `public.pem`.)

- [ ] **Step 5: заметки 1.1.0** — `docs/release-notes/1.1.0.md`:

```markdown
## Что нового в 1.1.0

- **Новая иконка** — капля с кошачьими ушками: в проводнике, на рабочем столе, в панели задач и в самой программе.
- **Автоматическое обновление.** Программа сама находит новую версию, скачивает её в фоне, проверяет цифровую подпись и ставит при следующем запуске. Выключить можно в «Настройки → Основные → Обновления».
- **Надёжнее резервные копии реестра:** если Windows не успевает записать копию с первого раза, программа повторяет попытку, а без копии по-прежнему ничего не меняет.

Если у вас версия 1.0.0, скачайте эту версию один раз вручную — дальше обновления придут сами.
```

- [ ] **Step 6: проверка** — `pwsh` есть локально? Если да: пробный прогон скрипта с временным ключом, как в Step 3. Иначе — проверка в CI. Все тесты (новый — Skipped с причиной).
- [ ] **Step 7: коммит** («Release workflow: signed update.json on every tag»); после отправки убедиться, что шаг пробного манифеста в CI зелёный.

---

### Task 8: Версия 1.1.0, документация, лицензия

**Files:**
- Modify: `src/PcCleaner/PcCleaner.csproj` (`<Version>1.1.0</Version>`), `README.md` (раздел «Безопасность» или «Установка»: пара строк про автообновление и где его выключить), `docs/ARCHITECTURE.md` (строка про `Core/Updates/`)
- Create: `LICENSE`

- [ ] **Step 1:** `LICENSE` — стандартный текст MIT, строка `Copyright (c) 2026 WaterCa7`.
- [ ] **Step 2:** версия, README, ARCHITECTURE; `git config user.email 149539786+WaterCa7@users.noreply.github.com` (только для этого репозитория).
- [ ] **Step 3:** сборка `--no-incremental`, все тесты, `--screenshots` → в подвале «v1.1.0».
- [ ] **Step 4:** коммит («1.1.0: MIT license, docs»).

---

### Task 9: Открытие репозитория и релиз (каждый шаг — с согласия пользователя)

- [ ] **Step 1:** все связанные сессии простаивают, рабочая копия чиста, всё отправлено (`git status`, `git log origin/master..main`).
- [ ] **Step 2:** резервная копия: `git bundle create <scratchpad>/before-email-rewrite.bundle --all`.
- [ ] **Step 3:** переписать почту:

```bash
git filter-branch --env-filter '
if [ "$GIT_AUTHOR_EMAIL" = "<old-address>" ]; then export GIT_AUTHOR_EMAIL="149539786+WaterCa7@users.noreply.github.com"; fi
if [ "$GIT_COMMITTER_EMAIL" = "<old-address>" ]; then export GIT_COMMITTER_EMAIL="149539786+WaterCa7@users.noreply.github.com"; fi
' --tag-name-filter cat -- --branches --tags
git log --all --format='%ae%n%ce' | sort | uniq -c
```

Ожидается: адреса `<old-address>` нет.
- [ ] **Step 4:** `git push --force origin main:master` и `git push --force origin v1.0.0`; `gh release view v1.0.0` — exe на месте; удалить `refs/original/*`.
- [ ] **Step 5:** секрет: `gh secret set UPDATE_SIGNING_KEY --repo WaterCa7/WaterCa7-s-pc-cleaner < "$USERPROFILE/WaterCa7sPcCleaner-signing/update-signing-key.pem"`; напомнить сохранить ключ в надёжном месте.
- [ ] **Step 6:** видимость: `gh repo edit WaterCa7/WaterCa7-s-pc-cleaner --visibility public --accept-visibility-change-consequences`.
- [ ] **Step 7:** релиз: `git tag v1.1.0 && git push origin v1.1.0`; дождаться workflow «Release»; проверить три файла релиза; скачать `update.json` и `.sig` в пустую папку и прогнать `ScriptManifest_IsAcceptedByTheApp` с `UPDATE_MANIFEST_DIR` (вшитый ключ); запустить скачанный exe с `--screenshots`.
