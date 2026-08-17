using Lurp.Handlers;
using Lurp.Workspace;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class McpErrorMapperTests
{
    [Fact]
    public void Map_CliExitException_ReturnsInvalidParams_WithoutStackTrace()
    {
        var ex = new CliExitException("bad flag", 1);
        var mapped = Lurp.Mcp.McpErrorMapper.Map(ex);

        Assert.IsType<McpProtocolException>(mapped);
        Assert.Equal(McpErrorCode.InvalidParams, mapped.ErrorCode);
        Assert.Equal("bad flag", mapped.Message);
        Assert.DoesNotContain("at ", mapped.Message);
        Assert.DoesNotContain("StackTrace", mapped.Message);
    }

    [Fact]
    public void Map_CliExitException_ExitCodeTwo_StillInvalidParams()
    {
        var ex = new CliExitException("not fresh", 2);
        var mapped = Lurp.Mcp.McpErrorMapper.Map(ex);

        Assert.Equal(McpErrorCode.InvalidParams, mapped.ErrorCode);
        Assert.Equal("not fresh", mapped.Message);
    }

    [Fact]
    public void Map_WorkspaceUnreadableException_ReturnsInternalError()
    {
        var ex = new WorkspaceUnreadableException("no bindable compilation");
        var mapped = Lurp.Mcp.McpErrorMapper.Map(ex);

        Assert.Equal(McpErrorCode.InternalError, mapped.ErrorCode);
        Assert.Contains("no bindable compilation", mapped.Message);
        Assert.DoesNotContain("WorkspaceUnreadableException", mapped.StackTrace ?? string.Empty);
    }

    [Fact]
    public void Map_GenericException_ReturnsInternalError_WithoutStackTrace()
    {
        var ex = new InvalidOperationException("something broke");
        var mapped = Lurp.Mcp.McpErrorMapper.Map(ex);

        Assert.Equal(McpErrorCode.InternalError, mapped.ErrorCode);
        Assert.Equal("something broke", mapped.Message);
        Assert.DoesNotContain("InvalidOperationException", mapped.Message);
    }

    [Fact]
    public void Map_NeverIncludesStackTraceText()
    {
        var ex = new Exception("hello");
        var mapped = Lurp.Mcp.McpErrorMapper.Map(ex);
        var combined = mapped.Message + " " + (mapped.ToString() ?? string.Empty);
        // The mapped exception's Message must not contain stack frames.
        Assert.DoesNotContain(" at ", combined);
    }
}
