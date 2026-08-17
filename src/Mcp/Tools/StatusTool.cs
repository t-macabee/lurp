using System.ComponentModel;
using System.Text.Json;
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

    public StatusTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_status", Title = "Lurp Status", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Show pinned snapshot status and freshness. Uses full workspace check when --solution= was given, otherwise cheap stat check. Detail expands sample and manifest.")]
    public async Task<string> LurpStatus(
        string? snapshot_id = null,
        object? detail = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);
            var isDetail = IsDetail(detail);

            object freshness;
            string freshnessMethod;
            bool isFresh = true;
            IReadOnlyList<object>? mismatchesForDetail = null;

            if (!string.IsNullOrEmpty(_session.SolutionPath) && File.Exists(_session.SolutionPath))
            {
                // Full freshness path — may touch MSBuild/WorkspaceLoader but acceptable only here.
                try
                {
                    var result = await CheckFullFreshnessAsync(snapshotId);
                    isFresh = result.IsFresh;
                    var state = result.IsFresh ? "fresh" : "stale";
                    freshnessMethod = "full";
                    var count = result.Mismatches.Count;
                    var sample = result.Mismatches
                        .Select(m => m.Document?.ToString() ?? m.Description)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Take(isDetail ? int.MaxValue : 10)
                        .ToList();
                    freshness = new
                    {
                        state,
                        method = freshnessMethod,
                        changed_document_count = count,
                        changed_documents_sample = sample,
                        checked_at_utc = DateTime.UtcNow,
                        snapshot_id = snapshotId,
                        scope = "full",
                        is_fresh = result.IsFresh,
                        mismatches = isDetail ? result.Mismatches.Select(m => new
                        {
                            kind = m.Kind.ToString(),
                            description = m.Description,
                            document = m.Document?.ToString(),
                            detail = m.Detail
                        }).ToList() : null
                    };
                    if (isDetail)
                        mismatchesForDetail = result.Mismatches.Select(m => (object)new
                        {
                            kind = m.Kind.ToString(),
                            description = m.Description,
                            document = m.Document?.ToString(),
                            detail = m.Detail
                        }).ToList();
                }
                catch
                {
                    // Fall back to cheap on any workspace load failure.
                    freshness = isDetail ? _session.GetFreshnessJsonUncapped() : _session.GetFreshnessJson();
                }
            }
            else
            {
                freshness = isDetail ? _session.GetFreshnessJsonUncapped() : _session.GetFreshnessJson();
            }

            object? detailObj = null;
            if (isDetail)
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
                        manifest = latestRow != null ? ManifestJson(latestRow, includeDocuments: false) : null,
                        git_root = latestRow?.GitRoot,
                        solution_path = latestRow?.SolutionPath
                    };
                }
                catch
                {
                    detailObj = new { note = "detail unavailable" };
                }
            }

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                detail = detailObj
            };

            return JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
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
        // Handle JsonElement boxed as object via System.Text.Json deserialization to object
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

    private static object? ManifestJson(SnapshotRow row, bool includeDocuments)
    {
        try
        {
            var manifest = SnapshotManifest.FromStorageManifest(row);
            var node = JsonSerializer.SerializeToNode(manifest, new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            if (node is System.Text.Json.Nodes.JsonObject obj && !includeDocuments)
            {
                var docCount = (obj["document_versions"] as System.Text.Json.Nodes.JsonObject)?.Count ?? 0;
                obj.Remove("document_versions");
                obj["document_count"] = docCount;
            }
            return node;
        }
        catch
        {
            return null;
        }
    }
}
