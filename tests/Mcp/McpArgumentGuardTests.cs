using System.Text.Json;
using Lurp.Mcp;
using Lurp.Parity.Shared;

namespace Lurp.Tests.Mcp;

/// <summary>R105: an MCP argument name that the tool's input schema does not declare is rejected.</summary>
public sealed class McpArgumentGuardTests : IntegrationTestBase
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"query":{"type":"string"},"limit":{"type":"integer"}}}
        """).RootElement;

    [Fact]
    public void UnknownArgument_IsReported()
    {
        Assert.Equal("bogus", McpArgumentGuard.FindUnknownArgument(Schema, ["query", "bogus"]));
    }

    [Fact]
    public void DeclaredArguments_AreAccepted()
    {
        Assert.Null(McpArgumentGuard.FindUnknownArgument(Schema, ["query", "limit"]));
    }

    [Fact]
    public void NoArguments_AreAccepted()
    {
        Assert.Null(McpArgumentGuard.FindUnknownArgument(Schema, []));
    }

    [Fact]
    public async Task Serve_UnknownArgument_IsInvalidParams_AndDeclaredArgumentsStillWork()
    {
        CreateProject("GuardProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace GuardProj {
                    public class Foo { public void Bar() {} }
                }
                """
        });
        await RunFullIndexAsync(DbPath);

        var server = McpServeProcess.Start(
            LurpTool.Argv("--mode=serve", $"--output-dir={Path.GetDirectoryName(DbPath)!}", $"--solution={SolutionPath}"),
            _ => { });
        try
        {
            var timeout = TimeSpan.FromSeconds(15);
            var init = await server.Client.InitializeAsync("test", timeout);
            Assert.True(init.TryGetProperty("result", out _), "initialize must return result");

            var bad = await server.Client.SendAsync("tools/call", new { name = "lurp_search", arguments = new { query = "Foo", bogus = 1 } }, timeout);
            Assert.True(bad.TryGetProperty("error", out var error), $"expected a JSON-RPC error, got: {bad}");
            Assert.Equal(-32602, error.GetProperty("code").GetInt32());
            Assert.Contains("unknown parameter 'bogus'", error.GetProperty("message").GetString());

            var good = await server.Client.SendAsync("tools/call", new { name = "lurp_search", arguments = new { query = "Foo" } }, timeout);
            Assert.True(good.TryGetProperty("result", out var result), $"expected a result, got: {good}");
            Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), $"declared arguments must still work: {result}");
        }
        finally
        {
            server.Dispose();
        }
    }
}
