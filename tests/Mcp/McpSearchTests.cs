using Lurp.Mcp;
using Lurp.Mcp.Tools;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpSearchTests : IntegrationTestBase
{
    [Fact]
    public async Task Search_UnknownKind_IsInvalidParams()
    {
        CreateProject("KindProj", new Dictionary<string, string>
        {
            ["Calc.cs"] = "namespace KindProj; public static class Calc { public static int Add(int a) => a; }"
        });
        await RunFullIndexAsync(DbPath);

        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var search = new SearchTool(session);

        var ex = Assert.Throws<McpProtocolException>(() =>
            search.LurpSearch(query: "Add", type: "symbol", kind: "Bogus"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Equal("kind must be one of: Type, Method, Property, Field, Event. Got 'Bogus'.", ex.Message);
    }
}
