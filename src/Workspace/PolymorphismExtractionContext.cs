using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class PolymorphismExtractionContext(ProjectExtractionInputs inputs) : ExtractionContextBase(inputs)
{
    internal EdgeRecord MakeMayDispatchEdge(string sourceId, string targetId, ISymbol targetSymbol, string provenance, string? typeArgumentsJson = null)
    {
        return MakeEdge(sourceId, targetId, nameof(EdgeKind.MayDispatchTo), ExtractorConstants.PolymorphismExtractor,
            Resolve(targetSymbol), provenance, typeArgumentsJson);
    }

    internal bool IsTypeInScope(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.DeclaringSyntaxReferences.Any(r => IsInScope(r.SyntaxTree));
    }
}
