using System.Text.Json;
using Lurp.Parity.Shared;

namespace Lurp.Tests.Mcp;

public sealed class McpStdioPurityTests : IntegrationTestBase
{
    private static string? ExtractInnerText(JsonElement result)
    {
        // MCP SDK wraps string returns as content[0].text
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0)
        {
            var first = content[0];
            if (first.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                return text.GetString();
            if (first.TryGetProperty("data", out var data)) return data.ToString();
        }
        if (result.TryGetProperty("structuredContent", out var sc)) return sc.ToString();
        // Fallback: result itself may be the payload if SDK does not wrap
        return result.ToString();
    }

    private static JsonDocument? TryParseInnerJson(JsonElement result, out string? innerText)
    {
        innerText = ExtractInnerText(result);
        if (innerText == null) return null;
        try { return JsonDocument.Parse(innerText); } catch { return null; }
    }

    [Fact]
    public async Task StdioPurity_FullToolSurface_NoStdoutLeak_AndJsonOnly()
    {
        // Build small fast fixture with 2 snapshots for diff
        CreateProject("PurityProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace PurityProj {
                    public class Foo {
                        public void Bar() {}
                        public void Caller() { Bar(); }
                    }
                    public class BarUtil {
                        public void Use() { new Foo().Bar(); }
                    }
                }
                """,
            ["Extra.cs"] = """
                namespace PurityProj {
                    public class Extra {
                        public void ExtraMethod() {}
                    }
                }
                """
        });
        var snap1 = await RunFullIndexAsync(DbPath);

        // Second snapshot for diff
        WriteFile("PurityProj", "Extra.cs", """
            namespace PurityProj {
                public class Extra {
                    public void ExtraMethod() {}
                    public void AddedInSecond() {}
                }
            }
            """);
        var snap2 = await RunIncrementalIndexAsync();

        // Gather known identifiers from snap2 (pinned)
        string symbolId;
        string docPath;
        string fqn;
        using (var store = OpenStore(DbPath))
        {
            var ids = store.GetSymbolIdsInSnapshot(snap2);
            symbolId = ids.First(id => {
                var info = store.GetSymbolInfo(id, snap2);
                return info != null && info.FullyQualifiedName != null && info.FullyQualifiedName.Contains("PurityProj.Foo");
            });
            var infoFound = store.GetSymbolInfo(symbolId, snap2)!;
            fqn = infoFound.FullyQualifiedName ?? symbolId;
            var docs = store.GetDocumentVersionIdsByPath(snap2);
            // Pick a doc that exists
            docPath = docs.Keys.First(k => k.Contains("Models.cs"));
            // Prefer forward slash form
            docPath = docPath.Replace("\\", "/");
        }

        var outputDir = Path.GetDirectoryName(DbPath)!;

        var stdoutLines = new List<string>();
        var stdoutLock = new object();

        var server = McpServeProcess.Start(
            LurpTool.Argv("--mode=serve", "--enable-write-tools", $"--output-dir={outputDir}", $"--solution={SolutionPath}"),
            line =>
            {
                lock (stdoutLock)
                    stdoutLines.Add(line);
            });

        try
        {
            // Give process a moment to start and emit pinned line to stderr
            await Task.Delay(500);

            var timeout = TimeSpan.FromSeconds(15);

            // Initialize
            var initResp = await server.Client.InitializeAsync("test", timeout);
            Assert.True(initResp.TryGetProperty("result", out _), "initialize must return result");

            // Small delay to let server process initialized
            await Task.Delay(200);

            // Helper to call tool
            async Task<JsonElement> CallTool(string name, object? args)
            {
                var toolParams = new { name = name, arguments = args ?? new { } };
                var resp = await server.Client.SendAsync("tools/call", toolParams, timeout);
                Assert.True(resp.TryGetProperty("result", out var result), $"tools/call {name} must have result");
                // Check for isError false
                if (result.TryGetProperty("isError", out var isErr) && isErr.ValueKind == JsonValueKind.True)
                {
                    var text = ExtractInnerText(result) ?? result.ToString();
                    throw new InvalidOperationException($"Tool {name} returned isError=true: {text}");
                }
                // Clone result for caller
                return result.Clone();
            }

            // 1. lurp_search
            {
                var res = await CallTool("lurp_search", new { query = "Foo", type = "all" });
                var innerText = ExtractInnerText(res);
                Assert.False(string.IsNullOrEmpty(innerText));
                using var inner = JsonDocument.Parse(innerText!);
                Assert.True(inner.RootElement.TryGetProperty("results", out _));
            }

            // 2. lurp_find_symbol
            {
                var res = await CallTool("lurp_find_symbol", new { symbol = "PurityProj.Foo" });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                Assert.True(inner.RootElement.TryGetProperty("snapshot_id", out _));
            }

            // 3. lurp_get_symbol
            {
                var res = await CallTool("lurp_get_symbol", new { symbol = symbolId, view = "summary" });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                Assert.True(inner.RootElement.TryGetProperty("snapshot_id", out _));
            }

            // 4. lurp_get_annotations
            {
                var res = await CallTool("lurp_get_annotations", new { symbol = symbolId });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                Assert.True(inner.RootElement.TryGetProperty("annotations", out _));
            }

            // 5. lurp_get_source
            {
                var res = await CallTool("lurp_get_source", new { document = docPath });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                Assert.True(inner.RootElement.TryGetProperty("source", out _));
            }

            // 6. lurp_navigate
            {
                var res = await CallTool("lurp_navigate", new { file = docPath, line = 3 });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                // navigate returns target
                Assert.True(inner.RootElement.TryGetProperty("snapshot_id", out _) || inner.RootElement.TryGetProperty("target", out _));
            }

            // 7. lurp_context
            {
                var res = await CallTool("lurp_context", new { symbol = symbolId });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                Assert.True(inner.RootElement.TryGetProperty("capsule", out _));
            }

            // 8. lurp_impact
            {
                var res = await CallTool("lurp_impact", new { symbol = symbolId, direction = "downstream", max_depth = 3 });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                Assert.True(inner.RootElement.TryGetProperty("symbols", out _));
            }

            // 9. lurp_diff (from snap1 to snap2)
            {
                var res = await CallTool("lurp_diff", new { from_snapshot = snap1, to_snapshot = snap2 });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                Assert.True(inner.RootElement.TryGetProperty("changes", out _));
            }

            // 10. lurp_status
            {
                var res = await CallTool("lurp_status", new { detail = true });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                Assert.True(inner.RootElement.TryGetProperty("snapshot_id", out _));
                Assert.True(inner.RootElement.TryGetProperty("freshness", out _));
            }

            // 11. lurp_timings
            {
                var res = await CallTool("lurp_timings", new { snapshot_id = snap2 });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                Assert.True(inner.RootElement.TryGetProperty("snapshot_id", out _));
            }

            // 12. lurp_refresh (no ack)
            {
                var res = await CallTool("lurp_refresh", new { });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                Assert.True(inner.RootElement.TryGetProperty("new_snapshot_id", out _));
            }

            // Prepare file change for lurp_index incremental
            WriteFile("PurityProj", "Extra.cs", """
                namespace PurityProj {
                    public class Extra {
                        public void ExtraMethod() {}
                        public void AddedInSecond() {}
                        public void AddedForIndex() {}
                    }
                }
                """);

            // 13. lurp_index incremental through to completion
            string? newSnapshotId = null;
            string operationId;
            {
                var res = await CallTool("lurp_index", new { strategy = "incremental" });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                Assert.True(inner.RootElement.TryGetProperty("operation_id", out var opEl), "lurp_index start must return operation_id");
                Assert.True(inner.RootElement.TryGetProperty("status", out var stEl) && stEl.GetString() == "running");
                operationId = opEl.GetString()!;
                Assert.False(string.IsNullOrEmpty(operationId));
            }

            // Poll for completion (up to 60s)
            var pollTimeout = TimeSpan.FromSeconds(60);
            var pollStart = DateTime.UtcNow;
            while (DateTime.UtcNow - pollStart < pollTimeout)
            {
                await Task.Delay(500);
                var res = await CallTool("lurp_index", new { operation_id = operationId });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                Assert.True(inner.RootElement.TryGetProperty("status", out var stEl));
                var status = stEl.GetString();
                if (status == "completed")
                {
                    if (inner.RootElement.TryGetProperty("result_snapshot_id", out var rs) && rs.ValueKind == JsonValueKind.String)
                        newSnapshotId = rs.GetString();
                    break;
                }
                else if (status == "failed")
                {
                    var err = inner.RootElement.TryGetProperty("error", out var e) ? e.GetString() : innerText;
                    throw new InvalidOperationException($"lurp_index failed: {err}");
                }
                else if (status == "cancelled")
                {
                    throw new InvalidOperationException("lurp_index was cancelled unexpectedly");
                }
                // else running, continue
            }
            Assert.False(string.IsNullOrEmpty(newSnapshotId), "lurp_index did not complete with new snapshot within timeout");
            Assert.NotEqual(snap2, newSnapshotId);

            // Verify refresh now reports changed and ack advances pin
            {
                var res = await CallTool("lurp_refresh", new { });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                Assert.True(inner.RootElement.TryGetProperty("changed", out var ch) && ch.GetBoolean() == true);
                Assert.True(inner.RootElement.TryGetProperty("new_snapshot_id", out var nid) && nid.GetString() == newSnapshotId);
            }
            {
                var res = await CallTool("lurp_refresh", new { ack = newSnapshotId });
                var innerText = ExtractInnerText(res);
                using var inner = JsonDocument.Parse(innerText!);
                // After ack, changed should be false or new pin advanced
                Assert.True(inner.RootElement.TryGetProperty("new_snapshot_id", out _));
            }

            // Allow stderr to flush
            await Task.Delay(500);
            server.Close(3000);

            // Final purity assertions
            string[] stdoutCopy;
            lock (stdoutLock) stdoutCopy = stdoutLines.ToArray();
            var stderrCombined = server.Stderr;

            Assert.True(stdoutCopy.Length > 0, $"No stdout captured. stderr: {stderrCombined}");
            foreach (var line in stdoutCopy)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var ex = Record.Exception(() => JsonDocument.Parse(line));
                Assert.True(ex == null, $"Stdout line is not valid JSON (stdout purity violated): {line}\nStderr: {stderrCombined}");
            }

            Assert.Contains("mcp: pinned snapshot", stderrCombined);
            var stdoutCombined = string.Join("\n", stdoutCopy);
            Assert.DoesNotContain("mcp: pinned snapshot", stdoutCombined);

            // Stdout purity is already proven by the per-line JsonDocument.Parse above;
            // the following ensures the pinned line is on stderr only, not stdout.
            // Progress text like "Loading solution" is expected inside the JSON result's
            // content[0].text (structured via McpIndexOutputSink) and is not a leak when
            // wrapped in JSON — the leak would be a raw non-JSON line on stdout.
        }
        finally
        {
            server.Dispose();
        }
    }
}
