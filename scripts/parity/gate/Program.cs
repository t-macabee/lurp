using System.Diagnostics;
using System.Globalization;
using System.Text;
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
        catch (Exception ex) when (ex is HarnessError or InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    ///     Audit B2 read-only guarantee on a real solution. Restore, snapshot,
    ///     run every CLI mode and every MCP tool, snapshot again, and fail on any
    ///     added, removed, or changed path. Exit 1 on a tree difference, 2 on a
    ///     data error (a mode/tool missing from the run, an unusable tool).
    /// </summary>
    private static async Task<int> RunIntegrityAsync(string solution, string solutionDirectory, string lurpCmd)
    {
        // Restore runs before the measured window: its obj/ assets are part of
        // the baseline the later index must not disturb.
        var restore = ProcessRunner.Run(["dotnet", "restore", solution, "--nologo"], RestoreTimeoutMs);
        if (restore.TimedOut || restore.ExitCode != 0)
            return Fail($"dotnet restore failed (exit {restore.ExitCode}): {LastLine(restore.Stderr)}");

        var before = TreeSnapshot.Record(solutionDirectory);

        // The tool's own --help is the mode list; a count change is a data error.
        var modes = DeriveCliModes(lurpCmd);
        if (modes.Count != ExpectedCliModes)
            return Fail($"the tool listed {modes.Count} CLI mode(s); expected {ExpectedCliModes}.");

        var ranModes = new HashSet<string>(StringComparer.Ordinal);
        var context = new RunContext(solution, solutionDirectory);

        var index = LurpCommand.Run(lurpCmd, ["--mode=index", "--strategy=full", "--solution=" + solution], IndexTimeoutMs);
        if (index.TimedOut || index.ExitCode != 0)
            return Fail($"index failed (exit {index.ExitCode}): {LastLine(index.Stderr)}");
        ranModes.Add("index");

        var snapshot = ReadStatus(lurpCmd, solution, ranModes);
        if (snapshot is null)
            return Fail("could not resolve latest_snapshot_id from --mode=status --json.");
        context.Snapshot = snapshot;

        if (!DiscoverDeclaration(lurpCmd, solution, context, ranModes))
            return Fail("no indexed source declaration was found to drive the read modes.");

        // Every remaining mode, in the tool's own order so dependent modes
        // (annotate before retract-annotation) run in a sensible sequence.
        foreach (var mode in modes)
        {
            if (mode == "serve" || ranModes.Contains(mode))
                continue;

            switch (mode)
            {
                case "pin-snapshot":
                    RunCli(lurpCmd, "pin-snapshot", ["--snapshot=" + context.Snapshot, "--solution=" + solution], CliTimeoutMs, ranModes);
                    RunCli(lurpCmd, "pin-snapshot", ["--clear", "--solution=" + solution], CliTimeoutMs, ranModes);
                    break;
                case "annotate":
                    context.CliAnnotationId = CreateAnnotation(lurpCmd, context, "tree integrity cli");
                    context.McpAnnotationId = CreateAnnotation(lurpCmd, context, "tree integrity mcp");
                    ranModes.Add("annotate");
                    break;
                case "retract-annotation":
                    RunCli(lurpCmd, "retract-annotation", ["--annotation-id=" + context.CliAnnotationId.ToString(CultureInfo.InvariantCulture), "--solution=" + solution], CliTimeoutMs, ranModes);
                    break;
                default:
                    RunCli(lurpCmd, mode, ModeArgs(mode, context), CliTimeoutMs, ranModes);
                    break;
            }
        }

        var ranTools = new HashSet<string>(StringComparer.Ordinal);
        var toolNames = await RunMcpSurfaceAsync(lurpCmd, solution, context, ranTools);
        ranModes.Add("serve");

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

    private static void RunCli(string lurpCmd, string mode, IReadOnlyList<string> extra, int timeoutMs, HashSet<string> ran)
    {
        var args = new List<string> { "--mode=" + mode };
        args.AddRange(extra);
        LurpCommand.Run(lurpCmd, args, timeoutMs);
        ran.Add(mode);
    }

    private static string? ReadStatus(string lurpCmd, string solution, HashSet<string> ran)
    {
        var outcome = LurpCommand.Run(lurpCmd, ["--mode=status", "--json", "--solution=" + solution], CliTimeoutMs);
        ran.Add("status");
        if (outcome.TimedOut || outcome.ExitCode != 0 || !TryParseJson(outcome.Stdout, out var json))
            return null;
        return TryGetString(json, "latest_snapshot_id", out var snapshot) ? snapshot : null;
    }

    /// <summary>
    ///     Walks the solution directory for the first source document that carries
    ///     an indexed declaration, and records the symbol/doc/line the read modes
    ///     reuse. No fixture-specific names are assumed.
    /// </summary>
    private static bool DiscoverDeclaration(string lurpCmd, string solution, RunContext context, HashSet<string> ran)
    {
        foreach (var document in EnumerateSourceDocuments(context.SolutionDirectory))
        {
            var outcome = LurpCommand.Run(lurpCmd, ["--mode=outline", "--document=" + document, "--output=json", "--quiet", "--solution=" + solution], CliTimeoutMs);
            ran.Add("outline");
            if (outcome.TimedOut || outcome.ExitCode != 0 || !TryParseJson(outcome.Stdout, out var json))
                continue;
            if (!json.TryGetProperty("declarations", out var declarations)
                || declarations.ValueKind != JsonValueKind.Array
                || declarations.GetArrayLength() == 0)
                continue;

            var first = declarations[0];
            if (!TryGetString(first, "symbol_id", out var symbolId)
                || !TryGetString(first, "fully_qualified_name", out var fqn)
                || !first.TryGetProperty("start_line", out var startLine)
                || startLine.ValueKind != JsonValueKind.Number)
                continue;

            context.Document = document;
            context.SymbolId = symbolId;
            context.Fqn = fqn;
            context.Line = startLine.GetInt32();
            context.Query = SimpleName(fqn);
            return true;
        }

        return false;
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
        relativePath.Split('/').Any(segment =>
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase));

    private static string SimpleName(string fullyQualifiedName)
    {
        var lastDot = fullyQualifiedName.LastIndexOf('.');
        var name = lastDot >= 0 ? fullyQualifiedName[(lastDot + 1)..] : fullyQualifiedName;
        var cut = name.IndexOfAny(['(', '<', '`']);
        return cut >= 0 ? name[..cut] : name;
    }

    private static long CreateAnnotation(string lurpCmd, RunContext context, string value)
    {
        var outcome = LurpCommand.Run(
            lurpCmd,
            ["--mode=annotate", "--symbol=" + context.SymbolId, "--annotation-kind=note", "--value=" + value, "--solution=" + context.Solution],
            CliTimeoutMs);
        if (outcome.TimedOut || outcome.ExitCode != 0 || !TryParseJson(outcome.Stdout, out var json)
            || !json.TryGetProperty("annotation_id", out var id) || id.ValueKind != JsonValueKind.Number)
            return 0;
        return id.GetInt64();
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

    private static async Task<List<string>> RunMcpSurfaceAsync(string lurpCmd, string solution, RunContext context, HashSet<string> ranTools)
    {
        var argv = LurpCommand.Split(lurpCmd);
        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        for (var i = 1; i < argv.Length; i++)
            psi.ArgumentList.Add(argv[i]);
        psi.ArgumentList.Add("--mode=serve");
        psi.ArgumentList.Add("--enable-write-tools");
        psi.ArgumentList.Add("--solution=" + solution);

        using var serve = Process.Start(psi) ?? throw new HarnessError($"could not start '{argv[0]}' for --mode=serve.");
        var stderrTask = serve.StandardError.ReadToEndAsync();
        try
        {
            var client = new McpStdioClient(serve);
            await client.SendAsync("initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "lurp-parity-integrity", version = "1.0" }
            });
            await client.NotifyAsync("notifications/initialized", new { });

            var listed = await client.SendAsync("tools/list", new { });
            var toolNames = listed.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!)
                .ToList();
            if (toolNames.Count != ExpectedMcpTools)
                throw new HarnessError($"the MCP server listed {toolNames.Count} tool(s); expected {ExpectedMcpTools}.");

            foreach (var tool in toolNames)
            {
                try
                {
                    if (tool == "lurp_index")
                        await RunMcpIndexAsync(client);
                    else
                        await client.CallToolAsync(tool, McpArgs(tool, context));
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                {
                    Console.Error.WriteLine("WARNING: MCP tool '" + tool + "' failed: " + ex.Message);
                }

                ranTools.Add(tool);
            }

            return toolNames;
        }
        finally
        {
            // Always stop the server, whatever happened above.
            try
            {
                serve.StandardInput.Close();
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            if (!serve.WaitForExit(ServeShutdownTimeoutMs))
            {
                try
                {
                    serve.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }

            try
            {
                stderrTask.Wait(5000);
            }
            catch (AggregateException)
            {
            }
        }
    }

    private static async Task RunMcpIndexAsync(McpStdioClient client)
    {
        var started = await client.CallToolAsync("lurp_index", new { strategy = "incremental" });
        var operationId = ReadJsonField(started, "operation_id");
        if (operationId is null)
            return;

        var deadline = DateTime.UtcNow.AddMilliseconds(IndexPollTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            var poll = await client.CallToolAsync("lurp_index", new { operation_id = operationId });
            var status = ReadJsonField(poll, "status");
            if (status is "completed" or "failed" or "cancelled")
                return;
        }
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

        public long CliAnnotationId { get; set; }

        public long McpAnnotationId { get; set; }
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
        catch (Exception ex) when (ex is HarnessError or InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    ///     Audit B1 Fixture 3 recall on a real solution. Index with the packed
    ///     tool, open the same solution with MSBuildWorkspace, draw a seeded
    ///     sample of the declared source methods the index holds, and let Oracle B
    ///     compare Roslyn's own callers for each sampled method with the persisted
    ///     edge set. Exit 1 on any miss, 2 on a data error (index or workspace
    ///     failure, an empty candidate set or sample).
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

            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in msbuildSolution.Projects)
            {
                var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
                if (compilation is null)
                    continue;

                foreach (var symbol in compilation.GetSymbolsWithName(static _ => true, SymbolFilter.Member))
                {
                    if (symbol is not IMethodSymbol method || method.IsImplicitlyDeclared)
                        continue;
                    if (!SymbolFinderOracle.TargetKinds.Contains(method.MethodKind))
                        continue;

                    var id = RecalledDocId(method);
                    if (string.IsNullOrEmpty(id))
                        continue;

                    // The index resolves and classifies the declaration (source-only,
                    // generated excluded); a method it does not hold cannot be sampled.
                    if (store.ResolveSymbolByDocCommentId(id, snapshotId, includeGenerated: false) is null)
                        continue;

                    candidates.Add(id);
                }
            }

            var orderedCandidates = candidates.OrderBy(static id => id, StringComparer.Ordinal).ToList();
            if (orderedCandidates.Count == 0)
                return Fail("no declared source method candidate was found in the index.");

            var sampled = MethodSampler.Sample(orderedCandidates, seed, sampleSize);
            if (sampled.Count == 0)
                return Fail("the seeded sample is empty.");

            var result = await SymbolFinderOracle.CompareAsync(
                msbuildSolution,
                edges,
                RecalledDocId,
                new HashSet<string>(sampled, StringComparer.Ordinal)).ConfigureAwait(false);

            var misses = result.Targets.SelectMany(static target => target.MissingCallers).ToList();
            Console.WriteLine(
                $"recall: seed={seed}, sample={sampleSize}, candidates={orderedCandidates.Count}, " +
                $"targets={result.Targets.Count}, checked={result.Summary.CheckedPairs}, " +
                $"matched={result.Summary.MatchedPairs}, misses={misses.Count}");
            Console.WriteLine(
                $"recall set aside: " +
                $"{SymbolFinderOracle.BindsExternalReason}={result.Summary.SetAsideCallers.Count(static entry => entry.Reason == SymbolFinderOracle.BindsExternalReason)}, " +
                $"{SymbolFinderOracle.BindsOtherReason}={result.Summary.SetAsideCallers.Count(static entry => entry.Reason == SymbolFinderOracle.BindsOtherReason)}");
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

            return misses.Count == 0 ? ExitPass : ExitGateFailed;
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
    {
        var pipe = symbolId.IndexOf('|');
        return pipe > 0 ? symbolId[..pipe] : symbolId;
    }

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
    ///     Splits a command line on whitespace, honoring double quotes. Throws
    ///     <see cref="HarnessError" /> for an unterminated quote or an empty command.
    /// </summary>
    public static string[] Split(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            hasToken = true;
            current.Append(c);
        }

        if (inQuotes)
            throw new HarnessError("--lurp-cmd has an unterminated double quote.");
        if (hasToken)
            tokens.Add(current.ToString());
        if (tokens.Count == 0)
            throw new HarnessError("--lurp-cmd is empty.");
        return [.. tokens];
    }

    /// <summary>
    ///     Splits <paramref name="commandLine" /> and runs it with
    ///     <paramref name="arguments" /> appended, returning stdout, stderr, the
    ///     exit code, and whether the timeout fired.
    /// </summary>
    public static RunOutcome Run(string commandLine, IReadOnlyList<string> arguments, int timeoutMs)
    {
        var argv = Split(commandLine);
        if (arguments.Count > 0)
            argv = [.. argv, .. arguments];
        return ProcessRunner.Run(argv, timeoutMs);
    }
}
