using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lurp.PerfHarness;

internal sealed class Session(
    HarnessOptions options,
    string[] toolArgv,
    string fixtureRoot,
    string solutionPath,
    string runDir,
    string primaryDir,
    AnchorSet? anchors)
{
    public const string SnapshotMetricPeak = "peak_working_set_mb";

    private const int ToolTimeoutMs = 30 * 60 * 1000;
    private const int McpStartupTimeoutMs = 120 * 1000;
    private const int CrossCheckPercent = 15;
    private const int TouchEveryNthDocument = 10;

    public static readonly string[] AnchorRoles = ["upstream_wide", "downstream_wide", "middle"];
    private static readonly Regex EditMethodPattern = new("PerfEdit[AB](\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public List<OperationRecord> Operations { get; } = [];

    public string? CurrentSnapshot { get; set; }

    public bool AnyFailure { get; private set; }

    public string? AnchorFqn(string role) => role switch
    {
        "upstream_wide" => anchors?.UpstreamWide,
        "downstream_wide" => anchors?.DownstreamWide,
        "middle" => anchors?.Middle,
        _ => null
    };

    public static string[] BuildArgv(string[] toolArgv, IReadOnlyList<string> args)
    {
        var argv = new string[toolArgv.Length + args.Count];
        for (var i = 0; i < toolArgv.Length; i++)
            argv[i] = toolArgv[i];
        for (var i = 0; i < args.Count; i++)
            argv[toolArgv.Length + i] = args[i];

        return argv;
    }

    public OperationRecord FullIndex()
    {
        var op = new OperationRecord { Name = "full_index" };
        for (var i = 1; i <= options.Warmup + options.Runs; i++)
        {
            var dir = Path.Combine(runDir, "full-index", $"run-{i:D2}");
            Directory.CreateDirectory(dir);
            var outcome = RunTool(["--mode=index", $"--solution={solutionPath}", $"--output-dir={dir}", "--strategy=full"]);
            if (i <= options.Warmup)
                continue;

            AddRun(op, RecordIndexOutcome(outcome, dir, before: null, freshDatabase: true).Record);
        }

        return op;
    }

    public OperationRecord Incremental(string name, int editCount)
    {
        var op = new OperationRecord { Name = name };
        var targets = editCount > 0 ? EditTargets(editCount) : [];
        for (var i = 1; i <= options.Warmup + options.Runs; i++)
        {
            if (editCount > 0)
                ApplyEdits(targets);

            var before = CurrentSnapshot;
            var outcome = RunTool(["--mode=index", $"--solution={solutionPath}", $"--output-dir={primaryDir}"]);
            var (record, latestSnapshotId) = RecordIndexOutcome(outcome, primaryDir, before, freshDatabase: false);
            if (latestSnapshotId != null)
                CurrentSnapshot = latestSnapshotId;
            if (i <= options.Warmup)
                continue;

            AddRun(op, record);
        }

        return op;
    }

    public OperationRecord Read(string name, IReadOnlyList<string> args, string? anchor = null)
    {
        var op = new OperationRecord { Name = name, Anchor = anchor };
        for (var i = 1; i <= options.Warmup + options.Runs; i++)
        {
            var outcome = RunTool(args);
            if (i <= options.Warmup)
                continue;

            var error = outcome.TimedOut
                ? $"timed out after {ToolTimeoutMs} ms"
                : outcome.ExitCode != 0 ? LastErrorLine(outcome.Stderr, outcome.Stdout) : null;
            AddRun(op, new RunRecord(outcome.WallMs, outcome.PeakWorkingSetMb, outcome.ExitCode, Error: error));
        }

        return op;
    }

    public OperationRecord Freshness(string name, string mode, string symbolFqn, string dbDir, int? touchedFiles = null, (string State, int ChangedCount)? expected = null)
    {
        var op = new OperationRecord { Name = name, TouchedFiles = touchedFiles };
        for (var i = 1; i <= options.Warmup + options.Runs; i++)
        {
            var outcome = RunTool(["--mode=find-symbol", $"--symbol={symbolFqn}", $"--freshness={mode}", "--output=json", "--quiet", $"--output-dir={dbDir}"]);
            if (i <= options.Warmup)
                continue;

            var error = outcome.TimedOut
                ? $"timed out after {ToolTimeoutMs} ms"
                : outcome.ExitCode != 0 ? LastErrorLine(outcome.Stderr, outcome.Stdout) : null;
            AddRun(op, new RunRecord(outcome.WallMs, outcome.PeakWorkingSetMb, outcome.ExitCode, Error: error));
            if (error != null)
                continue;

            var stamp = ReadFreshnessStamp(outcome.Stdout);
            if (stamp == null)
            {
                MarkFailed(op, expected is { } wanted
                    ? $"find-symbol output has no freshness block (expected state={wanted.State}, changed_document_count={wanted.ChangedCount})."
                    : "find-symbol output has no freshness block.");
                continue;
            }

            op.FreshnessState ??= stamp.Value.State;
            op.FreshnessChangedDocumentCount ??= stamp.Value.ChangedCount;
            if (expected is { } want && (stamp.Value.State != want.State || stamp.Value.ChangedCount != want.ChangedCount))
                MarkFailed(op, $"freshness assertion failed: state={stamp.Value.State} changed_document_count={stamp.Value.ChangedCount} (expected state={want.State} changed_document_count={want.ChangedCount}).");
        }

        return op;
    }

    public void FreshnessTouchedOps(string symbolFqn, string dbDir)
    {
        var touched = SelectTouchedDocuments();
        var originals = new List<(string FullPath, DateTime LastWriteTimeUtc)>(touched.Count);
        try
        {
            foreach (var relativePath in touched)
            {
                var fullPath = Path.Combine(fixtureRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                originals.Add((fullPath, File.GetLastWriteTimeUtc(fullPath)));
            }

            var touchedAtUtc = DateTime.UtcNow.AddMinutes(1);
            foreach (var (fullPath, _) in originals)
                File.SetLastWriteTimeUtc(fullPath, touchedAtUtc);

            Operations.Add(Freshness("find_symbol.freshness_auto_touched", "auto", symbolFqn, dbDir, touched.Count, ("stale", touched.Count)));
            Operations.Add(Freshness("find_symbol.freshness_hash_touched", "hash", symbolFqn, dbDir, touched.Count, ("fresh", 0)));
        }
        finally
        {
            foreach (var (fullPath, lastWriteTimeUtc) in originals)
            {
                try
                {
                    File.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
                {
                    Console.Error.WriteLine($"warning: could not restore the mtime of '{fullPath}': {ex.Message}");
                }
            }
        }
    }

    private List<string> SelectTouchedDocuments()
    {
        var relativePaths = Directory.EnumerateFiles(fixtureRoot, "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(fixtureRoot, path).Replace('\\', '/'))
            .Where(path => !IsBuildOutputPath(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var touched = new List<string>();
        for (var index = TouchEveryNthDocument - 1; index < relativePaths.Count; index += TouchEveryNthDocument)
            touched.Add(relativePaths[index]);
        return touched;
    }

    private static bool IsBuildOutputPath(string relativePath)
        => relativePath.Split('/').Any(segment =>
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase));

    private static (string State, int ChangedCount)? ReadFreshnessStamp(string stdout)
    {
        if (JsonTools.TryParseObject(stdout) is not { } json || json["freshness"] is not JsonObject freshness)
            return null;
        if (JsonTools.GetString(freshness, "state") is not { } state)
            return null;
        if (freshness["changed_document_count"] is not JsonValue changedValue || !changedValue.TryGetValue<int>(out var changed))
            return null;

        return (state, changed);
    }

    private void MarkFailed(OperationRecord op, string failure)
    {
        op.Failed = true;
        AnyFailure = true;
        if (op.Failure != null)
            return;

        op.Failure = failure;
        Console.WriteLine($"[failed] {op.Name}: {failure}");
    }

    public OperationRecord McpStatus(string name, bool full)
    {
        var op = new OperationRecord { Name = name };
        var arguments = new JsonObject();
        if (full)
            arguments["full"] = true;

        McpClient? client = null;
        try
        {
            client = new McpClient(BuildArgv(toolArgv, ["--mode=serve", $"--solution={solutionPath}", $"--output-dir={primaryDir}"]));
            client.Initialize(McpStartupTimeoutMs);
            for (var i = 1; i <= options.Warmup + options.Runs; i++)
            {
                var (latencyMs, peakMb, error) = client.Call("lurp_status", arguments, ToolTimeoutMs);
                if (i <= options.Warmup)
                    continue;

                AddRun(op, new RunRecord(latencyMs, peakMb, error == null ? 0 : 1, Error: error));
            }

            op.ServerExitCode = client.Close();
            if (op.ServerExitCode != 0)
            {
                op.Failed = true;
                AnyFailure = true;
                op.Failure = $"serve exited with code {op.ServerExitCode}.";
                Console.WriteLine($"[failed] {op.Name}: {op.Failure}");
            }
        }
        catch (Exception ex) when (ex is HarnessError or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            op.Failed = true;
            AnyFailure = true;
            op.Failure = ex.Message;
            Console.WriteLine($"[failed] {op.Name}: {ex.Message}");
        }
        finally
        {
            client?.Dispose();
        }

        return op;
    }

    public string? GetLatestSnapshotId(string dbDir)
    {
        var outcome = RunTool(["--mode=status", "--output=json", $"--output-dir={dbDir}"], 2 * 60 * 1000);
        if (outcome.ExitCode != 0 || outcome.TimedOut)
            return null;

        var json = JsonTools.TryParseObject(outcome.Stdout);
        if (json is null)
            return null;

        return JsonTools.GetString(json, "built_at_latest_snapshot_id")
               ?? JsonTools.GetString(json, "latest_snapshot_id");
    }

    public (long? TotalMs, JsonArray? Steps, long? PeakWorkingSetMb) GetTimingsSnapshot(string dbDir, string snapshotId)
    {
        var outcome = RunTool(["--mode=timings", "--json", $"--snapshot={snapshotId}", $"--output-dir={dbDir}"], 2 * 60 * 1000);
        if (outcome.ExitCode != 0 || outcome.TimedOut)
            return (null, null, null);

        var json = JsonTools.TryParseObject(outcome.Stdout);
        if (json == null)
            return (null, null, null);

        long? totalMs = null;
        if (json["total_ms"] is JsonValue totalValue && totalValue.TryGetValue<long>(out var total))
            totalMs = total;

        JsonArray? steps = null;
        if (json["steps"] is JsonArray rawSteps)
        {
            steps = new JsonArray();
            foreach (var node in rawSteps)
            {
                if (node is JsonObject step &&
                    JsonTools.GetString(step, "step") is { } stepName &&
                    step["elapsed_ms"] is JsonValue elapsedValue &&
                    elapsedValue.TryGetValue<long>(out var elapsedMs))
                {
                    steps.Add(new JsonObject { ["step"] = stepName, ["elapsed_ms"] = elapsedMs });
                }
            }
        }

        long? peak = null;
        if (json["metrics"] is JsonObject metrics &&
            metrics[SnapshotMetricPeak] is JsonValue peakValue &&
            peakValue.TryGetValue<long>(out var peakValueMb))
            peak = peakValueMb;

        return (totalMs, steps, peak);
    }

    private RunOutcome RunTool(IReadOnlyList<string> args, int timeoutMs = ToolTimeoutMs)
        => ProcessRunner.Run(BuildArgv(toolArgv, args), timeoutMs);

    private (RunRecord Record, string? LatestSnapshotId) RecordIndexOutcome(RunOutcome outcome, string dbDir, string? before, bool freshDatabase)
    {
        long? dbBytes = null;
        var dbPath = Path.Combine(dbDir, "index.db");
        if (File.Exists(dbPath))
            dbBytes = new FileInfo(dbPath).Length;

        var error = outcome.TimedOut
            ? $"timed out after {ToolTimeoutMs} ms"
            : outcome.ExitCode != 0 ? LastErrorLine(outcome.Stderr, outcome.Stdout) : null;
        if (error != null)
            return (new RunRecord(outcome.WallMs, outcome.PeakWorkingSetMb, outcome.ExitCode, dbBytes, Error: error), null);

        var after = GetLatestSnapshotId(dbDir);
        var wroteNewSnapshot = after != null && (freshDatabase || !string.Equals(after, before, StringComparison.Ordinal));
        long? metric = null;
        long? timingsTotalMs = null;
        JsonArray? timingsSteps = null;
        string crossCheck;
        if (!wroteNewSnapshot)
        {
            crossCheck = "no-new-snapshot";
        }
        else
        {
            (timingsTotalMs, timingsSteps, metric) = GetTimingsSnapshot(dbDir, after!);
            crossCheck = metric.HasValue ? CrossCheck(outcome.PeakWorkingSetMb, metric.Value) : "no-metric";
        }

        return (new RunRecord(outcome.WallMs, outcome.PeakWorkingSetMb, outcome.ExitCode, dbBytes, metric, crossCheck, null, TimingsTotalMs: timingsTotalMs, TimingsSteps: timingsSteps), after);
    }

    private void AddRun(OperationRecord op, RunRecord record)
    {
        if (record.DbBytes is { } bytes)
        {
            var previousBytes = op.Runs.LastOrDefault(run => run.DbBytes is not null)?.DbBytes;
            record = record with { DbDeltaBytes = previousBytes is { } previous ? bytes - previous : null };
        }

        op.Runs.Add(record);
        if (record.ExitCode == 0 && record.Error == null)
            return;

        op.Failed = true;
        AnyFailure = true;
        op.Failure ??= record.Error ?? $"exit code {record.ExitCode}";
        Console.WriteLine($"[failed] {op.Name}: {op.Failure}");
    }

    private List<string> EditTargets(int editCount)
    {
        var editDocument = anchors?.EditDocument;
        if (string.IsNullOrEmpty(editDocument))
            throw new HarnessError("the edit operations need an edit_document anchor, and none is available.");

        if (editCount == 1)
            return [editDocument];

        var relativeDir = Path.GetDirectoryName(editDocument)?.Replace('\\', '/') ?? "";
        var absoluteDir = Path.Combine(fixtureRoot, relativeDir.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(absoluteDir))
            throw new HarnessError($"the edit_document directory '{absoluteDir}' does not exist in the fixture.");

        var candidates = Directory.GetFiles(absoluteDir, "Unit*.cs")
            .Select(Path.GetFileName)
            .Where(name => name != null)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(editCount)
            .Select(name => relativeDir.Length == 0 ? name! : $"{relativeDir}/{name}")
            .ToList();
        if (candidates.Count < editCount)
            throw new HarnessError($"the fixture directory '{absoluteDir}' holds only {candidates.Count} Unit*.cs files; {editCount} edits are required.");

        return candidates;
    }

    private void ApplyEdits(IReadOnlyList<string> targets)
    {
        if (fixtureRoot.Length == 0)
            throw new HarnessError("edit operations are only allowed for generated fixtures.");

        foreach (var target in targets)
        {
            var fullPath = Path.Combine(fixtureRoot, target.Replace('/', Path.DirectorySeparatorChar));
            if (!PathTools.IsInside(fullPath, fixtureRoot))
                throw new HarnessError($"edit target '{fullPath}' is outside the fixture '{fixtureRoot}'.");

            var text = File.ReadAllText(fullPath);
            File.WriteAllText(fullPath, AppendEditMethod(text));
        }
    }

    private static string AppendEditMethod(string text)
    {
        var brace = text.LastIndexOf('}');
        if (brace < 0)
            throw new HarnessError("an edit target has no closing brace to append the edited method to.");

        var next = 1;
        foreach (Match match in EditMethodPattern.Matches(text))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= next)
                next = number + 1;
        }

        var block = next % 2 == 1
            ? $"    // lurp-perf-edit variant=A\n    public int PerfEditA{next}(int value) => value + {100 + next};\n"
            : $"    // lurp-perf-edit variant=B\n    public int PerfEditB{next}(int value) => value + {200 + next};\n";
        return text.Insert(brace, block);
    }

    private static string CrossCheck(long polledMb, long metricMb)
    {
        if (metricMb <= 0)
            return "mismatch";

        var delta = Math.Abs(polledMb - metricMb);
        var tolerance = (long)Math.Ceiling(metricMb * CrossCheckPercent / 100.0);
        return delta <= tolerance ? "ok" : "mismatch";
    }

    private static string LastErrorLine(string stderr, string stdout)
    {
        var text = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = lines.Length > 0 ? lines[^1] : "non-zero exit code with no output";
        return line.Length <= 500 ? line : line[..500];
    }
}
