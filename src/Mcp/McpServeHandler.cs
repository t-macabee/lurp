using Lurp.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Lurp.Mcp;

internal static class McpServeHandler
{
    public static async Task Run(string[] args)
    {
        await using var session = McpSessionContext.Create(args);

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(session);
        builder.Services.AddSingleton<McpIndexSessionState>();
        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<ContextTool>()
            .WithTools<GetSourceTool>()
            .WithTools<OutlineTool>()
            .WithTools<NavigateTool>()
            .WithTools<FindSymbolTool>()
            .WithTools<SearchTool>()
            .WithTools<ImpactTool>()
            .WithTools<DiffTool>()
            .WithTools<GetSymbolTool>()
            .WithTools<AnnotationsTool>()
            .WithTools<DiagnosticsTool>()
            .WithTools<GrepTool>()
            .WithTools<StatusTool>()
            .WithTools<TimingsTool>()
            .WithTools<RefreshTool>()
            .WithTools<IndexTool>()
            .WithTools<DeadCandidatesTool>();

        var host = builder.Build();
        await host.RunAsync();
    }
}
