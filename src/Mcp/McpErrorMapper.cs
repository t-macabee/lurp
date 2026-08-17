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
        {
            // Structured data, not a stack trace (4.9): surface reason code and remediation
            // without leaking an internal stack trace to the MCP client.
            var data = new
            {
                reason_code = "workspace_unreadable",
                message = wue.Message,
                remediation = "Run 'dotnet restore' on the solution and confirm the required SDK and target frameworks are installed, then re-index."
            };
            // ModelContextProtocol's McpProtocolException can carry structured data via the Data property
            // on the underlying JsonRpc error. We encode it as JSON in the message's data slot by
            // wrapping in a protocol exception that the server will serialize. If the SDK does not
            // surface Data directly, the message itself remains structured and stack-trace-free.
            var msg = $"{wue.Message} | data: {System.Text.Json.JsonSerializer.Serialize(data)}";
            return new McpProtocolException(msg, McpErrorCode.InternalError);
        }

        if (ex.Message.Contains("project.assets.json", StringComparison.Ordinal) || ex.Message.Contains("not been restored", StringComparison.Ordinal))
        {
            var data = new
            {
                reason_code = "restore_required",
                message = ex.Message,
                remediation = "Run 'dotnet restore' on the solution before indexing — without it MSBuildWorkspace cannot resolve project-to-project references, which produces phantom diagnostics and silently drops edges."
            };
            var msg = $"{ex.Message} | data: {System.Text.Json.JsonSerializer.Serialize(data)}";
            return new McpProtocolException(msg, McpErrorCode.InternalError);
        }

        return new McpProtocolException(ex.Message, McpErrorCode.InternalError);
    }

    /// <summary>
    ///     Structured error payload for the MSBuild restore gate.
    ///     Callers that have a concrete <see cref="List{String}"/> of unrestored project names
    ///     should prefer this overload so the client receives a machine-readable list.
    /// </summary>
    public static McpProtocolException RestoreRequired(IReadOnlyList<string> unrestoredProjects, string? contextMessage = null)
    {
        var data = new
        {
            reason_code = "restore_required",
            message = contextMessage ?? WorkspaceLoadGate.DescribeUnrestored(unrestoredProjects),
            unrestored_projects = unrestoredProjects,
            remediation = "Run 'dotnet restore' on the solution before indexing."
        };
        var msg = $"{data.message} | data: {System.Text.Json.JsonSerializer.Serialize(data)}";
        return new McpProtocolException(msg, McpErrorCode.InternalError);
    }

    public static McpProtocolException WorkspaceUnreadable(string message)
    {
        var data = new
        {
            reason_code = "workspace_unreadable",
            message,
            remediation = "Run 'dotnet restore' on the solution and confirm the required SDK and target frameworks are installed, then re-index."
        };
        var msg = $"{message} | data: {System.Text.Json.JsonSerializer.Serialize(data)}";
        return new McpProtocolException(msg, McpErrorCode.InternalError);
    }
}
