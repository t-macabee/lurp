using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class DeadCandidateStore
{
    private static readonly HashSet<string> LiveKinds = new(StringComparer.Ordinal)
    {
        nameof(EdgeKind.Calls),
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

    private HashSet<string> BuildUnobservableByDocument(List<BindingIncompletenessRecord> records)
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

    private HashSet<string> BuildUnobservableProjects(List<BindingIncompletenessRecord> records)
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

    private bool OverlapsBindingIncompleteness(List<string> docPaths, string assemblyName, HashSet<string> byDoc, HashSet<string> byProject)
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
        for (var i = 0; i < symbolIds.Count; i += ChunkSize)
        {
            var chunk = symbolIds.Skip(i).Take(ChunkSize).ToList();
            using var cmd = _connection.CreateCommand();
            var paramNames = chunk.Select((_, idx) => $"@p{idx}").ToList();
            cmd.CommandText = $"""
                SELECT source_symbol_id, target_symbol_id, kind, provenance, snapshot_id, extractor_version, source_document_path, source_start_line, source_start_column, source_end_line, source_end_column, is_cross_generated, type_arguments_json, receiver_type_constraints_json
                FROM edges
                WHERE snapshot_id = @snapshotId
                  AND target_symbol_id IN ({string.Join(",", paramNames)})
                  AND kind IN ({liveKindList})
                ORDER BY edge_id;
                """;
            cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            for (var idx = 0; idx < chunk.Count; idx++)
                cmd.Parameters.AddWithValue(paramNames[idx], chunk[idx]);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var rec = new EdgeRecord
                {
                    SourceSymbolId = reader.GetString(0),
                    TargetSymbolId = reader.GetString(1),
                    Kind = reader.GetString(2),
                    Provenance = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    SnapshotId = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    ExtractorVersion = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    SourceDocumentPath = reader.IsDBNull(6) ? null : reader.GetString(6),
                    SourceStartLine = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    SourceStartColumn = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    SourceEndLine = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    SourceEndColumn = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    IsCrossGenerated = !reader.IsDBNull(11) && reader.GetBoolean(11),
                    TypeArgumentsJson = reader.IsDBNull(12) ? null : reader.GetString(12),
                    ReceiverTypeConstraintsJson = reader.IsDBNull(13) ? null : reader.GetString(13)
                };
                if (!result.TryGetValue(rec.TargetSymbolId, out var lst))
                {
                    lst = [];
                    result[rec.TargetSymbolId] = lst;
                }
                lst.Add(rec);
            }
        }
        return result;
    }

    private Dictionary<string, DeclInfo> FetchDeclarationInfo(string snapshotId, List<string> symbolIds, bool includeGenerated)
    {
        var result = new Dictionary<string, DeclInfo>(StringComparer.Ordinal);
        if (symbolIds.Count == 0) return result;
        const int ChunkSize = 800;
        // We need per-symbol locations and counts; we can batch query per chunk
        for (var i = 0; i < symbolIds.Count; i += ChunkSize)
        {
            var chunk = symbolIds.Skip(i).Take(ChunkSize).ToList();
            using var cmd = _connection.CreateCommand();
            var paramNames = chunk.Select((_, idx) => $"@p{idx}").ToList();
            cmd.CommandText = $"""
                SELECT d.symbol_id, doc.relative_path, d.full_start, d.full_end, dv.line_starts, dv.content, COALESCE(d.is_generated,0), d.is_partial
                FROM declarations d
                JOIN snapshot_documents sd ON sd.document_version_id = d.document_version_id
                JOIN document_versions dv ON dv.document_version_id = d.document_version_id
                JOIN documents doc ON doc.document_id = dv.document_id
                WHERE sd.snapshot_id = @snapshotId
                  AND d.symbol_id IN ({string.Join(",", paramNames)})
                ORDER BY d.symbol_id ASC, doc.relative_path ASC, d.full_start ASC;
                """;
            cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            for (var idx = 0; idx < chunk.Count; idx++)
                cmd.Parameters.AddWithValue(paramNames[idx], chunk[idx]);

            // Temporary grouping per symbol
            var perSymbol = new Dictionary<string, List<(string docPath, int? fs, int? fe, string? lineStarts, byte[]? content, int isGen)>>(StringComparer.Ordinal);
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var sid = reader.GetString(0);
                    var docPath = reader.GetString(1);
                    var fs = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
                    var fe = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
                    var ls = reader.IsDBNull(4) ? null : reader.GetString(4);
                    var content = reader.IsDBNull(5) ? null : (byte[])reader[5];
                    var isGen = reader.GetInt32(6);
                    if (!perSymbol.TryGetValue(sid, out var lst))
                    {
                        lst = [];
                        perSymbol[sid] = lst;
                    }
                    lst.Add((docPath, fs, fe, ls, content, isGen));
                }
            }

            // Get declaration counts per symbol in this chunk (separate query for accurate count)
            using var countCmd = _connection.CreateCommand();
            countCmd.CommandText = $"""
                SELECT d.symbol_id, COUNT(*)
                FROM declarations d
                JOIN snapshot_documents sd ON sd.document_version_id = d.document_version_id
                WHERE sd.snapshot_id = @snapshotId
                  AND d.symbol_id IN ({string.Join(",", paramNames)})
                GROUP BY d.symbol_id;
                """;
            countCmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            for (var idx = 0; idx < chunk.Count; idx++)
                countCmd.Parameters.AddWithValue(paramNames[idx], chunk[idx]);
            var counts = new Dictionary<string,int>(StringComparer.Ordinal);
            using (var cr = countCmd.ExecuteReader())
            {
                while (cr.Read()) counts[cr.GetString(0)] = cr.GetInt32(1);
            }

            foreach (var kv in perSymbol)
            {
                var sid = kv.Key;
                var rows = kv.Value;
                var isGeneratedOverall = rows.Any(r => r.isGen == 1);
                var declCount = counts.TryGetValue(sid, out var c) ? c : rows.Count;
                var locations = new List<DeclarationLocation>();
                var docPaths = new List<string>();
                foreach (var r in rows)
                {
                    // Skip generated rows if not including generated and overall not generated? Actually if includeGenerated false we already excluded generated symbols, but for remaining symbols all rows are non-generated.
                    // Still respect isGen filtering for locations: if isGen==1 and !includeGenerated we would skip, but such symbol wouldn't be in filtered list anyway.
                    // Keep all for accuracy when includeGenerated true
                    if (r.fs == null || r.fe == null || r.lineStarts == null || r.content == null)
                    {
                        // Degraded location without line mapping -> use 0
                        locations.Add(new DeclarationLocation(r.docPath, 0, 0, 0, 0, r.isGen == 1));
                        if (!docPaths.Contains(r.docPath, StringComparer.Ordinal)) docPaths.Add(r.docPath);
                        continue;
                    }
                    try
                    {
                        var lineStarts = JsonSerializer.Deserialize<int[]>(r.lineStarts);
                        if (lineStarts is { Length: > 0 } && r.fs.Value >= 0 && r.fe.Value >= r.fs.Value && r.fe.Value <= r.content.Length)
                        {
                            var sIdx = FindLineIndex(lineStarts, r.fs.Value);
                            var eIdx = FindLineIndex(lineStarts, r.fe.Value);
                            var startLine = LineNumbers.ToOneBased(sIdx);
                            var endLine = LineNumbers.ToOneBased(eIdx);
                            var startCol = Utf8Column(r.content, lineStarts[sIdx], r.fs.Value);
                            var endCol = Utf8Column(r.content, lineStarts[eIdx], r.fe.Value);
                            locations.Add(new DeclarationLocation(r.docPath, startLine, startCol, endLine, endCol, r.isGen == 1));
                        }
                        else
                        {
                            locations.Add(new DeclarationLocation(r.docPath, 0, 0, 0, 0, r.isGen == 1));
                        }
                    }
                    catch
                    {
                        locations.Add(new DeclarationLocation(r.docPath, 0, 0, 0, 0, r.isGen == 1));
                    }
                    if (!docPaths.Contains(r.docPath, StringComparer.Ordinal)) docPaths.Add(r.docPath);
                }
                result[sid] = new DeclInfo { IsGenerated = isGeneratedOverall, Locations = locations, DeclarationCount = declCount, DocumentPaths = docPaths };
            }

            // Ensure symbols with no declarations (should not happen for snapshot_symbols but could for synthetic) still have entry
            foreach (var sid in chunk)
            {
                if (!result.ContainsKey(sid))
                    result[sid] = new DeclInfo { IsGenerated = false, Locations = [], DeclarationCount = 0, DocumentPaths = [] };
                else
                {
                    // Fill missing counts for symbols that had no rows due to join mismatch (e.g., no declarations)
                    if (!counts.ContainsKey(sid))
                        result[sid].DeclarationCount = result[sid].Locations.Count;
                }
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

    private bool IsEfPrivateMember(CandidateRow cand, string? accessibility, HashSet<string> mapsToTargets)
    {
        if (cand.Kind is not (nameof(IndexedSymbolKind.Method) or nameof(IndexedSymbolKind.Property) or nameof(IndexedSymbolKind.Field)))
            return false;
        if (accessibility is not ("Private" or "PrivateProtected"))
            return false;
        var enclosing = SymbolId.DeriveContainingTypeSymbolId(cand.SymbolId);
        if (enclosing == null) return false;
        return mapsToTargets.Contains(enclosing);
    }

    private bool IsSerializationConvention(CandidateRow cand, string? accessibility, bool hasSystemTextJson, DeclInfo decl)
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

    private DeadCandidateIncomingSummary BuildIncomingSummary(List<EdgeRecord> incoming)
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

    private EdgeRecord? GetStrongestWeak(List<EdgeRecord> incoming)
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
        var desc = $"Unmodeled construct: a '{edge.Kind}' edge carries 'runtime_unknown' provenance because the construct is listed in DeclaredBoundaries.Known as deliberately not fully modeled. The concrete type was resolved but the runtime activation/registration semantics are not captured. See DeclaredBoundaries.Known for the full, closed list of declared boundaries (10 entries).";
        // For ReflectionTargetUnknown the more specific wording is:
        if (string.Equals(edge.Kind, nameof(EdgeKind.ReflectionTargetUnknown), StringComparison.Ordinal))
            desc = "Unknown reflection target: the runtime target of this reflection call cannot be statically determined.";
        return new DeadCandidateUncertainty([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, desc);
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
