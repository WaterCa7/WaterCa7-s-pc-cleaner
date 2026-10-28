using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

public class UpdateFooterTests
{
    private static readonly UpdateManifest M = UpdateManifest.Parse(
        UpdateTestKit.ManifestJson("1.6.0", 2_000_000, new string('a', 64)), out _)!;

    private static readonly Version Current = new(1, 5, 0);

    [Theory]
    [InlineData(UpdateState.Checking, "v1.5.0 · проверка…")]
    [InlineData(UpdateState.UpToDate, "v1.5.0 · последняя версия")]
    [InlineData(UpdateState.Downloading, "v1.5.0 · скачивается 1.6.0")]
    [InlineData(UpdateState.Ready, "v1.5.0 · скачана 1.6.0")]
    [InlineData(UpdateState.ManualRequired, "v1.5.0 · есть версия 1.6.0")]
    [InlineData(UpdateState.Updated, "v1.5.0 · обновлена")]
    [InlineData(UpdateState.None, "v1.5.0")]
    [InlineData(UpdateState.Failed, "v1.5.0")]
    public void TheCorner_ShowsTheVersionAndWhatTheUpdaterKnows(UpdateState state, string expected)
        => Assert.Equal(expected, UpdateFooter.Text(state, M, Current));

    [Theory]
    [InlineData(UpdateState.Downloading)]
    [InlineData(UpdateState.Ready)]
    [InlineData(UpdateState.ManualRequired)]
    public void WithoutAManifest_OnlyTheVersionIsShown(UpdateState state)
        => Assert.Equal("v1.5.0", UpdateFooter.Text(state, null, Current));

    [Fact]
    public void AVersionWithoutABuildNumber_IsShownWithThreeParts()
        => Assert.Equal("v1.5.0 · последняя версия", UpdateFooter.Text(UpdateState.UpToDate, null, new Version(1, 5)));
}
