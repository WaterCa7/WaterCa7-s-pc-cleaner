using PcCleaner.Core.Account;
using PcCleaner.Modules.Profile;
using PcCleaner.Tests.Account;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class ProfileTextsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    public ProfileTextsTests() => TestEnvironment.Init();

    private static LicenseStatus Pro(DateTimeOffset? expires, DateTimeOffset? issued = null, string plan = "pro", string kind = "subscription")
        => new(Tier.Pro, LicenseReason.None, plan, kind, 1, expires, issued ?? Now.AddHours(-1), Now.AddDays(13), 13);

    [Theory]
    [InlineData("free", "Free")]
    [InlineData("pro", "Pro")]
    [InlineData("family", "Pro Семья")]
    [InlineData("custom", "Особый")]
    [InlineData(null, "Free")]
    public void ThePlan_HasItsName(string? plan, string name) => Assert.Equal(name, ProfileTexts.PlanName(plan));

    [Fact]
    public void TheTerm_IsADate_OrForever_OrNothingForFree()
    {
        Assert.Equal("до 24.10.2027", ProfileTexts.Term(Pro(new DateTimeOffset(2027, 10, 24, 10, 0, 0, TimeSpan.Zero))));
        Assert.Equal("навсегда", ProfileTexts.Term(Pro(null, kind: "lifetime")));
        Assert.Null(ProfileTexts.Term(new LicenseStatus(Tier.Free, LicenseReason.NoPlan, "free", "none", 0, null, Now, Now.AddDays(13), null)));
    }

    [Fact]
    public void TheLastCheck_IsTheTicketTime_OrNothingWithoutATicket()
    {
        Assert.StartsWith("проверено ", ProfileTexts.Checked(Pro(null), Now));
        Assert.Null(ProfileTexts.Checked(new LicenseStatus(Tier.Guest, LicenseReason.NotSignedIn, null, null, 0, null, null, null, null), Now));
    }

    [Theory]
    [InlineData(LicenseReason.Stale, "Обновить")]
    [InlineData(LicenseReason.ClockBack, "Часы")]
    [InlineData(LicenseReason.WrongDevice, "другому компьютеру")]
    [InlineData(LicenseReason.Expired, "закончилась")]
    public void TheNote_ExplainsAFreeTicket(LicenseReason reason, string part)
    {
        var s = new LicenseStatus(Tier.Free, reason, "pro", "subscription", 1, Now.AddDays(-1), Now.AddDays(-15), Now.AddDays(-1), null);

        Assert.Contains(part, ProfileTexts.Note(s, Now));
    }

    [Fact]
    public void AnOldButValidProTicket_CountsTheOfflineDays()
    {
        Assert.Null(ProfileTexts.Note(Pro(Now.AddDays(100)), Now));
        Assert.Contains("13", ProfileTexts.Note(Pro(Now.AddDays(100), issued: Now.AddDays(-2)), Now));
    }

    [Fact]
    public void AComputer_HasItsSystemDateAndLastSeen()
    {
        var d = new DeviceEntry(5, "Office", "mac", new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), null, false);

        Assert.Equal("Mac · привязан 01.10.2026", ProfileTexts.DeviceDetails(d, Now));
        Assert.StartsWith("Windows · привязан 01.10.2026 · в сети", ProfileTexts.DeviceDetails(d with { Platform = "windows", LastSeenAt = Now.AddHours(-3) }, Now));
    }
}
