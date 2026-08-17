using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Lurp.Tests.Mcp;

public sealed class McpStdioPurityTests : IntegrationTestBase
{
    private string FindLurpDll()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "Lurp.dll");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        var cur = Directory.GetCurrentDirectory();
        while (cur != null)
        {
            var bin = Path.Combine(cur, "src", "bin");
            if (Directory.Exists(bin))
            {
                var matches = Directory.GetFiles(bin, "Lurp.dll", SearchOption.AllDirectories);
                if (matches.Length > 0)
                    return matches.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
            }
            cur = Directory.GetParent(cur)?.FullName;
        }
        throw new InvalidOperationException("Could not locate Lurp.dll for stdio purity test.");
    }

    private string FindDotnet()
    {
        var winPath = "/mnt/c/Program Files/dotnet/dotnet.exe";
        if (File.Exists(winPath)) return winPath;
        var winPath2 = "C:\\Program Files\\dotnet\\dotnet.exe";
        if (File.Exists(winPath2)) return winPath2;
        return "dotnet";
    }

    private static async Task<JsonDocument> SendRequestAsync(Process process, List<string> stdoutLines, object stdoutLock, int id, string method, object? paramsObj, TimeSpan timeout)
    {
        var req = new { jsonrpc = "2.0", id = id, method = method, @params = paramsObj };
        var json = JsonSerializer.Serialize(req);
        await process.StandardInput.WriteLineAsync(json);
        await process.StandardInput.FlushAsync();

        var cts = new CancellationTokenSource(timeout);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            string? line;
            try
            {
                // Use timeout per read
                var readTask = process.StandardOutput.ReadLineAsync();
                var completed = await Task.WhenAny(readTask, Task.Delay(timeout, cts.Token));
                if (completed != readTask) throw new TimeoutException($"Timed out waiting for response id={id} method={method}");
                line = await readTask;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out waiting for response id={id} method={method}");
            }

            if (line == null) throw new InvalidOperationException($"Process stdout closed before response id={id} method={method}");
            if (string.IsNullOrWhiteSpace(line)) continue;

            lock (stdoutLock) stdoutLines.Add(line);

            // Purity check: every stdout line must be valid JSON
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Stdout line is not valid JSON (leak): {line}", ex);
            }

            // If this line is the response for our id, return it
            if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number && idProp.GetInt32() == id)
            {
                return doc;
            }
            else
            {
                // Notification or other response; continue waiting but we already validated JSON
                doc.Dispose();
                continue;
            }
        }
        throw new TimeoutException($"No response for id={id} method={method} within {timeout}");
    }

    private static async Task SendNotificationAsync(Process process, string method, object? paramsObj)
    {
        var req = new { jsonrpc = "2.0", method = method, @params = paramsObj };
        var json = JsonSerializer.Serialize(req);
        await process.StandardInput.WriteLineAsync(json);
        await process.StandardInput.FlushAsync();
    }

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

        var lurpDll = FindLurpDll();
        var dotnet = FindDotnet();
        var outputDir = Path.GetDirectoryName(DbPath)!;

        var psi = new ProcessStartInfo
        {
            FileName = dotnet,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(lurpDll);
        psi.ArgumentList.Add("--mode=serve");
        psi.ArgumentList.Add($"--output-dir={outputDir}");
        psi.ArgumentList.Add($"--solution={SolutionPath}");

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        var stdoutLock = new object();
        var stderrLock = new object();

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Lurp serve subprocess");
        // Stderr reader
        var stderrReader = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) != null)
            {
                lock (stderrLock) stderrLines.Add(line);
            }
        });

        try
        {
            // Give process a moment to start and emit pinned line to stderr
            await Task.Delay(500);

            var timeout = TimeSpan.FromSeconds(15);
            int id = 1;

            // Initialize
            var initParams = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "test", version = "1.0" }
            };
            using var initResp = await SendRequestAsync(process, stdoutLines, stdoutLock, id++, "initialize", initParams, timeout);
            Assert.True(initResp.RootElement.TryGetProperty("result", out _), "initialize must return result");

            // Send initialized notification (no response expected)
            await SendNotificationAsync(process, "notifications/initialized", new { });

            // Small delay to let server process initialized
            await Task.Delay(200);

            // Helper to call tool
            async Task<JsonElement> CallTool(string name, object? args)
            {
                var toolParams = new { name = name, arguments = args ?? new { } };
                using var resp = await SendRequestAsync(process, stdoutLines, stdoutLock, id++, "tools/call", toolParams, timeout);
                Assert.True(resp.RootElement.TryGetProperty("result", out var result), $"tools/call {name} must have result");
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
                Assert.True(inner.RootElement.TryGetProperty("paths", out _));
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
            try { process.StandardInput.Close(); } catch { }
            // Give process time to exit gracefully
            var exited = process.WaitForExit(3000);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit(2000);
            }

            // Final purity assertions
            string[] stdoutCopy;
            lock (stdoutLock) stdoutCopy = stdoutLines.ToArray();
            string[] stderrCopy;
            lock (stderrLock) stderrCopy = stderrLines.ToArray();

            Assert.True(stdoutCopy.Length > 0, $"No stdout captured. stderr: {string.Join("\n", stderrCopy)}");
            foreach (var line in stdoutCopy)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var ex = Record.Exception(() => JsonDocument.Parse(line));
                Assert.True(ex == null, $"Stdout line is not valid JSON (stdout purity violated): {line}\nStderr: {string.Join("\n", stderrCopy)}");
            }

            var stderrCombined = string.Join("\n", stderrCopy);
            Assert.Contains("mcp: pinned snapshot", stderrCombined);
            var stdoutCombined = string.Join("\n", stdoutCopy);
            Assert.DoesNotContain("mcp: pinned snapshot", stdoutCombined);

            // Stdout purity is already proven by the per-line JsonDocument.Parse above;
            // the following ensures the pinned line is on stderr only, not stdout.
            // Progress text like "Loading solution" is expected inside the JSON result's
            // content[0].text (structured via McpIndexOutputSink) and is not a leak when
            // wrapped in JSON — the leak would be a raw non-JSON line on stdout.

            // Wait for stderr reader to finish
            try { await stderrReader.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { process.Dispose(); } catch { }
            try { await stderrReader.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
    }
}
