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
