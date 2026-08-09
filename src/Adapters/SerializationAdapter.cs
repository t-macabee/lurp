using Lurp.Shared;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class SerializationAdapter : IFrameworkAdapter
{
    public string Name => "Serialization";
    public string Version => "serialization-v1";
    public string Description => "Serialization framework edges (JSON/XML contracts)";

    public AdapterExtractionResult Extract(AdapterExtractionContext context)
    {
        var compilation = context.Compilation;
        var snapshotId = context.SnapshotId;
        var locationResolver = context.LocationResolver;
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
        var assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
        var ctx = new ExtractionContext(assemblyIdentity, snapshotId, Version, edges, seen, locationResolver);

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!context.IsInScope(tree))
                continue;

            var semanticModel = context.GetSemanticModel(tree);

            foreach (var property in tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                ProcessMemberWithSerializationAttrs(property, property.AttributeLists, semanticModel, ctx);
            }

            foreach (var field in tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    ProcessMemberWithSerializationAttrs(variable, field.AttributeLists, semanticModel, ctx);
                }
            }

            foreach (var typeDecl in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                ProcessJsonSerializableType(typeDecl, semanticModel, ctx);
            }
        }

        return edges;
    }

    private static void ProcessJsonSerializableType(TypeDeclarationSyntax typeDecl, SemanticModel semanticModel, ExtractionContext ctx)
    {
        foreach (var attrList in typeDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = GetAttributeName(attr);
                if (attrName != "JsonSerializable" || attr.ArgumentList == null)
                    continue;

                foreach (var arg in attr.ArgumentList.Arguments)
                {
                    if (arg.Expression is not TypeOfExpressionSyntax typeofExpr)
                        continue;

                    var typeInfo = semanticModel.GetTypeInfo(typeofExpr.Type);
                    if (typeInfo.Type is not INamedTypeSymbol serializableType)
                        continue;

                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl);
                    if (typeSymbol == null)
                        continue;

                    var sourceId = SymbolIdFactory.Make(typeSymbol, ctx.AssemblyIdentity);
                    var targetId = SymbolIdFactory.Make(serializableType, ctx.AssemblyIdentity);
                    if (sourceId != null && targetId != null)
                    {
                        var key = (sourceId, targetId, EdgeKind.References.ToString());
                        if (ctx.Seen.Add(key))
                            ctx.Edges.Add(MakeEdge(sourceId, targetId, EdgeKind.References.ToString(), ctx, typeDecl.GetLocation()));
                    }
                }
            }
        }
    }

    private static void ProcessMemberWithSerializationAttrs(SyntaxNode memberNode, SyntaxList<AttributeListSyntax> attributeLists, SemanticModel semanticModel, ExtractionContext ctx)
    {

        ISymbol? memberSymbol = memberNode switch
        {
            PropertyDeclarationSyntax prop => semanticModel.GetDeclaredSymbol(prop),
            VariableDeclaratorSyntax variable => semanticModel.GetDeclaredSymbol(variable) as IFieldSymbol,
            _ => null
        };

        if (memberSymbol == null)
            return;

        var memberId = SymbolIdFactory.Make(memberSymbol, ctx.AssemblyIdentity);
        if (memberId == null)
            return;

        ITypeSymbol? memberType = memberSymbol switch
        {
            IPropertySymbol prop => prop.Type,
            IFieldSymbol field => field.Type,
            _ => null
        };

        string? targetId = null;
        if (memberType is INamedTypeSymbol namedType)
        {
            targetId = SymbolIdFactory.Make(namedType, ctx.AssemblyIdentity);
        }

        // Resolve location from the syntax node (property/field decl) : the evidence site
        var evidenceLocation = memberNode.GetLocation();

        foreach (var attrList in attributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = GetAttributeName(attr);
                var classification = ClassifySerializationAttribute(attrName);
                if (!classification)
                    continue;

                EmitSerializationReferenceEdge(memberId, targetId, ctx, evidenceLocation);
            }
        }
    }

    private static bool ClassifySerializationAttribute(string attrName) => attrName switch
    {
        "JsonPropertyName" or "JsonProperty" or "DataMember" or "JsonIgnore" or "IgnoreDataMember" => true,
        _ => false
    };

    private static void EmitSerializationReferenceEdge(string memberId, string? targetId, ExtractionContext ctx, Location evidenceLocation)
    {
        if (targetId != null)
        {
            var key = (memberId, targetId, EdgeKind.References.ToString());
            if (ctx.Seen.Add(key))
            {
                ctx.Edges.Add(MakeEdge(memberId, targetId, EdgeKind.References.ToString(),
                    ctx, evidenceLocation));
            }
        }
    }

    private static string GetAttributeName(AttributeSyntax attr)
    {
        var name = attr.Name.ToString();

        if (name.EndsWith("Attribute", StringComparison.Ordinal))
            name = name[..^"Attribute".Length];
        return name;
    }

    private static EdgeRecord MakeEdge(string sourceId, string targetId, string kind, ExtractionContext ctx, Location evidenceLocation)
    {
        var (path, sl, sc, el, ec) = ctx.LocationResolver.Resolve(evidenceLocation);

        return new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = kind,
            Provenance = Provenance.FrameworkDerived,
            SnapshotId = ctx.SnapshotId,
            ExtractorVersion = "serialization-v1",
            SourceDocumentPath = path,
            SourceStartLine = sl,
            SourceStartColumn = sc,
            SourceEndLine = el,
            SourceEndColumn = ec,
            IsCrossGenerated = ctx.LocationResolver.IsGenerated(path),
        };
    }
}
