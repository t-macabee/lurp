using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class UnknownPatternReflectionExtractor(ReflectionExtractionContext context)
{
    internal List<EdgeRecord> Extract(SyntaxNode root, SemanticModel semanticModel)
    {
        var edges = new List<EdgeRecord>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var sink = ReflectionSinkPatterns.Resolve(invocation, semanticModel);
            if (sink == null)
                continue;

            var sourceId = context.OwnerIdOf(invocation);
            if (sourceId == null)
                continue;

            var loc = context.GetLocationInfo(invocation.GetLocation());
            edges.Add(context.MakeEdge(sourceId, sourceId, nameof(EdgeKind.ReflectionTargetUnknown), ExtractorConstants.ReflectionExtractor, loc, Provenance.RuntimeUnknown));
        }

        return edges;
    }
}