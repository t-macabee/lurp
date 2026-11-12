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
        var seen = new HashSet<(string source, string pattern, string argument)>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var sink = ReflectionSinkPatterns.Resolve(invocation, semanticModel);
            if (sink == null)
                continue;

            var sourceId = context.OwnerIdOf(invocation);
            if (sourceId == null)
                continue;

            var argumentString = GetFirstStringLiteralArgument(invocation);

            var key = (sourceId, sink.Value.Sink.Pattern, argumentString ?? "");
            if (!seen.Add(key))
                continue;

            var loc = context.GetLocationInfo(invocation.GetLocation());
            edges.Add(context.MakeEdge(sourceId, sourceId, nameof(EdgeKind.ReflectionTargetUnknown), ExtractorConstants.ReflectionExtractor, loc, Provenance.RuntimeUnknown));
        }

        return edges;
    }

    private static string? GetFirstStringLiteralArgument(InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count == 0)
            return null;

        var firstArg = invocation.ArgumentList.Arguments[0].Expression;
        if (firstArg is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
            return lit.Token.ValueText;

        return null;
    }
}