using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     Audit B2 read-only guarantee: the analyzed tree must be byte-identical
///     before and after the full surface runs. The Phase 5 CallShapes corpus is
///     copied to temp, restored (outside the measured window), hashed, then every
///     CLI mode and every MCP tool runs against it. `LURP_TOOL_PATH` selects the
///     packed tool for CI; otherwise the locally built `Lurp.dll` is used. The
///     allowlist is empty: any added, removed, or changed file fails the test.
/// </summary>
public sealed class TargetTreeIntegrityTests
{
    private static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task CallShapesTree_IsByteIdentical_AfterEveryCliModeAndMcpTool()
    {
        var fixtureRoot = LocateFixtureRoot();
        var testDir = Path.Combine(Path.GetTempPath(), $"lurp-tree-integrity-{Guid.NewGuid():N}");
        var treeRoot = Path.Combine(testDir, "GroundTruth");
        string? cacheDir = null;
        Process? serve = null;

        try
        {
            CopyTree(fixtureRoot, treeRoot);
            var solutionPath = Path.Combine(treeRoot, "CallShapes", "CallShapes.slnx");
            Assert.True(File.Exists(solutionPath), $"Copied fixture solution missing: {solutionPath}");

            // Restore happens before the measured window: obj/ assets are part of
            // the baseline and must not change either, but restore is not Lurp's work.
            var restore = RunProcess("dotnet", ["restore", solutionPath, "--nologo"], CliTimeout);
            Assert.True(restore.ExitCode == 0, $"dotnet restore failed ({restore.ExitCode}):\n{restore.Stderr}");

            var before = CaptureTree(treeRoot);
            cacheDir = LurpCache.ResolveSolutionCacheDir(solutionPath);

            // ── Every CLI mode, all resolving the default output directory from --solution= ──
            var index = RunCli(IndexTimeout, "--mode=index", $"--solution={solutionPath}", "--strategy=full");
            Assert.Contains("Index complete for snapshot", index.Stdout);

            var status = RunCli(CliTimeout, "--mode=status", $"--solution={solutionPath}", "--output=json");
            using var statusDoc = JsonDocument.Parse(status.Stdout);
            var snapshotId = statusDoc.RootElement.GetProperty("latest_snapshot_id").GetString()!;

            var find = RunCli(CliTimeout, "--mode=find-symbol", "--symbol=CallShapes.App.ShapeCallers.FromQueryLambda",
                $"--solution={solutionPath}", "--output=json", "--quiet");
            using var findDoc = JsonDocument.Parse(find.Stdout);
            var symbolId = findDoc.RootElement.GetProperty("symbol_id").GetString()!;

            AssertCliOk(RunCli(CliTimeout, "--mode=get-source", "--document=App/ShapeCallers.cs", $"--solution={solutionPath}", "--quiet"));
            AssertCliOk(RunCli(CliTimeout, "--mode=outline", "--document=App/ShapeCallers.cs", $"--solution={solutionPath}", "--output=json", "--quiet"));
            AssertCliOk(RunCli(CliTimeout, "--mode=get-symbol", $"--symbol={symbolId}", "--view=signature", $"--solution={solutionPath}"));
            AssertCliOk(RunCli(CliTimeout, "--mode=search", "--query=FromQueryLambda", $"--solution={solutionPath}", "--output=json", "--quiet"));
            AssertCliOk(RunCli(CliTimeout, "--mode=grep", "--query=FromQueryLambda", $"--solution={solutionPath}", "--output=json", "--quiet"));
            AssertCliOk(RunCli(CliTimeout, "--mode=navigate", "--file=App/ShapeCallers.cs", "--line=21", $"--solution={solutionPath}", "--quiet"));
            AssertCliOk(RunCli(CliTimeout, "--mode=impact", $"--symbol={symbolId}", "--direction=upstream", $"--solution={solutionPath}", "--output=json", "--quiet"));
            AssertCliOk(RunCli(CliTimeout, "--mode=context", $"--symbol={symbolId}", $"--solution={solutionPath}", "--output=summary", "--quiet"));
            AssertCliOk(RunCli(CliTimeout, "--mode=timings", "--json", $"--solution={solutionPath}"));
            AssertCliOk(RunCli(CliTimeout, "--mode=diagnostics", "--limit=5", $"--solution={solutionPath}", "--output=json", "--quiet"));
            AssertCliOk(RunCli(CliTimeout, "--mode=dead-candidates", "--limit=5", $"--solution={solutionPath}", "--output=json", "--quiet"));

            var annotate = RunCli(CliTimeout, "--mode=annotate", $"--symbol={symbolId}", "--annotation-kind=note",
                "--value=tree integrity", $"--solution={solutionPath}");
            AssertCliOk(annotate);
            using var annotateDoc = JsonDocument.Parse(annotate.Stdout);
            var annotationId = annotateDoc.RootElement.GetProperty("annotation_id").GetInt64();
            var mcpAnnotate = RunCli(CliTimeout, "--mode=annotate", $"--symbol={symbolId}", "--annotation-kind=note",
                "--value=tree integrity mcp", $"--solution={solutionPath}");
            AssertCliOk(mcpAnnotate);
            using var mcpAnnotateDoc = JsonDocument.Parse(mcpAnnotate.Stdout);
            var mcpAnnotationId = mcpAnnotateDoc.RootElement.GetProperty("annotation_id").GetInt64();

            AssertCliOk(RunCli(CliTimeout, "--mode=get-annotations", $"--symbol={symbolId}", $"--solution={solutionPath}", "--output=json", "--quiet"));
            AssertCliOk(RunCli(CliTimeout, "--mode=retract-annotation", $"--annotation-id={annotationId}", $"--solution={solutionPath}"));
            AssertCliOk(RunCli(CliTimeout, "--mode=diff", $"--from-snapshot={snapshotId}", $"--to-snapshot={snapshotId}", $"--solution={solutionPath}"));
            AssertCliOk(RunCli(CliTimeout, "--mode=pin-snapshot", $"--snapshot={snapshotId}", $"--solution={solutionPath}"));
            AssertCliOk(RunCli(CliTimeout, "--mode=pin-snapshot", "--clear", $"--solution={solutionPath}"));

            // The default location must have received the database and the redirected
            // MSBuild intermediates; otherwise "no writes in the tree" could pass
            // because nothing ran at all.
            Assert.True(File.Exists(Path.Combine(cacheDir, "index.db")), $"index.db not found in default cache {cacheDir}");
            Assert.True(Directory.EnumerateFiles(Path.Combine(cacheDir, "obj"), "*.cs", SearchOption.AllDirectories).Any(),
                $"no redirected generated sources under {Path.Combine(cacheDir, "obj")}");

            // ── MCP: --mode=serve, all 18 tools (read plus gated write tools) ──
            serve = StartServe(solutionPath, writeTools: true);
            var client = new McpClient(serve);
            await client.SendAsync("initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "target-tree-integrity", version = "1.0" }
            });
            await client.NotifyAsync("notifications/initialized", new { });

            var tools = await client.SendAsync("tools/list", new { });
            var toolNames = tools.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(18, toolNames.Count);
            foreach (var expected in new[]
                     {
                         "lurp_context", "lurp_get_source", "lurp_outline", "lurp_navigate", "lurp_find_symbol",
                         "lurp_search", "lurp_grep", "lurp_impact", "lurp_diff", "lurp_get_symbol",
                         "lurp_get_annotations", "lurp_diagnostics", "lurp_status", "lurp_timings",
                         "lurp_refresh", "lurp_dead_candidates", "lurp_index", "lurp_retract_annotation"
                     })
                Assert.Contains(expected, toolNames);

            await client.CallToolAsync("lurp_status", new { });
            await client.CallToolAsync("lurp_search", new { query = "FromQueryLambda" });
            await client.CallToolAsync("lurp_grep", new { query = "FromQueryLambda" });
            await client.CallToolAsync("lurp_get_source", new { document = "App/ShapeCallers.cs" });
            await client.CallToolAsync("lurp_outline", new { document = "App/ShapeCallers.cs" });
            await client.CallToolAsync("lurp_navigate", new { file = "App/ShapeCallers.cs", line = 21 });
            await client.CallToolAsync("lurp_find_symbol", new { symbol = "CallShapes.App.ShapeCallers.FromQueryLambda" });
            await client.CallToolAsync("lurp_get_symbol", new { symbol = symbolId, view = "summary" });
            await client.CallToolAsync("lurp_impact", new { symbol = symbolId, direction = "upstream" });
            await client.CallToolAsync("lurp_context", new { symbol = symbolId });
            await client.CallToolAsync("lurp_timings", new { });
            await client.CallToolAsync("lurp_diagnostics", new { limit = 5 });
            await client.CallToolAsync("lurp_dead_candidates", new { limit = 5 });
            await client.CallToolAsync("lurp_diff", new { from_snapshot = snapshotId, to_snapshot = snapshotId });
            await client.CallToolAsync("lurp_refresh", new { });
            await client.CallToolAsync("lurp_retract_annotation", new { annotation_id = mcpAnnotationId });

            var started = await client.CallToolAsync("lurp_index", new { strategy = "incremental" });
            using var startedDoc = JsonDocument.Parse(started.GetProperty("content")[0].GetProperty("text").GetString()!);
            var operationId = startedDoc.RootElement.GetProperty("operation_id").GetString()!;
            await WaitForIndexAsync(client, operationId);

            serve.StandardInput.Close();
            Assert.True(serve.WaitForExit(120_000), "serve did not exit after stdin close");
            Assert.Equal(0, serve.ExitCode);
            serve.Dispose();
            serve = null;

            // ── The measured window closes: the tree must not have moved a byte ──
            var after = CaptureTree(treeRoot);
            AssertTreeUnchanged(before, after);
        }
        finally
        {
            try { if (serve is { HasExited: false }) serve.Kill(entireProcessTree: true); } catch { }
            serve?.Dispose();
            try { if (cacheDir != null && Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true); } catch { }
            try { if (Directory.Exists(testDir)) Directory.Delete(testDir, true); } catch { }
        }
    }

    private static async Task WaitForIndexAsync(McpClient client, string operationId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            var poll = await client.CallToolAsync("lurp_index", new { operation_id = operationId });
            using var pollDoc = JsonDocument.Parse(poll.GetProperty("content")[0].GetProperty("text").GetString()!);
            var status = pollDoc.RootElement.GetProperty("status").GetString();
            if (status == "completed")
                return;
            if (status == "failed")
                throw new InvalidOperationException($"lurp_index failed: {pollDoc.RootElement}");
            if (status == "cancelled")
                throw new InvalidOperationException("lurp_index was cancelled unexpectedly");
        }

        throw new TimeoutException("lurp_index did not complete within 3 minutes");
    }

    private static void AssertCliOk((int ExitCode, string Stdout, string Stderr) result)
    {
        Assert.True(result.ExitCode == 0, $"lurp exited {result.ExitCode}:\n{result.Stderr}\n{result.Stdout}");
    }

    private static void AssertTreeUnchanged(SortedDictionary<string, string> before, SortedDictionary<string, string> after)
    {
        var added = after.Keys.Except(before.Keys, StringComparer.Ordinal).ToList();
        var removed = before.Keys.Except(after.Keys, StringComparer.Ordinal).ToList();
        var changed = before.Keys
            .Where(k => after.TryGetValue(k, out var v) && v != before[k])
            .ToList();

        if (added.Count == 0 && removed.Count == 0 && changed.Count == 0)
            return;

        static string Sample(List<string> items) =>
            items.Count == 0 ? "none" : string.Join("\n  ", items.Take(15)) + (items.Count > 15 ? $"\n  … +{items.Count - 15} more" : "");

        Assert.Fail(
            $"the analyzed tree changed inside the measured window.\n" +
            $"ADDED ({added.Count}):\n  {Sample(added)}\n" +
            $"REMOVED ({removed.Count}):\n  {Sample(removed)}\n" +
            $"CHANGED ({changed.Count}):\n  {Sample(changed)}");
    }

    /// <summary>Path plus size plus SHA-256 for every file, and every directory path.</summary>
    private static SortedDictionary<string, string> CaptureTree(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            map["dir: " + Path.GetRelativePath(root, dir).Replace('\\', '/')] = "";

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            using var stream = info.OpenRead();
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            map[Path.GetRelativePath(root, file).Replace('\\', '/')] = $"{info.Length}|{hash}";
        }

        return map;
    }

    private static void CopyTree(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var dir in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceRoot, dir);
            if (IsBuildDirectory(rel))
                continue;
            Directory.CreateDirectory(Path.Combine(destinationRoot, rel));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceRoot, file);
            if (IsBuildDirectory(Path.GetDirectoryName(rel) ?? ""))
                continue;
            var destination = Path.Combine(destinationRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        static bool IsBuildDirectory(string relativeDir) =>
            relativeDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string LocateFixtureRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "tests", "Fixtures", "GroundTruth", "CallShapes", "CallShapes.slnx");
            if (File.Exists(candidate))
                return Path.GetDirectoryName(Path.GetDirectoryName(candidate))!;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not locate tests/Fixtures/GroundTruth from " + AppContext.BaseDirectory);
    }

    internal static (string FileName, string? DllPath) ResolveToolCommand()
    {
        var packed = Environment.GetEnvironmentVariable("LURP_TOOL_PATH");
        if (!string.IsNullOrEmpty(packed))
            return (Path.GetFullPath(packed), null);

        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "Lurp.dll");
            if (File.Exists(candidate))
                return ("dotnet", candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }

        var current = Directory.GetCurrentDirectory();
        while (current != null)
        {
            var bin = Path.Combine(current, "src", "bin");
            if (Directory.Exists(bin))
            {
                var matches = Directory.GetFiles(bin, "Lurp.dll", SearchOption.AllDirectories);
                if (matches.Length > 0)
                {
                    var release = matches.FirstOrDefault(m => m.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}"));
                    return ("dotnet", release ?? matches.OrderByDescending(File.GetLastWriteTimeUtc).First());
                }
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new InvalidOperationException("Could not locate Lurp.dll (or set LURP_TOOL_PATH to a packed lurp executable).");
    }

    private static Process StartServe(string solutionPath, bool writeTools)
    {
        var (fileName, dllPath) = ResolveToolCommand();
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (dllPath != null)
            psi.ArgumentList.Add(dllPath);
        psi.ArgumentList.Add("--mode=serve");
        if (writeTools)
            psi.ArgumentList.Add("--enable-write-tools");
        psi.ArgumentList.Add($"--solution={solutionPath}");
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start lurp serve.");
    }

    internal static (int ExitCode, string Stdout, string Stderr) RunCli(TimeSpan timeout, params string[] args)
    {
        var (fileName, dllPath) = ResolveToolCommand();
        var fullArgs = dllPath == null ? args : [dllPath, .. args];
        return RunProcess(fileName, fullArgs, timeout);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, string[] args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{fileName} {string.Join(' ', args)} did not exit within {timeout}.");
        }

        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>Minimal MCP stdio client: one request id at a time, response matched by id.</summary>
    private sealed class McpClient
    {
        private readonly Process _process;
        private readonly StreamWriter _stdin;
        private readonly StreamReader _stdout;
        private int _nextId = 1;

        public McpClient(Process process)
        {
            _process = process;
            _stdin = process.StandardInput;
            _stdout = process.StandardOutput;
        }

        public async Task<JsonElement> SendAsync(string method, object? parameters)
        {
            var id = _nextId++;
            var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
            await _stdin.WriteLineAsync(request);
            await _stdin.FlushAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                while (true)
                {
                    var line = await _stdout.ReadLineAsync(cts.Token);
                    if (line == null)
                        throw new InvalidOperationException($"lurp serve stdout closed while waiting for '{method}' (id={id}).");
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("id", out var idProp)
                        && idProp.ValueKind == JsonValueKind.Number
                        && idProp.GetInt32() == id)
                        return doc.RootElement.Clone();

                    doc.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"timed out waiting for '{method}' (id={id}) from lurp serve.");
            }
        }

        public async Task<JsonElement> CallToolAsync(string name, object? arguments)
        {
            var response = await SendAsync("tools/call", new { name, arguments = arguments ?? new { } });
            if (response.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"MCP tool {name} returned an error: {error}");

            var result = response.GetProperty("result");
            if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
                throw new InvalidOperationException($"MCP tool {name} returned isError: {result}");

            return result.Clone();
        }

        public async Task NotifyAsync(string method, object? parameters)
        {
            var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
            await _stdin.WriteLineAsync(request);
            await _stdin.FlushAsync();
        }
    }
}
