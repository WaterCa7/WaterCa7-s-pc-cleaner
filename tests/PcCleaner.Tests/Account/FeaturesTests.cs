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
