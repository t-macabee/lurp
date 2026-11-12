using Lurp.Workspace;
using Microsoft.CodeAnalysis;

namespace Lurp.Adapters;

/// <summary>
///     S02-F18: an adapter identifies a framework API by the full metadata name of its type, never by
///     a simple name (the same rule as D21 for reflection sinks).
/// </summary>
internal static class FrameworkTypes
{
    /// <summary>
    ///     True when the original definition of <paramref name="type" /> has the full metadata name
    ///     <paramref name="fullMetadataName" /> (<c>Namespace.Type`1</c>). A name that other libraries
    ///     also declare is narrowed by <paramref name="assemblyName" />.
    /// </summary>
    internal static bool Is(ITypeSymbol? type, string fullMetadataName, string? assemblyName = null)
    {
        return type?.OriginalDefinition is INamedTypeSymbol named &&
               ExtractionUtils.FullMetadataName(named) == fullMetadataName &&
               (assemblyName == null || named.ContainingAssembly?.Name == assemblyName);
    }

    /// <summary>The declaring type of the method that <paramref name="method" /> binds (an extension in its static form).</summary>
    internal static INamedTypeSymbol? DeclaringType(IMethodSymbol method)
    {
        return (method.ReducedFrom ?? method).OriginalDefinition.ContainingType;
    }
}
