using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PcCleaner.Core;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// Microsoft Store / MSIX files in WindowsApps are signed through their package. Everything here runs on a synthetic
/// WindowsApps folder in the test temp directory; nothing is written into the real one.
/// </summary>
public sealed class PackageSignatureTests : IDisposable
{
    private const string MicrosoftDn = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    private const string WindowsDn = "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    private const string VendorDn = "CN=\"Contoso, Ltd.\", O=\"Contoso, Ltd.\", L=Berlin, C=DE";

    private readonly string _temp;
    private readonly string _root;
    private readonly List<string> _junctions = new();

    public PackageSignatureTests()
    {
        TestEnvironment.Init();
        _temp = TestEnvironment.NewTempDir("pkgsig");
        // The final path the file system reports (no 8.3 names): the same normalization the check itself uses.
        var canonical = PackageSignature.FinalPath(_temp) ?? _temp;
        _root = Path.Combine(canonical, "WindowsApps");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var junction in _junctions)
        {
            try
            {
                Directory.Delete(junction); // removes the link only
            }
            catch (IOException)
            {
            }
        }
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FullName(string name, string version, string publisher, string arch = "x64")
        => $"{name}_{version}_{arch}__{PackageSignature.PublisherId(publisher)}";

    private static string Manifest(string name, string version, string publisher, string? publisherDisplayName) =>
        $"""
         <?xml version="1.0" encoding="utf-8"?>
         <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                  xmlns:mp="http://schemas.microsoft.com/appx/2014/phone/manifest">
           <Identity Name="{name}" Publisher="{System.Security.SecurityElement.Escape(publisher)}" Version="{version}" ProcessorArchitecture="x64" />
           <mp:PhoneIdentity PhoneProductId="7d47d89a-7900-47c5-93f2-46eb6d94c159" PhonePublisherId="95d94207-0c7c-47ed-82db-d75c81153c35" />
           <Properties>
             <DisplayName>Test app</DisplayName>
             {(publisherDisplayName is null ? "" : $"<PublisherDisplayName>{publisherDisplayName}</PublisherDisplayName>")}
           </Properties>
         </Package>
         """;

    /// <summary>Creates root\FullName\{AppxManifest.xml, AppxSignature.p7x, App.exe}; returns the exe path.</summary>
    private string CreatePackage(string fullName, string manifest, bool withSignature = true, string? root = null)
    {
        var dir = Path.Combine(root ?? _root, fullName);
        Directory.CreateDirectory(Path.Combine(dir, "Sub"));
        File.WriteAllText(Path.Combine(dir, PackageSignature.ManifestFileName), manifest, Encoding.UTF8);
        if (withSignature)
            File.WriteAllBytes(Path.Combine(dir, PackageSignature.SignatureFileName), new byte[] { 0x50, 0x4B, 0x43, 0x58, 1, 2, 3 });
        var exe = Path.Combine(dir, "Sub", "App.exe");
        File.WriteAllText(exe, "not a real program");
        return exe;
    }

    private string CreatePackage(string name, string version, string publisher, string? displayName, bool withSignature = true)
        => CreatePackage(FullName(name, version, publisher), Manifest(name, version, publisher, displayName), withSignature);

    private Func<string, StagedPackage?> Staged(PackageOrigin origin = PackageOrigin.Store)
        => fullName => new StagedPackage(Path.Combine(_root, fullName), origin);

    private SignatureInfo? Verify(string path, PackageOrigin origin = PackageOrigin.Store)
        => PackageSignature.TryVerify(path, _root, Staged(origin));

    // ---- publisher id / DN parsing ----

    [Theory]
    [InlineData(MicrosoftDn, "8wekyb3d8bbwe")]
    [InlineData(WindowsDn, "cw5n1h2txyewy")]
    [InlineData("CN=Razer USA Ltd., O=Razer USA Ltd., L=Irvine, S=California, C=US", "qemfkr3nbbywc")]
    public void PublisherId_MatchesWindows(string publisher, string expected)
        => Assert.Equal(expected, PackageSignature.PublisherId(publisher));

