using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Settings;
using Xunit;

namespace PcCleaner.Tests.Settings;

[Collection(SettingsFileCollection.Name)]
public class SettingsLogicTests
{
    public SettingsLogicTests() => TestEnvironment.Init();

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(24, 24)]
    [InlineData(720, 720)]
    [InlineData(721, 720)]
    [InlineData(int.MaxValue, 720)]
    public void ClampMinAge_KeepsValueInRange(int input, int expected)
        => Assert.Equal(expected, SettingsLogic.ClampMinAge(input));

    [Theory]
    [InlineData("24", true, 24)]
    [InlineData(" 0 ", true, 0)]
    [InlineData("720", true, 720)]
    [InlineData("721", false, 24)]
    [InlineData("-1", false, 24)]
    [InlineData("сутки", false, 24)]
    [InlineData("", false, 24)]
    [InlineData(null, false, 24)]
    public void TryParseMinAge_ValidatesRange(string? text, bool ok, int expected)
    {
        Assert.Equal(ok, SettingsLogic.TryParseMinAge(text, out var hours, out var error));
        Assert.Equal(expected, hours);
        Assert.Equal(ok, error is null);
    }

    [Fact]
    public void AgeOptions_ContainPresetsAndCustomValue()
    {
        var options = SettingsLogic.BuildAgeOptions(12);
        Assert.Equal(new[] { 0, 1, 6, 12, 24, 48, 72, 168 }, options.Select(o => o.Hours));
        Assert.Contains(options, o => o.Hours == 24 && o.Label.Contains("рекомендуется"));
        Assert.Equal("12 часов", options.Single(o => o.Hours == 12).Label);
    }

    [Fact]
    public void AgeOptions_ClampOutOfRangeValue()
    {
        var options = SettingsLogic.BuildAgeOptions(100_000);
        Assert.Equal(720, options.Max(o => o.Hours));
        Assert.DoesNotContain(options, o => o.Hours > 720);
    }

    [Theory]
    [InlineData(1, "1 час")]
    [InlineData(6, "6 часов")]
    [InlineData(48, "2 дня")]
    [InlineData(72, "3 дня")]
    [InlineData(120, "5 дней")]
    [InlineData(168, "1 неделя")]
    public void AgeLabel_IsNaturalRussian(int hours, string expected)
        => Assert.Equal(expected, SettingsLogic.AgeLabel(hours));

    [Theory]
    [InlineData(@"C:\Temp\", @"C:\Temp")]
    [InlineData(@"  ""C:\Program Files\App""  ", @"C:\Program Files\App")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\a\..\b", @"C:\b")]
    [InlineData(@"D:/Projects/Site", @"D:\Projects\Site")]
    public void TryNormalizePath_NormalizesValidPaths(string input, string expected)
    {
        Assert.True(SettingsLogic.TryNormalizePath(input, out var normalized, out var error), error);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryNormalizePath_ExpandsEnvironmentVariables()
    {
        Assert.True(SettingsLogic.TryNormalizePath(@"%SystemRoot%\Temp", out var normalized, out _));
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        Assert.Equal(expected, normalized, ignoreCase: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(@"relative\folder")]
    [InlineData(@"\no-drive")]
    [InlineData(@"C:\bad*name")]
    [InlineData(@"C:\what?")]
    [InlineData(@"C:\a|b")]
    [InlineData(@"C:\file.txt:stream")]
    public void TryNormalizePath_RejectsInvalidInput(string? input)
    {
        Assert.False(SettingsLogic.TryNormalizePath(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryAddExclusion_AddsNormalizedPathAndRejectsDuplicates()
    {
        var settings = new AppSettings();
        Assert.Equal(ExclusionAddResult.Added,
            SettingsLogic.TryAddExclusion(settings, @"D:\Work\Projects\", out var normalized, out _));
        Assert.Equal(@"D:\Work\Projects", normalized);
        Assert.Single(settings.CleanerExclusions);

        Assert.Equal(ExclusionAddResult.Duplicate,
            SettingsLogic.TryAddExclusion(settings, @"d:\work\PROJECTS", out _, out var message));
        Assert.Contains("уже есть", message);

        Assert.Equal(ExclusionAddResult.AlreadyCovered,
            SettingsLogic.TryAddExclusion(settings, @"D:\Work\Projects\Site\cache", out _, out message));
        Assert.Contains(@"D:\Work\Projects", message);

        // A sibling whose name merely starts with the same text is not covered.
        Assert.Equal(ExclusionAddResult.Added,
            SettingsLogic.TryAddExclusion(settings, @"D:\Work\ProjectsOld", out _, out _));

        Assert.Equal(ExclusionAddResult.Invalid, SettingsLogic.TryAddExclusion(settings, "relative", out _, out _));
        Assert.Equal(2, settings.CleanerExclusions.Count);
    }

    [Fact]
    public void TryAddExclusion_DoesNotProbeNetworkPaths()
    {
        // Probing an unreachable server would block the UI thread for the SMB timeout.
        var settings = new AppSettings();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(ExclusionAddResult.Added,
            SettingsLogic.TryAddExclusion(settings, @"\\pcc-no-such-server.invalid\share\folder", out var normalized, out var message));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
        Assert.Equal(@"\\pcc-no-such-server.invalid\share\folder", normalized);
        Assert.DoesNotContain("пока нет", message);
        Assert.True(SettingsLogic.IsNetworkPath(normalized));
        Assert.False(SettingsLogic.IsNetworkPath(@"C:\Temp"));
    }

    [Fact]
    public void RemoveItems_IsCaseInsensitive()
    {
        var list = new List<string> { @"C:\A", @"C:\B", @"C:\C" };
        Assert.Equal(2, SettingsLogic.RemoveItems(list, new[] { @"c:\a", @"C:\C", @"C:\Missing" }));
        Assert.Equal(new[] { @"C:\B" }, list);
    }

    [Fact]
    public void ResetToDefaults_ClearsPreferencesButKeepsModuleData()
    {
        var settings = new AppSettings
        {
            CreateRestorePoint = false,
            CleanerMinFileAgeHours = 2,
            CleanerExclusions = new() { @"D:\Keep" },
            CleanerDisabledCategories = new() { "browser" },
            TrustedItems = new() { "tool.exe" },
        };
        settings.ModuleData["Optimizer"] = new() { ["PowerPlan.Previous"] = "guid" };

        var trustedBefore = settings.TrustedItems;

        SettingsLogic.ResetToDefaults(settings);

        Assert.True(settings.CreateRestorePoint);
        Assert.Equal(24, settings.CleanerMinFileAgeHours);
        Assert.Empty(settings.CleanerExclusions);
        Assert.Empty(settings.CleanerDisabledCategories);
        Assert.Empty(settings.TrustedItems);
        Assert.False(settings.IsTrusted("tool.exe"));
        // Cleared through AppSettings.RemoveTrusted: the list other pages may be reading is replaced, not edited.
        Assert.Equal(new[] { "tool.exe" }, trustedBefore);
        Assert.Equal("guid", settings.GetModuleValue("Optimizer", "PowerPlan.Previous"));
    }

    [Fact]
    public void RemoveTrusted_IsCaseInsensitiveAndCountsWhatWasRemoved()
    {
        var settings = new AppSettings();
        settings.AddTrusted(@"C:\Tools\a.exe");
        settings.AddTrusted("Program B");

        Assert.Equal(1, SettingsLogic.RemoveTrusted(settings, new[] { @"c:\tools\A.EXE", "missing.exe" }));
        Assert.Equal(new[] { "Program B" }, settings.TrustedItems);
        Assert.Equal(0, SettingsLogic.RemoveTrusted(settings, Array.Empty<string>()));
        Assert.Equal(1, SettingsLogic.RemoveTrusted(settings, new[] { "program b", "PROGRAM B" }));
        Assert.Empty(settings.TrustedItems);
    }

    [Fact]
    public void Sanitize_RepairsTheTrustedListThroughAddRemoveTrusted()
    {
        var settings = new AppSettings { TrustedItems = new() { "a.exe", " ", "A.EXE", " b.exe " } };
        var loaded = settings.TrustedItems;

        Assert.True(SettingsLogic.Sanitize(settings) > 0);

        Assert.Equal(new[] { "a.exe", "b.exe" }, settings.TrustedItems);
        Assert.True(settings.IsTrusted("B.EXE"));
        Assert.Equal(new[] { "a.exe", " ", "A.EXE", " b.exe " }, loaded); // never edited in place
        Assert.Equal(0, SettingsLogic.Sanitize(settings));
    }

    [Fact]
    public void Sanitize_RepairsHandEditedValues()
    {
        var settings = new AppSettings
        {
            CleanerMinFileAgeHours = -10,
            CleanerExclusions = new() { @"C:\A", " ", @"c:\a", @"C:\B" },
            TrustedItems = null!,
        };

        Assert.True(SettingsLogic.Sanitize(settings) > 0);
        Assert.Equal(0, settings.CleanerMinFileAgeHours);
        Assert.Equal(new[] { @"C:\A", @"C:\B" }, settings.CleanerExclusions);
        Assert.NotNull(settings.TrustedItems);
        Assert.Equal(0, SettingsLogic.Sanitize(settings));
    }

    [Fact]
    public void ListEdits_NeverModifyTheSharedListInstance()
    {
        // Other modules (e.g. a cleaning scan on a background thread) may be enumerating these lists right now:
        // edits must replace the list instead of changing it in place.
        var settings = new AppSettings
        {
            CleanerExclusions = new() { @"C:\A", @"c:\a", " " },
            TrustedItems = new() { "a.exe", "A.EXE" },
        };
        var exclusions = settings.CleanerExclusions;
        var trusted = settings.TrustedItems;

        SettingsLogic.Sanitize(settings);
        Assert.Equal(new[] { @"C:\A", @"c:\a", " " }, exclusions);
        Assert.Equal(new[] { "a.exe", "A.EXE" }, trusted);
        Assert.Equal(new[] { @"C:\A" }, settings.CleanerExclusions);
        Assert.Equal(new[] { "a.exe" }, settings.TrustedItems);

        var sanitized = settings.CleanerExclusions;
        Assert.Equal(ExclusionAddResult.Added, SettingsLogic.TryAddExclusion(settings, @"D:\B", out _, out _));
        Assert.Equal(new[] { @"C:\A" }, sanitized);
        Assert.Equal(new[] { @"C:\A", @"D:\B" }, settings.CleanerExclusions);

        var source = settings.CleanerExclusions;
        var rest = SettingsLogic.Without(source, new[] { @"c:\a" }, out var removed);
        Assert.Equal(1, removed);
        Assert.Equal(new[] { @"D:\B" }, rest);
        Assert.Equal(new[] { @"C:\A", @"D:\B" }, source);
    }

    [Fact]
    public void RunAsAdmin_IsOnByDefault_AlsoForASettingsFileFromAnOlderVersion()
    {
        Assert.True(new AppSettings().RunAsAdmin);

        var older = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{ \"AutoUpdate\": false, \"CreateRestorePoint\": false }")!;
        Assert.True(older.RunAsAdmin);

        var off = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{ \"RunAsAdmin\": false }")!;
        Assert.False(off.RunAsAdmin);
    }

    [Fact]
    public void Settings_RoundTripThroughFile()
    {
        // AppPaths.DataDir points to the temp test folder (TestEnvironment), never to the real profile.
        Assert.StartsWith(Path.GetTempPath(), AppPaths.SettingsFile, StringComparison.OrdinalIgnoreCase);

        var marker = "C:\\RoundTrip\\" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings
        {
            CreateRestorePoint = false,
            CleanerMinFileAgeHours = SettingsLogic.ClampMinAge(5000),
            TrustedItems = new() { @"C:\Tools\trusted.exe" },
        };
        Assert.Equal(ExclusionAddResult.Added, SettingsLogic.TryAddExclusion(settings, marker, out _, out _));

        // Tests of other modules may save their own settings to the same temp file at any moment: retry a few times.
        AppSettings loaded = null!;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            settings.SetModuleValue("Settings.Tests", "Key", "Значение"); // also saves
            loaded = AppSettings.Load();
            if (loaded.CleanerExclusions.Contains(marker))
                break;
        }

        Assert.False(loaded.CreateRestorePoint);
        Assert.Equal(720, loaded.CleanerMinFileAgeHours);
        Assert.Contains(marker, loaded.CleanerExclusions);
        Assert.Equal(new[] { @"C:\Tools\trusted.exe" }, loaded.TrustedItems);
        Assert.Equal("Значение", loaded.GetModuleValue("Settings.Tests", "Key"));
    }
}
