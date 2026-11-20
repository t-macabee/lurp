using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Lurp.Parity.Shared;
using Lurp.PerfHarness;
using Lurp.Shared;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Lurp.Parity.Gate;

/// <summary>
///     Audit B1 parity gate. Two commands, <c>integrity</c> and <c>recall</c>,
///     parse and validate their arguments. <c>integrity</c> runs the full CLI
///     and MCP surface against a solution and fails on any tree change;
///     <c>recall</c> compares a seeded sample of methods against Roslyn's own
///     callers. Exit codes: 0 pass, 1 gate failed, 2 usage or data error.
/// </summary>
internal static class Program
{
    private const int ExitPass = 0;
    private const int ExitGateFailed = 1;
    private const int ExitUsageOrData = 2;

    private const int RestoreTimeoutMs = 10 * 60 * 1000;
    private const int IndexTimeoutMs = 30 * 60 * 1000;
    private const int CliTimeoutMs = 5 * 60 * 1000;
    private const int ServeShutdownTimeoutMs = 2 * 60 * 1000;
    private const int IndexPollTimeoutMs = 3 * 60 * 1000;

    // The surface audited against the analyzed tree. The gate derives both
    // lists from the tool at run time (--help and MCP tools/list) and fails if
    // either count drifts, so a stale list cannot hide a missing member.
    private const int ExpectedCliModes = 20;
    private const int ExpectedMcpTools = 18;

    private static readonly string[] IntegrityOptions = ["--solution", "--lurp-cmd"];

    private static readonly string[] RecallOptions = ["--solution", "--lurp-cmd", "--seed", "--sample"];

    private const string Usage =
        "usage: dotnet run --project scripts/parity/gate -c Release -- integrity --solution=<path> --lurp-cmd=\"<exe and leading args>\"\n" +
        "       dotnet run --project scripts/parity/gate -c Release -- recall --solution=<path> --lurp-cmd=\"<exe and leading args>\" --seed=<n> --sample=<n>";

    public static int Main(string[] args)
    {
        if (args.Length == 0)
            return Fail(Usage);

        return args[0] switch
        {
            "integrity" => RunIntegrity(args),
            "recall" => RunRecall(args),
            _ => Fail($"unknown command '{args[0]}'.\n{Usage}")
        };
    }

