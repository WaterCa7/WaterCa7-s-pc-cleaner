using PcCleaner.Modules.Programs;
using Xunit;

namespace PcCleaner.Tests.Programs;

public class StoreAppCatalogTests
{
    public StoreAppCatalogTests() => TestEnvironment.Init();

    [Theory]
    [InlineData("Microsoft.BingNews")]
    [InlineData("Microsoft.BingWeather")]
    [InlineData("Microsoft.GetHelp")]
    [InlineData("Microsoft.Getstarted")]
    [InlineData("Microsoft.MicrosoftSolitaireCollection")]
    [InlineData("Microsoft.People")]
    [InlineData("Microsoft.WindowsFeedbackHub")]
    [InlineData("Microsoft.ZuneMusic")]
    [InlineData("Microsoft.ZuneVideo")]
    [InlineData("Microsoft.MixedReality.Portal")]
    [InlineData("Microsoft.SkypeApp")]
    [InlineData("Microsoft.Todos")]
    [InlineData("Microsoft.PowerAutomateDesktop")]
    [InlineData("Clipchamp.Clipchamp")]
    [InlineData("Microsoft.549981C3F5F10")]
    [InlineData("king.com.CandyCrushSaga")]
    [InlineData("king.com.BubbleWitch3Saga")]
    [InlineData("Disney.37853FC22B2CE")]
    [InlineData("BytedancePte.Ltd.TikTok")]
    [InlineData("Facebook.Facebook")]
    [InlineData("Facebook.InstagramBeta")]
    [InlineData("SpotifyAB.SpotifyMusic")]
    [InlineData("Microsoft.MicrosoftOfficeHub")]
    [InlineData("microsoft.bingnews")]
    public void Classify_PreinstalledBloatware(string name)
        => Assert.Equal(StoreAppKind.Bloatware, StoreAppCatalog.Classify(name));

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.Windows.Photos")]
    [InlineData("Microsoft.WindowsCalculator")]
    [InlineData("Microsoft.WindowsNotepad")]
    [InlineData("Microsoft.WindowsTerminal")]
    [InlineData("Microsoft.SecHealthUI")]
    [InlineData("Microsoft.VCLibs.140.00")]
    [InlineData("Microsoft.VCLibs.140.00.UWPDesktop")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.NET.Native.Framework.2.2")]
    [InlineData("Microsoft.Xbox.TCUI")]
    [InlineData("Microsoft.XboxIdentityProvider")]
    [InlineData("Microsoft.XboxGameOverlay")]
    [InlineData("Microsoft.GamingApp")]
    [InlineData("Microsoft.Paint")]
    [InlineData("Microsoft.ScreenSketch")]
    [InlineData("Microsoft.HEVCVideoExtension")]
    [InlineData("Microsoft.WebpImageExtension")]
    [InlineData("Microsoft.LanguageExperiencePackru-RU")]
    [InlineData("NVIDIACorp.NVIDIAControlPanel")]
    public void Classify_ProtectedApps(string name)
    {
        Assert.Equal(StoreAppKind.Protected, StoreAppCatalog.Classify(name));
        Assert.False(string.IsNullOrWhiteSpace(StoreAppCatalog.ProtectReason(name)));
    }

    [Theory]
    [InlineData("Microsoft.OutlookForWindows")]
    [InlineData("Microsoft.YourPhone")]
    [InlineData("TelegramMessengerLLP.TelegramDesktop")]
    [InlineData("SomeVendor.UnknownApp")]
    [InlineData("")]
    [InlineData(null)]
    public void Classify_RegularApps(string? name)
        => Assert.Equal(StoreAppKind.Regular, StoreAppCatalog.Classify(name));

    [Fact]
    public void FriendlyNames_AreRussian()
    {
        Assert.Equal("Погода", StoreAppCatalog.FriendlyName("Microsoft.BingWeather"));
        Assert.Equal("Калькулятор", StoreAppCatalog.FriendlyName("Microsoft.WindowsCalculator"));
        Assert.Equal("Cortana", StoreAppCatalog.FriendlyName("Microsoft.549981C3F5F10"));
        Assert.Null(StoreAppCatalog.FriendlyName("SomeVendor.UnknownApp"));
    }

    [Theory]
    [InlineData("TelegramMessengerLLP.TelegramDesktop", "Telegram Desktop")]
    [InlineData("Microsoft.BingWeather", "Bing Weather")]
    [InlineData("Claude", "Claude")]
    [InlineData("Microsoft.549981C3F5F10", "Microsoft.549981C3F5F10")]
    [InlineData("OpenAI.Codex", "Codex")]
    public void DeriveName_SplitsCamelCase(string name, string expected)
        => Assert.Equal(expected, StoreAppCatalog.DeriveName(name));

    [Theory]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", "Microsoft Corporation")]
    [InlineData("CN=5E8FC25E-D1B4-4E8D-8B3E-6A3B2B1A8F6C", null)]
    [InlineData("CN=Some Dev", "Some Dev")]
    [InlineData("CN=\"Acme, Inc.\", O=\"Acme, Inc.\", C=US", "Acme, Inc.")]
    [InlineData(null, null)]
    public void PublisherFromDistinguishedName(string? dn, string? expected)
        => Assert.Equal(expected, StoreAppCatalog.PublisherFromDistinguishedName(dn));

    [Fact]
    public void ParseJson_Array()
    {
        const string json = """
            [{"Name":"Microsoft.BingNews","PackageFullName":"Microsoft.BingNews_4.1.0.0_x64__8wekyb3d8bbwe","PackageFamilyName":"Microsoft.BingNews_8wekyb3d8bbwe",
              "Publisher":"CN=Microsoft Corporation, O=Microsoft Corporation","Version":"4.1.0.0","InstallLocation":"C:\\Program Files\\WindowsApps\\X","SignatureKind":"Store"},
             {"Name":"Claude","PackageFullName":"Claude_2.4.0.0_x64__pzs8sxrjxfjjc","Publisher":"CN=Anthropic","Version":"2.4.0.0","InstallLocation":null,"SignatureKind":"Developer"}]
            """;
        var list = StoreAppCatalog.ParseJson(json);
        Assert.Equal(2, list.Count);
        Assert.Equal("Microsoft.BingNews", list[0].Name);
        Assert.Equal("4.1.0.0", list[0].Version);
        Assert.Equal(@"C:\Program Files\WindowsApps\X", list[0].InstallLocation);
        Assert.Equal("Store", list[0].SignatureKind);
        Assert.Null(list[1].InstallLocation);
        Assert.Null(list[1].PackageFamilyName);
    }

    [Fact]
    public void ParseJson_SingleObject()
    {
        const string json = """{"Name":"Microsoft.ZuneVideo","PackageFullName":"Microsoft.ZuneVideo_10.1_x64__8wekyb3d8bbwe","Version":"10.1"}""";
        var list = StoreAppCatalog.ParseJson(json);
        Assert.Single(list);
        Assert.Equal("Microsoft.ZuneVideo", list[0].Name);
    }

    [Fact]
    public void ParseJson_VersionAsObject_AndNumericSignatureKind()
    {
        const string json = """{"Name":"A.B","PackageFullName":"A.B_1.2.3.4_x64__abc","Version":{"Major":1,"Minor":2,"Build":3,"Revision":4},"SignatureKind":3}""";
        var r = Assert.Single(StoreAppCatalog.ParseJson(json));
        Assert.Equal("1.2.3.4", r.Version);
        Assert.Equal("3", r.SignatureKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("not json at all")]
    [InlineData("[{\"Name\":\"NoFullName\"}]")]
    [InlineData("[1, \"x\"]")]
    public void ParseJson_EmptyOrInvalid_ReturnsEmpty(string? json)
        => Assert.Empty(StoreAppCatalog.ParseJson(json));

    [Theory]
    [InlineData("Microsoft.BingNews_4.1.0.0_x64__8wekyb3d8bbwe", true)]
    [InlineData("king.com.CandyCrushSaga_1.2.3.0_x86__kgqvnymyfvs32", true)]
    [InlineData("Bad'; Remove-Item C:\\ -Recurse; '", false)]
    [InlineData("name with spaces_1.0_x64__abc", false)]
    [InlineData("*", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidPackageFullName_RejectsInjection(string? name, bool expected)
        => Assert.Equal(expected, StoreAppCatalog.IsValidPackageFullName(name));

    [Theory]
    [InlineData("king.com.CandyCrushSaga", "king.com.*", true)]
    [InlineData("Disney.37853FC22B2CE", "*Disney*", true)]
    [InlineData("Microsoft.BingNews", "Microsoft.BingNews", true)]
    [InlineData("Microsoft.BingNewsX", "Microsoft.BingNews", false)]
    [InlineData("Microsoft.VCLibs.140.00", "Microsoft.VCLibs*", true)]
    [InlineData("kingXcom.Game", "king.com.*", false)]
    public void WildcardMatch(string text, string pattern, bool expected)
        => Assert.Equal(expected, StoreAppCatalog.WildcardMatch(text, pattern));

    [Fact]
    public void CreateStoreApp_UsesCatalogAndPublisherFallback()
    {
        var app = ProgramsService.CreateStoreApp(new StoreAppRecord(
            "Microsoft.BingNews", "Microsoft.BingNews_4.1.0.0_x64__8wekyb3d8bbwe", "Microsoft.BingNews_8wekyb3d8bbwe",
            "CN=Microsoft Corporation, O=Microsoft Corporation", "4.1.0.0", null, "Store"));
        Assert.Equal("Новости", app.DisplayName);
        Assert.Equal("Microsoft Corporation", app.PublisherDisplay);
        Assert.True(app.IsBloatware);
        Assert.True(app.CanRemove);
        Assert.NotNull(app.Badge);

        var store = ProgramsService.CreateStoreApp(new StoreAppRecord(
            "Microsoft.WindowsStore", "Microsoft.WindowsStore_1.0.0.0_x64__8wekyb3d8bbwe", null, null, "1.0", null, "Store"));
        Assert.False(store.CanRemove);
        Assert.Equal("Не указан", store.PublisherDisplay);
    }
}
