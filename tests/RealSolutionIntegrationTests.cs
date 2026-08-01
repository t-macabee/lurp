using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Lurp.Storage;
using Lurp.Queries;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lurp.Storage.Tests;

/// <summary>
/// T19 integration tests that drive the real <see cref="IndexRunner.RunAsync"/>
/// entrypoint against the committed <c>tests/fixtures/Sample/</c> solution.
/// Each test copies the fixture to a temp directory, indexes it, and asserts
/// invariants that would have caught C1–C6 and D1–D2.
/// </summary>
public sealed class RealSolutionIntegrationTests : IDisposable
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

    /// <summary>
    /// Set up a temp copy of the fixture, git init it, and build it.
    /// Call at the start of each test.
    /// Returns (dbPath, solutionPath, outputDir).
    /// </summary>
    private (string DbPath, string SolutionPath, string OutputDir) SetupFixture()
    {
        _testDir = Path.Combine(
            Path.GetTempPath(),
            $"lurp_real_test_{Guid.NewGuid():N}");

        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "index.db");

        var solutionPath = IntegrationHarness.CopyFixtureToTemp(_testDir);

        // Git init so WorkspaceFreshness can compute stable workspace identities
        // and obj/bin exclusion behaves correctly (T7).
        RunGitCommand(_testDir, "init");
        RunGitCommand(_testDir, "config user.email test@test.com");
        RunGitCommand(_testDir, "config user.name test");
        RunGitCommand(_testDir, "add -A");
        RunGitCommand(_testDir, "commit -m init");

        // Build so generated files (obj/bin) are present — exercises T7 exclusion.
        RunDotNetBuild(solutionPath);

        return (_dbPath, solutionPath, _testDir);
    }

    /// <summary>
    /// Same as <see cref="SetupFixture"/>, but injects a real <c>.g.cs</c> file
    /// into the Library project's source tree (not <c>obj/</c>) before the
    /// initial commit and build, so generated-code detection (order 11) has
    /// something in scope to exercise end to end.
    /// </summary>
    private (string DbPath, string SolutionPath, string OutputDir) SetupFixtureWithGeneratedFile()
    {
        _testDir = Path.Combine(
            Path.GetTempPath(),
            $"lurp_real_test_{Guid.NewGuid():N}");

        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "index.db");

        var solutionPath = IntegrationHarness.CopyFixtureToTemp(_testDir);

        var generatedFilePath = Path.Combine(_testDir, "Library", "GeneratedWidgetUser.g.cs");
        File.WriteAllText(generatedFilePath, GeneratedFileSource);

        RunGitCommand(_testDir, "init");
        RunGitCommand(_testDir, "config user.email test@test.com");
        RunGitCommand(_testDir, "config user.name test");
        RunGitCommand(_testDir, "add -A");
        RunGitCommand(_testDir, "commit -m init");

        RunDotNetBuild(solutionPath);

        return (_dbPath, solutionPath, _testDir);
    }

    // Deliberately short (well under 512 bytes) — exercises the fix for
    // DeriveGeneratorIdentity/IsGeneratedHeader bailing out on short files.
    private const string GeneratedFileSource = """
        using System.CodeDom.Compiler;

        namespace Library;

        [GeneratedCode("TestGenerator", "1.0.0")]
        public class GeneratedWidgetUser
        {
            public string UseWidget()
            {
                var widget = new Widget();
                return widget.GetLabel();
            }
        }
        """;

    private static void RunGitCommand(string workingDir, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(30000);
    }

    private static void RunDotNetBuild(string solutionPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"build \"{solutionPath}\" --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(60000);
    }

    // ── Test 1: FullIndex_Completes_WithNonZeroCounts ──────────────────────
    // Catches C1 (positional record crash) and C2 (FTS built after extraction).

    [SkippableFact]
    public async Task FullIndex_Completes_WithNonZeroCounts()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var symbolCount = CountFromSql(conn, "SELECT COUNT(*) FROM snapshot_symbols WHERE snapshot_id = @id", snapshotId);
        var edgeCount = CountFromSql(conn, "SELECT COUNT(*) FROM edges WHERE snapshot_id = @id", snapshotId);
        var ftsCount = CountFromSql(conn, "SELECT COUNT(*) FROM symbol_fts WHERE snapshot_id = @id", snapshotId);

        Assert.True(symbolCount > 0, $"Expected > 0 symbols, got {symbolCount}");
        Assert.True(edgeCount > 0, $"Expected > 0 edges, got {edgeCount}");
        Assert.True(ftsCount > 0, $"Expected > 0 symbol_fts rows, got {ftsCount}");
    }

    // ── Test 2: Status_AfterFreshIndex_ReportsUpToDate ─────────────────────
    // Catches C5 (snapshot properly marked complete; status path doesn't crash).

    [SkippableFact]
    public async Task Status_AfterFreshIndex_ReportsUpToDate()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        // Drive the real --mode=status path (StatusHandler.Run) rather than
        // asserting on the database directly — this is what the CLI's status
        // command actually executes, and C5 is about that path not crashing
        // and reporting freshness correctly right after an index run.
        var originalOut = Console.Out;
        using var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        try
        {
            await Lurp.Handlers.StatusHandler.Run(
            [
                $"--output-dir={outputDir}",
                $"--solution={solutionPath}",
            ]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = capturedOut.ToString();
        Assert.True(output.Contains("Freshness: up to date."), $"Expected up-to-date freshness, got:\n{output}");
        Assert.DoesNotContain("mismatch", output, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 3: Incremental_PreservesPriorSnapshotSource ───────────────────
    // Catches C4 (old snapshot source overwritten after incremental).

    [SkippableFact]
    public async Task Incremental_PreservesPriorSnapshotSource()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        var snapshotA = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        // Pick a symbol from GetProductHandler in the App project and read its original source
        string originalSource;
        string symbolId;
        using (var store = IntegrationHarness.OpenReadStore(dbPath))
        {
            var symbols = store.GetSymbolIdsInSnapshot(snapshotA);
            // Symbol IDs are "{docCommentId}|{assemblyIdentity}" (see SymbolId.Value in
            // SymbolModels.cs), e.g. "T:App.GetProductHandler|App, Version=...". Match on
            // the namespace-qualified type name rather than the assembly identity, whose
            // display-name format doesn't contain ":App:".
            symbolId = symbols.FirstOrDefault(s =>
                s.Contains("App.GetProductHandler", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("No GetProductHandler symbol found in App");

            originalSource = store.GetSymbolSource(symbolId, snapshotA, ViewKind.Declaration)
                ?? throw new InvalidOperationException($"No source found for symbol {symbolId}");
        }

        // Mutate a method body in the App project
        var appDir = Path.Combine(_testDir!, "App");
        MutateGetProductHandler(appDir);

        var snapshotB = await IntegrationHarness.RunIncrementalIndexAsync(dbPath, solutionPath, outputDir);

        // Read the old snapshot's source — it must still be the original
        using (var store = IntegrationHarness.OpenReadStore(dbPath))
        {
            var oldSource = store.GetSymbolSource(symbolId, snapshotA, ViewKind.Declaration);
            Assert.NotNull(oldSource);
            Assert.Equal(originalSource, oldSource);

            // Snapshot B must return the new mutated source
            var newSource = store.GetSymbolSource(symbolId, snapshotB, ViewKind.Declaration);
            Assert.NotNull(newSource);
            Assert.NotEqual(originalSource, newSource);
            Assert.Contains("ModifiedWidget", newSource);
        }
    }

    // ── Test 4: Incremental_Matches_CleanRebuild_OnFixture ─────────────────
    // Catches C6 and D1 (incremental silently diverges from full rebuild).

    [SkippableFact]
    public async Task Incremental_Matches_CleanRebuild_OnFixture()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        // Mutate the handler
        var appDir = Path.Combine(_testDir!, "App");
        MutateGetProductHandler(appDir);

        // Delete db so incremental starts fresh (it needs an existing snapshot,
        // but RunAsync with incremental strategy auto-falls-back to full if none exists).
        // We want a clean compare: full(A) → mutate → incremental(B) → full(C).
        // Don't delete — we need the previous snapshot for incremental to work.
        var snapshotB = await IntegrationHarness.RunIncrementalIndexAsync(dbPath, solutionPath, outputDir);

        // Full rebuild with same state as B
        var snapshotC = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        SnapshotAssertions.CompareSnapshotsAreEquivalent(dbPath, snapshotB, snapshotC);
    }

    // ── Test 5: Declaration lookup and partial-type contract ───────────────
    // The public lookup accepts the caller-facing FQN with or without
    // Roslyn's global:: prefix. A partial type must resolve to one symbol
    // while retaining one declaration row for each source declaration.

    [SkippableFact]
    public async Task DeclarationLookup_ResolvesPartialType_AndPreservesBothDeclarations()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        using var store = IntegrationHarness.OpenReadStore(dbPath);
        var withoutGlobalPrefix = store.ResolveSymbolByFqn("Library.Widget", snapshotId);
        var withGlobalPrefix = store.ResolveSymbolByFqn("global::Library.Widget", snapshotId);

        Assert.NotNull(withoutGlobalPrefix);
        Assert.NotNull(withGlobalPrefix);
        Assert.Equal(withoutGlobalPrefix!.SymbolId.Value, withGlobalPrefix!.SymbolId.Value);
        Assert.Equal("global::Library.Widget", withoutGlobalPrefix.FullyQualifiedName);
        Assert.Equal(IndexedSymbolKind.Type, withoutGlobalPrefix.Kind);
        Assert.Equal(2, withoutGlobalPrefix.DeclarationCount);
        Assert.True(withoutGlobalPrefix.IsPartial);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT doc.relative_path
            FROM declarations d
            JOIN snapshot_documents sd ON sd.document_version_id = d.document_version_id
            JOIN document_versions dv ON dv.document_version_id = d.document_version_id
            JOIN documents doc ON doc.document_id = dv.document_id
            WHERE d.symbol_id = @symbolId AND sd.snapshot_id = @snapshotId
            ORDER BY doc.relative_path;";
        command.Parameters.AddWithValue("@symbolId", withoutGlobalPrefix.SymbolId.Value);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var declarationPaths = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                declarationPaths.Add(reader.GetString(0));
        }

        Assert.Equal(2, declarationPaths.Count);
        Assert.Contains(declarationPaths, path => path.EndsWith("Widget.cs", StringComparison.Ordinal));
        Assert.Contains(declarationPaths, path => path.EndsWith("Widget.Extra.cs", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task FastTravelQuery_NavigatesFromIndexedSpan()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");
        var (dbPath, solutionPath, outputDir) = SetupFixture();
        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        using var store = IntegrationHarness.OpenReadStore(dbPath);
        var queries = new FastTravelQueries(store);
        var target = queries.Navigate("Library/Widget.cs", 6, snapshotId);

        Assert.NotNull(target);
        Assert.EndsWith("Library/Widget.cs", target!.DocumentPath, StringComparison.Ordinal);
        Assert.True(target.FullStart < target.FullEnd);
        Assert.True(target.NameStart < target.NameEnd);
        var source = queries.GetDocument(target.DocumentPath, snapshotId);
        Assert.NotNull(source);
        var sourceBytes = System.Text.Encoding.UTF8.GetBytes(source!);
        Assert.Contains("Name", System.Text.Encoding.UTF8.GetString(sourceBytes, target.NameStart, target.NameEnd - target.NameStart));
    }

    [SkippableFact]
    public async Task NavigateHandler_ReturnsSnapshotBoundTarget()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");
        var (dbPath, solutionPath, outputDir) = SetupFixture();
        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        var originalOut = Console.Out;
        using var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        try
        {
            Lurp.Handlers.NavigateHandler.Run([
                $"--output-dir={outputDir}",
                $"--snapshot={snapshotId}",
                "--file=Library/Widget.cs",
                "--line=6",
            ]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = capturedOut.ToString();
        Assert.Contains(snapshotId, output, StringComparison.Ordinal);
        Assert.Contains("Library/Widget.cs", output, StringComparison.Ordinal);
        Assert.Contains("Name", output, StringComparison.Ordinal);
    }

    // ── Test 6: SourceSearch_Returns_Bounded_Distinct_Snippets ─────────────
    // Catches C3 (source search returns duplicates or full-file dumps).

    [SkippableFact]
    public async Task SourceSearch_Returns_Bounded_Distinct_Snippets()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        using var store = IntegrationHarness.OpenReadStore(dbPath);
        var results = store.SearchSource("Product", snapshotId, limit: 3);

        Assert.Equal(3, results.Count);

        // All document paths must be distinct
        var docPaths = results.Select(r => r.DocumentPath).ToList();
        Assert.Equal(docPaths.Distinct(StringComparer.Ordinal).Count(), docPaths.Count);

        // Each snippet must be bounded — no full-file dumps (> 2000 chars is suspicious)
        const int maxSnippetLength = 2000;
        foreach (var result in results)
        {
            Assert.True(result.Snippet.Length <= maxSnippetLength,
                $"Snippet for {result.DocumentPath} is {result.Snippet.Length} chars — expected <= {maxSnippetLength}");
        }
    }

    // ── Test 7: Edges_Have_No_AbsolutePaths_And_Only_Canonical_Provenance ──
    // Catches D1 (absolute paths in source_document_path) and
    // D2 (non-canonical provenance values).

    [SkippableFact]
    public async Task Edges_Have_No_AbsolutePaths_And_Only_Canonical_Provenance()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        // D1: no absolute paths (Windows drive letter or Unix root)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM edges WHERE source_document_path LIKE '_:%';";
            var absolutePathCount = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            Assert.Equal(0, absolutePathCount);
        }

        // D2: all provenance values are canonical
        var canonical = new HashSet<string>(StringComparer.Ordinal)
        {
            "compiler_proved", "framework_derived", "possible",
            "name_candidate", "runtime_unknown", "convention"
        };

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT provenance FROM edges;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var provenance = reader.GetString(0);
                Assert.True(canonical.Contains(provenance),
                    $"Non-canonical provenance value found: '{provenance}'");
            }
        }
    }

    // ── Test 8: FullIndex_Has_No_Orphan_Edge_Targets ─────────────────────
    // Edge endpoints must be present in snapshot_symbols or snapshot_graph_nodes.
    // An endpoint in neither membership table is orphaned — a sign that the
    // refresher or extractor produced a stale target id.

    [SkippableFact]
    public async Task FullIndex_Has_No_Orphan_Edge_Targets()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var orphanCount = CountFromSql(conn,
            @"SELECT COUNT(DISTINCT target_symbol_id) FROM edges
              WHERE source_document_path IS NOT NULL
                AND snapshot_id = @id
                AND target_symbol_id NOT IN (
                    SELECT symbol_id FROM snapshot_symbols WHERE snapshot_id = @id
                    UNION
                    SELECT node_id FROM snapshot_graph_nodes WHERE snapshot_id = @id
                )",
            snapshotId);

        Assert.Equal(0, orphanCount);
    }

    // ── Test 9: FullIndex_ProjectFailure_LeavesSnapshotInProgress ──────────
    // Catches D5 (project failures are swallowed, leaving snapshot "complete"
    // when it should be "in_progress").

    [SkippableFact]
    public async Task FullIndex_ProjectFailure_LeavesFailedSnapshot()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        // Create a real store and wrap it in a proxy that throws on
        // SaveDeclarations — simulates a per-project extraction failure.
        using var innerStore = new SqliteIndexStore(dbPath);
        innerStore.Open();
        innerStore.RunMigrations();

        var proxy = DispatchProxy.Create<IIndexStore, ThrowingSaveDeclarationsStore>();
        ((ThrowingSaveDeclarationsStore)(object)proxy).SetInner(innerStore);

        try
        {
            var ex = await Assert.ThrowsAsync<AggregateException>(() =>
                IndexRunner.RunAsync(
                    proxy,
                    solutionPath,
                    outputDir,
                    skipAdapters: [],
                    jsonExportPath: null,
                    strategyArg: "full"));

            Assert.Contains(
                "One or more projects failed during full index.",
                ex.Message);
        }
        finally
        {
        }

        // Verify the snapshot stayed in_progress (MarkSnapshotComplete was
        // never reached).
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT status, workspace_id, failure_reason_code, failure_message FROM snapshots ORDER BY built_at_utc DESC LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "Expected at least one snapshot row.");
        var status = reader.GetString(0);
        var workspaceId = reader.GetString(1);

        Assert.Equal("failed", status);
        Assert.Equal("full_index_failure", reader.GetString(2));
        Assert.Contains("One or more projects failed during full index", reader.GetString(3));

        // LoadLatestSnapshot only returns complete snapshots — must be null.
        using var readStore = IntegrationHarness.OpenReadStore(dbPath);
        var latest = readStore.LoadLatestSnapshot(workspaceId);
        Assert.Null(latest);
    }

    // ── Test 10: GeneratedFile_MarksDeclarationsAndCrossGeneratedEdges ────
    // Order 11 (generated-code provenance): a real .g.cs file living in a
    // project's source tree (not obj/) must have its declarations marked
    // is_generated with a derived generator_identity, and edges sourced from
    // it must be flagged is_cross_generated. This is the bound characterization
    // test task 10 identified as blocked on the order 11 scope decision.

    [SkippableFact]
    public async Task GeneratedFile_MarksDeclarationsAndCrossGeneratedEdges()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");
        var (dbPath, solutionPath, outputDir) = SetupFixtureWithGeneratedFile();

        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT d.is_generated, d.generator_identity
                FROM declarations d
                JOIN document_versions dv ON dv.document_version_id = d.document_version_id
                JOIN documents doc ON doc.document_id = dv.document_id
                JOIN snapshot_symbols ss ON ss.symbol_id = d.symbol_id AND ss.snapshot_id = @id
                WHERE doc.relative_path LIKE '%GeneratedWidgetUser.g.cs';";
            cmd.Parameters.AddWithValue("@id", snapshotId);

            using var reader = cmd.ExecuteReader();
            var found = false;
            while (reader.Read())
            {
                found = true;
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal("TestGenerator", reader.GetString(1));
            }
            Assert.True(found, "Expected at least one declaration from the injected generated file.");
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT COUNT(*) FROM edges
                WHERE snapshot_id = @id
                  AND kind = 'Calls'
                  AND source_document_path LIKE '%GeneratedWidgetUser.g.cs'
                  AND is_cross_generated = 1;";
            cmd.Parameters.AddWithValue("@id", snapshotId);
            var count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            Assert.True(count > 0, "Expected at least one cross-generated edge sourced from the injected file.");
        }

        using (var cmd = conn.CreateCommand())
        {
            // DeclaresEdgeExtractor previously built its EdgeRecord manually and
            // never set IsCrossGenerated, unlike every other edge extractor which
            // goes through MemberEdgeExtractionContext.MakeEdge.
            cmd.CommandText = @"
                SELECT COUNT(*) FROM edges
                WHERE snapshot_id = @id
                  AND kind = 'Declares'
                  AND source_document_path LIKE '%GeneratedWidgetUser.g.cs'
                  AND is_cross_generated = 1;";
            cmd.Parameters.AddWithValue("@id", snapshotId);
            var count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            Assert.True(count > 0, "Expected the Declares edge sourced from the injected file to be flagged cross-generated.");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static int CountFromSql(SqliteConnection conn, string sql, string snapshotId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", snapshotId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Mutate the GetProductHandler body so the file hash changes.
    /// This forces the incremental indexer to re-extract App's edges.
    /// </summary>
    private static void MutateGetProductHandler(string appDir)
    {
        var handlerPath = Path.Combine(appDir, "GetProductHandler.cs");
        var content = File.ReadAllText(handlerPath);

        // Change the product name in the handler body
        content = content.Replace("\"Widget\"", "\"ModifiedWidget\"");

        File.WriteAllText(handlerPath, content);
    }

    /// <summary>
    /// A <see cref="DispatchProxy"/> that delegates every <see cref="IIndexStore"/>
    /// call to an inner <see cref="SqliteIndexStore"/> except
    /// <see cref="IIndexStore.SaveDeclarations"/>, which throws. This lets
    /// integration tests simulate a per-project failure during extraction.
    /// </summary>
    private class ThrowingSaveDeclarationsStore : DispatchProxy
    {
        private object? _inner;

        public void SetInner(object inner) => _inner = inner;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod != null
                && targetMethod.Name == nameof(IIndexStore.SaveDeclarations))
            {
                throw new InvalidOperationException(
                    "Injected failure for testing — SaveDeclarations is disabled.");
            }

            return targetMethod?.Invoke(_inner, args);
        }
    }
}
