using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lurp.Handlers;
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
    internal const int DefaultMaxBytes = 40_000;

    public StatusTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_status", Title = "Lurp Status", ReadOnly = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(StatusResponse))]
    [Description("Show pinned snapshot status and freshness. Cheap stat check by default; pass full=true to run the MSBuild workspace freshness check (requires serve --solution=). max_bytes (default 40000) bounds the manifest parts only. Over the target, the largest manifest parts are summarized or left out, truncated is true, and detail.manifest.omitted_fields names them. Freshness and document_freshness are returned in full. The payload appears twice on the wire (content[0].text and structuredContent), and client size limits apply to both copies. Pass projects to narrow the manifest to named projects.")]
    public Task<string> LurpStatus(
        string? snapshot_id = null,
        [Description("Legacy; use sections. true selects sections=manifest; false or omitted selects none. Accepts true, false, 1 or 0; any other value is an error.")] object? detail = null,
        [Description("Sections to include, comma-separated: freshness, manifest, documents, references, completeness or all. Values combine as a union; documents returns the per-document version map. Unknown values are an error.")] string? sections = null,
        int? max_documents = null,
        int? max_mismatches = null,
        [Description("Array of document paths (strings) that selects the paths for the per-document freshness list (`document_freshness`). An empty array returns `null`. An empty or whitespace entry is an error.")] string[]? documents = null,
        bool? full = null,
        [Description("Project names (the keys of detail.manifest.target_frameworks). Builds the manifest from these projects only: their per-project maps, documents and binding rows. Needs a manifest section: any sections value except freshness.")] string[]? projects = null,
        [Description("Manifest size target in UTF-8 bytes (default 40000, at most 400000). Over the target, the largest manifest parts are summarized or left out and named in detail.manifest.omitted_fields. Other sections stay in full. Client size limits apply to both wire copies (content[0].text and structuredContent).")] int? max_bytes = null,
        CancellationToken cancellationToken = default) =>
        _session.RunReadToolAsync(snapshot_id, async (store, snapshotId) =>
        {
            var maxDocs = McpLimits.RequirePositiveAtMost(max_documents, DefaultMaxDocuments, McpLimits.MaxLimit, "max_documents");
            var maxMism = McpLimits.RequirePositiveAtMost(max_mismatches, StatusHandler.DefaultMaxMismatches, McpLimits.MaxLimit, "max_mismatches");
            var maxBytes = McpLimits.RequirePositiveAtMost(max_bytes, DefaultMaxBytes, McpLimits.MaxStatusBytes, "max_bytes");

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
                throw new McpProtocolException("projects needs a manifest section: use any sections value except freshness.", McpErrorCode.InvalidParams);

            List<string>? normalizedRequested = null;
            if (documents is { Length: > 0 })
            {
                McpLimits.RequireAtMost(documents.Length, McpLimits.MaxLimit, "documents length");
                normalizedRequested = new List<string>(documents.Length);
                foreach (var raw in documents)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        throw new McpProtocolException("documents contains an empty path.", McpErrorCode.InvalidParams);
                    var normalized = HandlerBootstrap.NormalizeDocumentPath(raw);
                    if (string.IsNullOrWhiteSpace(normalized))
                        throw new McpProtocolException("documents contains an empty path.", McpErrorCode.InvalidParams);
                    normalizedRequested.Add(normalized);
                }
            }

            Dictionary<string, object?> freshness;
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
                catch (Exception ex)
                {
                    var stamp = McpSessionContext.GetFreshness(store, snapshotId);
                    cheapStampForDoc = stamp;
                    freshness = FreshnessJson.ForStatus(stamp, maxDocs);
                    FreshnessJson.SetFullCheckError(freshness, FreshnessJson.DescribeFullCheckFailure(ex));
                }
            }
            else
            {
                var stamp = McpSessionContext.GetFreshness(store, snapshotId);
                cheapStampForDoc = stamp;
                freshness = FreshnessJson.ForStatus(stamp, maxDocs);
                if (full == true)
                    FreshnessJson.SetFullCheckError(freshness, FreshnessJson.FullCheckNotRun);
            }

            StatusDetail? detailObj = null;
            JsonNode? manifestNode = null;
            if (includeManifest)
            {
                var filters = new
                {
                    projects = projectFilter?.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                    max_bytes = maxBytes
                };
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
                    detailObj = new StatusDetail(dbPath, schemaVersion, VersionConstants.CliMcpContractVersion,
                        snapshotId, manifestNode, filters, latestRow?.GitRoot, latestRow?.SolutionPath, null, null, null);
                }
                catch (McpProtocolException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    detailObj = new StatusDetail(_session.DbPath, null, VersionConstants.CliMcpContractVersion,
                        null, null, filters, null, null, ex.GetType().Name, ex.Message,
                        "detail unavailable due to a store error");
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
                var envelope = new StatusResponse(snapshotId, VersionConstants.CliMcpContractVersion,
                    freshness, true, detailObj, documentFreshness, truncated);
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
        }, cancellationToken);

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
        var detailValue = false;
        if (detail != null && !TryParseDetail(detail, out detailValue))
            throw new McpProtocolException("detail: unsupported value. Use true, false, 1 or 0, or use sections.", McpErrorCode.InvalidParams);
        if (!string.IsNullOrWhiteSpace(sections))
        {
            if (!StatusSections.TryParse(sections, allowSurfaceNames: true, out var parsed, out var badValue))
                throw new McpProtocolException($"sections: unknown value '{badValue}'. Use {StatusSections.Vocabulary(true)}.", McpErrorCode.InvalidParams);
            return parsed;
        }
        if (detailValue)
            return new StatusSectionSet(Manifest: true, Documents: false, References: false, Completeness: false);
        return default;
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

    private static bool TryParseDetail(object? detail, out bool value)
    {
        value = false;
        switch (detail)
        {
            case null:
                return true;
            case bool b:
                value = b;
                return true;
            case string s:
                if (string.IsNullOrWhiteSpace(s))
                    return true;
                return bool.TryParse(s, out value);
            case int i:
                if (i is not (0 or 1))
                    return false;
                value = i == 1;
                return true;
            case JsonElement e:
                switch (e.ValueKind)
                {
                    case JsonValueKind.True:
                        value = true;
                        return true;
                    case JsonValueKind.False:
                        return true;
                    case JsonValueKind.Null:
                    case JsonValueKind.Undefined:
                        return true;
                    case JsonValueKind.String:
                        return TryParseDetail(e.GetString(), out value);
                    case JsonValueKind.Number:
                        if (!e.TryGetInt32(out var n) || n is not (0 or 1))
                            return false;
                        value = n == 1;
                        return true;
                    default:
                        return false;
                }
            default:
                return false;
        }
    }
}

internal sealed record StatusDetail(
    string? database_path, int? schema_version, int? contract_version, string? latest_snapshot_id,
    JsonNode? manifest, object? filters, string? git_root, string? solution_path,
    string? error, string? error_message, string? note);

internal sealed record StatusResponse(
    string snapshot_id, int contract_version, Dictionary<string, object?> freshness,
    bool pinned, StatusDetail? detail, List<object>? document_freshness, bool truncated);
