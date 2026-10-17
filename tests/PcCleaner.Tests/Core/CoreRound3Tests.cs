using System.Globalization;
using System.IO;
using PcCleaner.Core;
using Xunit;

namespace PcCleaner.Tests.Core;

public class SeverityToTextConverterTests
{
    [Theory]
    [InlineData(IssueSeverity.Critical, "Критично")]
    [InlineData(IssueSeverity.High, "Важно")]
    [InlineData(IssueSeverity.Medium, "Внимание")]
    [InlineData(IssueSeverity.Low, "Мелочь")]
    [InlineData(IssueSeverity.Info, "Совет")]
    public void Badge_UsesNounLikeLabels(IssueSeverity severity, string expected)
        => Assert.Equal(expected, new SeverityToTextConverter().Convert(severity, typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Badge_DoesNotUseAdverbLabels()
    {
        var converter = new SeverityToTextConverter();
        foreach (var severity in Enum.GetValues<IssueSeverity>())
        {
            var text = (string)converter.Convert(severity, typeof(string), null, CultureInfo.InvariantCulture);
            Assert.NotEqual("Средне", text);
            Assert.NotEqual("Низко", text);
        }
    }
}

public class ProcessRunnerExplorerPathTests
{
    [Fact]
    public void ExplorerPath_IsTheFullPathInTheWindowsFolder()
    {
        var path = ProcessRunner.ExplorerPath;
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), path, ignoreCase: true);
    }
}
