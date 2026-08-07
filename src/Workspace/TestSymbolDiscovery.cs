using Lurp.Storage;

namespace Lurp.Workspace;

internal static class TestSymbolDiscovery
{
    internal static IEnumerable<string> ExpandProductionSymbolIds(string symbolId)
    {
        yield return symbolId;
        var containingTypeId = DeriveContainingTypeId(symbolId);
        if (containingTypeId != null && containingTypeId != symbolId)
            yield return containingTypeId;
    }

    internal static string? DeriveContainingTypeId(string symbolId)
    {
        var pipeIndex = symbolId.IndexOf('|');
        if (pipeIndex < 0)
            return null;

        var docCommentId = symbolId[..pipeIndex];
        var assemblyIdentity = symbolId[(pipeIndex + 1)..];

        var typeDocCommentId = SymbolId.DeriveContainingTypeDocCommentId(docCommentId);
        if (typeDocCommentId == null)
            return null;

        return $"{typeDocCommentId}|{assemblyIdentity}";
    }
}
