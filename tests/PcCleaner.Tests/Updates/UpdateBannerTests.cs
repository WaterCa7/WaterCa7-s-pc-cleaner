using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

public class UpdateBannerTests
{
    private static readonly UpdateManifest M = UpdateManifest.Parse(
        UpdateTestKit.ManifestJson("1.2.0", 2_000_000, new string('a', 64)), out _)!;

    [Fact]
    public void Ready_OffersARestart()
    {
        var b = UpdateBanner.For(UpdateState.Ready, M, new Version(1, 1, 0))!;
        Assert.Contains("1.2.0", b.Text);
        Assert.Equal("Перезапустить сейчас", b.Action);
    }

    [Fact]
    public void Updated_OffersTheNotes()
    {
        var b = UpdateBanner.For(UpdateState.Updated, M, new Version(1, 2, 0))!;
        Assert.Contains("обновлена до версии 1.2.0", b.Text);
        Assert.Equal("Что нового", b.Action);
    }

    [Fact]
    public void ManualRequired_OffersTheDownloadPage()
        => Assert.Equal("Скачать вручную", UpdateBanner.For(UpdateState.ManualRequired, M, new Version(1, 1, 0))!.Action);

    [Theory]
    [InlineData(UpdateState.None)]
    [InlineData(UpdateState.Checking)]
    [InlineData(UpdateState.UpToDate)]
    [InlineData(UpdateState.Downloading)]
    [InlineData(UpdateState.Failed)]
    public void QuietStates_ShowNothing(UpdateState state)
        => Assert.Null(UpdateBanner.For(state, M, new Version(1, 1, 0)));

    [Fact]
    public void Updated_WithoutNotes_StillShowsTheVersion()
        => Assert.Contains("1.2.0", UpdateBanner.For(UpdateState.Updated, null, new Version(1, 2, 0))!.Text);

    [Fact]
    public void BusyWarning_NamesTheSections()
    {
        var text = UpdateBanner.BusyWarning(new[] { "Очистка", "Диски" });
        Assert.Contains("«Очистка»", text);
        Assert.Contains("«Диски»", text);
        Assert.Contains("Перезапустить", text);
    }
}
