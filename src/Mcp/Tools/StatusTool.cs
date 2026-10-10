using System.ComponentModel;
using System.Text;
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
    private const int DefaultMaxBytes = 40_000;

    public StatusTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_status", Title = "Lurp Status", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Show pinned snapshot status and freshness. Cheap stat check by default; pass full=true to run the MSBuild workspace freshness check (requires serve --solution=). The response is capped at max_bytes (default 40000): over the cap, the largest manifest parts are summarized or left out, truncated is true and detail.manifest.omitted_fields names them; pass projects to narrow the manifest to named projects.")]
    public async Task<string> LurpStatus(
        string? snapshot_id = null,
        object? detail = null,
        [Description("Sections to include, comma-separated: freshness, manifest, documents, references, completeness or all. Values combine as a union; documents returns the per-document version map. Unknown values are an error.")] string? sections = null,
        int? max_documents = null,
        int? max_mismatches = null,
        object? documents = null,
        bool? full = null,
        [Description("Project names (the keys of detail.manifest.target_frameworks). Builds the manifest from these projects only: their per-project maps, documents and binding rows. Needs sections=manifest, completeness, references or all.")] string[]? projects = null,
        [Description("Response size cap in UTF-8 bytes (default 40000, at most 400000). Over the cap, the largest manifest parts are summarized or left out and named in detail.manifest.omitted_fields. Above about 44000 bytes of reference identities, MCP clients with a 25000-token output limit may flag the result as truncated.")] int? max_bytes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);
            cancellationToken.ThrowIfCancellationRequested();
            using var store = _session.OpenReadStore();
            var maxDocs = max_documents ?? DefaultMaxDocuments;
            var maxMism = max_mismatches ?? DefaultMaxMismatches;
            if (maxDocs < 1)
                throw new McpProtocolException("max-documents must be a positive integer.", McpErrorCode.InvalidParams);
            if (maxMism < 1)
                throw new McpProtocolException("max-mismatches must be a positive integer.", McpErrorCode.InvalidParams);

            var maxBytes = max_bytes ?? DefaultMaxBytes;
            if (maxBytes < 1)
                throw new McpProtocolException("max_bytes must be a positive integer.", McpErrorCode.InvalidParams);
            McpLimits.RequireAtMost(max_bytes, McpLimits.MaxStatusBytes, "max_bytes");

            HashSet<string>? projectFilter = null;
            if (projects != null)
            {
                var names = projects.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
                if (names.Length > 0)
                    projectFilter = new HashSet<string>(names, StringComparer.Ordinal);
            }

            var resolvedSections = ResolveSections(detail, sections);
            var includeManifest = resolvedSections.Manifest;
            var includeReferences = resolvedSections.References;
            var includeDocumentsInManifest = resolvedSections.Documents;
            var includeCompleteness = resolvedSections.Completeness;
            var includeMismatches = includeManifest;

            if (projectFilter != null && !includeManifest)
                throw new McpProtocolException("projects needs a manifest section: sections=manifest, completeness, references or all.", McpErrorCode.InvalidParams);

            var requestedDocs = ParseDocuments(documents);
            if (documents != null && requestedDocs == null)
                throw new McpProtocolException("documents must be an array of strings.", McpErrorCode.InvalidParams);

            // Normalize requested docs early for validation and later use
            List<string>? normalizedRequested = null;
            if (requestedDocs != null)
            {
                normalizedRequested = new List<string>();
                foreach (var raw in requestedDocs)
                {
                    var norm = HandlerBootstrap.NormalizeDocumentPath(raw) ?? raw;
                    if (string.IsNullOrWhiteSpace(norm))
                        throw new McpProtocolException("documents contains an empty path.", McpErrorCode.InvalidParams);
                    normalizedRequested.Add(norm);
                }
            }

            object freshness;
            FreshnessStamp? cheapStampForDoc = null;
            WorkspaceFreshness.FreshnessResult? fullResultForDoc = null;

            if (full == true && !string.IsNullOrEmpty(_session.SolutionPath) && File.Exists(_session.SolutionPath))
            {
                try
                {
                    var result = await CheckFullFreshnessAsync(store, snapshotId, cancellationToken);
                    fullResultForDoc = result;
                    freshness = FreshnessJson.ForStatus(snapshotId, result, DateTime.UtcNow, maxDocs, maxMism, includeMismatches);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    var stamp = McpSessionContext.GetFreshness(store, snapshotId);
                    cheapStampForDoc = stamp;
                    freshness = FreshnessJson.ForStatus(stamp, maxDocs);
                }
            }
            else
            {
                var stamp = McpSessionContext.GetFreshness(store, snapshotId);
                cheapStampForDoc = stamp;
                freshness = FreshnessJson.ForStatus(stamp, maxDocs);
            }

            object? detailObj = null;
            JsonNode? manifestNode = null;
            if (includeManifest)
            {
                try
                {
                    var latestRow = store.LoadSnapshot(snapshotId);
                    if (projectFilter != null && latestRow != null)
                    {
                        var knownProjects = latestRow.Projects.Select(project => project.Name).ToHashSet(StringComparer.Ordinal);
                        foreach (var name in projectFilter)
                        {
                            if (!knownProjects.Contains(name))
                                throw new McpProtocolException($"projects: unknown project '{name}'. Use the keys of detail.manifest.target_frameworks.", McpErrorCode.InvalidParams);
                        }
                    }

                    var schemaVersion = store.GetCurrentSchemaVersion();
                    var dbPath = _session.DbPath;
                    manifestNode = latestRow != null
                        ? SnapshotManifestJson.Render(SnapshotManifestJson.WithBindingCompleteness(store, latestRow, includeCompleteness, projectFilter), includeDocumentsInManifest, includeReferences, "sections=documents", "sections=references")
                        : null;
                    detailObj = new
                    {
                        database_path = dbPath,
                        schema_version = schemaVersion,
                        contract_version = Lurp.Workspace.VersionConstants.CliMcpContractVersion,
                        latest_snapshot_id = snapshotId,
                        manifest = manifestNode,
                        filters = new
                        {
                            projects = projectFilter?.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                            max_bytes = maxBytes
                        },
                        git_root = latestRow?.GitRoot,
                        solution_path = latestRow?.SolutionPath
                    };
                }
                catch (McpProtocolException)
                {
                    throw;
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
                documentFreshness = ComputeDocumentFreshness(store, snapshotId, cheapStampForDoc, fullResultForDoc, normalizedRequested);
            }

            string Serialize(bool truncated)
            {
                var envelope = new Dictionary<string, object?>
                {
                    ["snapshot_id"] = snapshotId,
                    ["contract_version"] = Lurp.Workspace.VersionConstants.CliMcpContractVersion,
                    ["freshness"] = freshness,
                    ["pinned"] = true,
                    ["detail"] = detailObj
                };
                if (documentFreshness != null)
                    envelope["document_freshness"] = documentFreshness;
                if (truncated)
                    envelope["truncated"] = true;
                return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
            }

            bool Fits() => Encoding.UTF8.GetByteCount(Serialize(true)) <= maxBytes;

            var omittedNote = $"Parts listed in omitted_fields were summarized or left out to keep this response under {maxBytes} bytes. Pass projects=[...] (keys of target_frameworks) to narrow the manifest, or a larger max_bytes (at most {McpLimits.MaxStatusBytes}). CLI 'status --json --detail=all' has no size cap.";

            var json = Serialize(false);
            if (Encoding.UTF8.GetByteCount(json) > maxBytes
                && manifestNode is JsonObject manifestObject
                && SnapshotManifestJson.FitToSize(manifestObject, Fits, omittedNote).Count > 0)
                json = Serialize(true);

            return json;
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw McpErrorMapper.Map(ex);
        }
    }

    private static List<object> ComputeDocumentFreshness(SqliteIndexStore store, string snapshotId, FreshnessStamp? cheapStamp, WorkspaceFreshness.FreshnessResult? fullResult, List<string> requested)
    {
        var snapshotDocs = store.GetDocumentVersionIdsByPath(snapshotId);
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
            var fallback = McpSessionContext.GetFreshness(store, snapshotId);
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

    private static StatusSectionSet ResolveSections(object? detail, string? sections)
    {
        if (!string.IsNullOrWhiteSpace(sections))
        {
            if (!StatusSections.TryParse(sections, allowSurfaceNames: true, out var parsed, out var badValue))
                throw new McpProtocolException($"sections: unknown value '{badValue}'. Use {StatusSections.Vocabulary(true)}.", McpErrorCode.InvalidParams);
            return parsed;
        }
        if (detail != null && IsDetail(detail))
            return new StatusSectionSet(Manifest: true, Documents: false, References: false, Completeness: false);
        return default;
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

    private async Task<WorkspaceFreshness.FreshnessResult> CheckFullFreshnessAsync(SqliteIndexStore store, string snapshotId, CancellationToken cancellationToken)
    {
        if (!MSBuildLocator.IsRegistered) MSBuildRegistration.RegisterOrThrow();

        using var workspace = MSBuildWorkspace.Create(LurpCache.CreateWorkspaceGlobalProperties(_session.SolutionPath!));
        var solution = await workspace.OpenSolutionAsync(_session.SolutionPath!, cancellationToken: cancellationToken);
        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(_session.SolutionPath!))!;
        var workspaceInfo = new WorkspaceInfo(solution, gitRoot);
        var metadata = store.LoadSnapshot(snapshotId);
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
        return false;
    }
}
