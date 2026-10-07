using Lurp.Storage;

namespace Lurp.Tests;

public sealed class SourceLineMapTests
{
    [Fact]
    public void ParseLineStarts_NullInput_ReturnsNull()
    {
        Assert.Null(SourceLineMap.ParseLineStarts(null, "dv"));
    }

    [Fact]
    public void ParseLineStarts_EmptyInput_ReturnsNull()
    {
        Assert.Null(SourceLineMap.ParseLineStarts(string.Empty, "dv"));
    }

    [Fact]
    public void ParseLineStarts_ValidJson_ReturnsArray()
    {
        var lineStarts = SourceLineMap.ParseLineStarts("[0,4,8]", "dv");

        Assert.NotNull(lineStarts);
        Assert.Equal(3, lineStarts!.Length);
        Assert.Equal(0, lineStarts[0]);
        Assert.Equal(4, lineStarts[1]);
        Assert.Equal(8, lineStarts[2]);
    }

    [Fact]
    public void ParseLineStarts_CorruptJson_ThrowsNamingDocumentVersion()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SourceLineMap.ParseLineStarts("{not json", "dv-123"));

        Assert.Contains("dv-123", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void ParseLineStarts_EmptyArray_ThrowsNamingDocumentVersion()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SourceLineMap.ParseLineStarts("[]", "dv-123"));

        Assert.Contains("dv-123", ex.Message);
    }

    [Fact]
    public void MapDeclaration_AbsentInput_ReturnsNull()
    {
        var content = "abc"u8.ToArray();

        Assert.Null(SourceLineMap.MapDeclaration("a.cs", null, 1, [0], content, false, "dv", "sid"));
        Assert.Null(SourceLineMap.MapDeclaration("a.cs", 0, null, [0], content, false, "dv", "sid"));
        Assert.Null(SourceLineMap.MapDeclaration("a.cs", 0, 1, null, content, false, "dv", "sid"));
        Assert.Null(SourceLineMap.MapDeclaration("a.cs", 0, 1, [0], null, false, "dv", "sid"));
    }

    [Fact]
    public void MapDeclaration_OutOfRangeSpan_ThrowsNamingSymbolAndDocumentVersion()
    {
        var content = "abc"u8.ToArray();

        var negative = Assert.Throws<InvalidOperationException>(() =>
            SourceLineMap.MapDeclaration("a.cs", -1, 1, [0], content, false, "dv-123", "sid"));
        Assert.Contains("sid", negative.Message);
        Assert.Contains("dv-123", negative.Message);

        Assert.Throws<InvalidOperationException>(() =>
            SourceLineMap.MapDeclaration("a.cs", 2, 1, [0], content, false, "dv", "sid"));
        Assert.Throws<InvalidOperationException>(() =>
            SourceLineMap.MapDeclaration("a.cs", 0, 4, [0], content, false, "dv", "sid"));
    }

    [Fact]
    public void MapDeclaration_ValidSpan_ReturnsOneBasedLocation()
    {
        // "one\ntwo\nthree": line starts at bytes 0, 4, 8. The span [4, 7) is "two".
        var content = "one\ntwo\nthree"u8.ToArray();

        var location = SourceLineMap.MapDeclaration("a.cs", 4, 7, [0, 4, 8], content, true, "dv", "sid");

        Assert.NotNull(location);
        Assert.Equal("a.cs", location!.DocumentPath);
        Assert.Equal(2, location.StartLine);
        Assert.Equal(0, location.StartColumn);
        Assert.Equal(2, location.EndLine);
        Assert.Equal(3, location.EndColumn);
        Assert.True(location.IsGenerated);
    }
}
