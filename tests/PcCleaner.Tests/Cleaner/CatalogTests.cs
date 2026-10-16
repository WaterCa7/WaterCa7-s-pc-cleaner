using System.IO;
using PcCleaner.Modules.Cleaner;
using Xunit;

namespace PcCleaner.Tests.Cleaner;

public class CatalogTests
{
    /// <summary>Lays out a fake Chrome/Firefox/Opera/Discord installation (cache and private data) inside the test folder.</summary>
    internal static void CreateFakeInstallation(CleanerTestFolder t, CleanerEnvironment env)
    {
        var chrome = Path.Combine(env.LocalAppData, @"Google\Chrome\User Data");
        foreach (var profile in new[] { "Default", "Profile 1", "Guest Profile", "System Profile" })
        {
            Directory.CreateDirectory(Path.Combine(chrome, profile, @"Cache\Cache_Data"));
            Directory.CreateDirectory(Path.Combine(chrome, profile, "Code Cache"));
            Directory.CreateDirectory(Path.Combine(chrome, profile, "GPUCache"));
            Directory.CreateDirectory(Path.Combine(chrome, profile, "Network"));
            Directory.CreateDirectory(Path.Combine(chrome, profile, "Local Storage"));
            Directory.CreateDirectory(Path.Combine(chrome, profile, "Extensions"));
            Directory.CreateDirectory(Path.Combine(chrome, profile, "Sessions"));
            Directory.CreateDirectory(Path.Combine(chrome, profile, @"Service Worker\CacheStorage"));
            foreach (var file in new[] { "Cookies", "History", "Login Data", "Bookmarks", "Web Data" })
                File.WriteAllText(Path.Combine(chrome, profile, file), "private");
        }
        Directory.CreateDirectory(Path.Combine(chrome, "ShaderCache"));
        Directory.CreateDirectory(Path.Combine(chrome, "GrShaderCache"));
        Directory.CreateDirectory(Path.Combine(chrome, "Crashpad"));

        var firefox = Path.Combine(env.LocalAppData, @"Mozilla\Firefox\Profiles");
        Directory.CreateDirectory(Path.Combine(firefox, @"abc.default-release\cache2\entries"));
        Directory.CreateDirectory(Path.Combine(firefox, @"xyz.default\startupCache"));

        var operaLocal = Path.Combine(env.LocalAppData, @"Opera Software\Opera Stable");
        Directory.CreateDirectory(Path.Combine(operaLocal, "Cache"));
        File.WriteAllText(Path.Combine(operaLocal, @"Cache\data_0"), "x");
        Directory.CreateDirectory(Path.Combine(env.RoamingAppData, @"Opera Software\Opera Stable\Default\Code Cache"));

        Directory.CreateDirectory(Path.Combine(env.RoamingAppData, @"discord\Cache\Cache_Data"));
        Directory.CreateDirectory(Path.Combine(env.RoamingAppData, @"discord\Local Storage"));
        Directory.CreateDirectory(Path.Combine(env.LocalAppData, @"NVIDIA\DXCache"));
        Directory.CreateDirectory(Path.Combine(env.LocalAppData, @"EpicGamesLauncher\Saved\webcache_4430"));
        Directory.CreateDirectory(Path.Combine(env.LocalAppData, @"EpicGamesLauncher\Saved\Config"));
        Directory.CreateDirectory(env.Temp);
        Directory.CreateDirectory(Path.Combine(env.WindowsDir, "Temp"));
    }

    private static readonly string[] PrivateNames =
    {
        "Cookies", "History", "Login Data", "Bookmarks", "Web Data", "Network", "Local Storage", "Extensions", "Sessions",
        "Service Worker", "System Profile", "startupCache", "Config", "Crashpad",
    };

    [Fact]
    public void Browsers_OnlyCacheFoldersAreListed()
    {
        using var t = new CleanerTestFolder("catalog");
        var env = CleanerEnvironment.ForTests(t.Root);
        CreateFakeInstallation(t, env);

        var categories = CleanerCatalog.Build(env, 24);
        var chrome = Assert.Single(categories, c => c.Id == "browser.chrome");
        var roots = chrome.Roots.Select(r => Path.GetRelativePath(Path.Combine(env.LocalAppData, @"Google\Chrome\User Data"), r.Path))
            .OrderBy(p => p)
            .ToList();

        Assert.Contains(@"Default\Cache\Cache_Data", roots);
        Assert.Contains(@"Default\Code Cache", roots);
        Assert.Contains(@"Default\GPUCache", roots);
        Assert.Contains(@"Profile 1\Cache\Cache_Data", roots);
        Assert.Contains(@"Guest Profile\GPUCache", roots);
        Assert.Contains("ShaderCache", roots);
        Assert.Contains("GrShaderCache", roots);
        Assert.Equal(11, roots.Count); // 3 profiles x 3 folders + 2 shader caches

        foreach (var root in categories.SelectMany(c => c.Roots))
            foreach (var part in root.Path.Split('\\'))
                Assert.DoesNotContain(part, PrivateNames);
    }

