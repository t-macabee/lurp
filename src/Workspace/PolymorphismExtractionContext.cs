using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class PolymorphismExtractionContext : ExtractionContextBase
{
    internal PolymorphismExtractionContext(Compilation compilation, string snapshotId, string gitRoot, IReadOnlySet<string>? scopeDocuments = null, BindingIncompletenessCollector? incompleteness = null,
        Dictionary<SyntaxTree, SemanticModel>? semanticModelCache = null, IEnumerable<string>? documentPaths = null, IEnumerable<string>? generatedDocumentPaths = null)
        : base(compilation, snapshotId, gitRoot, scopeDocuments, incompleteness, semanticModelCache, documentPaths, generatedDocumentPaths)
    {
    }

    internal EdgeRecord MakeMayDispatchEdge(string sourceId, string targetId, ISymbol targetSymbol, string provenance, string? typeArgumentsJson = null)
    {
        var (path, sl, sc, el, ec) = Resolve(targetSymbol);

        return new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = nameof(EdgeKind.MayDispatchTo),
            Provenance = provenance,
            SnapshotId = SnapshotId,
            ExtractorVersion = ExtractorConstants.PolymorphismExtractor,
            SourceDocumentPath = path,
            SourceStartLine = sl,
            SourceStartColumn = sc,
            SourceEndLine = el,
            SourceEndColumn = ec,
            TypeArgumentsJson = typeArgumentsJson,
            IsCrossGenerated = IsGenerated(path)
        };
    }

    internal bool IsTypeInScope(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.DeclaringSyntaxReferences.Any(r => ExtractionUtils.IsInScope(ScopeDocuments, r.SyntaxTree));
    }

    internal static List<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol ns)
    {
        return ExtractionUtils.GetAllNamedTypes(ns).ToList();
    }
}