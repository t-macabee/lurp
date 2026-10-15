using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using Lurp.Tests.Docs;

namespace Lurp.Tests;

/// <summary>
///     Pins every number an MCP tool description states as a default to the
///     owning constant. C# attribute arguments must be constants, so the
///     descriptions keep literal numbers; this test is what keeps them true.
/// </summary>
public sealed class McpDefaultDescriptionTests
{
    public static IEnumerable<object[]> NumericDefaults()
    {
        yield return [typeof(AnnotationsTool), "limit", AnnotationHandler.DefaultLimit];
        yield return [typeof(ContextTool), "content_budget", ContextHandler.DefaultBudget];
        yield return [typeof(ContextTool), "content_budget", ContextHandler.DefaultTypeAnchorBudget];
        yield return [typeof(DeadCandidatesTool), "limit", DeadCandidatesHandler.DefaultLimit];
        yield return [typeof(DeadCandidatesTool), "limit", DeadCandidateLiveness.MaxPageSize];
        yield return [typeof(StatusTool), "max_bytes", StatusTool.DefaultMaxBytes];
        yield return [typeof(StatusTool), "max_bytes", McpLimits.MaxStatusBytes];
    }

    [Theory]
    [MemberData(nameof(NumericDefaults))]
    public void Description_StatesTheOwningConstant(Type toolType, string parameterName, int expected)
    {
        var method = DocRenderers.ToolMethods(toolType).Single();
        var parameter = method.GetParameters().Single(p => p.Name == parameterName);
        var description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description
            ?? method.GetCustomAttribute<DescriptionAttribute>()!.Description;

        Assert.Contains(expected.ToString(CultureInfo.InvariantCulture), description);
    }
}
