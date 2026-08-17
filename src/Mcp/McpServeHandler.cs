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
            .WithTools<ContextTool>()
            .WithTools<GetSourceTool>()
            .WithTools<NavigateTool>()
            .WithTools<FindSymbolTool>()
            .WithTools<SearchTool>()
            .WithTools<ImpactTool>()
            .WithTools<DiffTool>()
            .WithTools<GetSymbolTool>()
            .WithTools<AnnotationsTool>()
            .WithTools<StatusTool>()
            .WithTools<RefreshTool>();

        var host = builder.Build();
        await host.RunAsync();
    }
}
