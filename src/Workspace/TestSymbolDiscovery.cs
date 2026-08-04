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

        var docCommentId = symbolId.AsSpan(0, pipeIndex);
        if (docCommentId.Length < 3 || docCommentId[1] != ':')
            return null;

        var kind = docCommentId[0];
        if (kind == 'T' || kind == 'N')
            return null;

        var afterPrefix = docCommentId[2..];
        var parenIndex = afterPrefix.IndexOf('(');
        var methodNamePart = parenIndex >= 0 ? afterPrefix[..parenIndex] : afterPrefix;
        var lastDot = methodNamePart.LastIndexOf('.');
        if (lastDot < 0)
            return null;

        var parentTypeName = afterPrefix[..lastDot];
        var assemblyIdentity = symbolId.AsSpan(pipeIndex + 1);
        return string.Concat("T:".AsSpan(), parentTypeName, "|".AsSpan(), assemblyIdentity);
    }
}
