using Lurp.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Lurp.Mcp;

internal static class McpServeHandler
{
    public static async Task Run(string[] args)
    {
        await using var session = McpSessionContext.Create(args);

        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSingleton(session);
        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<ContextTool>();

        var host = builder.Build();
        await host.RunAsync();
    }
}
