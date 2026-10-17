using Lurp.Handlers;
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

        var result = await TestConsole.CaptureAsync(() => StatusHandler.Run([
            $"--output-dir={TestDir}", $"--solution={SolutionPath}", "--json", "--max-mismatches=1"
        ]));

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

        var result = await TestConsole.CaptureAsync(() => StatusHandler.Run([$"--output-dir={TestDir}"]));

        Assert.Null(result.Failure);
        Assert.Contains("Latest failed index:", result.Stdout);
        Assert.Contains(snapshotId, result.Stdout);
        Assert.Contains("extraction_failed", result.Stdout);
    }

    [Fact]
    public async Task Status_Json_PrintsNullForKeysWithoutAValue()
    {
        // Never-indexed branch: no database at all.
        var missingDir = Path.Combine(TestDir, "no-db");
        Directory.CreateDirectory(missingDir);
        var neverIndexed = await TestConsole.CaptureAsync(() => StatusHandler.Run([
            $"--output-dir={missingDir}", "--json"
        ]));

        Assert.Null(neverIndexed.Failure);
        using (var doc = JsonDocument.Parse(neverIndexed.Stdout))
            AssertJsonNull(doc.RootElement, "latest_failure");

        // Snapshot-only and freshness branches: an indexed database, no failed snapshot,
        // no pin.
        CreateProject("App", new Dictionary<string, string>
        {
            ["One.cs"] = "namespace App { public class One { public void M() {} } }"
        });
        await RunFullIndexAsync(DbPath);

        var snapshotOnly = await TestConsole.CaptureAsync(() => StatusHandler.Run([
            $"--output-dir={TestDir}", "--json"
        ]));
        Assert.Null(snapshotOnly.Failure);
        using (var doc = JsonDocument.Parse(snapshotOnly.Stdout))
        {
            AssertJsonNull(doc.RootElement, StatusNullKeys);
            Assert.False(doc.RootElement.GetProperty("pin_active").GetBoolean());
        }

        var freshness = await TestConsole.CaptureAsync(() => StatusHandler.Run([
            $"--output-dir={TestDir}", $"--solution={SolutionPath}", "--json"
        ]));
        Assert.Null(freshness.Failure);
        using (var doc = JsonDocument.Parse(freshness.Stdout))
        {
            AssertJsonNull(doc.RootElement, StatusNullKeys);
            Assert.False(doc.RootElement.GetProperty("pin_active").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("mismatches_truncated").GetBoolean());
        }
    }

    private static readonly string[] StatusNullKeys =
        ["latest_failure", "pinned_snapshot_id", "previous_pinned_snapshot_id"];

    private static void AssertJsonNull(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            Assert.True(root.TryGetProperty(key, out var value), $"status --json has no key '{key}'");
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
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

        var snapshotOnly = await TestConsole.CaptureAsync(() => StatusHandler.Run([$"--output-dir={TestDir}"]));
        Assert.Null(snapshotOnly.Failure);
        Assert.Contains($"Pinned snapshot: {snapshotId} (pinned at ", snapshotOnly.Stdout);

        var freshness = await TestConsole.CaptureAsync(() => StatusHandler.Run([
            $"--output-dir={TestDir}", $"--solution={SolutionPath}"
        ]));
        Assert.Null(freshness.Failure);
        Assert.Contains($"Pinned snapshot: {snapshotId} (pinned at ", freshness.Stdout);
    }

    [Fact]
    public async Task Status_UnknownDetail_FailsBeforeReadingDatabase()
    {
        var result = await TestConsole.CaptureAsync(() => StatusHandler.Run([$"--output-dir={TestDir}", "--json", "--detail=bogus"]));

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

        var result = await TestConsole.CaptureAsync(() => StatusHandler.Run([
            $"--output-dir={TestDir}", $"--solution={SolutionPath}", "--json", "--detail=documents,references"
        ]));

        Assert.Null(result.Failure);
        using var doc = JsonDocument.Parse(result.Stdout);
        var manifest = doc.RootElement.GetProperty("manifest");
        Assert.True(manifest.TryGetProperty("document_versions", out var docVers));
        Assert.Equal(JsonValueKind.Object, docVers.ValueKind);
        Assert.True(manifest.TryGetProperty("metadata_reference_identities", out var ids));
        Assert.Equal(JsonValueKind.Object, ids.ValueKind);
    }
}
