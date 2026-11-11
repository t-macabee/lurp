namespace Lurp.Tests.Docs;

internal static class DocCatalog
{
    internal static IReadOnlyList<DocSection> Sections { get; } =
    [
        new("README.md", "versions", DocRenderers.RenderVersionFacts),
        new("docs/CLI_REFERENCE.md", "versions", DocRenderers.RenderVersionFacts),
        new("docs/ARCHITECTURE.md", "versions", DocRenderers.RenderVersionFacts),
        new("docs/ARCHITECTURE.md", "declared-boundaries", DocRenderers.RenderDeclaredBoundaries),
        new("docs/ARCHITECTURE.md", "mcp-tools:read", DocRenderers.RenderMcpReadTools),
        new("docs/ARCHITECTURE.md", "mcp-tools:write", DocRenderers.RenderMcpWriteTools),
        new("docs/CLI_REFERENCE.md", "flags:read-options", DocRenderers.RenderReadOptions),
        .. Program.ModeRegistry.Select(entry => new DocSection("docs/CLI_REFERENCE.md", $"flags:{entry.Name}", () => DocRenderers.RenderModeFlags(entry))),
        new("docs/CLI_REFERENCE.md", "mcp-tools:read", DocRenderers.RenderMcpReadTools),
        new("docs/CLI_REFERENCE.md", "mcp-tools:write", DocRenderers.RenderMcpWriteTools),
        new("docs/CLI_REFERENCE.md", "mcp-tools:total", DocRenderers.RenderMcpToolTotal),
        new("docs/CLI_REFERENCE.md", "mcp-tools:table", DocRenderers.RenderMcpToolTable),
        new("docs/CLI_REFERENCE.md", "mcp-limits", DocRenderers.RenderMcpLimits),
        new("SECURITY.md", "mcp-tools:total", DocRenderers.RenderMcpToolTotal),
        new("SECURITY.md", "mcp-tools:access", DocRenderers.RenderMcpToolAccessTable),
        new("SECURITY.md", "mcp-limits", DocRenderers.RenderMcpLimits),
        new("README.md", "mcp-tools:read", DocRenderers.RenderMcpReadTools),
        new("README.md", "mcp-tools:write", DocRenderers.RenderMcpWriteTools),
    ];

    internal static bool UpdateMode =>
        Environment.GetEnvironmentVariable("LURP_UPDATE_DOCS") == "1";
}
