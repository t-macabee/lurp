using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lurp.PerfHarness;

internal static class Program
{
    private const int SchemaVersion = 1;
    private const string WorkMarkerFileName = ".lurp-perf-work";
    private const string WorkMarkerContent = "lurp-perf-harness 1.0.0\n";
    private const string FixtureMarkerFileName = ".lurp-perf-gen";
    private static readonly string[] VersionArgs = ["--version"];
    private static readonly string[] Directions = ["upstream", "downstream"];
    private static readonly int[] Depths = [3, 6];
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public static int Main(string[] args)
    {
        HarnessOptions options;
        try
        {
            options = HarnessOptions.Parse(args);
        }
        catch (HarnessError ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            Console.Error.WriteLine(HarnessOptions.Usage);
            return 2;
        }

        try
        {
            return Execute(options);
        }
        catch (HarnessError ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: unexpected harness failure: " + ex);
            return 3;
        }
    }

    private static int Execute(HarnessOptions options)
    {
        if (options.Evaluate != null)
            return GateEvaluator.Evaluate(options);

        var startedAt = DateTime.UtcNow;
        var repoRoot = PathTools.FindRepoRoot(AppContext.BaseDirectory) ?? PathTools.FindRepoRoot(Directory.GetCurrentDirectory());
        var workDir = Path.GetFullPath(options.Work);
        var outPath = Path.GetFullPath(options.Out);

        if (repoRoot != null && PathTools.IsInside(workDir, repoRoot))
            throw new HarnessError($"--work '{workDir}' is inside the Lurp repo at '{repoRoot}'. Use scratch space outside the repo.");
        if (repoRoot != null && PathTools.IsInside(outPath, repoRoot))
            throw new HarnessError($"--out '{outPath}' is inside the Lurp repo at '{repoRoot}'. Keep result files outside the repo.");

        string solutionPath;
        string? fixtureRoot = null;
        JsonObject? fixtureManifest = null;
        AnchorSet? anchors = null;

        if (options.Fixture != null)
        {
            fixtureRoot = Path.GetFullPath(options.Fixture);
            if (!Directory.Exists(fixtureRoot))
                throw new HarnessError($"--fixture '{fixtureRoot}' does not exist.");
            if (PathTools.IsInside(workDir, fixtureRoot))
                throw new HarnessError($"--work '{workDir}' is inside the fixture. Use scratch space outside the fixture.");
            if (!File.Exists(Path.Combine(fixtureRoot, FixtureMarkerFileName)))
                throw new HarnessError($"--fixture '{fixtureRoot}' has no {FixtureMarkerFileName} marker; edit operations run only against generated fixtures.");
            var manifestPath = Path.Combine(fixtureRoot, "perf-manifest.json");
            if (!File.Exists(manifestPath))
                throw new HarnessError($"--fixture '{fixtureRoot}' has no perf-manifest.json.");
            fixtureManifest = JsonTools.TryParseObject(File.ReadAllText(manifestPath))
                              ?? throw new HarnessError($"perf-manifest.json in '{fixtureRoot}' is not a JSON object.");
            anchors = ReadAnchors(fixtureManifest, requireEditDocument: true);
            var solutions = Directory.GetFiles(fixtureRoot, "*.slnx");
            if (solutions.Length != 1)
                throw new HarnessError($"--fixture '{fixtureRoot}' must hold exactly one .slnx solution; found {solutions.Length}.");
            solutionPath = solutions[0];
        }
        else
        {
            solutionPath = Path.GetFullPath(options.Solution!);
            if (!File.Exists(solutionPath))
                throw new HarnessError($"--solution '{solutionPath}' does not exist.");
            var solutionDir = Path.GetDirectoryName(solutionPath);
            if (solutionDir != null && PathTools.IsInside(workDir, solutionDir))
                throw new HarnessError($"--work '{workDir}' is inside the solution tree '{solutionDir}'.");
            if (options.AnchorsPath != null)
            {
                var anchorsPath = Path.GetFullPath(options.AnchorsPath);
                if (!File.Exists(anchorsPath))
                    throw new HarnessError($"--anchors '{anchorsPath}' does not exist.");
                var anchorsSource = JsonTools.TryParseObject(File.ReadAllText(anchorsPath))
                                    ?? throw new HarnessError($"anchors file '{anchorsPath}' is not a JSON object.");
                anchors = ReadAnchors(anchorsSource, requireEditDocument: false);
            }
        }

        var toolArgv = ResolveToolArgv(options, repoRoot);
        var toolVersion = ReadToolVersion(toolArgv);
        var (repoCommit, repoDirty) = ReadRepoState(repoRoot);
        var runnerImage = Environment.GetEnvironmentVariable("ImageVersion");

        Console.WriteLine($"lurp perf harness: tool = {string.Join(" ", toolArgv)}");
        Console.WriteLine($"lurp perf harness: {(fixtureRoot != null ? $"fixture = {fixtureRoot}" : $"solution = {solutionPath}")}");
        Console.WriteLine($"lurp perf harness: work = {workDir}  protocol = {options.Protocol}  runs = {options.Runs}  warmup = {options.Warmup}");

        SetupWorkDirectory(workDir, options.KeepWork);
        var runDir = Path.Combine(workDir, $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
        Directory.CreateDirectory(runDir);
        var primaryDir = Path.Combine(runDir, "full-index", $"run-{options.Warmup + 1:D2}");

        var session = new Session(options, toolArgv, fixtureRoot ?? "", solutionPath, runDir, primaryDir, anchors);
        var operations = session.Operations;

        operations.Add(session.FullIndex());
        var primarySnapshot = session.GetLatestSnapshotId(primaryDir);
        if (primarySnapshot == null)
        {
            Console.Error.WriteLine($"ERROR: the full index left no readable snapshot in '{primaryDir}'; every dependent operation is skipped.");
            AddAllSkipped(operations, anchors, fixtureRoot != null, "the full index produced no readable snapshot");
            WriteOutput(outPath, options, toolArgv, toolVersion, repoCommit, repoDirty, runnerImage, fixtureManifest, fixtureRoot, solutionPath, workDir, runDir, startedAt, operations);
            PrintSummary(operations);
            return 1;
        }

        session.CurrentSnapshot = primarySnapshot;
        var fullSnapshotPinned = TryPinSnapshot(toolArgv, primaryDir, primarySnapshot);

        if (anchors != null && !ResolveAnchors(session, toolArgv, primaryDir))
            return 2;

        operations.Add(session.Incremental("noop_incremental", 0));

        if (fixtureRoot != null && anchors?.EditDocument != null)
        {
            operations.Add(session.Incremental("incremental_1_file", 1));
            operations.Add(session.Incremental("incremental_20_files", 20));
        }
        else
        {
            var editReason = fixtureRoot == null ? "--solution runs never edit files" : "no edit_document anchor";
            operations.Add(Skipped("incremental_1_file", null, editReason));
            operations.Add(Skipped("incremental_20_files", null, editReason));
        }

        if (fullSnapshotPinned)
            TryClearPin(toolArgv, primaryDir);

        if (anchors == null)
        {
            AddAnchorSkipped(operations, "no anchors available (pass --anchors for --solution runs)");
        }
        else
        {
            foreach (var role in Session.AnchorRoles)
            {
                var fqn = session.AnchorFqn(role)!;
                foreach (var direction in Directions)
                {
                    foreach (var depth in Depths)
                    {
                        operations.Add(session.Read(
                            $"impact.{direction}.d{depth}.{role}",
                            ["--mode=impact", $"--symbol={fqn}", $"--direction={direction}", $"--max-depth={depth}", "--output=json", "--quiet", $"--output-dir={primaryDir}"],
                            role));
                    }
                }

                operations.Add(session.Read(
                    $"context.default.{role}",
                    ["--mode=context", $"--symbol={fqn}", "--output=summary", "--quiet", $"--output-dir={primaryDir}"],
                    role));
                operations.Add(session.Read(
                    $"context.maxhops6.{role}",
                    ["--mode=context", $"--symbol={fqn}", "--max-hops=6", "--output=summary", "--quiet", $"--output-dir={primaryDir}"],
                    role));
            }
        }

        if (anchors == null)
        {
            operations.Add(Skipped("search", null, "no anchors available"));
            operations.Add(Skipped("grep", null, "no anchors available"));
            operations.Add(Skipped("find_symbol", null, "no anchors available"));
        }
        else
        {
            var middleFqn = session.AnchorFqn("middle")!;
            var simpleName = middleFqn[(middleFqn.LastIndexOf('.') + 1)..];
            operations.Add(session.Read("search", ["--mode=search", $"--query={simpleName}", "--output=json", "--quiet", $"--output-dir={primaryDir}"]));
            operations.Add(session.Read("grep", ["--mode=grep", $"--query={simpleName}", "--output=json", "--quiet", $"--output-dir={primaryDir}"]));
            operations.Add(session.Read("find_symbol", ["--mode=find-symbol", $"--symbol={middleFqn}", "--output=json", "--quiet", $"--output-dir={primaryDir}"]));
        }

        var latestSnapshot = session.GetLatestSnapshotId(primaryDir);
        if (latestSnapshot == null)
            operations.Add(Skipped("diff", null, "no latest snapshot id available"));
        else
            operations.Add(session.Read("diff", ["--mode=diff", $"--from-snapshot={primarySnapshot}", $"--to-snapshot={latestSnapshot}", $"--output-dir={primaryDir}"]));

        if (anchors == null)
        {
            operations.Add(Skipped("find_symbol.freshness_auto_untouched", null, "no anchors available"));
            operations.Add(Skipped("find_symbol.freshness_hash_untouched", null, "no anchors available"));
        }
        else
        {
            var middleFqn = session.AnchorFqn("middle")!;
            operations.Add(session.Freshness("find_symbol.freshness_auto_untouched", "auto", middleFqn, primaryDir));
            operations.Add(session.Freshness("find_symbol.freshness_hash_untouched", "hash", middleFqn, primaryDir));
        }

        operations.Add(session.Read("dead_candidates", ["--mode=dead-candidates", "--output=json", "--quiet", $"--output-dir={primaryDir}"]));
        operations.Add(session.McpStatus("mcp_status.default", full: false));
        operations.Add(session.McpStatus("mcp_status.full", full: true));

        // The mtime touch must come last so no other operation sees touched files.
        if (fixtureRoot == null)
        {
            operations.Add(Skipped("find_symbol.freshness_auto_touched", null, "--solution runs never touch files"));
            operations.Add(Skipped("find_symbol.freshness_hash_touched", null, "--solution runs never touch files"));
        }
        else if (anchors == null)
        {
            operations.Add(Skipped("find_symbol.freshness_auto_touched", null, "no anchors available"));
            operations.Add(Skipped("find_symbol.freshness_hash_touched", null, "no anchors available"));
        }
        else
        {
            session.FreshnessTouchedOps(session.AnchorFqn("middle")!, primaryDir);
        }

        WriteOutput(outPath, options, toolArgv, toolVersion, repoCommit, repoDirty, runnerImage, fixtureManifest, fixtureRoot, solutionPath, workDir, runDir, startedAt, operations);
        PrintSummary(operations);
        return session.AnyFailure ? 1 : 0;
    }

    private static bool ResolveAnchors(Session session, string[] toolArgv, string primaryDir)
    {
        foreach (var role in Session.AnchorRoles)
        {
            var fqn = session.AnchorFqn(role)!;
            var outcome = ProcessRunner.Run(
                Session.BuildArgv(toolArgv, ["--mode=find-symbol", $"--symbol={fqn}", "--output=json", "--quiet", $"--output-dir={primaryDir}"]),
                2 * 60 * 1000);
            if (outcome.ExitCode != 0 || outcome.TimedOut)
            {
                Console.Error.WriteLine($"ERROR: anchor '{role}' ({fqn}) did not resolve in '{primaryDir}' (exit {outcome.ExitCode}).");
                if (!string.IsNullOrWhiteSpace(outcome.Stderr))
                    Console.Error.WriteLine(outcome.Stderr.Trim());
                return false;
            }
        }

        Console.WriteLine($"anchors resolved: {string.Join(", ", Session.AnchorRoles.Select(role => $"{role}={session.AnchorFqn(role)}"))}");
        return true;
    }

    private static bool TryPinSnapshot(string[] toolArgv, string primaryDir, string snapshotId)
    {
        var outcome = ProcessRunner.Run(
            Session.BuildArgv(toolArgv, ["--mode=pin-snapshot", $"--snapshot={snapshotId}", $"--output-dir={primaryDir}"]),
            2 * 60 * 1000);
        if (outcome.ExitCode == 0 && !outcome.TimedOut)
            return true;

        Console.WriteLine($"warning: could not pin the full-index snapshot; the diff operation may fail: {outcome.Stderr.Trim()}");
        return false;
    }

    private static void TryClearPin(string[] toolArgv, string primaryDir)
    {
        var outcome = ProcessRunner.Run(
            Session.BuildArgv(toolArgv, ["--mode=pin-snapshot", "--clear", $"--output-dir={primaryDir}"]),
            2 * 60 * 1000);
        if (outcome.ExitCode != 0 || outcome.TimedOut)
            Console.WriteLine($"warning: could not clear the snapshot pin: {outcome.Stderr.Trim()}");
    }

    private static void AddAnchorSkipped(List<OperationRecord> operations, string reason)
    {
        foreach (var role in Session.AnchorRoles)
        {
            foreach (var direction in Directions)
            {
                foreach (var depth in Depths)
                    operations.Add(Skipped($"impact.{direction}.d{depth}.{role}", role, reason));
            }

            operations.Add(Skipped($"context.default.{role}", role, reason));
            operations.Add(Skipped($"context.maxhops6.{role}", role, reason));
        }
    }

    private static void AddAllSkipped(List<OperationRecord> operations, AnchorSet? anchors, bool fixturePresent, string reason)
    {
        operations.Add(Skipped("noop_incremental", null, reason));
        var editReason = fixturePresent ? reason : "--solution runs never edit files";
        operations.Add(Skipped("incremental_1_file", null, editReason));
        operations.Add(Skipped("incremental_20_files", null, editReason));
        AddAnchorSkipped(operations, anchors == null ? "no anchors available (pass --anchors for --solution runs)" : reason);
        operations.Add(Skipped("search", null, anchors == null ? "no anchors available" : reason));
        operations.Add(Skipped("grep", null, anchors == null ? "no anchors available" : reason));
        operations.Add(Skipped("find_symbol", null, anchors == null ? "no anchors available" : reason));
        operations.Add(Skipped("diff", null, reason));
        operations.Add(Skipped("find_symbol.freshness_auto_untouched", null, anchors == null ? "no anchors available" : reason));
        operations.Add(Skipped("find_symbol.freshness_hash_untouched", null, anchors == null ? "no anchors available" : reason));
        operations.Add(Skipped("find_symbol.freshness_auto_touched", null, fixturePresent ? reason : "--solution runs never touch files"));
        operations.Add(Skipped("find_symbol.freshness_hash_touched", null, fixturePresent ? reason : "--solution runs never touch files"));
        operations.Add(Skipped("dead_candidates", null, reason));
        operations.Add(Skipped("mcp_status.default", null, reason));
        operations.Add(Skipped("mcp_status.full", null, reason));
    }

    private static OperationRecord Skipped(string name, string? anchor, string reason)
        => new() { Name = name, Anchor = anchor, Skipped = true, SkipReason = reason };

    private static void SetupWorkDirectory(string workDir, bool keepWork)
    {
        Directory.CreateDirectory(workDir);
        var markerPath = Path.Combine(workDir, WorkMarkerFileName);
        if (Directory.EnumerateFileSystemEntries(workDir).Any())
        {
            if (!File.Exists(markerPath))
                throw new HarnessError($"--work '{workDir}' is not empty and has no {WorkMarkerFileName} marker. Point --work at a fresh or harness-owned directory.");
            var markerText = File.ReadAllText(markerPath);
            if (!markerText.StartsWith("lurp-perf-harness", StringComparison.Ordinal))
                throw new HarnessError($"{markerPath} was not written by this harness. Point --work at a fresh or harness-owned directory.");
        }

        File.WriteAllText(markerPath, WorkMarkerContent, new UTF8Encoding(false));
        if (keepWork)
            return;

        foreach (var dir in Directory.GetDirectories(workDir, "run-*"))
        {
            Directory.Delete(dir, recursive: true);
            Console.WriteLine($"cleaned previous run directory: {dir}");
        }
    }

    private static string[] ResolveToolArgv(HarnessOptions options, string? repoRoot)
    {
        if (!string.IsNullOrWhiteSpace(options.LurpCmd))
            return Tokenize(options.LurpCmd);
        var envCmd = Environment.GetEnvironmentVariable("LURP_CMD");
        if (!string.IsNullOrWhiteSpace(envCmd))
            return Tokenize(envCmd);
        if (repoRoot == null)
            throw new HarnessError("cannot locate the Lurp repo root (Lurp.slnx) for the default tool command; pass --lurp-cmd or set LURP_CMD.");
        var dll = Path.Combine(repoRoot, "src", "bin", "Release", "net10.0", "Lurp.dll");
        if (!File.Exists(dll))
            throw new HarnessError($"the default tool '{dll}' does not exist; build src in Release or pass --lurp-cmd.");
        return ["dotnet", dll];
    }

    private static string[] Tokenize(string commandLine)
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

    private static string ReadToolVersion(string[] toolArgv)
    {
        var outcome = ProcessRunner.Run(Session.BuildArgv(toolArgv, VersionArgs), 2 * 60 * 1000);
        if (outcome.ExitCode != 0 || outcome.TimedOut)
            throw new HarnessError($"'{string.Join(" ", toolArgv)} --version' exited {outcome.ExitCode}: {outcome.Stderr.Trim()}");
        var lines = outcome.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[0] : "";
    }

    private static (string? Commit, bool? Dirty) ReadRepoState(string? repoRoot)
    {
        if (repoRoot == null)
            return (null, null);

        var log = ProcessRunner.Run(["git", "-C", repoRoot, "log", "--oneline", "-1"], 60 * 1000);
        string? commit = null;
        if (log.ExitCode == 0 && !log.TimedOut)
        {
            var fields = log.Stdout.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 0)
                commit = fields[0];
        }

        var status = ProcessRunner.Run(["git", "-C", repoRoot, "status", "--short"], 60 * 1000);
        bool? dirty = status.ExitCode == 0 && !status.TimedOut ? status.Stdout.Trim().Length > 0 : null;
        return (commit, dirty);
    }

