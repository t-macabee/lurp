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

            var sourceId = context.GetContainingMemberSymbolId(invocation, semanticModel);
            if (sourceId == null)
                continue;

            var argumentString = GetFirstStringLiteralArgument(invocation);

            var key = (sourceId, sink.Value.Pattern, argumentString ?? "");
            if (!seen.Add(key))
                continue;

            var loc = context.GetLocationInfo(invocation.GetLocation());
            edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = sourceId,
                Kind = nameof(EdgeKind.ReflectionTargetUnknown),
                Provenance = Provenance.RuntimeUnknown,
                SnapshotId = context.SnapshotId,
                ExtractorVersion = ExtractorConstants.ReflectionExtractor,
                SourceDocumentPath = loc.path,
                SourceStartLine = loc.startLine,
                SourceStartColumn = loc.startColumn,
                SourceEndLine = loc.endLine,
                SourceEndColumn = loc.endColumn,
                IsCrossGenerated = context.IsGenerated(loc.path)
            });
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