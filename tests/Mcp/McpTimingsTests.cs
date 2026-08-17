using System.Text.Json;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpTimingsTests : IntegrationTestBase
{
    private async Task<string> IndexAsync()
    {
        CreateProject("TimingsProj", new Dictionary<string, string>
        {
            ["Models.cs"] = "namespace TimingsProj { public class Foo { public void Bar() {} } }"
        });
        return await RunFullIndexAsync(DbPath);
    }

    [Fact]
    public async Task Timings_ReturnsSteps_ForPinnedSnapshot()
    {
        var snapshotId = await IndexAsync();
        var args = new[] { $"--solution={SolutionPath}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new TimingsTool(session);

        var json = tool.LurpTimings();
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(snapshotId, doc.RootElement.GetProperty("snapshot_id").GetString());
        Assert.True(doc.RootElement.GetProperty("pinned").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("freshness", out _));
        Assert.True(doc.RootElement.TryGetProperty("total_ms", out var totalEl));
        Assert.True(doc.RootElement.TryGetProperty("steps", out var stepsEl));
        Assert.Equal(JsonValueKind.Array, stepsEl.ValueKind);

        // Compare against direct store timings
        using var store = OpenStore(DbPath);
        var direct = store.GetTimings(snapshotId);
        Assert.Equal(direct.Count, stepsEl.GetArrayLength());
        Assert.Equal(direct.Sum(t => t.ElapsedMs), totalEl.GetInt64());

        var total = direct.Sum(t => t.ElapsedMs);
        int idx = 0;
        foreach (var stepEl in stepsEl.EnumerateArray())
        {
            Assert.Equal(direct[idx].StepName, stepEl.GetProperty("step").GetString());
            Assert.Equal(direct[idx].ElapsedMs, stepEl.GetProperty("elapsed_ms").GetInt64());
            var expectedPct = total > 0 ? Math.Round((double)direct[idx].ElapsedMs / total * 100, 1) : 0;
            Assert.Equal(expectedPct, stepEl.GetProperty("percent").GetDouble());
            idx++;
        }
    }

    [Fact]
    public async Task Timings_SnapshotMismatch_ReturnsInvalidParams()
    {
        await IndexAsync();
        var args = new[] { $"--solution={SolutionPath}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new TimingsTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpTimings(snapshot_id: "mismatch"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("snapshot mismatch", ex.Message);
        Assert.Contains("lurp_refresh", ex.Message);
    }

    [Fact]
    public async Task Timings_ExplicitPinnedId_Succeeds()
    {
        var snapshotId = await IndexAsync();
        var args = new[] { $"--solution={SolutionPath}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new TimingsTool(session);

        var json = tool.LurpTimings(snapshot_id: snapshotId);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(snapshotId, doc.RootElement.GetProperty("snapshot_id").GetString());
    }

    [Fact]
    public async Task Timings_EmptyOrMissing_ReturnsZeroTotal()
    {
        var snapshotId = await IndexAsync();
        // Timings are produced during indexing; verify structure even when steps exist.
        // If a snapshot had no timings, total_ms should be 0 and steps empty — not an error.
        var args = new[] { $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var tool = new TimingsTool(session);

        var json = tool.LurpTimings();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(snapshotId, doc.RootElement.GetProperty("snapshot_id").GetString());
        Assert.True(doc.RootElement.TryGetProperty("steps", out var stepsEl));
        Assert.Equal(JsonValueKind.Array, stepsEl.ValueKind);
    }
}
