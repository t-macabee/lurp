using System.Text.Json;
using Lurp.Handlers;
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
    public async Task Serve_ToolErrors_ReturnIsErrorResult_AndUnknownToolStaysProtocolError()
    {
        IndexedDirFixture.Create(1, stalePin: false, out _, out var snapshotId, outputDir: TestDir);

        using var server = McpServeProcess.Start(LurpTool.Argv("--mode=serve", $"--output-dir={TestDir}"));
        var timeout = TimeSpan.FromSeconds(30);
        await server.Client.InitializeAsync("test", timeout);

        Task<JsonElement> Call(string name, object arguments) =>
            server.Client.SendAsync("tools/call", new { name, arguments }, timeout);

        var bad = await Call("lurp_search", new { query = "Foo", bogus = 1 });
        Assert.Contains("unknown parameter 'bogus' for lurp_search.", AssertToolError(bad));

        var badLimit = await Call("lurp_search", new { query = "Foo", limit = 0 });
        Assert.Equal("limit must be a positive integer.", AssertToolError(badLimit));

        var missing = await Call("lurp_find_symbol", new { symbol = "Nope.Nope" });
        Assert.Equal($"Symbol 'Nope.Nope' not found in snapshot '{snapshotId}'. {HandlerBootstrap.AcceptedSymbolFormsText}",
            AssertToolError(missing));

        var unknown = await Call("lurp_does_not_exist", new { });
        Assert.True(unknown.TryGetProperty("error", out _));
        Assert.False(unknown.TryGetProperty("result", out _));

        var good = await server.Client.CallToolAsync("lurp_search", new { query = "Foo" });
        Assert.NotEmpty(good.GetProperty("content").EnumerateArray());
    }

    internal static string AssertToolError(JsonElement response)
    {
        Assert.False(response.TryGetProperty("error", out _), $"Expected a tool result: {response}");
        var result = response.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        return content.GetProperty("text").GetString()!;
    }
}
