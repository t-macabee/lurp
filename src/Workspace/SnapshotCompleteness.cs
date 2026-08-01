using System.Text.Json;
using System.Text.Json.Serialization;
using Lurp.Storage;

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

    [JsonPropertyName("binding_incompleteness")]
    public List<BindingIncompletenessRecord> BindingIncompleteness { get; init; } = [];

    [JsonPropertyName("binding_incompleteness_summary")]
    public List<BindingIncompletenessSummary> BindingIncompletenessSummary { get; init; } = [];

    [JsonPropertyName("binding_incompleteness_total")]
    public int BindingIncompletenessTotal { get; init; }
}
