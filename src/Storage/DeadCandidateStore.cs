using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class DeadCandidateStore
{
    private static readonly HashSet<string> StrongProvenance = new(DeadCandidateLiveness.StrongProvenance, StringComparer.Ordinal);

    private static readonly HashSet<string> CandidateKinds = new(DeadCandidateLiveness.CandidateKinds, StringComparer.Ordinal);

    private static readonly HashSet<string> SerializationOptInAttributes = new(StringComparer.Ordinal)
    {
        "global::System.Text.Json.Serialization.JsonIncludeAttribute",
        "global::Newtonsoft.Json.JsonPropertyAttribute",
        "global::System.Runtime.Serialization.DataMemberAttribute"
    };

    private readonly SqliteConnection _connection;

    public DeadCandidateStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>
    ///     The declaration query of <see cref="FetchDeclarationInfo" />. One owner: the
    ///     plan tests pin this text, so a change here is what they see.
    /// </summary>
    internal static string DeclarationSql(int idCount)
    {
        var paramNames = Enumerable.Range(0, idCount).Select(static i => $"@p{i}");
        return $"""
            SELECT d.symbol_id, d.document_version_id, d.full_start, d.full_end, COALESCE(d.is_generated,0), d.is_partial
            FROM declarations d
            CROSS JOIN snapshot_documents sd
            WHERE sd.snapshot_id = @snapshotId
              AND sd.document_version_id = d.document_version_id
              AND d.symbol_id IN ({string.Join(",", paramNames)});
            """;
    }

    /// <summary>
    ///     The incoming-edge query of <see cref="FetchIncomingEdgesBatched" />, for one
    ///     chunk of <paramref name="idCount" /> symbol ids and one edge-kind list.
    /// </summary>
    internal static string IncomingEdgesSql(IReadOnlyList<string> kinds, int idCount)
    {
        var paramNames = Enumerable.Range(0, idCount).Select(static i => $"@p{i}");
        var kindList = string.Join(",", kinds.Select(k => $"'{k}'"));
        return $"""
            SELECT edge_id, source_symbol_id, target_symbol_id, kind, provenance, snapshot_id, extractor_version, source_document_path, source_start_line, source_start_column, source_end_line, source_end_column, is_cross_generated, type_arguments_json, receiver_type_constraints_json
            FROM edges
            WHERE snapshot_id = @snapshotId
              AND target_symbol_id IN ({string.Join(",", paramNames)})
              AND kind IN ({kindList});
            """;
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
        var projectFacts = ProjectFacts.Load(_connection, snapshotId);
        var incompletenessByDocument = BuildUnobservableByDocument(bindingRecords);
        var incompletenessProjects = BuildUnobservableProjects(bindingRecords, projectFacts);
        // Pre-fetch declarations for all candidates in batches
        var declInfo = FetchDeclarationInfo(snapshotId, allCandidates.Select(c => c.SymbolId).ToList(), includeGenerated);

        // Apply candidate-universe filters (kind, project, document, generated, tests) in-memory.
        // Metadata is parsed once per candidate row into CandidateFacts and reused by the filter
        // loop and the evaluation loop (R5.1: one parse per candidate). B34 keeps every parsed
        // fact by id, so an accessor can read its associated symbol's facts and a type can read
        // its members' facts even when those symbols are filtered out of this page.
        var factsById = new Dictionary<string, CandidateFacts>(StringComparer.Ordinal);
        var filteredCandidates = new List<CandidateFacts>();
        foreach (var c in allCandidates)
        {
            if (!CandidateKinds.Contains(c.Kind))
                continue;

            var facts = BuildFacts(c);
            factsById[facts.SymbolId] = facts;

            if (!string.IsNullOrEmpty(kind) && !string.Equals(c.Kind, kind, StringComparison.OrdinalIgnoreCase))
                continue;

            // Universe exclusions (design decision 4): no user can call these, or the compiler
            // calls them. They are skipped like is_extension_block and never counted.
            if (facts.IsExtensionBlock || facts.IsStaticConstructor || facts.IsImplicitlyDeclared)
                continue;

            var assemblyName = facts.AssemblyName;
            var isTestProject = projectFacts.IsTestProject(assemblyName);
            if (!includeTests && isTestProject)
                continue;

            // project filter: exact match on assembly name
            if (!string.IsNullOrEmpty(project) && !string.Equals(assemblyName, project, StringComparison.Ordinal))
                continue;

            // document filter: requires at least one declaration in that document
            if (!string.IsNullOrEmpty(document))
            {
                if (!declInfo.TryGetValue(c.SymbolId, out var di) || !di.DocumentPaths.Contains(document, StringComparer.Ordinal))
                    continue;
            }

            // is_generated: derived from declInfo
            var isGenerated = declInfo.TryGetValue(c.SymbolId, out var d) && d.IsGenerated;
            if (!includeGenerated && isGenerated)
                continue;

            filteredCandidates.Add(facts);
        }

        // Candidate count is filtered universe before LIVE/suppression
        var candidateCount = filteredCandidates.Count;

        if (filteredCandidates.Count == 0)
            return new DeadCandidatePage([], null, candidateCount, 0, 0, 0);

        // Sort for deterministic keyset
        filteredCandidates.Sort((a, b) => string.Compare(a.SymbolId, b.SymbolId, StringComparison.Ordinal));

        var hasTypeCandidate = filteredCandidates.Any(static f => f.Kind == nameof(IndexedSymbolKind.Type));

        // B24: an accessor inherits its property's or event's edges by role, so the associated
        // symbol's live edges must be fetched alongside the candidate's own.
        var fetchIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in filteredCandidates)
        {
            fetchIds.Add(f.SymbolId);
            if (f.AssociatedSymbolId != null)
                fetchIds.Add(f.AssociatedSymbolId);
        }

        // B25: a type inherits from its descendants. Descendants are found through each member's
        // derived containing type and through the type-to-nested-type Contains edges.
        var membersByContainingType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var containsChildren = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (hasTypeCandidate)
        {
            foreach (var c in allCandidates)
            {
                var enclosing = SymbolId.DeriveContainingTypeSymbolId(c.SymbolId);
                if (enclosing != null)
                {
                    if (!membersByContainingType.TryGetValue(enclosing, out var members))
                    {
                        members = [];
                        membersByContainingType[enclosing] = members;
                    }
                    members.Add(c.SymbolId);
                }

                if (CandidateKinds.Contains(c.Kind))
                    fetchIds.Add(c.SymbolId);
            }
            FetchContainsChildren(snapshotId, containsChildren);
        }

        // Batched LIVE incoming edges for fetchIds
        var incomingByTarget = FetchIncomingEdgesBatched(snapshotId, fetchIds.ToList(), DeadCandidateLiveness.LiveEdgeKinds);

        // B25: type-use edges are not LIVE kinds, so they are one extra batched query for the
        // Type candidates only.
        var typeUseByTarget = new Dictionary<string, List<EdgeRecord>>(StringComparer.Ordinal);
        var subtreeByType = new Dictionary<string, TypeSubtree>(StringComparer.Ordinal);
        if (hasTypeCandidate)
        {
            var typeIds = filteredCandidates
                .Where(static f => f.Kind == nameof(IndexedSymbolKind.Type))
                .Select(static f => f.SymbolId)
                .ToList();
            typeUseByTarget = FetchIncomingEdgesBatched(snapshotId, typeIds, DeadCandidateLiveness.TypeUseEdgeKinds);
            foreach (var typeId in typeIds)
                subtreeByType[typeId] = CollectSubtree(typeId, membersByContainingType, containsChildren);
        }

        // Evaluate each candidate to status/reason
        var evaluated = new List<DeadCandidateEntry>();
        var deadCount = 0;
        var uncertainCount = 0;
        var unresolvedCount = 0;

        foreach (var cand in filteredCandidates)
        {
            var decl = declInfo.TryGetValue(cand.SymbolId, out var d) ? d : new DeclInfo { IsGenerated = false, Locations = [], DeclarationCount = 0, DocumentPaths = [] };
            var docPaths = decl.DocumentPaths;
            var accessibility = cand.Accessibility;
            var assemblyName = cand.AssemblyName;
            var isTest = projectFacts.IsTestProject(assemblyName);

            var incoming = BuildEffectiveIncoming(cand, incomingByTarget, typeUseByTarget, subtreeByType);
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
                uncertainties = [MakeBindingIncompletenessUncertainty(cand.SymbolId, bindingRecords, projectFacts, docPaths, assemblyName)];
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
                uncertainties = [MakeEntryPointConventionUncertainty(cand.SymbolId, cand.Kind == nameof(IndexedSymbolKind.Type))];
                uncertainCount++;
            }
            else if (cand.ImplementsExternalInterface)
            {
                // The runtime or a framework calls an explicitly implemented interface member
                // through the interface, so the index cannot observe the call (B26).
                status = DeadCandidateStatus.UncertainDead;
                reason = DeadCandidateReason.ExternalInterfaceImplementation;
                uncertainties = [MakeExternalInterfaceImplementationUncertainty(cand.SymbolId)];
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
                // No LIVE incoming at all - check the conventions before proved
                if (TryConventionVerdict(cand, factsById, subtreeByType, mapsToTargets, out var conventionReason, out var conventionSymbolId))
                {
                    // The candidate's own EF or serialization verdict comes first. Then (B34) an
                    // accessor inherits its associated symbol's verdict, and a type inherits the
                    // first verdict among its descendant members.
                    status = DeadCandidateStatus.UncertainDead;
                    reason = conventionReason;
                    uncertainties = [MakeConventionUncertainty(conventionReason, conventionSymbolId)];
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
            var documentPath = decl.DocumentPaths.Count > 0 ? decl.DocumentPaths[0] : null;
            // Find best declaration location for start line? Use first location's start
            var entry = new DeadCandidateEntry(
                cand.SymbolId,
                cand.Row.Fqn,
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

    private static HashSet<string> BuildUnobservableProjects(List<BindingIncompletenessRecord> records, ProjectFacts projectFacts)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
            if (r.DocumentPath == null && IsUnobservableReason(r.Reason))
                set.Add(projectFacts.ToAssemblyName(r.ProjectName));
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

    private void FetchContainsChildren(string snapshotId, Dictionary<string, List<string>> result)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT source_symbol_id, target_symbol_id FROM edges WHERE snapshot_id = @snapshotId AND kind = @kind;";
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("@kind", nameof(EdgeKind.Contains));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var source = reader.GetString(0);
            var target = reader.GetString(1);
            if (!result.TryGetValue(source, out var children))
            {
                children = [];
                result[source] = children;
            }
            children.Add(target);
        }
    }

    private Dictionary<string, List<EdgeRecord>> FetchIncomingEdgesBatched(string snapshotId, List<string> symbolIds, IReadOnlyList<string> edgeKinds)
    {
        var result = new Dictionary<string, List<EdgeRecord>>(StringComparer.Ordinal);
        if (symbolIds.Count == 0) return result;
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
            cmd.CommandText = IncomingEdgesSql(edgeKinds, chunk.Count);
            cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            for (var idx = 0; idx < chunk.Count; idx++)
                cmd.Parameters.AddWithValue(paramNames[idx], chunk[idx]);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var edgeId = reader.GetInt64(0);
                var rec = EdgeRecordReader.Read(reader, 1);
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
            cmd.CommandText = DeclarationSql(chunk.Count);
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
                    var lineStarts = SourceLineMap.ParseLineStarts(lineStartsJson, docVersionId);
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
                var locatedRows = new List<(string DocPath, string DocVersionId, int? FullStart, int? FullEnd, int[]? LineStarts, byte[]? Content, int IsGenerated)>();
                foreach (var r in rows)
                {
                    if (documentCache.TryGetValue(r.DocVersionId, out var doc))
                        locatedRows.Add((doc.DocPath, r.DocVersionId, r.FullStart, r.FullEnd, doc.LineStarts, doc.Content, r.IsGenerated));
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
                    var location = SourceLineMap.MapDeclaration(
                        r.DocPath, r.FullStart, r.FullEnd, r.LineStarts, r.Content, r.IsGenerated == 1, r.DocVersionId, sid);
                    if (location != null)
                        locations.Add(location);
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

    private static CandidateFacts BuildFacts(CandidateRow row)
    {
        // One metadata parse per candidate row (R5.1). A row that does not parse still fails
        // loudly and names the offending symbol (SymbolMetadata.Parse).
        var metadata = SymbolMetadata.Parse(row.MetadataJson, row.SymbolId);
        var accessorKind = GetString(metadata, SymbolMetadataKeys.AccessorKind);
        var associatedSymbol = GetString(metadata, SymbolMetadataKeys.AssociatedSymbol);
        return new CandidateFacts(
            row,
            metadata,
            GetString(metadata, SymbolMetadataKeys.Accessibility),
            MetadataReferenceIdentity.SimpleName(row.AssemblyIdentity),
            IsTrue(metadata, SymbolMetadataKeys.IsExtensionBlock),
            IsTrue(metadata, SymbolMetadataKeys.IsStaticConstructor),
            IsTrue(metadata, SymbolMetadataKeys.IsImplicitlyDeclared),
            IsTrue(metadata, SymbolMetadataKeys.ImplementsExternalInterface),
            accessorKind,
            accessorKind != null && associatedSymbol != null
                ? SymbolId.Compose(associatedSymbol, row.AssemblyIdentity)
                : null,
            IsTrue(metadata, SymbolMetadataKeys.ContainsEntryPoint),
            IsTrue(metadata, SymbolMetadataKeys.IsEntryPoint));
    }

    private static bool IsTrue(JsonElement? metadata, string key)
    {
        return metadata is not null
            && metadata.Value.TryGetProperty(key, out var el)
            && el.ValueKind == JsonValueKind.True;
    }

    private static string? GetString(JsonElement? metadata, string key)
    {
        if (metadata is null) return null;
        if (metadata.Value.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    /// <summary>
    ///     The symbols inside a Type candidate's subtree: the type itself, its members and the
    ///     members of its nested types (through the type-to-nested-type Contains edges). Members
    ///     are found by their derived containing type because a nested type's doc-comment id
    ///     alone cannot tell a namespace from nesting (B25).
    /// </summary>
    private static TypeSubtree CollectSubtree(
        string typeSymbolId,
        Dictionary<string, List<string>> membersByContainingType,
        Dictionary<string, List<string>> containsChildren)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal) { typeSymbolId };
        var descendants = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(typeSymbolId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (membersByContainingType.TryGetValue(current, out var members))
            {
                foreach (var member in members)
                    if (symbols.Add(member))
                        descendants.Add(member);
            }
            if (containsChildren.TryGetValue(current, out var nested))
            {
                foreach (var child in nested)
                {
                    if (symbols.Add(child))
                    {
                        descendants.Add(child);
                        queue.Enqueue(child);
                    }
                }
            }
        }
        descendants.Sort(StringComparer.Ordinal);
        return new TypeSubtree(symbols, descendants);
    }

    /// <summary>
    ///     The effective incoming LIVE list for a candidate: its own live edges plus the edges
    ///     it inherits under design decision 1 (an accessor from its property or event by role;
    ///     a type from its descendants and from the type-use edges that enter it from outside its
    ///     subtree). The ladder and the summary run on this list (R5.7).
    /// </summary>
    private static List<EdgeRecord> BuildEffectiveIncoming(
        CandidateFacts cand,
        Dictionary<string, List<EdgeRecord>> incomingByTarget,
        Dictionary<string, List<EdgeRecord>> typeUseByTarget,
        Dictionary<string, TypeSubtree> subtreeByType)
    {
        var effective = new List<EdgeRecord>();
        if (incomingByTarget.TryGetValue(cand.SymbolId, out var own))
            effective.AddRange(own);

        // B24: an accessor inherits its property's or event's edges by role.
        if (cand.AccessorKind != null && cand.AssociatedSymbolId != null)
        {
            if (incomingByTarget.TryGetValue(cand.AssociatedSymbolId, out var associated))
            {
                foreach (var edge in associated)
                    if (InheritsForAccessor(cand.AccessorKind, edge.Kind))
                        effective.Add(edge);
            }
        }

        // B25: a type inherits its descendants' live edges and the type-use edges that enter it
        // from outside its own subtree. Self edges are never inherited.
        if (cand.Kind == nameof(IndexedSymbolKind.Type) && subtreeByType.TryGetValue(cand.SymbolId, out var subtree))
        {
            foreach (var descendant in subtree.Descendants)
            {
                if (!incomingByTarget.TryGetValue(descendant, out var descendantEdges)) continue;
                foreach (var edge in descendantEdges)
                    if (!subtree.Symbols.Contains(edge.SourceSymbolId))
                        effective.Add(edge);
            }

            if (typeUseByTarget.TryGetValue(cand.SymbolId, out var typeUse))
            {
                foreach (var edge in typeUse)
                    if (!subtree.Symbols.Contains(edge.SourceSymbolId))
                        effective.Add(edge);
            }
        }

        return effective;
    }

    private static bool InheritsForAccessor(string accessorKind, string edgeKind)
    {
        return accessorKind switch
        {
            "get" => edgeKind != nameof(EdgeKind.Writes),
            "set" or "init" => edgeKind == nameof(EdgeKind.Writes),
            "add" or "remove" or "raise" => true,
            _ => false
        };
    }

    private static bool IsPublicOrProtected(string? accessibility)
    {
        return accessibility is "Public" or "Protected" or "ProtectedOrInternal";
    }

    /// <summary>
    ///     True when this candidate is the compilation's own process entry point: an entry-point
    ///     method (an explicit <c>static void Main</c> or the compiler-synthesized
    ///     top-level-statements form) tagged via <c>SymbolMetadataKeys.IsEntryPoint</c>, or the
    ///     type that contains it, tagged via <c>SymbolMetadataKeys.ContainsEntryPoint</c> (F13).
    ///     Nothing in-repo ever calls the entry point — the runtime launcher does — so it would
    ///     otherwise land in the terminal no-incoming-edges branch below and read as proved_dead.
    /// </summary>
    private static bool IsProcessEntryPoint(CandidateFacts cand)
    {
        if (cand.Kind == nameof(IndexedSymbolKind.Method))
            return cand.IsEntryPointMethod;
        if (cand.Kind == nameof(IndexedSymbolKind.Type))
            return cand.ContainsEntryPoint;
        return false;
    }

    private static bool IsEfPrivateMember(CandidateFacts cand, HashSet<string> mapsToTargets)
    {
        if (cand.Kind is not (nameof(IndexedSymbolKind.Method) or nameof(IndexedSymbolKind.Property) or nameof(IndexedSymbolKind.Field)))
            return false;
        if (cand.Accessibility is not ("Private" or "PrivateProtected"))
            return false;
        var enclosing = SymbolId.DeriveContainingTypeSymbolId(cand.SymbolId);
        if (enclosing == null) return false;
        return mapsToTargets.Contains(enclosing);
    }

    private static bool IsSerializationConvention(CandidateFacts cand)
    {
        if (cand.Kind is not (nameof(IndexedSymbolKind.Property) or nameof(IndexedSymbolKind.Field)))
            return false;
        // The member is non-public (public and protected members never reach this branch), so a
        // serializer sees it only when an opt-in attribute names it. Exact stored names only:
        // the attribute formatter writes fully qualified names such as
        // "global::System.Runtime.Serialization.DataMemberAttribute".
        var metadata = cand.Metadata;
        if (metadata is null) return false;
        if (metadata.Value.TryGetProperty(SymbolMetadataKeys.Attributes, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) continue;
                var s = el.GetString();
                if (s != null && SerializationOptInAttributes.Contains(s))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    ///     B34: the convention verdict for a candidate that inherits it from another symbol. An
    ///     accessor inherits its associated property's or event's verdict; a type inherits the
    ///     first verdict among its descendant members (EF before serialization, in the subtree's
    ///     ordinal member order). Returns the reason and the symbol id the uncertainty is made
    ///     with.
    /// </summary>
    private static bool TryInheritedConvention(
        CandidateFacts cand,
        Dictionary<string, CandidateFacts> factsById,
        Dictionary<string, TypeSubtree> subtreeByType,
        HashSet<string> mapsToTargets,
        out string reason,
        out string symbolId)
    {
        if (cand.AssociatedSymbolId != null)
        {
            if (factsById.TryGetValue(cand.AssociatedSymbolId, out var associated) &&
                TryConvention(associated, mapsToTargets, out reason))
            {
                symbolId = cand.AssociatedSymbolId;
                return true;
            }
        }

        if (cand.Kind == nameof(IndexedSymbolKind.Type) &&
            subtreeByType.TryGetValue(cand.SymbolId, out var subtree))
        {
            foreach (var memberId in subtree.Descendants)
            {
                if (factsById.TryGetValue(memberId, out var member) &&
                    TryConvention(member, mapsToTargets, out reason))
                {
                    symbolId = memberId;
                    return true;
                }
            }
        }

        reason = string.Empty;
        symbolId = string.Empty;
        return false;
    }

    /// <summary>
    ///     The candidate's own convention verdict first, then the inherited one (B34).
    /// </summary>
    private static bool TryConventionVerdict(
        CandidateFacts cand,
        Dictionary<string, CandidateFacts> factsById,
        Dictionary<string, TypeSubtree> subtreeByType,
        HashSet<string> mapsToTargets,
        out string reason,
        out string symbolId)
    {
        if (TryConvention(cand, mapsToTargets, out reason))
        {
            symbolId = cand.SymbolId;
            return true;
        }

        return TryInheritedConvention(cand, factsById, subtreeByType, mapsToTargets, out reason, out symbolId);
    }

    private static bool TryConvention(CandidateFacts cand, HashSet<string> mapsToTargets, out string reason)
    {
        if (IsEfPrivateMember(cand, mapsToTargets))
        {
            reason = DeadCandidateReason.EfConvention;
            return true;
        }

        if (IsSerializationConvention(cand))
        {
            reason = DeadCandidateReason.SerializationConvention;
            return true;
        }

        reason = string.Empty;
        return false;
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

    private static DeadCandidateUncertainty MakeBindingIncompletenessUncertainty(string symbolId, List<BindingIncompletenessRecord> all, ProjectFacts projectFacts, List<string> docPaths, string assemblyName)
    {
        // Find relevant binding records that overlap this candidate's docs. Restricted
        // to IsUnobservableReason so the description names the reason that actually
        // triggered OverlapsBindingIncompleteness — otherwise a co-located but
        // non-triggering record (e.g. filtered_external, which never makes a
        // candidate unresolved) could win the reason pick and describe the wrong cause.
        // A binding record carries the Roslyn project name, the candidate carries the
        // assembly name, so the project-level comparison maps one to the other (B18).
        var relevant = all.Where(r => IsUnobservableReason(r.Reason)
                                   && (r.DocumentPath != null && docPaths.Contains(r.DocumentPath, StringComparer.Ordinal)
                                   || r.DocumentPath == null && string.Equals(projectFacts.ToAssemblyName(r.ProjectName), assemblyName, StringComparison.Ordinal))).ToList();
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

    private static DeadCandidateUncertainty MakeEntryPointConventionUncertainty(string symbolId, bool isType)
    {
        var description = isType
            ? "Process entry point: this is the type that contains the compilation's Main method (explicit or the compiler-synthesized top-level-statements form), invoked by the runtime launcher rather than from within the indexed call graph. It has no incoming edge by definition; that is not evidence of dead code."
            : "Process entry point: this is the compilation's Main method (explicit or the compiler-synthesized top-level-statements form), invoked by the runtime launcher rather than from within the indexed call graph. It has no incoming edge by definition; that is not evidence of dead code.";
        return new DeadCandidateUncertainty([symbolId], "entry_point_convention", description);
    }

    private static DeadCandidateUncertainty MakeExternalInterfaceImplementationUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "external_interface_implementation", "Explicit implementation of an interface member declared outside the compilation: the runtime or a framework calls it through the interface, so the index cannot observe the call.");
    }

    private static DeadCandidateUncertainty MakeEfConventionUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "ef_convention", "EF Core model conventions beyond query filters and indexes are not modeled: the containing type is an EF-mapped entity (MapsTo target) and the member is materialized via reflection/private-setter with no Writes edge. See DeclaredBoundaries.ef_convention.", "ef_convention");
    }

    private static DeadCandidateUncertainty MakeSerializationConventionUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "serialization_convention", "Serialization opt-in: the member is not public, has no LIVE incoming edge, and carries a serializer opt-in attribute (JsonInclude, Newtonsoft JsonProperty or DataMember), so a serializer may read or write it by reflection. Verify no serialization contract depends on it before removing.");
    }

    private static DeadCandidateUncertainty MakeConventionUncertainty(string reason, string symbolId)
    {
        return reason == DeadCandidateReason.EfConvention
            ? MakeEfConventionUncertainty(symbolId)
            : MakeSerializationConventionUncertainty(symbolId);
    }

    private sealed record CandidateRow(string SymbolId, string Kind, string? Fqn, string? MetadataJson, string DocCommentId, string AssemblyIdentity);

    /// <summary>
    ///     One metadata parse per candidate row (R5.1). Every filter and ladder decision reads
    ///     these parsed fields instead of re-parsing <see cref="CandidateRow.MetadataJson" />.
    /// </summary>
    private sealed record CandidateFacts(
        CandidateRow Row,
        JsonElement? Metadata,
        string? Accessibility,
        string AssemblyName,
        bool IsExtensionBlock,
        bool IsStaticConstructor,
        bool IsImplicitlyDeclared,
        bool ImplementsExternalInterface,
        string? AccessorKind,
        string? AssociatedSymbolId,
        bool ContainsEntryPoint,
        bool IsEntryPointMethod)
    {
        public string SymbolId => Row.SymbolId;
        public string Kind => Row.Kind;
    }

    private sealed record TypeSubtree(HashSet<string> Symbols, List<string> Descendants);

    private sealed class DeclInfo
    {
        public bool IsGenerated { get; set; }
        public List<DeclarationLocation> Locations { get; set; } = [];
        public int DeclarationCount { get; set; }
        public List<string> DocumentPaths { get; set; } = [];
    }
}
