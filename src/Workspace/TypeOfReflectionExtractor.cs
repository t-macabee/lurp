using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class TypeOfReflectionExtractor(ReflectionExtractionContext context)
{
    internal List<EdgeRecord> Extract(SyntaxNode root, SemanticModel semanticModel)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

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

            var sourceId = context.GetContainingMemberSymbolId(typeOfExpr, semanticModel);
            if (sourceId == null)
                continue;

            var key = (sourceId, targetId, nameof(EdgeKind.ReflectionTypeRef));
            if (!seen.Add(key))
                continue;

            var loc = context.GetLocationInfo(typeOfExpr.GetLocation());
            edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = targetId,
                Kind = nameof(EdgeKind.ReflectionTypeRef),
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

        return edges;
    }
}