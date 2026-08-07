using Microsoft.CodeAnalysis;
using Lurp.Shared;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class PolymorphismExtractionContext : ExtractionContextBase
{
    internal PolymorphismExtractionContext(Compilation compilation, string snapshotId, string gitRoot, IReadOnlySet<string>? scopeDocuments = null, BindingIncompletenessCollector? incompleteness = null, Dictionary<SyntaxTree, SemanticModel>? semanticModelCache = null, IEnumerable<string>? documentPaths = null, IEnumerable<string>? generatedDocumentPaths = null)
        : base(compilation, snapshotId, gitRoot, scopeDocuments, incompleteness, semanticModelCache, documentPaths, generatedDocumentPaths)
    {
    }

    internal EdgeRecord MakeMayDispatchEdge(string sourceId, string targetId, ISymbol targetSymbol, string provenance, string? typeArgumentsJson = null)
    {
        int? startLine = null;
        int? startColumn = null;
        int? endLine = null;
        int? endColumn = null;

        var syntaxRef = EdgeLocationResolver.PrimaryDeclaration(targetSymbol);
        if (syntaxRef != null)
        {
            var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
            startLine = span.StartLinePosition.Line;
            startColumn = span.StartLinePosition.Character;
            endLine = span.EndLinePosition.Line;
            endColumn = span.EndLinePosition.Character;
        }

        var sourceDocumentPath = GetDocumentPath(targetSymbol);

        return new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = EdgeKind.MayDispatchTo.ToString(),
            Provenance = provenance,
            SnapshotId = SnapshotId,
            ExtractorVersion = ExtractorConstants.PolymorphismExtractor,
            SourceDocumentPath = sourceDocumentPath,
            SourceStartLine = startLine,
            SourceStartColumn = startColumn,
            SourceEndLine = endLine,
            SourceEndColumn = endColumn,
            TypeArgumentsJson = typeArgumentsJson,
            IsCrossGenerated = IsGenerated(sourceDocumentPath),
        };
    }

    internal string? GetDocumentPath(ISymbol symbol)
    {
        var syntaxRef = EdgeLocationResolver.PrimaryDeclaration(symbol);
        if (syntaxRef == null)
            return null;
        var path = syntaxRef.SyntaxTree?.FilePath;
        if (string.IsNullOrEmpty(path))
            return null;
        return DocumentChangeDetector.GetRelativePath(path, _gitRoot);
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