    private static AnchorSet ReadAnchors(JsonObject source, bool requireEditDocument)
    {
        var anchors = source["anchors"] as JsonObject ?? source;
        var upstream = JsonTools.GetString(anchors, "upstream_wide");
        var downstream = JsonTools.GetString(anchors, "downstream_wide");
        var middle = JsonTools.GetString(anchors, "middle");
        if (string.IsNullOrWhiteSpace(upstream) || string.IsNullOrWhiteSpace(downstream) || string.IsNullOrWhiteSpace(middle))
            throw new HarnessError("no usable 'anchors' object: upstream_wide, downstream_wide and middle are required. Regenerate the fixture with the current scripts/perf/gen, or pass --anchors for --solution runs.");
        var editDocument = JsonTools.GetString(anchors, "edit_document");
        if (requireEditDocument && string.IsNullOrWhiteSpace(editDocument))
            throw new HarnessError("the fixture anchors lack edit_document; regenerate it with the current scripts/perf/gen.");
        return new AnchorSet(upstream, downstream, middle, editDocument);
    }

    private static void WriteOutput(
        string outPath,
        HarnessOptions options,
        string[] toolArgv,
        string toolVersion,
        string? repoCommit,
        bool? repoDirty,
        string? runnerImage,
        JsonObject? fixtureManifest,
        string? fixtureRoot,
        string solutionPath,
        string workDir,
        string runDir,
        DateTime startedAt,
        IReadOnlyList<OperationRecord> operations)
    {
        if (fixtureManifest != null && fixtureRoot != null)
            fixtureManifest["path"] = fixtureRoot;

        var root = new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["tool_cmd"] = string.Join(" ", toolArgv),
            ["tool_version"] = toolVersion,
            ["repo_commit"] = repoCommit,
            ["repo_dirty"] = repoDirty,
            ["os"] = RuntimeInformation.OSDescription,
            ["runner_image"] = runnerImage,
            ["fixture"] = fixtureManifest,
            ["solution"] = fixtureManifest == null ? solutionPath : null,
            ["work_dir"] = workDir,
            ["run_dir"] = runDir,
            ["started_at_utc"] = startedAt.ToString("O", CultureInfo.InvariantCulture),
            ["finished_at_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["protocol"] = options.Protocol,
            ["runs"] = options.Runs,
            ["warmup"] = options.Warmup,
            ["keep_work"] = options.KeepWork,
            ["operations"] = BuildOperationsJson(operations)
        };

        var parent = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        File.WriteAllText(outPath, root.ToJsonString(IndentedJson), new UTF8Encoding(false));
        Console.WriteLine($"result JSON written: {outPath}");
    }

