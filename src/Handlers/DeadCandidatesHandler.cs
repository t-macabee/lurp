using System.Text.Json;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class DeadCandidatesHandler
{
    public static void Run(string[] args, IOutputSink output)
    {
        var projectArg = HandlerBootstrap.GetArgValue(args, "--project=");
        var documentArg = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--document="));
        var kindArg = HandlerBootstrap.GetArgValue(args, "--kind=");
        var cursorArg = HandlerBootstrap.GetArgValue(args, "--cursor=");
        var includePublic = args.Contains("--include-public");
        var includeGenerated = args.Contains("--include-generated");
        var includeTests = args.Contains("--include-tests");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        if (!string.IsNullOrEmpty(kindArg) && !DeadCandidateLiveness.IsCandidateKind(kindArg))
            HandlerBootstrap.Fail($"ERROR: --kind must be one of: {DeadCandidateLiveness.CandidateKindsText} (case-insensitive). Got '{kindArg}'.");

        var limit = HandlerBootstrap.ParsePositiveIntArg(args, "--limit=", DeadCandidateLiveness.DefaultPageSize);
        if (limit > DeadCandidateLiveness.MaxPageSize)
            HandlerBootstrap.Fail($"ERROR: --limit must be <= {DeadCandidateLiveness.MaxPageSize}.");

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

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId, output);
            var filters = DeadCandidatePayload.Filters(projectFilter, documentFilter, kindFilter, includePublic, includeGenerated, includeTests);
            var freshnessJson = FreshnessJson.Cheap(freshness);

            switch (outputMode)
            {
                case OutputMode.Summary:
                    foreach (var e in page.Candidates)
                    {
                        var line = e.Locations.FirstOrDefault()?.StartLine;
                        var loc = e.DocumentPath == null ? "<no-doc>" : line is { } l ? $"{e.DocumentPath}:{l}" : e.DocumentPath;
                        output.WriteLine($"{e.Kind,-12} {e.Accessibility ?? "unknown",-18} {e.Fqn ?? e.SymbolId}  {loc}  {e.Reason}  {e.Status}");
                    }
                    output.WriteLine($"-- {page.Candidates.Count}/{page.DeadCount + page.UncertainCount + page.UnresolvedCount} dead candidate(s) shown (proved:{page.DeadCount} uncertain:{page.UncertainCount} unresolved:{page.UnresolvedCount} total candidates:{page.CandidateCount}){(page.NextCursor != null ? "; more available (--cursor)" : "")}");
                    break;

                case OutputMode.Jsonl:
                    var meta = DeadCandidatePayload.Meta(page, filters, snapshotId, freshnessJson);
                    output.WriteLine(JsonSerializer.Serialize(new { type = "meta", meta }, HandlerBootstrap.CompactJson));
                    foreach (var c in DeadCandidatePayload.Candidates(page))
                        output.WriteLine(JsonSerializer.Serialize(new { type = "candidate", candidate = c }, HandlerBootstrap.CompactJson));
                    break;

                default:
                    var envelope = DeadCandidatePayload.Envelope(page, filters, snapshotId, freshnessJson);
                    output.WriteLine(JsonSerializer.Serialize(envelope, HandlerBootstrap.IndentedJson));
                    break;
            }
        }, output);
    }
}
