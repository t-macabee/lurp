using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Shared;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Lurp.Tests;

/// <summary>
///     Output-contract snapshot test (code review F6 step 1). Runs every CLI mode
///     (JSON and JSONL) and every MCP tool against the CallShapes fixture, reduces
///     each payload to a sorted list of "path: type" lines, and compares with the
///     committed snapshot. A later change to any output field, type, or shape shows
///     as a reviewed diff. The snapshot starts empty; the first run writes the
///     .actual file and fails with instructions.
/// </summary>
[Trait("Category", "Slow")]
public sealed class OutputContractSnapshotTests(OutputContractFixture fixture) : IClassFixture<OutputContractFixture>
{

    // Known data maps, by full path: objects whose keys are data values (document
    // paths, project names), not schema fields. Under a map path every key is
    // recorded as "{*}", so the values never become part of the contract. An object
    // that is not listed here must have keys matching ^[a-z][a-z0-9_]*$; a key that
    // does not is collected as a violation instead of being descended into (T6).
    // A new entry needs a model type that is a dictionary; a key violation alone is
    // not a reason to add one (R10).
    private static readonly HashSet<string> MapPaths = new(StringComparer.Ordinal)
    {
        // SnapshotManifest.CompilationOptionsFingerprints: keys are project names ("Core", "App").
        "$.compilation_options_fingerprints",
        // SnapshotCompleteness.ActiveTfms: keys are target framework monikers ("net8.0").
        "$.completeness.active_tfms",
        // SnapshotManifest.DocumentVersions: keys are document paths ("App/ShapeCallers.cs").
        "$.document_versions",
        // SnapshotManifest.MetadataReferenceIdentities: keys are project names.
        "$.metadata_reference_identities",
        // SnapshotManifest.ProjectDocuments: keys are project names.
        "$.project_documents",
        // SnapshotManifest.ProjectGraph: keys are project names.
        "$.project_graph",
        // SnapshotManifest.TargetFrameworks: keys are project names.
        "$.target_frameworks",
        // The status payload nests the manifest one level deeper, under "$.manifest".
        // SnapshotManifest.CompilationOptionsFingerprints: keys are project names.
        "$.manifest.compilation_options_fingerprints",
        // SnapshotCompleteness.ActiveTfms: keys are target framework monikers.
        "$.manifest.completeness.active_tfms",
        // StatusHandler.ManifestJson metadata_reference_counts (--detail=references off): keys are project names.
        "$.manifest.metadata_reference_counts",
        // SnapshotManifest.ProjectDocuments: keys are project names.
        "$.manifest.project_documents",
        // SnapshotManifest.ProjectGraph: keys are project names.
        "$.manifest.project_graph",
        // SnapshotManifest.TargetFrameworks: keys are project names.
        "$.manifest.target_frameworks",
        // StatusHandler --detail=documents: SnapshotManifest.DocumentVersions, keys are document paths.
        "$.manifest.document_versions",
        // StatusHandler --detail=references: SnapshotManifest.MetadataReferenceIdentities, keys are project names.
        "$.manifest.metadata_reference_identities",
        // MCP lurp_status nests the manifest under "$.detail.manifest"; the CLI/MCP layout difference is review item B17, not fixed here.
        // SnapshotManifest.TargetFrameworks: keys are project names.
        "$.detail.manifest.target_frameworks",
        // SnapshotManifest.ProjectGraph: keys are project names.
        "$.detail.manifest.project_graph",
        // SnapshotManifest.CompilationOptionsFingerprints: keys are project names ("Core", "App").
        "$.detail.manifest.compilation_options_fingerprints",
        // SnapshotManifest.ProjectDocuments: keys are project names.
        "$.detail.manifest.project_documents",
        // SnapshotCompleteness.ActiveTfms: keys are target framework monikers ("net8.0").
        "$.detail.manifest.completeness.active_tfms",
        // StatusHandler.ManifestJson metadata_reference_counts (--detail=references off): keys are project names.
        "$.detail.manifest.metadata_reference_counts",
        // SnapshotCompleteness.ActiveTfms (capsule's completeness): keys are target framework monikers.
        "$.capsule.completeness.active_tfms"
    };

    private readonly HashSet<string> coveredModes = new(StringComparer.Ordinal);

    // MCP tool names exercised by the snapshot (T9): the surface is
    // "mcp:" + the name without the "lurp_" prefix. T10 checks this set against the
    // tools found by reflection.
    private readonly HashSet<string> coveredTools = new(StringComparer.Ordinal);

    // Modes that are deliberately never run through RunMode, with the reason (T10). Every
    // Program.ModeRegistry name must be in coveredModes or here. A mode in both sets is a
    // problem: an excluded mode that was run means the exclusion is stale (R8). Only
    // `serve` is excluded; get-source is covered as a text surface.
    private static readonly Dictionary<string, string> ExcludedModes = new(StringComparer.Ordinal)
    {
        ["serve"] = "MCP stdio transport; its payloads are the MCP tool payloads."
    };

    private static readonly string[] CallShapesAppProject = ["CallShapes.App"];

    // Key-pattern violations collected while walking every payload (T6). The test
    // reports all of them at once.
    private readonly List<string> keyViolations = new();

    // One accumulator for every payload (T5): surface -> path -> set of types.
    // Every run of the same surface unions its shapes here, so a surface that is
    // exercised twice (for example pin-snapshot) cannot emit duplicate paths.
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> surfaceShapes = new(StringComparer.Ordinal);

    [Fact]
    public async Task OutputContract_MatchesSnapshot()
    {
        // --- index export (surface: cli:index:export) ---
        // index runs inside the fixture, through the same ModeRegistry + CliFlagValidation
        // path every other mode uses (fixture.IndexModeRan). Record it here, beside the
        // export surface it produced, and only once that surface was captured.
        ExtractJsonShape("cli:index:export", ReadFile(fixture.ExportAPath));
        if (fixture.IndexModeRan && surfaceShapes.ContainsKey("cli:index:export"))
            coveredModes.Add("index");

        // --- write modes, in the order the reads depend on them ---
        // annotate three times; each annotation_id is read from that call's JSON stdout,
        // never from the store. "retract-cli" is retracted below, "retract-mcp" is left
        // for the MCP retract (T9), and "keep" stays attached.
        var annotationIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var value in new[] { "keep", "retract-cli", "retract-mcp" })
            annotationIds[value] = await Annotate(value);

