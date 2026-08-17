using Lurp.Handlers;
using Lurp.Workspace;
using ModelContextProtocol;

namespace Lurp.Mcp;

internal static class McpErrorMapper
{
    public static McpProtocolException Map(Exception ex)
    {
        if (ex is CliExitException cli)
            return new McpProtocolException(cli.Message, McpErrorCode.InvalidParams);

        if (ex is WorkspaceUnreadableException wue)
            return new McpProtocolException(wue.Message, McpErrorCode.InternalError);

        return new McpProtocolException(ex.Message, McpErrorCode.InternalError);
    }
}
