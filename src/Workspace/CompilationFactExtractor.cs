using Lurp.Adapters;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public static class CompilationFactExtractor
{
    /// <summary>
    ///     Shared factory for the pipeline options : one WARNING/ERROR console format
    ///     (line-oriented, no padding) used by every indexing pipeline (full,
    ///     incremental, and the cross-document refresh), so the same extractor message
    ///     renders identically regardless of which pipeline emitted it.
    /// </summary>
    public static ExtractionOptions CreateOptions(
        IReadOnlySet<string>? skipAdapters = null,
        IReadOnlySet<string>? scopeDocuments = null)
    {
        return new ExtractionOptions(
            skipAdapters,
            ScopeDocuments: scopeDocuments,
            LogWarning: msg => Console.Error.WriteLine($"WARNING: {msg}"),
            LogError: msg => Console.Error.WriteLine($"ERROR: {msg}"));
    }

    /// <summary>
    ///     Runs one extraction stage, catching any exception so a single failing
    ///     stage degrades (recorded in <see cref="ExtractionFailure" /> and
    ///     <see cref="BindingIncompletenessCollector" />) instead of aborting the
    ///     rest of <see cref="ExtractAll" />. All six extraction stages —
    ///     including Polymorphism, previously unguarded — go through this so a
    ///     thrown exception in any one of them can never escape <c>ExtractAll</c>.
    ///     Internal (not private) so the failure/incompleteness contract can be
    ///     unit-tested directly rather than only through the six ExtractAll call
    ///     sites.
    /// </summary>
    internal static void RunStage(
        StageContext ctx,
        string stageName,
        string? adapterName,
        Action<string>? log,
        Func<string, string> describeFailure,
        Action stage)
    {
        try
        {
            stage();
        }
        catch (Exception ex)
        {
            log?.Invoke(describeFailure(ex.Message));
            ctx.Failures.Add(new ExtractionFailure(stageName, ctx.ProjectName, adapterName, ex.Message, ex));
            ctx.Incompleteness.RecordExtractorFailure();
        }
    }

    /// <inheritdoc cref="RunStage(StageContext, string, string?, Action{string}?, Func{string, string}, Action)" />
    /// <remarks>Value-returning overload for stages that produce a result rather than mutating a shared list; <paramref name="onFailure" /> is the result used when the stage throws.</remarks>
    internal static T RunStage<T>(
        StageContext ctx,
        string stageName,
        string? adapterName,
        Action<string>? log,
        Func<string, string> describeFailure,
        Func<T> stage,
        T onFailure)
    {
        try
        {
            return stage();
        }
        catch (Exception ex)
        {
            log?.Invoke(describeFailure(ex.Message));
            ctx.Failures.Add(new ExtractionFailure(stageName, ctx.ProjectName, adapterName, ex.Message, ex));
            ctx.Incompleteness.RecordExtractorFailure();
            return onFailure;
        }
    }

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
        var ctx = new StageContext(projectName, failures, incompleteness);

        var symbolExtractor = new SymbolExtractor(compilation, workspaceInfo.DocumentContents, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, snapshotId, logWarning, scopeDocuments, incompleteness);

        var declarations = RunStage(
            ctx, "SymbolDeclaration", null, logError,
            msg => $"Symbol extraction failed for project '{projectName}': {msg}",
            symbolExtractor.ExtractAll,
            new List<SymbolDeclaration>());

        var edges = RunStage(
            ctx, "StructuralEdge", null, logError,
            msg => $"Edge extraction failed for project '{projectName}': {msg}",
            symbolExtractor.ExtractEdges,
            new List<EdgeRecord>());

        var gitRoot = workspaceInfo.Id.GitRoot;

        var sharedModelCache = new Dictionary<SyntaxTree, SemanticModel>();

        var memberEdgeExtractor = new MemberEdgeExtractor(compilation, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, snapshotId, gitRoot, scopeDocuments, incompleteness, sharedModelCache);

        RunStage(
            ctx, "MemberEdge", null, logError,
            msg => $"Member edge extraction failed for project '{projectName}': {msg}",
            () =>
            {
                edges.AddRange(memberEdgeExtractor.ExtractAll());
                measurements.AddRange(memberEdgeExtractor.Measurements);
            });

        // Same document-id string projection the adapter EdgeLocationResolver
        // (below) uses; shared so poly/reflection edges carry the identical
        // generated-source signal as the member-edge and adapter lineages.
        var documentIdStrings = workspaceInfo.Documents.Keys.Select(static id => id.ToString()).ToArray();
        var generatedIdStrings = workspaceInfo.GeneratedDocuments.Select(static id => id.ToString()).ToArray();

        var polyExtractor = new PolymorphismExtractor(compilation, snapshotId, gitRoot, scopeDocuments, incompleteness, sharedModelCache, documentIdStrings, generatedIdStrings);

        // Polymorphism was previously unguarded: a thrown exception here
        // escaped ExtractAll entirely. It now degrades like every other
        // stage — recorded as a failure/incompleteness entry rather than
        // being fatal — for consistency with the other five stages.
        RunStage(
            ctx, "Polymorphism", null, logError,
            msg => $"Polymorphism extraction failed for project '{projectName}': {msg}",
            () => edges.AddRange(polyExtractor.ExtractAll()));

        RunStage(
            ctx, "Reflection", null, logWarning,
            msg => $"Reflection extraction failed: {msg}",
            () =>
            {
                var reflectionExtractor = new ReflectionExtractor(compilation, snapshotId, gitRoot, scopeDocuments, incompleteness, sharedModelCache, documentIdStrings, generatedIdStrings);
                edges.AddRange(reflectionExtractor.Extract());
                measurements.AddRange(reflectionExtractor.Measurements);
            });

        var adapters = adapterProvider(skipAdapters);

        var locationResolver = new EdgeLocationResolver(documentIdStrings, generatedIdStrings, gitRoot);

        var adapterContext = new AdapterExtractionContext(
            compilation, snapshotId, locationResolver, scopeDocuments, sharedModelCache,
            incompleteness);

        var annotations = new List<AnnotationRecord>();

        foreach (var adapter in adapters)
            RunStage(
                ctx, "Adapter", adapter.Name, logError,
                msg => $"Adapter '{adapter.Name}' failed: {msg}",
                () =>
                {
                    var result = adapter.Extract(adapterContext);
                    edges.AddRange(result.Edges);
                    annotations.AddRange(result.Annotations);
                });

        var diagnostics = CompilationHelper.GetDiagnostics(projectName, compilation);

        return new ExtractionResult(declarations, edges, diagnostics, incompleteness.ToRecords().ToList(), measurements, annotations, failures.Count > 0 ? failures : null);
    }

    public sealed record ExtractionFailure(string Stage, string ProjectName, string? AdapterName, string Message, Exception Exception);

    public sealed record ExtractionMeasurement(string Extractor, long ElapsedMilliseconds, long AllocatedBytes);

    public sealed record ExtractionResult(
        List<SymbolDeclaration> Declarations,
        List<EdgeRecord> Edges,
        List<DiagnosticRecord> Diagnostics,
        List<BindingIncompletenessRecord> BindingIncompleteness,
        List<ExtractionMeasurement> Measurements,
        List<AnnotationRecord> Annotations,
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
    ///     Shared state a <see cref="RunStage" /> call records a failure against:
    ///     the project a stage failed for, the accumulated failure list, and the
    ///     binding-incompleteness collector every stage degrades into on error.
    /// </summary>
    internal sealed record StageContext(string ProjectName, List<ExtractionFailure> Failures, BindingIncompletenessCollector Incompleteness);
}