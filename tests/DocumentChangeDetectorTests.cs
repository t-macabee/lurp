using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using DocumentId = Lurp.Workspace.DocumentId;
using RoslynDocumentId = Microsoft.CodeAnalysis.DocumentId;

namespace Lurp.Tests;

/// <summary>
///     T6: <see cref="DocumentChangeDetector.DetectAndLogChanges" /> builds its changed-path
///     set with <see cref="PathNormalizer.PathComparer" />, so two documents that differ only
///     in case stay two entries where the file system is case-sensitive. Collapsing them
///     drops one document from the incremental invalidation, and the incremental result then
///     diverges from a full rebuild.
/// </summary>
public sealed class DocumentChangeDetectorTests : IDisposable
{
    private readonly string _tempDir;

    public DocumentChangeDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lurp-docchange-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        TestDatabase.DeleteSolutionCache(Path.Combine(_tempDir, "Test.slnx"));
        TestDatabase.DeleteDirectory(_tempDir);
        GC.SuppressFinalize(this);
    }

    [SkippableFact]
    public void CaseOnlyDistinctDocuments_BothReportedAsChanged()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows cannot hold two documents that differ only in case.");

        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        workspace.AddSolution(SolutionInfo.Create(
            SolutionId.CreateNewId(), VersionStamp.Create(), Path.Combine(_tempDir, "Test.slnx")));

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Create(), "TestProject", "TestProject", LanguageNames.CSharp));

        // Both files exist as distinct entries on Linux; WorkspaceInfo re-reads them from
        // FilePath, so the placeholder SourceText never matters.
        foreach (var fileName in new[] { "src/Foo.cs", "src/foo.cs" })
        {
            var fullPath = Path.Combine(_tempDir, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, $"// {fileName}\n");
            solution = solution.AddDocument(
                RoslynDocumentId.CreateNewId(projectId),
                fileName,
                SourceText.From(""),
                filePath: fullPath);
        }

        workspace.TryApplyChanges(solution);

        var sink = new CapturingOutputSink();
        var info = new WorkspaceInfo(workspace.CurrentSolution, _tempDir, sink);

        // Every previous hash differs from the current one, so both documents are "Changed".
        var previous = new SnapshotManifest
        {
            DocumentVersions = new Dictionary<DocumentId, DocumentVersionId>
            {
                [new DocumentId("src/Foo.cs")] = new DocumentVersionId("src/Foo.cs", "previous-upper"),
                [new DocumentId("src/foo.cs")] = new DocumentVersionId("src/foo.cs", "previous-lower")
            }
        };

        var detector = new DocumentChangeDetector(_tempDir, sink);
        var (changedDocs, changedPaths) = detector.DetectAndLogChanges(info, previous);

        Assert.Equal(2, changedDocs.Count);
        Assert.Equal(2, changedPaths.Count);
        Assert.Contains("src/Foo.cs", changedPaths);
        Assert.Contains("src/foo.cs", changedPaths);
    }
}
