using PcCleaner.Core;
using Xunit;

namespace PcCleaner.Tests.Core;

public class ShellOpenLogTests
{
    [Theory]
    [InlineData("https://waterca7.ru/auth/device?code=ABCD-1234", "https://waterca7.ru")]
    [InlineData("https://pay.example.com/checkout/one-time-token-123?x=1#frag", "https://pay.example.com")]
    [InlineData("http://user:secret@example.com:8080/path", "http://example.com:8080")]
    public void AWebAddress_IsLoggedWithoutItsPathOrQuery(string target, string logged)
    {
        var text = ProcessRunner.LogTarget(target);

        Assert.Equal(logged, text);
    }

    [Theory]
    [InlineData(@"C:\Users\Test\report.html")]
    [InlineData("ms-settings:network-status")]
    [InlineData("shell:RecycleBinFolder")]
    public void ALocalTarget_StaysAsItIs(string target) => Assert.Equal(target, ProcessRunner.LogTarget(target));
}
