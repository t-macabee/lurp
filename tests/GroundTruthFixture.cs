using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Lurp.Tests;

/// <summary>
///     Loads a ground-truth corpus once (copied to temp, restored) and runs the
///     real extraction pipeline over every project compilation in-process, so the
///     ground-truth tests share one MSBuild load. Facts keep the full symbol id
///     (docCommentId|assembly) plus a doc-comment-id projection for comparison
///     with the reviewed golden file. Derived fixtures select the corpus;
///     multi-target projects contribute one compilation per target framework.
/// </summary>
public abstract class GroundTruthFixtureBase : IAsyncLifetime
{
    private string? _testDir;

    protected abstract string SolutionRelativePath { get; }

    public string SolutionPath { get; private set; } = "";
    public List<ExtractedFact> Facts { get; } = [];
    public List<(string Project, Compilation Compilation)> Compilations { get; } = [];
    public List<CompilationFactExtractor.ExtractionResult> Results { get; } = [];
    public Solution? Solution { get; private set; }
    public int DeclaredCount { get; private set; }
    public IReadOnlyList<string> DeclaredSample { get; private set; } = [];
    public IReadOnlyList<string> FactSample { get; private set; } = [];

    public async Task InitializeAsync()
    {
        IntegrationHarness.EnsureMSBuildRegistered();

        var fixtureRoot = LocateFixtureRoot();
        _testDir = Path.Combine(Path.GetTempPath(), $"lurp-groundtruth-{Guid.NewGuid():N}");
        var treeRoot = Path.Combine(_testDir, "GroundTruth");
        CopyTree(fixtureRoot, treeRoot);

        SolutionPath = Path.Combine(treeRoot, SolutionRelativePath);
        if (!File.Exists(SolutionPath))
            throw new InvalidOperationException($"Copied fixture solution missing: {SolutionPath}");

        TestProcess.RestoreSolution(SolutionPath);

        var workspace = MSBuildWorkspace.Create(LurpCache.CreateWorkspaceGlobalProperties(SolutionPath));
        Solution = await workspace.OpenSolutionAsync(SolutionPath);
        var workspaceInfo = new WorkspaceInfo(Solution, treeRoot);

        var declared = new HashSet<string>(StringComparer.Ordinal);
        var perProject = new List<(Compilation Compilation, List<EdgeRecord> Edges, List<SymbolDeclaration> Declarations)>();

        foreach (var project in Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync()
                              ?? throw new InvalidOperationException($"GetCompilationAsync returned null for '{project.Name}'.");
            Compilations.Add((project.Name, compilation));

            var options = CompilationFactExtractor.CreateOptions();
            var result = CompilationFactExtractor.ExtractAll(compilation, workspaceInfo, GroundTruthSnapshotId, project.Name, project, options);
            result.EnsureRequiredSuccess();
            Results.Add(result);
            perProject.Add((compilation, result.Edges, result.Declarations));

            foreach (var declaration in result.Declarations)
                declared.Add(declaration.SymbolId.DocCommentId);
        }

        foreach (var (compilation, edges, _) in perProject)
        {
            var assembly = compilation.Assembly.Identity.GetDisplayName();
            foreach (var edge in edges)
            {
                var source = edge.SourceSymbolId;
                var target = edge.TargetSymbolId;
                var sourceParsed = SymbolId.TryParse(source, out var parsedSource);
                var targetParsed = SymbolId.TryParse(target, out var parsedTarget);
                var sourceDoc = sourceParsed ? parsedSource!.DocCommentId : source;
                var targetDoc = targetParsed ? parsedTarget!.DocCommentId : target;
                Facts.Add(new ExtractedFact(
                    sourceDoc,
                    edge.Kind,
                    targetDoc,
                    edge.ExtractorVersion,
                    source,
                    target,
                    sourceParsed && declared.Contains(sourceDoc),
                    targetParsed && declared.Contains(targetDoc),
                    assembly));
            }
        }

        DeclaredCount = declared.Count;
        DeclaredSample = [.. declared.Take(5)];
        FactSample = [.. Facts.Take(5).Select(f => $"{f.Source}|{f.Kind}|{f.Target}")];
    }

    public Task DisposeAsync()
    {
        if (SolutionPath.Length > 0)
            TestDatabase.DeleteSolutionCache(SolutionPath);
        if (_testDir != null)
            TestDatabase.DeleteDirectory(_testDir);

        return Task.CompletedTask;
    }

    public sealed record ExtractedFact(
        string Source,
        string Kind,
        string Target,
        string ExtractorVersion,
        string SourceSymbolId,
        string TargetSymbolId,
        bool SourceDeclared,
        bool TargetDeclared,
        string Assembly);

