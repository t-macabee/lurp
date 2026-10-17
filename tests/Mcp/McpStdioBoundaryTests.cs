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
        await IndexFixtureAsync();

        using var server = McpServeProcess.Start(LurpTool.Argv("--mode=serve", "--enable-write-tools", $"--output-dir={Path.GetDirectoryName(DbPath)!}"));
        await server.Client.InitializeAsync("test", TimeSpan.FromSeconds(30));

        var list = await server.Client.SendAsync("tools/list", null, TimeSpan.FromSeconds(60));
        var names = list.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(18, names.Count);
        Assert.Contains("lurp_index", names);
        Assert.Contains("lurp_retract_annotation", names);

        // annotation_id is optional in the tool schema: the SDK binds a missing value
        // as null and the tool answers with its own named -32602 error.
        var retract = list.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "lurp_retract_annotation");
        var schema = retract.GetProperty("inputSchema");
        Assert.False(
            schema.TryGetProperty("required", out var required) &&
            required.EnumerateArray().Any(name => name.GetString() == "annotation_id"));
    }
}
