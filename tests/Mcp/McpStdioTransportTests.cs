using System.Text.Json;
using Lurp.Parity.Shared;

namespace Lurp.Tests.Mcp;

public sealed class McpStdioTransportTests : IntegrationTestBase
{
    [Fact]
    public async Task StdioTransport_Stdout_IsJsonOnly_AndPinOnStderr()
    {
        // Minimal fixture: index one project so serve has a pinned snapshot
        CreateProject("StdioProj", new Dictionary<string, string>
        {
            ["Models.cs"] = "namespace StdioProj { public class Foo { public void Bar() {} } }"
        });
        await RunFullIndexAsync(DbPath);

        var outputDir = Path.GetDirectoryName(DbPath)!;
        var stdoutLines = new List<string>();
        var gate = new object();
        using var server = McpServeProcess.Start(
            LurpTool.Argv("--mode=serve", $"--output-dir={outputDir}"),
            line =>
            {
                lock (gate)
                    stdoutLines.Add(line);
            });

        await server.Client.InitializeAsync("test", TimeSpan.FromSeconds(10));

        // Give stderr a moment to flush pin line
        await Task.Delay(300);
        server.Close(5000);

        var stderrCollected = server.Stderr;

        // Assertions: every non-empty stdout line must be valid JSON
        Assert.True(stdoutLines.Count > 0, $"No stdout lines captured. stderr: {stderrCollected}");
        foreach (var line in stdoutLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var ex = Record.Exception(() => JsonDocument.Parse(line));
            Assert.True(ex == null, $"Stdout line is not valid JSON and leaks log: {line}\nFull stderr: {stderrCollected}");
        }

        // Pin line must appear on stderr, not stdout
        Assert.Contains("mcp: pinned snapshot", stderrCollected ?? "", StringComparison.Ordinal);
        var stdoutCombined = string.Join("\n", stdoutLines);
        Assert.DoesNotContain("mcp: pinned snapshot", stdoutCombined);
    }
}
