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
