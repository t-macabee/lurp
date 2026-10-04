using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class DeadCandidateStore
{
    private static readonly HashSet<string> LiveKinds = new(StringComparer.Ordinal)
    {
        nameof(EdgeKind.Calls),
        nameof(EdgeKind.MethodGroupRef),
        nameof(EdgeKind.Constructs),
        nameof(EdgeKind.Reads),
        nameof(EdgeKind.Writes),
        nameof(EdgeKind.Handles),
        nameof(EdgeKind.RoutesTo),
        nameof(EdgeKind.Registers),
        nameof(EdgeKind.MapsTo),
        nameof(EdgeKind.MayDispatchTo),
        nameof(EdgeKind.StaticallyCalls),
        nameof(EdgeKind.TestedBy),
        nameof(EdgeKind.ReflectionTypeRef),
        nameof(EdgeKind.ReflectionMemberRef),
        nameof(EdgeKind.ReflectionNameCandidate)
    };

    private static readonly HashSet<string> StrongProvenance = new(StringComparer.Ordinal)
    {
        Provenance.CompilerProved,
        Provenance.FrameworkDerived,
        Provenance.GlobalImplementationRelation
    };

    private static readonly HashSet<string> CandidateKinds = new(StringComparer.Ordinal)
    {
        nameof(IndexedSymbolKind.Type),
        nameof(IndexedSymbolKind.Method),
        nameof(IndexedSymbolKind.Property),
        nameof(IndexedSymbolKind.Field),
        nameof(IndexedSymbolKind.Event)
    };

    private static readonly HashSet<string> SerializationAttributeSubstrings = new(StringComparer.Ordinal)
    {
        "JsonPropertyName",
        "JsonProperty",
        "DataMember",
        "JsonIgnore",
        "IgnoreDataMember"
    };

    private readonly SqliteConnection _connection;

    public DeadCandidateStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public DeadCandidatePage GetDeadCandidatesPage(
        string snapshotId,
        string? project,
        string? document,
        string? kind,
        bool includePublic,
        bool includeGenerated,
        bool includeTests,
        int limit,
        DeadCandidateCursor? cursor)
    {
        if (string.IsNullOrEmpty(snapshotId))
            throw new ArgumentException("snapshotId is required.", nameof(snapshotId));
        if (limit <= 0)
            throw new ArgumentException("--limit must be a positive integer.", nameof(limit));

        limit = Math.Max(1, limit);
        var fingerprint = DeadCandidateCursor.ComputeFingerprint(project, document, kind, includePublic, includeGenerated, includeTests);
        if (cursor != null)
        {
            try { cursor.Validate(snapshotId, fingerprint); }
            catch (ArgumentException ex) { throw new ArgumentException(ex.Message, ex); }
        }

        // Fetch base candidate rows for snapshot
        var allCandidates = FetchCandidateRows(snapshotId);

        // Fetch auxiliary persisted facts once
        var mapsToTargets = FetchMapsToTargets(snapshotId);
        var bindingRecords = FetchBindingIncompleteness(snapshotId);
        var projectHasSystemTextJson = FetchProjectHasSystemTextJson(snapshotId);
        var incompletenessByDocument = BuildUnobservableByDocument(bindingRecords);
        var incompletenessProjects = BuildUnobservableProjects(bindingRecords);
        // Pre-fetch declarations for all candidates in batches
        var declInfo = FetchDeclarationInfo(snapshotId, allCandidates.Select(c => c.SymbolId).ToList(), includeGenerated);

        // Apply candidate-universe filters (kind, project, document, generated, tests) in-memory
        var filteredCandidates = new List<CandidateRow>();
        foreach (var c in allCandidates)
        {
            if (!CandidateKinds.Contains(c.Kind))
                continue;
            if (!string.IsNullOrEmpty(kind) && !string.Equals(c.Kind, kind, StringComparison.OrdinalIgnoreCase))
                continue;

            var assemblyName = ParseAssemblyName(c.AssemblyIdentity);
            var isTestProject = IsTestProject(assemblyName);
            if (!includeTests && isTestProject)
                continue;

            // project filter: exact match on assembly name
            if (!string.IsNullOrEmpty(project) && !string.Equals(assemblyName, project, StringComparison.Ordinal))
                continue;

            // document filter: requires at least one declaration in that document
            if (!string.IsNullOrEmpty(document))
            {
                if (!declInfo.TryGetValue(c.SymbolId, out var di) || di.Locations.Count == 0)
                    continue;
                var found = false;
                foreach (var loc in di.Locations)
                {
                    if (string.Equals(loc.DocumentPath, document, StringComparison.Ordinal))
                    { found = true; break; }
                }
                if (!found)
                    continue;
            }

            // is_generated: derived from declInfo
            var isGenerated = false;
            if (declInfo.TryGetValue(c.SymbolId, out var d))
                isGenerated = d.IsGenerated;

            if (!includeGenerated && isGenerated)
                continue;

            filteredCandidates.Add(c);
        }

        // Candidate count is filtered universe before LIVE/suppression
        var candidateCount = filteredCandidates.Count;

        if (filteredCandidates.Count == 0)
            return new DeadCandidatePage([], null, candidateCount, 0, 0, 0);

        // Sort for deterministic keyset
        filteredCandidates.Sort((a, b) => string.Compare(a.SymbolId, b.SymbolId, StringComparison.Ordinal));

        // Batched LIVE incoming edges for filtered candidates
        var incomingByTarget = FetchIncomingLiveEdgesBatched(snapshotId, filteredCandidates.Select(c => c.SymbolId).ToList());

        // Evaluate each candidate to status/reason
        var evaluated = new List<DeadCandidateEntry>();
        var deadCount = 0;
        var uncertainCount = 0;
        var unresolvedCount = 0;

        foreach (var cand in filteredCandidates)
        {
            var decl = declInfo.TryGetValue(cand.SymbolId, out var d) ? d : new DeclInfo { IsGenerated = false, Locations = [], DeclarationCount = 0, DocumentPaths = [] };
            var docPaths = decl.DocumentPaths;
            var accessibility = ParseAccessibility(cand.MetadataJson);
            var assemblyName = ParseAssemblyName(cand.AssemblyIdentity);
            var isTest = IsTestProject(assemblyName);
            var hasSystemTextJson = projectHasSystemTextJson.TryGetValue(assemblyName, out var has) && has;

            var incoming = incomingByTarget.TryGetValue(cand.SymbolId, out var list) ? list : [];
            var hasStrong = incoming.Any(e => StrongProvenance.Contains(e.Provenance));
            if (hasStrong)
            {
                // Alive - not included in dead page, but provenance still needed for summary if we ever wanted
                continue;
            }

            // Prepare incoming summary
            var summary = BuildIncomingSummary(incoming);

            // Determine if overlaps binding incompleteness (unobservable)
            var overlapsBinding = OverlapsBindingIncompleteness(docPaths, assemblyName, incompletenessByDocument, incompletenessProjects);

            string status;
            string reason;
            List<DeadCandidateUncertainty> uncertainties;

            if (overlapsBinding)
            {
                status = DeadCandidateStatus.Unresolved;
                reason = DeadCandidateReason.BindingIncompleteness;
                uncertainties = [MakeBindingIncompletenessUncertainty(cand.SymbolId, bindingRecords, docPaths, assemblyName)];
                unresolvedCount++;
            }
            else if (IsProcessEntryPoint(cand))
            {
                // Checked ahead of the public/protected suppression below: the entry point's own
                // accessibility varies by coding style (private for top-level statements, often
                // public/internal for an explicit Main), and either way "nothing in-repo calls the
                // process entry point" is definitional, not evidence of dead code. Surface it
                // visibly as uncertain rather than either proved_dead or silently excluded.
                status = DeadCandidateStatus.UncertainDead;
                reason = DeadCandidateReason.EntryPointConvention;
                uncertainties = [MakeEntryPointConventionUncertainty(cand.SymbolId)];
                uncertainCount++;
            }
            else if (IsPublicOrProtected(accessibility) && !includePublic)
            {
                // Excluded from proved_dead - not counted as dead at all
                continue;
            }
            else if (IsPublicOrProtected(accessibility) && includePublic)
            {
                status = DeadCandidateStatus.UncertainDead;
                reason = DeadCandidateReason.PublicSurface;
                uncertainties = [MakePublicSurfaceUncertainty(cand.SymbolId)];
                uncertainCount++;
            }
            else if (decl.IsGenerated && includeGenerated)
            {
                status = DeadCandidateStatus.Uncertain;
                reason = DeadCandidateReason.GeneratedExcluded;
                uncertainties = [MakeGeneratedUncertainty(cand.SymbolId)];
                uncertainCount++;
            }
            else if (isTest && includeTests)
            {
                // Test harness reflection discovery
                status = DeadCandidateStatus.Uncertain;
                reason = DeadCandidateReason.TestHarness;
                uncertainties = [MakeTestHarnessUncertainty(cand.SymbolId)];
                uncertainCount++;
            }
            else if (incoming.Count > 0)
            {
                // Weak-only incoming
                var best = GetStrongestWeak(incoming);
                if (best == null)
                {
                    // Should not happen, but fallback to proved
                    status = DeadCandidateStatus.ProvedDead;
                    reason = DeadCandidateReason.NoIncomingLiveEdges;
                    uncertainties = [];
                    deadCount++;
                }
                else if (best.Provenance == Provenance.Possible && string.Equals(best.Kind, nameof(EdgeKind.MayDispatchTo), StringComparison.Ordinal))
                {
                    status = DeadCandidateStatus.UncertainDead;
                    reason = DeadCandidateReason.PossibleDispatch;
                    uncertainties = [MakePossibleDispatchUncertainty(best)];
                    uncertainCount++;
                }
                else if (best.Provenance == Provenance.Convention)
                {
                    status = DeadCandidateStatus.UncertainDead;
                    reason = DeadCandidateReason.FrameworkConvention;
                    uncertainties = [MakeFrameworkConventionUncertainty(best)];
                    uncertainCount++;
                }
                else if (best.Provenance == Provenance.NameCandidate)
                {
                    status = DeadCandidateStatus.UncertainDead;
                    reason = DeadCandidateReason.NameCandidate;
                    uncertainties = [MakeNameCandidateUncertainty(best)];
                    uncertainCount++;
                }
                else if (best.Provenance == Provenance.RuntimeUnknown)
                {
                    status = DeadCandidateStatus.Unresolved;
                    reason = DeadCandidateReason.RuntimeUnknown;
                    uncertainties = [MakeRuntimeUnknownUncertainty(best)];
                    // RuntimeUnknown is counted as unresolved per spec table
                    unresolvedCount++;
                }
                else
                {
                    status = DeadCandidateStatus.UncertainDead;
                    reason = DeadCandidateReason.FrameworkConvention;
                    uncertainties = [MakeFrameworkConventionUncertainty(best)];
                    uncertainCount++;
                }
            }
            else
            {
                // No LIVE incoming at all - check EF and serialization before proved
                var isEfPrivate = IsEfPrivateMember(cand, accessibility, mapsToTargets);
                if (isEfPrivate)
                {
                    status = DeadCandidateStatus.UncertainDead;
                    reason = DeadCandidateReason.EfConvention;
                    uncertainties = [MakeEfConventionUncertainty(cand.SymbolId)];
                    uncertainCount++;
                }
                else if (IsSerializationConvention(cand, accessibility, hasSystemTextJson, decl))
                {
                    status = DeadCandidateStatus.UncertainDead;
                    reason = DeadCandidateReason.SerializationConvention;
                    uncertainties = [MakeSerializationConventionUncertainty(cand.SymbolId)];
                    uncertainCount++;
                }
                else
                {
                    status = DeadCandidateStatus.ProvedDead;
                    reason = DeadCandidateReason.NoIncomingLiveEdges;
                    uncertainties = [];
                    deadCount++;
                }
            }

            var projectName = assemblyName;
            var documentPath = decl.Locations.Count > 0 ? decl.Locations[0].DocumentPath : null;
            // Find best declaration location for start line? Use first location's start
            var entry = new DeadCandidateEntry(
                cand.SymbolId,
                cand.Fqn,
                cand.Kind,
                accessibility,
                documentPath,
                decl.Locations,
                projectName,
                decl.DeclarationCount,
                decl.IsGenerated,
                status,
                reason,
                uncertainties,
                summary);
            evaluated.Add(entry);
        }

        // Sort evaluated by symbol_id ASC (already in order because filteredCandidates sorted and we iterated in order)
        // Apply cursor pagination over evaluated (dead) list
        var totalDeadAndUncertain = evaluated; // includes proved, uncertain, unresolved
        // But counts above are for totals; pagination over all dead candidates (proved+uncertain+unresolved)
        // Cursor is over dead symbol ids, not candidate universe.
        IEnumerable<DeadCandidateEntry> windowed = totalDeadAndUncertain;
        if (cursor != null)
        {
            windowed = windowed.Where(e => string.Compare(e.SymbolId, cursor.LastSymbolId, StringComparison.Ordinal) > 0);
        }
        var windowedList = windowed.ToList();
        var pageItems = windowedList.Take(limit + 1).ToList();
        string? nextCursor = null;
        if (pageItems.Count > limit)
        {
            pageItems.RemoveAt(pageItems.Count - 1);
            var last = pageItems[^1];
            nextCursor = new DeadCandidateCursor(snapshotId, fingerprint, last.SymbolId).Encode();
        }

        return new DeadCandidatePage(pageItems, nextCursor, candidateCount, deadCount, uncertainCount, unresolvedCount);
    }

    private List<CandidateRow> FetchCandidateRows(string snapshotId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT ss.symbol_id, s.kind, ss.fqn, ss.metadata_json, s.doc_comment_id, s.assembly_identity
            FROM snapshot_symbols ss
            JOIN symbols s ON s.symbol_id = ss.symbol_id
            WHERE ss.snapshot_id = @snapshotId
            ORDER BY ss.symbol_id ASC;
            """;
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        var list = new List<CandidateRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new CandidateRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }
        return list;
    }

    private HashSet<string> FetchMapsToTargets(string snapshotId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT target_symbol_id FROM edges WHERE snapshot_id = @snapshotId AND kind = @kind;";
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("@kind", nameof(EdgeKind.MapsTo));
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add(reader.GetString(0));
        return set;
    }

    private List<BindingIncompletenessRecord> FetchBindingIncompleteness(string snapshotId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT project_name, document_path, reason, occurrence_count, extractor_version FROM binding_incompleteness WHERE snapshot_id = @snapshotId;";
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        var list = new List<BindingIncompletenessRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var doc = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (doc != null && doc.Length == 0) doc = null;
            // stored document_path may be empty string for project-level rows (binding store uses "" for null)
            // Normalize empty to null for consistency
            list.Add(new BindingIncompletenessRecord(reader.GetString(0), doc, reader.GetString(2), reader.GetInt32(3), reader.GetString(4)));
        }
        return list;
    }

    private Dictionary<string,bool> FetchProjectHasSystemTextJson(string snapshotId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name, metadata_reference_identities FROM projects WHERE snapshot_id = @snapshotId;";
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        var dict = new Dictionary<string,bool>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var json = reader.IsDBNull(1) ? null : reader.GetString(1);
            var has = false;
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var arr = JsonSerializer.Deserialize<string[]>(json);
                    if (arr != null)
                    {
                        foreach (var id in arr)
                        {
                            // id is like "System.Text.Json, Version=8.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51|sha256=..."
                            var assemblyPart = id.Split('|')[0];
                            var simpleName = assemblyPart.Split(',')[0].Trim();
                            if (string.Equals(simpleName, "System.Text.Json", StringComparison.Ordinal))
                            { has = true; break; }
                        }
                    }
                }
                catch { }
            }
            dict[name] = has;
        }
        return dict;
    }

    private static HashSet<string> BuildUnobservableByDocument(List<BindingIncompletenessRecord> records)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (r.DocumentPath == null) continue;
            if (IsUnobservableReason(r.Reason))
                set.Add(r.DocumentPath);
        }
        return set;
    }

    private static HashSet<string> BuildUnobservableProjects(List<BindingIncompletenessRecord> records)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
            if (r.DocumentPath == null && IsUnobservableReason(r.Reason))
                set.Add(r.ProjectName);
        return set;
    }

    private static bool IsUnobservableReason(string reason)
    {
        return reason is "ambiguous_overload" or "compiler_error" or "unresolved_metadata" or "unsupported_syntax" or "extractor_failure" or "project_unreadable" or "convention_scan";
    }

    private static bool OverlapsBindingIncompleteness(List<string> docPaths, string assemblyName, HashSet<string> byDoc, HashSet<string> byProject)
    {
        foreach (var dp in docPaths)
            if (byDoc.Contains(dp))
                return true;
        if (byProject.Contains(assemblyName))
            return true;
        return false;
    }

    private Dictionary<string, List<EdgeRecord>> FetchIncomingLiveEdgesBatched(string snapshotId, List<string> symbolIds)
    {
        var result = new Dictionary<string, List<EdgeRecord>>(StringComparer.Ordinal);
        if (symbolIds.Count == 0) return result;
        // Prepare live kinds filter string for SQL IN
        var liveKindList = string.Join(",", LiveKinds.Select((k, i) => $"'{k}'"));
        // Chunk symbolIds to avoid SQLITE_MAX_VARIABLE_NUMBER
        const int ChunkSize = 900;
        // edge_id is selected so each target's list can be ordered by it in C#. The old
        // ORDER BY edge_id forced the planner onto idx_edges_snapshot_id (whose implicit
        // rowid ordering satisfies the sort) and scanned the full snapshot edge set per
        // chunk; without it the planner probes idx_edges_snapshot_target per target id.
        // Order still matters: GetStrongestWeak resolves equal-rank ties by first occurrence.
        var byTarget = new Dictionary<string, List<(long EdgeId, EdgeRecord Record)>>(StringComparer.Ordinal);
        for (var i = 0; i < symbolIds.Count; i += ChunkSize)
        {
            var chunk = symbolIds.Skip(i).Take(ChunkSize).ToList();
            using var cmd = _connection.CreateCommand();
            var paramNames = chunk.Select((_, idx) => $"@p{idx}").ToList();
            cmd.CommandText = $"""
                SELECT edge_id, source_symbol_id, target_symbol_id, kind, provenance, snapshot_id, extractor_version, source_document_path, source_start_line, source_start_column, source_end_line, source_end_column, is_cross_generated, type_arguments_json, receiver_type_constraints_json
                FROM edges
                WHERE snapshot_id = @snapshotId
                  AND target_symbol_id IN ({string.Join(",", paramNames)})
                  AND kind IN ({liveKindList});
                """;
            cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            for (var idx = 0; idx < chunk.Count; idx++)
                cmd.Parameters.AddWithValue(paramNames[idx], chunk[idx]);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var edgeId = reader.GetInt64(0);
                var rec = new EdgeRecord
                {
                    SourceSymbolId = reader.GetString(1),
                    TargetSymbolId = reader.GetString(2),
                    Kind = reader.GetString(3),
                    Provenance = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    SnapshotId = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    ExtractorVersion = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    SourceDocumentPath = reader.IsDBNull(7) ? null : reader.GetString(7),
                    SourceStartLine = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    SourceStartColumn = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    SourceEndLine = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    SourceEndColumn = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    IsCrossGenerated = !reader.IsDBNull(12) && reader.GetBoolean(12),
                    TypeArgumentsJson = reader.IsDBNull(13) ? null : reader.GetString(13),
                    ReceiverTypeConstraintsJson = reader.IsDBNull(14) ? null : reader.GetString(14)
                };
                if (!byTarget.TryGetValue(rec.TargetSymbolId, out var lst))
                {
                    lst = [];
                    byTarget[rec.TargetSymbolId] = lst;
                }
                lst.Add((edgeId, rec));
            }
        }
        foreach (var (target, list) in byTarget)
        {
            list.Sort(static (a, b) => a.EdgeId.CompareTo(b.EdgeId));
            result[target] = [.. list.Select(static entry => entry.Record)];
        }
        return result;
    }

    private Dictionary<string, DeclInfo> FetchDeclarationInfo(string snapshotId, List<string> symbolIds, bool includeGenerated)
    {
        var result = new Dictionary<string, DeclInfo>(StringComparer.Ordinal);
        if (symbolIds.Count == 0) return result;
        const int ChunkSize = 800;
        // document_version_id -> (relative_path, line_starts, content), filled once per distinct
        // document across all chunks. The old per-chunk four-way join re-read the same
        // content/line_starts blobs for every chunk whose symbols shared a document.
        var documentCache = new Dictionary<string, (string DocPath, int[]? LineStarts, byte[]? Content)>(StringComparer.Ordinal);
        for (var i = 0; i < symbolIds.Count; i += ChunkSize)
        {
            var chunk = symbolIds.Skip(i).Take(ChunkSize).ToList();
            var paramNames = chunk.Select((_, idx) => $"@p{idx}").ToList();
            // Declarations only, driven from the symbol ids. CROSS JOIN pins declarations as the
            // outer table so that with no sqlite_stat1 rows the planner cannot start from
            // snapshot_documents and probe the IN list once per document row.
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT d.symbol_id, d.document_version_id, d.full_start, d.full_end, COALESCE(d.is_generated,0), d.is_partial
                FROM declarations d
                CROSS JOIN snapshot_documents sd
                WHERE sd.snapshot_id = @snapshotId
                  AND sd.document_version_id = d.document_version_id
                  AND d.symbol_id IN ({string.Join(",", paramNames)});
                """;
            cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            for (var idx = 0; idx < chunk.Count; idx++)
                cmd.Parameters.AddWithValue(paramNames[idx], chunk[idx]);

            var perSymbol = new Dictionary<string, List<(string DocVersionId, int? FullStart, int? FullEnd, int IsGenerated)>>(StringComparer.Ordinal);
            var pendingDocIds = new List<string>();
            var pendingDocIdSet = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var sid = reader.GetString(0);
                    var docVersionId = reader.GetString(1);
                    var fs = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
                    var fe = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
                    var isGen = reader.GetInt32(4);
                    if (!documentCache.ContainsKey(docVersionId) && pendingDocIdSet.Add(docVersionId))
                        pendingDocIds.Add(docVersionId);
                    if (!perSymbol.TryGetValue(sid, out var lst))
                    {
                        lst = [];
                        perSymbol[sid] = lst;
                    }
                    lst.Add((docVersionId, fs, fe, isGen));
                }
            }

            // Content and line_starts load once per distinct document_version_id (chunked for
            // SQLITE_MAX_VARIABLE_NUMBER), not once per declaration row.
            for (var docOffset = 0; docOffset < pendingDocIds.Count; docOffset += ChunkSize)
            {
                var docChunk = pendingDocIds.Skip(docOffset).Take(ChunkSize).ToList();
                var docParamNames = docChunk.Select((_, idx) => $"@d{idx}").ToList();
                using var docCmd = _connection.CreateCommand();
                docCmd.CommandText = $"""
                    SELECT dv.document_version_id, doc.relative_path, dv.line_starts, dv.content
                    FROM document_versions dv
                    JOIN documents doc ON doc.document_id = dv.document_id
                    WHERE dv.document_version_id IN ({string.Join(",", docParamNames)});
                    """;
                for (var idx = 0; idx < docChunk.Count; idx++)
                    docCmd.Parameters.AddWithValue(docParamNames[idx], docChunk[idx]);
                using var docReader = docCmd.ExecuteReader();
                while (docReader.Read())
                {
                    var docVersionId = docReader.GetString(0);
                    var docPath = docReader.GetString(1);
                    var lineStartsJson = docReader.IsDBNull(2) ? null : docReader.GetString(2);
                    int[]? lineStarts = null;
                    if (lineStartsJson != null)
                    {
                        try { lineStarts = JsonSerializer.Deserialize<int[]>(lineStartsJson); }
                        catch { lineStarts = null; }
                    }
                    var content = docReader.IsDBNull(3) ? null : (byte[])docReader[3];
                    documentCache[docVersionId] = (docPath, lineStarts, content);
                }
            }

            foreach (var kv in perSymbol)
            {
                var sid = kv.Key;
                var rows = kv.Value;
                // The old query only returned rows that joined document_versions/documents;
                // keep rows without document data out of locations while still counting them
                // (the old separate COUNT(*) query had no such join either).
                var locatedRows = new List<(string DocPath, int? FullStart, int? FullEnd, int[]? LineStarts, byte[]? Content, int IsGenerated)>();
                foreach (var r in rows)
                {
                    if (documentCache.TryGetValue(r.DocVersionId, out var doc))
                        locatedRows.Add((doc.DocPath, r.FullStart, r.FullEnd, doc.LineStarts, doc.Content, r.IsGenerated));
                }

                // Same order as the old ORDER BY d.symbol_id, doc.relative_path, d.full_start:
                // ordinal path (SQLite BINARY), NULL full_start first.
                locatedRows.Sort(static (a, b) =>
                {
                    var byPath = string.CompareOrdinal(a.DocPath, b.DocPath);
                    return byPath != 0 ? byPath : Comparer<int?>.Default.Compare(a.FullStart, b.FullStart);
                });

                var isGeneratedOverall = locatedRows.Any(static r => r.IsGenerated == 1);
                var declCount = locatedRows.Count > 0 ? rows.Count : 0;
                var locations = new List<DeclarationLocation>();
                var docPaths = new List<string>();
                foreach (var r in locatedRows)
                {
                    if (r.FullStart == null || r.FullEnd == null || r.LineStarts == null || r.Content == null)
                    {
                        // Degraded location without line mapping -> use 0
                        locations.Add(new DeclarationLocation(r.DocPath, 0, 0, 0, 0, r.IsGenerated == 1));
                        if (!docPaths.Contains(r.DocPath, StringComparer.Ordinal)) docPaths.Add(r.DocPath);
                        continue;
                    }
                    try
                    {
                        var lineStarts = r.LineStarts;
                        if (lineStarts is { Length: > 0 } && r.FullStart.Value >= 0 && r.FullEnd.Value >= r.FullStart.Value && r.FullEnd.Value <= r.Content.Length)
                        {
                            var sIdx = FindLineIndex(lineStarts, r.FullStart.Value);
                            var eIdx = FindLineIndex(lineStarts, r.FullEnd.Value);
                            var startLine = LineNumbers.ToOneBased(sIdx);
                            var endLine = LineNumbers.ToOneBased(eIdx);
                            var startCol = Utf8Column(r.Content, lineStarts[sIdx], r.FullStart.Value);
                            var endCol = Utf8Column(r.Content, lineStarts[eIdx], r.FullEnd.Value);
                            locations.Add(new DeclarationLocation(r.DocPath, startLine, startCol, endLine, endCol, r.IsGenerated == 1));
                        }
                        else
                        {
                            locations.Add(new DeclarationLocation(r.DocPath, 0, 0, 0, 0, r.IsGenerated == 1));
                        }
                    }
                    catch
                    {
                        locations.Add(new DeclarationLocation(r.DocPath, 0, 0, 0, 0, r.IsGenerated == 1));
                    }
                    if (!docPaths.Contains(r.DocPath, StringComparer.Ordinal)) docPaths.Add(r.DocPath);
                }
                result[sid] = new DeclInfo { IsGenerated = isGeneratedOverall, Locations = locations, DeclarationCount = declCount, DocumentPaths = docPaths };
            }

            // Ensure symbols with no declarations (should not happen for snapshot_symbols but could for synthetic) still have entry
            foreach (var sid in chunk)
            {
                if (!result.ContainsKey(sid))
                    result[sid] = new DeclInfo { IsGenerated = false, Locations = [], DeclarationCount = 0, DocumentPaths = [] };
            }
        }
        return result;
    }

    private static int FindLineIndex(int[] lineStarts, int byteOffset)
    {
        int lo = 0, hi = lineStarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= byteOffset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private static int Utf8Column(byte[] content, int lineStart, int offset)
    {
        var safe = Math.Clamp(offset, lineStart, content.Length);
        return System.Text.Encoding.UTF8.GetCharCount(content, lineStart, safe - lineStart);
    }

    private static string? ParseAccessibility(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("accessibility", out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
            if (doc.RootElement.TryGetProperty("Accessibility", out var el2) && el2.ValueKind == JsonValueKind.String)
                return el2.GetString();
        }
        catch { }
        return null;
    }

    private static bool IsPublicOrProtected(string? accessibility)
    {
        return accessibility is "Public" or "Protected" or "ProtectedOrInternal";
    }

    private static string ParseAssemblyName(string assemblyIdentity)
    {
        var comma = assemblyIdentity.IndexOf(',');
        if (comma > 0) return assemblyIdentity[..comma].Trim();
        return assemblyIdentity.Trim();
    }

    private static bool IsTestProject(string assemblyName)
    {
        return string.Equals(assemblyName, "eNote.Tests", StringComparison.Ordinal)
            || assemblyName.EndsWith(".Tests", StringComparison.Ordinal);
    }

    /// <summary>
    ///     True when this candidate is the compilation's own process entry point (an explicit
    ///     <c>static void Main</c> or the compiler-synthesized top-level-statements form), tagged
    ///     at extraction time by <see cref="Lurp.Workspace.SymbolDeclarationExtractor"/> via
    ///     <c>SymbolMetadataKeys.IsEntryPoint</c>. Nothing in-repo ever calls the entry point —
    ///     the runtime launcher does — so it would otherwise always land in the terminal
    ///     no-incoming-edges branch below and read as proved_dead.
    /// </summary>
    private static bool IsProcessEntryPoint(CandidateRow cand)
    {
        if (cand.Kind != nameof(IndexedSymbolKind.Method))
            return false;
        if (string.IsNullOrEmpty(cand.MetadataJson))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(cand.MetadataJson);
            return doc.RootElement.TryGetProperty(SymbolMetadataKeys.IsEntryPoint, out var el)
                && el.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    private static bool IsEfPrivateMember(CandidateRow cand, string? accessibility, HashSet<string> mapsToTargets)
    {
        if (cand.Kind is not (nameof(IndexedSymbolKind.Method) or nameof(IndexedSymbolKind.Property) or nameof(IndexedSymbolKind.Field)))
            return false;
        if (accessibility is not ("Private" or "PrivateProtected"))
            return false;
        var enclosing = SymbolId.DeriveContainingTypeSymbolId(cand.SymbolId);
        if (enclosing == null) return false;
        return mapsToTargets.Contains(enclosing);
    }

    private static bool IsSerializationConvention(CandidateRow cand, string? accessibility, bool hasSystemTextJson, DeclInfo decl)
    {
        if (cand.Kind != nameof(IndexedSymbolKind.Property)) return false;
        if (accessibility is not ("Public" or "Internal")) return false;
        if (!hasSystemTextJson) return false;
        // Check if any attribute is serialization attribute
        if (string.IsNullOrEmpty(cand.MetadataJson)) return true; // no attributes -> attribute-free
        try
        {
            using var doc = JsonDocument.Parse(cand.MetadataJson);
            if (doc.RootElement.TryGetProperty("attributes", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var s = el.GetString();
                    if (s == null) continue;
                    foreach (var sub in SerializationAttributeSubstrings)
                        if (s.Contains(sub, StringComparison.Ordinal))
                            return false; // has explicit attr -> not convention blind spot
                }
                return true; // attribute array exists but none are serialization attrs
            }
            // No attributes property -> attribute-free
            return true;
        }
        catch { return true; }
    }

    private static DeadCandidateIncomingSummary BuildIncomingSummary(List<EdgeRecord> incoming)
    {
        var prov = new Dictionary<string,int>(StringComparer.Ordinal);
        var kind = new Dictionary<string,int>(StringComparer.Ordinal);
        int strong = 0, weak = 0;
        foreach (var e in incoming)
        {
            var p = string.IsNullOrEmpty(e.Provenance) ? "unknown" : e.Provenance;
            prov[p] = prov.GetValueOrDefault(p) + 1;
            kind[e.Kind] = kind.GetValueOrDefault(e.Kind) + 1;
            if (StrongProvenance.Contains(e.Provenance ?? string.Empty)) strong++; else weak++;
        }
        return new DeadCandidateIncomingSummary(strong, weak, prov, kind);
    }

    private static EdgeRecord? GetStrongestWeak(List<EdgeRecord> incoming)
    {
        EdgeRecord? best = null;
        var bestRank = -2;
        foreach (var e in incoming)
        {
            // Only consider weak provenance among LIVE kinds (already filtered)
            if (StrongProvenance.Contains(e.Provenance ?? string.Empty)) continue;
            var r = EdgeMerge.ProvenanceRank(e.Provenance ?? string.Empty);
            if (r > bestRank)
            {
                bestRank = r;
                best = e;
            }
        }
        return best;
    }

    private static DeadCandidateUncertainty MakeBindingIncompletenessUncertainty(string symbolId, List<BindingIncompletenessRecord> all, List<string> docPaths, string assemblyName)
    {
        // Find relevant binding records that overlap this candidate's docs. Restricted
        // to IsUnobservableReason so the description names the reason that actually
        // triggered OverlapsBindingIncompleteness — otherwise a co-located but
        // non-triggering record (e.g. filtered_external, which never makes a
        // candidate unresolved) could win the reason pick and describe the wrong cause.
        var relevant = all.Where(r => IsUnobservableReason(r.Reason)
                                   && (r.DocumentPath != null && docPaths.Contains(r.DocumentPath, StringComparer.Ordinal)
                                   || r.DocumentPath == null && string.Equals(r.ProjectName, assemblyName, StringComparison.Ordinal))).ToList();
        var byReason = relevant.GroupBy(r => r.Reason, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
        if (byReason != null)
        {
            var reason = byReason.Key;
            var count = byReason.Sum(r => r.Count);
            var projects = byReason.Select(r => r.ProjectName).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var scope = string.Join(", ", projects);
            var desc = DescribeBindingIncompleteness(reason, count, scope);
            return new DeadCandidateUncertainty([symbolId], "binding_incompleteness", desc);
        }
        return new DeadCandidateUncertainty([symbolId], "binding_incompleteness", "Binding incompleteness overlaps this symbol's document; relations may be missing.");
    }

    private static string DescribeBindingIncompleteness(string reason, int count, string scope)
    {
        return reason switch
        {
            "compiler_error" => $"{count} binding(s) in {scope} could not be completed because the snapshot compilation reported compiler errors in those projects. Relations that depend on that code may be missing from the graph even though the references exist in source.",
            "unresolved_metadata" => $"{count} binding(s) in {scope} could not be resolved against project metadata (for example missing package or project references). Relations that depend on those bindings may not be persisted even though the references exist in source.",
            "filtered_external" => $"{count} binding(s) in {scope} resolved to symbols in assemblies outside the compilation. Edges to those external targets are intentionally filtered from the persisted graph; their absence is a declared boundary, not an extraction failure.",
            "ambiguous_overload" => $"{count} binding(s) in {scope} were ambiguous, so no unique overload target could be selected. Dispatch targets for those call sites are uncertain.",
            "unsupported_syntax" => $"{count} binding(s) in {scope} could not be completed because the extractor does not support the relevant syntax. Relations at those sites may be missing.",
            "extractor_failure" => $"{count} extractor failure(s) were recorded while producing the snapshot for {scope}. Some relations may be missing.",
            "project_unreadable" => $"{count} binding(s) in {scope} could not be completed because the project was unreadable.",
            "convention_scan" => $"{count} binding(s) in {scope} are convention-scan sites with an open match set.",
            _ => $"{count} binding-incompleteness record(s) (reason '{reason}') affect {scope}. Relations in that code may be incomplete."
        };
    }

    private static DeadCandidateUncertainty MakePublicSurfaceUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "public_surface", "Public/protected member with no internal incoming LIVE edge — verify no external caller or reflection consumer before removing.");
    }

    private static DeadCandidateUncertainty MakeGeneratedUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "generated_excluded", $"Generated symbol '{symbolId}' was excluded because includeGenerated is set to false. Review generated code if runtime behavior depends on it.");
    }

    private static DeadCandidateUncertainty MakeTestHarnessUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "test_harness", "Test-harness symbol: reachable only via xUnit reflection discovery with no LIVE incoming edge. Verify no production code depends on this member before removing.");
    }

    private static DeadCandidateUncertainty MakePossibleDispatchUncertainty(EdgeRecord edge)
    {
        return new DeadCandidateUncertainty([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, $"Dispatch candidate '{edge.TargetSymbolId}' was resolved with evidence level '{edge.Provenance}'. Manually verify that the runtime dispatch reaches the correct implementation.");
    }

    private static DeadCandidateUncertainty MakeFrameworkConventionUncertainty(EdgeRecord edge)
    {
        return new DeadCandidateUncertainty([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, $"Convention-based framework binding: the '{edge.Kind}' edge was inferred by naming convention, not explicit registration. Verify that the expected target is reached at runtime.");
    }

    private static DeadCandidateUncertainty MakeNameCandidateUncertainty(EdgeRecord edge)
    {
        return new DeadCandidateUncertainty([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, $"Reflection name candidate: the string-based reference to '{edge.TargetSymbolId}' was matched by name. Verify that this reference correctly resolves at runtime.");
    }

    private static DeadCandidateUncertainty MakeRuntimeUnknownUncertainty(EdgeRecord edge)
    {
        var desc = $"Unmodeled construct: a '{edge.Kind}' edge carries 'runtime_unknown' provenance because the construct is listed in DeclaredBoundaries.Known as deliberately not fully modeled. The concrete type was resolved but the runtime activation/registration semantics are not captured. See DeclaredBoundaries.Known for the full, closed list of declared boundaries.";
        // For ReflectionTargetUnknown the more specific wording is:
        if (string.Equals(edge.Kind, nameof(EdgeKind.ReflectionTargetUnknown), StringComparison.Ordinal))
            desc = "Unknown reflection target: the runtime target of this reflection call cannot be statically determined.";
        return new DeadCandidateUncertainty([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, desc);
    }

    private static DeadCandidateUncertainty MakeEntryPointConventionUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "entry_point_convention", "Process entry point: this is the compilation's Main method (explicit or the compiler-synthesized top-level-statements form), invoked by the runtime launcher rather than from within the indexed call graph. It has no incoming edge by definition; that is not evidence of dead code.");
    }

    private static DeadCandidateUncertainty MakeEfConventionUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "ef_convention", "EF Core model conventions beyond query filters and indexes are not modeled: the containing type is an EF-mapped entity (MapsTo target) and the member is materialized via reflection/private-setter with no Writes edge. See DeclaredBoundaries.ef_convention.", "ef_convention");
    }

    private static DeadCandidateUncertainty MakeSerializationConventionUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "serialization_convention", "Serialization convention: the property is serialization-eligible (public/internal, no System.Text.Json attribute) in a System.Text.Json-referencing project, but no SerializationAdapter edge witnesses usage. Verify no serialization contract depends on this member — System.Text.Json serializes public properties by convention with no attribute.");
    }

    private sealed record CandidateRow(string SymbolId, string Kind, string? Fqn, string? MetadataJson, string DocCommentId, string AssemblyIdentity);

    private sealed class DeclInfo
    {
        public bool IsGenerated { get; set; }
        public List<DeclarationLocation> Locations { get; set; } = [];
        public int DeclarationCount { get; set; }
        public List<string> DocumentPaths { get; set; } = [];
    }
}
