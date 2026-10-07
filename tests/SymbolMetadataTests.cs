using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Tests;

public sealed class SymbolMetadataTests
{
    [Fact]
    public void Parse_NullInput_ReturnsNull()
    {
        Assert.Null(SymbolMetadata.Parse(null, "S:Null"));
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsNull()
    {
        Assert.Null(SymbolMetadata.Parse(string.Empty, "S:Empty"));
    }

    [Fact]
    public void Parse_ValidJson_ReturnsRootElement()
    {
        var metadata = SymbolMetadata.Parse("""{"accessibility":"Public"}""", "S:Valid");

        Assert.NotNull(metadata);
        Assert.Equal(JsonValueKind.Object, metadata!.Value.ValueKind);
        Assert.Equal("Public", metadata.Value.GetProperty(SymbolMetadataKeys.Accessibility).GetString());
    }

    [Fact]
    public void Parse_CorruptJson_ThrowsNamingSymbol()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SymbolMetadata.Parse("{not json", "S:Corrupt"));

        Assert.Contains("S:Corrupt", ex.Message);
        Assert.NotNull(ex.InnerException);
    }
}
