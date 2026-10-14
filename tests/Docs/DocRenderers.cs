using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Lurp.Mcp;
using Lurp.Workspace;
using ModelContextProtocol.Server;

namespace Lurp.Tests.Docs;

internal static class DocRenderers
{
    private static readonly FlagSpec[] SharedReadOptions =
        [FlagCatalog.Output, FlagCatalog.Quiet, FlagCatalog.Freshness, FlagCatalog.RequireFresh];

    internal static string RenderVersionFacts() =>
        $"tool {VersionConstants.ToolVersion}, schema v{VersionConstants.DatabaseSchemaVersion}, " +
        $"extractor {VersionConstants.ExtractorVersion}, CLI/MCP contract v{VersionConstants.CliMcpContractVersion}, " +
        $"output schema v{VersionConstants.OutputSchemaVersion}";

    internal static string RenderFlagTable(IEnumerable<FlagSpec> specs)
    {
        var rows = specs.Select(RenderRow);
        return "\n| Argument | Required | Description |\n|---|---|---|\n"
               + string.Join("\n", rows) + "\n";
    }

    internal static string RenderModeFlags(Program.ModeRegistryEntry entry)
    {
        var specs = entry.Specs.Where(spec => !SharedReadOptions.Contains(spec));
        return RenderFlagTable(specs);
    }

    internal static string RenderReadOptions() => RenderFlagTable(SharedReadOptions);

    private static string RenderRow(FlagSpec spec) =>
        $"| {RenderArgument(spec)} | {RenderRequirement(spec)} | {RenderDescription(spec)} |";

    private static string RenderArgument(FlagSpec spec)
    {
        if (!spec.TakesValue)
            return $"`{spec.Name}`";

        var values = spec.AllowedValues is { Count: > 0 }
            ? string.Join("\\|", spec.AllowedValues)
            : spec.ValueHint;
        return $"`{spec.Name}<{values}>`";
    }

    private static string RenderRequirement(FlagSpec spec) => spec.Requirement switch
    {
        FlagRequirement.Required when spec.EnvironmentVariable is not null => $"Yes, or `{spec.EnvironmentVariable}`",
        FlagRequirement.Required => "Yes",
        FlagRequirement.Conditional => "Yes*",
        _ => "No",
    };

    private static string RenderDescription(FlagSpec spec)
    {
        var description = spec.Description.Replace("|", "\\|");
        if (spec.Repeatable && !description.EndsWith(" May be repeated.", StringComparison.Ordinal))
            description += " May be repeated.";
        return description;
    }

    internal static string RenderMcpToolNames(IEnumerable<Type> toolTypes, string separator = ", ") =>
        string.Join(separator, toolTypes.SelectMany(ToolMethods).Select(method => $"`{ToolName(method)}`"));

    internal static string RenderMcpReadTools() =>
        $"{McpServeHandler.ReadToolTypes.Length} read tools ({RenderMcpToolNames(McpServeHandler.ReadToolTypes)})";

    internal static string RenderMcpWriteTools() => RenderMcpToolNames(McpServeHandler.WriteToolTypes, " and ");

    internal static string RenderMcpToolTotal() =>
        (McpServeHandler.ReadToolTypes.Length + McpServeHandler.WriteToolTypes.Length)
        .ToString(CultureInfo.InvariantCulture);

    internal static string RenderMcpToolTable()
    {
        var rows = McpServeHandler.ReadToolTypes.Select(type => RenderMcpToolRow(type, "read"))
            .Concat(McpServeHandler.WriteToolTypes.Select(type => RenderMcpToolRow(type, "write (needs `--enable-write-tools`)")));
        return "\n| Tool | Access | Parameters | Description |\n|---|---|---|---|\n"
               + string.Join("\n", rows) + "\n";
    }

    private static string RenderMcpToolRow(Type type, string access)
    {
        var method = ToolMethods(type).Single();
        var description = Regex.Replace(method.GetCustomAttribute<DescriptionAttribute>()!.Description, @"\s+", " ")
            .Replace("|", "\\|");
        return $"| `{ToolName(method)}` | {access} | {RenderMcpParameters(method)} | {description} |";
    }

    internal static string RenderMcpToolAccessTable()
    {
        var rows = McpServeHandler.ReadToolTypes.Select(type => RenderMcpToolAccessRow(type, "always"))
            .Concat(McpServeHandler.WriteToolTypes.Select(type => RenderMcpToolAccessRow(type, "only with `--enable-write-tools`")));
        return "\n| Tool | Registered | Effect (MCP annotations) |\n|---|---|---|\n"
               + string.Join("\n", rows) + "\n";
    }

    private static string RenderMcpToolAccessRow(Type type, string registered)
    {
        var attribute = ToolMethods(type).Single().GetCustomAttribute<McpServerToolAttribute>()!;
        var effect = attribute.ReadOnly ? "read-only" : attribute.Destructive ? "writes, destructive" : "writes";
        return $"| `{attribute.Name}` | {registered} | {effect} |";
    }

    internal static string RenderMcpLimits()
    {
        var rows = typeof(McpLimits).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .OrderBy(field => field.MetadataToken)
            .Select(field =>
                $"| {field.GetCustomAttribute<DescriptionAttribute>()!.Description.Replace("|", "\\|")} | " +
                $"{((int)field.GetRawConstantValue()!).ToString(CultureInfo.InvariantCulture)} |");
        return "\n| Parameter | Maximum |\n|---|---|\n" + string.Join("\n", rows) + "\n";
    }

    private static string RenderMcpParameters(MethodInfo method)
    {
        var parts = new List<string>();
        foreach (var parameter in method.GetParameters())
        {
            if (parameter.ParameterType == typeof(CancellationToken))
                continue;

            var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
            var rendered = $"`{parameter.Name}` ({RenderTypeName(type)}";
            if (parameter.HasDefaultValue && parameter.DefaultValue is not null)
            {
                var value = parameter.DefaultValue is bool flag
                    ? flag ? "true" : "false"
                    : Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture);
                rendered += $", default `{value}`";
            }

            rendered += ")";
            parts.Add(rendered);
        }

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static string RenderTypeName(Type type)
    {
        if (type.IsArray)
            return RenderTypeName(type.GetElementType()!) + "[]";
        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(long)) return "long";
        if (type == typeof(double)) return "double";
        if (type == typeof(object)) return "object";
        return type.Name;
    }

    internal static IEnumerable<MethodInfo> ToolMethods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null);

    private static string ToolName(MethodInfo method) =>
        method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
}
