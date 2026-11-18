using Lurp.Workspace;
using Microsoft.CodeAnalysis;

namespace Lurp.Adapters;

/// <summary>
///     The inputs of one adapter run: the project's shared extraction inputs (S01-F32), the one edge
///     maker (S02-F19) and the D8 owner roots. Adapters do not dedupe their edges: the store collapse
///     keeps the row of the highest provenance rank for each relation (D6, D27).
/// </summary>
public sealed class AdapterExtractionContext
{
    private readonly AdapterWorkspaceContext _workspace;

    internal AdapterExtractionContext(ProjectExtractionInputs inputs)
    {
        Inputs = inputs;
        _workspace = new AdapterWorkspaceContext(inputs);
    }

    internal ProjectExtractionInputs Inputs { get; }

    public Compilation Compilation => _workspace.Compilation;
    public string SnapshotId => _workspace.SnapshotId;
    public EdgeLocationResolver LocationResolver => _workspace.LocationResolver;

    /// <summary>
    ///     Absolute, forward-slash-normalized document paths; null means the whole compilation.
    ///     Honored by every adapter, including <c>EfCoreAdapter</c>, whose annotations carry
    ///     the evidence document and are retired by
    ///     <c>IEdgeStore.DeleteAnnotationsByDocumentPaths</c> over the same scope.
    /// </summary>
    public IReadOnlySet<string>? ScopeDocuments => _workspace.ScopeDocuments;

    /// <summary>
    ///     Collector for unobservable-binding records, shared with the workspace
    ///     extractors so adapter-detected incompleteness lands in the same persisted
    ///     vocabulary.
    /// </summary>
    internal BindingIncompletenessCollector? Incompleteness => _workspace.Incompleteness;

    internal string AssemblyIdentity => _workspace.AssemblyIdentity;

    public SemanticModel GetSemanticModel(SyntaxTree tree)
    {
        return _workspace.GetOrCreateSemanticModel(tree);
    }

    public bool IsInScope(SyntaxTree? syntaxTree)
    {
        return _workspace.IsInScope(syntaxTree);
    }

    /// <summary>
    ///     True when any declaring part of <paramref name="symbol" /> is in scope. Used by
    ///     the symbol-driven adapters, which have no syntax tree at the point they decide
    ///     whether to walk a type.
    /// </summary>
    /// <remarks>
    ///     Any-part rather than all-parts matches the type-level granularity of the
    ///     Workspace guards, and costs nothing in practice: the incremental extraction
    ///     scope is already widened to every document declaring a part of a touched type
    ///     (<c>IncrementalIndexer.ExpandToDeclaringTypeParts</c>), so parts are in scope
    ///     together or not at all. An implicitly-declared symbol has no document to scope
    ///     by and stays in scope.
    /// </remarks>
    public bool IsSymbolInScope(ISymbol? symbol)
    {
        if (ScopeDocuments == null || symbol == null)
            return true;

        var references = symbol.DeclaringSyntaxReferences;
        if (references.Length == 0)
            return true;

        return references.Any(reference => IsInScope(reference.SyntaxTree));
    }

    /// <summary>Every named type of the compilation's own assembly, nested types included.</summary>
    internal IEnumerable<INamedTypeSymbol> GetAllNamedTypes()
    {
        return _workspace.GetAllNamedTypes();
    }

    internal string? MakeSymbolId(ISymbol symbol)
    {
        return _workspace.MakeSymbolId(symbol);
    }

    /// <summary>The code roots in scope, each with its D8 owner (<see cref="ExecutableRoots.Bodies" />).</summary>
    internal IEnumerable<(ISymbol Owner, SyntaxNode Node)> Bodies()
    {
        return ExecutableRoots.Bodies(_workspace);
    }

    /// <summary>The one adapter edge maker (S02-F19).</summary>
    internal EdgeRecord MakeEdge(
        string sourceId,
        string targetId,
        string kind,
        string provenance,
        string extractorVersion,
        (string? path, int? sl, int? sc, int? el, int? ec) location,
        bool sourceIsBoundary = false,
        bool targetIsBoundary = false)
    {
        return _workspace.MakeEdge(sourceId, targetId, kind, extractorVersion, location, provenance,
            sourceIsBoundary: sourceIsBoundary, targetIsBoundary: targetIsBoundary);
    }

    private sealed class AdapterWorkspaceContext(ProjectExtractionInputs inputs) : ExtractionContextBase(inputs);
}
