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
            try
            {
                File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the next download overwrites it
            }
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
