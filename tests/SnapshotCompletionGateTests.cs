using System.Reflection;
using Lurp.Adapters;
using Lurp.Shared;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Tests;

public sealed class SnapshotCompletionGateTests : IDisposable
{
    private string? _testDir;
    private string? _dbPath;

    public void Dispose()
    {
        if (_testDir != null && Directory.Exists(_testDir))
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(_testDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [Fact]
    public void ExtractAll_EnabledAdapterThrows_RecordsRequiredFailureAndBlocksBatch()
    {
        var source = "class Foo { void Bar() {} }";
        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: "/tmp/test.cs");
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        using var workspace = new AdhocWorkspace();
        var tempDir = Path.Combine(Path.GetTempPath(), $"lurp_unit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var roslynProjectId = ProjectId.CreateNewId();
            var solutionFilePath = Path.Combine(tempDir, "Test.sln");
            File.WriteAllText(solutionFilePath, "");
            var projectFilePath = Path.Combine(tempDir, "TestProject.csproj");
            File.WriteAllText(projectFilePath, "");
            var docFilePath = Path.Combine(tempDir, "test.cs");
            File.WriteAllText(docFilePath, source);

            var projectInfo = ProjectInfo.Create(
                roslynProjectId,
                VersionStamp.Create(),
                "TestProject",
                "TestAssembly",
                LanguageNames.CSharp,
                filePath: projectFilePath,
                documents: [DocumentInfo.Create(Microsoft.CodeAnalysis.DocumentId.CreateNewId(roslynProjectId), "test.cs", filePath: docFilePath)]);
            var solutionInfo = SolutionInfo.Create(
                SolutionId.CreateNewId(),
                VersionStamp.Create(),
                solutionFilePath,
                projects: [projectInfo]);
            workspace.AddSolution(solutionInfo);
            var solution = workspace.CurrentSolution;

            var workspaceInfo = new WorkspaceInfo(solution, tempDir);

            var throwingAdapter = new ThrowingAdapter();
            IFrameworkAdapter[] adapterProvider(IReadOnlySet<string>? skip) => [throwingAdapter];

            var options = new CompilationFactExtractor.ExtractionOptions(
                AdapterProvider: adapterProvider);

            var result = CompilationFactExtractor.ExtractAll(
                compilation, workspaceInfo, "snap-test", "TestProject", options);

            Assert.NotNull(result.RequiredFailures);
            Assert.Single(result.RequiredFailures);

            var failure = result.RequiredFailures[0];
            Assert.Equal("Adapter", failure.Stage);
            Assert.Equal("ThrowingAdapter", failure.AdapterName);
            Assert.Equal("TestProject", failure.ProjectName);

            var ex = Assert.Throws<InvalidOperationException>(result.EnsureRequiredSuccess);
            Assert.Contains("ThrowingAdapter", ex.Message);
            Assert.Contains("TestProject", ex.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // T16c: Regression test for the failed-snapshot storage leak. The rework
    // made failures explicit (MarkSnapshotFailed) but left
    // DeleteIncompleteSnapshots selecting only 'in_progress', so partial data
    // from every failed index accumulated indefinitely. Failed snapshots are
    // now reclaimed as tombstones: the payload is deleted, the row : and with
    // it the failure reason P2-9 exists to expose : is retained.
    [Fact]
    public void DeleteIncompleteSnapshots_FailedSnapshotKeepsTombstoneAndReclaimsPayload()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp_prune_{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new SqliteIndexStore(dbPath))
            {
                store.Open();
                store.RunMigrations();

                var workspaceId = "workspace:///prune-test";
                store.SaveWorkspace(workspaceId, "/repo", "Test.sln", DateTime.UtcNow);

                var failedId = "snap-failed";
                var inProgressId = "snap-in-progress";
                foreach (var id in new[] { failedId, inProgressId })
                {
                    store.SaveSnapshot(new SnapshotRow
                    {
                        SnapshotId = id,
                        WorkspaceId = workspaceId,
                        GitRoot = "/repo",
                        SolutionPath = "/repo/Test.sln",
                        SdkVersion = "10.0.301",
                        CompilerVersion = "4.12.0.0",
                        CreatedAtUtc = DateTime.UtcNow,
                        Documents =
                        [
                            new DocumentVersion
                            {
                                DocumentId = id + "/doc",
                                FilePath = "src/A.cs",
                                ContentHash = "hash",
                                Encoding = "utf-8",
                                LineStart = "",
                            },
                        ],
                    });
                }

                store.MarkSnapshotFailed(failedId, "full_index_failure", "boom");
                // The second snapshot stays 'in_progress', as a crashed run would.

                store.SaveBindingIncompleteness(failedId,
                [
                    new BindingIncompletenessRecord(
                        "TestProject", "src/A.cs",
                        BindingIncompletenessReason.CompilerError, Count: 2,
                        VersionConstants.ExtractorVersion),
                ]);
                store.SaveDiagnostics(failedId,
                [
                    new DiagnosticRecord
                    {
                        ProjectName = "TestProject",
                        DocumentPath = "src/A.cs",
                        Severity = "Error",
                        Id = "CS1234",
                        Message = "x",
                        StartLine = 1,
                        StartColumn = 1,
                        EndLine = 1,
                        EndColumn = 2,
                    },
                ]);
                store.SaveEdges(failedId,
                [
                    new EdgeRecord
                    {
                        SourceSymbolId = "M:A|asm",
                        TargetSymbolId = "M:B|asm",
                        Kind = EdgeKind.Calls.ToString(),
                        Provenance = Provenance.CompilerProved,
                        SnapshotId = failedId,
                        ExtractorVersion = VersionConstants.ExtractorVersion,
                    },
                ]);
                store.SaveTimings(failedId,
                [
                    new SnapshotTimingRow("extraction", 10, DateTime.UtcNow),
                ]);

                store.DeleteIncompleteSnapshots();

                using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();

                    // Failed snapshot: tombstone row retained with reason intact.
                    cmd.CommandText = @"
                        SELECT status, failure_reason_code, failure_message, payload_pruned
                        FROM snapshots WHERE snapshot_id = @id;";
                    cmd.Parameters.AddWithValue("@id", failedId);
                    using var reader = cmd.ExecuteReader();
                    Assert.True(reader.Read(), "Failed snapshot tombstone row must be retained.");
                    Assert.Equal("failed", reader.GetString(0));
                    Assert.Equal("full_index_failure", reader.GetString(1));
                    Assert.Equal("boom", reader.GetString(2));
                    Assert.Equal(1L, reader.GetInt64(3));
                    reader.Close();

                    // Payload reclaimed.
                    cmd.CommandText = "SELECT COUNT(*) FROM binding_incompleteness WHERE snapshot_id = @id;";
                    Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
                    cmd.CommandText = "SELECT COUNT(*) FROM diagnostics WHERE snapshot_id = @id;";
                    Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
                    cmd.CommandText = "SELECT COUNT(*) FROM edges WHERE snapshot_id = @id;";
                    Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
                    cmd.CommandText = "SELECT COUNT(*) FROM snapshot_timings WHERE snapshot_id = @id;";
                    Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
                    cmd.CommandText = "SELECT COUNT(*) FROM snapshot_documents WHERE snapshot_id = @id;";
                    Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
                    cmd.CommandText = "SELECT COUNT(*) FROM projects WHERE snapshot_id = @id;";
                    Assert.Equal(0L, (long)cmd.ExecuteScalar()!);

                    // In-progress snapshot: fully removed (original cleanup preserved).
                    cmd.CommandText = "SELECT COUNT(*) FROM snapshots WHERE snapshot_id = @inProgress;";
                    cmd.Parameters.AddWithValue("@inProgress", inProgressId);
                    Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
                }

                // The failure reason remains readable through the public API.
                var failure = store.GetLatestSnapshotFailure(workspaceId);
                Assert.NotNull(failure);
                Assert.Equal(failedId, failure!.SnapshotId);
                Assert.Equal("full_index_failure", failure.ReasonCode);

                // The payload_pruned marker makes a second run a no-op.
                store.DeleteIncompleteSnapshots();
                using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT failure_reason_code, payload_pruned
                        FROM snapshots WHERE snapshot_id = @id;";
                    cmd.Parameters.AddWithValue("@id", failedId);
                    using var reader = cmd.ExecuteReader();
                    Assert.True(reader.Read());
                    Assert.Equal("full_index_failure", reader.GetString(0));
                    Assert.Equal(1L, reader.GetInt64(1));
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [SkippableFact]
    public async Task RunIncrementalAsync_SemanticDiffWriteFails_DoesNotPublishSnapshot()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");

        var (dbPath, solutionPath, outputDir) = SetupFixture();

        // Step 1: Do a full index to establish a baseline snapshot.
        var firstSnapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        // Step 2: Modify a source file so incremental has something to do.
        var libraryCs = Path.Combine(_testDir!, "Library", "Class1.cs");
        File.AppendAllText(libraryCs, "\n// incremental change\n");
        RunGitCommand(_testDir!, "add -A");
        RunGitCommand(_testDir!, "commit -m change");

        // Step 3: Use a proxy that throws on SaveSemanticChanges.
        using var innerStore = new SqliteIndexStore(dbPath);
        innerStore.Open();
        innerStore.RunMigrations();

        var proxy = DispatchProxy.Create<IIndexStore, ThrowingSemanticDiffStore>();
        ((ThrowingSemanticDiffStore)(object)proxy).SetInner(innerStore);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IndexRunner.RunAsync(
                proxy,
                solutionPath,
                outputDir,
                skipAdapters: [],
                jsonExportPath: null,
                strategyArg: "incremental"));

        Assert.Contains("SaveSemanticChanges", ex.Message);

        // Step 4: Verify no new complete snapshot was created.
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT snapshot_id, status FROM snapshots WHERE status = 'complete' ORDER BY built_at_utc DESC LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        var latestCompleteId = reader.GetString(0);
        Assert.Equal(firstSnapshotId, latestCompleteId);
        reader.Close();

        cmd.CommandText = "SELECT status, failure_reason_code, failure_message FROM snapshots WHERE status = 'failed' ORDER BY built_at_utc DESC LIMIT 1;";
        using var failedReader = cmd.ExecuteReader();
        Assert.True(failedReader.Read());
        Assert.Equal("failed", failedReader.GetString(0));
        Assert.Equal("incremental_index_failure", failedReader.GetString(1));
        Assert.Contains("SaveSemanticChanges", failedReader.GetString(2));
    }

    [Fact]
    public async Task CompilationLoader_NullCompilation_ThrowsWithProjectContext()
    {
        // Create a project with an unsupported language so GetCompilationAsync
        // returns null, verifying the loader throws with project context.
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "NullCompProject",
            "NullCompAssembly",
            "UnsupportedLanguage",
            filePath: "/tmp/NullCompProject.csproj");
        var solution = workspace.CurrentSolution.AddProject(projectInfo);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in CompilationHelper.GetAllAsync(solution))
            {
                // Should not reach here : the loader must throw.
            }
        });

        Assert.Contains("NullCompProject", ex.Message);
        Assert.Contains("Compilation loader", ex.Message);
    }

    private (string DbPath, string SolutionPath, string OutputDir) SetupFixture()
    {
        _testDir = Path.Combine(
            Path.GetTempPath(),
            $"lurp_gate_test_{Guid.NewGuid():N}");

        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "index.db");

        var solutionPath = IntegrationHarness.CopyFixtureToTemp(_testDir);

        RunGitCommand(_testDir, "init");
        RunGitCommand(_testDir, "config user.email test@test.com");
        RunGitCommand(_testDir, "config user.name Test");
        RunGitCommand(_testDir, "add -A");
        RunGitCommand(_testDir, "commit -m init");

        RunDotNetBuild(solutionPath);

        return (_dbPath, solutionPath, _testDir);
    }

    private static void RunGitCommand(string workingDir, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(30_000);
    }

    private static void RunDotNetBuild(string solutionPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"build \"{solutionPath}\" --no-restore -v q")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(120_000);
    }

    private sealed class ThrowingAdapter : IFrameworkAdapter
    {
        public string Name => "ThrowingAdapter";
        public string Version => "1.0.0";

        public List<EdgeRecord> Extract(Compilation compilation, string snapshotId, EdgeLocationResolver locationResolver)
        {
            throw new InvalidOperationException("Adapter intentionally failed for testing.");
        }
    }

    private class ThrowingSemanticDiffStore : DispatchProxy
    {
        private object? _inner;

        public void SetInner(object inner) => _inner = inner;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod != null
                && targetMethod.Name == nameof(IIndexStore.SaveSemanticChanges))
            {
                throw new InvalidOperationException(
                    "Injected failure for testing : SaveSemanticChanges is disabled.");
            }

            return targetMethod?.Invoke(_inner, args);
        }
    }
}
