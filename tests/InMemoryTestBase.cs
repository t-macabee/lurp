using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using RoslynDocumentId = Microsoft.CodeAnalysis.DocumentId;

namespace Lurp.Tests;

/// <summary>
///     Pattern B test infrastructure: in-memory CSharpCompilation built from source
///     strings, extracted directly via <see cref="CompilationFactExtractor.ExtractAll" />.
///     No MSBuild restore and no .csproj — source files are still written to a temp
///     directory because <see cref="WorkspaceInfo" /> reads document bytes from disk.
/// </summary>
public abstract class InMemoryTestBase : IDisposable
{
    private static readonly MetadataReference[] _defaultReferences =
        [.. ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
        .Split(Path.PathSeparator)
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(p => MetadataReference.CreateFromFile(p!))];

    /// <summary>All adapter names, for tests that must exclude adapter output.</summary>
    protected static readonly HashSet<string> AllAdapterNames =
        ["ASP.NET Core", "Dependency Injection", "MediatR", "EF Core", "Serialization", "Test"];

    private readonly string _tempDir;

    protected InMemoryTestBase()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lurp-mem-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        TestDatabase.DeleteDirectory(_tempDir);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Builds a compilation from the given file name → source map and extracts
    ///     all facts. Adapters are skipped unless <paramref name="runAdapters" /> is
    ///     true, keeping golden edge tests focused on the workspace extractors.
    /// </summary>
    public async Task<Extraction> ExtractAsync(
        IReadOnlyDictionary<string, string> files,
        string projectName = "TestProject",
        bool runAdapters = false,
        string? snapshotId = null,
        Func<IReadOnlySet<string>?, Lurp.Adapters.IFrameworkAdapter[]>? adapterProvider = null)
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solutionId = SolutionId.CreateNewId();

        workspace.AddSolution(SolutionInfo.Create(
            solutionId, VersionStamp.Create(), Path.Combine(_tempDir, "Test.slnx")));

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                projectName,
                projectName,
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable),
                metadataReferences: _defaultReferences));

        foreach (var (fileName, content) in files)
        {
            var fullPath = Path.Combine(_tempDir, fileName);
            await File.WriteAllTextAsync(fullPath, content);
            solution = solution.AddDocument(
                RoslynDocumentId.CreateNewId(projectId),
                fileName,
                SourceText.From(content, Encoding.UTF8),
                filePath: fullPath);
        }

        workspace.TryApplyChanges(solution);

        var project = workspace.CurrentSolution.GetProject(projectId)!;
        var compilation = (CSharpCompilation)(await project.GetCompilationAsync() ??
                                              throw new InvalidOperationException("Compilation failed to build."));

        var workspaceInfo = new WorkspaceInfo(workspace.CurrentSolution, _tempDir);

        var options = CompilationFactExtractor.CreateOptions(
            runAdapters ? null : AllAdapterNames) with { AdapterProvider = adapterProvider };

        var result = CompilationFactExtractor.ExtractAll(
            compilation, workspaceInfo, snapshotId ?? "test-snapshot", projectName, options);

        var fqnToSymbolId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declaration in result.Declarations)
        {
            var fqn = declaration.SymbolId.FullyQualifiedName;
            if (fqn != null && !fqnToSymbolId.ContainsKey(fqn))
                fqnToSymbolId[fqn] = declaration.SymbolId.Value;
        }

        return new Extraction { Result = result, FqnToSymbolId = fqnToSymbolId };
    }

    public sealed class Extraction
    {
        public required CompilationFactExtractor.ExtractionResult Result { get; init; }
        public required IReadOnlyDictionary<string, string> FqnToSymbolId { get; init; }

        public List<EdgeRecord> EdgesOf(string kind, string provenance = Provenance.CompilerProved)
        {
            return [.. Result.Edges.Where(e => e.Kind == kind && e.Provenance == provenance)];
        }

        public string ResolveId(string fqn)
        {
            return FqnToSymbolId.TryGetValue(fqn, out var id)
                ? id
                : throw new InvalidOperationException($"No declared symbol with FQN '{fqn}'.");
        }

        public EdgeRecord SingleEdge(string kind, string sourceFqn, string targetFqn,
            string provenance = Provenance.CompilerProved)
        {
            var sourceId = ResolveId(sourceFqn);
            var targetId = ResolveId(targetFqn);
            var matches = Result.Edges.Where(e =>
                e.Kind == kind && e.Provenance == provenance &&
                e.SourceSymbolId == sourceId && e.TargetSymbolId == targetId).ToList();
            return Assert.Single(matches);
        }

        /// <summary>
        ///     Asserts the exact set of compiler-proved <paramref name="kind" /> edges whose source and
        ///     target are both declared in the test source, as (source FQN, target FQN) pairs. An extra
        ///     or a missing edge fails. Edges to symbols outside the test source are not in the set.
        /// </summary>
        public void AssertEdgeSet(string kind, params (string Source, string Target)[] expected)
        {
            AssertEdgeSetOf(kind, Provenance.CompilerProved, expected);
        }

        /// <summary><see cref="AssertEdgeSet" /> for the edges of one <paramref name="provenance" />.</summary>
        public void AssertEdgeSetOf(string kind, string provenance, params (string Source, string Target)[] expected)
        {
            var fqnById = Result.Declarations
                .Where(declaration => declaration.SymbolId.FullyQualifiedName != null)
                .GroupBy(declaration => declaration.SymbolId.Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().SymbolId.FullyQualifiedName!, StringComparer.Ordinal);
            var actual = Result.Edges
                .Where(e => e.Kind == kind && e.Provenance == provenance &&
                            fqnById.ContainsKey(e.SourceSymbolId) && fqnById.ContainsKey(e.TargetSymbolId))
                .Select(e => (Source: fqnById[e.SourceSymbolId], Target: fqnById[e.TargetSymbolId]))
                .Distinct()
                .Order()
                .ToList();
            Assert.Equal(expected.Order().ToList(), actual);
        }

        /// <summary>
        ///     <see cref="AssertEdgeSet" /> with Roslyn documentation-comment ids in place of FQNs, for
        ///     overloads that share one FQN (a record's two <c>Equals</c>).
        /// </summary>
        public void AssertDocIdEdgeSet(string kind, params (string Source, string Target)[] expected)
        {
            AssertDocIdEdgeSetOf(kind, Provenance.CompilerProved, expected);
        }

        /// <summary><see cref="AssertDocIdEdgeSet" /> for the edges of one <paramref name="provenance" />.</summary>
        public void AssertDocIdEdgeSetOf(string kind, string provenance, params (string Source, string Target)[] expected)
        {
            var declared = Result.Declarations.Select(declaration => declaration.SymbolId.Value).ToHashSet(StringComparer.Ordinal);
            var actual = Result.Edges
                .Where(e => e.Kind == kind && e.Provenance == provenance &&
                            declared.Contains(e.SourceSymbolId) && declared.Contains(e.TargetSymbolId))
                .Select(e => (Source: DocId(e.SourceSymbolId), Target: DocId(e.TargetSymbolId)))
                .Distinct()
                .Order()
                .ToList();
            Assert.Equal(expected.Order().ToList(), actual);

            static string DocId(string symbolId) => symbolId[..symbolId.IndexOf('|', StringComparison.Ordinal)];
        }

    }
}