using System.Text.Json.Serialization;

namespace Lurp.Workspace;

public sealed record SnapshotCompleteness
{
    [JsonPropertyName("generated_trees_included")]
    public bool GeneratedTreesIncluded { get; init; }

    [JsonPropertyName("skipped_adapters")] public List<string> SkippedAdapters { get; init; } = [];

    [JsonPropertyName("active_tfms")] public Dictionary<string, string> ActiveTfms { get; init; } = [];

    [JsonPropertyName("extractor_version")]
    public string ExtractorVersion { get; init; } = "";

    [JsonPropertyName("binding_incompleteness")]
    public List<BindingIncompletenessRecord> BindingIncompleteness { get; init; } = [];

    [JsonPropertyName("binding_incompleteness_summary")]
    public List<BindingIncompletenessSummary> BindingIncompletenessSummary { get; init; } = [];

    [JsonPropertyName("binding_incompleteness_total")]
    public int BindingIncompletenessTotal { get; init; }

    /// <summary>
    ///     The single place where binding-incompleteness fields are populated
    ///     together, so detail, summary, and total can never drift apart.
    /// </summary>
    public SnapshotCompleteness WithBindingIncompleteness(
        IReadOnlyList<BindingIncompletenessRecord> records, bool includeDetail)
    {
        return this with
        {
            BindingIncompleteness = includeDetail ? records.ToList() : [],
            BindingIncompletenessSummary = BuildBindingIncompletenessSummary(records),
            BindingIncompletenessTotal = records.Sum(static record => record.Count)
        };
    }

    internal static List<BindingIncompletenessSummary> BuildBindingIncompletenessSummary(IReadOnlyList<BindingIncompletenessRecord> records)
    {
        return records
            .GroupBy(static record => (record.ProjectName, record.Reason))
            .Select(static group => new BindingIncompletenessSummary(
                group.Key.ProjectName, group.Key.Reason, group.Sum(static record => record.Count)))
            .OrderBy(static summary => summary.ProjectName, StringComparer.Ordinal)
            .ThenBy(static summary => summary.Reason, StringComparer.Ordinal)
            .ToList();
    }
}