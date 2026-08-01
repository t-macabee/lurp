using Microsoft.CodeAnalysis;
using Lurp.Shared;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class PolymorphismExtractionContext : ExtractionContextBase
{
    internal PolymorphismExtractionContext(Compilation compilation, string snapshotId, string gitRoot, IReadOnlySet<string>? scopeDocuments = null, BindingIncompletenessCollector? incompleteness = null)
        : base(compilation, snapshotId, gitRoot, scopeDocuments, incompleteness)
    {
    }

    internal EdgeRecord MakeMayDispatchEdge(string sourceId, string targetId, ISymbol targetSymbol, string provenance, string? typeArgumentsJson = null)
    {
        return new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = EdgeKind.MayDispatchTo.ToString(),
            Provenance = provenance,
            SnapshotId = SnapshotId,
            ExtractorVersion = ExtractorConstants.PolymorphismExtractor,
            SourceDocumentPath = GetDocumentPath(targetSymbol),
            SourceStartLine = GetStartLine(targetSymbol),
            SourceStartColumn = GetStartColumn(targetSymbol),
            SourceEndLine = GetEndLine(targetSymbol),
            SourceEndColumn = GetEndColumn(targetSymbol),
            TypeArgumentsJson = typeArgumentsJson,
        };
    }

    internal string? GetDocumentPath(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var path = syntaxRef.SyntaxTree?.FilePath;
        if (string.IsNullOrEmpty(path))
            return null;
        return DocumentChangeDetector.GetRelativePath(path, _gitRoot);
    }

    internal static int? GetStartLine(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.StartLinePosition.Line;
    }

    internal static int? GetStartColumn(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.StartLinePosition.Character;
    }

    internal static int? GetEndLine(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.EndLinePosition.Line;
    }

    internal static int? GetEndColumn(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.EndLinePosition.Character;
    }

    internal bool IsTypeInScope(INamedTypeSymbol typeSymbol)
    {
        if (ScopeDocuments == null)
            return true;
        foreach (var syntaxRef in typeSymbol.DeclaringSyntaxReferences)
        {
            var filePath = syntaxRef.SyntaxTree?.FilePath;
            if (filePath != null && ScopeDocuments.Contains(filePath.Replace('\\', '/')))
                return true;
        }
        return false;
    }

    internal static List<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol ns)
    {
        var types = new List<INamedTypeSymbol>();
        CollectTypes(ns, types);
        return types;
    }

    private static void CollectTypes(INamespaceSymbol ns, List<INamedTypeSymbol> types)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            types.Add(type);
            CollectNestedTypes(type, types);
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            CollectTypes(childNs, types);
        }
    }

    private static void CollectNestedTypes(INamedTypeSymbol parent, List<INamedTypeSymbol> types)
    {
        foreach (var nested in parent.GetTypeMembers())
        {
            types.Add(nested);
            CollectNestedTypes(nested, types);
        }
    }
}