        await Annotate("get-symbol-target", fixture.CallerSymbolId);
        await Annotate("constraint-on-caller", fixture.CallerSymbolId, "constraint");

        // get-annotations while all three annotations exist.
        ExtractJsonShape("cli:get-annotations:json",
            await RunMode("get-annotations", $"--symbol={fixture.MethodGroupSymbolId}", "--output=json"));
        ExtractJsonlShape("get-annotations",
            await RunMode("get-annotations", $"--symbol={fixture.MethodGroupSymbolId}", "--output=jsonl"));

        ExtractJsonShape("cli:retract-annotation:json",
            await RunMode("retract-annotation", $"--annotation-id={annotationIds["retract-cli"]}"));

        // pin-snapshot: set then clear. --json and --output=json are an alias pair. pin-snapshot
        // changes state, so each spelling runs a full set/clear cycle from the same unpinned
        // state; otherwise previous_pinned_snapshot_id differs between the two spellings.
        var setJson = await RunMode("pin-snapshot", $"--snapshot={fixture.SnapshotB}", "--json");
        var clearJson = await RunMode("pin-snapshot", "--clear", "--json");
        var setOutput = await RunMode("pin-snapshot", $"--snapshot={fixture.SnapshotB}", "--output=json");
        var clearOutput = await RunMode("pin-snapshot", "--clear", "--output=json");
        ExtractJsonAliasPair("cli:pin-snapshot:json", "set", setJson, setOutput);
        ExtractJsonAliasPair("cli:pin-snapshot:json", "clear", clearJson, clearOutput);

        // --- read modes ---
        // get-source prints raw text, not JSON (T4).
        ExtractTextSurface("cli:get-source:text",
            await RunMode("get-source", $"--document={fixture.DocPath}"));

        ExtractJsonShape("cli:outline:json",
            await RunMode("outline", $"--document={fixture.DocPath}", "--output=json"));

        // get-symbol has no --output= flag; --view= selects the payload. metadata is JSON;
        // the other five views print raw source, so each records a text surface. A type
        // symbol's parent is its namespace (no source), so containing-type uses the member
        // symbol; surrounding uses the type symbol (T7).
        ExtractJsonShape("cli:get-symbol:json",
            await RunMode("get-symbol", $"--symbol={fixture.CallerSymbolId}", "--view=metadata"));
        ExtractTextSurface("cli:get-symbol:signature:text",
            await RunMode("get-symbol", $"--symbol={fixture.CallerSymbolId}", "--view=signature"));
        ExtractTextSurface("cli:get-symbol:body:text",
            await RunMode("get-symbol", $"--symbol={fixture.CallerSymbolId}", "--view=body"));
        ExtractTextSurface("cli:get-symbol:declaration:text",
            await RunMode("get-symbol", $"--symbol={fixture.CallerSymbolId}", "--view=declaration"));
        ExtractTextSurface("cli:get-symbol:containing-type:text",
            await RunMode("get-symbol", $"--symbol={fixture.CallerSymbolId}", "--view=containing-type"));
        ExtractTextSurface("cli:get-symbol:surrounding:text",
            await RunMode("get-symbol", $"--symbol={fixture.TypeSymbolId}", "--view=surrounding"));

        // search: one call per --type= value, JSON and JSONL, each its own variant.
        foreach (var type in new[] { "all", "source", "symbol" })
        {
            ExtractJsonShape("cli:search:json",
                await RunMode("search", "--query=CoreApi", $"--type={type}", "--output=json"),
                $"type={type}");
            ExtractJsonlShape("search",
                await RunMode("search", "--query=CoreApi", $"--type={type}", "--output=jsonl"),
                $"type={type}");
        }

        ExtractJsonShape("cli:find-symbol:json",
            await RunMode("find-symbol", $"--symbol={fixture.CallerSymbolId}", "--output=json"));

        ExtractJsonShape("cli:navigate:json",
            await RunMode("navigate", $"--file={fixture.NavDocPath}", $"--line={fixture.NavLine}"));

        ExtractJsonShape("cli:diff:json",
            await RunMode("diff", $"--from-snapshot={fixture.SnapshotA}", $"--to-snapshot={fixture.SnapshotB}"));

        // Impact runs on DiffSymbolId with --direction=upstream: DiffSymbolId is new in
        // snapshot B, so it has a persisted semantic change, and upstream reaches
        // OutputContractCaller, so symbols and groups stay filled.
        ExtractJsonShape("cli:impact:json",
            await RunMode("impact", $"--symbol={fixture.DiffSymbolId}", "--direction=upstream", "--output=json"));

        // context: a capsule for each anchor, then a real tier page and its cursor page.
        // JSON carries two payload variants (capsule and tier); JSONL is tier only, so its
        // surface stays unchanged.
        foreach (var anchor in fixture.AnchorSymbolIds)
            ExtractJsonShape("cli:context:json",
                await RunMode("context", $"--symbol={anchor}", "--affected-project=CallShapes.App", "--output=json"),
                "capsule");

        // T7: find a real continuation cursor; no cursor is invented. The tier page is
        // run first-page and cursor-page in both output modes. --output=jsonl is accepted
        // only when --tier= is present (ParseOutputMode allowJsonl gate), which it is.
        var (cursorSymbol, cursorTier, cursor) = await FindContextCursor();
        ExtractJsonShape("cli:context:json",
            await RunMode("context", $"--symbol={cursorSymbol}", $"--tier={cursorTier}", "--tier-limit=1", "--output=json"),
            "tier");
        ExtractJsonShape("cli:context:json",
            await RunMode("context", $"--symbol={cursorSymbol}", $"--tier={cursorTier}", "--tier-limit=1", $"--cursor={cursor}", "--output=json"),
            "tier");
        ExtractJsonlShape("context",
            await RunMode("context", $"--symbol={cursorSymbol}", $"--tier={cursorTier}", "--tier-limit=1", "--output=jsonl"));
        ExtractJsonlShape("context",
            await RunMode("context", $"--symbol={cursorSymbol}", $"--tier={cursorTier}", "--tier-limit=1", $"--cursor={cursor}", "--output=jsonl"));

