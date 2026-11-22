using System.Text.Json.Nodes;
using Lurp.PerfHarness;
using HarnessProgram = Lurp.PerfHarness.Program;

namespace Lurp.Tests;

public sealed class PerfHarnessHelperTests
{
    private static readonly int[] RunOrder = [4, 1, 3, 5, 2];

    [Fact]
    public void MedianSortsAndAveragesMiddleValues()
    {
        Assert.Equal(2.5, ResultFile.Median([1, 2, 3, 4]));
        Assert.Equal(3, ResultFile.Median([5, 1, 4, 2, 3]));
        Assert.Equal(7, ResultFile.Median([7]));
    }

    [Theory]
    [InlineData(true, "gate", 5, 3)]
    [InlineData(false, "custom", 5, 1)]
    public void ProtocolDeterminesRunCounts(bool gate, string protocol, int runs, int warmup)
    {
        var options = HarnessOptions.Parse(gate
            ? ["--fixture=f", "--work=w", "--out=o", "--protocol=gate"]
            : ["--fixture=f", "--work=w", "--out=o"]);

        Assert.Equal(protocol, options.Protocol);
        Assert.Equal(runs, options.Runs);
        Assert.Equal(warmup, options.Warmup);
    }

    [Theory]
    [InlineData("--fixture=f --protocol=gate --runs=3", "cannot be combined with --protocol")]
    [InlineData("--fixture=f --protocol=fast", "unknown --protocol")]
    [InlineData("--fixture=f --solution=s", "exactly one")]
    [InlineData("", "exactly one")]
    [InlineData("--fixture=f --anchors=a", "--anchors is only used with --solution")]
    [InlineData("--fixture=f --runs=0", "--runs must be at least 1")]
    [InlineData("--fixture=f --warmup=-1", "--warmup must be 0 or more")]
    [InlineData("--fixture=f --max-ratio=3", "--max-ratio is only valid together with --evaluate")]
    public void InvalidOptionsAreRefused(string arguments, string reason)
    {
        var error = Assert.Throws<HarnessError>(() =>
            HarnessOptions.Parse(["--work=w", "--out=o", .. arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)]));

        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void EditsAlternateAndIncreaseBeforeTheLastBrace()
    {
        var first = Session.AppendEditMethod("class A\n{\n}\n");
        var second = Session.AppendEditMethod(first);
        var third = Session.AppendEditMethod(second);

        Assert.EndsWith("public int PerfEditA1(int value) => value + 101;\n}\n", first);
        Assert.EndsWith("public int PerfEditB2(int value) => value + 202;\n}\n", second);
        Assert.EndsWith("public int PerfEditA3(int value) => value + 103;\n}\n", third);
        Assert.Contains("PerfEditA1", third);
        Assert.Contains("PerfEditB2", third);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(1048576, 1)]
    [InlineData(1048577, 2)]
    public void PeakDisplayRoundsUpToWholeMiB(long bytes, long expected)
        => Assert.Equal(expected, ResultFile.CeilingMiB(bytes));

    [Fact]
    public void PeakBytesKeepExactPrecision()
    {
        // 106.9 MiB is 112092774 bytes. The stored measurement must not become 107 MiB.
        var peak = ResultFile.PeakMeasurement.ExactBytes(112092774);

        Assert.Equal(112092774, peak.Bytes);
        Assert.True(peak.Exact);
        Assert.Equal("106.9", ResultFile.FormatPeak(peak));
    }

    [Theory]
    [InlineData(115, 100, "ok")]
    [InlineData(85, 100, "ok")]
    [InlineData(116, 100, "mismatch")]
    [InlineData(84, 100, "mismatch")]
    [InlineData(0, 0, "mismatch")]
    public void PeakCrossCheckUsesFifteenPercent(long polled, long metric, string expected)
        => Assert.Equal(expected, Session.CrossCheck(polled, metric));

    [Theory]
    [InlineData("{\"change_count\":3}", null)]
    [InlineData("{\"change_count\":0}", "change_count was 0")]
    [InlineData("{\"change_count\":-1}", "change_count was -1")]
    [InlineData("{\"change_count\":1.5}", "change_count was 1.5")]
    [InlineData("invalid", "not a readable JSON object")]
    public void DiffOutputRequiresPositiveChangeCount(string stdout, string? expected)
    {
        var failure = Session.CheckDiffOutput(stdout);

        if (expected == null)
            Assert.Null(failure);
        else
            Assert.Contains(expected, failure);
    }

    [Fact]
    public void RepoDirty_IgnoresUntrackedFiles()
    {
        var dir = Directory.CreateTempSubdirectory("lurp-perf-git-").FullName;
        try
        {
            var clone = TestProcess.Run("git", ["clone", "--shared", "--quiet", TestRepoRoot.Find(), dir], TimeSpan.FromMinutes(1));
            Assert.True(clone.ExitCode == 0, clone.Stderr);
            File.WriteAllText(Path.Combine(dir, "perf-untracked-source.cs"), "");
            Assert.False(HarnessProgram.ReadRepoState(dir).Dirty);

            File.AppendAllText(Path.Combine(dir, "Lurp.slnx"), "\n");
            Assert.True(HarnessProgram.ReadRepoState(dir).Dirty);
        }
        finally
        {
            // A shallow source clone makes git clone --shared copy the pack, and git marks it
            // read-only. Windows cannot delete such a file, so clear the attribute first.
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }

            TestDatabase.DeleteDirectory(dir);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WriterResultsPassTheCorrespondingGate(bool fixture)
    {
        var dir = Directory.CreateTempSubdirectory("lurp-perf-writer-").FullName;
        try
        {
            var first = WriteResult(dir, "first.json", fixture, 4, 100);
            var second = WriteResult(dir, "second.json", fixture, 16, 300);

            var (code, stdout, stderr) = PerfHarnessResults.RunHarness(fixture
                ? [$"--evaluate={first},{second}"]
                : [$"--baseline={first}", $"--candidate={second}"]);

            Assert.True(code == 0, stdout + stderr);
            Assert.Contains("| full_index | 300 | 900 | 3.00 | 100.0 | 100.0 | 1.00 |", stdout);
        }
        finally
        {
            TestDatabase.DeleteDirectory(dir);
        }
    }

    private static string WriteResult(string dir, string file, bool fixture, int size, int interval)
    {
        var path = Path.Combine(dir, file);
        var options = HarnessOptions.Parse([
            fixture ? $"--fixture={dir}" : "--solution=Perf.slnx",
            $"--work={dir}", $"--out={path}", "--protocol=gate"
        ]);
        string[] names = fixture
            ? ["full_index", "incremental_1_file", "incremental_20_files", "diff", "dead_candidates"]
            : ["full_index", "noop_incremental", "impact.upstream.d6.middle", "context.default.middle", "search", "grep", "find_symbol", "dead_candidates"];
        var operations = new List<OperationRecord>();
        foreach (var name in names)
        {
            var op = new OperationRecord { Name = name };
            op.Runs.AddRange(RunOrder.Select(n => new RunRecord(
                fixture || name == "full_index" ? n * interval : 100,
                100 * PerfHarnessResults.MiB,
                0)));
            operations.Add(op);
        }

        var manifest = fixture ? new JsonObject { ["size"] = size, ["generator_version"] = "1.1.0", ["seed"] = 1 } : null;
        HarnessProgram.WriteOutput(path, options, ["lurp"], "lurp 4.0.0", null, false, null,
            "4ba9901", manifest, fixture ? dir : null, "Perf.slnx", dir, dir, DateTime.UtcNow, operations);
        return path;
    }
}
