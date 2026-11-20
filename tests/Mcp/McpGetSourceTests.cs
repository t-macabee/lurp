using Lurp.Mcp;
using Lurp.Mcp.Tools;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpGetSourceTests : IntegrationTestBase
{
    private const string Document = "src/WinProj/Five.cs";
    private const string MissingDocument = "src/WinProj/Missing.cs";

    [Fact]
    public async Task GetSource_StartLineBeyondEndAndMissingDocument_UseExpectedMessages()
    {
        CreateProject("WinProj", new Dictionary<string, string>
        {
            ["Five.cs"] = "// l1\n// l2\n// l3\n// l4\n// l5\n"
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={Path.GetDirectoryName(DbPath)!}" };
        await using var session = McpSessionContext.Create(args);
        var getSource = new GetSourceTool(session);

        var rangeEx = Assert.Throws<McpProtocolException>(() =>
            getSource.LurpGetSource(document: Document, start_line: 99));
        Assert.Equal(McpErrorCode.InvalidParams, rangeEx.ErrorCode);
        Assert.Equal($"start_line 99 is beyond the last line (6) of '{Document}'.", rangeEx.Message);

        var missingEx = Assert.Throws<McpProtocolException>(() =>
            getSource.LurpGetSource(document: MissingDocument));
        Assert.Equal(McpErrorCode.InvalidParams, missingEx.ErrorCode);
        Assert.Equal($"Document '{MissingDocument}' not found in snapshot '{snapshotId}'.", missingEx.Message);
    }
}
