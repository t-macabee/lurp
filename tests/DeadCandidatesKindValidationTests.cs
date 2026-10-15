using System.Text.Json;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using ModelContextProtocol;

namespace Lurp.Tests;

public sealed class DeadCandidatesKindValidationTests : IntegrationTestBase
{
    private async Task<string> IndexFixtureAsync()
    {
        CreateProject("DeadProj", new Dictionary<string, string>
        {
            ["Models.cs"] = "namespace DeadProj { public class Foo { public void Bar() {} } }"
        });
        return await RunFullIndexAsync(DbPath);
    }

    private McpSessionContext CreateSession() => McpSessionContext.Create([$"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}"]);

    [Fact]
    public void Cli_InvalidKind_ThrowsCliExitException()
    {
        var ex = Assert.Throws<CliExitException>(() => DeadCandidatesHandler.Run([
            "--mode=dead-candidates",
            "--kind=Bogus",
            $"--output-dir={TestDir}"
        ]));
        Assert.Contains("--kind must be one of", ex.Message);
    }

    [Fact]
    public async Task Mcp_InvalidKind_ThrowsInvalidParams()
    {
        await IndexFixtureAsync();
        await using var session = CreateSession();
        var tool = new DeadCandidatesTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpDeadCandidates(kind: "Bogus"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    [Fact]
    public async Task ValidKind_DifferentCasing_NoKindError()
    {
        await IndexFixtureAsync();

        var cliResult = TestConsole.Capture(() => DeadCandidatesHandler.Run([
            "--mode=dead-candidates",
            "--kind=method",
            $"--output-dir={TestDir}"
        ]));
        Assert.Null(cliResult.Failure);
        Assert.False(string.IsNullOrEmpty(cliResult.Stdout));

        await using var session = CreateSession();
        var tool = new DeadCandidatesTool(session);
        var json = tool.LurpDeadCandidates(kind: "method");
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void Cli_LimitAboveCap_NamesTheCapWithoutTheOldReason()
    {
        var ex = Assert.Throws<CliExitException>(() => DeadCandidatesHandler.Run([
            "--mode=dead-candidates",
            "--limit=201",
            $"--output-dir={TestDir}"
        ]));
        Assert.Contains("--limit must be <= 200.", ex.Message);
        Assert.DoesNotContain("SQLITE_MAX_VARIABLE_NUMBER", ex.Message);
    }

    [Fact]
    public async Task Mcp_LimitAboveCap_IsInvalidParams_WithTheSameMessage()
    {
        await IndexFixtureAsync();
        await using var session = CreateSession();
        var tool = new DeadCandidatesTool(session);

        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpDeadCandidates(limit: 201));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Equal("limit must be a positive integer <=200.", ex.Message);
    }
}