        ExtractJsonShape("cli:diagnostics:json",
            await RunMode("diagnostics", $"--document={fixture.DocPath}", "--output=json"));

        ExtractJsonShape("cli:grep:json",
            await RunMode("grep", "--query=CoreApi.", "--output=json"));

        ExtractJsonShape("cli:dead-candidates:json",
            await RunMode("dead-candidates", "--output=json"));

        // --- read modes: the remaining JSONL variants (T8) ---
        ExtractJsonlShape("find-symbol",
            await RunMode("find-symbol", $"--symbol={fixture.CallerSymbolId}", "--output=jsonl"));
        ExtractJsonlShape("impact",
            await RunMode("impact", $"--symbol={fixture.DiffSymbolId}", "--direction=upstream", "--output=jsonl"));
        ExtractJsonlShape("diagnostics",
            await RunMode("diagnostics", $"--document={fixture.DocPath}", "--output=jsonl"));
        ExtractJsonlShape("grep",
            await RunMode("grep", "--query=CoreApi.", "--output=jsonl"));
        ExtractJsonlShape("dead-candidates",
            await RunMode("dead-candidates", "--output=jsonl"));
        ExtractJsonlShape("outline",
            await RunMode("outline", $"--document={fixture.DocPath}", "--output=jsonl"));

        // timings then status last (T8), so status surfaces see the final database.
        ExtractJsonShape("cli:timings:json",
            await RunMode("timings", "--json"));
        // status: --json and --output=json are an alias pair for the default payload; each
        // --detail= value and the snapshot-only path are their own variants.
        ExtractJsonAliasPair("cli:status:json", "default",
            await RunMode("status", "--json"),
            await RunMode("status", "--output=json"));
        ExtractJsonShape("cli:status:json",
            await RunMode("status", "--output=json", "--detail=documents"), "detail=documents");
        ExtractJsonShape("cli:status:json",
            await RunMode("status", "--output=json", "--detail=references"), "detail=references");
        ExtractJsonShape("cli:status:json",
            await RunMode("status", "--output=json", "--detail=completeness"), "detail=completeness");
        ExtractJsonShape("cli:status:json",
            await RunMode("status", "--output=json", "--detail=all"), "detail=all");
        // Snapshot-only path (ReportSnapshotOnly): no --solution=.
        ExtractJsonShape("cli:status:json",
            await RunModeWithoutSolution("status", "--output=json"), "snapshot-only");

        // --- MCP tools (T9) ---
        // One session for every tool call; it pins to the latest snapshot, which is
        // snapshot B (indexed last in the fixture). Every read below answers from B.
        await using var session = McpSessionContext.Create(new[] { $"--solution={fixture.SolutionPath}", $"--output-dir={fixture.OutputDir}" });
        Assert.Equal(fixture.SnapshotB, session.PinnedSnapshotId);

        // lurp_context: a capsule for each anchor, then a real tier first page and
        // its cursor page (T7). capsule and tier are separate variants.
        var contextTool = new ContextTool(session);
        foreach (var anchor in fixture.AnchorSymbolIds)
            ExtractToolShape("lurp_context", () => contextTool.LurpContext(symbol: anchor, affected_project: CallShapesAppProject), "capsule");

        var (mcpCursorSymbol, mcpCursorTier, mcpCursor) = FindMcpContextCursor(session);
        ExtractToolShape("lurp_context", () => contextTool.LurpContext(symbol: mcpCursorSymbol, tier: mcpCursorTier, tier_limit: 1), "tier");
        ExtractToolShape("lurp_context", () => contextTool.LurpContext(symbol: mcpCursorSymbol, tier: mcpCursorTier, tier_limit: 1, cursor: mcpCursor), "tier");

        // lurp_get_source: without and with the outline payload, as separate variants.
        var getSourceTool = new GetSourceTool(session);
        ExtractToolShape("lurp_get_source", () => getSourceTool.LurpGetSource(document: fixture.DocPath), "outline=false");
        ExtractToolShape("lurp_get_source", () => getSourceTool.LurpGetSource(document: fixture.DocPath, outline: true), "outline=true");

        ExtractToolShape("lurp_outline", () => new OutlineTool(session).LurpOutline(document: fixture.DocPath));
        ExtractToolShape("lurp_navigate", () => new NavigateTool(session).LurpNavigate(file: fixture.NavDocPath, line: fixture.NavLine));
        ExtractToolShape("lurp_find_symbol", () => new FindSymbolTool(session).LurpFindSymbol(symbol: fixture.CallerSymbolId));

        // lurp_search: one call per type value, each its own variant. Same query as the
        // CLI search (R9), so MCP and CLI are exercised with the same input.
        var searchTool = new SearchTool(session);
        foreach (var type in new[] { "all", "source", "symbol" })
            ExtractToolShape("lurp_search", () => searchTool.LurpSearch(query: "CoreApi", type: type), $"type={type}");

        ExtractToolShape("lurp_impact", () => new ImpactTool(session).LurpImpact(symbol: fixture.DiffSymbolId, direction: "upstream"));
        ExtractToolShape("lurp_diff", () => new DiffTool(session).LurpDiff(from_snapshot: fixture.SnapshotA, to_snapshot: fixture.SnapshotB));

        // lurp_get_symbol: every view the tool accepts (summary, source, all).
        var getSymbolTool = new GetSymbolTool(session);
        foreach (var view in new[] { "summary", "source", "all" })
            ExtractToolShape("lurp_get_symbol", () => getSymbolTool.LurpGetSymbol(symbol: fixture.CallerSymbolId, view: view), $"view={view}");

