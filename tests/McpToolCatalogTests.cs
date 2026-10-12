using System.Reflection;
using Lurp.Mcp;
using Lurp.Tests.Docs;
using ModelContextProtocol.Server;

namespace Lurp.Tests;

/// <summary>
/// Ties the MCP tool catalog in <see cref="McpServeHandler"/> to the tool types
/// that carry <see cref="McpServerToolTypeAttribute"/>, and pins the read/write
/// split the server registers. The contract snapshot pins the exposed surface;
/// this file names the catalog that feeds it.
/// </summary>
public sealed class McpToolCatalogTests
{
    [Fact]
    public void ToolCatalog_CoversEveryToolType_ExactlyOnce()
    {
        var attributed = typeof(McpServeHandler).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .Select(type => type.FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var catalog = McpServeHandler.ReadToolTypes
            .Concat(McpServeHandler.WriteToolTypes)
            .Select(type => type.FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(attributed, catalog);
        Assert.Empty(McpServeHandler.ReadToolTypes.Intersect(McpServeHandler.WriteToolTypes));
    }

    [Fact]
    public void ReadTools_AreReadOnly_WriteTools_AreNot()
    {
        foreach (var type in McpServeHandler.ReadToolTypes)
            Assert.All(DocRenderers.ToolMethods(type), method => Assert.True(ToolAttribute(method).ReadOnly));

        foreach (var type in McpServeHandler.WriteToolTypes)
            Assert.All(DocRenderers.ToolMethods(type), method => Assert.False(ToolAttribute(method).ReadOnly));
    }

    [Fact]
    public void ToolCounts_AreSixteenAndTwo()
    {
        Assert.Equal(16, McpServeHandler.ReadToolTypes.Length);
        Assert.Equal(2, McpServeHandler.WriteToolTypes.Length);
    }

    private static McpServerToolAttribute ToolAttribute(MethodInfo method) =>
        method.GetCustomAttribute<McpServerToolAttribute>()!;
}
