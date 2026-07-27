using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class CallsEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
        var semanticModelCache = new Dictionary<SyntaxTree, SemanticModel>();

        foreach (var (methodSymbol, methodSyntax) in context.EnumerateMethodDeclarations())
        {
            var bodySyntax = MemberEdgeExtractionContext.GetMethodBody(methodSyntax);

            if (bodySyntax == null)
                continue;

            var semanticModel = context.GetOrCreateSemanticModel(methodSyntax.SyntaxTree, semanticModelCache);
            var callerId = context.MakeSymbolId(methodSymbol);

            if (callerId == null)
                continue;

            var invocations = bodySyntax.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                AddCallEdge(invocation, semanticModel, callerId, edges, seen);
            }

            // Overloaded operators are represented by BinaryExpressionSyntax,
            // not InvocationExpressionSyntax. Keep built-in operators out of
            // the graph: only a compiler-resolved operator method is a call.
            foreach (var binary in bodySyntax.DescendantNodes().OfType<BinaryExpressionSyntax>())
            {
                AddCallEdge(binary, semanticModel, callerId, edges, seen);
            }

            // User-defined conversions are represented by CastExpressionSyntax
            // and resolve to the conversion operator method.
            foreach (var cast in bodySyntax.DescendantNodes().OfType<CastExpressionSyntax>())
            {
                AddCallEdge(cast, semanticModel, callerId, edges, seen);
            }

            // Indexer access (obj[key]) resolves to the indexer property, not
            // an accessor method. Emit Reads/Writes edges so consumers can
            // distinguish indexer binding from ordinary method invocations.
            foreach (var elementAccess in bodySyntax.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
            {
                AddIndexerEdge(elementAccess, semanticModel, callerId, edges, seen);
            }
        }

        return edges;
    }

    private void AddIndexerEdge(
        ElementAccessExpressionSyntax elementAccess,
        SemanticModel semanticModel,
        string callerId,
        List<EdgeRecord> edges,
        HashSet<(string source, string target, string kind)> seen)
    {
        if (semanticModel.GetSymbolInfo(elementAccess).Symbol is not IPropertySymbol { IsIndexer: true } indexer)
            return;

        var indexerId = context.MakeSymbolId(indexer);
        if (indexerId == null || indexerId == callerId)
            return;

        var isWrite = IsWriteContext(elementAccess);
        var kind = isWrite ? EdgeKind.Writes.ToString() : EdgeKind.Reads.ToString();

        if (!seen.Add((callerId, indexerId, kind)))
            return;

        var location = context.GetLocationInfo(elementAccess.GetLocation());
        edges.Add(context.MakeEdge(callerId, indexerId, kind, ExtractorConstants.CallsExtractor, location));
    }

    private static bool IsWriteContext(SyntaxNode node)
    {
        if (node.Parent is AssignmentExpressionSyntax assign)
            return assign.Left == node;

        if (node.Parent is PrefixUnaryExpressionSyntax preUnary &&
            (preUnary.IsKind(SyntaxKind.PreIncrementExpression) ||
             preUnary.IsKind(SyntaxKind.PreDecrementExpression)))
        {
            return preUnary.Operand == node;
        }

        if (node.Parent is PostfixUnaryExpressionSyntax postUnary &&
            (postUnary.IsKind(SyntaxKind.PostIncrementExpression) ||
             postUnary.IsKind(SyntaxKind.PostDecrementExpression)))
        {
            return postUnary.Operand == node;
        }

        if (node.Parent is ArgumentSyntax arg &&
            (arg.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) ||
             arg.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)))
        {
            return true;
        }

        return false;
    }

    private void AddCallEdge(
        SyntaxNode syntax,
        SemanticModel semanticModel,
        string callerId,
        List<EdgeRecord> edges,
        HashSet<(string source, string target, string kind)> seen)
    {
        if (semanticModel.GetSymbolInfo(syntax).Symbol is not IMethodSymbol callee ||
            callee.MethodKind == MethodKind.AnonymousFunction)
            return;

        var calleeId = context.MakeSymbolId(callee);
        if (calleeId == null || calleeId == callerId)
            return;

        var kind = EdgeKind.Calls.ToString();
        if (!seen.Add((callerId, calleeId, kind)))
            return;

        var location = context.GetLocationInfo(syntax.GetLocation());
        edges.Add(context.MakeEdge(callerId, calleeId, kind, ExtractorConstants.CallsExtractor, location));
    }
}
