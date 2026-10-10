using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lurp.Workspace;

internal static class SnapshotManifestJson
{
    internal static SnapshotManifest WithBindingCompleteness(IBindingIncompletenessStore store, SnapshotRow snapshot, bool includeDetail)
    {
        var manifest = SnapshotManifest.FromStorageManifest(snapshot);
        var records = store.GetBindingIncompleteness(snapshot.SnapshotId);
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
        {
            var documentCount = (obj["document_versions"] as JsonObject)?.Count ?? 0;
            obj.Remove("document_versions");
            obj["document_count"] = documentCount;
            obj["documents_note"] = $"Per-document versions omitted; pass {documentsHint} to include them.";
        }

        if (!includeReferences && obj["metadata_reference_identities"] is JsonObject refsObj)
        {
            var counts = new JsonObject();
            int total = 0;
            foreach (var kvp in refsObj)
            {
                var cnt = (kvp.Value as JsonArray)?.Count ?? 0;
                counts[kvp.Key] = cnt;
                total += cnt;
            }
            obj.Remove("metadata_reference_identities");
            obj["metadata_reference_counts"] = counts;
            obj["metadata_reference_total"] = total;
            obj["metadata_reference_note"] = $"Full identities omitted; pass {referencesHint} to include them.";
        }

        return node;
    }
}
