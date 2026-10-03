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

    private static RunResult RunCaptured(Action action)
    {
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        CliExitException? failure = null;
        try
        {
            Console.SetOut(stdout);
            action();
        }
        catch (CliExitException ex)
        {
            failure = ex;
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return new RunResult(stdout.ToString(), failure);
    }

    private sealed record RunResult(string Stdout, CliExitException? Failure);

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

        var cliResult = RunCaptured(() => DeadCandidatesHandler.Run([
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
}
