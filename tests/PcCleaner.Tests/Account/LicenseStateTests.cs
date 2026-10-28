using System.Text;
using PcCleaner.Core.Account;
using Xunit;

namespace PcCleaner.Tests.Account;

public class LicenseStateTests
{
    private static readonly DateTimeOffset Now = TestLicense.Now;

    private static LicenseTicketData Ticket(string json) => LicenseTicket.Parse(Encoding.UTF8.GetBytes(json))!;

    private static LicenseStatus Evaluate(string json, DateTimeOffset? now = null, string hw = TestLicense.Hw, long? device = TestLicense.DeviceId,
        long? account = TestLicense.Sub)
        => LicenseState.Evaluate(Ticket(json), true, hw, device, account, now ?? Now);

    [Fact]
    public void NotSignedIn_IsAGuest_WhateverTheTicket()
    {
        var status = LicenseState.Evaluate(Ticket(TestLicense.Json()), false, TestLicense.Hw, TestLicense.DeviceId, TestLicense.Sub, Now);

        Assert.Equal(Tier.Guest, status.Tier);
        Assert.Equal(LicenseReason.NotSignedIn, status.Reason);
    }

    [Fact]
    public void SignedInWithoutATicket_IsFree()
    {
        var status = LicenseState.Evaluate(null, true, TestLicense.Hw, TestLicense.DeviceId, TestLicense.Sub, Now);

        Assert.Equal(Tier.Free, status.Tier);
        Assert.Equal(LicenseReason.NoTicket, status.Reason);
    }

    [Theory]
    [InlineData("pro")]
    [InlineData("family")]
    [InlineData("custom")]
    public void APaidPlan_WithAFreshTicketOfThisComputer_IsPro(string plan)
    {
        var status = Evaluate(TestLicense.Json(plan: plan));

        Assert.Equal(Tier.Pro, status.Tier);
        Assert.Equal(LicenseReason.None, status.Reason);
        Assert.Equal(plan, status.Plan);
        Assert.Equal(13, status.OfflineDaysLeft);
    }

    [Fact]
    public void TheFreePlan_IsFree_ButKeepsItsDates()
    {
        var status = Evaluate(TestLicense.Json(plan: "free", kind: "none", noExpiry: true, devicesMax: 0));

        Assert.Equal(Tier.Free, status.Tier);
        Assert.Equal(LicenseReason.NoPlan, status.Reason);
        Assert.Equal("free", status.Plan);
        Assert.NotNull(status.ValidUntil);
        Assert.Null(status.OfflineDaysLeft);
    }

    [Fact]
    public void AnotherComputersTicket_IsFree()
    {
        Assert.Equal(LicenseReason.WrongDevice, Evaluate(TestLicense.Json(), hw: new string('f', 64)).Reason);
        Assert.Equal(LicenseReason.WrongDevice, Evaluate(TestLicense.Json(), device: 43).Reason);
        Assert.Equal(LicenseReason.WrongDevice, Evaluate(TestLicense.Json(), device: null).Reason);
        Assert.Equal(LicenseReason.WrongDevice, Evaluate(TestLicense.Json(), account: 8).Reason);
    }

    [Fact]
    public void TheHwHash_IsComparedIgnoringCase()
        => Assert.Equal(Tier.Pro, Evaluate(TestLicense.Json(hw: TestLicense.Hw.ToUpperInvariant())).Tier);

    [Fact]
    public void ClockTurnedBack_IsFree()
    {
        var status = Evaluate(TestLicense.Json(issued: Now.AddMinutes(5)));

        Assert.Equal(Tier.Free, status.Tier);
        Assert.Equal(LicenseReason.ClockBack, status.Reason);
    }

    [Fact]
    public void AStaleTicket_IsFree_FromTheMomentOfValidUntil()
    {
        var json = TestLicense.Json(issued: Now.AddDays(-14), validUntil: Now);

        Assert.Equal(LicenseReason.Stale, Evaluate(json).Reason);
        Assert.Equal(Tier.Pro, Evaluate(json, Now.AddSeconds(-1)).Tier);
    }

    [Fact]
    public void AnEndedSubscription_IsFree_FromTheMomentOfExpiresAt()
    {
        var json = TestLicense.Json(expires: Now);

        Assert.Equal(LicenseReason.Expired, Evaluate(json).Reason);
        Assert.Equal(Tier.Pro, Evaluate(json, Now.AddSeconds(-1)).Tier);
    }

    [Fact]
    public void ALifetimeLicense_NeverExpires_ButStillNeedsAFreshTicket()
    {
        var json = TestLicense.Json(kind: "lifetime", noExpiry: true);

        Assert.Equal(Tier.Pro, Evaluate(json).Tier);
        Assert.Equal(LicenseReason.Stale, Evaluate(json, Now.AddDays(30)).Reason);
    }

    [Fact]
    public void OfflineDays_AreRoundedUp()
    {
        var json = TestLicense.Json(issued: Now.AddDays(-13).AddHours(-1), validUntil: Now.AddHours(23));

        Assert.Equal(1, Evaluate(json).OfflineDaysLeft);
    }
}
