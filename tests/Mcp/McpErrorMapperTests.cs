using Lurp.Handlers;
using Lurp.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Lurp.Tests.Mcp;

public sealed class McpErrorMapperTests
{
    [Fact]
    public void Map_CliExitException_ReturnsInvalidParams_WithoutStackTrace()
    {
        var ex = new CliExitException("bad flag", 1);
        var mapped = McpErrorMapper.Map(ex);

        Assert.IsType<McpProtocolException>(mapped);
        Assert.Equal(McpErrorCode.InvalidParams, mapped.ErrorCode);
        Assert.Equal("bad flag", mapped.Message);
        Assert.DoesNotContain("at ", mapped.Message);
        Assert.DoesNotContain("StackTrace", mapped.Message);
    }

    [Theory]
    [InlineData("ERROR: bad flag", "bad flag")]
    [InlineData("ERROR: ERROR: bad flag", "ERROR: bad flag")]
    [InlineData("plain", "plain")]
    public void Map_CliExitException_StripsErrorPrefix_AndReturnsInvalidParams(string message, string expected)
    {
        var mapped = McpErrorMapper.Map(new CliExitException(message, 1));
        Assert.Equal(McpErrorCode.InvalidParams, mapped.ErrorCode);
        Assert.Equal(expected, mapped.Message);
    }

    [Fact]
    public void ToToolResult_HasOneTextBlock_WithTheMessage()
    {
        var result = McpErrorMapper.ToToolResult(McpErrorMapper.Map(new CliExitException("ERROR: bad flag", 1)));
        Assert.True(result.IsError);
        Assert.Equal("bad flag", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void Map_GenericException_ReturnsInternalError_WithoutStackTrace()
    {
        var ex = new InvalidOperationException("something broke");
        var mapped = McpErrorMapper.Map(ex);

        Assert.Equal(McpErrorCode.InternalError, mapped.ErrorCode);
        Assert.Equal("something broke", mapped.Message);
        Assert.DoesNotContain("InvalidOperationException", mapped.Message);
    }

    [Fact]
    public void Map_NeverIncludesStackTraceText()
    {
        var ex = new TestOnlyException("hello");
        var mapped = McpErrorMapper.Map(ex);
        var combined = mapped.Message + " " + (mapped.ToString() ?? string.Empty);
        // The mapped exception's Message must not contain stack frames.
        Assert.DoesNotContain(" at ", combined);
    }

    private sealed class TestOnlyException : Exception
    {
        public TestOnlyException(string message) : base(message) { }
    }
}
