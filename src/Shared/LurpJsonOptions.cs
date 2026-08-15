using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lurp.Shared;

internal static class LurpJsonOptions
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions IndentedIgnoreNull = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly JsonSerializerOptions CompactIgnoreNull = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly JsonSerializerOptions SnakeCaseIndented = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}