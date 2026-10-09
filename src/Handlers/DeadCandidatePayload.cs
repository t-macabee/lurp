using Lurp.Storage;

namespace Lurp.Handlers;

/// <summary>
///     The one projection of a dead-candidate page, shared by the CLI handler and the MCP
///     tool, so the two surfaces cannot drift again (for example <c>declaration_span</c>).
/// </summary>
internal static class DeadCandidatePayload
{
    internal static List<object> Candidates(DeadCandidatePage page)
    {
        return page.Candidates.Select(e => (object)new
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
            },
            declaration_span = e.Locations.Count > 0 ? new
            {
                start_line = e.Locations[0].StartLine,
                end_line = e.Locations[0].EndLine
            } : null
        }).ToList();
    }
}