    [Fact]
    public void Dn_QuotedValuesKeepTheirCommas()
    {
        Assert.Equal("Contoso, Ltd.", PackageSignature.DnValue(VendorDn, "CN"));
        Assert.Equal("Contoso, Ltd.", PackageSignature.DnValue(VendorDn, "O"));
        Assert.Equal("DE", PackageSignature.DnValue(VendorDn, "C"));
        Assert.Equal("Microsoft Windows", PackageSignature.DnValue(WindowsDn, "cn"));
        Assert.Equal("Say \"hi\"", PackageSignature.DnValue("CN=\"Say \"\"hi\"\"\", C=US", "CN"));
    }

    [Theory]
    [InlineData(MicrosoftDn, true)]
    [InlineData(WindowsDn, true)]
    [InlineData("CN=Microsoft Corporation, O=Contoso, C=US", false)]
    [InlineData("CN=Contoso, OU=O=Microsoft Corporation, C=US", false)]
    [InlineData("CN=Contoso, O=Microsoft Corporation Fake, C=US", false)]
    [InlineData("CN=Contoso, O=Contoso, O=Microsoft Corporation, C=US", false)]
    [InlineData(VendorDn, false)]
    [InlineData("", false)]
    public void IsMicrosoftPublisher_NeedsExactlyMicrosoftCorporationAsOrganization(string publisher, bool expected)
        => Assert.Equal(expected, PackageSignature.IsMicrosoftPublisher(publisher));

    [Theory]
    [InlineData("Microsoft.WindowsStore_22608.1401.7.0_x64__8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.UI.Xaml.2.8_8.2501.31001.0_x64__8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.WindowsCalculator_11.2502.2.0_neutral_split.scale-100_8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.WindowsStore_22608.1401.7.0_x64_8wekyb3d8bbwe", false)]
    [InlineData("Microsoft.WindowsStore_22608.1401.7.0_x64__8wekyb3d8bbw", false)]
    [InlineData("Microsoft.WindowsStore_22608.1401.7.0_x64__8wekyb3d8bbwi", false)]
    [InlineData("Microsoft.WindowsStore_v22608_x64__8wekyb3d8bbwe", false)]
    [InlineData("Deleted", false)]
    [InlineData("MutableBackup", false)]
    public void FullName_IsParsedStrictly(string folder, bool expected)
        => Assert.Equal(expected, PackageSignature.TryParseFullName(folder, out _, out _, out _));

    // ---- manifest ----

