using Lurp.Adapters;
using Lurp.Shared;
using Lurp.Storage;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public static class CompilationFactExtractor
{
    public sealed record ExtractionFailure(string Stage, string ProjectName, string? AdapterName, string Message, Exception Exception);
    public sealed record ExtractionMeasurement(string Extractor, long ElapsedMilliseconds, long AllocatedBytes);

    public sealed record ExtractionResult(
        List<SymbolDeclaration> Declarations,
        List<EdgeRecord> Edges,
        List<DiagnosticRecord> Diagnostics,
        List<BindingIncompletenessRecord> BindingIncompleteness,
        List<ExtractionMeasurement> Measurements,
        IReadOnlyList<ExtractionFailure>? RequiredFailures = null)
    {
        public void EnsureRequiredSuccess()
        {
            if (RequiredFailures == null || RequiredFailures.Count == 0)
                return;

            var lines = RequiredFailures.Select(f =>
                f.AdapterName != null
                    ? $"  - [{f.Stage}] adapter '{f.AdapterName}' in project '{f.ProjectName}': {f.Message}"
                    : $"  - [{f.Stage}] project '{f.ProjectName}': {f.Message}");
            var message = $"Required extraction stages failed for project(s):\n{string.Join("\n", lines)}";

            var innerExceptions = RequiredFailures.Select(f => f.Exception).ToArray();
            throw new InvalidOperationException(message, new AggregateException(message, innerExceptions));
        }
    }

    public sealed record ExtractionOptions(
        IReadOnlySet<string>? SkipAdapters = null,
        Action<string>? LogWarning = null,
        Action<string>? LogError = null,
        IReadOnlySet<string>? ScopeDocuments = null,
        Func<IReadOnlySet<string>?, IFrameworkAdapter[]>? AdapterProvider = null
    );

    /// <summary>
    /// Shared factory for the pipeline options : one WARNING/ERROR console format
    /// (line-oriented, no padding) used by every indexing pipeline (full,
    /// incremental, and the cross-document refresh), so the same extractor message
    /// renders identically regardless of which pipeline emitted it.
    /// </summary>
    public static ExtractionOptions CreateOptions(
        IReadOnlySet<string>? skipAdapters = null,
        IReadOnlySet<string>? scopeDocuments = null)
        => new(
            SkipAdapters: skipAdapters,
            ScopeDocuments: scopeDocuments,
            LogWarning: msg => Console.Error.WriteLine($"WARNING: {msg}"),
            LogError: msg => Console.Error.WriteLine($"ERROR: {msg}"));

    public static ExtractionResult ExtractAll(Compilation compilation, WorkspaceInfo workspaceInfo, string snapshotId, string projectName, ExtractionOptions? options = null)
    {
        var skipAdapters = options?.SkipAdapters;
        var logWarning = options?.LogWarning;
        var logError = options?.LogError;
        var scopeDocuments = options?.ScopeDocuments;
        var adapterProvider = options?.AdapterProvider ?? AdapterRegistry.GetAdapters;

        var failures = new List<ExtractionFailure>();
        var measurements = new List<ExtractionMeasurement>();
        var incompleteness = new BindingIncompletenessCollector(projectName, workspaceInfo.Id.GitRoot);

        var symbolExtractor = new SymbolExtractor(compilation, workspaceInfo.DocumentContents, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, snapshotId, logWarning, scopeDocuments, incompleteness);

        List<SymbolDeclaration> declarations;
        try
        {
            declarations = symbolExtractor.ExtractAll();
        }
        catch (Exception ex)
        {
            logError?.Invoke($"Symbol extraction failed for project '{projectName}': {ex.Message}");
            failures.Add(new ExtractionFailure("SymbolDeclaration", projectName, null, ex.Message, ex));
            declarations = new List<SymbolDeclaration>();
        }

        List<EdgeRecord> edges;
        try
        {
            edges = symbolExtractor.ExtractEdges();
        }
        catch (Exception ex)
        {
            logError?.Invoke($"Edge extraction failed for project '{projectName}': {ex.Message}");
            failures.Add(new ExtractionFailure("StructuralEdge", projectName, null, ex.Message, ex));
            edges = new List<EdgeRecord>();
        }


        var gitRoot = workspaceInfo.Id.GitRoot;

        var memberEdgeExtractor = new MemberEdgeExtractor(compilation, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, snapshotId, gitRoot, scopeDocuments, incompleteness);

        try
        {
            edges.AddRange(memberEdgeExtractor.ExtractAll());
            measurements.AddRange(memberEdgeExtractor.Measurements);
        }
        catch (Exception ex)
        {
            logError?.Invoke($"Member edge extraction failed for project '{projectName}': {ex.Message}");
            failures.Add(new ExtractionFailure("MemberEdge", projectName, null, ex.Message, ex));
            incompleteness.RecordExtractorFailure();
        }


        var polyExtractor = new PolymorphismExtractor(compilation, snapshotId, gitRoot, scopeDocuments, incompleteness);

        edges.AddRange(polyExtractor.ExtractAll());

        try
        {
            var reflectionExtractor = new ReflectionExtractor(compilation, snapshotId, gitRoot, scopeDocuments, incompleteness);
            edges.AddRange(reflectionExtractor.Extract());
            measurements.AddRange(reflectionExtractor.Measurements);
        }
        catch (Exception ex)
        {
            logWarning?.Invoke($"Reflection extraction failed: {ex.Message}");
            failures.Add(new ExtractionFailure("Reflection", projectName, null, ex.Message, ex));
            incompleteness.RecordExtractorFailure();
        }

        var adapters = adapterProvider(skipAdapters);

        var locationResolver = new EdgeLocationResolver(
            workspaceInfo.Documents.Keys.Select(static id => id.ToString()),
            workspaceInfo.GeneratedDocuments.Select(static id => id.ToString()),
            gitRoot);

        foreach (var adapter in adapters)
        {
            try
            {
                edges.AddRange(adapter.Extract(compilation, snapshotId, locationResolver));
            }
            catch (Exception ex)
            {
                logError?.Invoke($"Adapter '{adapter.Name}' failed: {ex.Message}");
                failures.Add(new ExtractionFailure("Adapter", projectName, adapter.Name, ex.Message, ex));
                incompleteness.RecordExtractorFailure();
            }
        }

        var diagnostics = CompilationHelper.GetDiagnostics(projectName, compilation);

        return new ExtractionResult(declarations, edges, diagnostics, incompleteness.ToRecords().ToList(), measurements, RequiredFailures: failures.Count > 0 ? failures : null);
    }
}
