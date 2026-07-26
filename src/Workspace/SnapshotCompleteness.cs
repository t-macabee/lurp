using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lurp.Workspace;

public sealed class SnapshotCompleteness
{
    [JsonPropertyName("generated_trees_included")]
    public bool GeneratedTreesIncluded { get; init; }

    [JsonPropertyName("skipped_adapters")]
    public List<string> SkippedAdapters { get; init; } = [];

    [JsonPropertyName("active_tfms")]
    public Dictionary<string, string> ActiveTfms { get; init; } = [];

    [JsonPropertyName("extractor_version")]
    public string ExtractorVersion { get; init; } = "";
}
