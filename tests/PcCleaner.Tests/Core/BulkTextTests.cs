using PcCleaner.Core;
using Xunit;

namespace PcCleaner.Tests.Core;

public class BulkTextTests
{
    [Fact]
    public void BulletList_PutsEachLineOnItsOwnBulletedRow()
    {
        Assert.Equal("• a\n• b", BulkText.BulletList(new[] { "a", "b" }));
    }

    [Fact]
    public void BulletList_OfNothing_IsEmpty()
    {
        Assert.Equal("", BulkText.BulletList(Array.Empty<string>()));
    }

    [Fact]
    public void BulletList_ShowsFourteenLinesByDefault_AndCountsTheRest()
    {
        var lines = Enumerable.Range(1, 14).Select(i => $"item {i}").ToList();

        Assert.DoesNotContain("и ещё", BulkText.BulletList(lines));

        lines.Add("item 15");
        lines.Add("item 16");
        var text = BulkText.BulletList(lines).Split('\n');

        Assert.Equal(15, text.Length);
        Assert.Equal("• item 1", text[0]);
        Assert.Equal("• item 14", text[13]);
        Assert.Equal("…и ещё 2", text[14]);
    }

    [Fact]
    public void BulletList_HonoursAnExplicitMax()
    {
        var text = BulkText.BulletList(new[] { "a", "b", "c", "d", "e" }, 3);

        Assert.Equal("• a\n• b\n• c\n…и ещё 2", text);
    }

    [Fact]
    public void BulletList_ExactlyMaxLines_HasNoTail()
    {
        Assert.Equal("• a\n• b", BulkText.BulletList(new[] { "a", "b" }, 2));
    }

    [Fact]
    public void BulletList_AcceptsALazySequence()
    {
        IEnumerable<string> Lines()
        {
            yield return "x";
            yield return "y";
            yield return "z";
        }

        Assert.Equal("• x\n…и ещё 2", BulkText.BulletList(Lines(), 1));
    }
}
