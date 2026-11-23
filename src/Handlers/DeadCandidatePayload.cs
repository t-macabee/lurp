using System.Text.Json.Serialization;

namespace Lurp.Handlers;

/// <summary>
///     The one projection of a dead-candidate page and envelope, shared by the CLI handler
///     and the MCP tool, so the two surfaces cannot drift again.
/// </summary>
internal static class DeadCandidatePayload
{
    internal static object Filters(
        string? project,
        string? document,
        string? kind,
        bool includePublic,
        bool includeGenerated,
        bool includeTests) => new
    {
        project,
        document,
        kind,
        include_public = includePublic,
        include_generated = includeGenerated,
        include_tests = includeTests
    };

    internal static CliResponse Envelope(
        DeadCandidatePage page,
        object filters,
        string snapshotId,
        object? freshness) =>
        new(Build(page, filters, snapshotId, Candidates(page)), freshness);

    internal static CliResponse Meta(
        DeadCandidatePage page,
        object filters,
        string snapshotId,
        object? freshness) =>
        new(Build(page, filters, snapshotId, candidates: null), freshness);

    internal static McpResponse PinnedEnvelope(
        DeadCandidatePage page,
        object filters,
        string snapshotId,
        object freshness) =>
        new(Build(page, filters, snapshotId, Candidates(page)), freshness);

    private static PageResponse Build(
        DeadCandidatePage page,
        object filters,
        string snapshotId,
        IReadOnlyList<object>? candidates) =>
        new(snapshotId, filters, DeadCandidateLiveness.LiveEdgeKinds, DeadCandidateLiveness.TypeUseEdgeKinds,
            DeadCandidateLiveness.StrongProvenance, DeadCandidateLiveness.UncertainProvenance,
            page.CandidateCount, page.DeadCount, page.UncertainCount, page.UnresolvedCount, candidates, page.NextCursor);

    internal record PageResponse(
        [property: JsonPropertyOrder(-2)] string snapshot_id,
        object filters,
        IReadOnlyList<string> incoming_edge_kinds_checked,
        IReadOnlyList<string> type_use_edge_kinds_checked,
        IReadOnlyList<string> live_provenance_rank,
        IReadOnlyList<string> uncertain_provenance,
        int candidate_count, int dead_count, int uncertain_count, int unresolved_count,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<object>? candidates,
        string? next_cursor);

    internal sealed record CliResponse : PageResponse
    {
        internal CliResponse(PageResponse page, object? freshness) : base(page) => this.freshness = freshness;

        [JsonPropertyOrder(1)]
        public object? freshness { get; }
    }

    internal sealed record McpResponse : PageResponse
    {
        internal McpResponse(PageResponse page, object freshness) : base(page) => this.freshness = freshness;

        [JsonPropertyOrder(-1)]
        public object freshness { get; }

        [JsonPropertyOrder(-1)]
        public bool pinned { get; } = true;
    }

    internal static List<object> Candidates(DeadCandidatePage page)
    {
        return page.Candidates.Select(e => (object)new
        {
            symbol_id = e.SymbolId,
            fqn = e.Fqn,
            kind = e.Kind,
            accessibility = e.Accessibility,
            effective_accessibility = e.EffectiveAccessibility,
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
            },
            declaration_span = e.Locations.Count > 0 ? new
            {
                start_line = e.Locations[0].StartLine,
                end_line = e.Locations[0].EndLine
            } : null
        }).ToList();
    }
}