    [Fact]
    public void Manifest_ReadsIdentityAndPublisherDisplayName()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Manifest("Microsoft.WindowsStore", "22608.1401.7.0", MicrosoftDn, "Microsoft Corporation")));
        var manifest = PackageSignature.ReadManifest(stream);
        Assert.NotNull(manifest);
        Assert.Equal("Microsoft.WindowsStore", manifest!.Name);
        Assert.Equal("22608.1401.7.0", manifest.Version);
        Assert.Equal(MicrosoftDn, manifest.Publisher);
        Assert.Equal("Microsoft Corporation", manifest.PublisherDisplayName);
    }

    [Fact]
    public void Manifest_WithDtd_IsRejected()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE Package [ <!ENTITY pub "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"> ]>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="A" Publisher="&pub;" Version="1.0.0.0" />
            </Package>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.ThrowsAny<System.Xml.XmlException>(() => PackageSignature.ReadManifest(stream));
    }

    [Fact]
    public void Manifest_IdentityOutsideTheFoundationNamespaceOrNested_IsIgnored()
    {
        const string xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:x="urn:other">
              <x:Identity Name="A" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation" Version="1.0.0.0" />
              <Extensions><Identity Name="A" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation" Version="1.0.0.0" /></Extensions>
            </Package>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.Null(PackageSignature.ReadManifest(stream));
    }

    [Fact]
    public void Manifest_PublisherDisplayNameIsTakenOnlyFromProperties()
    {
        const string xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Extensions><PublisherDisplayName>Microsoft Corporation</PublisherDisplayName></Extensions>
              <Properties><DisplayName>App</DisplayName><PublisherDisplayName>Contoso</PublisherDisplayName></Properties>
              <Identity Name="A" Publisher="CN=Contoso, O=Contoso" Version="1.0.0.0" />
            </Package>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var manifest = PackageSignature.ReadManifest(stream);
        Assert.NotNull(manifest);
        Assert.Equal("Contoso", manifest!.PublisherDisplayName);
        Assert.Equal("A", manifest.Name);
    }

    [Fact]
    public void Manifest_TwoIdentities_IsRejected()
    {
        const string xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="A" Publisher="CN=Contoso, O=Contoso" Version="1.0.0.0" />
              <Identity Name="A" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation" Version="1.0.0.0" />
            </Package>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.Null(PackageSignature.ReadManifest(stream));
    }

    // ---- verification on a synthetic WindowsApps folder ----

    [Fact]
    public void MicrosoftPackageFile_IsValidPackageSignedMicrosoft()
    {
        var exe = CreatePackage("Microsoft.WindowsStore", "22608.1401.7.0", MicrosoftDn, "Microsoft Corporation");

        var info = Verify(exe);

        Assert.NotNull(info);
        Assert.Equal(SignatureStatus.Valid, info!.Status);
        Assert.True(info.IsPackageSigned);
        Assert.False(info.IsCatalogSigned);
        Assert.True(info.IsMicrosoft);
        Assert.Equal("Microsoft Corporation", info.Publisher);
        Assert.Equal(MicrosoftDn, info.PackagePublisher);
        Assert.Equal("Подписан: Microsoft Corporation (пакет приложения)", info.StatusText);
        Assert.Equal(SuspicionResult.Clean, Suspicion.Analyze(exe, info));
    }

    [Fact]
    public void ResourceDisplayName_FallsBackToTheCommonName()
    {
        var exe = CreatePackage("Microsoft.WidgetsPlatformRuntime", "1.6.13.0", WindowsDn, "ms-resource:PublisherDisplayName");
        var info = Verify(exe);
        Assert.NotNull(info);
        Assert.Equal("Microsoft Windows", info!.Publisher);
        Assert.True(info.IsMicrosoft);
    }

    [Fact]
    public void ThirdPartyPackage_IsValidButNotMicrosoft()
    {
        var exe = CreatePackage("Contoso.App", "2.1.0.0", VendorDn, null);
        var info = Verify(exe, PackageOrigin.DeveloperSigned);
        Assert.NotNull(info);
        Assert.True(info!.IsValid);
        Assert.True(info.IsPackageSigned);
        Assert.False(info.IsMicrosoft);
        Assert.Equal("Contoso, Ltd.", info.Publisher);
    }

    [Fact]
    public void ThirdPartyPackage_ClaimingMicrosoftAsDisplayName_IsNotMicrosoft()
    {
        var exe = CreatePackage("Contoso.App", "2.1.0.0", VendorDn, "Microsoft Corporation");
        var info = Verify(exe);
        Assert.NotNull(info);
        Assert.False(info!.IsMicrosoft);
        Assert.DoesNotMatch("^Microsoft", info.Publisher);
        Assert.Equal(VendorDn, info.Publisher);
    }

    [Fact]
    public void ThirdPartyPackage_WithMicrosoftCommonName_IsNotMicrosoft()
    {
        const string dn = "CN=Microsoft Corporation, O=Contoso, C=US";
        var exe = CreatePackage("Contoso.App", "2.1.0.0", dn, null);
        var info = Verify(exe);
        Assert.NotNull(info);
        Assert.False(info!.IsMicrosoft);
        Assert.Equal(dn, info.Publisher);
    }

    [Fact]
    public void PackageWithoutAppxSignature_StaysUnsigned()
    {
        var exe = CreatePackage("Microsoft.WindowsStore", "22608.1401.7.0", MicrosoftDn, "Microsoft Corporation", withSignature: false);
        Assert.Null(Verify(exe));
    }

    [Fact]
    public void UnsignedMsixPublisherMarker_StaysUnsigned()
    {
        const string dn = "CN=Contoso, O=Contoso, OID.2.25.311729368913984317654407730594956997722=1";
        var exe = CreatePackage("Contoso.Unsigned", "1.0.0.0", dn, "Contoso");
        Assert.Null(Verify(exe));
        Assert.True(PackageSignature.HasUnsignedMarker(dn));
    }

    [Theory]
    [InlineData((int)PackageOrigin.Unsigned)]
    [InlineData((int)PackageOrigin.DeveloperUnsigned)]
    [InlineData((int)PackageOrigin.Unknown)]
    public void UnsignedOrigin_StaysUnsigned(int origin)
    {
        var exe = CreatePackage("Microsoft.WindowsStore", "22608.1401.7.0", MicrosoftDn, "Microsoft Corporation");
        Assert.Null(Verify(exe, (PackageOrigin)origin));
        Assert.NotNull(Verify(exe, PackageOrigin.Store));
    }

    [Fact]
    public void PackageThatIsNotStaged_OrStagedElsewhere_StaysUnsigned()
    {
        var exe = CreatePackage("Microsoft.WindowsStore", "22608.1401.7.0", MicrosoftDn, "Microsoft Corporation");
        Assert.Null(PackageSignature.TryVerify(exe, _root, _ => null));
        Assert.Null(PackageSignature.TryVerify(exe, _root, _ => new StagedPackage(Path.Combine(_temp, "Elsewhere"), PackageOrigin.Store)));
    }

    [Fact]
    public void FolderNameNotMatchingTheManifest_StaysUnsigned()
    {
        var manifest = Manifest("Microsoft.WindowsStore", "22608.1401.7.0", MicrosoftDn, "Microsoft Corporation");
        // Publisher hash of a different publisher, other name, other version.
        Assert.Null(Verify(CreatePackage(FullName("Microsoft.WindowsStore", "22608.1401.7.0", VendorDn), manifest)));
        Assert.Null(Verify(CreatePackage(FullName("Microsoft.Other", "22608.1401.7.0", MicrosoftDn), manifest)));
        Assert.Null(Verify(CreatePackage(FullName("Microsoft.WindowsStore", "1.0.0.0", MicrosoftDn), manifest)));
    }

    [Fact]
    public void FilesOutsideAPackageFolder_StayUnsigned()
    {
        // Directly in WindowsApps, in a look-alike sibling folder, and a relative trick back out of the root.
        var direct = Path.Combine(_root, "App.exe");
        File.WriteAllText(direct, "x");
        Assert.Null(Verify(direct));

        var siblingRoot = _root + "X";
        Directory.CreateDirectory(siblingRoot);
        var fullName = FullName("Microsoft.WindowsStore", "22608.1401.7.0", MicrosoftDn);
        var sibling = CreatePackage(fullName, Manifest("Microsoft.WindowsStore", "22608.1401.7.0", MicrosoftDn, "Microsoft Corporation"), root: siblingRoot);
        Assert.Null(Verify(sibling));

        var escaped = Path.Combine(_root, fullName, "..", "..", "WindowsAppsX", fullName, "Sub", "App.exe");
        Assert.Null(Verify(escaped));

        // The same package inside the root passes, but not through a device path.
        var inside = CreatePackage(fullName, Manifest("Microsoft.WindowsStore", "22608.1401.7.0", MicrosoftDn, "Microsoft Corporation"));
        Assert.NotNull(Verify(inside));
        Assert.Null(Verify(@"\\?\" + inside));
        Assert.Null(Verify(@"\\.\" + inside));
    }

    [Fact]
    public void PackageFolderReachedThroughAJunction_StaysUnsigned()
    {
        var fullName = FullName("Microsoft.WindowsStore", "22608.1401.7.0", MicrosoftDn);
        var realRoot = Path.Combine(_temp, "Real");
        Directory.CreateDirectory(realRoot);
        CreatePackage(fullName, Manifest("Microsoft.WindowsStore", "22608.1401.7.0", MicrosoftDn, "Microsoft Corporation"), root: realRoot);

        var junction = Path.Combine(_root, fullName);
        if (!CreateJunction(junction, Path.Combine(realRoot, fullName)))
            return; // mklink unavailable: nothing to test here

        var throughJunction = Path.Combine(junction, "Sub", "App.exe");
        Assert.True(File.Exists(throughJunction));
        Assert.Null(Verify(throughJunction));
    }

    [Fact]
    public void SignatureInfo_DefaultsAreNotPackageSigned()
    {
        var plain = new SignatureInfo(SignatureStatus.Valid, "Microsoft Windows");
        Assert.False(plain.IsPackageSigned);
        Assert.Null(plain.PackagePublisher);
        Assert.True(plain.IsMicrosoft);
        Assert.Equal("Подписан: Microsoft Windows", plain.StatusText);
        Assert.Equal(plain, new SignatureInfo(SignatureStatus.Valid, "Microsoft Windows"));
        Assert.NotEqual(plain, plain with { IsPackageSigned = true, PackagePublisher = WindowsDn });

        // A package-signed result is Microsoft only by its publisher name, not by the shown name.
        var spoofed = new SignatureInfo(SignatureStatus.Valid, "Microsoft Corporation") { IsPackageSigned = true, PackagePublisher = VendorDn };
        Assert.False(spoofed.IsMicrosoft);
        var unsigned = new SignatureInfo(SignatureStatus.Unsigned, null) { IsPackageSigned = true, PackagePublisher = MicrosoftDn };
        Assert.False(unsigned.IsMicrosoft);
    }

    [Fact]
    public void RealWindowsApps_ResolvesUnderProgramFiles()
    {
        var root = PackageSignature.WindowsAppsRoot;
        Assert.NotNull(root);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"), root, ignoreCase: true);
    }

    /// <summary>Read-only check on this PC: every exe of the installed Microsoft Store package counts as Microsoft-signed.</summary>
    [Fact]
    public void RealStorePackage_ExecutablesAreMicrosoftSigned()
    {
        var fullName = FindPackageFullName("Microsoft.WindowsStore_8wekyb3d8bbwe");
        if (fullName is null)
            return; // Store not installed
        var staged = PackageSignature.QueryStagedPackage(fullName);
        if (staged?.InstallPath is not { } dir || !Directory.Exists(dir))
            return;
        var exes = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Take(5).ToList();
        foreach (var exe in exes)
        {
            var info = FileSignature.Get(exe);
            Assert.True(info.IsValid, $"{exe}: {info.StatusText}");
            Assert.True(info.IsMicrosoft, $"{exe}: {info.StatusText}");
            if (info.IsPackageSigned)
                Assert.True(PackageSignature.IsMicrosoftPublisher(info.PackagePublisher));
        }
    }

    private bool CreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
                return false;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);
            if (process.ExitCode != 0 || !Directory.Exists(link))
                return false;
            _junctions.Add(link);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string? FindPackageFullName(string familyName)
    {
        try
        {
            uint count = 0, length = 0;
            var rc = GetPackagesByPackageFamily(familyName, ref count, null, ref length, null);
            if (rc != 122 || count == 0 || length == 0)
                return null;
            var pointers = new IntPtr[count];
            var buffer = new char[length];
            rc = GetPackagesByPackageFamily(familyName, ref count, pointers, ref length, buffer);
            if (rc != 0)
                return null;
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count,
        [Out] IntPtr[]? packageFullNames, ref uint bufferLength, [Out] char[]? buffer);
}
