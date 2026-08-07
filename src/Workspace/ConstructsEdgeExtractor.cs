using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class ConstructsEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
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

            IndexTrace.TreeWalk("MemberEdge", "ConstructsEdgeExtractor", methodSyntax.SyntaxTree.FilePath);

            var semanticModel = context.GetOrCreateSemanticModel(methodSyntax.SyntaxTree);
            var callerId = context.MakeSymbolId(methodSymbol);
            if (callerId == null)
                continue;

            var creations = bodySyntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();

            foreach (var creation in creations)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(creation);
                if (symbolInfo.Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
                {
                    context.RecordUnresolvedBinding(symbolInfo, creation, semanticModel);
                    continue;
                }

                if (ctor.MethodKind == MethodKind.Constructor)
                {
                    context.RecordFilteredExternal(ctor, creation);

                    // Implicit default constructors are never emitted as declarations (the
                    // declaration extractor skips IsImplicitlyDeclared members), so an edge to
                    // the phantom .#ctor would orphan on cleanup and the construction fact
                    // would be lost. Anchor it to the containing type instead, which IS
                    // declared. Explicit constructors keep pointing at the constructor.
                    var target = ctor.IsImplicitlyDeclared ? (ISymbol)ctor.ContainingType : ctor;
                    var targetId = context.MakeSymbolId(target);
                    if (targetId == null)
                        continue;

                    var key = (callerId, targetId, EdgeKind.Constructs.ToString());
                    if (!seen.Add(key))
                        continue;

                    var loc = context.GetLocationInfo(creation.GetLocation());
                    edges.Add(context.MakeEdge(callerId, targetId, EdgeKind.Constructs.ToString(),
                        ExtractorConstants.ConstructsExtractor, loc));
                }
            }
        }

        return edges;
    }
}