        ExtractToolShape("lurp_get_annotations", () => new AnnotationsTool(session).LurpGetAnnotations(symbol: fixture.MethodGroupSymbolId));
        ExtractToolShape("lurp_diagnostics", () => new DiagnosticsTool(session).LurpDiagnostics(document: fixture.DocPath));
        ExtractToolShape("lurp_grep", () => new GrepTool(session).LurpGrep(query: "CoreApi."));

        // lurp_status: every sections value, then the full MSBuild freshness check.
        var statusTool = new StatusTool(session);
        foreach (var sections in new[] { "freshness", "manifest", "references", "completeness", "all" })
            await ExtractToolShapeAsync("lurp_status", () => statusTool.LurpStatus(sections: sections), $"sections={sections}");
        await ExtractToolShapeAsync("lurp_status", () => statusTool.LurpStatus(full: true), "full");

        ExtractToolShape("lurp_timings", () => new TimingsTool(session).LurpTimings());
        ExtractToolShape("lurp_dead_candidates", () => new DeadCandidatesTool(session).LurpDeadCandidates());

        // --- MCP write tools (T9) ---
        // lurp_retract_annotation: retract the "retract-mcp" annotation left by the
        // CLI annotate calls above ("retract-cli" was already retracted over the CLI).
        ExtractToolShape("lurp_retract_annotation", () => new RetractAnnotationTool(session).LurpRetractAnnotation(annotation_id: annotationIds["retract-mcp"]));

        // lurp_index needs a changed tree to produce a new snapshot: the snapshot id
        // is content-addressed, so an unchanged full re-index reuses B. Add one
        // member (the run sequence does the same for snapshot B) before indexing.
        var indexCorePath = Path.Combine(Path.GetDirectoryName(fixture.SolutionPath)!, "Core", "CoreApi.cs");
        var indexCoreContent = File.ReadAllText(indexCorePath);
        indexCoreContent = indexCoreContent.Insert(indexCoreContent.LastIndexOf('}'), "    public static int McpIndexSentinel() => 7;\n");
        File.WriteAllText(indexCorePath, indexCoreContent);
        Assert.Contains("McpIndexSentinel", File.ReadAllText(indexCorePath));

        await RunIndexToolToCompletion(new IndexTool(session, new McpIndexSessionState()));

        // lurp_refresh, after lurp_index: without ack it reports the new snapshot
        // without moving the pin; the ack advances the pin.
        var refreshTool = new RefreshTool(session);
        var refreshNoAck = refreshTool.LurpRefresh();
        string refreshTarget;
        using (var noAckDoc = JsonDocument.Parse(refreshNoAck))
        {
            Assert.True(noAckDoc.RootElement.GetProperty("requires_ack").GetBoolean(),
                "lurp_refresh without ack must require an ack after a completed lurp_index.");
            refreshTarget = noAckDoc.RootElement.GetProperty("new_snapshot_id").GetString()!;
        }
        ExtractToolShape("lurp_refresh", () => refreshNoAck, "no-ack");
        ExtractToolShape("lurp_refresh", () => refreshTool.LurpRefresh(ack: refreshTarget), "ack");

        // --- MCP error envelope (T9) ---
        // The ModelContextProtocol SDK, not Lurp, serializes the JSON-RPC error
        // object from a thrown McpProtocolException, so the test reconstructs
        // {code, message} from the exception. This is a real failing call.
        try
        {
            new FindSymbolTool(session).LurpFindSymbol(symbol: "T:Does.Not.Exist|Nope");
            Assert.Fail("Expected McpProtocolException for an unknown symbol");
        }
        catch (McpProtocolException ex)
        {
            var errorJson = JsonSerializer.Serialize(new { code = (int)ex.ErrorCode, message = ex.Message }, LurpJsonOptions.Indented);
            ExtractJsonShape("mcp:error", errorJson);
        }

