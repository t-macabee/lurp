using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lurp.Workspace;

internal static class SnapshotManifestJson
{
    private const string BindingRowsPart = "completeness.binding_incompleteness";
    private const string FittedPartNote = "Omitted to fit the response size cap; see omitted_note.";

    private static readonly string[] _summarizableParts =
    [
        "document_versions",
        "metadata_reference_identities",
        BindingRowsPart
    ];

    private static readonly string[] _removableParts =
    [
        "project_documents",
        "project_graph",
        "compilation_options_fingerprints",
        "assembly_names",
        "metadata_reference_counts",
        "completeness.active_tfms",
        "completeness.binding_incompleteness_summary"
    ];

    internal static SnapshotManifest WithBindingCompleteness(IBindingIncompletenessStore store, SnapshotRow snapshot, bool includeDetail, IReadOnlySet<string>? projects = null)
    {
        var manifest = SnapshotManifest.FromStorageManifest(snapshot, projects);
        var records = store.GetBindingIncompleteness(snapshot.SnapshotId);
        if (projects != null)
            records = [.. records.Where(record => projects.Contains(record.ProjectName))];
        manifest.Completeness = manifest.Completeness?.WithBindingIncompleteness(records, includeDetail);
        return manifest;
    }

    /// <summary>
    ///     Renders the manifest for JSON output, replacing the per-document version map with
    ///     its count unless the caller asks for it with <paramref name="includeDocuments" />.
    ///     The map is the single largest thing in the JSON status output (196 entries on the
    ///     self-host solution) and is almost never what the caller wanted. It is *summarized*,
    ///     not dropped: <c>document_count</c> takes its place, so the output never silently
    ///     looks like a snapshot with no documents. Serializing through a node keeps every
    ///     other manifest field bound to the model rather than to a hand-copied field list
    ///     that would drift the first time the manifest gains a property. The caller passes
    ///     the hint text, and both surfaces call it (B17).
    /// </summary>
    internal static JsonNode? Render(SnapshotManifest manifest, bool includeDocuments, bool includeReferences, string documentsHint, string referencesHint)
    {
        var node = JsonSerializer.SerializeToNode(manifest, LurpJsonOptions.CompactIgnoreNull);
        if (node is not JsonObject obj)
            return node;

        if (!includeDocuments)
            SummarizeDocuments(obj, $"Per-document versions omitted; pass {documentsHint} to include them.");

        if (!includeReferences)
            SummarizeReferences(obj, $"Full identities omitted; pass {referencesHint} to include them.");

        return node;
    }

    private static void SummarizeDocuments(JsonObject manifest, string note)
    {
        var documentCount = (manifest["document_versions"] as JsonObject)?.Count ?? 0;
        manifest.Remove("document_versions");
        manifest["document_count"] = documentCount;
        manifest["documents_note"] = note;
    }

    private static void SummarizeReferences(JsonObject manifest, string note)
    {
        if (manifest["metadata_reference_identities"] is not JsonObject refsObj)
            return;

        var counts = new JsonObject();
        int total = 0;
        foreach (var kvp in refsObj)
        {
            var count = (kvp.Value as JsonArray)?.Count ?? 0;
            counts[kvp.Key] = count;
            total += count;
        }

        manifest.Remove("metadata_reference_identities");
        manifest["metadata_reference_counts"] = counts;
        manifest["metadata_reference_total"] = total;
        manifest["metadata_reference_note"] = note;
    }

    /// <summary>
    ///     Shrinks a rendered manifest until <paramref name="fits" /> returns true: the largest
    ///     part still in full form is summarized or removed, then recorded in
    ///     <c>omitted_fields</c>, and this repeats until the manifest fits or no part is left,
    ///     with <paramref name="note" /> written to <c>omitted_note</c> on the first entry (B21).
    ///     The caller invokes this only when the response is already over its size cap, so
    ///     <paramref name="fits" /> is first evaluated after the first part changed and the
    ///     caller's own serialization is not repeated for a manifest that already fits. No
    ///     property outside the summarizable and removable parts is touched.
    /// </summary>
    internal static IReadOnlyList<TruncationEntry> FitToSize(JsonObject manifest, Func<bool> fits, string note)
    {
        var entries = new List<TruncationEntry>();
        while (true)
        {
            var candidate = FindLargestPart(manifest);
            if (candidate is null)
                break;

            var (path, summarizable) = candidate.Value;
            if (summarizable)
            {
                SummarizePart(manifest, path);
                entries.Add(new TruncationEntry(path, "summarized"));
            }
            else
            {
                RemoveProperty(manifest, path);
                entries.Add(new TruncationEntry(path, "budget_exhausted"));
            }

            manifest["omitted_fields"] = JsonSerializer.SerializeToNode(entries, LurpJsonOptions.CompactIgnoreNull);
            if (entries.Count == 1)
                manifest["omitted_note"] = note;

            if (fits())
                break;
        }

        return entries;
    }

    private static void SummarizePart(JsonObject manifest, string path)
    {
        if (path == BindingRowsPart)
        {
            ((JsonObject)manifest["completeness"]!)["binding_incompleteness"] = new JsonArray();
            return;
        }

        if (path == "document_versions")
            SummarizeDocuments(manifest, FittedPartNote);
        else
            SummarizeReferences(manifest, FittedPartNote);
    }

    private static (string Path, bool Summarizable)? FindLargestPart(JsonObject manifest)
    {
        (string Path, bool Summarizable)? best = null;
        var bestLength = -1;

        void Consider(string path, bool summarizable)
        {
            var node = ResolveProperty(manifest, path);
            if (node is null)
                return;
            if (path == BindingRowsPart && node is not JsonArray { Count: > 0 })
                return;

            var length = node.ToJsonString().Length;
            if (best is null
                || length > bestLength
                || (length == bestLength && string.CompareOrdinal(path, best.Value.Path) < 0))
            {
                best = (path, summarizable);
                bestLength = length;
            }
        }

        foreach (var path in _summarizableParts)
            Consider(path, true);
        foreach (var path in _removableParts)
            Consider(path, false);

        return best;
    }

    private static JsonNode? ResolveProperty(JsonObject manifest, string path)
    {
        var dot = path.IndexOf('.');
        return dot < 0
            ? manifest[path]
            : (manifest[path[..dot]] as JsonObject)?[path[(dot + 1)..]];
    }

    private static void RemoveProperty(JsonObject manifest, string path)
    {
        var dot = path.IndexOf('.');
        if (dot < 0)
            manifest.Remove(path);
        else
            (manifest[path[..dot]] as JsonObject)?.Remove(path[(dot + 1)..]);
    }
}
