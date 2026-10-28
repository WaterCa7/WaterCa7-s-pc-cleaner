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
