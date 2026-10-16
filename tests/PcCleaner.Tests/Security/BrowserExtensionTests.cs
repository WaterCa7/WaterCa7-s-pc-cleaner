using System.IO;
using System.Text;
using PcCleaner.Modules.Security;
using Xunit;

namespace PcCleaner.Tests.Security;

public class BrowserExtensionTests
{
    public BrowserExtensionTests() => TestEnvironment.Init();

    private static string WriteExtension(string extensionsDir, string id, string version, string manifestJson,
        params (string Locale, string MessagesJson)[] locales)
    {
        var dir = Path.Combine(extensionsDir, id, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifestJson, Encoding.UTF8);
        foreach (var (locale, json) in locales)
        {
            var ldir = Path.Combine(dir, "_locales", locale);
            Directory.CreateDirectory(ldir);
            File.WriteAllText(Path.Combine(ldir, "messages.json"), json, Encoding.UTF8);
        }
        return dir;
    }

    [Fact]
    public void Manifest_ResolvesMsgPlaceholderFromDefaultLocale()
    {
        var root = TestEnvironment.NewTempDir("ext");
        var dir = WriteExtension(root, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1.2.3_0",
            """{ "name": "__MSG_appName__", "version": "1.2.3", "default_locale": "en" }""",
            ("en", """{ "appName": { "message": "Super Coupon Finder" } }"""));

        var m = BrowserExtensionScanner.ReadManifest(dir);
        Assert.NotNull(m);
        Assert.Equal("Super Coupon Finder", m!.Value.Name);
        Assert.Equal("1.2.3", m.Value.Version);
    }

    [Fact]
    public void Manifest_MsgKeysAreCaseInsensitive_AndRussianIsPreferred()
    {
        var root = TestEnvironment.NewTempDir("ext");
        var dir = WriteExtension(root, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "3.0.1_0",
            """{ "name": "__MSG_EXT_NAME__", "version": "3.0.1", "default_locale": "en_US" }""",
            ("en_US", """{ "ext_name": { "message": "Search Helper" } }"""),
            ("ru", """{ "Ext_Name": { "message": "Помощник поиска", "description": "имя" } }"""));

        Assert.Equal("Помощник поиска", BrowserExtensionScanner.ReadManifest(dir)!.Value.Name);
    }

    [Fact]
    public void Manifest_UnresolvedPlaceholder_FallsBackToShortName()
    {
        var root = TestEnvironment.NewTempDir("ext");
        var dir = WriteExtension(root, "cccccccccccccccccccccccccccccccc", "1.0_0",
            """{ "name": "__MSG_missing__", "short_name": "Tabby", "version": "1.0" }""");
        Assert.Equal("Tabby", BrowserExtensionScanner.ReadManifest(dir)!.Value.Name);
    }

    [Fact]
    public void ResolveMessage_PlainNameUnchanged()
        => Assert.Equal("uBlock Origin", BrowserExtensionScanner.ResolveMessage(TestEnvironment.NewTempDir("ext"), "uBlock Origin", "en"));

