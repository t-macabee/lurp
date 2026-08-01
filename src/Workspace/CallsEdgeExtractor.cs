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

            foreach (var node in bodySyntax.DescendantNodes())
            {
                switch (node)
                {
                    case InvocationExpressionSyntax invocation:
                        AddCallEdge(invocation, semanticModel, callerId, edges, seen);
                        break;
                    case BinaryExpressionSyntax binary:
                        AddCallEdge(binary, semanticModel, callerId, edges, seen);
                        break;
                    case CastExpressionSyntax cast:
                        AddCallEdge(cast, semanticModel, callerId, edges, seen);
                        break;
                    case ElementAccessExpressionSyntax elementAccess:
                        AddIndexerEdge(elementAccess, semanticModel, callerId, edges, seen);
                        break;
                }
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
        var symbolInfo = semanticModel.GetSymbolInfo(elementAccess);
        if (symbolInfo.Symbol is not IPropertySymbol { IsIndexer: true } indexer)
        {
            if (symbolInfo.Symbol == null &&
                (symbolInfo.CandidateReason != CandidateReason.None || symbolInfo.CandidateSymbols.Length > 0 ||
                 semanticModel.GetDiagnostics(elementAccess.Span).Any(static d => d.Severity == DiagnosticSeverity.Error)))
            {
                context.RecordUnresolvedBinding(symbolInfo, elementAccess, semanticModel);
            }
            return;
        }

        context.RecordFilteredExternal(indexer, elementAccess);

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
        var symbolInfo = semanticModel.GetSymbolInfo(syntax);
        if (symbolInfo.Symbol is not IMethodSymbol callee)
        {
            if (syntax is InvocationExpressionSyntax || symbolInfo.CandidateReason != CandidateReason.None || symbolInfo.CandidateSymbols.Length > 0)
                context.RecordUnresolvedBinding(symbolInfo, syntax, semanticModel);
            return;
        }

        if (callee.MethodKind == MethodKind.AnonymousFunction)
            return;

        context.RecordFilteredExternal(callee, syntax);

        var calleeId = context.MakeSymbolId(callee);
        if (calleeId == null || calleeId == callerId)
            return;

        var kind = EdgeKind.Calls.ToString();
        if (!seen.Add((callerId, calleeId, kind)))
            return;

        var location = context.GetLocationInfo(syntax.GetLocation());
        edges.Add(context.MakeEdge(callerId, calleeId, kind, ExtractorConstants.CallsExtractor, location));

        // Extension-method receiver edge: when a method is called with
        // extension-method syntax (e.g. foo.Bar()), emit a distinct edge from
        // the receiver type to the extension method so consumers can trace
        // which concrete types are extended by which methods.
        if (callee.ReducedFrom != null && syntax is InvocationExpressionSyntax invocation)
        {
            var receiverExpr = invocation.Expression is MemberAccessExpressionSyntax ma
                ? ma.Expression
                : null;

            if (receiverExpr != null)
            {
                var receiverType = semanticModel.GetTypeInfo(receiverExpr).Type;
                if (receiverType is INamedTypeSymbol receiverNamedType)
                {
                    var receiverTypeId = context.MakeSymbolId(receiverNamedType);
                    if (receiverTypeId != null)
                    {
                        var extKind = EdgeKind.ExtensionReceiver.ToString();
                        if (seen.Add((receiverTypeId, calleeId, extKind)))
                        {
                            edges.Add(context.MakeEdge(receiverTypeId, calleeId, extKind,
                                ExtractorConstants.ExtensionReceiverExtractor, location));
                        }
                    }
                }
            }
        }
    }
}
