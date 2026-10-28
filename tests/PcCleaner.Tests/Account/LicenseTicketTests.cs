using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PcCleaner.Core.Account;
using Xunit;

namespace PcCleaner.Tests.Account;

public class LicenseTicketTests
{
    private static string ServerTestKey()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("license_test_pub.pem")!;
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public void AGoodTicket_IsRead()
    {
        using var key = new TestLicense();
        var t = key.Sign(TestLicense.Json(plan: "family", devicesMax: 3));

        var data = LicenseTicket.Verify(t.Ticket, t.Signature, key.Keys)!;

        Assert.Equal(1, data.V);
        Assert.Equal(TestLicense.Sub, data.Sub);
        Assert.Equal("family", data.Plan);
        Assert.Equal(3, data.DevicesMax);
        Assert.Equal(TestLicense.Hw, data.HwHash);
        Assert.Equal(TestLicense.DeviceId, data.DeviceId);
        Assert.Equal(TestLicense.Now.AddDays(-1), data.IssuedAt);
        Assert.Equal(TestLicense.Now.AddDays(13), data.ValidUntil);
        Assert.NotNull(data.ExpiresAt);
    }

    [Fact]
    public void AnyKeyOfTheList_MaySign_ButAForeignKeyMayNot()
    {
        using var key = new TestLicense();
        using var other = new TestLicense();
        var t = key.Sign(TestLicense.Json());

        Assert.NotNull(LicenseTicket.Verify(t.Ticket, t.Signature, new[] { other.PublicPem, key.PublicPem }));
        Assert.Null(LicenseTicket.Verify(t.Ticket, t.Signature, other.Keys));
        Assert.Null(LicenseTicket.Verify(t.Ticket, t.Signature, Array.Empty<string>()));
    }

    [Fact]
    public void AP1363Signature_IsRejected()
    {
        using var key = new TestLicense();
        var t = key.Sign(TestLicense.Json(), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.Null(LicenseTicket.Verify(t.Ticket, t.Signature, key.Keys));
    }

    [Fact]
    public void AChangedByte_IsRejected()
    {
        using var key = new TestLicense();
        var t = key.Sign(TestLicense.Json(plan: "free"));
        var bytes = Convert.FromBase64String(t.Ticket);
        var text = Encoding.UTF8.GetString(bytes).Replace("\"free\"", "\"pro_\"");

        Assert.Null(LicenseTicket.Verify(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), t.Signature, key.Keys));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("")]
    [InlineData(null)]
    public void GarbageIsRejected(string? ticket)
    {
        using var key = new TestLicense();
        var t = key.Sign(TestLicense.Json());

        Assert.Null(LicenseTicket.Verify(ticket, t.Signature, key.Keys));
        Assert.Null(LicenseTicket.Verify(t.Ticket, ticket, key.Keys));
    }

    [Fact]
    public void AnotherVersion_IsRejected_EvenWhenSigned()
    {
        using var key = new TestLicense();
        var t = key.Sign(TestLicense.Json(v: 2));

        Assert.Null(LicenseTicket.Verify(t.Ticket, t.Signature, key.Keys));
    }

    [Fact]
    public void ASignedTicketWithoutAField_IsRejected()
    {
        using var key = new TestLicense();
        var t = key.Sign("{\"v\":1,\"sub\":7,\"plan\":\"pro\"}");

        Assert.Null(LicenseTicket.Verify(t.Ticket, t.Signature, key.Keys));
    }

    [Fact]
    public void ALifetimeTicket_HasNoEnd()
    {
        using var key = new TestLicense();
        var t = key.Sign(TestLicense.Json(kind: "lifetime", noExpiry: true));

        Assert.Null(LicenseTicket.Verify(t.Ticket, t.Signature, key.Keys)!.ExpiresAt);
    }

    [Fact]
    public void TheBuild_HasALiveP256Key()
    {
        Assert.NotEmpty(LicenseKeys.Live);
        Assert.All(LicenseKeys.Live, k => Assert.Matches("^[0-9a-f]{64}$", LicenseKeys.Fingerprint(k)));
    }

    [Fact]
    public void TheLiveKeys_NeverContainTheServersTestKey()
    {
        var test = LicenseKeys.Fingerprint(ServerTestKey());

        Assert.DoesNotContain(LicenseKeys.Live, k => LicenseKeys.Fingerprint(k) == test);
    }

    [Fact]
    public void ATicketSignedWithTheServersTestKey_IsNotPro_InTheProgram()
    {
        // The server signs test tickets with license_test_key.pem; the program only trusts LicenseKeys.Live.
        Assert.DoesNotContain(ServerTestKey().Trim(), LicenseKeys.Live.Select(k => k.Trim()));
    }

    [Fact]
    public void Load_ReadsEveryPemBlock_AndNothingWithoutAResource()
    {
        using var a = new TestLicense();
        using var b = new TestLicense();
        var pem = a.PublicPem + "\n" + b.PublicPem;

        var keys = LicenseKeys.Load(new MemoryStream(Encoding.ASCII.GetBytes(pem)));

        Assert.Equal(new[] { LicenseKeys.Fingerprint(a.PublicPem), LicenseKeys.Fingerprint(b.PublicPem) }, keys.Select(LicenseKeys.Fingerprint));
        Assert.Empty(LicenseKeys.Load(null));
    }

    [Fact]
    public void Fingerprint_IsTheSameForTheSameKey_InAnyLineEndings()
    {
        using var key = new TestLicense();

        Assert.Equal(LicenseKeys.Fingerprint(key.PublicPem), LicenseKeys.Fingerprint(key.PublicPem.Replace("\n", "\r\n")));
        Assert.Matches("^[0-9a-f]{64}$", LicenseKeys.Fingerprint(key.PublicPem));
    }
}
