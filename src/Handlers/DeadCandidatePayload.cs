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

    internal static Dictionary<string, object?> Envelope(
        DeadCandidatePage page,
        object filters,
        string snapshotId,
        object? freshness,
        List<object>? candidates = null) =>
        Build(page, filters, snapshotId, freshness, candidates ?? Candidates(page), includeCandidates: true);

    internal static Dictionary<string, object?> Meta(
        DeadCandidatePage page,
        object filters,
        string snapshotId,
        object? freshness) =>
        Build(page, filters, snapshotId, freshness, candidates: null, includeCandidates: false);

    private static Dictionary<string, object?> Build(
        DeadCandidatePage page,
        object filters,
        string snapshotId,
        object? freshness,
        List<object>? candidates,
        bool includeCandidates)
    {
        var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["snapshot_id"] = snapshotId,
            ["filters"] = filters,
            ["incoming_edge_kinds_checked"] = DeadCandidateLiveness.LiveEdgeKinds,
            ["type_use_edge_kinds_checked"] = DeadCandidateLiveness.TypeUseEdgeKinds,
            ["live_provenance_rank"] = DeadCandidateLiveness.StrongProvenance,
            ["uncertain_provenance"] = DeadCandidateLiveness.UncertainProvenance,
            ["candidate_count"] = page.CandidateCount,
            ["dead_count"] = page.DeadCount,
            ["uncertain_count"] = page.UncertainCount,
            ["unresolved_count"] = page.UnresolvedCount
        };
        if (includeCandidates)
            envelope["candidates"] = candidates;
        envelope["next_cursor"] = page.NextCursor;
        envelope["freshness"] = freshness;
        return envelope;
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