    private static int RunIntegrity(string[] args)
    {
        if (!TryParse(args, IntegrityOptions, out var values, out var error))
            return Fail(error);

        var solution = Path.GetFullPath(values["--solution"]);
        if (!File.Exists(solution))
            return Fail($"solution not found: {solution}");

        var solutionDirectory = Path.GetDirectoryName(solution);
        if (string.IsNullOrEmpty(solutionDirectory))
            return Fail($"could not resolve the directory of {solution}.");

        try
        {
            return RunIntegrityAsync(solution, solutionDirectory, values["--lurp-cmd"]).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsSurfaceException(ex))
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    ///     Restore the solution and record its tree. Run every CLI mode and MCP tool.
    ///     Return 2 when a mode or tool fails. After a successful run, compare the
    ///     tree and return 1 when a path was added, removed, or changed.
    /// </summary>
    private static async Task<int> RunIntegrityAsync(string solution, string solutionDirectory, string lurpCmd)
    {
        var restore = ProcessRunner.Run(["dotnet", "restore", solution, "--nologo"], RestoreTimeoutMs);
        if (restore.TimedOut || restore.ExitCode != 0)
            return Fail($"dotnet restore failed (exit {restore.ExitCode}): {LastLine(restore.Stderr)}");

        var before = TreeSnapshot.Record(solutionDirectory);
        var modes = DeriveCliModes(lurpCmd);
        if (modes.Count != ExpectedCliModes)
            return Fail($"the tool listed {modes.Count} CLI mode(s); expected {ExpectedCliModes}.");

        var outcomes = new List<SurfaceOutcome>();
        var ranModes = new HashSet<string>(StringComparer.Ordinal);
        var context = new RunContext(solution, solutionDirectory);

        RecordCli(RunCli(lurpCmd, "index", ["--strategy=full", "--solution=" + solution], IndexTimeoutMs, out _));
        RecordCli(ReadStatus(lurpCmd, context));
        RecordCli(DiscoverDeclaration(lurpCmd, context));

        // The help order puts annotate before retract-annotation.
        foreach (var mode in modes)
        {
            if (mode is "index" or "status" or "outline" or "serve")
                continue;

            switch (mode)
            {
                case "pin-snapshot":
                    var pin = RunCli(lurpCmd, mode, ["--snapshot=" + context.Snapshot, "--solution=" + solution], CliTimeoutMs, out _);
                    var clear = RunCli(lurpCmd, mode, ["--clear", "--solution=" + solution], CliTimeoutMs, out _);
                    RecordCli(pin);
                    RecordCli(clear);
                    break;
                case "annotate":
                    var first = CreateAnnotation(lurpCmd, context, "tree integrity cli", out var cliId);
                    var second = CreateAnnotation(lurpCmd, context, "tree integrity mcp", out var mcpId);
                    if (first.Succeeded && second.Succeeded)
                    {
                        context.CliAnnotationId = cliId;
                        context.McpAnnotationId = mcpId;
                    }
                    RecordCli(first);
                    RecordCli(second);
                    break;
                case "retract-annotation":
                    RecordCli(context.CliAnnotationId is { } id
                        ? RunCli(lurpCmd, mode, ["--annotation-id=" + id.ToString(CultureInfo.InvariantCulture), "--solution=" + solution],
                            CliTimeoutMs, out _)
                        : SurfaceOutcome.Failed(mode, "annotate failed"));
                    break;
                default:
                    RecordCli(RunCli(lurpCmd, mode, ModeArgs(mode, context), CliTimeoutMs, out _));
                    break;
            }
        }

        var ranTools = new HashSet<string>(StringComparer.Ordinal);
        var toolNames = await RunMcpSurfaceAsync(lurpCmd, context, outcomes, ranTools);
        if (outcomes.Any(static outcome => outcome is { Name: "serve", Succeeded: true }))
            ranModes.Add("serve");
        if (outcomes.Any(static outcome => !outcome.Succeeded))
        {
            foreach (var outcome in outcomes.Where(static outcome => !outcome.Succeeded))
                Console.Error.WriteLine($"FAILED {outcome.Name}: {outcome.Reason}");
            return ExitUsageOrData;
        }

        foreach (var mode in modes)
            if (!ranModes.Contains(mode))
                return Fail($"CLI mode '{mode}' was not run.");
        foreach (var tool in toolNames)
            if (!ranTools.Contains(tool))
                return Fail($"MCP tool '{tool}' was not run.");

        var after = TreeSnapshot.Record(solutionDirectory);
        var diff = TreeSnapshot.Diff(before, after);
        if (diff.Added.Count == 0 && diff.Removed.Count == 0 && diff.Changed.Count == 0)
            return ExitPass;

        foreach (var path in diff.Added)
            Console.WriteLine("ADDED " + path);
        foreach (var path in diff.Removed)
            Console.WriteLine("REMOVED " + path);
        foreach (var (path, oldValue, newValue) in diff.Changed)
            Console.WriteLine($"CHANGED {path} {oldValue} -> {newValue}");
        return ExitGateFailed;

        void RecordCli(SurfaceOutcome outcome)
        {
            outcomes.Add(outcome);
            if (outcome.Succeeded)
                ranModes.Add(outcome.Name);
        }
    }

    private sealed record SurfaceOutcome(string Name, bool Succeeded, string? Reason)
    {
        public static SurfaceOutcome Success(string name) => new(name, true, null);

        public static SurfaceOutcome Failed(string name, string reason) => new(name, false, reason);
    }

    private static List<string> DeriveCliModes(string lurpCmd)
    {
        var outcome = LurpCommand.Run(lurpCmd, ["--help"], CliTimeoutMs);
        if (outcome.TimedOut || outcome.ExitCode != 0)
            throw new HarnessError($"--help did not list the CLI modes (exit {outcome.ExitCode.ToString(CultureInfo.InvariantCulture)}).");

        var modes = new List<string>();
        var inModesBlock = false;
        foreach (var rawLine in outcome.Stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim() == "MODES")
            {
                inModesBlock = true;
                continue;
            }

            if (!inModesBlock)
                continue;
            if (line.Trim() == "OPTIONS BY MODE")
                break;

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("--mode=", StringComparison.Ordinal))
                continue;

            var name = trimmed["--mode=".Length..];
            var space = name.IndexOf(' ');
            modes.Add(space >= 0 ? name[..space] : name);
        }

