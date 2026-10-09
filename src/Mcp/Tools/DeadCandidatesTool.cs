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

    [McpServerTool(Name = "lurp_dead_candidates", Title = "Lurp Dead Candidates", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List dead-code candidates for the pinned snapshot: symbols of kind Type, Method, Property, Field, Event with no incoming LIVE edge (Calls, MethodGroupRef, Constructs, Reads, Writes, Handles, RoutesTo, Registers, MapsTo, MayDispatchTo, StaticallyCalls, TestedBy, ReflectionTypeRef, ReflectionMemberRef, ReflectionNameCandidate) of strong provenance (compiler_proved, framework_derived, global_implementation_relation). Inherited liveness: accessors take their property's or event's edges by role (a getter every edge except Writes; a setter or init only Writes; an event accessor every edge of the event); type candidates also take these edges from outside the type: References, Returns, Inherits, Implements, Throws. Suppression ladder, in the order the store checks it: (1) a strong LIVE edge (the candidate's own, or inherited as above) -> alive, not a candidate; (2) the candidate overlaps a binding_incompleteness UnobservableReason -> unresolved, reason binding_incompleteness; (3) the process entry point or the type that contains it (explicit static Main or the compiler-synthesized top-level-statements form; nothing in the repository calls it, whatever its accessibility) -> uncertain_dead, reason entry_point_convention; (4) explicit implementation of an interface member declared outside the compilation -> uncertain_dead, reason external_interface_implementation; (5) Public, Protected or protected internal -> excluded from proved_dead without include_public, uncertain_dead with reason public_surface when include_public is set; (6) generated symbol with include_generated -> uncertain, reason generated_excluded; (7) symbol of a test project with include_tests -> uncertain, reason test_harness; (8) when the candidate has only weak incoming edges, the strongest weak edge decides: MayDispatchTo with possible provenance -> uncertain_dead, reason possible_dispatch; convention provenance -> uncertain_dead, reason framework_convention; name_candidate provenance -> uncertain_dead, reason name_candidate; runtime_unknown provenance -> unresolved, reason runtime_unknown; (9) when the candidate has no incoming LIVE edge at all, its own convention verdict: a private or private-protected method, property or field of a MapsTo entity -> uncertain_dead, reason ef_convention; a non-public property or field with an exact serialization opt-in attribute (JsonInclude, Newtonsoft JsonProperty or DataMember) -> uncertain_dead, reason serialization_convention; then the inherited verdict (an accessor takes the verdict of its property, a type takes the first verdict among its members); (10) otherwise proved_dead, reason no_incoming_live_edges. Default excludes compiler-synthesized members (static constructors, implicitly declared members), extension-block marker types, generated symbols and test-project symbols (a project that references a test framework: Microsoft.VisualStudio.TestPlatform.ObjectModel, Microsoft.Testing.Platform, xunit.core, xunit.v3.core, nunit.framework or Microsoft.VisualStudio.TestPlatform.TestFramework); lift with include_generated/include_tests (surfaced as uncertain reason generated_excluded/test_harness). Batched WHERE target_symbol_id IN (...) per page, never per-candidate GetIncomingEdges. Keyset pagination via limit/cursor ordered by symbol_id ASC; limit default 50 max 200. Filters: project (assembly name), document (forward-slash relative path), kind (Type/Method/Property/Field/Event). Response carries snapshot_id, filters, incoming_edge_kinds_checked[15], type_use_edge_kinds_checked[5], live_provenance_rank[3], uncertain_provenance[4], candidate_count (total filtered universe), dead_count/uncertain_count/unresolved_count (totals across all pages), candidates[] page window with symbol_id, fqn, kind, accessibility, document_path, locations[], project_name, declaration_count, is_generated, status (proved_dead/uncertain_dead/unresolved/uncertain), reason, uncertainties[] (verbatim UncertaintyDetector/DeclaredBoundaries wording), incoming_edge_summary, declaration_span {start_line, end_line} (null when the symbol has no location), plus next_cursor and freshness.")]
    public string LurpDeadCandidates(
        int? limit = null,
        string? cursor = null,
        string? snapshot_id = null,
        string? project = null,
        string? document = null,
        string? kind = null,
        bool? include_public = null,
        bool? include_generated = null,
        bool? include_tests = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);
            cancellationToken.ThrowIfCancellationRequested();
            using var store = _session.OpenReadStore();

            if (!string.IsNullOrEmpty(kind) && !DeadCandidateLiveness.IsCandidateKind(kind!))
                throw new McpProtocolException($"kind must be one of: {DeadCandidateLiveness.CandidateKindsText}. Got '{kind}'.", McpErrorCode.InvalidParams);

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
                page = store.GetDeadCandidatesPage(snapshotId, projectFilter, normalizedDocument, kindFilter, includePublic, includeGenerated, includeTests, limitVal, cursorObj);
            }
            catch (ArgumentException ex)
            {
                throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
            }

            var freshness = _session.GetFreshnessJson();

            var candidates = DeadCandidatePayload.Candidates(page);

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
                incoming_edge_kinds_checked = DeadCandidateLiveness.LiveEdgeKinds,
                type_use_edge_kinds_checked = DeadCandidateLiveness.TypeUseEdgeKinds,
                live_provenance_rank = DeadCandidateLiveness.StrongProvenance,
                uncertain_provenance = DeadCandidateLiveness.UncertainProvenance,
                candidate_count = page.CandidateCount,
                dead_count = page.DeadCount,
                uncertain_count = page.UncertainCount,
                unresolved_count = page.UnresolvedCount,
                candidates,
                next_cursor = page.NextCursor
            };

            return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
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
}
