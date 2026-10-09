using System.Globalization;
using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

internal static class DeadCandidatesHandler
{
    private const int DefaultLimit = 50;

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

        if (!string.IsNullOrEmpty(kindArg) && !DeadCandidateLiveness.IsCandidateKind(kindArg))
            HandlerBootstrap.Fail($"ERROR: --kind must be one of: {DeadCandidateLiveness.CandidateKindsText} (case-insensitive). Got '{kindArg}'.");

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

            var candidates = DeadCandidatePayload.Candidates(page);

            var meta = new
            {
                snapshot_id = snapshotId,
                filters,
                incoming_edge_kinds_checked = DeadCandidateLiveness.LiveEdgeKinds,
                type_use_edge_kinds_checked = DeadCandidateLiveness.TypeUseEdgeKinds,
                live_provenance_rank = DeadCandidateLiveness.StrongProvenance,
                uncertain_provenance = DeadCandidateLiveness.UncertainProvenance,
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
                    foreach (var e in page.Candidates)
                    {
                        var loc = e.DocumentPath != null ? $"{e.DocumentPath}:{e.Locations.FirstOrDefault()?.StartLine ?? 0}" : "<no-doc>";
                        Console.WriteLine($"{e.Kind,-12} {e.Accessibility ?? "unknown",-18} {e.Fqn ?? e.SymbolId}  {loc}  {e.Reason}  {e.Status}");
                    }
                    Console.WriteLine($"-- {page.Candidates.Count}/{page.DeadCount + page.UncertainCount + page.UnresolvedCount} dead candidate(s) shown (proved:{page.DeadCount} uncertain:{page.UncertainCount} unresolved:{page.UnresolvedCount} total candidates:{page.CandidateCount}){(page.NextCursor != null ? "; more available (--cursor)" : "")}");
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
                            incoming_edge_kinds_checked = DeadCandidateLiveness.LiveEdgeKinds,
                            type_use_edge_kinds_checked = DeadCandidateLiveness.TypeUseEdgeKinds,
                            live_provenance_rank = DeadCandidateLiveness.StrongProvenance,
                            uncertain_provenance = DeadCandidateLiveness.UncertainProvenance,
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
}
