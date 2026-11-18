using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public sealed class BindingIncompletenessCollector(string projectName, string gitRoot)
{
    private static readonly HashSet<string> MissingMetadataDiagnosticIds =
        ["CS0012", "CS0234", "CS0246", "CS1069", "CS1705", "CS7069"];

    // D16: occurrence_count is the number of distinct binding sites. A site is a node span
    // in its document (or, for an extractor failure, the failed stage), so a site that
    // several extractors see counts once.
    private readonly Dictionary<(string? documentPath, string reason), HashSet<string>> _sites = [];

    internal void RecordUnresolved(SymbolInfo symbolInfo, SyntaxNode node, SemanticModel semanticModel)
    {
        var reason = Classify(symbolInfo, node, semanticModel);
        Record(reason, node);
    }

    internal void RecordUnresolved(SyntaxNode node, SemanticModel semanticModel)
    {
        var diagnostics = semanticModel.GetDiagnostics(node.Span)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();
        var reason = diagnostics.Any(diagnostic => MissingMetadataDiagnosticIds.Contains(diagnostic.Id))
            ? BindingIncompletenessReason.UnresolvedMetadata
            : diagnostics.Count > 0
                ? BindingIncompletenessReason.CompilerError
                : BindingIncompletenessReason.UnsupportedSyntax;
        Record(reason, node);
    }

    /// <summary>
    ///     Records a resolved binding whose target lives in an assembly outside the
    ///     compilation. Such targets are never declared in the snapshot, so the edge
    ///     emitted for them is filtered by the snapshot filter (DeleteOrphanEdges)
    ///     and the relation is absent from the persisted graph. Counting the filtered
    ///     target is the §5.5 honest form: consumers can distinguish "no edge because
    ///     the target is external" from "no edge because nothing was resolved".
    /// </summary>
    internal void RecordFilteredExternal(ISymbol resolvedTarget, SyntaxNode? node, Compilation compilation)
    {
        if (!ExtractionUtils.IsOutsideCompilation(resolvedTarget, compilation))
            return;
        Record(BindingIncompletenessReason.FilteredExternal, node);
    }

    /// <summary>
    ///     Records a failed extraction stage on each document of the project (D34), so a
    ///     capsule, a dead candidate and an incremental pass see it per document. A project
    ///     with no document keeps one project-level row.
    /// </summary>
    internal void RecordExtractorFailure(string stage, IReadOnlyList<string> documentPaths)
    {
        if (documentPaths.Count == 0)
        {
            Record(BindingIncompletenessReason.ExtractorFailure, null, stage);
            return;
        }

        foreach (var documentPath in documentPaths)
            Record(BindingIncompletenessReason.ExtractorFailure, documentPath, stage);
    }

    /// <summary>
    ///     Records a compile item under <c>bin/</c> or <c>obj/</c> excluded from the
    ///     snapshot (D4, D165.5). <paramref name="relativeDocumentPath" /> is already
    ///     git-root-relative.
    /// </summary>
    internal void RecordExcludedDocument(string relativeDocumentPath)
    {
        var normalized = PathNormalizer.ToForwardSlash(relativeDocumentPath);
        var key = ((string?)normalized, BindingIncompletenessReason.ExcludedDocument);
        if (!_sites.TryGetValue(key, out var sites))
            _sites[key] = sites = new HashSet<string>(StringComparer.Ordinal);
        sites.Add(normalized);
    }

    /// <summary>
    ///     Records that a DI convention scan site has an open match set. The row seeds
    ///     the reverse-edge closure frontier (<see cref="BindingIncompletenessReason.UnobservableReasons" />),
    ///     so the registration document is re-examined on any change even though no
    ///     previous edge points at a type the convention could newly match.
    /// </summary>
    internal void RecordConventionScan(SyntaxNode node)
    {
        Record(BindingIncompletenessReason.ConventionScan, node);
    }

    /// <summary>
    ///     Declaring syntax for <paramref name="symbol" />, falling back to its containing
    ///     type when the symbol is implicitly declared (default constructor, record
    ///     synthesized member, auto-property accessor) and so has no syntax of its own.
    /// </summary>
    /// <remarks>
    ///     Without the fallback these records land in a document-less bucket that is a
    ///     whole-compilation aggregate: no document-scoped delete can retire it and no
    ///     document-scoped re-extraction can reproduce it, so a scoped incremental pass
    ///     could not converge on the clean-rebuild value. Same resolution B4 applies to
    ///     null-path edges in <see cref="ReverseEdgeClosure" />.
    /// </remarks>
    internal static SyntaxNode? DeclaringSyntaxOrContainingType(ISymbol? symbol)
    {
        if (symbol == null)
            return null;
        return symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
               ?? symbol.ContainingType?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
    }

    internal IReadOnlyList<BindingIncompletenessRecord> ToRecords()
    {
        return [.. _sites
            .OrderBy(static pair => pair.Key.documentPath, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.reason, StringComparer.Ordinal)
            .Select(pair => new BindingIncompletenessRecord(projectName, pair.Key.documentPath, pair.Key.reason, pair.Value.Count, VersionConstants.ExtractorVersion))];
    }

    private static string Classify(SymbolInfo symbolInfo, SyntaxNode node, SemanticModel semanticModel)
    {
        if (symbolInfo.CandidateReason == CandidateReason.Ambiguous ||
            symbolInfo is { CandidateReason: CandidateReason.OverloadResolutionFailure, CandidateSymbols.Length: > 1 })
            return BindingIncompletenessReason.AmbiguousOverload;

        var diagnostics = semanticModel.GetDiagnostics(node.Span)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();
        if (diagnostics.Any(diagnostic => MissingMetadataDiagnosticIds.Contains(diagnostic.Id)))
            return BindingIncompletenessReason.UnresolvedMetadata;
        if (diagnostics.Count > 0 || symbolInfo.CandidateReason == CandidateReason.OverloadResolutionFailure)
            return BindingIncompletenessReason.CompilerError;
        return BindingIncompletenessReason.UnsupportedSyntax;
    }

    /// <summary>A node site: its document and its span. A node with no tree is one document-less site.</summary>
    private void Record(string reason, SyntaxNode? node)
    {
        Record(reason, node?.SyntaxTree.FilePath, node == null ? "" : $"{node.Span.Start}:{node.Span.Length}");
    }

    private void Record(string reason, string? filePath, string site)
    {
        var documentPath = string.IsNullOrEmpty(filePath)
            ? null
            : PathNormalizer.ToGitRelative(filePath, gitRoot);
        var key = (documentPath, reason);
        if (!_sites.TryGetValue(key, out var sites))
            _sites[key] = sites = new HashSet<string>(StringComparer.Ordinal);
        sites.Add(site);
    }
}