using Lurp.Parity.Shared;

namespace Lurp.Tests.Mcp;

/// <summary>
///     Stdio boundary tests: write tools must not be registered unless
///     <c>--enable-write-tools</c> is passed, and malformed or oversized JSON-RPC
///     input must produce errors without killing the server (audit B5 test 5 and
///     A1 <c>McpBoundaryTests</c>).
/// </summary>
public sealed class McpStdioBoundaryTests : IntegrationTestBase
{
    private void SeedSnapshot() =>
        IndexedDirFixture.Create(1, stalePin: false, out _, out _, outputDir: TestDir);

    [Fact]
    public async Task Serve_WithoutWriteTools_ExposesReadToolsOnly_AndSurvivesMalformedInput()
    {
        SeedSnapshot();

        using var server = McpServeProcess.Start(LurpTool.Argv("--mode=serve", $"--output-dir={Path.GetDirectoryName(DbPath)!}"));
        await server.Client.InitializeAsync("test", TimeSpan.FromSeconds(30));

        // Malformed JSON and an oversized unknown request must not kill the server.
        await server.Client.SendLineAsync("{not json");
        await server.Client.SendLineAsync("{\"jsonrpc\":\"2.0\",\"id\":98,\"method\":\"" + new string('x', 256 * 1024) + "\"}");

        var list = await server.Client.SendAsync("tools/list", null, TimeSpan.FromSeconds(60));
        var names = list.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("lurp_search", names);
        Assert.Equal(16, names.Count);
        Assert.DoesNotContain("lurp_index", names);
        Assert.DoesNotContain("lurp_retract_annotation", names);
    }

    [Fact]
    public async Task Serve_WithWriteTools_ExposesIndexAndRetract()
    {
        SeedSnapshot();

        using var server = McpServeProcess.Start(LurpTool.Argv("--mode=serve", "--enable-write-tools", $"--output-dir={Path.GetDirectoryName(DbPath)!}"));
        await server.Client.InitializeAsync("test", TimeSpan.FromSeconds(30));

        var list = await server.Client.SendAsync("tools/list", null, TimeSpan.FromSeconds(60));
        var names = list.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(18, names.Count);
        Assert.Contains("lurp_index", names);
        Assert.Contains("lurp_retract_annotation", names);

        var retract = list.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "lurp_retract_annotation");
        var required = Assert.Single(retract.GetProperty("inputSchema").GetProperty("required").EnumerateArray());
        Assert.Equal("annotation_id", required.GetString());
        var omitted = await server.Client.SendAsync("tools/call",
            new { name = "lurp_retract_annotation", arguments = new { } }, TimeSpan.FromSeconds(30));
        Assert.Equal("annotation_id is required.", McpArgumentGuardTests.AssertToolError(omitted));
    }

    [Fact]
    public async Task Serve_ToolsList_DeclaresExactRequiredParameters_AndOmittedRequiredStillReturnsNamedError()
    {
        SeedSnapshot();
        using var server = McpServeProcess.Start(LurpTool.Argv("--mode=serve", "--enable-write-tools", $"--output-dir={TestDir}"));
        var timeout = TimeSpan.FromSeconds(30);
        await server.Client.InitializeAsync("test", timeout);

        var expected = new Dictionary<string, string[]>
        {
            ["lurp_get_source"] = ["document"],
            ["lurp_outline"] = ["document"],
            ["lurp_navigate"] = ["file", "line"],
            ["lurp_find_symbol"] = ["symbol"],
            ["lurp_get_symbol"] = ["symbol"],
            ["lurp_impact"] = ["symbol"],
            ["lurp_search"] = ["query"],
            ["lurp_grep"] = ["query"],
            ["lurp_diff"] = ["from_snapshot", "to_snapshot"],
            ["lurp_retract_annotation"] = ["annotation_id"]
        };

        var list = await server.Client.SendAsync("tools/list", null, timeout);
        var tools = list.GetProperty("result").GetProperty("tools");
        Assert.Equal(18, tools.GetArrayLength());
        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString()!;
            var schema = tool.GetProperty("inputSchema");
            var actual = schema.TryGetProperty("required", out var required)
                ? required.EnumerateArray().Select(item => item.GetString()).ToArray()
                : [];
            Assert.Equal(expected.Remove(name, out var parameters) ? parameters : [], actual);
        }
        Assert.Empty(expected);

        var grep = await server.Client.SendAsync("tools/call", new { name = "lurp_grep", arguments = new { } }, timeout);
        Assert.Equal("query is required.", McpArgumentGuardTests.AssertToolError(grep));
    }
}
