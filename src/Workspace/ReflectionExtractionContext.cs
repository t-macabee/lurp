using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lurp.Workspace;

internal sealed class ReflectionExtractionContext : ExtractionContextBase
{
    internal ReflectionExtractionContext(Compilation compilation, string snapshotId, string gitRoot, IReadOnlySet<string>? scopeDocuments = null, BindingIncompletenessCollector? incompleteness = null, Dictionary<SyntaxTree, SemanticModel>? semanticModelCache = null, IEnumerable<string>? documentPaths = null, IEnumerable<string>? generatedDocumentPaths = null)
        : base(compilation, snapshotId, gitRoot, scopeDocuments, incompleteness, semanticModelCache, documentPaths, generatedDocumentPaths)
    {
        KnownTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        KnownMemberNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectKnownNames(compilation.Assembly.GlobalNamespace, KnownTypeNames, KnownMemberNames);
    }

    internal HashSet<string> KnownTypeNames { get; }
    internal HashSet<string> KnownMemberNames { get; }

    internal void RecordUnresolvedBinding(SymbolInfo symbolInfo, SyntaxNode node, SemanticModel semanticModel)
        => Incompleteness?.RecordUnresolved(symbolInfo, node, semanticModel);

    internal void RecordUnresolvedBinding(SyntaxNode node, SemanticModel semanticModel)
        => Incompleteness?.RecordUnresolved(node, semanticModel);

    internal string? GetContainingMemberSymbolId(SyntaxNode node, SemanticModel semanticModel)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            ISymbol? memberSymbol = null;

            if (current is MethodDeclarationSyntax)
            {
                memberSymbol = semanticModel.GetDeclaredSymbol(current) as IMethodSymbol;
            }
            else if (current is PropertyDeclarationSyntax)
            {
                memberSymbol = semanticModel.GetDeclaredSymbol(current) as IPropertySymbol;
            }
            else if (current is ConstructorDeclarationSyntax)
            {
                memberSymbol = semanticModel.GetDeclaredSymbol(current) as IMethodSymbol;
            }
            else if (current is FieldDeclarationSyntax fieldDecl)
            {
                var firstVariable = fieldDecl.Declaration.Variables.FirstOrDefault();
                if (firstVariable != null)
                {
                    memberSymbol = semanticModel.GetDeclaredSymbol(firstVariable) as IFieldSymbol;
                }
            }

            if (memberSymbol != null)
                return MakeSymbolId(memberSymbol);
        }

        return null;
    }

    private static void CollectKnownNames(INamespaceSymbol ns, HashSet<string> typeNames, HashSet<string> memberNames)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            typeNames.Add(type.Name);
            foreach (var member in type.GetMembers())
            {
                memberNames.Add(member.Name);
            }
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            CollectKnownNames(childNs, typeNames, memberNames);
        }
    }
}
