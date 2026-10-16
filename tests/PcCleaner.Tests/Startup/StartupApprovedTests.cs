using PcCleaner.Modules.Startup;
using Xunit;

namespace PcCleaner.Tests.Startup;

public class StartupApprovedTests
{
    [Fact]
    public void MissingOrEmptyValue_MeansEnabled()
    {
        Assert.True(StartupApproved.IsEnabled(null));
        Assert.True(StartupApproved.IsEnabled(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData((byte)0x02, true)]
    [InlineData((byte)0x06, true)]
    [InlineData((byte)0x03, false)]
    [InlineData((byte)0x07, false)]
    [InlineData((byte)0x00, true)]
    [InlineData((byte)0x01, false)]
    public void FirstByteLowBit_DecidesState(byte flag, bool enabled)
    {
        var data = new byte[12];
        data[0] = flag;
        Assert.Equal(enabled, StartupApproved.IsEnabled(data));
    }

    [Fact]
    public void DisabledWithZeroTime_HasNoDate()
    {
        var data = new byte[12];
        data[0] = 0x03;
        Assert.False(StartupApproved.IsEnabled(data));
        Assert.Null(StartupApproved.DisabledAt(data));
    }

    [Fact]
    public void RealTaskManagerValue_IsParsed()
    {
        // Value written by Task Manager: 03 00 00 00 + FILETIME
        var data = new byte[] { 0x03, 0, 0, 0, 0xFE, 0x6C, 0xE6, 0x44, 0x3A, 0x2E, 0xD7, 0x01 };
        var when = StartupApproved.DisabledAt(data);
        Assert.NotNull(when);
        Assert.InRange(when!.Value.Year, 2020, 2022);
    }

    [Fact]
    public void CreateDisabled_RoundTripsTheTime()
    {
        var now = new DateTime(2026, 3, 14, 12, 30, 45, DateTimeKind.Utc);
        var data = StartupApproved.CreateDisabled(now);
        Assert.Equal(StartupApproved.Length, data.Length);
        Assert.Equal(0x03, data[0]);
        Assert.Equal(0, data[1] | data[2] | data[3]);
        Assert.False(StartupApproved.IsEnabled(data));
        Assert.Equal(now.ToLocalTime(), StartupApproved.DisabledAt(data));
    }

    [Fact]
    public void CreateEnabled_IsTwoFollowedByZeros()
    {
        var data = StartupApproved.CreateEnabled();
        Assert.Equal(StartupApproved.Length, data.Length);
        Assert.Equal(0x02, data[0]);
        Assert.All(data.Skip(1), b => Assert.Equal(0, b));
        Assert.True(StartupApproved.IsEnabled(data));
        Assert.Null(StartupApproved.DisabledAt(data));
    }

    [Fact]
    public void EnabledValueWithTime_HasNoDisabledDate()
    {
        var data = StartupApproved.CreateDisabled(DateTime.UtcNow);
        data[0] = 0x02;
        Assert.Null(StartupApproved.DisabledAt(data));
    }
}
