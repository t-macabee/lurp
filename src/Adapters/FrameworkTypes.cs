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

    /// <summary>
    ///     True when <paramref name="type" /> or any of its base types has the full metadata name
    ///     <paramref name="fullMetadataName" />.
    /// </summary>
    internal static bool DerivesFrom(ITypeSymbol? type, string fullMetadataName, string? assemblyName = null)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (Is(current, fullMetadataName, assemblyName))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Reads attributes on <paramref name="symbol" /> and its base types (for a type) or
    ///     overridden methods (for a method) following CLR <c>AttributeUsage</c> (<c>Inherited</c>
    ///     and <c>AllowMultiple</c>).
    /// </summary>
    internal static IEnumerable<AttributeData> GetAttributesWithInheritance(ISymbol symbol)
    {
        HashSet<INamedTypeSymbol>? seenSingle = null;
        ISymbol? current = symbol;
        var isDerived = false;

        while (current != null)
        {
            foreach (var attr in current.GetAttributes())
            {
                var attrClass = attr.AttributeClass;
                if (attrClass == null)
                    continue;

                var (inherited, allowMultiple) = GetAttributeUsage(attrClass);
                if (isDerived && !inherited)
                    continue;

                if (!allowMultiple)
                {
                    seenSingle ??= new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                    if (!seenSingle.Add(attrClass))
                        continue;
                }

                yield return attr;
            }

            isDerived = true;
            current = current switch
            {
                INamedTypeSymbol type => type.BaseType,
                IMethodSymbol method => method.OverriddenMethod,
                _ => null
            };
        }
    }

    private static (bool Inherited, bool AllowMultiple) GetAttributeUsage(INamedTypeSymbol attributeClass)
    {
        for (var current = attributeClass; current != null; current = current.BaseType)
        {
            foreach (var attr in current.GetAttributes())
            {
                if (!Is(attr.AttributeClass, "System.AttributeUsageAttribute"))
                    continue;

                var inherited = true;
                var allowMultiple = false;

                foreach (var named in attr.NamedArguments)
                {
                    if (named.Key == "Inherited" && named.Value.Value is bool inh)
                        inherited = inh;
                    else if (named.Key == "AllowMultiple" && named.Value.Value is bool mult)
                        allowMultiple = mult;
                }

                return (inherited, allowMultiple);
            }
        }

        return (true, false);
    }

    /// <summary>The declaring type of the method that <paramref name="method" /> binds (an extension in its static form).</summary>
    internal static INamedTypeSymbol? DeclaringType(IMethodSymbol method)
    {
        return (method.ReducedFrom ?? method).OriginalDefinition.ContainingType;
    }
}
