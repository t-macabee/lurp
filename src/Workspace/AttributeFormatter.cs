using Microsoft.CodeAnalysis;
using System.Text;

namespace Lurp.Workspace;

internal static class AttributeFormatter
{
    private static readonly SymbolDisplayFormat FullyQualifiedFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    public static string FormatAttribute(AttributeData attr)
    {
        var sb = new StringBuilder();
        sb.Append(attr.AttributeClass?.ToDisplayString(FullyQualifiedFormat) ?? "?");

        var parts = new List<string>();

        parts.AddRange(attr.ConstructorArguments.Select(FormatTypedConstant));

        parts.AddRange(attr.NamedArguments.Select(named => $"{named.Key} = {FormatTypedConstant(named.Value)}"));

        if (parts.Count == 0)
            return sb.ToString();

        sb.Append('(');
        sb.Append(string.Join(", ", parts));
        sb.Append(')');
        return sb.ToString();
    }

    private static string FormatTypedConstant(TypedConstant constant)
    {
        if (constant.IsNull)
            return "null";

        return constant.Kind switch
        {
            TypedConstantKind.Primitive => FormatPrimitive(constant.Value),
            TypedConstantKind.Enum => FormatEnum(constant),
            TypedConstantKind.Type => $"typeof({((INamedTypeSymbol?)constant.Value)?.ToDisplayString(FullyQualifiedFormat) ?? "?"})",
            TypedConstantKind.Array => $"[{string.Join(", ", constant.Values.Select(FormatTypedConstant))}]",
            _ => constant.ToString() ?? "?"
        };
    }

    private static string FormatEnum(TypedConstant constant)
    {
        var enumType = constant.Type?.ToDisplayString(FullyQualifiedFormat);
        var value = constant.Value;
        if (enumType != null && value != null)
            return $"{enumType}.{value}";
        return constant.ToString() ?? "?";
    }

    private static string FormatPrimitive(object? value)
    {
        return value switch
        {
            null => "null",
            string s => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
            char c => $"'{c}'",
            bool b => b ? "true" : "false",
            _ => FormattableString.Invariant($"{value}")
        };
    }
}