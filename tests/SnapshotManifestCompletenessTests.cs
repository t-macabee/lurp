using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynDocumentId = Microsoft.CodeAnalysis.DocumentId;

namespace Lurp.Tests;

/// <summary>
/// GAP 3 / GAP 1: <see cref="SnapshotCompleteness.GeneratedTreesIncluded"/> is
/// declared but never asserted. Both <see cref="SnapshotManifest.FromWorkspace"/>
/// and <see cref="SnapshotManifest.FromStorageManifest"/> hardcode it to false;
/// this test pins the FromWorkspace branch so the declared completeness seam
/// cannot silently flip without a failing test.
/// </summary>
public sealed class SnapshotManifestCompletenessTests : IDisposable
{
    private static readonly MetadataReference[] _platformReferences =
        (((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")) ?? "")
        .Split(Path.PathSeparator)
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(p => MetadataReference.CreateFromFile(p!))
        .ToArray();

    private readonly string _tempDir;

    public SnapshotManifestCompletenessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lurp-completeness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void FromWorkspace_GeneratedTreesIncluded_IsFalse()
    {
        var workspace = BuildSingleFileWorkspace();

        var manifest = SnapshotManifest.FromWorkspace(
            workspace, SnapshotIdentity.Create(workspace, new HashSet<string>()));

        Assert.NotNull(manifest.Completeness);
        Assert.False(manifest.Completeness.GeneratedTreesIncluded);
    }

    /// <summary>
    /// Builds a one-file adhoc workspace wrapped in a <see cref="WorkspaceInfo"/>.
    /// No MSBuild and no external dependency — pure Roslyn, CI-runnable on any
    /// platform.
    /// </summary>
    private WorkspaceInfo BuildSingleFileWorkspace()
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solutionId = SolutionId.CreateNewId();

        workspace.AddSolution(SolutionInfo.Create(
            solutionId, VersionStamp.Create(), filePath: Path.Combine(_tempDir, "P.slnx")));

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                name: "P",
                assemblyName: "P",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable),
                metadataReferences: _platformReferences));

        const string source = """
            namespace P;

            public class Svc
            {
                public int Value() => 42;
            }
            """;
        var docPath = Path.Combine(_tempDir, "Svc.cs");
        File.WriteAllText(docPath, source);
        solution = solution.AddDocument(
            RoslynDocumentId.CreateNewId(projectId),
            "Svc.cs",
            SourceText.From(source, System.Text.Encoding.UTF8),
            filePath: docPath);

        workspace.TryApplyChanges(solution);

        return new WorkspaceInfo(workspace.CurrentSolution, _tempDir);
    }
}
