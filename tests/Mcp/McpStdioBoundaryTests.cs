using System.Diagnostics;
using System.Text.Json;

namespace Lurp.Tests.Mcp;

/// <summary>
///     Stdio boundary tests: write tools must not be registered unless
///     <c>--enable-write-tools</c> is passed, and malformed or oversized JSON-RPC
///     input must produce errors without killing the server (audit B5 test 5 and
///     A1 <c>McpBoundaryTests</c>).
/// </summary>
public sealed class McpStdioBoundaryTests : IntegrationTestBase
{
    private static string FindLurpDll()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "Lurp.dll");
            if (File.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not locate Lurp.dll for stdio boundary test.");
    }

    private async Task IndexFixtureAsync()
    {
        CreateProject("BoundaryProj", new Dictionary<string, string>
        {
            ["Models.cs"] = "namespace BoundaryProj { public class Foo { public void Bar() {} } }"
        });
        await RunFullIndexAsync(DbPath);
    }

    [Fact]
    public async Task Serve_WithoutWriteTools_ExposesReadToolsOnly_AndSurvivesMalformedInput()
    {
        await IndexFixtureAsync();

        using var server = ServeProcess.Start(FindLurpDll(), Path.GetDirectoryName(DbPath)!, enableWriteTools: false);
        await server.InitializeAsync();

        // Malformed JSON and an oversized unknown request must not kill the server.
        await server.WriteLineAsync("{not json");
        await server.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":98,\"method\":\"" + new string('x', 256 * 1024) + "\"}");

        using var list = await server.CallAsync("tools/list", null, id: 2);
        var names = list.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("lurp_search", names);
        Assert.Equal(16, names.Count);
        Assert.DoesNotContain("lurp_index", names);
        Assert.DoesNotContain("lurp_retract_annotation", names);
    }

    [Fact]
    public async Task Serve_WithWriteTools_ExposesIndexAndRetract()
    {
        await IndexFixtureAsync();

        using var server = ServeProcess.Start(FindLurpDll(), Path.GetDirectoryName(DbPath)!, enableWriteTools: true);
        await server.InitializeAsync();

        using var list = await server.CallAsync("tools/list", null, id: 2);
        var names = list.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(18, names.Count);
        Assert.Contains("lurp_index", names);
        Assert.Contains("lurp_retract_annotation", names);
    }

    private sealed class ServeProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stderr;

        private ServeProcess(Process process)
        {
            _process = process;
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public static ServeProcess Start(string lurpDll, string outputDir, bool enableWriteTools)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(lurpDll);
            psi.ArgumentList.Add("--mode=serve");
            psi.ArgumentList.Add($"--output-dir={outputDir}");
            if (enableWriteTools)
                psi.ArgumentList.Add("--enable-write-tools");

            var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Lurp serve subprocess");
            return new ServeProcess(process);
        }

        public async Task InitializeAsync()
        {
            await WriteLineAsync(JsonSerializer.Serialize(new
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
            }));
            using var _ = await ReadUntilIdAsync(1, TimeSpan.FromSeconds(30));
            await WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));
        }

        public async Task<JsonDocument> CallAsync(string method, object? parameters, int id)
        {
            await WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            return await ReadUntilIdAsync(id, TimeSpan.FromSeconds(60));
        }

        public async Task WriteLineAsync(string line)
        {
            await _process.StandardInput.WriteLineAsync(line);
            await _process.StandardInput.FlushAsync();
        }

        private async Task<JsonDocument> ReadUntilIdAsync(int id, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var line = await _process.StandardOutput.ReadLineAsync(cts.Token);
                    if (line == null)
                        throw new InvalidOperationException($"server closed stdout; stderr:\n{await SafeStderrAsync()}");

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); }
                    catch { continue; }

                    if (doc.RootElement.TryGetProperty("id", out var idEl) &&
                        idEl.ValueKind == JsonValueKind.Number &&
                        idEl.GetInt32() == id)
                        return doc;

                    doc.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"no response with id {id}; stderr:\n{await SafeStderrAsync()}");
            }
        }

        private async Task<string> SafeStderrAsync()
        {
            try
            {
                return await Task.WhenAny(_stderr, Task.Delay(1000)) == _stderr ? await _stderr : "";
            }
            catch
            {
                return "";
            }
        }

        public void Dispose()
        {
            try { _process.StandardInput.Close(); } catch { }
            try
            {
                if (!_process.WaitForExit(5000))
                    _process.Kill(entireProcessTree: true);
            }
            catch { }

            _process.Dispose();
        }
    }
}
