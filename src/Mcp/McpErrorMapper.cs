using Lurp.Handlers;
using Lurp.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Lurp.Mcp;

internal static class McpErrorMapper
{
    public static McpProtocolException Map(Exception ex)
    {
        if (ex is CliExitException cli)
        {
            const string prefix = "ERROR: ";
            var message = cli.Message.StartsWith(prefix, StringComparison.Ordinal)
                ? cli.Message[prefix.Length..]
                : cli.Message;
            return new McpProtocolException(message, McpErrorCode.InvalidParams);
        }

        return new McpProtocolException(ex.Message, McpErrorCode.InternalError);
    }

    internal static CallToolResult ToToolResult(McpProtocolException ex) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = ex.Message }]
    };

    internal static McpReasonPayload WorkspaceUnreadable(WorkspaceUnreadableException exception) => new(
        "workspace_unreadable",
        exception.Message,
        exception.Remediation);
}

internal sealed record McpReasonPayload(string reason_code, string message, string remediation);
