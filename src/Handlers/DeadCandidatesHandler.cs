using System.Globalization;
using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

internal static class DeadCandidatesHandler
{
    private const int DefaultLimit = 50;

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

    public static void Run(string[] args)
    {
        var projectArg = HandlerBootstrap.GetArgValue(args, "--project=");
        var documentArg = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--document="));
        var kindArg = HandlerBootstrap.GetArgValue(args, "--kind=");
        var limitArg = HandlerBootstrap.GetArgValue(args, "--limit=");
        var cursorArg = HandlerBootstrap.GetArgValue(args, "--cursor=");
        var includePublic = args.Contains("--include-public");
        var includeGenerated = args.Contains("--include-generated");
        var includeTests = args.Contains("--include-tests");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        if (!string.IsNullOrEmpty(kindArg) && !IsValidKind(kindArg))
            HandlerBootstrap.Fail($"ERROR: --kind must be one of: Type, Method, Property, Field, Event (case-insensitive). Got '{kindArg}'.");

        var limit = DefaultLimit;
        if (!string.IsNullOrEmpty(limitArg))
        {
            if (!int.TryParse(limitArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit < 1)
                HandlerBootstrap.Fail("ERROR: --limit must be a positive integer.");
        }
        if (limit > 200)
            HandlerBootstrap.Fail("ERROR: --limit must be <= 200 (SQLITE_MAX_VARIABLE_NUMBER guard).");

        var projectFilter = string.IsNullOrEmpty(projectArg) ? null : projectArg;
        var documentFilter = string.IsNullOrEmpty(documentArg) ? null : documentArg;
        var kindFilter = string.IsNullOrEmpty(kindArg) ? null : kindArg;

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            DeadCandidateCursor? cursor = null;
            if (!string.IsNullOrEmpty(cursorArg))
            {
                cursor = DeadCandidateCursor.TryDecode(cursorArg);
                if (cursor == null)
                    HandlerBootstrap.Fail("ERROR: --cursor is not a valid continuation token.");
            }

            DeadCandidatePage page;
            try
            {
                page = store.GetDeadCandidatesPage(snapshotId, projectFilter, documentFilter, kindFilter, includePublic, includeGenerated, includeTests, limit, cursor);
            }
            catch (ArgumentException ex)
            {
                HandlerBootstrap.Fail($"ERROR: {ex.Message}");
                return;
            }

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId);

            var filters = new
            {
                project = projectFilter,
                document = documentFilter,
                kind = kindFilter,
                include_public = includePublic,
                include_generated = includeGenerated,
                include_tests = includeTests
            };

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
                },
                declaration_span = e.Locations.Count > 0 ? new
                {
                    start_line = e.Locations[0].StartLine,
                    end_line = e.Locations[0].EndLine
                } : null
            }).ToList();

            var meta = new
            {
                snapshot_id = snapshotId,
                filters,
                incoming_edge_kinds_checked = IncomingEdgeKindsChecked,
                live_provenance_rank = LiveProvenanceRank,
                uncertain_provenance = UncertainProvenance,
                candidate_count = page.CandidateCount,
                dead_count = page.DeadCount,
                uncertain_count = page.UncertainCount,
                unresolved_count = page.UnresolvedCount,
                next_cursor = page.NextCursor,
                freshness = HandlerBootstrap.FreshnessJson(freshness)
            };

            switch (outputMode)
            {
                case OutputMode.Summary:
                    foreach (var c in candidates)
                    {
                        var loc = c.document_path != null ? $"{c.document_path}:{c.locations.FirstOrDefault()?.start_line ?? 0}" : "<no-doc>";
                        Console.WriteLine($"{c.kind,-12} {c.accessibility ?? "unknown",-18} {c.fqn ?? c.symbol_id}  {loc}  {c.reason}  {c.status}");
                    }
                    Console.WriteLine($"-- {candidates.Count}/{page.DeadCount + page.UncertainCount + page.UnresolvedCount} dead candidate(s) shown (proved:{page.DeadCount} uncertain:{page.UncertainCount} unresolved:{page.UnresolvedCount} total candidates:{page.CandidateCount}){(page.NextCursor != null ? "; more available (--cursor)" : "")}");
                    break;

                case OutputMode.Jsonl:
                    Console.WriteLine(JsonSerializer.Serialize(new { type = "meta", meta }, HandlerBootstrap.CompactJson));
                    foreach (var c in candidates)
                        Console.WriteLine(JsonSerializer.Serialize(new { type = "candidate", candidate = c }, HandlerBootstrap.CompactJson));
                    break;

                default:
                    Console.WriteLine(JsonSerializer.Serialize(
                        new
                        {
                            snapshot_id = snapshotId,
                            filters,
                            incoming_edge_kinds_checked = IncomingEdgeKindsChecked,
                            live_provenance_rank = LiveProvenanceRank,
                            uncertain_provenance = UncertainProvenance,
                            candidate_count = page.CandidateCount,
                            dead_count = page.DeadCount,
                            uncertain_count = page.UncertainCount,
                            unresolved_count = page.UnresolvedCount,
                            candidates,
                            next_cursor = page.NextCursor,
                            freshness = HandlerBootstrap.FreshnessJson(freshness)
                        },
                        HandlerBootstrap.IndentedJson));
                    break;
            }
        });
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
