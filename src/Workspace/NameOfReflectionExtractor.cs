using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class NameOfReflectionExtractor(ReflectionExtractionContext context)
{
    internal List<EdgeRecord> Extract(SyntaxNode root, SemanticModel semanticModel)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!IsNameOfInvocation(invocation))
                continue;

            EmitNameOfEdge(invocation, semanticModel, edges, seen);
        }

        return edges;
    }

    internal static bool IsNameOfInvocation(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is IdentifierNameSyntax identifier
               && identifier.Identifier.Text.Equals("nameof", StringComparison.Ordinal)
               && invocation.ArgumentList.Arguments.Count == 1;
    }

    /// <summary>
    ///     True when <paramref name="node" /> is part of a <c>nameof</c> argument. A
    ///     <c>nameof</c> is a compile-time name, not a read or a write, so every
    ///     Reads/Writes producer skips it; this extractor records the names instead.
    /// </summary>
    internal static bool IsInsideNameOf(SyntaxNode node)
    {
        return node.Ancestors().OfType<InvocationExpressionSyntax>().Any(IsNameOfInvocation);
    }

    private void EmitNameOfEdge(InvocationExpressionSyntax invocation, SemanticModel semanticModel, List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        var sourceId = context.GetContainingMemberSymbolId(invocation, semanticModel);
        if (sourceId == null)
            return;

        var argument = invocation.ArgumentList.Arguments[0].Expression;
        var symbolInfo = semanticModel.GetSymbolInfo(argument);
        var resolvedSymbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

        if (resolvedSymbol is not { CanBeReferencedByName: true })
        {
            if (resolvedSymbol == null)
                context.RecordUnresolvedBinding(symbolInfo, argument, semanticModel);
            return;
        }

        context.RecordFilteredExternal(resolvedSymbol, argument);
        AddEdge(sourceId, resolvedSymbol, invocation, edges, seen);

        // R147: a nameof names every symbol in its chain at compile time. The
        // argument's own symbol is emitted above; the receivers of its member access
        // (`_holder` in `nameof(_holder.Value)`) are named too, so each one is a
        // ReflectionMemberRef rather than a read.
        for (var receiver = ReceiverOf(argument); receiver != null; receiver = ReceiverOf(receiver))
        {
            var receiverSymbol = semanticModel.GetSymbolInfo(receiver).Symbol;
            if (receiverSymbol is not (IFieldSymbol or IPropertySymbol or IEventSymbol))
                continue;

            context.RecordFilteredExternal(receiverSymbol, receiver);
            AddEdge(sourceId, receiverSymbol, invocation, edges, seen);
        }
    }

    private static ExpressionSyntax? ReceiverOf(ExpressionSyntax expression)
    {
        return expression is MemberAccessExpressionSyntax memberAccess ? memberAccess.Expression : null;
    }

    private void AddEdge(string sourceId, ISymbol targetSymbol, InvocationExpressionSyntax invocation, List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        var targetId = context.MakeSymbolId(targetSymbol);
        if (targetId == null)
            return;

        var key = (sourceId, targetId, nameof(EdgeKind.ReflectionMemberRef));
        if (!seen.Add(key))
            return;

        var loc = context.GetLocationInfo(invocation.GetLocation());
        edges.Add(new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = nameof(EdgeKind.ReflectionMemberRef),
            Provenance = Provenance.CompilerProved,
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
}