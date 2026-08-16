using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

/// <summary>
///     Walk every method body in the compilation and emit a StaticallyCalls
///     edge for each invocation whose target is a polymorphic dispatch point:
///     an interface member, an abstract member, or a virtual (non-sealed) member.
///     These edges are complementary to the Calls edges emitted by
///     MemberEdgeExtractor : Calls covers every invocation (concrete + dispatch),
///     while this edge explicitly marks the dispatch-point calls so that the
///     graph can be traversed as:
///     Handler.Handle --[statically_calls]--> IRepository.SaveAsync
///     IRepository.SaveAsync --[may_dispatch_to]--> Repository.SaveAsync
/// </summary>
internal sealed class StaticDispatchCallExtractor(PolymorphismExtractionContext context)
{
    internal List<EdgeRecord> Extract(List<INamedTypeSymbol> allTypes)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var (methodSymbol, methodSyntax) in ExtractionUtils.EnumerateMethodDeclarations(allTypes))
        {
            var bodySyntax = ExtractionUtils.GetMethodBody(methodSyntax);
            if (bodySyntax == null)
                continue;

            var semanticModel = context.GetOrCreateSemanticModel(methodSyntax.SyntaxTree);
            var callerId = context.MakeSymbolId(methodSymbol);
            if (callerId == null)
                continue;

            foreach (var invocation in bodySyntax.DescendantNodes().OfType<InvocationExpressionSyntax>()) EmitStaticCallEdge(invocation, semanticModel, callerId, edges, seen);
        }

        return edges;
    }

    private void EmitStaticCallEdge(InvocationExpressionSyntax invocation, SemanticModel semanticModel, string callerId,
        List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        if (symbolInfo.Symbol is not IMethodSymbol callee || callee.MethodKind == MethodKind.AnonymousFunction)
            return;

        // A "dispatch point" is a method whose runtime target requires
        // polymorphic resolution: interface methods, abstract methods,
        // and virtual (non-sealed) methods.
        var isDispatchPoint = callee.ContainingType?.TypeKind == TypeKind.Interface
                              || callee.IsAbstract
                              || (callee.IsVirtual && !callee.IsSealed);

        if (!isDispatchPoint)
            return;

        context.RecordFilteredExternal(callee, invocation);

        var calleeId = context.MakeSymbolId(callee);
        if (calleeId == null || calleeId == callerId)
            return;

        var key = (callerId, calleeId, nameof(EdgeKind.StaticallyCalls));
        if (!seen.Add(key))
            return;

        var loc = context.GetLocationInfo(invocation.GetLocation());
        edges.Add(new EdgeRecord
        {
            SourceSymbolId = callerId,
            TargetSymbolId = calleeId,
            Kind = nameof(EdgeKind.StaticallyCalls),
            Provenance = Provenance.CompilerProved,
            SnapshotId = context.SnapshotId,
            ExtractorVersion = ExtractorConstants.StaticallyCallsExtractor,
            SourceDocumentPath = loc.path,
            SourceStartLine = loc.startLine,
            SourceStartColumn = loc.startColumn,
            SourceEndLine = loc.endLine,
            SourceEndColumn = loc.endColumn,
            IsCrossGenerated = context.IsGenerated(loc.path)
        });
    }
}