    public const string GroundTruthSnapshotId = "00000000000000000000000000000001";

    public static string LocateFixtureRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "tests", "Fixtures", "GroundTruth", "CallShapes", "CallShapes.slnx");
            if (File.Exists(candidate))
                return Path.GetDirectoryName(Path.GetDirectoryName(candidate))!;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not locate tests/Fixtures/GroundTruth from " + AppContext.BaseDirectory);
    }

    internal static void CopyTree(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var dir in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceRoot, dir);
            if (IsBuildDirectory(rel))
                continue;
            Directory.CreateDirectory(Path.Combine(destinationRoot, rel));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceRoot, file);
            if (IsBuildDirectory(Path.GetDirectoryName(rel) ?? ""))
                continue;
            var destination = Path.Combine(destinationRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        static bool IsBuildDirectory(string relativeDir) =>
            relativeDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Fixture 1: the CallShapes call-shape corpus (single target framework).</summary>
public sealed class GroundTruthFixture : GroundTruthFixtureBase
{
    protected override string SolutionRelativePath => Path.Combine("CallShapes", "CallShapes.slnx");
}

/// <summary>
///     Fixture 2: the C#-only CrossProject corpus. CrossProject.Lib multi-targets
///     net9.0/net10.0, so the pipeline extracts one compilation per target
///     framework and the fact set is the union over every TFM compilation.
/// </summary>
public sealed class CrossProjectGroundTruthFixture : GroundTruthFixtureBase
{
    protected override string SolutionRelativePath => Path.Combine("CrossProject", "CrossProject.CSharp.slnx");
}

/// <summary>
///     Fixture 3: the D103 extractor version gate corpus: one shape for each framework adapter and
///     for each edge extractor that the other corpora do not reach.
/// </summary>
public sealed class ExtractorGateFixture : GroundTruthFixtureBase
{
    protected override string SolutionRelativePath => Path.Combine("ExtractorGate", "ExtractorGate.slnx");
}

/// <summary>
///     One golden file of a ground-truth corpus (Oracle C, D18): the compared kinds, the meaning of
///     each basis value, and the complete fact list. A fact with <see cref="GoldenFact.Pending" /> is
///     true, but the extractor does not write it yet.
/// </summary>
public sealed record GoldenFile(
    IReadOnlyList<string> ComparedKinds,
    IReadOnlyDictionary<string, string> BasisValues,
    IReadOnlyList<GoldenFact> Facts)
{
    /// <summary>Loads a golden file by its path under tests/Fixtures/GroundTruth.</summary>
    public static GoldenFile Load(string relativePath)
    {
        var path = Path.Combine(GroundTruthFixtureBase.LocateFixtureRoot(), relativePath);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var kinds = root.GetProperty("compared_kinds").EnumerateArray().Select(kind => kind.GetString()!).ToList();
        var basisValues = root.GetProperty("basis_values").EnumerateObject()
            .ToDictionary(value => value.Name, value => value.Value.GetString()!, StringComparer.Ordinal);
        var facts = root.GetProperty("facts").EnumerateArray().Select(ReadFact).ToList();
        return new GoldenFile(kinds, basisValues, facts);
    }

    private static GoldenFact ReadFact(JsonElement element)
    {
        GoldenPending? pending = null;
        if (element.TryGetProperty("pending", out var value))
        {
            pending = new GoldenPending(
                value.GetProperty("stage").GetString()!,
                value.GetProperty("tracking").GetString()!,
                value.GetProperty("reason").GetString()!);
        }

        return new GoldenFact(
            element.GetProperty("id").GetString()!,
            element.GetProperty("source").GetString()!,
            element.GetProperty("kind").GetString()!,
            element.GetProperty("target").GetString()!,
            element.GetProperty("basis").GetString()!,
            element.GetProperty("reason").GetString()!,
            pending);
    }
}

public sealed record GoldenFact(
    string Id,
    string Source,
    string Kind,
    string Target,
    string Basis,
    string Reason,
    GoldenPending? Pending);

/// <summary>The stage that will make the extractor write a pending fact, and why it does not yet.</summary>
public sealed record GoldenPending(string Stage, string Tracking, string Reason);

[CollectionDefinition("GroundTruth", DisableParallelization = true)]
public sealed class GroundTruthSuite
    : ICollectionFixture<GroundTruthFixture>, ICollectionFixture<CrossProjectGroundTruthFixture>,
      ICollectionFixture<ExtractorGateFixture>;
