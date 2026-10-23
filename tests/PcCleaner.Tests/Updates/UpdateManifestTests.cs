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
        Assert.Equal(UpdateEnvironment.SetupName, m.Download.Name);
        Assert.Equal(UpdateTestKit.Url("1.2.0"), m.Download.Url);
        Assert.Equal(66_617_845, m.Download.Size);
        Assert.Equal(Sha, m.Download.Sha256);
    }

    [Theory]
    [InlineData("https://github.com/Evil/WaterCa7-s-pc-cleaner/releases/download/v1.2.0/WaterCa7sPcCleaner-Setup.exe")]
    [InlineData("https://github.com/WaterCa7/WaterCa7-s-pc-cleaner/releases/download/v1.1.0/WaterCa7sPcCleaner-Setup.exe")]
    [InlineData("http://github.com/WaterCa7/WaterCa7-s-pc-cleaner/releases/download/v1.2.0/WaterCa7sPcCleaner-Setup.exe")]
    [InlineData("https://example.com/WaterCa7sPcCleaner-Setup.exe")]
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
        Assert.Contains("win-x64-setup", p2);
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
