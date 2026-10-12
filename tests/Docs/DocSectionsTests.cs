using Lurp.Tests.Docs;

namespace Lurp.Tests;

public sealed class DocSectionsTests
{
    private static readonly string[] ExpectedKeys = ["one", "two"];

    [Fact]
    public void Extract_ReturnsTheTextBetweenTheMarkers()
    {
        const string text = "a<!-- generated:k -->CONTENT<!-- /generated:k -->b";

        Assert.Equal("CONTENT", DocSections.Extract(text, "k"));
    }

    [Fact]
    public void Extract_ReturnsNull_WhenTheKeyIsAbsent()
    {
        Assert.Null(DocSections.Extract("no markers here", "k"));
    }

    [Fact]
    public void Replace_ReplacesOnlyTheSpan_AndKeepsTextOutsideTheMarkers()
    {
        const string inline = "a<!-- generated:k -->OLD<!-- /generated:k -->b";
        Assert.Equal("a<!-- generated:k -->NEW<!-- /generated:k -->b", DocSections.Replace(inline, "k", "NEW"));

        const string block = "a\n<!-- generated:k -->\nOLD\n<!-- /generated:k -->\nb";
        Assert.Equal("a\n<!-- generated:k -->\nNEW\n<!-- /generated:k -->\nb", DocSections.Replace(block, "k", "\nNEW\n"));
    }

    [Fact]
    public void Replace_Throws_WhenTheEndMarkerIsMissing()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DocSections.Replace("<!-- generated:k -->OLD", "k", "NEW"));

        Assert.Contains("k", exception.Message);
    }

    [Fact]
    public void Replace_Throws_WhenTheKeyHasTwoBeginMarkers()
    {
        const string text =
            "<!-- generated:k -->a<!-- /generated:k --><!-- generated:k -->b<!-- /generated:k -->";

        Assert.Throws<InvalidOperationException>(() => DocSections.Replace(text, "k", "x"));
    }

    [Fact]
    public void FindKeys_ReturnsEveryKeyInTheText()
    {
        const string text =
            "<!-- generated:one -->x<!-- /generated:one --><!-- generated:two -->y<!-- /generated:two -->";

        Assert.Equal(ExpectedKeys, DocSections.FindKeys(text));
    }
}