    [Fact]
    public void NotInstalledBrowsersAndApps_AreNotListed()
    {
        using var t = new CleanerTestFolder("catalog-empty");
        var env = CleanerEnvironment.ForTests(t.Root);

        var categories = CleanerCatalog.Build(env, 24);

        Assert.DoesNotContain(categories, c => c.Group == CleanerCategory.GroupBrowsers);
        Assert.DoesNotContain(categories, c => c.Group == CleanerCategory.GroupApps);
        Assert.Contains(categories, c => c.Id == "win.usertemp");
        Assert.Contains(categories, c => c.Id == "win.recyclebin");
    }

    [Fact]
    public void FirefoxOperaDiscordEpic_AreDetected()
    {
        using var t = new CleanerTestFolder("catalog-apps");
        var env = CleanerEnvironment.ForTests(t.Root);
        CreateFakeInstallation(t, env);

        var categories = CleanerCatalog.Build(env, 24).ToDictionary(c => c.Id);

        var firefox = categories["browser.firefox"];
        Assert.Single(firefox.Roots);
        Assert.EndsWith(@"abc.default-release\cache2", firefox.Roots[0].Path);

        var opera = categories["browser.opera"];
        Assert.Contains(opera.Roots, r => r.Path.EndsWith(@"Opera Stable\Cache")); // legacy cache layout
        Assert.Contains(opera.Roots, r => r.Path.EndsWith(@"Default\Code Cache"));

        Assert.Single(categories["app.discord"].Roots);
        Assert.EndsWith(@"discord\Cache\Cache_Data", categories["app.discord"].Roots[0].Path);
        Assert.EndsWith("webcache_4430", Assert.Single(categories["app.epic"].Roots).Path);
        Assert.False(categories["app.nvidia"].DefaultChecked);
    }

    [Fact]
    public void RunningProgramsAreDetectedByProcessName()
    {
        using var t = new CleanerTestFolder("catalog-running");
        var env = CleanerEnvironment.ForTests(t.Root,
            new HashSet<string>(new[] { "chrome", "Discord" }, StringComparer.OrdinalIgnoreCase));
        CreateFakeInstallation(t, env);

        var categories = CleanerCatalog.Build(env, 24).ToDictionary(c => c.Id);

        Assert.True(categories["browser.chrome"].IsRunning);
        Assert.True(categories["app.discord"].IsRunning);
        Assert.False(categories["browser.firefox"].IsRunning);
    }

    [Fact]
    public void Defaults_UserDataAndRiskyCategoriesAreNotPreselected()
    {
        using var t = new CleanerTestFolder("catalog-defaults");
        var env = CleanerEnvironment.ForTests(t.Root);
        var categories = CleanerCatalog.Build(env, 24).ToDictionary(c => c.Id);

        Assert.False(categories["win.recyclebin"].DefaultChecked);
        Assert.False(categories["win.logs"].DefaultChecked);
        Assert.NotNull(categories["win.recyclebin"].Warning);
        Assert.True(categories["win.usertemp"].DefaultChecked);
        Assert.Equal(AgeRule.UserSetting, categories["win.usertemp"].Age);
        Assert.True(categories["win.systemtemp"].RequiresAdmin);
        Assert.True(categories["win.wer"].PartlyRequiresAdmin);
        Assert.True(categories["win.deliveryopt"].RequiresAdmin);
        Assert.Contains("24 часов", categories["win.usertemp"].Description);
    }

    [Fact]
    public void ChromiumRoots_SkipJunctionedProfiles()
    {
        using var t = new CleanerTestFolder("catalog-junction");
        var userData = t.Dir("User Data");
        var elsewhere = t.Dir("elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, @"Cache\Cache_Data"));
        t.Junction(Path.Combine(userData, "Default"), elsewhere);

        var roots = CleanerCatalog.ChromiumCacheRoots(new[] { userData }, userDataIsProfile: false);

        Assert.Empty(roots);
    }
}