    [Fact]
    public void ScanChromium_ReadsProfilesPicksLatestVersionAndSkipsBuiltIns()
    {
        var userData = Path.Combine(TestEnvironment.NewTempDir("chrome"), "User Data");
        var profile = Path.Combine(userData, "Default");
        var ext = Path.Combine(profile, "Extensions");
        const string adware = "dddddddddddddddddddddddddddddddd";
        const string good = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string component = "ffffffffffffffffffffffffffffffff";
        const string forced = "gggggggggggggggggggggggggggggggg";
        WriteExtension(ext, adware, "1.0.0_0", """{ "name": "Old", "version": "1.0.0" }""");
        WriteExtension(ext, adware, "1.10.0_0", """{ "name": "__MSG_n__", "version": "1.10.0", "default_locale": "en" }""",
            ("en", """{ "n": { "message": "Best Deals & Coupons" } }"""));
        WriteExtension(ext, good, "2.0_0", """{ "name": "Password Manager", "version": "2.0" }""");
        WriteExtension(ext, component, "1.0_0", """{ "name": "Built-in thing", "version": "1.0" }""");
        WriteExtension(ext, forced, "1.0_0", """{ "name": "Helper", "version": "1.0" }""");
        WriteExtension(ext, "nmmhkkegccagdldgiimedpiccmgmieda", "1.0_0", """{ "name": "Payments", "version": "1.0" }""");
        WriteExtension(ext, "hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh", "1.0_0", """{ "name": "My Theme", "version": "1.0", "theme": {} }""");
        Directory.CreateDirectory(Path.Combine(ext, "Temp"));

        File.WriteAllText(Path.Combine(profile, "Secure Preferences"), $$"""
            { "extensions": { "settings": {
                "{{adware}}": { "location": 1, "state": 1 },
                "{{good}}": { "location": 1, "disable_reasons": [ 1 ] },
                "{{component}}": { "location": 5 },
                "{{forced}}": { "location": 7 }
            } } }
            """);
        File.WriteAllText(Path.Combine(userData, "Local State"),
            """{ "profile": { "info_cache": { "Default": { "name": "Работа" } } } }""");

        var (list, browsers) = BrowserExtensionScanner.Scan(new[] { new BrowserRoot("Chrome", userData, BrowserKind.Chromium) });

        Assert.Equal(1, browsers);
        Assert.Equal(3, list.Count);
        var a = list.Single(e => e.Id == adware);
        Assert.Equal("Best Deals & Coupons", a.Name);
        Assert.Equal("1.10.0", a.Version);
        Assert.Equal("Работа", a.Profile);
        Assert.Equal(SecurityStatus.Warning, a.Risk);
        Assert.True(a.Enabled);

        var g = list.Single(e => e.Id == good);
        Assert.False(g.Enabled);
        Assert.Equal(SecurityStatus.Ok, g.Risk);

        var f = list.Single(e => e.Id == forced);
        Assert.Equal(ExtensionSource.Policy, f.Source);
        Assert.Equal(SecurityStatus.Warning, f.Risk);
        Assert.StartsWith("https://chromewebstore.google.com/detail/", f.StoreUrl);
    }

    [Fact]
    public void ParseFirefox_SkipsBuiltInsAndThemes()
    {
        var json = Encoding.UTF8.GetBytes("""
            { "addons": [
              { "id": "formautofill@mozilla.org", "type": "extension", "location": "app-system-defaults", "defaultLocale": { "name": "Form Autofill" }, "active": true },
              { "id": "default-theme@mozilla.org", "type": "theme", "location": "app-builtin", "defaultLocale": { "name": "System theme" } },
              { "id": "uBlock0@raymondhill.net", "type": "extension", "location": "app-profile", "version": "1.60.0",
                "defaultLocale": { "name": "uBlock Origin" }, "active": true, "path": "C:\\x\\ublock.xpi" },
              { "id": "sovetnik@yandex.ru", "type": "extension", "location": "app-profile", "version": "5.0",
                "defaultLocale": { "name": "Советник Яндекс.Маркета" }, "active": true },
              { "id": "bundled@vendor", "type": "extension", "location": "app-system-share", "version": "1.0",
                "defaultLocale": { "name": "Vendor Helper" }, "active": false }
            ] }
            """);
        var list = BrowserExtensionScanner.ParseFirefoxExtensions(json, "Firefox", "default-release");
        foreach (var e in list)
            SecurityRules.ClassifyExtension(e);

        Assert.Equal(3, list.Count);
        Assert.Equal(SecurityStatus.Ok, list.Single(e => e.Name == "uBlock Origin").Risk);
        Assert.Equal(SecurityStatus.Warning, list.Single(e => e.Id == "sovetnik@yandex.ru").Risk);
        var vendor = list.Single(e => e.Id == "bundled@vendor");
        Assert.Equal(ExtensionSource.OtherProgram, vendor.Source);
        Assert.False(vendor.Enabled);
        Assert.Equal(SecurityStatus.Info, vendor.Risk);
    }

