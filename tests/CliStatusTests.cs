using Lurp.Handlers;
using Lurp.Storage;
using System.Text.Json;

namespace Lurp.Tests;

/// <summary>
///     CLI coverage for <c>--mode=status</c>: the registry accepts
///     <c>--max-mismatches=</c> (and rejects the removed <c>--max-documents=</c>),
///     and the documented cap is enforced on the freshness report.
/// </summary>
public sealed class CliStatusTests : IntegrationTestBase
{
    [Fact]
    public void StatusRegistry_AcceptsMaxMismatches_RejectsMaxDocuments()
    {
        var entry = Program.FindMode("status")!;

        CliFlagValidation.Validate(entry, ["--output-dir=.", "--max-mismatches=5"]);

        var ex = Assert.Throws<CliExitException>(() =>
            CliFlagValidation.Validate(entry, ["--output-dir=.", "--max-documents=5"]));
        Assert.Contains("unknown flag '--max-documents='", ex.Message);
    }

    [Fact]
    public async Task Status_MaxMismatches_CapsMismatchesAndSetsTruncated()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["One.cs"] = "namespace App { public class One { public void M() {} } }",
            ["Two.cs"] = "namespace App { public class Two { public void M() {} } }",
            ["Three.cs"] = "namespace App { public class Three { public void M() {} } }"
        });
        await RunFullIndexAsync(DbPath);

        WriteFile("App", "One.cs", "namespace App { public class One { public void M(int x) {} } }");
        WriteFile("App", "Two.cs", "namespace App { public class Two { public void M(int x) {} } }");
        WriteFile("App", "Three.cs", "namespace App { public class Three { public void M(int x) {} } }");

        var result = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([
            $"--output-dir={TestDir}", $"--solution={SolutionPath}", "--json", "--max-mismatches=1"
        ], sink));

        Assert.Null(result.Failure);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.False(doc.RootElement.GetProperty("is_fresh").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("mismatches").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("mismatches_truncated").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("mismatches_total").GetInt32() > 1);
    }

    [Fact]
    public async Task Status_Text_ShowsLatestFailedIndex()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["One.cs"] = "namespace App { public class One { public void M() {} } }"
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        using (var store = OpenStore(DbPath))
            store.MarkSnapshotFailed(snapshotId, "extraction_failed", "adapter threw");

        var result = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([$"--output-dir={TestDir}"], sink));

        Assert.Null(result.Failure);
        Assert.Contains("Latest failed index:", result.Stdout);
        Assert.Contains(snapshotId, result.Stdout);
        Assert.Contains("extraction_failed", result.Stdout);
    }

    private static readonly string[] ExpectedStatusJsonKeys =
    [
        "database_path",
        "database_exists",
        "schema_version",
        "schema_version_expected",
        "schema_version_mismatch",
        "schema_version_note",
        "contract_version",
        "indexed",
        "latest_snapshot_id",
        "effective_latest_snapshot_id",
        "selected_latest_snapshot_id",
        "pinned_snapshot_id",
        "pinned_at_utc",
        "previous_pinned_snapshot_id",
        "stale_pinned_snapshot_id",
        "pin_active",
        "freshness_checked",
        "note",
        "is_fresh",
        "mismatches",
        "mismatches_truncated",
        "mismatches_total",
        "timing_summary",
        "timing_total_ms",
        "manifest",
        "latest_failure"
    ];

    [Fact]
    public async Task Status_ExplicitMissingSolution_FailsBeforeTheDatabase()
    {
        var missingDir = Path.Combine(TestDir, "empty-out");
        Directory.CreateDirectory(missingDir);
        var missingSolution = Path.Combine(TestDir, "missing", "x.slnx");

        var result = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([
            "--json",
            $"--solution={missingSolution}",
            $"--output-dir={missingDir}"
        ], sink));

        Assert.NotNull(result.Failure);
        Assert.Equal(1, result.Failure!.ExitCode);
        Assert.Equal($"ERROR: --solution=<path> must point to an existing file: '{missingSolution}'.", result.Failure.Message);
    }

    [Fact]
    public async Task Status_InvalidMaxMismatches_FailsWithoutADatabase()
    {
        var missingDir = Path.Combine(TestDir, "empty-out-max");
        Directory.CreateDirectory(missingDir);

        var result = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([
            $"--output-dir={missingDir}",
            "--json",
            "--max-mismatches=0"
        ], sink));

        Assert.NotNull(result.Failure);
        Assert.Equal(1, result.Failure!.ExitCode);
        Assert.Equal("ERROR: --max-mismatches must be a positive integer.", result.Failure.Message);
    }

    [Fact]
    public async Task Status_MissingSolutionFromTheEnvironment_IsNoCheck()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["One.cs"] = "namespace App { public class One { public void M() {} } }"
        });
        await RunFullIndexAsync(DbPath);

        var prior = Environment.GetEnvironmentVariable("LURP_SOLUTION_PATH");
        Environment.SetEnvironmentVariable("LURP_SOLUTION_PATH", Path.Combine(TestDir, "missing.slnx"));
        try
        {
            var result = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([
                $"--output-dir={TestDir}", "--json"
            ], sink));

            Assert.Null(result.Failure);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.False(doc.RootElement.GetProperty("freshness_checked").GetBoolean());
            Assert.Equal(
                "Pass --solution=path or set LURP_SOLUTION_PATH to check freshness against the current workspace.",
                doc.RootElement.GetProperty("note").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LURP_SOLUTION_PATH", prior);
        }
    }

    [Fact]
    public async Task Status_Json_EveryBranchPrintsTheSameKeys()
    {
        // 1. No database
        var noDbDir = Path.Combine(TestDir, "no-db");
        Directory.CreateDirectory(noDbDir);
        var noDb = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([
            $"--output-dir={noDbDir}", "--json"
        ], sink));
        Assert.Null(noDb.Failure);
        using (var doc = JsonDocument.Parse(noDb.Stdout))
        {
            var root = doc.RootElement;
            Assert.Equal(ExpectedStatusJsonKeys, root.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.False(root.GetProperty("database_exists").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("schema_version_mismatch").ValueKind);
            Assert.False(root.GetProperty("indexed").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("pin_active").ValueKind);
            Assert.False(root.GetProperty("freshness_checked").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("note").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("is_fresh").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("latest_failure").ValueKind);
        }

        // 2. Database with a newer schema version (schema mismatch)
        var mismatchDir = Path.Combine(TestDir, "mismatch-db");
        Directory.CreateDirectory(mismatchDir);
        var mismatchDbPath = Path.Combine(mismatchDir, "index.db");
        new MigrationRunner(mismatchDbPath).RunMigrations();
        using (var conn = TestDatabase.Open(mismatchDbPath))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO schema_metadata (version, applied_at_utc, migration_id) VALUES ({MigrationRunner.LatestVersion + 1}, '2030-01-01T00:00:00.0000000Z', 'FutureMigration');";
            cmd.ExecuteNonQuery();
        }
        var mismatch = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([
            $"--output-dir={mismatchDir}", "--json"
        ], sink));
        Assert.Null(mismatch.Failure);
        using (var doc = JsonDocument.Parse(mismatch.Stdout))
        {
            var root = doc.RootElement;
            Assert.Equal(ExpectedStatusJsonKeys, root.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.True(root.GetProperty("database_exists").GetBoolean());
            Assert.True(root.GetProperty("schema_version_mismatch").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("indexed").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("pin_active").ValueKind);
            Assert.False(root.GetProperty("freshness_checked").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("note").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("is_fresh").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("latest_failure").ValueKind);
        }

        // 3. Database with no snapshot (never indexed)
        var emptySchemaDir = Path.Combine(TestDir, "empty-schema-db");
        Directory.CreateDirectory(emptySchemaDir);
        new MigrationRunner(Path.Combine(emptySchemaDir, "index.db")).RunMigrations();
        var emptySchema = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([
            $"--output-dir={emptySchemaDir}", "--json"
        ], sink));
        Assert.Null(emptySchema.Failure);
        using (var doc = JsonDocument.Parse(emptySchema.Stdout))
        {
            var root = doc.RootElement;
            Assert.Equal(ExpectedStatusJsonKeys, root.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.True(root.GetProperty("database_exists").GetBoolean());
            Assert.False(root.GetProperty("schema_version_mismatch").GetBoolean());
            Assert.False(root.GetProperty("indexed").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("pin_active").ValueKind);
            Assert.False(root.GetProperty("freshness_checked").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("note").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("is_fresh").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("latest_failure").ValueKind);
        }

        // 4 & 5. Indexed database without --solution, then with --solution
        CreateProject("App", new Dictionary<string, string>
        {
            ["One.cs"] = "namespace App { public class One { public void M() {} } }"
        });
        await RunFullIndexAsync(DbPath);

        var snapshotOnly = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([
            $"--output-dir={TestDir}", "--json"
        ], sink));
        Assert.Null(snapshotOnly.Failure);
        using (var doc = JsonDocument.Parse(snapshotOnly.Stdout))
        {
            var root = doc.RootElement;
            Assert.Equal(ExpectedStatusJsonKeys, root.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.True(root.GetProperty("database_exists").GetBoolean());
            Assert.False(root.GetProperty("schema_version_mismatch").GetBoolean());
            Assert.True(root.GetProperty("indexed").GetBoolean());
            Assert.False(root.GetProperty("pin_active").GetBoolean());
            Assert.False(root.GetProperty("freshness_checked").GetBoolean());
            Assert.Equal(
                "Pass --solution=path or set LURP_SOLUTION_PATH to check freshness against the current workspace.",
                root.GetProperty("note").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("is_fresh").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("latest_failure").ValueKind);
        }

        var freshness = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([
            $"--output-dir={TestDir}", $"--solution={SolutionPath}", "--json"
        ], sink));
        Assert.Null(freshness.Failure);
        using (var doc = JsonDocument.Parse(freshness.Stdout))
        {
            var root = doc.RootElement;
            Assert.Equal(ExpectedStatusJsonKeys, root.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.True(root.GetProperty("database_exists").GetBoolean());
            Assert.False(root.GetProperty("schema_version_mismatch").GetBoolean());
            Assert.True(root.GetProperty("indexed").GetBoolean());
            Assert.False(root.GetProperty("pin_active").GetBoolean());
            Assert.True(root.GetProperty("freshness_checked").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("note").ValueKind);
            Assert.True(root.GetProperty("is_fresh").GetBoolean());
            Assert.False(root.GetProperty("mismatches_truncated").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("latest_failure").ValueKind);
        }
    }

    [Fact]
    public async Task Status_Text_PrintsThePinBlockInBothBranches()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["One.cs"] = "namespace App { public class One { public void M() {} } }"
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        using (var store = OpenStore(DbPath))
            store.SetPinnedSnapshot(snapshotId);

        var snapshotOnly = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([$"--output-dir={TestDir}"], sink));
        Assert.Null(snapshotOnly.Failure);
        Assert.Contains($"Pinned snapshot: {snapshotId} (pinned at ", snapshotOnly.Stdout);

        var freshness = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([
            $"--output-dir={TestDir}", $"--solution={SolutionPath}"
        ], sink));
        Assert.Null(freshness.Failure);
        Assert.Contains($"Pinned snapshot: {snapshotId} (pinned at ", freshness.Stdout);
    }

    [Fact]
    public async Task Status_UnknownDetail_FailsBeforeReadingDatabase()
    {
        var result = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([$"--output-dir={TestDir}", "--json", "--detail=bogus"], sink));

        Assert.NotNull(result.Failure);
        Assert.Contains("--detail", result.Failure!.Message);
        Assert.Contains("documents, references, completeness or all", result.Failure.Message);
    }

    [Fact]
    public async Task Status_DetailUnion_RestoresDocumentsAndReferences()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["One.cs"] = "namespace App { public class One { public void M() {} } }"
        });
        await RunFullIndexAsync(DbPath);

        var result = await CapturingOutputSink.CaptureAsync(sink => StatusHandler.Run([
            $"--output-dir={TestDir}", $"--solution={SolutionPath}", "--json", "--detail=documents,references"
        ], sink));

        Assert.Null(result.Failure);
        using var doc = JsonDocument.Parse(result.Stdout);
        var manifest = doc.RootElement.GetProperty("manifest");
        Assert.True(manifest.TryGetProperty("document_versions", out var docVers));
        Assert.Equal(JsonValueKind.Object, docVers.ValueKind);
        Assert.True(manifest.TryGetProperty("metadata_reference_identities", out var ids));
        Assert.Equal(JsonValueKind.Object, ids.ValueKind);
    }
}
