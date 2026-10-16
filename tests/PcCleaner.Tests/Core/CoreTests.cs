using System.IO;
using PcCleaner.Core;
using Xunit;

namespace PcCleaner.Tests.Core;

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(1023, "1023 Б")]
    [InlineData(1536, "1,5 КБ")]
    [InlineData(10L * 1024 * 1024, "10 МБ")]
    [InlineData(3L * 1024 * 1024 * 1024 / 2, "1,5 ГБ")]
    public void Bytes_FormatsInRussianUnits(long bytes, string expected)
        => Assert.Equal(expected, Format.Bytes(bytes));

    [Theory]
    [InlineData(1, "файл")]
    [InlineData(2, "файла")]
    [InlineData(5, "файлов")]
    [InlineData(11, "файлов")]
    [InlineData(21, "файл")]
    [InlineData(112, "файлов")]
    [InlineData(1004, "файла")]
    public void Plural_UsesRussianRules(long n, string expected)
        => Assert.Equal(expected, Format.Plural(n, "файл", "файла", "файлов"));
}

public class SuspicionTests
{
    public SuspicionTests() => TestEnvironment.Init();

    [Fact]
    public void SystemNameOutsideWindows_IsHighRisk()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "svchost.exe");
        var result = Suspicion.Analyze(path, new SignatureInfo(SignatureStatus.Unsigned, null));
        Assert.Equal(SuspicionLevel.High, result.Level);
    }

    [Fact]
    public void MicrosoftSignedSystemFile_IsClean()
    {
        var path = Path.Combine(Environment.SystemDirectory, "svchost.exe");
        var result = Suspicion.Analyze(path, new SignatureInfo(SignatureStatus.Valid, "Microsoft Windows"));
        Assert.Equal(SuspicionLevel.None, result.Level);
    }

    [Fact]
    public void UnsignedExeInTemp_IsSuspicious()
    {
        var path = Path.Combine(Path.GetTempPath(), "updater.exe");
        var result = Suspicion.Analyze(path, new SignatureInfo(SignatureStatus.Unsigned, null));
        Assert.True(result.IsSuspicious);
    }

    [Fact]
    public void HiddenEncodedPowerShell_IsFlagged()
    {
        var result = Suspicion.Analyze(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            new SignatureInfo(SignatureStatus.Valid, "Microsoft Windows"),
            "powershell.exe -w hidden -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQAIABOAGUAdAA=");
        Assert.True(result.IsSuspicious);
    }

    [Fact]
    public void DoubleExtension_IsFlagged()
    {
        var result = Suspicion.Analyze(@"D:\docs\invoice.pdf.exe", new SignatureInfo(SignatureStatus.Unsigned, null));
        Assert.True(result.IsSuspicious);
    }

    [Theory]
    [InlineData("a8f3k2x9q1zz", true)]
    [InlineData("3f9a0c1b2d4e5f", true)]
    [InlineData("chrome", false)]
    [InlineData("SteamService", false)]
    [InlineData("OneDriveSetup", false)]
    public void LooksRandom_Heuristic(string name, bool expected) => Assert.Equal(expected, Suspicion.LooksRandom(name));

    [Theory]
    [InlineData("svch0st.exe", "svchost.exe")]
    [InlineData("scvhost.exe", "svchost.exe")]
    [InlineData("lsasss.exe", "lsass.exe")]
    [InlineData("expl0rer.exe", "explorer.exe")]
    [InlineData("svchost.exe", null)]
    [InlineData("iexplore.exe", null)]
    [InlineData("chrome.exe", null)]
    [InlineData("steam.exe", null)]
    [InlineData("taskhost.exe", null)]
    public void LookalikeOf_DetectsTyposquatting(string name, string? expected)
        => Assert.Equal(expected, Suspicion.LookalikeOf(name));

    [Fact]
    public void Lookalike_InAppData_IsHighRisk()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "svch0st.exe");
        Assert.Equal(SuspicionLevel.High, Suspicion.Analyze(path, new SignatureInfo(SignatureStatus.Unsigned, null)).Level);
    }

    [Fact]
    public void Miner_IsSuspiciousEvenWhenSigned()
    {
        var r = Suspicion.Analyze(@"C:\Tools\xmrig.exe", new SignatureInfo(SignatureStatus.Valid, "Some Vendor"));
        Assert.True(r.IsSuspicious);
    }

    [Fact]
    public void SignedRemoteAccessTool_IsOnlyANotice()
    {
        var r = Suspicion.Analyze(@"C:\Program Files (x86)\AnyDesk\AnyDesk.exe", new SignatureInfo(SignatureStatus.Valid, "philandro Software GmbH"));
        Assert.Equal(SuspicionLevel.Low, r.Level);
        Assert.Contains(r.Reasons, x => x.Contains("AnyDesk"));
    }

    [Fact]
    public void KnownPup_IsMatched()
    {
        Assert.NotNull(KnownPups.Match("Amigo", null));
        Assert.Null(KnownPups.Match("Google Chrome", "Google LLC"));
    }
}

public class FileSignatureTests
{
    [Fact]
    public void WindowsBinary_IsValidMicrosoftSignature()
    {
        var info = FileSignature.Get(Path.Combine(Environment.SystemDirectory, "svchost.exe"));
        Assert.Equal(SignatureStatus.Valid, info.Status);
        Assert.True(info.IsMicrosoft, $"Publisher was '{info.Publisher}'");
    }

    [Fact]
    public void CatalogSignedBinary_IsValid()
    {
        // notepad.exe in System32 is catalog-signed on most Windows builds; cmd.exe as fallback.
        var path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "..", "..", "cmd.exe");
        var info = FileSignature.Get(Path.GetFullPath(path));
        Assert.Equal(SignatureStatus.Valid, info.Status);
    }

    [Fact]
    public void PlainTextFile_IsUnsigned()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, "hello");
        try
        {
            Assert.Equal(SignatureStatus.Unsigned, FileSignature.Get(file).Status);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void MissingFile_IsNotFound()
        => Assert.Equal(SignatureStatus.NotFound, FileSignature.Get(@"C:\definitely\missing\file.exe").Status);
}
