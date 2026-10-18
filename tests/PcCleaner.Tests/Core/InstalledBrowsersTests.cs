using System.IO;
using PcCleaner.Core;
using Xunit;

namespace PcCleaner.Tests.Core;

public class InstalledBrowsersTests : IDisposable
{
    private readonly string _root = TestEnvironment.NewTempDir("browsers");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private InstalledBrowsers Browsers(params string[] registered) => InstalledBrowsers.ForTests(_root, () => registered);

    /// <summary>Creates an empty file (with its folders) standing in for a browser .exe.</summary>
    internal static void CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    private IEnumerable<BrowserProgram> Installed(InstalledBrowsers browsers)
        => Enum.GetValues<BrowserProgram>().Where(browsers.IsInstalled);

    [Fact]
    public void EmptySystem_NoBrowserIsInstalled()
        => Assert.Empty(Installed(Browsers()));

    [Theory]
    [InlineData(BrowserProgram.Chrome, @"Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(BrowserProgram.Chrome, @"Program Files (x86)\Google\Chrome\Application\chrome.exe")]
    [InlineData(BrowserProgram.Chrome, @"Local\Google\Chrome\Application\chrome.exe")]
    [InlineData(BrowserProgram.Edge, @"Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData(BrowserProgram.Yandex, @"Local\Yandex\YandexBrowser\Application\browser.exe")]
    [InlineData(BrowserProgram.Opera, @"Local\Programs\Opera\launcher.exe")]
    [InlineData(BrowserProgram.OperaGx, @"Local\Programs\Opera GX\opera.exe")]
    [InlineData(BrowserProgram.Brave, @"Program Files\BraveSoftware\Brave-Browser\Application\brave.exe")]
    [InlineData(BrowserProgram.Vivaldi, @"Local\Vivaldi\Application\vivaldi.exe")]
    [InlineData(BrowserProgram.Chromium, @"Local\Chromium\Application\chrome.exe")]
    [InlineData(BrowserProgram.Firefox, @"Program Files\Mozilla Firefox\firefox.exe")]
    [InlineData(BrowserProgram.Firefox, @"Local\Mozilla Firefox\firefox.exe")]
    public void ExeInTheUsualFolder_OnlyThatBrowserIsInstalled(BrowserProgram browser, string relativeExe)
    {
        CreateFile(Path.Combine(_root, relativeExe));

        Assert.Equal(new[] { browser }, Installed(Browsers()));
    }

    [Fact]
    public void OperaUninstallLeftovers_AreNotAnInstalledBrowser()
    {
        // What the Opera uninstaller leaves: old version folders and installer settings, but no opera.exe / launcher.exe.
        var gx = Path.Combine(_root, @"Local\Programs\Opera GX");
        Directory.CreateDirectory(Path.Combine(gx, "73.0.3856.438"));
        CreateFile(Path.Combine(gx, "installer_prefs.json"));
        CreateFile(Path.Combine(gx, "debug.log"));

        Assert.False(Browsers().IsInstalled(BrowserProgram.OperaGx));
    }

    [Fact]
    public void BrowserInACustomFolder_IsFoundThroughItsWindowsRegistration()
    {
        var exe = Path.Combine(_root, @"D\Apps\Firefox\firefox.exe");
        CreateFile(exe);

        Assert.Equal(new[] { BrowserProgram.Firefox }, Installed(Browsers(exe)));
    }

    [Fact]
    public void RegistrationOfADeletedExe_DoesNotCount()
        => Assert.Empty(Installed(Browsers(Path.Combine(_root, @"D\Apps\Firefox\firefox.exe"))));

    [Fact]
    public void RegisteredChromiumAndOperaGx_AreNotTakenForChromeAndOpera()
    {
        var chromium = Path.Combine(_root, @"D\Chromium\Application\chrome.exe");
        var operaGx = Path.Combine(_root, @"D\Opera GX\launcher.exe");
        CreateFile(chromium);
        CreateFile(operaGx);

        Assert.Equal(new[] { BrowserProgram.OperaGx, BrowserProgram.Chromium }, Installed(Browsers(chromium, operaGx)));
    }

    [Fact]
    public void RegisteredGenericBrowserExe_CountsOnlyInsideAYandexFolder()
    {
        var other = Path.Combine(_root, @"D\Some\browser.exe");
        var yandex = Path.Combine(_root, @"D\Yandex\YandexBrowser\Application\browser.exe");
        CreateFile(other);
        CreateFile(yandex);

        Assert.Empty(Installed(Browsers(other)));
        Assert.Equal(new[] { BrowserProgram.Yandex }, Installed(Browsers(yandex)));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\"", @"C:\Program Files\Mozilla Firefox\firefox.exe")]
    [InlineData("\"C:\\Users\\a\\AppData\\Local\\Programs\\Opera\\launcher.exe\" --url \"%1\"",
        @"C:\Users\a\AppData\Local\Programs\Opera\launcher.exe")]
    [InlineData(@"C:\Program Files\Vivaldi\Application\vivaldi.exe --single-argument %1", @"C:\Program Files\Vivaldi\Application\vivaldi.exe")]
    [InlineData("", null)]
    [InlineData("\"", null)]
    public void ExecutableFromCommand_TakesTheExePath(string command, string? expected)
        => Assert.Equal(expected, InstalledBrowsers.ExecutableFromCommand(command));
}
