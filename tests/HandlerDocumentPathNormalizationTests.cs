using Lurp.Handlers;

namespace Lurp.Storage.Tests;

public sealed class HandlerDocumentPathNormalizationTests
{
    [Fact]
    public void NativeWindowsSeparators_AreNormalizedToStoredForm()
    {
        var normalized = HandlerBootstrap.NormalizeDocumentPath(@"eNote.Application\Common\Paging\PagingExtensions.cs");

        Assert.Equal("eNote.Application/Common/Paging/PagingExtensions.cs", normalized);
    }

    [Fact]
    public void AlreadyNormalizedPath_IsUnchanged()
    {
        const string path = "eNote.Application/Common/Paging/PagingExtensions.cs";

        Assert.Equal(path, HandlerBootstrap.NormalizeDocumentPath(path));
    }

    [Fact]
    public void MissingArgument_StaysNull()
    {
        Assert.Null(HandlerBootstrap.NormalizeDocumentPath(null));
    }
}
