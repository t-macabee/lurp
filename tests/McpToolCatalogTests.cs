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
            Assert.All(DocRenderers.ToolMethods(type), method =>
            {
                var attr = ToolAttribute(method);
                Assert.True(attr.ReadOnly);
                // The SDK defaults Destructive to true; the hint only applies to write tools.
                var declaration = method.CustomAttributes.Single(attribute => attribute.AttributeType == typeof(McpServerToolAttribute));
                Assert.DoesNotContain(declaration.NamedArguments,
                    argument => argument.MemberName == nameof(attr.Destructive) && argument.TypedValue.Value is true);
                Assert.False(attr.OpenWorld);
            });

        foreach (var type in McpServeHandler.WriteToolTypes)
            Assert.All(DocRenderers.ToolMethods(type), method =>
            {
                var attr = ToolAttribute(method);
                Assert.False(attr.ReadOnly);
                // The SDK defaults Destructive to true, so a write tool must state it.
                var declaration = method.CustomAttributes.Single(attribute => attribute.AttributeType == typeof(McpServerToolAttribute));
                Assert.Contains(declaration.NamedArguments,
                    argument => argument.MemberName == nameof(attr.Destructive) && argument.TypedValue.Value is true);
                Assert.False(attr.OpenWorld);
            });
    }

    private static McpServerToolAttribute ToolAttribute(MethodInfo method) =>
        method.GetCustomAttribute<McpServerToolAttribute>()!;
}
