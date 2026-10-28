using PcCleaner.Core;
using PcCleaner.Core.Account;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// <see cref="App.ParseArgs"/> fills static properties (<c>App.ScreenshotDir</c>, <c>App.LanguageArg</c>…): every test that calls it
/// lives in this one collection, which never runs in parallel with anything, so no two tests parse at the same time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ParseArgsCollection
{
    public const string Name = "ParseArgs (sequential)";
}

[Collection(ParseArgsCollection.Name)]
public class ParseArgsTests
{
    [Theory]
    [InlineData(new[] { "--lang", "en" }, "en")]
    [InlineData(new[] { "--LANG", "EN" }, "en")]
    [InlineData(new[] { "--lang", "ru" }, "ru")]
    [InlineData(new[] { "--lang", "de" }, null)]
    public void ReadsTheLanguage(string[] args, string? expected)
    {
        App.ParseArgs(args);

        Assert.Equal(expected, App.LanguageArg);
    }

    [Theory]
    [InlineData(new[] { "--mode", "simple" }, UiMode.Simple)]
    [InlineData(new[] { "--MODE", "Expert" }, UiMode.Expert)]
    [InlineData(new[] { "--mode", "tiny" }, null)]
    public void ReadsTheMode(string[] args, UiMode? expected)
    {
        App.ParseArgs(args);

        Assert.Equal(expected, App.ModeArg);
    }

    [Fact]
    public void ReadsTheWelcomeSwitch_AnyCase_AndItNeedsNoValue()
    {
        App.ParseArgs(new[] { "--screenshots", "out", "--WELCOME", "--page", "Dashboard" });

        Assert.True(App.WelcomeArg);
        Assert.Equal(new[] { "Dashboard" }, App.ScreenshotPages);
    }

    [Fact]
    public void ReadsTheModeTogetherWithTheLanguageAndTheOtherSwitches()
    {
        App.ParseArgs(new[] { "--screenshots", "out", "--mode", "simple", "--lang", "en", "--page", "Settings" });

        Assert.Equal(UiMode.Simple, App.ModeArg);
        Assert.Equal("en", App.LanguageArg);
        Assert.Equal(new[] { "Settings" }, App.ScreenshotPages);
        Assert.Equal("out", App.ScreenshotDir);
    }

    [Fact]
    public void IgnoresTheElevatedRestartMarker_AndStillReadsTheOtherArguments()
    {
        // The restarted copy gets "<original arguments> --elevated-restart"; the command line parser must not trip over it.
        App.ParseArgs(new[] { "--page", "Cleaner", "--elevated-restart" });

        Assert.Equal(new[] { "Cleaner" }, App.ScreenshotPages);
        Assert.Null(App.ScreenshotDir);
        Assert.Null(App.UpdatedFrom);
    }

    [Fact]
    public void NoArguments_GiveTheDefaults()
    {
        App.ParseArgs(Array.Empty<string>());

        AssertDefaults();
        Assert.Null(App.ScreenshotPages);
    }

    [Fact]
    public void EachCall_DescribesOnlyItsOwnArguments()
    {
        // Everything a call can set...
        App.ParseArgs(new[]
        {
            "--screenshots", "out", "--page", "Settings,Network", "--no-scan", "--size", "800x600",
            "--updated-from", "1.0.0", "--lang", "en", "--mode", "simple", "--welcome",
        });
        Assert.Equal("out", App.ScreenshotDir);
        Assert.Equal(new[] { "Settings", "Network" }, App.ScreenshotPages);
        Assert.False(App.ScreenshotScan);
        Assert.Equal((800, 600), App.ScreenshotSize);
        Assert.Equal("1.0.0", App.UpdatedFrom);
        Assert.Equal("en", App.LanguageArg);
        Assert.Equal(UiMode.Simple, App.ModeArg);
        Assert.True(App.WelcomeArg);

        // ...is gone with the next call: it must not see what an earlier one (another test, another start) left behind.
        App.ParseArgs(new[] { "--page", "Cleaner", "--elevated-restart" });

        Assert.Equal(new[] { "Cleaner" }, App.ScreenshotPages);
        AssertDefaults();
    }

    [Theory]
    [InlineData("guest", Tier.Guest)]
    [InlineData("FREE", Tier.Free)]
    [InlineData("pro", Tier.Pro)]
    [InlineData("vip", null)]
    public void Tier_IsReadForTheScreenshotRun(string value, Tier? expected)
    {
        App.ParseArgs(new[] { "--screenshots", "x", "--tier", value });
        Assert.Equal(expected, App.TierArg);
        App.ParseArgs(Array.Empty<string>());
        Assert.Null(App.TierArg);
    }

    [Fact]
    public void SignIn_AsksForPicturesOfTheSignInWindow()
    {
        App.ParseArgs(new[] { "--screenshots", "x", "--signin" });
        Assert.True(App.SignInArg);
        App.ParseArgs(Array.Empty<string>());
        Assert.False(App.SignInArg);
    }

    private static void AssertDefaults()
    {
        Assert.Null(App.ScreenshotDir);
        Assert.True(App.ScreenshotScan);
        Assert.Null(App.ScreenshotSize);
        Assert.Null(App.UpdatedFrom);
        Assert.Null(App.LanguageArg);
        Assert.Null(App.ModeArg);
        Assert.False(App.WelcomeArg);
        Assert.Null(App.TierArg);
        Assert.False(App.SignInArg);
    }
}
