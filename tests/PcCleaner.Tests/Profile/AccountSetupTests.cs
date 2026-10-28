using PcCleaner.Core.Account;
using PcCleaner.Core.AccountWindows;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class AccountSetupTests
{
    [Fact]
    public void TheTestRun_UsesALocalGuest_NotTheRealAccount()
    {
        TestEnvironment.Init();

        // The screenshot account's fixed computer id: the real one would read WMI and account.bin (DPAPI).
        Assert.Equal(HwHash.Of("screenshot"), PcCleaner.Core.AppServices.Account.Device.HwHash);
        Assert.Equal(Tier.Guest, PcCleaner.Core.AppServices.Account.Tier);
    }

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
    public async Task ARefresh_OfAScreenshotAccount_KeepsTheTier()
    {
        var account = AccountSetup.ForScreenshots(Tier.Pro);

        Assert.False(await account.RefreshTicketAsync(CancellationToken.None));
        Assert.Equal(Tier.Pro, account.Tier);
    }
}
