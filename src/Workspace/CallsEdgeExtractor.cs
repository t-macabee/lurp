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
        var callEdges = new Dictionary<(string source, string target, string kind), EdgeRecord>();

        foreach (var (methodSymbol, methodSyntax) in context.EnumerateMethodDeclarations())
        {
            var bodySyntax = MemberEdgeExtractionContext.GetMethodBody(methodSyntax);

            if (bodySyntax == null)
                continue;

            var semanticModel = context.GetOrCreateSemanticModel(methodSyntax.SyntaxTree);
            var callerId = context.MakeSymbolId(methodSymbol);

            if (callerId == null)
                continue;

            foreach (var node in bodySyntax.DescendantNodes())
                switch (node)
                {
                    case InvocationExpressionSyntax invocation:
                        AddCallEdge(invocation, semanticModel, methodSymbol, callerId, edges, seen, callEdges);
                        break;
                    case BinaryExpressionSyntax binary:
                        AddCallEdge(binary, semanticModel, methodSymbol, callerId, edges, seen, callEdges);
                        break;
                    case CastExpressionSyntax cast:
                        AddCallEdge(cast, semanticModel, methodSymbol, callerId, edges, seen, callEdges);
                        break;
                    case ElementAccessExpressionSyntax elementAccess:
                        AddIndexerEdge(elementAccess, semanticModel, callerId, edges, seen);
                        break;
                }
        }

        edges.AddRange(callEdges.Values);
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
                context.RecordUnresolvedBinding(symbolInfo, elementAccess, semanticModel);
            return;
        }

        context.RecordFilteredExternal(indexer, elementAccess);

        var indexerId = context.MakeSymbolId(indexer);
        if (indexerId == null || indexerId == callerId)
            return;

        var isWrite = elementAccess.IsWriteContext();
        var kind = isWrite ? EdgeKind.Writes.ToString() : EdgeKind.Reads.ToString();

        if (!seen.Add((callerId, indexerId, kind)))
            return;

        var location = context.GetLocationInfo(elementAccess.GetLocation());
        edges.Add(context.MakeEdge(callerId, indexerId, kind, ExtractorConstants.CallsExtractor, location));
    }

    private void AddCallEdge(
        SyntaxNode syntax,
        SemanticModel semanticModel,
        IMethodSymbol caller,
        string callerId,
        List<EdgeRecord> edges,
        HashSet<(string source, string target, string kind)> seen,
        Dictionary<(string source, string target, string kind), EdgeRecord> callEdges)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(syntax);
        if (symbolInfo.Symbol is not IMethodSymbol callee)
        {
            // nameof(...) is an InvocationExpressionSyntax with no method symbol, but it
            // is not an unresolved call: NameOfReflectionExtractor emits a
            // ReflectionMemberRef edge for it. Recording it as unsupported_syntax would
            // falsely mark the document's binding region incomplete.
            if (syntax is InvocationExpressionSyntax nameOfInvocation &&
                NameOfReflectionExtractor.IsNameOfInvocation(nameOfInvocation))
                return;

            if (syntax is InvocationExpressionSyntax || symbolInfo.CandidateReason != CandidateReason.None || symbolInfo.CandidateSymbols.Length > 0)
                context.RecordUnresolvedBinding(symbolInfo, syntax, semanticModel);
            return;
        }

        if (callee.MethodKind == MethodKind.AnonymousFunction)
            return;

        // Local functions cannot be invoked outside their lexical method, so a
        // Calls edge to one carries no inter-symbol graph information. They are
        // also never declared (SymbolDeclarationExtractor walks GetMembers only),
        // so the edge would orphan out and be deleted by DeleteOrphanEdges.
        if (callee.MethodKind == MethodKind.LocalFunction)
            return;

        context.RecordFilteredExternal(callee, syntax);

        var location = context.GetLocationInfo(syntax.GetLocation());

        if (syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess } &&
            semanticModel.GetSymbolInfo(memberAccess.Expression).Symbol is INamedTypeSymbol containingType)
        {
            var containingTypeId = context.MakeSymbolId(containingType);
            if (containingTypeId != null)
            {
                var refKind = EdgeKind.References.ToString();
                if (seen.Add((callerId, containingTypeId, refKind)))
                    edges.Add(context.MakeEdge(callerId, containingTypeId, refKind,
                        ExtractorConstants.CallsExtractor, location));
            }
        }

        var calleeId = context.MakeSymbolId(callee);
        if (calleeId == null || calleeId == callerId)
            return;

        var kind = EdgeKind.Calls.ToString();
        var receiverConstraints = GetReceiverTypeConstraints(syntax, callee, caller, semanticModel);
        var key = (callerId, calleeId, kind);
        if (callEdges.TryGetValue(key, out var existingCall))
        {
            callEdges[key] = ReceiverTypeConstraints.WithMergedConstraints(existingCall, receiverConstraints);
        }
        else
        {
            var call = context.MakeEdge(callerId, calleeId, kind, ExtractorConstants.CallsExtractor, location);
            callEdges.Add(key, ReceiverTypeConstraints.WithMergedConstraints(call, receiverConstraints));
        }

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
                            edges.Add(context.MakeEdge(receiverTypeId, calleeId, extKind,
                                ExtractorConstants.ExtensionReceiverExtractor, location));
                    }
                }
            }
        }
    }

    private string? GetReceiverTypeConstraints(
        SyntaxNode syntax,
        IMethodSymbol callee,
        IMethodSymbol caller,
        SemanticModel semanticModel)
    {
        if (syntax is not InvocationExpressionSyntax invocation ||
            (callee.IsStatic && callee.ReducedFrom == null))
            return null;

        var receiverType = invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } => null,
            MemberAccessExpressionSyntax memberAccess
                when semanticModel.GetSymbolInfo(memberAccess.Expression).Symbol is INamedTypeSymbol => null,
            MemberAccessExpressionSyntax memberAccess => semanticModel.GetTypeInfo(memberAccess.Expression).Type,
            MemberBindingExpressionSyntax => invocation.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is { } conditional
                ? semanticModel.GetTypeInfo(conditional.Expression).Type
                : null,
            IdentifierNameSyntax or GenericNameSyntax when !caller.IsStatic => caller.ContainingType,
            _ => null
        };

        return ReceiverTypeConstraints.FromReceiverType(receiverType, context);
    }
}