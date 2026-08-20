using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Lurp.Tests.Mcp;

public sealed class McpStdioTransportTests : IntegrationTestBase
{
    private string FindLurpDll()
    {
        // Locate compiled Lurp.dll: from test assembly dir walk up to repo, then src/bin/Debug/net* / Lurp.dll
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "Lurp.dll");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        // Search from repo root
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
        throw new InvalidOperationException("Could not locate Lurp.dll for stdio transport test.");
    }

    [Fact]
    public async Task StdioTransport_Stdout_IsJsonOnly_AndPinOnStderr()
    {
        // Minimal fixture: index one project so serve has a pinned snapshot
        CreateProject("StdioProj", new Dictionary<string, string>
        {
            ["Models.cs"] = "namespace StdioProj { public class Foo { public void Bar() {} } }"
        });
        await RunFullIndexAsync(DbPath);

        var lurpDll = FindLurpDll();

        var outputDir = Path.GetDirectoryName(DbPath)!;
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(lurpDll);
        psi.ArgumentList.Add("--mode=serve");
        psi.ArgumentList.Add($"--output-dir={outputDir}");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Lurp serve subprocess");
        try
        {
            // Send initialize request per MCP JSON-RPC
            var init = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "test", version = "1.0" }
                }
            });
            await process.StandardInput.WriteLineAsync(init);
            await process.StandardInput.FlushAsync();

            // Wait for first response on stdout (up to 10s)
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stdoutLines = new List<string>();
            string? stderrCollected = null;
            var stdoutTask = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                    if (line == null) break;
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        stdoutLines.Add(line);
                        // Stop after we have the first response with id 1 or result
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            if (doc.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == 1)
                                break;
                            if (doc.RootElement.TryGetProperty("result", out _))
                                break;
                        }
                        catch { }
                        if (stdoutLines.Count >= 5) break;
                    }
                }
            }, cts.Token);

            var stderrTask = process.StandardError.ReadToEndAsync();

            await stdoutTask;

            // Give stderr a moment to flush pin line
            await Task.Delay(300, cts.Token);
            try { process.StandardInput.Close(); } catch { }
            // Kill if still running, but capture stderr first
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            // Bounded wait for exit
            try { process.WaitForExit(2000); } catch { }

            // Collect stderr (if not already completed)
            try
            {
                var stderrWait = await Task.WhenAny(stderrTask, Task.Delay(2000));
                if (stderrWait == stderrTask) stderrCollected = await stderrTask;
                else stderrCollected = "";
            }
            catch { stderrCollected = ""; }

            // Assertions: every non-empty stdout line must be valid JSON
            Assert.True(stdoutLines.Count > 0, $"No stdout lines captured. stderr: {stderrCollected}");
            foreach (var line in stdoutLines)
            {
                var ex = Record.Exception(() => JsonDocument.Parse(line));
                Assert.True(ex == null, $"Stdout line is not valid JSON and leaks log: {line}\nFull stderr: {stderrCollected}");
            }

            // Pin line must appear on stderr, not stdout
            Assert.Contains("mcp: pinned snapshot", stderrCollected ?? "", StringComparison.Ordinal);
            var stdoutCombined = string.Join("\n", stdoutLines);
            Assert.DoesNotContain("mcp: pinned snapshot", stdoutCombined);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }
}
