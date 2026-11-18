using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class TypeOfReflectionExtractor(ReflectionExtractionContext context)
{
    internal List<EdgeRecord> Extract(SyntaxNode root, SemanticModel semanticModel)
    {
        var edges = new List<EdgeRecord>();

        foreach (var typeOfExpr in root.DescendantNodes().OfType<TypeOfExpressionSyntax>())
        {
            var typeInfo = semanticModel.GetTypeInfo(typeOfExpr.Type);
            if (typeInfo.Type == null)
            {
                context.RecordUnresolvedBinding(typeOfExpr.Type, semanticModel);
                continue;
            }

            context.RecordFilteredExternal(typeInfo.Type, typeOfExpr);

            var targetId = context.MakeSymbolId(typeInfo.Type);
            if (targetId == null)
                continue;

            var sourceId = context.OwnerIdOf(typeOfExpr);
            if (sourceId == null)
                continue;

            var loc = context.GetLocationInfo(typeOfExpr.GetLocation());
            edges.Add(context.MakeEdge(sourceId, targetId, nameof(EdgeKind.ReflectionTypeRef), ExtractorConstants.ReflectionExtractor, loc));
        }

        return edges;
    }
}