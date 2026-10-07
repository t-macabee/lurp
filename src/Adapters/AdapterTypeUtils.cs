using Lurp.Workspace;
using Microsoft.CodeAnalysis;

namespace Lurp.Adapters;

internal static class AdapterTypeUtils
{
    internal static List<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol ns)
    {
        return ExtractionUtils.GetAllNamedTypes(ns).ToList();
    }
}