        return modes;
    }

    private static SurfaceOutcome RunCli(
        string lurpCmd, string mode, IReadOnlyList<string> extra, int timeoutMs, out string stdout)
    {
        stdout = "";
        try
        {
            var result = LurpCommand.Run(lurpCmd, ["--mode=" + mode, .. extra], timeoutMs);
            stdout = result.Stdout;
            if (result.TimedOut)
                return SurfaceOutcome.Failed(mode, "timed out");
            if (result.ExitCode == 0)
                return SurfaceOutcome.Success(mode);

            var detail = LastLine(result.Stderr);
            return SurfaceOutcome.Failed(mode, detail.Length == 0
                ? $"exit {result.ExitCode}"
                : $"exit {result.ExitCode}: {detail}");
        }
        catch (Exception ex) when (IsSurfaceException(ex))
        {
            return SurfaceOutcome.Failed(mode, ex.Message);
        }
    }

    private static SurfaceOutcome ReadStatus(string lurpCmd, RunContext context)
    {
        var outcome = RunCli(lurpCmd, "status", ["--json", "--solution=" + context.Solution], CliTimeoutMs, out var stdout);
        if (!outcome.Succeeded)
            return outcome;
        if (!TryParseJson(stdout, out var json) ||
            !TryGetString(json, "latest_snapshot_id", out var snapshot) || string.IsNullOrWhiteSpace(snapshot))
            return SurfaceOutcome.Failed("status", "missing latest_snapshot_id");

        context.Snapshot = snapshot;
        return outcome;
    }

    private static SurfaceOutcome DiscoverDeclaration(string lurpCmd, RunContext context)
    {
        var failure = SurfaceOutcome.Failed("outline", "no indexed source declaration was found");
        foreach (var document in EnumerateSourceDocuments(context.SolutionDirectory))
        {
            var outcome = RunCli(lurpCmd, "outline",
                ["--document=" + document, "--output=json", "--quiet", "--solution=" + context.Solution],
                CliTimeoutMs, out var stdout);
            if (!outcome.Succeeded)
            {
                failure = outcome;
                continue;
            }

            if (!TryParseJson(stdout, out var json) || json.ValueKind != JsonValueKind.Object ||
                !json.TryGetProperty("declarations", out var declarations) ||
                declarations.ValueKind != JsonValueKind.Array ||
                declarations.GetArrayLength() == 0)
                continue;

            var first = declarations[0];
            if (!TryGetString(first, "symbol_id", out var symbolId) ||
                !TryGetString(first, "fully_qualified_name", out var fqn) ||
                !first.TryGetProperty("start_line", out var startLine) ||
                startLine.ValueKind != JsonValueKind.Number ||
                !startLine.TryGetInt32(out var line))
                continue;

            context.Document = document;
            context.SymbolId = symbolId;
            context.Fqn = fqn;
            context.Line = line;
            context.Query = SimpleName(fqn);
            return outcome;
        }

        return failure;
    }

    private static IEnumerable<string> EnumerateSourceDocuments(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsBuildPath(relative))
                continue;
            yield return relative;
        }
    }

    private static bool IsBuildPath(string relativePath) =>
        relativePath.Split('/', '\\').Any(segment =>
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase));

    private static string SimpleName(string fullyQualifiedName)
    {
        var lastDot = fullyQualifiedName.LastIndexOf('.');
        var name = lastDot >= 0 ? fullyQualifiedName[(lastDot + 1)..] : fullyQualifiedName;
        var cut = name.IndexOfAny(['(', '<', '`']);
        return cut >= 0 ? name[..cut] : name;
    }

    private static SurfaceOutcome CreateAnnotation(string lurpCmd, RunContext context, string value, out long annotationId)
    {
        var outcome = RunCli(lurpCmd, "annotate",
            ["--symbol=" + context.SymbolId, "--annotation-kind=note", "--value=" + value, "--solution=" + context.Solution],
            CliTimeoutMs, out var stdout);
        annotationId = 0;
        if (!outcome.Succeeded)
            return outcome;

        if (!TryParseJson(stdout, out var json) || json.ValueKind != JsonValueKind.Object ||
            !json.TryGetProperty("annotation_id", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out annotationId) || annotationId < 1)
            return SurfaceOutcome.Failed("annotate", "missing or invalid annotation_id");

        return outcome;
    }

    private static IReadOnlyList<string> ModeArgs(string mode, RunContext context) => mode switch
    {
        "get-source" => ["--document=" + context.Document, "--quiet", "--solution=" + context.Solution],
        "get-symbol" => ["--symbol=" + context.SymbolId, "--view=signature", "--solution=" + context.Solution],
        "search" => ["--query=" + context.Query, "--output=json", "--quiet", "--solution=" + context.Solution],
        "find-symbol" => ["--symbol=" + context.Fqn, "--output=json", "--quiet", "--solution=" + context.Solution],
        "navigate" => ["--file=" + context.Document, "--line=" + context.Line.ToString(CultureInfo.InvariantCulture), "--quiet", "--solution=" + context.Solution],
        "diff" => ["--from-snapshot=" + context.Snapshot, "--to-snapshot=" + context.Snapshot, "--solution=" + context.Solution],
        "impact" => ["--symbol=" + context.SymbolId, "--direction=upstream", "--output=json", "--quiet", "--solution=" + context.Solution],
        "context" => ["--symbol=" + context.SymbolId, "--output=summary", "--quiet", "--solution=" + context.Solution],
        "timings" => ["--json", "--solution=" + context.Solution],
        "get-annotations" => ["--symbol=" + context.SymbolId, "--output=json", "--quiet", "--solution=" + context.Solution],
        "diagnostics" => ["--limit=5", "--output=json", "--quiet", "--solution=" + context.Solution],
        "grep" => ["--query=" + context.Query, "--output=json", "--quiet", "--solution=" + context.Solution],
        "dead-candidates" => ["--limit=5", "--output=json", "--quiet", "--solution=" + context.Solution],
        _ => ["--solution=" + context.Solution]
    };

    private static async Task<List<string>> RunMcpSurfaceAsync(
        string lurpCmd, RunContext context, List<SurfaceOutcome> outcomes, HashSet<string> ranTools)
    {
        List<string> toolNames = [];
        try
        {
            using var server = McpServeProcess.Start(
                [.. ProcessRunner.SplitCommandLine(lurpCmd), "--mode=serve", "--enable-write-tools", "--solution=" + context.Solution]);
            try
            {
                var client = server.Client;
                await client.InitializeAsync("lurp-parity-integrity");

                var listed = await client.SendAsync("tools/list", new { });
                toolNames = listed.GetProperty("result").GetProperty("tools").EnumerateArray()
                    .Select(static tool => tool.GetProperty("name").GetString()!).ToList();
                if (toolNames.Count != ExpectedMcpTools)
                    outcomes.Add(SurfaceOutcome.Failed("serve", $"listed {toolNames.Count} MCP tools, expected {ExpectedMcpTools}"));

                foreach (var tool in toolNames)
                {
                    if (tool == "lurp_retract_annotation" && context.McpAnnotationId is null)
                    {
                        outcomes.Add(SurfaceOutcome.Failed(tool, "annotate failed"));
                        continue;
                    }

                    try
                    {
                        if (tool == "lurp_index")
                            await RunMcpIndexAsync(client);
                        else
                            await client.CallToolAsync(tool, McpArgs(tool, context));
                        outcomes.Add(SurfaceOutcome.Success(tool));
                        ranTools.Add(tool);
                    }
                    catch (Exception ex) when (IsSurfaceException(ex))
                    {
                        outcomes.Add(SurfaceOutcome.Failed(tool, ex.Message));
                    }
                }
            }
            finally
            {
                var exit = server.Close(ServeShutdownTimeoutMs);
                if (exit != 0)
                    outcomes.Add(SurfaceOutcome.Failed("serve", $"exit {exit}: {LastLine(server.Stderr)}"));
            }

            if (!outcomes.Any(static outcome => outcome is { Name: "serve", Succeeded: false }))
                outcomes.Add(SurfaceOutcome.Success("serve"));
        }
        catch (Exception ex) when (IsSurfaceException(ex))
        {
            outcomes.Add(SurfaceOutcome.Failed("serve", ex.Message));
        }

        return toolNames;
    }

    private static bool IsSurfaceException(Exception ex) =>
        ex is HarnessError or InvalidOperationException or TimeoutException or IOException or Win32Exception
            or KeyNotFoundException or JsonException;

    private static async Task RunMcpIndexAsync(McpStdioClient client)
    {
        var timer = Stopwatch.StartNew();
        var timeout = TimeSpan.FromMilliseconds(IndexPollTimeoutMs);
        var started = await client.CallToolAsync("lurp_index", new { strategy = "incremental" });
        var operationId = ReadJsonField(started, "operation_id");
        if (string.IsNullOrWhiteSpace(operationId))
            throw new InvalidOperationException("missing operation_id");

        while (timeout - timer.Elapsed > TimeSpan.FromMilliseconds(500))
        {
            await Task.Delay(500);
            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            var poll = await client.CallToolAsync("lurp_index", new { operation_id = operationId }, remaining);
            var status = ReadJsonField(poll, "status");
            if (status == "completed" && timer.Elapsed <= timeout)
                return;
            if (status is "failed" or "cancelled")
                throw new InvalidOperationException($"index operation {operationId} {status}: {poll}");
        }

        throw new TimeoutException($"index operation {operationId} did not complete within {timeout.TotalMinutes} minutes");
    }

    private static object McpArgs(string tool, RunContext context) => tool switch
    {
        "lurp_context" => new { symbol = context.SymbolId },
        "lurp_dead_candidates" => new { limit = 5 },
        "lurp_diagnostics" => new { limit = 5 },
        "lurp_diff" => new { from_snapshot = context.Snapshot, to_snapshot = context.Snapshot },
        "lurp_find_symbol" => new { symbol = context.Fqn },
        "lurp_get_annotations" => new { symbol = context.SymbolId },
        "lurp_get_source" => new { document = context.Document },
        "lurp_get_symbol" => new { symbol = context.SymbolId, view = "summary" },
        "lurp_grep" => new { query = context.Query },
        "lurp_impact" => new { symbol = context.SymbolId, direction = "upstream" },
        "lurp_navigate" => new { file = context.Document, line = context.Line },
        "lurp_outline" => new { document = context.Document },
        "lurp_retract_annotation" => new { annotation_id = context.McpAnnotationId },
        "lurp_search" => new { query = context.Query },
        _ => new { }
    };

    private static string? ReadJsonField(JsonElement toolResult, string field)
    {
        if (!toolResult.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array
            || content.GetArrayLength() == 0)
            return null;

        if (!content[0].TryGetProperty("text", out var textProperty) || textProperty.ValueKind != JsonValueKind.String)
            return null;

        var text = textProperty.GetString();
        if (text is null || !TryParseJson(text, out var json))
            return null;
        return TryGetString(json, field, out var value) ? value : null;
    }

    private static bool TryParseJson(string text, out JsonElement element)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }

        value = "";
        return false;
    }

    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }

    private sealed class RunContext(string solution, string solutionDirectory)
    {
        public string Solution { get; } = solution;

        public string SolutionDirectory { get; } = solutionDirectory;

        public string Document { get; set; } = "";

        public string SymbolId { get; set; } = "";

        public string Fqn { get; set; } = "";

        public int Line { get; set; }

        public string Query { get; set; } = "";

        public string Snapshot { get; set; } = "";

        public long? CliAnnotationId { get; set; }

        public long? McpAnnotationId { get; set; }
    }

    private static int RunRecall(string[] args)
    {
        if (!TryParse(args, RecallOptions, out var values, out var error))
            return Fail(error);

        if (!int.TryParse(values["--seed"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
            return Fail($"--seed must be an integer, got '{values["--seed"]}'.");
        if (!int.TryParse(values["--sample"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleSize))
            return Fail($"--sample must be an integer, got '{values["--sample"]}'.");
        if (sampleSize < 1)
            return Fail($"--sample must be at least 1, got {sampleSize}.");

        var solution = Path.GetFullPath(values["--solution"]);
        if (!File.Exists(solution))
            return Fail($"solution not found: {solution}");

        try
        {
            return RunRecallAsync(solution, values["--lurp-cmd"], seed, sampleSize).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsSurfaceException(ex) || ex is NotSupportedException or AggregateException)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    ///     Audit B1 Fixture 3 recall on a real solution. Index with the packed
    ///     tool, open the same solution with MSBuildWorkspace, draw a seeded
    ///     sample of the source methods Roslyn declares in the snapshot scope, and
    ///     check each sampled declaration and its callers against the index.
    ///     Return 1 for a miss and 2 for a data error.
    /// </summary>
    private static async Task<int> RunRecallAsync(string solution, string lurpCmd, int seed, int sampleSize)
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "lurp-parity-recall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);
        try
        {
            var restore = ProcessRunner.Run(["dotnet", "restore", solution, "--nologo"], RestoreTimeoutMs);
            if (restore.TimedOut || restore.ExitCode != 0)
                return Fail($"dotnet restore failed (exit {restore.ExitCode}): {LastLine(restore.Stderr)}");

            var index = LurpCommand.Run(
                lurpCmd,
                ["--mode=index", "--strategy=full", "--solution=" + solution, "--output-dir=" + outputDir],
                IndexTimeoutMs);
            if (index.TimedOut || index.ExitCode != 0)
                return Fail($"index failed (exit {index.ExitCode}): {LastLine(index.Stderr)}");

            var dbPath = Path.Combine(outputDir, "index.db");
            if (!File.Exists(dbPath))
                return Fail($"index database not found at {dbPath}.");

            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();

            using var workspace = MSBuildWorkspace.Create(LurpCache.CreateWorkspaceGlobalProperties(solution));
            var msbuildSolution = await workspace.OpenSolutionAsync(solution).ConfigureAwait(false);

            using var store = new SqliteIndexStore(dbPath);
            store.OpenReadOnly();
            var snapshotId = store.GetLatestSnapshotId();
            if (string.IsNullOrEmpty(snapshotId))
                return Fail("the index has no snapshot.");

            // The persisted edge endpoints carry the assembly identity; Oracle B's
            // resolver returns the doc-comment half, so compare in that space.
            var edges = store.GetEdges(snapshotId)
                .Select(static edge => (Source: DocIdPart(edge.SourceSymbolId), edge.Kind, Target: DocIdPart(edge.TargetSymbolId)))
                .ToList();

            var candidates = new Dictionary<string, IMethodSymbol>(StringComparer.Ordinal);
            foreach (var project in msbuildSolution.Projects)
            {
                var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
                var projectDirectory = Path.GetDirectoryName(project.FilePath);
                if (compilation is null || projectDirectory is null)
                    continue;

                var inScopeTrees = new HashSet<SyntaxTree>();
                foreach (var document in project.Documents)
                {
                    if (document.FilePath is not { } path || IsBuildPath(Path.GetRelativePath(projectDirectory, path)))
                        continue;

                    if (await document.GetSyntaxTreeAsync().ConfigureAwait(false) is { } tree && !GeneratedCode.IsGenerated(tree))
                        inScopeTrees.Add(tree);
                }

                foreach (var symbol in compilation.GetSymbolsWithName(static _ => true, SymbolFilter.Member))
                {
                    if (symbol is IMethodSymbol method)
                        AddCandidate(method);
                    else if (symbol is IEventSymbol @event)
                    {
                        AddCandidate(@event.AddMethod);
                        AddCandidate(@event.RemoveMethod);
                    }
                }

                void AddCandidate(IMethodSymbol? method)
                {
                    if (method is null || !SymbolFinderOracle.IsTarget(method))
                        return;

                    var declaration = method.IsImplicitlyDeclared && method.AssociatedSymbol is IEventSymbol @event
                        ? (ISymbol)@event
                        : method;
                    if (declaration.DeclaringSyntaxReferences.Any(reference => inScopeTrees.Contains(reference.SyntaxTree)) &&
                        RecalledDocId(method) is { Length: > 0 } id)
                        candidates.TryAdd(id, method);
                }
            }

            var orderedCandidates = candidates.Keys.OrderBy(static id => id, StringComparer.Ordinal).ToList();
            if (orderedCandidates.Count == 0)
                return Fail("Roslyn found no source method candidate in scope.");

            var sampled = MethodSampler.Sample(orderedCandidates, seed, sampleSize);
            if (sampled.Count == 0)
                return Fail("the seeded sample is empty.");

            var declared = new HashSet<string>(StringComparer.Ordinal);
            var missingDeclarations = 0;
            var differentIds = 0;
            foreach (var id in sampled)
            {
                var method = candidates[id];
                if (!method.ContainingType.IsExtension && id != method.GetDocumentationCommentId())
                    differentIds++;

                var owner = method.AssociatedSymbol ?? method;
                var ownerId = RecalledDocId(owner);
                if (ownerId is not null &&
                    store.FindSymbols(SymbolKey.DocCommentId, ownerId, snapshotId, includeGenerated: false).Count != 0)
                {
                    declared.Add(id);
                    continue;
                }

                missingDeclarations++;
                Console.WriteLine($"MISSING DECLARATION {id} (owner {ownerId ?? "<no id>"})");
            }

            var result = await SymbolFinderOracle.CompareAsync(msbuildSolution, edges, RecalledDocId, declared)
                .ConfigureAwait(false);

            var misses = result.Targets.SelectMany(static target => target.MissingCallers).ToList();
            Console.WriteLine(
                $"recall: seed={seed}, sample={sampleSize}, candidates={orderedCandidates.Count}, " +
                $"targets={result.Targets.Count}, checked={result.Summary.CheckedPairs}, " +
                $"matched={result.Summary.MatchedPairs}, misses={misses.Count}, missing_declarations={missingDeclarations}");
            Console.WriteLine($"recall ids: differ_from_doc_comment_id={differentIds}");
            Console.WriteLine(
                $"recall set aside: " +
                $"{SymbolFinderOracle.BindsExternalReason}={result.Summary.SetAsideCallers.Count(static entry => entry.Reason == SymbolFinderOracle.BindsExternalReason)}, " +
                $"{SymbolFinderOracle.BindsOtherReason}={result.Summary.SetAsideCallers.Count(static entry => entry.Reason == SymbolFinderOracle.BindsOtherReason)}, " +
                $"{SymbolFinderOracle.DynamicBindingReason}={result.Summary.SetAsideCallers.Count(static entry => entry.Reason == SymbolFinderOracle.DynamicBindingReason)}");
            foreach (var setAside in result.Summary.SetAsideCallers
                         .OrderBy(static entry => entry.TargetId, StringComparer.Ordinal)
                         .ThenBy(static entry => entry.Reason, StringComparer.Ordinal)
                         .ThenBy(static entry => entry.CallerIds.Count > 0 ? entry.CallerIds[0] : "", StringComparer.Ordinal))
                Console.WriteLine(
                    $"SET ASIDE {setAside.Reason} {setAside.TargetId} expected caller {string.Join(" OR ", setAside.CallerIds)}");
            Console.WriteLine($"recall precision: {result.Summary.Precision:P2}");
            foreach (var miss in misses)
                Console.WriteLine(
                    $"MISS {miss.TargetId} expected caller {string.Join(" OR ", miss.CallerIds)} " +
                    $"missing edge kind {string.Join("|", miss.MissingEdgeKinds)}");

            return misses.Count + missingDeclarations == 0 ? ExitPass : ExitGateFailed;
        }
        finally
        {
            try
            {
                Directory.Delete(outputDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The <c>docCommentId</c> half of a persisted <c>docCommentId|assembly</c> symbol id.</summary>
    private static string DocIdPart(string symbolId)
        => SymbolId.DocCommentIdOf(symbolId);

    /// <summary>
    ///     The doc-comment id the extractor persists for a symbol, taken from the
    ///     extractor's own <see cref="SymbolIdFactory" /> (reduced extension methods
    ///     and extension-block members included), without the assembly half.
    /// </summary>
    private static string? RecalledDocId(ISymbol symbol) =>
        SymbolIdFactory.Make(symbol, "") is { } id ? DocIdPart(id) : null;

    private static bool TryParse(
        string[] args,
        IReadOnlyList<string> allowed,
        out Dictionary<string, string> values,
        out string error)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        error = "";
        foreach (var arg in args.AsSpan(1))
        {
            var separator = arg.IndexOf('=');
            if (separator <= 0)
            {
                error = $"unknown argument '{arg}'.";
                return false;
            }

            var name = arg[..separator];
            if (!allowed.Contains(name, StringComparer.Ordinal))
            {
                error = $"unknown argument '{name}'.";
                return false;
            }

            if (!values.TryAdd(name, arg[(separator + 1)..]))
            {
                error = $"duplicate argument '{name}'.";
                return false;
            }
        }

        foreach (var name in allowed)
        {
            if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                error = $"{name}=<value> is required.";
                return false;
            }
        }

        return true;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("ERROR: " + message);
        return ExitUsageOrData;
    }
}

/// <summary>
///     Splits a <c>--lurp-cmd</c> value into the executable and its leading
///     arguments, then runs it through the perf harness's shared
///     <see cref="ProcessRunner" />.
/// </summary>
internal static class LurpCommand
{
    /// <summary>
    ///     Splits <paramref name="commandLine" /> and runs it with
    ///     <paramref name="arguments" /> appended, returning stdout, stderr, the
    ///     exit code, and whether the timeout fired.
    /// </summary>
    public static RunOutcome Run(string commandLine, IReadOnlyList<string> arguments, int timeoutMs)
    {
        var argv = ProcessRunner.SplitCommandLine(commandLine);
        if (arguments.Count > 0)
            argv = [.. argv, .. arguments];
        return ProcessRunner.Run(argv, timeoutMs);
    }
}
