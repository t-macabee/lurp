using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class ThrowsEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var (methodSymbol, methodSyntax) in context.EnumerateMethodDeclarations())
        {
            var bodySyntax = MemberEdgeExtractionContext.GetMethodBody(methodSyntax);
            if (bodySyntax == null)
                continue;

            var semanticModel = context.GetOrCreateSemanticModel(methodSyntax.SyntaxTree);
            var callerId = context.MakeSymbolId(methodSymbol);
            if (callerId == null)
                continue;

            foreach (var (thrownExpression, location) in EnumerateThrownExpressions(bodySyntax)) TryAddThrowEdge(edges, seen, callerId, thrownExpression, location, semanticModel);
        }

        return edges;
    }

    private static IEnumerable<(ExpressionSyntax Expression, Location Location)> EnumerateThrownExpressions(SyntaxNode bodySyntax)
    {
        foreach (var node in bodySyntax.DescendantNodes())
            switch (node)
            {
                case ThrowStatementSyntax { Expression: not null } throwStatement:
                    yield return (throwStatement.Expression, throwStatement.GetLocation());
                    break;
                case ThrowExpressionSyntax throwExpression:
                    yield return (throwExpression.Expression, throwExpression.GetLocation());
                    break;
            }
    }

    private void TryAddThrowEdge(List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen,
        string callerId, ExpressionSyntax thrownExpression, Location location, SemanticModel semanticModel)
    {
        var exceptionType = ResolveThrownType(thrownExpression, semanticModel);
        if (exceptionType == null)
        {
            context.RecordUnresolvedBinding(thrownExpression, semanticModel);
            return;
        }

        context.RecordFilteredExternal(exceptionType, thrownExpression);

        var typeId = context.MakeSymbolId(exceptionType);
        if (typeId == null)
            return;

        var key = (callerId, typeId, nameof(EdgeKind.Throws));
        if (!seen.Add(key))
            return;

        var loc = context.GetLocationInfo(location);
        edges.Add(context.MakeEdge(callerId, typeId, nameof(EdgeKind.Throws), ExtractorConstants.ThrowsExtractor, loc));
    }

    private static INamedTypeSymbol? ResolveThrownType(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        if (expression is ObjectCreationExpressionSyntax creation)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(creation);
            if (symbolInfo.Symbol is IMethodSymbol ctor)
                return ctor.ContainingType;
        }

        var typeInfo = semanticModel.GetTypeInfo(expression);
        return typeInfo.Type as INamedTypeSymbol;
    }
}