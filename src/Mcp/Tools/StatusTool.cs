using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lurp.Handlers;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class StatusTool
{
    private readonly McpSessionContext _session;
    private const int DefaultMaxDocuments = 50;
    private const int DefaultMaxMismatches = 50;
    private const int EnvelopeCapBytes = 80000;

    public StatusTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_status", Title = "Lurp Status", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Show pinned snapshot status and freshness. Uses full workspace check when --solution= was given, otherwise cheap stat check. Supports sections and caps to bound payload.")]
    public async Task<string> LurpStatus(
        string? snapshot_id = null,
        object? detail = null,
        string? sections = null,
        int? max_documents = null,
        int? max_mismatches = null,
        object? documents = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);
            var maxDocs = max_documents ?? DefaultMaxDocuments;
            var maxMism = max_mismatches ?? DefaultMaxMismatches;
            if (maxDocs < 1)
                throw new McpProtocolException("--max-documents must be a positive integer.", McpErrorCode.InvalidParams);
            if (maxMism < 1)
                throw new McpProtocolException("--max-mismatches must be a positive integer.", McpErrorCode.InvalidParams);

            var resolvedSections = ResolveSections(detail, sections);
            var includeManifest = resolvedSections != "freshness";
            var includeReferences = resolvedSections == "references" || resolvedSections == "all";
            var includeDocumentsInManifest = resolvedSections == "all";
            var includeCompleteness = resolvedSections == "completeness" || resolvedSections == "all";
            var includeMismatches = includeManifest;

            var requestedDocs = ParseDocuments(documents);
            if (documents != null && requestedDocs == null)
                throw new McpProtocolException("--documents must be an array of strings.", McpErrorCode.InvalidParams);

            // Normalize requested docs early for validation and later use
            List<string>? normalizedRequested = null;
            if (requestedDocs != null)
            {
                normalizedRequested = new List<string>();
                foreach (var raw in requestedDocs)
                {
                    var norm = HandlerBootstrap.NormalizeDocumentPath(raw) ?? raw;
                    if (string.IsNullOrWhiteSpace(norm))
                        throw new McpProtocolException("--documents contains an empty path.", McpErrorCode.InvalidParams);
                    normalizedRequested.Add(norm);
                }
            }

            object freshness;
            FreshnessStamp? cheapStampForDoc = null;
            WorkspaceFreshness.FreshnessResult? fullResultForDoc = null;
            string freshnessScope = "documents_only";

            if (!string.IsNullOrEmpty(_session.SolutionPath) && File.Exists(_session.SolutionPath))
            {
                try
                {
                    var result = await CheckFullFreshnessAsync(snapshotId);
                    fullResultForDoc = result;
                    var state = result.IsFresh ? "fresh" : "stale";
                    var count = result.Mismatches.Count;
                    var mismatchesCapped = includeMismatches ? result.Mismatches.Take(maxMism).ToList() : new List<SnapshotMismatch>();
                    var mismatchesTruncated = result.Mismatches.Count > mismatchesCapped.Count;

                    List<string>? sample = null;
                    bool sampleTruncated = false;
                    if (includeMismatches && count > 0)
                    {
                        // Gap1: drop sample when mismatches present (redundant)
                        sample = null;
                    }
                    else
                    {
                        var sampleRaw = result.Mismatches
                            .Select(m => m.Document?.ToString() ?? m.Description)
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();
                        var capped = sampleRaw.Take(maxDocs).ToList();
                        sampleTruncated = sampleRaw.Count > capped.Count;
                        sample = capped;
                    }

                    var freshnessDict = new Dictionary<string, object?>
                    {
                        ["state"] = state,
                        ["method"] = "full",
                        ["changed_document_count"] = count,
                        ["checked_at_utc"] = DateTime.UtcNow,
                        ["snapshot_id"] = snapshotId,
                        ["scope"] = "full",
                        ["is_fresh"] = result.IsFresh
                    };
                    if (sample != null)
                    {
                        freshnessDict["changed_documents_sample"] = sample;
                        if (sampleTruncated)
                            freshnessDict["changed_documents_sample_truncated"] = true;
                    }
                    else
                    {
                        // still include empty or null sample field for parity; include as null so consumer knows it's omitted due to mismatches
                        freshnessDict["changed_documents_sample"] = null;
                    }
                    if (includeMismatches)
                    {
                        freshnessDict["mismatches"] = mismatchesCapped.Select(m => new
                        {
                            kind = m.Kind.ToString(),
                            description = m.Description,
                            document = m.Document?.ToString(),
                            detail = m.Detail
                        }).ToList();
                        if (mismatchesTruncated)
                            freshnessDict["mismatches_truncated"] = true;
                    }
                    else
                    {
                        freshnessDict["mismatches"] = null;
                    }
                    freshness = freshnessDict;
                    freshnessScope = "full";
                }
                catch
                {
                    var stamp = _session.GetFreshness();
                    cheapStampForDoc = stamp;
                    freshness = _session.GetFreshnessJsonWithStamp(stamp, maxDocs);
                    freshnessScope = stamp.Scope;
                }
            }
            else
            {
                var stamp = _session.GetFreshness();
                cheapStampForDoc = stamp;
                freshness = _session.GetFreshnessJsonWithStamp(stamp, maxDocs);
                freshnessScope = stamp.Scope;
            }

            object? detailObj = null;
            if (includeManifest)
            {
                try
                {
                    var latestRow = _session.Store.LoadSnapshot(snapshotId);
                    var schemaVersion = _session.Store.GetCurrentSchemaVersion();
                    var dbPath = _session.DbPath;
                    detailObj = new
                    {
                        database_path = dbPath,
                        schema_version = schemaVersion,
                        latest_snapshot_id = snapshotId,
                        manifest = latestRow != null
                            ? ManifestJson(WithBindingCompleteness(_session.Store, latestRow, includeCompleteness), includeDocuments: includeDocumentsInManifest, includeReferences: includeReferences)
                            : null,
                        git_root = latestRow?.GitRoot,
                        solution_path = latestRow?.SolutionPath
                    };
                }
                catch (Exception ex)
                {
                    detailObj = new
                    {
                        error = ex.GetType().Name,
                        error_message = ex.Message,
                        note = "detail unavailable due to a store error"
                    };
                }
            }

            // Compute document_freshness if requested
            List<object>? documentFreshness = null;
            if (normalizedRequested != null && normalizedRequested.Count > 0)
            {
                documentFreshness = ComputeDocumentFreshness(snapshotId, cheapStampForDoc, fullResultForDoc, normalizedRequested);
            }

            var envelope = new Dictionary<string, object?>
            {
                ["snapshot_id"] = snapshotId,
                ["freshness"] = freshness,
                ["pinned"] = true,
                ["detail"] = detailObj
            };
            if (documentFreshness != null)
                envelope["document_freshness"] = documentFreshness;

            var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });

            // Hard envelope cap per Gap1 step 5
            if (json.Length > EnvelopeCapBytes)
            {
                // Rebuild with manifest stripped and truncation marker
                var truncatedDetail = new
                {
                    note = $"payload truncated at {EnvelopeCapBytes} bytes; manifest omitted. Use sections=references or max_documents/max_mismatches to request specific data."
                };
                var truncatedEnvelope = new Dictionary<string, object?>
                {
                    ["snapshot_id"] = snapshotId,
                    ["freshness"] = freshness,
                    ["pinned"] = true,
                    ["detail"] = includeManifest ? truncatedDetail : detailObj,
                    ["truncated"] = true
                };
                if (documentFreshness != null)
                    truncatedEnvelope["document_freshness"] = documentFreshness;
                json = JsonSerializer.Serialize(truncatedEnvelope, new JsonSerializerOptions { WriteIndented = true });
            }

            return json;
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw McpErrorMapper.Map(ex);
        }
    }

    private List<object> ComputeDocumentFreshness(string snapshotId, FreshnessStamp? cheapStamp, WorkspaceFreshness.FreshnessResult? fullResult, List<string> requested)
    {
        var snapshotDocs = _session.Store.GetDocumentVersionIdsByPath(snapshotId);
        var snapshotSet = new HashSet<string>(snapshotDocs.Keys, StringComparer.Ordinal);
        HashSet<string> changedSet;
        if (fullResult != null)
        {
            changedSet = new HashSet<string>(
                fullResult.Mismatches
                    .Where(m => m.Document != null)
                    .Select(m => m.Document!.ToString()!),
                StringComparer.Ordinal);
        }
        else if (cheapStamp != null)
        {
            changedSet = new HashSet<string>(cheapStamp.ChangedDocumentsSample, StringComparer.Ordinal);
        }
        else
        {
            var fallback = _session.GetFreshness();
            changedSet = new HashSet<string>(fallback.ChangedDocumentsSample, StringComparer.Ordinal);
        }

        var result = new List<object>();
        foreach (var norm in requested)
        {
            string state;
            if (!snapshotSet.Contains(norm))
                state = "not_in_snapshot";
            else if (changedSet.Contains(norm))
                state = "stale";
            else
                state = "fresh";
            result.Add(new { document = norm, state });
        }
        return result;
    }

    private static string ResolveSections(object? detail, string? sections)
    {
        if (!string.IsNullOrWhiteSpace(sections))
        {
            var lower = sections.Trim().ToLowerInvariant();
            var parts = lower.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            // NormalizeKnown values
            bool hasAll = parts.Contains("all");
            if (hasAll) return "all";
            // Priority: references > completeness > manifest > freshness > documents
            if (parts.Contains("references")) return "references";
            if (parts.Contains("completeness")) return "completeness";
            if (parts.Contains("manifest")) return "manifest";
            if (parts.Contains("freshness")) return "freshness";
            if (parts.Contains("documents")) return "manifest"; // documents is handled as manifest variant
            // Single unknown value: map known aliases, else fresh
            var single = parts.FirstOrDefault();
            return single switch
            {
                "freshness" => "freshness",
                "manifest" => "manifest",
                "references" => "references",
                "completeness" => "completeness",
                "all" => "all",
                _ => "freshness"
            };
        }
        if (detail != null)
        {
            if (IsDetail(detail)) return "manifest";
            else return "freshness";
        }
        return "freshness";
    }

    private static List<string>? ParseDocuments(object? documents)
    {
        if (documents == null) return null;
        if (documents is string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return new List<string> { s };
        }
        if (documents is string[] arr)
        {
            var list = arr.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return list.Count == 0 ? null : list;
        }
        if (documents is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in je.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var str = el.GetString();
                        if (!string.IsNullOrWhiteSpace(str)) list.Add(str!);
                    }
                    else if (el.ValueKind != JsonValueKind.Null)
                    {
                        // coerce numbers etc to string?
                        var str = el.ToString();
                        if (!string.IsNullOrWhiteSpace(str)) list.Add(str);
                    }
                }
                return list.Count == 0 ? null : list;
            }
            if (je.ValueKind == JsonValueKind.String)
            {
                var str = je.GetString();
                return string.IsNullOrWhiteSpace(str) ? null : new List<string> { str! };
            }
            if (je.ValueKind == JsonValueKind.Null || je.ValueKind == JsonValueKind.Undefined)
                return null;
            return null;
        }
        if (documents is IEnumerable<string> enStr)
        {
            var list = enStr.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return list.Count == 0 ? null : list;
        }
        if (documents is IEnumerable<object> enObj)
        {
            var list = new List<string>();
            foreach (var o in enObj)
            {
                if (o is string str && !string.IsNullOrWhiteSpace(str)) list.Add(str);
                else if (o is JsonElement je2 && je2.ValueKind == JsonValueKind.String)
                {
                    var str2 = je2.GetString();
                    if (!string.IsNullOrWhiteSpace(str2)) list.Add(str2!);
                }
            }
            return list.Count == 0 ? null : list;
        }
        return null;
    }

    private async Task<WorkspaceFreshness.FreshnessResult> CheckFullFreshnessAsync(string snapshotId)
    {
        if (!MSBuildLocator.IsRegistered)
        {
            try { MSBuildLocator.RegisterDefaults(); } catch { }
        }

        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(_session.SolutionPath!);
        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(_session.SolutionPath!))!;
        var workspaceInfo = new WorkspaceInfo(solution, gitRoot);
        var metadata = _session.Store.LoadSnapshot(snapshotId);
        if (metadata == null)
            return new WorkspaceFreshness.FreshnessResult(false, [new SnapshotMismatch(MismatchKind.VersionChanged, "Snapshot not found.", null, snapshotId)]);

        var manifest = SnapshotManifest.FromStorageManifest(metadata);
        return WorkspaceFreshness.CheckFreshness(workspaceInfo, manifest);
    }

    private static bool IsDetail(object? detail)
    {
        if (detail == null) return false;
        if (detail is bool b) return b;
        if (detail is string s)
        {
            if (bool.TryParse(s, out var parsed)) return parsed;
            if (string.Equals(s, "all", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "documents", StringComparison.OrdinalIgnoreCase)) return true;
            return !string.IsNullOrWhiteSpace(s);
        }
        if (detail is JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.String:
                    var str = e.GetString();
                    if (bool.TryParse(str, out var pb)) return pb;
                    if (string.Equals(str, "all", StringComparison.OrdinalIgnoreCase)) return true;
                    return !string.IsNullOrEmpty(str);
                case JsonValueKind.Number:
                    return e.GetInt32() != 0;
                default: return false;
            }
        }
        var t = detail.GetType().Name;
        if (t == "JsonElement")
        {
            try
            {
                var je = (JsonElement)detail;
                if (je.ValueKind == JsonValueKind.True) return true;
                if (je.ValueKind == JsonValueKind.False) return false;
            }
            catch { }
        }
        return false;
    }

    private static SnapshotManifest WithBindingCompleteness(SqliteIndexStore store, SnapshotRow snapshot, bool includeDetail)
    {
        var manifest = SnapshotManifest.FromStorageManifest(snapshot);
        var records = store.GetBindingIncompleteness(snapshot.SnapshotId);
        manifest.Completeness = manifest.Completeness?.WithBindingIncompleteness(records, includeDetail);
        return manifest;
    }

    private static object? ManifestJson(SnapshotManifest manifest, bool includeDocuments, bool includeReferences)
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(manifest, new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            if (node is not JsonObject obj)
                return node;

            if (!includeDocuments)
            {
                var docCount = (obj["document_versions"] as JsonObject)?.Count ?? 0;
                obj.Remove("document_versions");
                obj["document_count"] = docCount;
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
                obj["metadata_reference_note"] = "Full identities omitted; pass sections=references to include them.";
            }

            return node;
        }
        catch
        {
            return null;
        }
    }

    private static object? ManifestJson(SnapshotRow row, bool includeDocuments, bool includeReferences)
    {
        return ManifestJson(SnapshotManifest.FromStorageManifest(row), includeDocuments, includeReferences);
    }

    // Back-compat overload for callers that still use single bool
    private static object? ManifestJson(SnapshotRow row, bool includeDocuments)
    {
        return ManifestJson(row, includeDocuments, includeReferences: false);
    }
}