        // --- compare with snapshot ---
        EnforceCompletenessThenCompare();
    }

    // --- helpers ---

    // Runs --mode=annotate once for value, records its JSON shape, and returns the
    // annotation_id from that call's stdout (never from the store). The optional symbolId
    // defaults to the fixture's MethodGroupSymbolId and kind defaults to "output-contract".
    private async Task<long> Annotate(string value, string? symbolId = null, string kind = "output-contract")
    {
        var run = await RunMode("annotate", $"--symbol={symbolId ?? fixture.MethodGroupSymbolId}",
            $"--annotation-kind={kind}", $"--value={value}");
        ExtractJsonShape("cli:annotate:json", run);
        using var doc = JsonDocument.Parse(run.Stdout);
        return doc.RootElement.GetProperty("annotation_id").GetInt64();
    }

    // Runs one CLI mode through the registry: resolves the entry by name, applies the
    // shared --output-dir/--solution defaults unless the call already sets them, validates
    // the flags the way Program.Main does, and captures stdout. A CliExitException is
    // rethrown as InvalidOperationException with the mode, args and captured stdout, so a
    // failed call can never pass as an empty payload.
    private Task<(string Args, string Stdout)> RunMode(string mode, params string[] args)
    {
        return RunModeCore(mode, true, args);
    }

    // The status snapshot-only path (ReportSnapshotOnly) is selected by the absence of
    // --solution=; this variant omits the default so that path is exercised.
    private Task<(string Args, string Stdout)> RunModeWithoutSolution(string mode, params string[] args)
    {
        return RunModeCore(mode, false, args);
    }

    private async Task<(string Args, string Stdout)> RunModeCore(string mode, bool includeSolution, string[] args)
    {
        var entry = Program.ModeRegistry.FirstOrDefault(e => e.Name == mode)
            ?? throw new InvalidOperationException($"No Program.ModeRegistry entry named '{mode}'.");

        var fullArgs = new List<string>(args);
        if (!fullArgs.Any(a => a.StartsWith("--output-dir=", StringComparison.Ordinal)))
            fullArgs.Add($"--output-dir={fixture.OutputDir}");
        if (includeSolution && !fullArgs.Any(a => a.StartsWith("--solution=", StringComparison.Ordinal)))
            fullArgs.Add($"--solution={fixture.SolutionPath}");

        var stdout = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(stdout);
            CliFlagValidation.Validate(entry, fullArgs.ToArray());
            await entry.Handler(fullArgs.ToArray());
        }
        catch (CliExitException ex)
        {
            throw new InvalidOperationException(
                $"Mode '{mode}' threw CliExitException for args [{string.Join(", ", fullArgs)}]. Captured stdout: {stdout}", ex);
        }
        finally
        {
            Console.SetOut(original);
        }

        coveredModes.Add(mode);
        return ($"{mode} {string.Join(" ", fullArgs)}", stdout.ToString());
    }

    // T7: pick a real continuation cursor instead of inventing one. Each anchor is
    // probed across every tier; the first one-item page that reports next_cursor
    // wins. Failing here means no tier has a second page, so the --cursor path would
    // otherwise be silently uncovered.
    private async Task<(string Symbol, string Tier, string Cursor)> FindContextCursor()
    {
        foreach (var symbol in fixture.AnchorSymbolIds)
            foreach (var tier in ContextAssembler.TierNames)
            {
                var run = await RunMode("context", $"--symbol={symbol}", $"--tier={tier}", "--tier-limit=1", "--output=json");
                var cursor = ReadStringProperty(run.Stdout, "next_cursor");
                if (cursor is not null)
                    return (symbol, tier, cursor);
            }

        throw new InvalidOperationException(
            "No context tier produced a next_cursor. T7 needs a tier with a second page to cover the cursor path.");
    }

    private (string Symbol, string Tier, string Cursor) FindMcpContextCursor(McpSessionContext session)
    {
        foreach (var symbol in fixture.AnchorSymbolIds)
            foreach (var tier in ContextAssembler.TierNames)
            {
                var json = new ContextTool(session).LurpContext(symbol: symbol, tier: tier, tier_limit: 1);
                var cursor = ReadTierPageStringProperty(json, "next_cursor");
                if (cursor is not null)
                    return (symbol, tier, cursor);
            }

        throw new InvalidOperationException(
            "No MCP context tier produced a next_cursor. T7 needs a tier with a second page to cover the cursor path.");
    }

    private static string? ReadStringProperty(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ReadTierPageStringProperty(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("tier_page", out var page)
               && page.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private void ExtractJsonShape(string surface, string json)
    {
        ExtractJsonShape(surface, string.Empty, json);
    }

    private void ExtractJsonShape(string surface, string args, string json)
    {
        Accumulate(surface, ParseJsonShapes(surface, args, json));
    }

    // Parses one JSON payload into a path -> type-set map without recording it. The same
    // empty-output and invalid-JSON checks guard ExtractJsonShape and the alias-pair helper.
    private Dictionary<string, HashSet<string>> ParseJsonShapes(string surface, string args, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                $"JSON surface '{surface}' produced empty output. Args: [{args}]. " +
                $"Stdout (first 500 chars):\n{First500(json)}");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"JSON surface '{surface}' is not valid JSON. Args: [{args}]. " +
                $"Stdout (first 500 chars):\n{First500(json)}", ex);
        }

        using (doc)
        {
            return JsonShape.Extract(doc.RootElement, MapPaths, surface, keyViolations);
        }
    }

    // Builds a surface name from its base and optional variant (R5). A run of a surface
    // merges only with runs of the same variant, so the variant is part of the key. This is
    // the one place a surface name is built.
    private static string Surface(string baseSurface, string? variant)
    {
        return variant is null ? baseSurface : $"{baseSurface}[{variant}]";
    }

    // Runs both spellings of one payload (an alias pair), checks that their extracted
    // shapes match (same path set, same type set per path), and records them under one
    // variant surface. A difference fails with the surface and both arg lists (R5).
    private void ExtractJsonAliasPair(
        string baseSurface,
        string variant,
        (string Args, string Stdout) first,
        (string Args, string Stdout) second)
    {
        var surface = Surface(baseSurface, variant);
        var firstShapes = ParseJsonShapes(surface, first.Args, first.Stdout);
        var secondShapes = ParseJsonShapes(surface, second.Args, second.Stdout);

        var differences = new List<string>();
        foreach (var path in firstShapes.Keys
                     .Concat(secondShapes.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            firstShapes.TryGetValue(path, out var firstTypes);
            secondShapes.TryGetValue(path, out var secondTypes);
            if (firstTypes is null)
                differences.Add($"{path}: only in [{second.Args}] ({JsonShape.FormatTypes(secondTypes!)})");
            else if (secondTypes is null)
                differences.Add($"{path}: only in [{first.Args}] ({JsonShape.FormatTypes(firstTypes)})");
            else if (!firstTypes.SetEquals(secondTypes))
                differences.Add(
                    $"{path}: {JsonShape.FormatTypes(firstTypes)} in [{first.Args}] vs {JsonShape.FormatTypes(secondTypes)} in [{second.Args}]");
        }

        Assert.True(differences.Count == 0,
            $"Alias pair for surface '{surface}' differs:\n  {string.Join("\n  ", differences)}\n" +
            $"First args:  [{first.Args}]\nSecond args: [{second.Args}]");

        Accumulate(surface, firstShapes);
    }

    private void ExtractJsonShape(string baseSurface, (string Args, string Stdout) run, string? variant = null)
    {
        ExtractJsonShape(Surface(baseSurface, variant), run.Args, run.Stdout);
    }

    private void ExtractJsonlShape(string mode, (string Args, string Stdout) run, string? variant = null)
    {
        var lines = run.Stdout.Split('\n')
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(x => !string.IsNullOrWhiteSpace(x.Line))
            .ToList();

        if (lines.Count == 0)
        {
            throw new InvalidOperationException(
                $"JSONL surface 'cli:{mode}:jsonl' produced no non-blank lines. Args: [{run.Args}]. " +
                $"Stdout (first 500 chars):\n{First500(run.Stdout)}");
        }

        foreach (var (line, number) in lines)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"JSONL surface 'cli:{mode}:jsonl' line {number} is not valid JSON. " +
                    $"Args: [{run.Args}]. Line: {line}", ex);
            }

            using (doc)
            {
                var root = doc.RootElement;
                // A JSONL line with no string "type" field is surface ":jsonl:(untyped)",
                // not ":jsonl:unknown", so a future regression (T13) shows as its own line.
                var type = root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                    ? typeEl.GetString() ?? "(untyped)"
                    : "(untyped)";

                var surface = Surface($"cli:{mode}:jsonl:{type}", variant);
                Accumulate(surface, JsonShape.Extract(root, MapPaths, surface, keyViolations));
            }
        }
    }

    private void ExtractTextSurface(string surface, (string Args, string Stdout) run)
    {
        Assert.False(string.IsNullOrWhiteSpace(run.Stdout),
            $"Expected non-empty text output for surface '{surface}'. Args: [{run.Args}].");
        Assert.False(LooksLikeJson(run.Stdout),
            $"Expected non-JSON text output for surface '{surface}'. Args: [{run.Args}]. " +
            $"Stdout (first 500 chars):\n{First500(run.Stdout)}");

        Accumulate(surface, new Dictionary<string, HashSet<string>> { ["$"] = new HashSet<string> { "text" } });
    }

    // Records one MCP tool call (T9): the tool name goes into coveredTools (always the
    // bare name), and the payload is reduced to "mcp:<name without lurp_>" with an
    // optional variant (R5). The surface is derived from the tool name so the two cannot drift.
    private void ExtractToolShape(string name, Func<string> call, string? variant = null)
    {
        coveredTools.Add(name);
        ExtractJsonShape(McpSurface(name, variant), call());
    }

    private async Task ExtractToolShapeAsync(string name, Func<Task<string>> call, string? variant = null)
    {
        coveredTools.Add(name);
        ExtractJsonShape(McpSurface(name, variant), await call());
    }

    private static string McpSurface(string toolName, string? variant = null)
    {
        const string prefix = "lurp_";
        var surface = $"mcp:{(toolName.StartsWith(prefix, StringComparison.Ordinal) ? toolName[prefix.Length..] : toolName)}";
        return Surface(surface, variant);
    }

    // T9/R6: start a full MCP index and poll until it leaves "running". The start payload
    // records as mcp:index[start] and the first non-running poll as mcp:index[final]; the
    // intermediate polls are not recorded, because their nulls and progress vary from run
    // to run. The poll loop and timeout follow tests/Mcp/McpIndexTests.cs:23
    // (WaitForCompletionAsync).
    private async Task RunIndexToolToCompletion(IndexTool tool)
    {
        const string name = "lurp_index";
        coveredTools.Add(name);

        var startJson = tool.LurpIndex(strategy: "full");
        ExtractJsonShape(McpSurface(name, "start"), startJson);

        string operationId;
        using (var startDoc = JsonDocument.Parse(startJson))
            operationId = startDoc.RootElement.GetProperty("operation_id").GetString()!;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 120000)
        {
            var json = tool.LurpIndex(operation_id: operationId);

            using (var doc = JsonDocument.Parse(json))
            {
                var status = doc.RootElement.GetProperty("status").GetString();
                if (status != "running")
                {
                    Assert.Equal("completed", status);
                    ExtractJsonShape(McpSurface(name, "final"), json);
                    return;
                }
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"operation {operationId} did not finish within 120000ms");
    }

    // Unions one payload's path -> type-set map into the per-surface accumulator (T5),
    // so repeated runs of a surface contribute a single line per path.
    private void Accumulate(string surface, Dictionary<string, HashSet<string>> shapes)
    {
        if (!surfaceShapes.TryGetValue(surface, out var existing))
        {
            existing = new Dictionary<string, HashSet<string>>();
            surfaceShapes[surface] = existing;
        }

        foreach (var (path, types) in shapes)
        {
            if (!existing.TryGetValue(path, out var set))
            {
                set = new HashSet<string>();
                existing[path] = set;
            }
            set.UnionWith(types);
        }
    }

    // Emits one "surface path types" line per surface+path, types sorted and joined with
    // "|", then sorts every line ordinally (T5).
    private List<string> FormatSurfaces()
    {
        return surfaceShapes
            .SelectMany(kv => JsonShape.Format(kv.Key, kv.Value))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();
    }

    private static bool LooksLikeJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string First500(string text)
    {
        return text.Length <= 500 ? text : text[..500];
    }

    // T10: one pass that collects every completeness and key problem, writes the .actual
    // file, and only then compares with the committed snapshot. Failing on the problems
    // first keeps a single run from reporting a shape diff that is really a missing mode
    // or an unexercised tool.
    private void EnforceCompletenessThenCompare()
    {
        var problems = new List<string>();

        // Modes: every registry mode must be covered or explicitly excluded; every
        // covered name must exist in the registry.
        var registryModes = Program.ModeRegistry.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var mode in registryModes)
            if (!coveredModes.Contains(mode) && !ExcludedModes.ContainsKey(mode))
                problems.Add($"mode '{mode}' was neither run nor excluded");
        foreach (var mode in coveredModes)
            if (!registryModes.Contains(mode))
                problems.Add($"covered mode '{mode}' is not in Program.ModeRegistry");
        // A stale exclusion: the mode is listed as excluded but was actually run (R8).
        foreach (var mode in coveredModes)
            if (ExcludedModes.ContainsKey(mode))
                problems.Add($"excluded mode '{mode}' was run");

        // Tools: the reflection name set and the covered set must match exactly.
        var reflectedTools = GetMcpToolNames().ToHashSet(StringComparer.Ordinal);
        foreach (var tool in reflectedTools.Except(coveredTools).OrderBy(t => t, StringComparer.Ordinal))
            problems.Add($"MCP tool '{tool}' was not exercised");
        foreach (var tool in coveredTools.Except(reflectedTools).OrderBy(t => t, StringComparer.Ordinal))
            problems.Add($"covered MCP tool '{tool}' does not exist");

        // Keys: every schema-invalid key collected while walking the payloads (T6).
        problems.AddRange(keyViolations);

        // Always write the actual file first, so a single run shows the whole picture.
        var snapshotDir = SnapshotDirectory();
        var snapshotPath = Path.Combine(snapshotDir, "output-contract.txt");
        var actualPath = Path.Combine(snapshotDir, "output-contract.actual.txt");
        var actual = string.Join("\n", FormatSurfaces().Select(l => l.TrimEnd())) + "\n";
        File.WriteAllText(actualPath, actual);

        if (problems.Count > 0)
        {
            var completeness = new System.Text.StringBuilder();
            completeness.Append("Output-contract completeness failed with ");
            completeness.Append(problems.Count);
            completeness.AppendLine(" problem(s):");
            foreach (var problem in problems) completeness.AppendLine("  " + problem);
            completeness.AppendLine();
            completeness.AppendLine("Actual output written to: " + actualPath);
            Assert.Fail(completeness.ToString());
        }

        if (!File.Exists(snapshotPath))
        {
            Assert.Fail($"Snapshot file does not exist. Review {actualPath}, copy it to " +
                        $"{snapshotPath}, and re-run.");
        }

        var expected = File.ReadAllText(snapshotPath);
        if (expected == actual)
            return; // match

        // Produce a diff for the failure message. An empty expected file is a plain
        // mismatch (every line is "only in actual"), not a crash.
        var expectedLines = expected.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        var actualLines = actual.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

        var onlyInExpected = expectedLines.Except(actualLines).ToList();
        var onlyInActual = actualLines.Except(expectedLines).ToList();

        var message = new System.Text.StringBuilder();
        message.AppendLine("Output-contract snapshot mismatch. Review the diff:");
        message.AppendLine();
        if (onlyInExpected.Count > 0)
        {
            message.AppendLine("--- only in expected (removed from actual) ---");
            foreach (var l in onlyInExpected) message.AppendLine("  " + l);
        }
        if (onlyInActual.Count > 0)
        {
            message.AppendLine("--- only in actual (added) ---");
            foreach (var l in onlyInActual) message.AppendLine("  " + l);
        }
        message.AppendLine();
        message.AppendLine("Actual output written to: " + actualPath);
        message.AppendLine("Review the diff. Copy the .actual file over output-contract.txt. " +
                           "Bump CliMcpContractVersion if the change is breaking (VERSIONING.md).");

        Assert.Fail(message.ToString());
    }

    // tests/Snapshots, resolved from the fixture root exactly as T10 specifies.
    private static string SnapshotDirectory()
    {
        var directory = Path.GetFullPath(Path.Combine(GroundTruthFixture.LocateFixtureRoot(), "..", "..", "Snapshots"));
        Assert.True(Directory.Exists(directory), $"Snapshot directory does not exist: {directory}");
        return directory;
    }

    // T10: the same reflection CliMcpContractSnapshotTests.GetMcpSurface uses, reduced to
    // the MCP-facing tool names ([McpServerToolType] types, [McpServerTool] methods, attr.Name).
    private static IEnumerable<string> GetMcpToolNames()
    {
        var asm = typeof(Program).Assembly;
        return asm.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!));
    }

    private static string ReadFile(string path)
    {
        return File.ReadAllText(path);
    }
}