    private static JsonArray BuildOperationsJson(IReadOnlyList<OperationRecord> operations)
    {
        var array = new JsonArray();
        foreach (var op in operations)
        {
            var runs = new JsonArray();
            foreach (var run in op.Runs)
            {
                runs.Add(JsonSerializer.SerializeToNode(new
                {
                    wall_ms = run.WallMs,
                    peak_working_set_mb = run.PeakWorkingSetMb,
                    exit_code = run.ExitCode,
                    db_bytes = run.DbBytes,
                    db_delta_bytes = run.DbDeltaBytes,
                    timings_total_ms = run.TimingsTotalMs,
                    timings_steps = run.TimingsSteps,
                    metric_peak_working_set_mb = run.MetricPeakWorkingSetMb,
                    peak_cross_check = run.PeakCrossCheck,
                    error = run.Error
                }));
            }

            var summary = Summarize(op.Runs);
            object? median = summary == null
                ? null
                : new { wall_ms = summary.MedianWallMs, peak_working_set_mb = summary.MedianPeakMb };
            object? max = summary == null
                ? null
                : new { wall_ms = summary.MaxWallMs, peak_working_set_mb = summary.MaxPeakMb };
            array.Add(JsonSerializer.SerializeToNode(new
            {
                name = op.Name,
                anchor = op.Anchor,
                skipped = op.Skipped,
                skip_reason = op.SkipReason,
                failed = op.Failed,
                failure = op.Failure,
                server_exit_code = op.ServerExitCode,
                touched_files = op.TouchedFiles,
                freshness_state = op.FreshnessState,
                freshness_changed_document_count = op.FreshnessChangedDocumentCount,
                runs,
                median,
                max
            }));
        }

        return array;
    }

