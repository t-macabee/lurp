namespace Lurp.Storage;

internal static class PublicSurface
{
    internal static bool IsInstanceConstructor(string kind, string docId)
    {
        if (kind != nameof(IndexedSymbolKind.Method))
            return false;
        var paren = docId.IndexOf('(', StringComparison.Ordinal);
        var name = paren >= 0 ? docId.AsSpan(0, paren) : docId.AsSpan();
        return name.EndsWith(".#ctor", StringComparison.Ordinal);
    }

    // A null result means the symbol needs friend facts that are unavailable.
    internal static bool? IsExposed(string? declaredAccess, string? effectiveAccess,
        string assemblyName, ProjectFacts? projects)
    {
        var access = effectiveAccess ?? declaredAccess;
        if (access is "Public" or "Protected" or "ProtectedOrInternal")
            return true;
        if (access is not ("Internal" or "ProtectedAndInternal"))
            return false;

        return projects?.HasAssembly(assemblyName) == true ? projects.GrantsInternalsOutsideSnapshot(assemblyName) : null;
    }
}
