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
