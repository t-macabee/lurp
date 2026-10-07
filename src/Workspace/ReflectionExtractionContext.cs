using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lurp.Workspace;

internal sealed class ReflectionExtractionContext : ExtractionContextBase
{
    internal ReflectionExtractionContext(Compilation compilation, string snapshotId, string gitRoot, IReadOnlySet<string>? scopeDocuments = null, BindingIncompletenessCollector? incompleteness = null,
        Dictionary<SyntaxTree, SemanticModel>? semanticModelCache = null, IEnumerable<string>? documentPaths = null, IEnumerable<string>? generatedDocumentPaths = null)
        : base(compilation, snapshotId, gitRoot, scopeDocuments, incompleteness, semanticModelCache, documentPaths, generatedDocumentPaths)
    {
        TypesByName = new Dictionary<string, List<ISymbol>>(StringComparer.OrdinalIgnoreCase);
        MembersByName = new Dictionary<string, List<ISymbol>>(StringComparer.OrdinalIgnoreCase);
        CollectKnownNames(compilation.Assembly.GlobalNamespace, TypesByName, MembersByName);
    }

    internal Dictionary<string, List<ISymbol>> TypesByName { get; }
    internal Dictionary<string, List<ISymbol>> MembersByName { get; }

    internal void RecordUnresolvedBinding(SymbolInfo symbolInfo, SyntaxNode node, SemanticModel semanticModel)
    {
        Incompleteness?.RecordUnresolved(symbolInfo, node, semanticModel);
    }

    internal void RecordUnresolvedBinding(SyntaxNode node, SemanticModel semanticModel)
    {
        Incompleteness?.RecordUnresolved(node, semanticModel);
    }

    internal string? GetContainingMemberSymbolId(SyntaxNode node, SemanticModel semanticModel)
    {
        return GetContainingMemberSymbol(node, semanticModel) is { } memberSymbol
            ? MakeSymbolId(memberSymbol)
            : null;
    }

    internal static ISymbol? GetContainingMemberSymbol(SyntaxNode node, SemanticModel semanticModel)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            ISymbol? memberSymbol = null;

            switch (current)
            {
                case MethodDeclarationSyntax:
                    memberSymbol = semanticModel.GetDeclaredSymbol(current) as IMethodSymbol;
                    break;
                case PropertyDeclarationSyntax:
                    memberSymbol = semanticModel.GetDeclaredSymbol(current) as IPropertySymbol;
                    break;
                case ConstructorDeclarationSyntax:
                    memberSymbol = semanticModel.GetDeclaredSymbol(current) as IMethodSymbol;
                    break;
                case FieldDeclarationSyntax fieldDecl:
                    var firstVariable = fieldDecl.Declaration.Variables.FirstOrDefault();
                    if (firstVariable != null) memberSymbol = semanticModel.GetDeclaredSymbol(firstVariable) as IFieldSymbol;
                    break;
            }

            if (memberSymbol != null)
                return memberSymbol;
        }

        return null;
    }

    private static void CollectKnownNames(INamespaceSymbol ns, Dictionary<string, List<ISymbol>> typesByName, Dictionary<string, List<ISymbol>> membersByName)
    {
        foreach (var type in ExtractionUtils.GetAllNamedTypes(ns))
        {
            AddSymbol(typesByName, type.Name, type);
            foreach (var member in type.GetMembers())
            {
                AddSymbol(membersByName, member.Name, member);
            }
        }
    }

    private static void AddSymbol(Dictionary<string, List<ISymbol>> dict, string name, ISymbol symbol)
    {
        if (!dict.TryGetValue(name, out var list))
        {
            list = [];
            dict.Add(name, list);
        }

        list.Add(symbol);
    }
}