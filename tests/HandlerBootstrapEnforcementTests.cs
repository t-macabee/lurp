using System.Text.RegularExpressions;
using Lurp.Handlers;
using Lurp.Mcp;

namespace Lurp.Tests;

/// <summary>
///     Drift guards for <see cref="HandlerBootstrap" /> helpers whose contract is "every
///     call site must route through this helper". The helpers are the chokepoint; these tests
///     are the tripwire that makes a newly-added CLI mode or MCP tool fail the build when it
///     reintroduces a private copy of the shared rule instead of calling the helper.
/// </summary>
public sealed class HandlerBootstrapEnforcementTests
{
    [Fact]
    public void EveryCliDocumentPathConsumer_CallsNormalizeDocumentPath()
    {
        var handlersDir = Path.Combine(TestRepoRoot.Find(), "src", "Handlers");

        var violations = new List<string>();
        var checkedFiles = new List<string>();
        foreach (var file in Directory.GetFiles(handlersDir, "*.cs"))
        {
            var source = File.ReadAllText(file);
            var readsDocumentArg = source.Contains("GetArgValue(args, \"--document=\")", StringComparison.Ordinal)
                                   || source.Contains("GetArgValue(args, \"--file=\")", StringComparison.Ordinal);
            if (!readsDocumentArg)
                continue;

            checkedFiles.Add(Path.GetFileName(file));
            if (!source.Contains("HandlerBootstrap.NormalizeDocumentPath", StringComparison.Ordinal))
                violations.Add(Path.GetFileName(file));
        }

        Assert.True(checkedFiles.Count > 0, "No handler files read --document= or --file=; the scan is not exercising anything.");
        Assert.True(
            violations.Count == 0,
            "Every CLI handler that reads --document= or --file= must normalize the path via " +
            "HandlerBootstrap.NormalizeDocumentPath before it reaches the store:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void EveryMcpDocumentPathParameter_CallsNormalizeDocumentPath()
    {
        var repoRoot = TestRepoRoot.Find();
        var toolsDir = Path.Combine(repoRoot, "src", "Mcp", "Tools");
        var pathParameterNames = new HashSet<string>(StringComparer.Ordinal) { "document", "file", "documents" };

        var toolTypes = typeof(McpServeHandler).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), false).Length > 0)
            .ToList();
        Assert.True(toolTypes.Count > 0, "No MCP tool types found; the reflection scan is not exercising anything.");

        var violations = new List<string>();
        var checkedTools = new List<string>();
        foreach (var type in toolTypes)
        {
            var hasPathParameter = type.GetMethods()
                .Where(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false).Length > 0)
                .SelectMany(m => m.GetParameters())
                .Any(p => pathParameterNames.Contains(p.Name ?? string.Empty));
            if (!hasPathParameter)
                continue;

            checkedTools.Add(type.Name);
            var toolPath = Path.Combine(toolsDir, type.Name + ".cs");
            Assert.True(File.Exists(toolPath), $"Tool source not found: {toolPath}");

            var source = File.ReadAllText(toolPath);
            if (!source.Contains("NormalizeDocumentPath", StringComparison.Ordinal))
                violations.Add(type.Name);
        }

        Assert.True(checkedTools.Count > 0, "No MCP tools with document/file/documents parameters found; the scan is not exercising anything.");
        Assert.True(
            violations.Count == 0,
            "Every MCP tool with a document/file/documents parameter must normalize the path via " +
            "HandlerBootstrap.NormalizeDocumentPath before it reaches the store:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void OnlySharedOutputModeParser_ReadsOutputFlag()
    {
        var handlersDir = Path.Combine(TestRepoRoot.Find(), "src", "Handlers");
        var documentedExceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HandlerBootstrap.cs", // the shared parser itself
            "PinSnapshotHandler.cs" // documented --output=json alias; not the summary|json|jsonl vocabulary
        };

        var violations = new List<string>();
        var checkedFiles = new List<string>();
        foreach (var file in Directory.GetFiles(handlersDir, "*.cs"))
        {
            var source = File.ReadAllText(file);
            if (!source.Contains("GetArgValue(args, \"--output=\")", StringComparison.Ordinal))
                continue;

            checkedFiles.Add(Path.GetFileName(file));
            if (!documentedExceptions.Contains(Path.GetFileName(file)))
                violations.Add(Path.GetFileName(file));
        }

        Assert.True(checkedFiles.Count > 0, "No handler files read --output=; the scan is not exercising anything.");
        Assert.True(
            violations.Count == 0,
            "Handlers must parse --output= through HandlerBootstrap.ParseOutputMode (or be a " +
            "documented exception like pin-snapshot's --output=json alias), never through a " +
            "private reimplementation of the summary|json|jsonl vocabulary:\n" +
            string.Join("\n", violations));
    }
}
