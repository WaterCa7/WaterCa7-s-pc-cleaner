using System.IO;
using System.Text;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

public class UpdateSignatureTests
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("{\"version\":\"1.2.0\"}");

    [Fact]
    public void Verify_AcceptsTheSignatureOfTheSameBytes()
    {
        using var kit = new UpdateTestKit();
        Assert.True(UpdateSignature.Verify(Data, kit.Sign(Data), kit.PublicKeyPem));
    }

    [Fact]
    public void Verify_RejectsChangedBytesAnotherKeyAndGarbage()
    {
        using var kit = new UpdateTestKit();
        using var other = new UpdateTestKit();
        var signature = kit.Sign(Data);
        var changed = Encoding.UTF8.GetBytes("{\"version\":\"9.9.9\"}");

        Assert.False(UpdateSignature.Verify(changed, signature, kit.PublicKeyPem));
        Assert.False(UpdateSignature.Verify(Data, signature, other.PublicKeyPem));
        Assert.False(UpdateSignature.Verify(Data, new byte[] { 1, 2, 3 }, kit.PublicKeyPem));
        Assert.False(UpdateSignature.Verify(Data, signature, "not a key"));
        Assert.False(UpdateSignature.Verify(Data, Array.Empty<byte>(), kit.PublicKeyPem));
    }

    [Fact]
    public void EmbeddedKey_IsTheOneInTools()
    {
        using var stream = typeof(UpdateSignatureTests).Assembly.GetManifestResourceStream("update-signing-public.pem");
        Assert.NotNull(stream);
        var file = new StreamReader(stream!).ReadToEnd();
        Assert.Equal(Normalize(file), Normalize(UpdateKeys.PublicKeyPem));
        Assert.DoesNotContain("PRIVATE", UpdateKeys.PublicKeyPem);
    }

    private static string Normalize(string pem) => string.Join("\n", pem.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
}
