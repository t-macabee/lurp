using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     B51: <see cref="StatusSections" /> is the single parser for the MCP
///     <c>lurp_status</c> <c>sections</c> and CLI <c>status --detail=</c> vocabularies.
///     Values combine as a union and unknown values are errors.
/// </summary>
public sealed class StatusSectionsTests
{
    [Theory]
    [InlineData("all", true, true, true, true)]
    [InlineData("documents", true, true, false, false)]
    [InlineData("References, completeness", true, false, true, true)]
    [InlineData("ALL", true, true, true, true)]
    [InlineData("references", true, false, true, false)]
    [InlineData("manifest", true, false, false, false)]
    [InlineData("freshness", false, false, false, false)]
    [InlineData("freshness,references", true, false, true, false)]
    public void TryParse_ValidList_SetsTheUnion(string list, bool manifest, bool documents, bool references, bool completeness)
    {
        Assert.True(StatusSections.TryParse(list, allowSurfaceNames: true, out var set, out var badValue));
        Assert.Null(badValue);
        Assert.Equal(manifest, set.Manifest);
        Assert.Equal(documents, set.Documents);
        Assert.Equal(references, set.References);
        Assert.Equal(completeness, set.Completeness);
    }

    [Theory]
    [InlineData("foo", true, "foo")]
    [InlineData("manifest,foo", true, "foo")]
    [InlineData(",", true, ",")]
    [InlineData("manifest", false, "manifest")]
    [InlineData("freshness", false, "freshness")]
    public void TryParse_InvalidList_ReturnsFalseWithTheBadValue(string list, bool allowSurfaceNames, string expectedBadValue)
    {
        Assert.False(StatusSections.TryParse(list, allowSurfaceNames, out _, out var badValue));
        Assert.Equal(expectedBadValue, badValue);
    }

    [Fact]
    public void Vocabulary_NamesTheValuesEachSurfaceAccepts()
    {
        Assert.Equal("freshness, manifest, documents, references, completeness or all", StatusSections.Vocabulary(true));
        Assert.Equal("documents, references, completeness or all", StatusSections.Vocabulary(false));
    }
}