    [Fact]
    public void ScanChromium_UnexpectedJsonShapes_DoNotHideOtherExtensions()
    {
        var userData = Path.Combine(TestEnvironment.NewTempDir("chrome"), "User Data");
        var profile = Path.Combine(userData, "Default");
        var ext = Path.Combine(profile, "Extensions");
        const string odd = "iiiiiiiiiiiiiiiiiiiiiiiiiiiiiiii";
        const string fine = "jjjjjjjjjjjjjjjjjjjjjjjjjjjjjjjj";
        const string arrayManifest = "kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk";
        // messages.json whose root is an array, a fractional "state" and a manifest that is not an object.
        WriteExtension(ext, odd, "1.0_0", """{ "name": "__MSG_n__", "short_name": "Odd", "version": "1.0", "default_locale": "en" }""",
            ("en", """[ "not", "an", "object" ]"""));
        WriteExtension(ext, fine, "2.0_0", """{ "name": "Dark Reader", "version": "2.0" }""");
        WriteExtension(ext, arrayManifest, "1.0_0", """[ 1, 2, 3 ]""");
        File.WriteAllText(Path.Combine(profile, "Preferences"), $$"""
            { "extensions": { "settings": {
                "{{odd}}": { "location": 1.5, "state": 1.5 },
                "{{fine}}": { "location": 1, "state": 1, "disable_reasons": 1.5 }
            } } }
            """);
        File.WriteAllText(Path.Combine(userData, "Local State"), """{ "profile": [ 1 ] }""");

        var (list, browsers) = BrowserExtensionScanner.Scan(new[] { new BrowserRoot("Chrome", userData, BrowserKind.Chromium) });

        Assert.Equal(1, browsers);
        Assert.Equal(2, list.Count);
        Assert.Equal("Odd", list.Single(e => e.Id == odd).Name);
        Assert.Equal("Dark Reader", list.Single(e => e.Id == fine).Name);
    }

    [Fact]
    public void ParseFirefox_NonObjectEntriesAreSkipped()
    {
        var json = Encoding.UTF8.GetBytes("""
            { "addons": [ 42, "x", { "id": "a@b", "type": "extension", "location": "app-profile", "defaultLocale": { "name": "Tab Tool" } } ] }
            """);
        var e = Assert.Single(BrowserExtensionScanner.ParseFirefoxExtensions(json, "Firefox", "p"));
        Assert.Equal("Tab Tool", e.Name);
        Assert.Empty(BrowserExtensionScanner.ParseFirefoxExtensions(Encoding.UTF8.GetBytes("[ 1 ]"), "Firefox", "p"));
    }

    [Theory]
    [InlineData("Search Manager Plus", true)]
    [InlineData("Поиск Яндекса", true)]
    [InlineData("Amigo Start", true)]
    [InlineData("Weather Forecast Pro", true)]
    [InlineData("uBlock Origin", false)]
    [InlineData("Google Translate", false)]
    [InlineData("Dark Reader", false)]
    public void SuspiciousNames(string name, bool suspicious)
        => Assert.Equal(suspicious, SecurityRules.SuspiciousExtensionReason(name) is not null);

    [Fact]
    public void DisabledSuspiciousExtension_IsOnlyInfo()
    {
        var e = new BrowserExtension { Browser = "Chrome", Id = "x", Name = "Super Search", Enabled = false };
        SecurityRules.ClassifyExtension(e);
        Assert.Equal(SecurityStatus.Info, e.Risk);
        Assert.Contains("отключено", e.Reason);
    }

    [Fact]
    public void EvaluateExtensions_Status()
    {
        var bad = new BrowserExtension { Browser = "Chrome", Id = "x", Name = "Super Search", Enabled = true, Risk = SecurityStatus.Warning };
        var ok = new BrowserExtension { Browser = "Chrome", Id = "y", Name = "Dark Reader", Enabled = true };
        Assert.Equal(SecurityStatus.Warning, SecurityRules.EvaluateExtensions(new[] { bad, ok }, 1).Status);
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateExtensions(new[] { ok }, 1).Status);
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateExtensions(Array.Empty<BrowserExtension>(), 0).Status);
    }
}
