using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class DeadCandidatesTool
{
    private readonly McpSessionContext _session;

    public DeadCandidatesTool(McpSessionContext session)
    {
        _session = session;
    }

    private static readonly string[] IncomingEdgeKindsChecked =
    [
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
    ];

    private static readonly string[] LiveProvenanceRank =
    [
        Provenance.CompilerProved,
        Provenance.FrameworkDerived,
        Provenance.GlobalImplementationRelation
    ];

    private static readonly string[] UncertainProvenance =
    [
        Provenance.Possible,
        Provenance.Convention,
        Provenance.NameCandidate,
        Provenance.RuntimeUnknown
    ];

    [McpServerTool(Name = "lurp_dead_candidates", Title = "Lurp Dead Candidates", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List dead-code candidates for the pinned snapshot: symbols of kind Type, Method, Property, Field, Event with no incoming LIVE edge (Calls, Constructs, Reads, Writes, Handles, RoutesTo, Registers, MapsTo, MayDispatchTo, StaticallyCalls, TestedBy, ReflectionTypeRef, ReflectionMemberRef, ReflectionNameCandidate) of strong provenance (compiler_proved, framework_derived, global_implementation_relation). Suppression ladder: (1) strong LIVE edge -> alive, not dead; (2) MayDispatchTo with possible provenance -> uncertain_dead reason possible_dispatch; (3) convention/name_candidate/runtime_unknown edges -> uncertain_dead with matching DeclaredBoundaries text (framework_convention, name_candidate, runtime_unknown); (4) candidate overlaps binding_incompleteness UnobservableReasons -> unresolved reason binding_incompleteness; (5) candidate is the compilation's own process entry point (explicit static Main or the compiler-synthesized top-level-statements form) -> uncertain_dead reason entry_point_convention, checked ahead of the public-surface step below since entry points have no in-repo caller by definition regardless of accessibility; (6) Public/Protected without --include-public -> excluded from proved_dead, with include_public -> uncertain_dead reason public_surface; (7) EF private member of MapsTo entity or attribute-free serialization-eligible public/internal property in System.Text.Json-referencing project -> uncertain_dead reason ef_convention or serialization_convention. Default excludes generated symbols and test-project symbols (project name %.Tests); lift with include_generated/include_tests (surfaced as uncertain reason generated_excluded/test_harness). Batched WHERE target_symbol_id IN (...) per page, never per-candidate GetIncomingEdges. Keyset pagination via limit/cursor ordered by symbol_id ASC; limit default 50 max 200. Filters: project (assembly name), document (forward-slash relative path), kind (Type/Method/Property/Field/Event). Response carries snapshot_id, filters, incoming_edge_kinds_checked[14], live_provenance_rank[3], uncertain_provenance[4], candidate_count (total filtered universe), dead_count/uncertain_count/unresolved_count (totals across all pages), candidates[] page window with symbol_id, fqn, kind, accessibility, document_path, locations[], project_name, declaration_count, is_generated, status (proved_dead/uncertain_dead/unresolved/uncertain), reason, uncertainties[] (verbatim UncertaintyDetector/DeclaredBoundaries wording), incoming_edge_summary, plus next_cursor and freshness.")]
    public string LurpDeadCandidates(
        int? limit = null,
        string? cursor = null,
        string? snapshot_id = null,
        string? project = null,
        string? document = null,
        string? kind = null,
        bool? include_public = null,
        bool? include_generated = null,
        bool? include_tests = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

            if (!string.IsNullOrEmpty(kind) && !IsValidKind(kind!))
                throw new McpProtocolException($"kind must be one of: Type, Method, Property, Field, Event. Got '{kind}'.", McpErrorCode.InvalidParams);

            string? normalizedDocument = null;
            if (!string.IsNullOrEmpty(document))
            {
                normalizedDocument = HandlerBootstrap.NormalizeDocumentPath(document);
                if (string.IsNullOrEmpty(normalizedDocument))
                    throw new McpProtocolException("document is required.", McpErrorCode.InvalidParams);
            }

            var projectFilter = string.IsNullOrEmpty(project) ? null : project;
            var kindFilter = string.IsNullOrEmpty(kind) ? null : kind;
            var includePublic = include_public ?? false;
            var includeGenerated = include_generated ?? false;
            var includeTests = include_tests ?? false;

            var limitVal = limit ?? 50;
            if (limitVal < 1 || limitVal > 200)
                throw new McpProtocolException("limit must be a positive integer <=200.", McpErrorCode.InvalidParams);

            DeadCandidateCursor? cursorObj = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                cursorObj = DeadCandidateCursor.TryDecode(cursor);
                if (cursorObj == null)
                    throw new McpProtocolException("cursor is not a valid continuation token.", McpErrorCode.InvalidParams);
            }

            DeadCandidatePage page;
            try
            {
                page = _session.Store.GetDeadCandidatesPage(snapshotId, projectFilter, normalizedDocument, kindFilter, includePublic, includeGenerated, includeTests, limitVal, cursorObj);
            }
            catch (ArgumentException ex)
            {
                throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
            }

            var freshness = _session.GetFreshnessJson();

            var candidates = page.Candidates.Select(e => new
            {
                symbol_id = e.SymbolId,
                fqn = e.Fqn,
                kind = e.Kind,
                accessibility = e.Accessibility,
                document_path = e.DocumentPath,
                locations = e.Locations.Select(l => new
                {
                    document_path = l.DocumentPath,
                    start_line = l.StartLine,
                    start_column = l.StartColumn,
                    end_line = l.EndLine,
                    end_column = l.EndColumn,
                    is_generated = l.IsGenerated
                }).ToList(),
                project_name = e.ProjectName,
                declaration_count = e.DeclarationCount,
                is_generated = e.IsGenerated,
                status = e.Status,
                reason = e.Reason,
                uncertainties = e.Uncertainties.Select(u => new
                {
                    symbol_ids = u.SymbolIds,
                    relationship_kind = u.RelationshipKind,
                    description = u.Description,
                    boundary_id = u.BoundaryId
                }).ToList(),
                incoming_edge_summary = new
                {
                    live_strong = e.IncomingEdgeSummary.LiveStrong,
                    live_weak = e.IncomingEdgeSummary.LiveWeak,
                    provenance_breakdown = e.IncomingEdgeSummary.ProvenanceBreakdown,
                    kind_breakdown = e.IncomingEdgeSummary.KindBreakdown
                }
            }).ToList();

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                filters = new
                {
                    project = projectFilter,
                    document = normalizedDocument,
                    kind = kindFilter,
                    include_public = includePublic,
                    include_generated = includeGenerated,
                    include_tests = includeTests
                },
                incoming_edge_kinds_checked = IncomingEdgeKindsChecked,
                live_provenance_rank = LiveProvenanceRank,
                uncertain_provenance = UncertainProvenance,
                candidate_count = page.CandidateCount,
                dead_count = page.DeadCount,
                uncertain_count = page.UncertainCount,
                unresolved_count = page.UnresolvedCount,
                candidates,
                next_cursor = page.NextCursor
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

    private static bool IsValidKind(string kind)
    {
        return string.Equals(kind, "Type", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "Method", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "Property", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "Field", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "Event", StringComparison.OrdinalIgnoreCase);
    }
}
