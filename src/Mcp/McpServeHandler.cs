using Lurp.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Lurp.Mcp;

internal static class McpServeHandler
{
    /// <summary>How long shutdown waits for an in-flight index run to stop.</summary>
    internal static readonly TimeSpan ShutdownIndexWait = TimeSpan.FromSeconds(30);

    public static async Task Run(string[] args)
    {
        var enableWriteTools = args.Contains("--enable-write-tools");

        await using var session = McpSessionContext.Create(args);

        // Empty builder on purpose: the host must not read the target repo's
        // appsettings*.json, DOTNET_*/LURP_* environment configuration, or the
        // command-line arguments as host configuration. The session parses the
        // launch arguments it needs itself.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
        });
        builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(session);
        builder.Services.AddSingleton<McpIndexSessionState>();
        var mcp = builder.Services.AddMcpServer()
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
            .WithTools<DeadCandidatesTool>();

        // Write tools are opt-in: an agent that follows prompt-injected text must
        // not be able to start an index run or hard-delete annotations by default.
        if (enableWriteTools)
        {
            mcp.WithTools<IndexTool>();
            mcp.WithTools<RetractAnnotationTool>();
        }

        var host = builder.Build();

        // Resolve before RunAsync: RunAsync disposes the host, so the service
        // provider is unusable once it returns.
        var indexState = host.Services.GetRequiredService<McpIndexSessionState>();

        await host.RunAsync();

        // The stdio transport returns when the client disconnects. Do not let the
        // process exit while a background index run still writes: cancel it and
        // wait a bounded time. A run that outlives the wait leaves an incomplete
        // snapshot that the next successful index run prunes.
        var backgroundTask = indexState.Current?.BackgroundTask;
        if (backgroundTask != null)
        {
            indexState.CancelCurrent();
            await Task.WhenAny(backgroundTask, Task.Delay(ShutdownIndexWait));
        }
    }
}
