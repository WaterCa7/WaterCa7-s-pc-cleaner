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
