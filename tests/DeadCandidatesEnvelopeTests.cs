using System.Text.Json;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;

namespace Lurp.Tests;

public sealed class DeadCandidatesEnvelopeTests : IntegrationTestBase
{
    [Fact]
    public async Task CliAndMcp_HaveTheSameEnvelopeKeys()
    {
        CreateProject("EnvelopeProj", new Dictionary<string, string>
        {
            ["Sample.cs"] = """
                            namespace EnvelopeProj;

                            internal sealed class Sample
                            {
                                internal void Unused() { }
                            }
                            """
        });
        await RunFullIndexAsync(DbPath);

        var cliJsonResult = CapturingOutputSink.Capture(sink => DeadCandidatesHandler.Run([
            "--mode=dead-candidates",
            "--output=json",
            $"--output-dir={TestDir}"
        ], sink));
        Assert.Null(cliJsonResult.Failure);

        var cliJsonlResult = CapturingOutputSink.Capture(sink => DeadCandidatesHandler.Run([
            "--mode=dead-candidates",
            "--output=jsonl",
            $"--output-dir={TestDir}"
        ], sink));
        Assert.Null(cliJsonlResult.Failure);

        await using var session = McpSessionContext.Create([$"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}"]);
        var tool = new DeadCandidatesTool(session);
        var mcpJson = tool.LurpDeadCandidates();

        using var cliDoc = JsonDocument.Parse(cliJsonResult.Stdout);
        var cliKeys = cliDoc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var firstJsonlLine = cliJsonlResult.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        using var jsonlDoc = JsonDocument.Parse(firstJsonlLine);
        Assert.Equal("meta", jsonlDoc.RootElement.GetProperty("type").GetString());
        var jsonlMetaKeys = jsonlDoc.RootElement.GetProperty("meta").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        using var mcpDoc = JsonDocument.Parse(mcpJson);
        var mcpKeys = mcpDoc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("pinned", mcpKeys);
        Assert.DoesNotContain("pinned", cliKeys);
        var mcpWithoutPinned = mcpKeys.Where(k => k != "pinned").ToHashSet(StringComparer.Ordinal);
        Assert.Equal(cliKeys, mcpWithoutPinned);

        Assert.Contains("candidates", cliKeys);
        Assert.DoesNotContain("candidates", jsonlMetaKeys);
        var cliWithoutCandidates = cliKeys.Where(k => k != "candidates").ToHashSet(StringComparer.Ordinal);
        Assert.Equal(cliWithoutCandidates, jsonlMetaKeys);
    }
}
