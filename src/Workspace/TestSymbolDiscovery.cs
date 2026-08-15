namespace Lurp.Workspace;

internal static class TestSymbolDiscovery
{
    internal static IEnumerable<string> ExpandProductionSymbolIds(string symbolId)
    {
        yield return symbolId;
        var containingTypeId = SymbolId.DeriveContainingTypeSymbolId(symbolId);
        if (containingTypeId != null && containingTypeId != symbolId)
            yield return containingTypeId;
    }
}