    private static void PrintSummary(IReadOnlyList<OperationRecord> operations)
    {
        Console.WriteLine();
        Console.WriteLine("| operation | anchor | runs | median wall_ms | max wall_ms | median peak_mb | max peak_mb | status |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|---|");
        foreach (var op in operations)
        {
            if (op.Skipped)
            {
                Console.WriteLine($"| {op.Name} | {op.Anchor ?? ""} | 0 |  |  |  |  | skipped: {op.SkipReason} |");
                continue;
            }

            var summary = Summarize(op.Runs);
            var status = op.Failed ? "FAILED" : "ok";
            if (summary == null)
            {
                Console.WriteLine($"| {op.Name} | {op.Anchor ?? ""} | 0 |  |  |  |  | {status} (no measured runs) |");
                continue;
            }

            var medianWall = summary.MedianWallMs.ToString("F0", CultureInfo.InvariantCulture);
            var medianPeak = summary.MedianPeakMb.ToString("F0", CultureInfo.InvariantCulture);
            Console.WriteLine($"| {op.Name} | {op.Anchor ?? ""} | {op.Runs.Count} | {medianWall} | {summary.MaxWallMs} | {medianPeak} | {summary.MaxPeakMb} | {status} |");
        }

        Console.WriteLine();
        Console.WriteLine("| index run | polled peak_mb | step-1 metric peak_mb | cross-check |");
        Console.WriteLine("|---|---:|---:|---|");
        foreach (var op in operations)
        {
            for (var i = 0; i < op.Runs.Count; i++)
            {
                var run = op.Runs[i];
                if (run.PeakCrossCheck == null)
                    continue;
                var metric = run.MetricPeakWorkingSetMb?.ToString(CultureInfo.InvariantCulture) ?? "";
                Console.WriteLine($"| {op.Name}#{i + 1} | {run.PeakWorkingSetMb} | {metric} | {run.PeakCrossCheck} |");
            }
        }
    }

    private static Summary? Summarize(List<RunRecord> runs)
    {
        if (runs.Count == 0)
            return null;
        var walls = runs.Select(run => run.WallMs).OrderBy(value => value).ToList();
        var peaks = runs.Select(run => run.PeakWorkingSetMb).OrderBy(value => value).ToList();
        return new Summary(Median(walls), walls[^1], Median(peaks), peaks[^1]);
    }

    private static double Median(List<long> values)
        => values.Count % 2 == 1
            ? values[values.Count / 2]
            : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2.0;

    private sealed record Summary(double MedianWallMs, long MaxWallMs, double MedianPeakMb, long MaxPeakMb);
}
