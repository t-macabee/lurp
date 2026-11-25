using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class DeadCandidateStore
{
    private static readonly HashSet<string> StrongProvenance = new(DeadCandidateLiveness.StrongProvenance, StringComparer.Ordinal);

    private static readonly HashSet<string> CandidateKinds = new(DeadCandidateLiveness.CandidateKinds, StringComparer.Ordinal);

    private readonly SqliteConnection _connection;

    public DeadCandidateStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>
    ///     The declaration query of <see cref="FetchDeclarationInfo" />. One owner: the
    ///     plan tests pin this text, so a change here is what they see.
    /// </summary>
    internal static string DeclarationSql() => $"""
        SELECT d.symbol_id, d.document_version_id, d.full_start, d.full_end, COALESCE(d.is_generated,0), d.is_partial
        FROM declarations d
        CROSS {SnapshotDeclarationLink.Join("d", "sdecl")}
        WHERE d.symbol_id IN (SELECT value FROM json_each(@symbolIds));
        """;

    /// <summary>
    ///     The incoming-edge query of <see cref="FetchIncomingEdgesBatched" />, for one
    ///     edge-kind list and one JSON array of symbol ids.
    /// </summary>
    internal static string IncomingEdgesSql(IReadOnlyList<string> kinds)
    {
        var kindList = string.Join(",", kinds.Select(k => $"'{k}'"));
        return $"""
            SELECT edge_id, source_symbol_id, target_symbol_id, kind, provenance, snapshot_id, extractor_version, source_document_path, source_start_line, source_start_column, source_end_line, source_end_column, is_cross_generated, type_arguments_json, receiver_type_constraints_json
            FROM edges
            WHERE snapshot_id = @snapshotId
              AND target_symbol_id IN (SELECT value FROM json_each(@symbolIds))
              AND kind IN ({kindList});
            """;
    }

    /// <summary>
    ///     The convention assembly-scan edge query of <see cref="FetchAssemblyScanOwners" />.
    ///     Uses a binary range seek (<c>&gt;=</c> / <c>&lt;</c>) instead of <c>LIKE @prefix</c>
    ///     so SQLite probes <c>idx_edges_snapshot_target</c> on both columns (under default
    ///     case-insensitive LIKE rules, a bound LIKE parameter cannot use a BINARY index and
    ///     treats <c>_</c> as a wildcard).
    /// </summary>
    internal static string AssemblyScanOwnersSql() => """
        SELECT source_symbol_id, target_symbol_id
        FROM edges
        WHERE snapshot_id = @snapshotId
          AND target_symbol_id >= @prefixStart
          AND target_symbol_id < @prefixEnd;
        """;

    internal static (string Start, string End) AssemblyScanPrefixRange()
    {
        const string prefix = GraphNodeIds.AssemblyScanConventionPrefix;
        var lastChar = prefix[^1];
        if (lastChar >= '\x7F')
            throw new InvalidOperationException("AssemblyScanConventionPrefix must end with an ASCII character below 0x7F.");
        return (prefix, prefix[..^1] + (char)(lastChar + 1));
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var fingerprint = DeadCandidateCursor.ComputeFingerprint(project, document, kind, includePublic, includeGenerated, includeTests);
        if (cursor != null)
        {
            cursor.Validate(snapshotId, fingerprint);
        }

        // Fetch base candidate rows for snapshot
        var allCandidates = FetchCandidateRows(snapshotId);

        // Fetch auxiliary persisted facts once
        var conventions = FetchConventionFacts(snapshotId);
        var assemblyScanOwners = FetchAssemblyScanOwners(snapshotId);
        var bindingRecords = FetchBindingIncompleteness(snapshotId);
        var projectFacts = ProjectFacts.Load(_connection, snapshotId);
        var incompletenessByDocument = BuildUnobservableByDocument(bindingRecords);
        var incompletenessProjects = BuildUnobservableProjects(bindingRecords, projectFacts);
        // Pre-fetch declarations for all candidates in batches
        var candidateIds = allCandidates.Where(c => CandidateKinds.Contains(c.Kind)).Select(c => c.SymbolId).ToList();
        var declInfo = FetchDeclarationInfo(snapshotId, candidateIds, out var coDeclaredPeers);

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

            var facts = BuildFacts(c, projectFacts);
            factsById[facts.SymbolId] = facts;

            if (!string.IsNullOrEmpty(kind) && !string.Equals(c.Kind, kind, StringComparison.OrdinalIgnoreCase))
                continue;

            // Universe exclusions (design decision 4): no user can call these, or the compiler
            // calls them. They are skipped like is_extension_block and never counted.
            if (facts.IsExtensionBlock || facts.IsStaticConstructor || facts.IsImplicitlyDeclared)
                continue;

            var assemblyName = facts.AssemblyName;
            if (!includeTests && facts.IsTest)
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
            AddIncomingTargets(f);
            if (coDeclaredPeers.TryGetValue(f.SymbolId, out var peers))
            {
                foreach (var peerId in peers)
                    AddIncomingTargets(factsById[peerId]);
            }
        }

        void AddIncomingTargets(CandidateFacts facts)
        {
            fetchIds.Add(facts.SymbolId);
            if (facts.AssociatedSymbolId != null)
                fetchIds.Add(facts.AssociatedSymbolId);
        }

        // D145: map associated symbol (property/event) -> its accessor ids once per call, so a
        // property/event ignores edges from its own accessors and an accessor ignores edges from
        // its associated symbol and sibling accessors.
        var accessorsByAssociatedSymbol = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in factsById.Values)
        {
            if (f.AssociatedSymbolId != null)
            {
                if (!accessorsByAssociatedSymbol.TryGetValue(f.AssociatedSymbolId, out var accessors))
                {
                    accessors = [];
                    accessorsByAssociatedSymbol[f.AssociatedSymbolId] = accessors;
                }
                accessors.Add(f.SymbolId);
            }
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
            var typeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in filteredCandidates)
            {
                if (candidate.Kind != nameof(IndexedSymbolKind.Type))
                    continue;
                typeIds.Add(candidate.SymbolId);
                if (coDeclaredPeers.TryGetValue(candidate.SymbolId, out var peers))
                    typeIds.UnionWith(peers);
            }
            typeUseByTarget = FetchIncomingEdgesBatched(snapshotId, typeIds.ToList(), DeadCandidateLiveness.TypeUseEdgeKinds);
            foreach (var typeId in typeIds)
                subtreeByType[typeId] = CollectSubtree(typeId, membersByContainingType, containsChildren);
        }

        // Evaluate each candidate to status/reason
        var evaluated = new List<DeadCandidateEntry>();

        foreach (var cand in filteredCandidates)
        {
            var decl = declInfo.TryGetValue(cand.SymbolId, out var d) ? d : new DeclInfo { IsGenerated = false, Locations = [], DeclarationCount = 0, DocumentPaths = [] };
            var assemblyName = cand.AssemblyName;

            var incoming = BuildEffectiveIncoming(cand, incomingByTarget, typeUseByTarget, subtreeByType, accessorsByAssociatedSymbol);
            if (coDeclaredPeers.TryGetValue(cand.SymbolId, out var peers))
            {
                // Apply each compilation's own self-source and accessor-role filters before merging.
                foreach (var peerId in peers)
                    incoming.AddRange(BuildEffectiveIncoming(factsById[peerId], incomingByTarget, typeUseByTarget,
                        subtreeByType, accessorsByAssociatedSymbol));
                incoming = incoming.Distinct().ToList();
            }
            var hasStrong = incoming.Any(e => StrongProvenance.Contains(e.Provenance));
            if (hasStrong)
                continue;

            // Prepare incoming summary
            var summary = BuildIncomingSummary(incoming);

            // Determine if overlaps binding incompleteness (unobservable)
            var overlapsBinding = OverlapsBindingIncompleteness(decl.DocumentPaths, assemblyName, incompletenessByDocument, incompletenessProjects);

            var verdict = ClassifyCandidate(
                cand, decl, incoming, includePublic, includeGenerated, includeTests,
                overlapsBinding, bindingRecords, projectFacts,
                factsById, subtreeByType, conventions, assemblyScanOwners);
            if (verdict is null)
                continue;

            var documentPath = decl.DocumentPaths.Count > 0 ? decl.DocumentPaths[0] : null;
            var entry = new DeadCandidateEntry(
                cand.SymbolId,
                cand.Row.Fqn,
                cand.Kind,
                cand.Accessibility,
                cand.EffectiveAccessibility,
                documentPath,
                decl.Locations,
                assemblyName,
                decl.DeclarationCount,
                decl.IsGenerated,
                verdict.Status,
                verdict.Reason,
                verdict.Uncertainties,
                summary);
            evaluated.Add(entry);
        }

        var deadCount = evaluated.Count(e => e.Status == DeadCandidateStatus.ProvedDead);
        var uncertainCount = evaluated.Count(e => e.Status == DeadCandidateStatus.UncertainDead || e.Status == DeadCandidateStatus.Uncertain);
        var unresolvedCount = evaluated.Count(e => e.Status == DeadCandidateStatus.Unresolved);

        // The cursor is keyset over dead symbol ids, not over the candidate universe.
        IEnumerable<DeadCandidateEntry> windowed = evaluated;
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

    internal sealed record CandidateVerdict(string Status, string Reason, List<DeadCandidateUncertainty> Uncertainties);

    internal static CandidateVerdict? ClassifyCandidate(
        CandidateFacts cand,
        DeclInfo decl,
        List<EdgeRecord> incoming,
        bool includePublic,
        bool includeGenerated,
        bool includeTests,
        bool overlapsBinding,
        List<BindingIncompletenessRecord> bindingRecords,
        ProjectFacts projectFacts,
        Dictionary<string, CandidateFacts> factsById,
        Dictionary<string, TypeSubtree> subtreeByType,
        ConventionFacts conventions,
        Dictionary<string, List<string>>? assemblyScanOwners = null)
    {
        string status;
        string reason;
        List<DeadCandidateUncertainty> uncertainties;

        if (overlapsBinding)
        {
            status = DeadCandidateStatus.Unresolved;
            reason = DeadCandidateReason.BindingIncompleteness;
            uncertainties = [MakeBindingIncompletenessUncertainty(cand.SymbolId, bindingRecords, projectFacts, decl.DocumentPaths, cand.AssemblyName)];
        }
        else if (IsInUnmodeledScanScope(cand, factsById, assemblyScanOwners, out var scanOwners))
        {
            status = DeadCandidateStatus.Unresolved;
            reason = DeadCandidateReason.BindingIncompleteness;
            uncertainties = [MakeAssemblyScanUncertainty(cand.SymbolId, cand.AssemblyName, scanOwners)];
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
        }
        else if (cand.ImplementsExternalMember)
        {
            // The runtime or a framework calls an implementation of an external interface member
            // or an override of an external member through that member, so the index cannot observe
            // the call (B26, D48).
            status = DeadCandidateStatus.UncertainDead;
            reason = DeadCandidateReason.ExternalInterfaceImplementation;
            uncertainties = [MakeExternalInterfaceImplementationUncertainty(cand.SymbolId)];
        }
        else if (PublicSurface.IsExposed(cand.Accessibility, cand.EffectiveAccessibility, cand.AssemblyName, projectFacts) == true)
        {
            if (!includePublic)
                return null;

            status = DeadCandidateStatus.UncertainDead;
            reason = DeadCandidateReason.PublicSurface;
            uncertainties = [MakePublicSurfaceUncertainty(cand.SymbolId)];
        }
        else if (decl.IsGenerated && includeGenerated)
        {
            status = DeadCandidateStatus.Uncertain;
            reason = DeadCandidateReason.GeneratedExcluded;
            uncertainties = [MakeGeneratedUncertainty(cand.SymbolId)];
        }
        else if (cand.IsTest && includeTests)
        {
            status = DeadCandidateStatus.Uncertain;
            reason = DeadCandidateReason.TestHarness;
            uncertainties = [MakeTestHarnessUncertainty(cand.SymbolId)];
        }
        else if (incoming.Count > 0)
        {
            var best = GetStrongestWeak(incoming);
            switch (best.Provenance)
            {
                case Provenance.Possible:
                    status = DeadCandidateStatus.UncertainDead;
                    reason = DeadCandidateReason.PossibleDispatch;
                    uncertainties = [MakePossibleDispatchUncertainty(best)];
                    break;
                case Provenance.Convention:
                    status = DeadCandidateStatus.UncertainDead;
                    reason = DeadCandidateReason.FrameworkConvention;
                    uncertainties = [MakeFrameworkConventionUncertainty(best)];
                    break;
                case Provenance.NameCandidate:
                    status = DeadCandidateStatus.UncertainDead;
                    reason = DeadCandidateReason.NameCandidate;
                    uncertainties = [MakeNameCandidateUncertainty(best)];
                    break;
                case Provenance.RuntimeUnknown:
                    status = DeadCandidateStatus.Unresolved;
                    reason = DeadCandidateReason.RuntimeUnknown;
                    uncertainties = [MakeRuntimeUnknownUncertainty(best)];
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown weak provenance '{best.Provenance}' on {best.Kind} edge from '{best.SourceSymbolId}' to '{best.TargetSymbolId}'.");
            }
        }
        else
        {
            // B34: the candidate's own convention verdict comes first, then the
            // verdict inherited from its associated symbol or descendants.
            if (TryConventionVerdict(cand, factsById, subtreeByType, conventions, out var conventionReason, out var conventionSymbolId))
            {
                status = DeadCandidateStatus.UncertainDead;
                reason = conventionReason;
                uncertainties = [MakeConventionUncertainty(conventionReason, conventionSymbolId)];
            }
            else if (IsRecordDataMember(cand, factsById))
            {
                status = DeadCandidateStatus.UncertainDead;
                reason = DeadCandidateReason.FrameworkConvention;
                uncertainties = [MakeRecordMemberUncertainty(cand.SymbolId)];
            }
            else
            {
                status = DeadCandidateStatus.ProvedDead;
                reason = DeadCandidateReason.NoIncomingLiveEdges;
                uncertainties = [];
            }
        }

        return new CandidateVerdict(status, reason, uncertainties);
    }

    internal sealed class ConventionFacts
    {
        public HashSet<string> EfSources { get; } = [];
        public HashSet<string> EfDirectTargets { get; } = [];
        public HashSet<string> EfMemberTypes { get; } = [];
        public HashSet<string> SerializationTargets { get; } = [];
        public HashSet<string> OptionsTypes { get; } = [];
        public HashSet<string> UnmodeledMediatRTypes { get; } = [];
        public HashSet<string> FrameworkActivatedTypes { get; } = [];
    }

    private ConventionFacts FetchConventionFacts(string snapshotId)
    {
        var facts = new ConventionFacts();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            WITH RECURSIVE
                convention_edges(kind, source_id, target_id) AS MATERIALIZED (
                    SELECT kind, source_symbol_id, target_symbol_id
                    FROM edges
                    WHERE snapshot_id = @snapshotId AND kind IN (@mapsTo, @registers, @handles, @routesTo)
                ),
                mappings(source_id, target_id) AS MATERIALIZED (
                    SELECT source_id, target_id FROM convention_edges WHERE kind = @mapsTo
                ),
                member_types(symbol_id) AS (
                    SELECT target_id FROM mappings
                    UNION
                    SELECT e.target_symbol_id
                    FROM member_types m
                    JOIN edges e ON e.source_symbol_id = m.symbol_id
                    WHERE e.snapshot_id = @snapshotId AND e.kind = @inherits
                )
            SELECT 'ef_source', source_id FROM mappings
            UNION
            SELECT 'ef_target', target_id FROM mappings
            UNION ALL
            SELECT 'ef_member_type', symbol_id FROM member_types
            UNION ALL
            SELECT kind, symbol_id FROM annotations
            WHERE snapshot_id = @snapshotId AND (
                (kind = 'serialization_contract' AND (value = 'included' OR value LIKE 'name=%'))
                OR (kind = 'options_binding' AND value = 'configured')
                OR kind = 'unmodeled_mediatr_pattern'
            )
            UNION ALL
            SELECT DISTINCT kind, target_id FROM convention_edges WHERE kind <> @mapsTo;
            """;
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("@mapsTo", nameof(EdgeKind.MapsTo));
        cmd.Parameters.AddWithValue("@inherits", nameof(EdgeKind.Inherits));
        cmd.Parameters.AddWithValue("@registers", nameof(EdgeKind.Registers));
        cmd.Parameters.AddWithValue("@handles", nameof(EdgeKind.Handles));
        cmd.Parameters.AddWithValue("@routesTo", nameof(EdgeKind.RoutesTo));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var symbolId = reader.GetString(1);
            switch (reader.GetString(0))
            {
                case "ef_source": facts.EfSources.Add(symbolId); break;
                case "ef_target": facts.EfDirectTargets.Add(symbolId); break;
                case "ef_member_type": facts.EfMemberTypes.Add(symbolId); break;
                case "serialization_contract": facts.SerializationTargets.Add(symbolId); break;
                case "options_binding": facts.OptionsTypes.Add(symbolId); break;
                case "unmodeled_mediatr_pattern": facts.UnmodeledMediatRTypes.Add(symbolId); break;
                case nameof(EdgeKind.Registers) when symbolId.StartsWith("T:", StringComparison.Ordinal):
                    facts.FrameworkActivatedTypes.Add(symbolId);
                    break;
                case nameof(EdgeKind.Handles) or nameof(EdgeKind.RoutesTo):
                    if (SymbolId.DeriveContainingTypeSymbolId(symbolId) is { } typeId)
                        facts.FrameworkActivatedTypes.Add(typeId);
                    break;
            }
        }
        return facts;
    }

    private Dictionary<string, List<string>> FetchAssemblyScanOwners(string snapshotId)
    {
        var (prefixStart, prefixEnd) = AssemblyScanPrefixRange();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = AssemblyScanOwnersSql();
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("@prefixStart", prefixStart);
        cmd.Parameters.AddWithValue("@prefixEnd", prefixEnd);
        var byAssembly = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var source = reader.GetString(0);
            var target = reader.GetString(1);
            if (!target.StartsWith(GraphNodeIds.AssemblyScanConventionPrefix, StringComparison.Ordinal))
                continue;
            var displayName = target[GraphNodeIds.AssemblyScanConventionPrefix.Length..];
            var comma = displayName.IndexOf(',', StringComparison.Ordinal);
            var assemblySimpleName = (comma >= 0 ? displayName[..comma] : displayName).Trim();
            if (assemblySimpleName.Length == 0)
                continue;
            if (!byAssembly.TryGetValue(assemblySimpleName, out var owners))
            {
                owners = new HashSet<string>(StringComparer.Ordinal);
                byAssembly[assemblySimpleName] = owners;
            }
            owners.Add(source);
        }

        return byAssembly.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.OrderBy(static id => id, StringComparer.Ordinal).ToList(),
            StringComparer.Ordinal);
    }

    private static bool IsInUnmodeledScanScope(
        CandidateFacts cand,
        Dictionary<string, CandidateFacts> factsById,
        Dictionary<string, List<string>>? assemblyScanOwners,
        out List<string> owners)
    {
        owners = [];
        if (assemblyScanOwners is null ||
            !assemblyScanOwners.TryGetValue(cand.AssemblyName, out var found) ||
            found.Count == 0)
            return false;

        if (cand.Kind == nameof(IndexedSymbolKind.Type))
        {
            if (!IsAssemblyScanCandidateType(cand))
                return false;
            owners = found;
            return true;
        }

        if (PublicSurface.IsInstanceConstructor(cand.Kind, cand.Row.DocCommentId))
        {
            var enclosing = SymbolId.DeriveContainingTypeSymbolId(cand.SymbolId);
            if (enclosing != null &&
                factsById.TryGetValue(enclosing, out var enclosingFacts) &&
                IsAssemblyScanCandidateType(enclosingFacts))
            {
                owners = found;
                return true;
            }
        }

        return false;
    }

    private static bool IsAssemblyScanCandidateType(CandidateFacts typeFacts)
    {
        if (typeFacts.Kind != nameof(IndexedSymbolKind.Type))
            return false;
        var typeKind = SymbolMetadata.GetString(typeFacts.Metadata, SymbolMetadataKeys.TypeKind);
        if (typeKind is not ("Class" or "Delegate"))
            return false;
        return !SymbolMetadata.IsTrue(typeFacts.Metadata, SymbolMetadataKeys.IsAbstract) &&
               !SymbolMetadata.IsTrue(typeFacts.Metadata, SymbolMetadataKeys.IsStatic);
    }

    private static DeadCandidateUncertainty MakeAssemblyScanUncertainty(string candidateSymbolId, string assemblyName, List<string> scanOwners)
    {
        var symbolIds = new HashSet<string>(scanOwners, StringComparer.Ordinal) { candidateSymbolId }
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToList();
        return new DeadCandidateUncertainty(
            symbolIds,
            "binding_incompleteness",
            $"An unmodeled dependency injection assembly scan of {assemblyName} may register this type. The index cannot resolve its registrations.");
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
            if (BindingIncompletenessReason.UnobservableReasons.Contains(r.Reason))
                set.Add(r.DocumentPath);
        }
        return set;
    }

    private static HashSet<string> BuildUnobservableProjects(List<BindingIncompletenessRecord> records, ProjectFacts projectFacts)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
            if (r.DocumentPath == null && BindingIncompletenessReason.UnobservableReasons.Contains(r.Reason))
                set.Add(projectFacts.ToAssemblyName(r.ProjectName));
        return set;
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
        // edge_id is selected so each target's list can be ordered by it in C#. The old
        // ORDER BY edge_id forced the planner onto idx_edges_snapshot_id (whose implicit
        // rowid ordering satisfies the sort) and scanned the full snapshot edge set; without
        // it the planner probes idx_edges_snapshot_target per target id.
        // Order still matters: GetStrongestWeak resolves equal-rank ties by first occurrence.
        var byTarget = new Dictionary<string, List<(long EdgeId, EdgeRecord Record)>>(StringComparer.Ordinal);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = IncomingEdgesSql(edgeKinds);
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        SqlIdList.Bind(cmd, "@symbolIds", symbolIds);
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
        foreach (var (target, list) in byTarget)
        {
            list.Sort(static (a, b) => a.EdgeId.CompareTo(b.EdgeId));
            result[target] = [.. list.Select(static entry => entry.Record)];
        }
        return result;
    }

    private Dictionary<string, DeclInfo> FetchDeclarationInfo(string snapshotId, List<string> symbolIds,
        out Dictionary<string, HashSet<string>> coDeclaredPeers)
    {
        coDeclaredPeers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var result = new Dictionary<string, DeclInfo>(StringComparer.Ordinal);
        if (symbolIds.Count == 0) return result;
        // document_version_id -> (relative_path, line_starts, content), filled once per distinct
        // document by the document query below.
        var documentCache = new Dictionary<string, (string DocPath, int[]? LineStarts, byte[]? Content)>(StringComparer.Ordinal);
        // Declarations only, driven from the symbol ids. CROSS JOIN pins declarations as the
        // outer table so that with no sqlite_stat1 rows the planner cannot start from
        // snapshot_declarations and probe the IN list once per link row.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = DeclarationSql();
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        SqlIdList.Bind(cmd, "@symbolIds", symbolIds);

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
                if (pendingDocIdSet.Add(docVersionId))
                    pendingDocIds.Add(docVersionId);
                if (!perSymbol.TryGetValue(sid, out var lst))
                {
                    lst = [];
                    perSymbol[sid] = lst;
                }
                lst.Add((docVersionId, fs, fe, isGen));
            }
        }

        // Match physical spans and symbol identity within the document. Distinct symbols can
        // share a span, such as synthesized declarations or members of a field declaration.
        var declarationsBySpan = new Dictionary<(string DocumentVersionId, int Start, int End, string DocId),
            (string First, List<string>? Shared)>();
        foreach (var (symbolId, rows) in perSymbol)
        {
            var docId = SymbolId.DocCommentIdOf(symbolId);
            foreach (var row in rows)
            {
                if (row.FullStart is not { } start || row.FullEnd is not { } end)
                    continue;
                var key = (row.DocVersionId, start, end, docId);
                if (!declarationsBySpan.TryGetValue(key, out var group))
                    declarationsBySpan[key] = (symbolId, null);
                else if (group.First != symbolId)
                {
                    if (group.Shared == null)
                    {
                        group.Shared = [group.First];
                        declarationsBySpan[key] = group;
                    }
                    group.Shared.Add(symbolId);
                }
            }
        }

        foreach (var (_, shared) in declarationsBySpan.Values)
        {
            if (shared == null)
                continue;
            foreach (var symbolId in shared)
            {
                if (!coDeclaredPeers.TryGetValue(symbolId, out var peers))
                    coDeclaredPeers[symbolId] = peers = new HashSet<string>(StringComparer.Ordinal);
                peers.UnionWith(shared);
                peers.Remove(symbolId);
            }
        }

        // Content and line_starts load once per distinct document_version_id, not once per
        // declaration row.
        if (pendingDocIds.Count > 0)
        {
            using var docCmd = _connection.CreateCommand();
            docCmd.CommandText = """
                SELECT dv.document_version_id, doc.relative_path, dv.line_starts, dv.content
                FROM document_versions dv
                JOIN documents doc ON doc.document_id = dv.document_id
                WHERE dv.document_version_id IN (SELECT value FROM json_each(@documentVersionIds));
                """;
            SqlIdList.Bind(docCmd, "@documentVersionIds", pendingDocIds);
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
                if (byPath != 0)
                    return byPath;
                var byStart = Comparer<int?>.Default.Compare(a.FullStart, b.FullStart);
                return byStart != 0 ? byStart : Comparer<int?>.Default.Compare(a.FullEnd, b.FullEnd);
            });

            var isGeneratedOverall = locatedRows.Count > 0 && locatedRows.All(static r => r.IsGenerated == 1);
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
        foreach (var sid in symbolIds)
        {
            if (!result.ContainsKey(sid))
                result[sid] = new DeclInfo { IsGenerated = false, Locations = [], DeclarationCount = 0, DocumentPaths = [] };
        }
        return result;
    }

    private static CandidateFacts BuildFacts(CandidateRow row, ProjectFacts projectFacts)
    {
        // One metadata parse per candidate row (R5.1). A row that does not parse still fails
        // loudly and names the offending symbol (SymbolMetadata.Parse).
        var metadata = SymbolMetadata.Parse(row.MetadataJson, row.SymbolId);
        var accessorKind = SymbolMetadata.GetString(metadata, SymbolMetadataKeys.AccessorKind);
        var associatedSymbol = SymbolMetadata.GetString(metadata, SymbolMetadataKeys.AssociatedSymbol);
        var assemblyName = MetadataReferenceIdentity.SimpleName(row.AssemblyIdentity);
        return new CandidateFacts(
            row,
            metadata,
            SymbolMetadata.GetString(metadata, SymbolMetadataKeys.Accessibility),
            SymbolMetadata.GetString(metadata, SymbolMetadataKeys.EffectiveAccessibility),
            assemblyName,
            projectFacts.IsTestProject(assemblyName),
            SymbolMetadata.IsTrue(metadata, SymbolMetadataKeys.IsExtensionBlock),
            SymbolMetadata.IsTrue(metadata, SymbolMetadataKeys.IsStaticConstructor),
            SymbolMetadata.IsImplicitlyDeclared(metadata),
            SymbolMetadata.IsTrue(metadata, SymbolMetadataKeys.ImplementsExternalMember),
            accessorKind,
            accessorKind != null && associatedSymbol != null
                ? SymbolId.Compose(associatedSymbol, row.AssemblyIdentity)
                : null,
            SymbolMetadata.IsTrue(metadata, SymbolMetadataKeys.ContainsEntryPoint),
            SymbolMetadata.IsTrue(metadata, SymbolMetadataKeys.IsEntryPoint));
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
    ///     D49 / D145: the set of edge sources that count as "self" for a candidate and therefore
    ///     never keep it alive (for own edges, accessor-inherited edges, descendant-inherited
    ///     edges and type-use edges alike).
    /// </summary>
    private static HashSet<string> BuildSelfSources(
        CandidateFacts cand,
        Dictionary<string, TypeSubtree> subtreeByType,
        Dictionary<string, List<string>> accessorsByAssociatedSymbol)
    {
        if (cand.Kind == nameof(IndexedSymbolKind.Type) &&
            subtreeByType.TryGetValue(cand.SymbolId, out var subtree))
            return subtree.Symbols;

        var self = new HashSet<string>(StringComparer.Ordinal) { cand.SymbolId };
        if (cand.Kind is nameof(IndexedSymbolKind.Property) or nameof(IndexedSymbolKind.Event) &&
            accessorsByAssociatedSymbol.TryGetValue(cand.SymbolId, out var ownAccessors))
        {
            foreach (var accessorId in ownAccessors)
                self.Add(accessorId);
        }

        if (cand.AssociatedSymbolId != null)
        {
            self.Add(cand.AssociatedSymbolId);
            if (accessorsByAssociatedSymbol.TryGetValue(cand.AssociatedSymbolId, out var siblingAccessors))
            {
                foreach (var siblingId in siblingAccessors)
                    self.Add(siblingId);
            }
        }

        return self;
    }

    /// <summary>
    ///     The effective incoming LIVE list for a candidate: its own live edges plus the edges
    ///     it inherits under design decision 1 (an accessor from its property or event by role;
    ///     a type from its descendants and from the type-use edges that enter it from outside its
    ///     subtree). Every edge whose source is in <see cref="BuildSelfSources" /> is dropped
    ///     for all three lists (D49, D145). The ladder and the summary run on this list (R5.7).
    /// </summary>
    private static List<EdgeRecord> BuildEffectiveIncoming(
        CandidateFacts cand,
        Dictionary<string, List<EdgeRecord>> incomingByTarget,
        Dictionary<string, List<EdgeRecord>> typeUseByTarget,
        Dictionary<string, TypeSubtree> subtreeByType,
        Dictionary<string, List<string>> accessorsByAssociatedSymbol)
    {
        var selfSources = BuildSelfSources(cand, subtreeByType, accessorsByAssociatedSymbol);

        // D13 / D49 / D145: an edge from the candidate's self set is a fact in the store,
        // but it never makes the candidate live.
        var effective = new List<EdgeRecord>();
        if (incomingByTarget.TryGetValue(cand.SymbolId, out var own))
        {
            foreach (var edge in own)
                if (!selfSources.Contains(edge.SourceSymbolId))
                    effective.Add(edge);
        }

        // B24: an accessor inherits its property's or event's edges by role.
        if (cand.AccessorKind != null && cand.AssociatedSymbolId != null)
        {
            if (incomingByTarget.TryGetValue(cand.AssociatedSymbolId, out var associated))
            {
                foreach (var edge in associated)
                    if (!selfSources.Contains(edge.SourceSymbolId) && AccessorEdges.Inherits(cand.AccessorKind, edge.Kind))
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
                    if (!selfSources.Contains(edge.SourceSymbolId))
                        effective.Add(edge);
            }

            if (typeUseByTarget.TryGetValue(cand.SymbolId, out var typeUse))
            {
                foreach (var edge in typeUse)
                    if (!selfSources.Contains(edge.SourceSymbolId))
                        effective.Add(edge);
            }
        }

        return effective;
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

    private static bool IsEfConventionMember(CandidateFacts cand, ConventionFacts conventions)
    {
        if (conventions.EfSources.Contains(cand.SymbolId))
            return true;

        var isPropertyAccessor = cand.AccessorKind is "get" or "set" or "init";
        var isInstanceConstructor = PublicSurface.IsInstanceConstructor(cand.Kind, cand.Row.DocCommentId);
        if (cand.Kind is not (nameof(IndexedSymbolKind.Property) or nameof(IndexedSymbolKind.Field)) &&
            !isPropertyAccessor && !isInstanceConstructor)
            return false;

        var memberId = isPropertyAccessor ? cand.AssociatedSymbolId ?? cand.SymbolId : cand.SymbolId;
        return SymbolId.DeriveContainingTypeSymbolId(memberId) is { } enclosing &&
               (isInstanceConstructor
                   ? conventions.EfDirectTargets.Contains(enclosing) || conventions.EfSources.Contains(enclosing)
                   : conventions.EfMemberTypes.Contains(enclosing));
    }

    private static bool IsFrameworkConvention(CandidateFacts cand, ConventionFacts conventions)
    {
        if (conventions.UnmodeledMediatRTypes.Contains(cand.SymbolId))
            return true;

        var memberId = cand.AssociatedSymbolId ?? cand.SymbolId;
        if (SymbolId.DeriveContainingTypeSymbolId(memberId) is not { } enclosing)
            return false;

        var isConstructor = PublicSurface.IsInstanceConstructor(cand.Kind, cand.Row.DocCommentId);
        if (isConstructor &&
            (conventions.FrameworkActivatedTypes.Contains(enclosing) || conventions.UnmodeledMediatRTypes.Contains(enclosing)))
            return true;

        return conventions.OptionsTypes.Contains(enclosing) &&
               (isConstructor || cand.AccessorKind is "get" or "set" or "init" ||
                cand.Kind is nameof(IndexedSymbolKind.Property) or nameof(IndexedSymbolKind.Field));
    }

    /// <summary>
    ///     An instance field, an instance property (not an indexer) or its getter of a record. The
    ///     compiler reads them in the members that it synthesizes for the record (Equals, GetHashCode,
    ///     PrintMembers, Deconstruct, the copy constructor), and the index has no edge for these reads.
    ///     The verdict is not inherited by the record type: a record that nothing uses stays dead.
    /// </summary>
    private static bool IsRecordDataMember(CandidateFacts cand, Dictionary<string, CandidateFacts> factsById)
    {
        if (cand.AccessorKind != "get" &&
            cand.Kind is not (nameof(IndexedSymbolKind.Property) or nameof(IndexedSymbolKind.Field)))
            return false;

        return !cand.Row.DocCommentId.EndsWith(')') &&
               !SymbolMetadata.IsTrue(cand.Metadata, SymbolMetadataKeys.IsStatic) &&
               SymbolId.DeriveContainingTypeSymbolId(cand.SymbolId) is { } enclosing &&
               factsById.TryGetValue(enclosing, out var type) &&
               SymbolMetadata.IsTrue(type.Metadata, SymbolMetadataKeys.IsRecord);
    }

    /// <summary>
    ///     B34: the convention verdict for a candidate that inherits it from another symbol. An
    ///     accessor inherits its associated property's or event's verdict; a type inherits the
    ///     first convention verdict among its descendants in ordinal member order. Returns the
    ///     reason and the symbol id supporting the verdict.
    /// </summary>
    private static bool TryInheritedConvention(
        CandidateFacts cand,
        Dictionary<string, CandidateFacts> factsById,
        Dictionary<string, TypeSubtree> subtreeByType,
        ConventionFacts conventions,
        out string reason,
        out string symbolId)
    {
        if (cand.AssociatedSymbolId != null)
        {
            if (factsById.TryGetValue(cand.AssociatedSymbolId, out var associated) &&
                TryConvention(associated, conventions, out reason))
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
                    TryConvention(member, conventions, out reason))
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
        ConventionFacts conventions,
        out string reason,
        out string symbolId)
    {
        if (TryConvention(cand, conventions, out reason))
        {
            symbolId = cand.SymbolId;
            return true;
        }

        return TryInheritedConvention(cand, factsById, subtreeByType, conventions, out reason, out symbolId);
    }

    private static bool TryConvention(
        CandidateFacts cand,
        ConventionFacts conventions,
        out string reason)
    {
        if (IsEfConventionMember(cand, conventions))
        {
            reason = DeadCandidateReason.EfConvention;
            return true;
        }

        if (cand.Kind is nameof(IndexedSymbolKind.Property) or nameof(IndexedSymbolKind.Field) or nameof(IndexedSymbolKind.Method) &&
            conventions.SerializationTargets.Contains(cand.SymbolId))
        {
            reason = DeadCandidateReason.SerializationConvention;
            return true;
        }

        if (IsFrameworkConvention(cand, conventions))
        {
            reason = DeadCandidateReason.FrameworkConvention;
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

    private static EdgeRecord GetStrongestWeak(List<EdgeRecord> incoming)
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
        // The caller enters the weak-only branch only for a candidate with at least
        // one incoming edge and no strong one, so a weak edge always sets best.
        return best!;
    }

    private static DeadCandidateUncertainty MakeBindingIncompletenessUncertainty(string symbolId, List<BindingIncompletenessRecord> all, ProjectFacts projectFacts, List<string> docPaths, string assemblyName)
    {
        // Find relevant binding records that overlap this candidate's docs. Restricted
        // to BindingIncompletenessReason.UnobservableReasons so the description names the reason that actually
        // triggered OverlapsBindingIncompleteness — otherwise a co-located but
        // non-triggering record (e.g. filtered_external, which never makes a
        // candidate unresolved) could win the reason pick and describe the wrong cause.
        // A binding record carries the Roslyn project name, the candidate carries the
        // assembly name, so the project-level comparison maps one to the other (B18).
        var relevant = all.Where(r => BindingIncompletenessReason.UnobservableReasons.Contains(r.Reason)
                                   && (r.DocumentPath != null && docPaths.Contains(r.DocumentPath, StringComparer.Ordinal)
                                   || r.DocumentPath == null && string.Equals(projectFacts.ToAssemblyName(r.ProjectName), assemblyName, StringComparison.Ordinal))).ToList();
        var byReason = relevant.GroupBy(r => r.Reason, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
        if (byReason != null)
        {
            var reason = byReason.Key;
            var count = byReason.Sum(r => r.Count);
            var projects = byReason.Select(r => r.ProjectName).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var scope = string.Join(", ", projects);
            var desc = BindingIncompletenessReason.Describe(reason, count, scope);
            return new DeadCandidateUncertainty([symbolId], "binding_incompleteness", desc);
        }
        return new DeadCandidateUncertainty([symbolId], "binding_incompleteness", "Binding incompleteness overlaps this symbol's document; relations may be missing.");
    }

    private static DeadCandidateUncertainty MakePublicSurfaceUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "public_surface", "Public/protected member with no internal incoming LIVE edge — verify no external caller or reflection consumer before removing.");
    }

    private static DeadCandidateUncertainty MakeGeneratedUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "generated_excluded", UncertaintyDescriptions.GeneratedDeadCandidate(symbolId));
    }

    private static DeadCandidateUncertainty MakeTestHarnessUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "test_harness", "Test-project symbol: a test framework may discover or call it by reflection, and no strong LIVE edge reaches it. Verify no production code depends on this member before removing.");
    }

    private static DeadCandidateUncertainty MakePossibleDispatchUncertainty(EdgeRecord edge)
    {
        return new DeadCandidateUncertainty([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, UncertaintyDescriptions.DispatchCandidate(edge.TargetSymbolId, edge.Provenance));
    }

    private static DeadCandidateUncertainty MakeFrameworkConventionUncertainty(EdgeRecord edge)
    {
        return new DeadCandidateUncertainty([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, UncertaintyDescriptions.FrameworkConvention(edge.Kind));
    }

    private static DeadCandidateUncertainty MakeNameCandidateUncertainty(EdgeRecord edge)
    {
        return new DeadCandidateUncertainty([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, UncertaintyDescriptions.NameCandidate(edge.TargetSymbolId));
    }

    private static DeadCandidateUncertainty MakeRuntimeUnknownUncertainty(EdgeRecord edge)
    {
        var desc = $"Unmodeled construct: a '{edge.Kind}' edge carries 'runtime_unknown' provenance because the construct is listed in DeclaredBoundaries.Known as deliberately not fully modeled. The concrete type was resolved but the runtime activation/registration semantics are not captured. See DeclaredBoundaries.Known for the full, closed list of declared boundaries.";
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
        return new DeadCandidateUncertainty([symbolId], "external_interface_implementation", "Implements an interface member or overrides a member declared outside the compilation: the runtime or a framework calls it through that member, so the index cannot observe the call.");
    }

    private static DeadCandidateUncertainty MakeEfConventionUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "ef_convention", "EF Core can discover mapping sources, fields, properties and accessors by convention. It can also invoke instance constructors of mapped entities and EF mapping source types. The index does not track all of these uses. See DeclaredBoundaries.ef_convention.", "ef_convention");
    }

    private static DeadCandidateUncertainty MakeSerializationConventionUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "serialization_convention", "A serializer may use this member by reflection through an explicit serialization contract, constructor attribute, or callback attribute.");
    }

    private static DeadCandidateUncertainty MakeConventionUncertainty(string reason, string symbolId)
    {
        return reason switch
        {
            DeadCandidateReason.EfConvention => MakeEfConventionUncertainty(symbolId),
            DeadCandidateReason.SerializationConvention => MakeSerializationConventionUncertainty(symbolId),
            _ => new DeadCandidateUncertainty([symbolId], "framework_convention",
                "A framework may use this symbol through runtime activation or options binding.")
        };
    }

    private static DeadCandidateUncertainty MakeRecordMemberUncertainty(string symbolId)
    {
        return new DeadCandidateUncertainty([symbolId], "framework_convention", "The compiler reads this member in the members that it synthesizes for a record (Equals, GetHashCode, ToString, Deconstruct, copy constructor). The index has no edge for these reads.");
    }

    internal sealed record CandidateRow(string SymbolId, string Kind, string? Fqn, string? MetadataJson, string DocCommentId, string AssemblyIdentity);

    /// <summary>
    ///     One metadata parse per candidate row (R5.1). Every filter and ladder decision reads
    ///     these parsed fields instead of re-parsing <see cref="CandidateRow.MetadataJson" />.
    /// </summary>
    internal sealed record CandidateFacts(
        CandidateRow Row,
        JsonElement? Metadata,
        string? Accessibility,
        string? EffectiveAccessibility,
        string AssemblyName,
        bool IsTest,
        bool IsExtensionBlock,
        bool IsStaticConstructor,
        bool IsImplicitlyDeclared,
        bool ImplementsExternalMember,
        string? AccessorKind,
        string? AssociatedSymbolId,
        bool ContainsEntryPoint,
        bool IsEntryPointMethod)
    {
        public string SymbolId => Row.SymbolId;
        public string Kind => Row.Kind;
    }

    internal sealed record TypeSubtree(HashSet<string> Symbols, List<string> Descendants);

    internal sealed class DeclInfo
    {
        public bool IsGenerated { get; set; }
        public List<DeclarationLocation> Locations { get; set; } = [];
        public int DeclarationCount { get; set; }
        public List<string> DocumentPaths { get; set; } = [];
    }
}