public sealed class OutputContractFixture : IAsyncLifetime
{
    public string FixtureRoot { get; private set; } = null!;
    public string TempDir { get; private set; } = null!;
    public string SolutionPath { get; private set; } = null!;
    public string OutputDir { get; private set; } = null!;
    public string DbPath { get; private set; } = null!;
    public string ExportAPath { get; private set; } = null!;
    public string SnapshotA { get; private set; } = null!;
    public string SnapshotB { get; private set; } = null!;

    // T10: true once the fixture's indexing has run through Program.ModeRegistry (not
    // directly through IndexHandler.Run). The test records "index" as a covered mode only
    // when this is set and the export surface was captured.
    public bool IndexModeRan { get; private set; }

    // T7: real anchors, resolved by doc-comment-id prefix, instead of arbitrary
    // snapshot entries. Each one is the input that fills a mode's array.
    public string MethodGroupSymbolId { get; private set; } = null!;
    public string ContractSymbolId { get; private set; } = null!;
    public string CallerSymbolId { get; private set; } = null!;
    public string DiffSymbolId { get; private set; } = null!;
    public string TypeSymbolId { get; private set; } = null!;
    public string ContractImplSymbolId { get; private set; } = null!;
    public string DocPath { get; private set; } = null!;
    public string NavDocPath { get; private set; } = null!;
    public int NavLine { get; private set; }

