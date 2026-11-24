using Lurp.Adapters;
using Lurp.Helpers;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public static class CompilationFactExtractor
{
    /// <summary>
    ///     Shared factory for the pipeline options : one WARNING console format
    ///     (line-oriented, no padding) used by every indexing pipeline (full,
    ///     incremental, and the cross-document refresh), so the same extractor message
    ///     renders identically regardless of which pipeline emitted it.
    /// </summary>
    public static ExtractionOptions CreateOptions(
        IReadOnlySet<string>? skipAdapters = null,
        IReadOnlySet<string>? scopeDocuments = null,
        IOutputSink? output = null)
    {
        var sink = output ?? ConsoleOutputSink.Instance;
        return new ExtractionOptions(
            skipAdapters,
            ScopeDocuments: scopeDocuments,
            LogWarning: msg => sink.WriteErrorLine($"WARNING: {msg}"));
    }

    /// <summary>
    ///     Runs one extraction stage that may degrade (D34): an exception is logged as a
    ///     warning, kept in <see cref="ExtractionResult.StageFailures" /> and recorded as one
    ///     <c>extractor_failure</c> site on each document of the project, and the other stages
    ///     still run. Only the declaration stage is required; it does not go through here.
    ///     Internal so the failure contract can be unit-tested directly.
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
            ctx.Incompleteness.RecordExtractorFailure(adapterName == null ? stageName : $"{stageName}:{adapterName}", ctx.DocumentPaths);
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
            ctx.Incompleteness.RecordExtractorFailure(adapterName == null ? stageName : $"{stageName}:{adapterName}", ctx.DocumentPaths);
            return onFailure;
        }
    }

    public static ExtractionResult ExtractAll(Compilation compilation, WorkspaceInfo workspaceInfo, string snapshotId, string projectName, ExtractionOptions? options = null)
    {
        return ExtractAll(compilation, workspaceInfo, snapshotId, projectName, project: null, options);
    }

    /// <summary>
    ///     Extracts every fact of one project. The declaration stage is required: when it
    ///     throws, the exception leaves this method and the caller records the project as
    ///     unreadable. Every other stage degrades (<see cref="RunStage" />): the result keeps
    ///     the facts of the stages that succeeded (D34).
    /// </summary>
    public static ExtractionResult ExtractAll(Compilation compilation, WorkspaceInfo workspaceInfo, string snapshotId, string projectName, Project? project, ExtractionOptions? options = null)
    {
        var skipAdapters = options?.SkipAdapters;
        var logWarning = options?.LogWarning;
        var scopeDocuments = options?.ScopeDocuments;
        var adapterProvider = options?.AdapterProvider ?? AdapterRegistry.GetAdapters;

        var failures = new List<ExtractionFailure>();
        var measurements = new List<ExtractionMeasurement>();
        var incompleteness = new BindingIncompletenessCollector(projectName, workspaceInfo.Id.GitRoot);
        foreach (var excludedPath in workspaceInfo.GetExcludedDocuments(project, projectName))
            incompleteness.RecordExcludedDocument(excludedPath);

        var normalizedRoot = PathNormalizer.NormalizeRoot(workspaceInfo.Id.GitRoot);
        var documentPaths = compilation.SyntaxTrees
            .Where(tree => !string.IsNullOrEmpty(tree.FilePath)
                           && ExtractionUtils.IsInScope(scopeDocuments, tree)
                           && !BuildOutputPaths.IsBuildOutputPath(PathNormalizer.ToGitRelativeFromNormalizedRoot(tree.FilePath, normalizedRoot)))
            .Select(static tree => tree.FilePath)
            .ToList();
        var ctx = new StageContext(projectName, failures, incompleteness, documentPaths);

        var targetFramework = WorkspaceInfo.UnknownValue;
        var open = projectName.LastIndexOf('(');
        if (open >= 0 && projectName.EndsWith(')') && open < projectName.Length - 2)
        {
            targetFramework = projectName[(open + 1)..^1];
        }
        else if (workspaceInfo.TargetFrameworks.TryGetValue(projectName, out var mappedTfm) && !mappedTfm.Contains(';'))
        {
            targetFramework = mappedTfm;
        }

        var documentIdStrings = workspaceInfo.Documents.Keys.Select(static id => id.ToString()).ToArray();
        var generatedIdStrings = workspaceInfo.GeneratedDocuments.Select(static id => id.ToString()).ToArray();
        var inputs = new ProjectExtractionInputs(
            compilation, snapshotId,
            new EdgeLocationResolver(documentIdStrings, generatedIdStrings, workspaceInfo.Id.GitRoot),
            scopeDocuments, incompleteness, [], GetSolutionAssemblyIdentities(compilation, workspaceInfo));

        var symbolExtractor = new SymbolExtractor(inputs, workspaceInfo, targetFramework);

        var declarations = symbolExtractor.ExtractAll();

        var edges = RunStage(
            ctx, "StructuralEdge", null, logWarning,
            msg => $"Edge extraction failed for project '{projectName}': {msg}",
            symbolExtractor.ExtractEdges,
            []);

        var memberEdgeExtractor = new MemberEdgeExtractor(inputs);

        RunStage(
            ctx, "MemberEdge", null, logWarning,
            msg => $"Member edge extraction failed for project '{projectName}': {msg}",
            () =>
            {
                edges.AddRange(memberEdgeExtractor.ExtractAll());
                measurements.AddRange(memberEdgeExtractor.Measurements);
            });

        RunStage(
            ctx, "Polymorphism", null, logWarning,
            msg => $"Polymorphism extraction failed for project '{projectName}': {msg}",
            () => edges.AddRange(new PolymorphismExtractor(inputs).ExtractAll()));

        RunStage(
            ctx, "Reflection", null, logWarning,
            msg => $"Reflection extraction failed: {msg}",
            () =>
            {
                var reflectionExtractor = new ReflectionExtractor(inputs);
                edges.AddRange(reflectionExtractor.Extract());
                measurements.AddRange(reflectionExtractor.Measurements);
            });

        var adapters = adapterProvider(skipAdapters);

        var adapterContext = new AdapterExtractionContext(inputs);

        var annotations = new List<AnnotationRecord>();

        foreach (var adapter in adapters)
            RunStage(
                ctx, "Adapter", adapter.Name, logWarning,
                msg => $"Adapter '{adapter.Name}' failed: {msg}",
                () =>
                {
                    var result = adapter.Extract(adapterContext);
                    edges.AddRange(result.Edges);
                    annotations.AddRange(result.Annotations);
                });

        var diagnostics = project != null
            ? CompilationHelper.GetDiagnostics(project, compilation, logWarning)
            : CompilationHelper.GetDiagnostics(projectName, compilation);

        return new ExtractionResult(declarations, edges, diagnostics, [.. incompleteness.ToRecords()], measurements, annotations, failures);
    }

    private static HashSet<AssemblyIdentity> GetSolutionAssemblyIdentities(Compilation compilation, WorkspaceInfo workspaceInfo)
    {
        var projectAssemblies = workspaceInfo.AssemblyNames.Values.ToHashSet(StringComparer.Ordinal);
        var identities = new HashSet<AssemblyIdentity> { compilation.Assembly.Identity };

        // Roslyn represents C# project references as compilations, including transitive project
        // dependencies. Package and reference assemblies use metadata references.
        foreach (var reference in compilation.References.OfType<CompilationReference>())
        {
            var identity = reference.Compilation.Assembly.Identity;
            if (projectAssemblies.Contains(identity.Name))
                identities.Add(identity);
        }

        return identities;
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
        IReadOnlyList<ExtractionFailure> StageFailures);

    public sealed record ExtractionOptions(
        IReadOnlySet<string>? SkipAdapters = null,
        Action<string>? LogWarning = null,
        IReadOnlySet<string>? ScopeDocuments = null,
        Func<IReadOnlySet<string>?, IFrameworkAdapter[]>? AdapterProvider = null
    );

    /// <summary>
    ///     Shared state a <see cref="RunStage" /> call records a failure against:
    ///     the project a stage failed for, the accumulated failure list, the
    ///     binding-incompleteness collector, and the in-scope document paths that
    ///     receive the <c>extractor_failure</c> site.
    /// </summary>
    internal sealed record StageContext(string ProjectName, List<ExtractionFailure> Failures, BindingIncompletenessCollector Incompleteness, IReadOnlyList<string> DocumentPaths);
}
