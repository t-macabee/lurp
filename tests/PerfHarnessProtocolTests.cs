using System.Text.Json.Nodes;

namespace Lurp.Tests;

public sealed class PerfHarnessProtocolTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("lurp-perf-protocol-").FullName;

    public void Dispose()
    {
        TestDatabase.DeleteDirectory(_temp);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Slow")]
    public void HarnessRecordsFixtureOrSolutionProtocol(bool useFixture)
    {
        var fixture = GenerateFixture();
        var solution = Path.Combine(fixture, "Perf.slnx");
        TestProcess.RestoreSolution(solution);
        var toolCommand = string.Join(" ", LurpTool.Argv().Select(arg => $"\"{arg}\""));
        var output = Path.Combine(_temp, "result.json");
        var result = PerfHarnessResults.RunHarness([
            .. (useFixture ? new[] { $"--fixture={fixture}" } :
                [$"--solution={solution}", $"--anchors={Path.Combine(fixture, "perf-manifest.json")}"]),
            $"--work={Path.Combine(_temp, "work")}", $"--out={output}", "--runs=1", "--warmup=0",
            $"--lurp-cmd={toolCommand}"
        ]);
        Assert.True(result.Code == 0, result.Stdout + result.Stderr);

        var json = JsonNode.Parse(File.ReadAllText(output))!.AsObject();
        foreach (var name in new[] { "outline", "navigate", "get_symbol", "get_annotations.document", "grep.ignore_case" })
        {
            var operation = PerfHarnessResults.FindOperation(json, name);
            Assert.False((bool?)operation["skipped"]);
            Assert.False((bool?)operation["failed"]);
            Assert.Single(operation["runs"]!.AsArray());
        }

        var diff = PerfHarnessResults.FindOperation(json, "diff");
        var newFile = Assert.Single(json["operations"]!.AsArray(),
            op => (string?)op!["name"] == "find_symbol.freshness_auto_new_file")!.AsObject();
        if (useFixture)
        {
            Assert.All(json["operations"]!.AsArray(), op =>
            {
                Assert.False((bool?)op!["failed"]);
                Assert.False((bool?)op["skipped"]);
            });
            Assert.Single(diff["runs"]!.AsArray());
            Assert.Equal("stale", (string?)newFile["freshness_state"]);
            Assert.Equal(1, (int?)newFile["freshness_changed_document_count"]);
            Assert.False(File.Exists(Path.Combine(fixture, "Perf.P0", "LurpPerfNewFile.cs")));
        }
        else
        {
            foreach (var operation in new[] { diff, newFile })
            {
                Assert.True((bool?)operation["skipped"]);
                Assert.Equal("--solution runs never edit files", (string?)operation["skip_reason"]);
            }
        }
    }

    [Fact]
    public void MissingTool_Returns2()
    {
        var solution = Path.Combine(_temp, "x.sln");
        File.WriteAllText(solution, "");

        var (code, _, stderr) = PerfHarnessResults.RunHarness([
            $"--solution={solution}", $"--work={_temp}-work", $"--out={Path.Combine(_temp, "result.json")}",
            $"--lurp-cmd=\"{Path.Combine(_temp, "no-such-tool")}\""
        ]);

        Assert.Equal(2, code);
        Assert.Contains("no-such-tool", stderr);
        Assert.DoesNotContain("unexpected", stderr);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PathUnderAnotherCheckout_IsRefused(bool workInside)
    {
        var repo = Directory.CreateDirectory(Path.Combine(_temp, "fakerepo")).FullName;
        File.WriteAllText(Path.Combine(repo, "Lurp.slnx"), "");
        var work = Path.Combine(workInside ? repo : _temp, "sub", "work");
        var output = Path.Combine(workInside ? _temp : repo, "sub", "result.json");

        var (code, _, stderr) = PerfHarnessResults.RunHarness([
            $"--fixture={Path.Combine(_temp, "none")}", $"--work={work}", $"--out={output}"
        ]);

        Assert.Equal(2, code);
        Assert.Contains($"inside the Lurp repo at '{repo}'", stderr);
        Assert.Contains(workInside ? "--work" : "--out", stderr);
    }

    [Theory]
    [InlineData(true, "tool-a")]
    [InlineData(false, "tool-b")]
    public void ToolOptionTakesPrecedenceOverEnvironment(bool hasOption, string expected)
    {
        var solution = Path.Combine(_temp, "x.sln");
        File.WriteAllText(solution, "");
        var env = new Dictionary<string, string> { ["LURP_CMD"] = $"\"{Path.Combine(_temp, "tool-b")}\"" };
        var (code, _, stderr) = PerfHarnessResults.RunHarness([
            $"--solution={solution}", $"--work={_temp}-work", $"--out={Path.Combine(_temp, "result.json")}",
            .. (hasOption ? new[] { $"--lurp-cmd=\"{Path.Combine(_temp, "tool-a")}\"" } : [])
        ], env);

        Assert.Equal(2, code);
        Assert.Contains(expected, stderr);
    }

    [Fact]
    public void FixtureWithExtraSourceFile_IsRefused()
    {
        var fixture = GenerateFixture();
        File.WriteAllText(Path.Combine(fixture, "Perf.P0", "Extra.cs"), "class Extra {}");

        var (code, _, stderr) = PerfHarnessResults.RunHarness([
            $"--fixture={fixture}", $"--work={Path.Combine(_temp, "work")}",
            $"--out={Path.Combine(_temp, "result.json")}", $"--lurp-cmd=\"{Path.Combine(_temp, "no-such-tool")}\""
        ]);

        Assert.Equal(2, code);
        Assert.Contains("250", stderr);
        Assert.Contains("251", stderr);
    }

    private string GenerateFixture()
    {
        var fixture = Path.Combine(_temp, "fixture");
        var result = PerfGeneratorTests.Generate(fixture);
        Assert.True(result.Code == 0, result.Stderr);
        return fixture;
    }
}
