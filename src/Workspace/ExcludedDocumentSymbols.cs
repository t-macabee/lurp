using Lurp.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lurp.Workspace;

/// <summary>
///     Collects symbol IDs declared only in excluded <c>bin/</c>/<c>obj/</c> compile
///     items (D4, D167.5) so <see cref="SqliteIndexStore.DeleteOrphanEdges" /> buckets
///     orphan edges touching them under <c>excluded_document</c> instead of
///     <c>other</c>. Runs once per index run (full or incremental) across all
///     projects with excluded documents.
/// </summary>
internal static class ExcludedDocumentSymbols
{
    public static async Task<HashSet<string>> CollectAsync(Solution solution, WorkspaceInfo workspaceInfo, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            if (!workspaceInfo.ExcludedDocuments.ContainsKey(project.Id)) continue;
            var compilation = await CompilationHelper.GetCompilationAsync(project, cancellationToken);
            if (compilation != null)
                result.UnionWith(await CollectForProjectAsync(project, compilation, workspaceInfo, cancellationToken));
        }
        return result;
    }

    /// <summary>Unions one project's excluded symbols into its name bucket; projects can share a name.</summary>
    public static async Task CollectIntoAsync(
        Dictionary<string, HashSet<string>> excludedByProject, Project project, Compilation compilation,
        WorkspaceInfo workspaceInfo, CancellationToken cancellationToken)
    {
        var collected = await CollectForProjectAsync(project, compilation, workspaceInfo, cancellationToken);
        if (excludedByProject.TryGetValue(project.Name, out var bucket))
            bucket.UnionWith(collected);
        else
            excludedByProject[project.Name] = collected;
    }

    public static async Task<HashSet<string>> CollectForProjectAsync(
        Project project, Compilation compilation, WorkspaceInfo workspaceInfo, CancellationToken cancellationToken)
    {
        var excludedIds = new HashSet<string>(StringComparer.Ordinal);
        var excludedPaths = workspaceInfo.GetExcludedDocuments(project, project.Name);
        var normalizedRoot = PathNormalizer.NormalizeRoot(workspaceInfo.Id.GitRoot);
        var assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
        var excludedSet = new HashSet<string>(excludedPaths, PathNormalizer.PathComparer);
        var visitedTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(tree.FilePath))
                continue;

            var relPath = PathNormalizer.ToGitRelativeFromNormalizedRoot(tree.FilePath, normalizedRoot);
            if (!excludedSet.Contains(relPath))
                continue;

            var semanticModel = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(cancellationToken);
            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                        if (semanticModel.GetDeclaredSymbol(node, cancellationToken) is INamedTypeSymbol typeSymbol)
                            CollectTypeAndMembers(typeSymbol, assemblyIdentity, workspaceInfo, normalizedRoot, visitedTypes, excludedIds);
                        break;
                    case BaseFieldDeclarationSyntax fieldDecl:
                        foreach (var variable in fieldDecl.Declaration.Variables)
                        {
                            var fieldSymbol = semanticModel.GetDeclaredSymbol(variable, cancellationToken);
                            TryAddSymbol(fieldSymbol, assemblyIdentity, workspaceInfo, normalizedRoot, excludedIds);
                        }
                        break;
                    case AccessorDeclarationSyntax accessorDecl:
                        TryAddSymbol(semanticModel.GetDeclaredSymbol(accessorDecl, cancellationToken), assemblyIdentity, workspaceInfo, normalizedRoot, excludedIds);
                        break;
                    case MemberDeclarationSyntax memberDecl when memberDecl is not BaseNamespaceDeclarationSyntax:
                        var memberSymbol = semanticModel.GetDeclaredSymbol(memberDecl, cancellationToken);
                        TryAddSymbol(memberSymbol, assemblyIdentity, workspaceInfo, normalizedRoot, excludedIds);
                        break;
                }
            }
        }
        return excludedIds;
    }

    private static void CollectTypeAndMembers(
        INamedTypeSymbol typeSymbol,
        string assemblyIdentity,
        WorkspaceInfo workspaceInfo,
        string normalizedRoot,
        HashSet<INamedTypeSymbol> visitedTypes,
        HashSet<string> excludedIds)
    {
        var originalType = typeSymbol.OriginalDefinition;
        if (!visitedTypes.Add(originalType))
            return;

        TryAddSymbol(originalType, assemblyIdentity, workspaceInfo, normalizedRoot, excludedIds);

        foreach (var member in originalType.GetMembers())
        {
            if (member is INamedTypeSymbol nestedType)
                CollectTypeAndMembers(nestedType, assemblyIdentity, workspaceInfo, normalizedRoot, visitedTypes, excludedIds);
            else
                TryAddSymbol(member, assemblyIdentity, workspaceInfo, normalizedRoot, excludedIds);
        }
    }

    private static void TryAddSymbol(
        ISymbol? symbol,
        string assemblyIdentity,
        WorkspaceInfo workspaceInfo,
        string normalizedRoot,
        HashSet<string> excludedIds)
    {
        if (symbol == null)
            return;

        if (HasSnapshotDeclaration(symbol, workspaceInfo, normalizedRoot))
            return;

        var id = SymbolIdFactory.Make(symbol, assemblyIdentity);
        if (id != null)
            excludedIds.Add(id);
    }

    private static bool HasSnapshotDeclaration(ISymbol symbol, WorkspaceInfo workspaceInfo, string normalizedRoot)
    {
        var original = symbol.OriginalDefinition;
        var refs = original.DeclaringSyntaxReferences;
        if (refs.IsEmpty && original.ContainingType != null)
            refs = original.ContainingType.OriginalDefinition.DeclaringSyntaxReferences;

        foreach (var syntaxRef in refs)
        {
            var filePath = syntaxRef.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(filePath))
                continue;

            var relPath = PathNormalizer.ToGitRelativeFromNormalizedRoot(filePath, normalizedRoot);
            if (workspaceInfo.Documents.ContainsKey(new DocumentId(relPath)))
                return true;
        }

        return false;
    }
}
