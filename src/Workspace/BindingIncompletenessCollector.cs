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
}

internal sealed class BindingIncompletenessCollector(string projectName, string gitRoot)
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
            : DocumentChangeDetector.GetRelativePath(filePath, gitRoot);
        var key = (documentPath, reason);
        _counts[key] = _counts.GetValueOrDefault(key) + 1;
    }
}
