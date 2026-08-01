using Microsoft.CodeAnalysis;
using Lurp.Shared;

namespace Lurp.Workspace;

internal abstract class ExtractionContextBase
{
    private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModelCache = [];
    protected readonly string _gitRoot;

    protected ExtractionContextBase(Compilation compilation, string snapshotId, string gitRoot, IReadOnlySet<string>? scopeDocuments = null, BindingIncompletenessCollector? incompleteness = null)
    {
        Compilation = compilation;
        SnapshotId = snapshotId;
        _gitRoot = gitRoot ?? throw new ArgumentNullException(nameof(gitRoot));
        AssemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
        ScopeDocuments = scopeDocuments;
        Incompleteness = incompleteness;
    }

    internal Compilation Compilation { get; }
    internal string SnapshotId { get; }
    internal string AssemblyIdentity { get; }
    internal IReadOnlySet<string>? ScopeDocuments { get; }
    internal BindingIncompletenessCollector? Incompleteness { get; }

    internal void RecordFilteredExternal(ISymbol resolvedTarget, SyntaxNode? node)
        => Incompleteness?.RecordFilteredExternal(resolvedTarget, node, Compilation);

    internal SemanticModel GetOrCreateSemanticModel(SyntaxTree syntaxTree)
    {
        if (!_semanticModelCache.TryGetValue(syntaxTree, out var model))
        {
            model = Compilation.GetSemanticModel(syntaxTree);
            _semanticModelCache[syntaxTree] = model;
        }
        return model;
    }

    internal string? MakeSymbolId(ISymbol symbol)
    {
        return SymbolIdFactory.Make(symbol, AssemblyIdentity);
    }

    internal (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)
        GetLocationInfo(Location location)
    {
        if (location == null || !location.IsInSource)
            return (null, null, null, null, null);

        var lineSpan = location.GetLineSpan();
        var filePath = location.SourceTree?.FilePath;
        var relativePath = string.IsNullOrEmpty(filePath) ? null : DocumentChangeDetector.GetRelativePath(filePath, _gitRoot);
        return (relativePath,
                lineSpan.StartLinePosition.Line,
                lineSpan.StartLinePosition.Character,
                lineSpan.EndLinePosition.Line,
                lineSpan.EndLinePosition.Character);
    }

    internal static IEnumerable<INamedTypeSymbol> GetNamespaceTypeMembers(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            foreach (var type in GetNamespaceTypeMembers(childNs))
            {
                yield return type;
            }
        }
    }
}
