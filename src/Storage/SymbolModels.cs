using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Lurp.Storage;

public enum EdgeKind
{
    Inherits,
    Implements,
    References,
    Contains,
    Overrides,
    Hides,
    ExtensionReceiver,
    Calls,
    Constructs,
    Reads,
    Writes,
    Returns,
    Throws,
    Declares,
    MayDispatchTo,
    StaticallyCalls,
    RoutesTo,
    Registers,
    Handles,
    MapsTo,
    TestedBy,
    ReflectionTypeRef,
    ReflectionMemberRef,
    ReflectionNameCandidate,
    ReflectionTargetUnknown
}

public enum IndexedSymbolKind
{
    Namespace,
    Type,
    Method,
    Property,
    Field,
    Event,
    Parameter,
    Local,
    RangeVariable,
    NamedType,
    ArrayType,
    PointerType,
    TypeParameter,
    Unknown
}

public sealed class SymbolId : IEquatable<SymbolId>
{
    public SymbolId(string docCommentId, string assemblyIdentity, string? fullyQualifiedName = null)
    {
        DocCommentId = docCommentId ?? throw new ArgumentNullException(nameof(docCommentId));
        AssemblyIdentity = assemblyIdentity ?? throw new ArgumentNullException(nameof(assemblyIdentity));
        FullyQualifiedName = fullyQualifiedName;
        Value = $"{docCommentId}|{assemblyIdentity}";
    }

    public string Value { get; }
    public string DocCommentId { get; }
    public string AssemblyIdentity { get; }
    public string? FullyQualifiedName { get; }

    public bool IsType =>
        DocCommentId is ['T', ':', ..];

    public bool Equals(SymbolId? other)
    {
        return other is not null && Value == other.Value;
    }

    public override bool Equals(object? obj)
    {
        return obj is SymbolId other && Equals(other);
    }

    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Value);
    }

    public override string ToString()
    {
        return Value;
    }

    public static SymbolId Parse(string value)
    {
        var pipeIndex = value.IndexOf('|');
        if (pipeIndex < 0)
            throw new FormatException($"Invalid SymbolId format: '{value}'. Expected 'docCommentId|assemblyIdentity'.");
        return new SymbolId(value[..pipeIndex], value[(pipeIndex + 1)..]);
    }

    public static bool TryParse(string value, [NotNullWhen(true)] out SymbolId? symbolId)
    {
        symbolId = null;
        var pipeIndex = value.IndexOf('|');
        if (pipeIndex < 0)
            return false;
        symbolId = new SymbolId(value[..pipeIndex], value[(pipeIndex + 1)..]);
        return true;
    }

    public static string? DeriveContainingTypeDocCommentId(string docCommentId)
    {
        if (string.IsNullOrEmpty(docCommentId))
            return null;

        if (docCommentId.Length < 3 || docCommentId[1] != ':')
            return null;

        var kind = docCommentId[0];
        if (kind is 'T' or 'N')
            return null;

        var afterPrefix = docCommentId[2..];

        var parenIndex = afterPrefix.IndexOf('(');
        var methodNamePart = parenIndex >= 0 ? afterPrefix[..parenIndex] : afterPrefix;

        var lastDot = methodNamePart.LastIndexOf('.');
        if (lastDot < 0)
            return null;

        return "T:" + methodNamePart[..lastDot];
    }

    /// <summary>
    ///     Given a member-style symbol id ("docCommentId|assemblyIdentity"), return
    ///     the symbol id of the containing type, or null when the symbol has no
    ///     derivable containing type (no pipe, a type/namespace doc comment id, or a
    ///     top-level member).
    /// </summary>
    public static string? DeriveContainingTypeSymbolId(string symbolId)
    {
        if (!TryParse(symbolId, out var parsed))
            return null;

        var typeDocCommentId = DeriveContainingTypeDocCommentId(parsed.DocCommentId);
        if (typeDocCommentId == null)
            return null;

        return $"{typeDocCommentId}|{parsed.AssemblyIdentity}";
    }
}

public sealed class DeclarationSpan
{
    public DeclarationSpan(int? start, int? end)
    {
        if (start.HasValue && end.HasValue && start.Value > end.Value)
            throw new ArgumentException($"Start ({start}) must be <= End ({end}).");
        Start = start;
        End = end;
    }

    public int? Start { get; }
    public int? End { get; }
    public int Length => Start.HasValue && End.HasValue ? End.Value - Start.Value : 0;

    public override string ToString()
    {
        return Start.HasValue && End.HasValue
            ? $"[{Start}..{End}) ({Length} bytes)"
            : "(null)";
    }
}

public sealed class SymbolDeclaration
{
    public SymbolId SymbolId { get; init; } = null!;
    public IndexedSymbolKind Kind { get; init; }
    public string DocumentVersionId { get; init; } = string.Empty;
    public DeclarationSpan FullSpan { get; init; } = null!;
    public DeclarationSpan SignatureSpan { get; init; } = null!;
    public DeclarationSpan BodySpan { get; init; } = null!;
    public DeclarationSpan NameSpan { get; init; } = null!;
    public bool IsPartial { get; init; }
    public string? MetadataJson { get; init; }
    public bool IsGenerated { get; init; }
    public string? GeneratorIdentity { get; init; }
}

public sealed record NavigationTarget(
    [property: JsonPropertyName("symbol_id")]
    string SymbolId,
    [property: JsonPropertyName("document_path")]
    string DocumentPath,
    [property: JsonPropertyName("document_version_id")]
    string DocumentVersionId,
    [property: JsonPropertyName("full_start")]
    int FullStart,
    [property: JsonPropertyName("full_end")]
    int FullEnd,
    [property: JsonPropertyName("name_start")]
    int NameStart,
    [property: JsonPropertyName("name_end")]
    int NameEnd,
    [property: JsonPropertyName("start_line")]
    int StartLine,
    [property: JsonPropertyName("end_line")]
    int EndLine);

/// <summary>
/// A declaration's source location. Lines are 1-based; columns are 0-based (Roslyn-native).
/// </summary>
public sealed record DeclarationLocation(
    [property: JsonPropertyName("document_path")]
    string DocumentPath,
    [property: JsonPropertyName("start_line")]
    int StartLine,
    [property: JsonPropertyName("start_column")]
    int StartColumn,
    [property: JsonPropertyName("end_line")]
    int EndLine,
    [property: JsonPropertyName("end_column")]
    int EndColumn,
    [property: JsonPropertyName("is_generated")]
    bool IsGenerated);