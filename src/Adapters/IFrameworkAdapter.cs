namespace Lurp.Adapters;

/// <summary>
/// Edges plus any annotations produced by a single adapter run. Adapters that
/// emit no annotations return their edge list directly — the implicit
/// conversion supplies an empty annotation list.
/// </summary>
public sealed record AdapterExtractionResult(
    List<EdgeRecord> Edges,
    IReadOnlyList<AnnotationRecord> Annotations)
{
    public AdapterExtractionResult(List<EdgeRecord> edges) : this(edges, []) { }

    public static implicit operator AdapterExtractionResult(List<EdgeRecord> edges) => new(edges);
}

public interface IFrameworkAdapter
{
    string Name { get; }
    string Version { get; }

    /// <summary>
    /// Human-readable description of what this adapter extracts. Feeds the
    /// <c>extractors</c> table row for <see cref="Version"/>, so the version
    /// string and its description have a single source: the adapter itself.
    /// </summary>
    string Description { get; }

    AdapterExtractionResult Extract(AdapterExtractionContext context);
}
