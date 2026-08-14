using Lurp.Storage;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

internal static class BindingIncompletenessReason
{
    internal const string AmbiguousOverload = "ambiguous_overload";
    internal const string CompilerError = "compiler_error";
    internal const string UnresolvedMetadata = "unresolved_metadata";
    internal const string UnsupportedSyntax = "unsupported_syntax";
    internal const string FilteredExternal = "filtered_external";
    internal const string ExtractorFailure = "extractor_failure";

    /// <summary>
    /// A DI convention scan site whose match set is open: any type added to the
    /// scanned assembly may newly match, and no persisted edge witnesses the new
    /// match, so the relation set for the site is never provably complete.
    /// </summary>
    internal const string ConventionScan = "convention_scan";

    /// <summary>The whole project failed to load or extract; no binding over it was observable.</summary>
    internal const string ProjectUnreadable = "project_unreadable";

    /// <summary>
    /// Reasons under which a missing relation proves nothing, because the relation was
    /// never observable. Excludes <see cref="FilteredExternal"/>: there the target was
    /// resolved and is knowably outside the snapshot, which is an explained absence
    /// rather than an unknown one.
    /// </summary>
    internal static readonly IReadOnlySet<string> UnobservableReasons =
        new HashSet<string>(StringComparer.Ordinal)
        {
            AmbiguousOverload,
            CompilerError,
            UnresolvedMetadata,
            UnsupportedSyntax,
            ExtractorFailure,
            ProjectUnreadable,
            ConventionScan,
        };
}

public sealed class BindingIncompletenessCollector(string projectName, string gitRoot)
{
    private static readonly HashSet<string> MissingMetadataDiagnosticIds =
        ["CS0012", "CS0234", "CS0246", "CS1069", "CS1705", "CS7069"];

    private readonly Dictionary<(string? documentPath, string reason), int> _counts = [];

    internal void RecordUnresolved(SymbolInfo symbolInfo, SyntaxNode node, SemanticModel semanticModel)
    {
        var reason = Classify(symbolInfo, node, semanticModel);
        Record(reason, node.SyntaxTree.FilePath);
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
        Record(reason, node.SyntaxTree.FilePath);
    }

    /// <summary>
    /// Records a resolved binding whose target lives in an assembly outside the
    /// compilation. Such targets are never declared in the snapshot, so the edge
    /// emitted for them is filtered by the snapshot filter (DeleteOrphanEdges)
    /// and the relation is absent from the persisted graph. Counting the filtered
    /// target is the §5.5 honest form: consumers can distinguish "no edge because
    /// the target is external" from "no edge because nothing was resolved".
    /// </summary>
    internal void RecordFilteredExternal(ISymbol resolvedTarget, SyntaxNode? node, Compilation compilation)
    {
        if (resolvedTarget.ContainingAssembly == null)
            return;
        if (SymbolEqualityComparer.Default.Equals(resolvedTarget.ContainingAssembly, compilation.Assembly))
            return;
        Record(BindingIncompletenessReason.FilteredExternal, node?.SyntaxTree?.FilePath);
    }

    internal void RecordExtractorFailure() => Record(BindingIncompletenessReason.ExtractorFailure, null);

    /// <summary>
    /// Records that a DI convention scan site has an open match set. The row seeds
    /// the cross-document refresh frontier (<see cref="BindingIncompletenessReason.UnobservableReasons"/>),
    /// so the registration document is re-examined on any change even though no
    /// previous edge points at a type the convention could newly match.
    /// </summary>
    internal void RecordConventionScan(SyntaxNode node)
        => Record(BindingIncompletenessReason.ConventionScan, node.SyntaxTree.FilePath);

    /// <summary>
    /// Declaring syntax for <paramref name="symbol"/>, falling back to its containing
    /// type when the symbol is implicitly declared (default constructor, record
    /// synthesized member, auto-property accessor) and so has no syntax of its own.
    /// </summary>
    /// <remarks>
    /// Without the fallback these records land in a document-less bucket that is a
    /// whole-compilation aggregate: no document-scoped delete can retire it and no
    /// document-scoped re-extraction can reproduce it, so a scoped incremental pass
    /// could not converge on the clean-rebuild value. Same resolution B4 applies to
    /// null-path edges in <see cref="CrossDocumentEdgeRefresher"/>.
    /// </remarks>
    internal static SyntaxNode? DeclaringSyntaxOrContainingType(ISymbol? symbol)
    {
        if (symbol == null)
            return null;
        return symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
            ?? symbol.ContainingType?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
    }

    internal IReadOnlyList<BindingIncompletenessRecord> ToRecords()
        => _counts
            .OrderBy(static pair => pair.Key.documentPath, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.reason, StringComparer.Ordinal)
            .Select(pair => new BindingIncompletenessRecord(projectName, pair.Key.documentPath, pair.Key.reason, pair.Value, VersionConstants.ExtractorVersion))
            .ToList();

    private static string Classify(SymbolInfo symbolInfo, SyntaxNode node, SemanticModel semanticModel)
    {
        if (symbolInfo.CandidateReason == CandidateReason.Ambiguous ||
            (symbolInfo.CandidateReason == CandidateReason.OverloadResolutionFailure && symbolInfo.CandidateSymbols.Length > 1))
        {
            return BindingIncompletenessReason.AmbiguousOverload;
        }

        var diagnostics = semanticModel.GetDiagnostics(node.Span)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();
        if (diagnostics.Any(diagnostic => MissingMetadataDiagnosticIds.Contains(diagnostic.Id)))
            return BindingIncompletenessReason.UnresolvedMetadata;
        if (diagnostics.Count > 0 || symbolInfo.CandidateReason == CandidateReason.OverloadResolutionFailure)
            return BindingIncompletenessReason.CompilerError;
        return BindingIncompletenessReason.UnsupportedSyntax;
    }

    private void Record(string reason, string? filePath)
    {
        var documentPath = string.IsNullOrEmpty(filePath)
            ? null
            : PathNormalizer.ToGitRelative(filePath, gitRoot);
        var key = (documentPath, reason);
        _counts[key] = _counts.GetValueOrDefault(key) + 1;
    }
}