    // Every anchor, in the order the cursor search probes them.
    public IReadOnlyList<string> AnchorSymbolIds =>
        [MethodGroupSymbolId, ContractSymbolId, CallerSymbolId, DiffSymbolId, TypeSymbolId, ContractImplSymbolId];

    public Task InitializeAsync()
    {
        if (!GroundTruthFixture.TryRegisterMSBuild())
            throw new InvalidOperationException("MSBuildLocator could not register an SDK; ground-truth tests need one.");

        FixtureRoot = GroundTruthFixture.LocateFixtureRoot();
        TempDir = Path.Combine(Path.GetTempPath(), $"lurp-output-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDir);
        var treeDir = Path.Combine(TempDir, "tree");
        GroundTruthFixture.CopyTree(FixtureRoot, treeDir);
        SolutionPath = Path.Combine(treeDir, "CallShapes", "CallShapes.slnx");
        OutputDir = Path.Combine(TempDir, "out");
        Directory.CreateDirectory(OutputDir);
        DbPath = Path.Combine(OutputDir, "index.db");

        GroundTruthFixture.RestoreSolution(SolutionPath);

        // Step 1: full index with export -> snapshot A.
        ExportAPath = Path.Combine(TempDir, "export-a.json");
        RunIndex(new[] { $"--solution={SolutionPath}", $"--output-dir={OutputDir}", $"--output-json={ExportAPath}" });
        SnapshotA = GetLatestSnapshot();

        // Step 2: modify files, incremental index -> snapshot B.
        // The incremental indexer detects the content-hash change and writes a new snapshot.
        // Both files use a file-scoped namespace, so each new member goes before its
        // class's closing brace; appending after the brace would not compile.
        var shapeCallersPath = Path.Combine(treeDir, "CallShapes", "App", "ShapeCallers.cs");
        var shapeCallersContent = File.ReadAllText(shapeCallersPath);
        shapeCallersContent = shapeCallersContent.Insert(shapeCallersContent.LastIndexOf('}'),
            "\n    public static int OutputContractCaller()\n" +
            "    {\n" +
            "        int unusedLocal = 0; // CS0219 warning: gives diagnostics output real rows\n" +
            "        return CoreApi.OutputContractTarget();\n" +
            "    }\n");
        File.WriteAllText(shapeCallersPath, shapeCallersContent);
        Assert.Contains("OutputContractCaller", File.ReadAllText(shapeCallersPath));

        var coreApiPath = Path.Combine(treeDir, "CallShapes", "Core", "CoreApi.cs");
        var coreApiContent = File.ReadAllText(coreApiPath);
        coreApiContent = coreApiContent.Insert(coreApiContent.LastIndexOf('}'),
            "    public static int OutputContractTarget() => 23;\n");
        File.WriteAllText(coreApiPath, coreApiContent);
        Assert.Contains("OutputContractTarget", File.ReadAllText(coreApiPath));
        RunIndex(new[] { $"--solution={SolutionPath}", $"--output-dir={OutputDir}" });
        SnapshotB = GetLatestSnapshot();

        // Collect inputs for the read modes from snapshot B. T7: resolve each anchor
        // by doc-comment-id prefix so the store picks the exact declaration; an
        // arbitrary store entry leaves the mode's arrays empty.
        using (var store = OpenStore())
        {
            MethodGroupSymbolId = ResolveSymbolIdByPrefix(store, SnapshotB, "M:CallShapes.Core.CoreApi.MethodGroupTarget(System.Int32)|");
            ContractSymbolId = ResolveSymbolIdByPrefix(store, SnapshotB, "M:CallShapes.Core.IShapeContract.ContractValue|");
            CallerSymbolId = ResolveSymbolIdByPrefix(store, SnapshotB, "M:CallShapes.App.ShapeCallers.FromContract(CallShapes.Core.IShapeContract)|");
            DiffSymbolId = ResolveSymbolIdByPrefix(store, SnapshotB, "M:CallShapes.Core.CoreApi.OutputContractTarget|");
            TypeSymbolId = ResolveSymbolIdByPrefix(store, SnapshotB, "T:CallShapes.App.ShapeCallers|");
            ContractImplSymbolId = ResolveSymbolIdByPrefix(store, SnapshotB, "T:CallShapes.Core.ShapeContractImpl|");

            var documentVersions = store.GetDocumentVersionIdsByPath(SnapshotB);
            DocPath = documentVersions.Keys.FirstOrDefault(path => path.EndsWith("App/ShapeCallers.cs", StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"No document path in snapshot '{SnapshotB}' ends with 'App/ShapeCallers.cs'. " +
                    $"Paths: {string.Join(", ", documentVersions.Keys)}");

            // Navigate needs a line that actually contains a declaration. T7 names
            // FromContract; the copied tree already carries T2's insertion.
            NavDocPath = DocPath;
            var shapeCallersLines = File.ReadAllLines(shapeCallersPath);
            NavLine = Array.FindIndex(shapeCallersLines, line => line.Contains("public static int FromContract(", StringComparison.Ordinal)) + 1;
            if (NavLine < 1)
                throw new InvalidOperationException($"No 'public static int FromContract(' line in {shapeCallersPath}.");
        }

        return Task.CompletedTask;
    }

    // T7: every anchor must be a real symbol. A prefix that matches nothing is a
    // test bug, so fail with the same-type candidates instead of guessing.
    private static string ResolveSymbolIdByPrefix(SqliteIndexStore store, string snapshotId, string prefix)
    {
        var matches = store.GetSymbolIdsInSnapshot(snapshotId)
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (matches.Count == 0)
        {
            var docCommentId = prefix[..prefix.IndexOf('|')];
            var candidates = store.GetSymbolIdsInSnapshot(snapshotId)
                .Where(id => id.StartsWith(docCommentId, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Take(20)
                .ToList();
            throw new InvalidOperationException(
                $"No symbol id in snapshot '{snapshotId}' starts with '{prefix}'. " +
                $"Ids starting with '{docCommentId}': {string.Join(", ", candidates)}");
        }

        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"Prefix '{prefix}' matches {matches.Count} symbol ids in snapshot '{snapshotId}': " +
                $"{string.Join(", ", matches)}");

        return matches[0];
    }

    public Task DisposeAsync()
    {
        try
        {
            SqliteConnection.ClearAllPools();
        }
        catch { }
        try
        {
            if (Directory.Exists(TempDir))
                Directory.Delete(TempDir, true);
        }
        catch { }

        return Task.CompletedTask;
    }

    public SqliteIndexStore OpenStore()
    {
        var store = new SqliteIndexStore(DbPath);
        store.Open();
        store.RunMigrations();
        return store;
    }

    private void RunIndex(string[] args)
    {
        // T10: index must run through the same ModeRegistry + CliFlagValidation path every
        // other mode uses, so the snapshot proves the registry entry works, not just the
        // handler. IndexModeRan records that this path ran.
        var entry = Program.ModeRegistry.FirstOrDefault(e => e.Name == "index")
            ?? throw new InvalidOperationException("No Program.ModeRegistry entry named 'index'.");
        CliFlagValidation.Validate(entry, args);

        // Index progress goes to stdout; not part of the contract. Ignore it.
        var original = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            entry.Handler(args).GetAwaiter().GetResult();
        }
        finally
        {
            Console.SetOut(original);
        }

        IndexModeRan = true;
    }

    private string GetLatestSnapshot()
    {
        using var store = OpenStore();
        return store.GetLatestSnapshotId() ?? throw new InvalidOperationException("No snapshot found");
    }
}
