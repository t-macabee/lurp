using System.Text.Json;
using Lurp.Handlers;

namespace Lurp.Tests;

public sealed class ImpactHandlerPagingTests : IntegrationTestBase
{
    private async Task<string> IndexInitialAsync()
    {
        CreateProject("ImpactProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace ImpactProj {
                    public class Foo {
                        public void Bar() {}
                        public void Caller() { Bar(); }
                        public void UpstreamCaller() { Caller(); }
                    }
                    public class Extra {
                        public void ExtraMethod() { new Foo().Bar(); }
                    }
                }
                """,
            ["Extra2.cs"] = """
                namespace ImpactProj {
                    public class Another {
                        public void AnotherMethod() { new Foo().Caller(); }
                    }
                }
                """
        });
        return await RunFullIndexAsync(DbPath);
    }

    private async Task<string> IndexSecondAsync()
    {
        WriteFile("ImpactProj", "Models.cs", """
            namespace ImpactProj {
                public class Foo {
                    public void Bar(int x) {}
                    public void Caller() { Bar(1); }
                    public void UpstreamCaller() { Caller(); }
                    public void NewMethod() {}
                }
                public class Extra {
                    public void ExtraMethod() { new Foo().Bar(1); }
                }
            }
            """);
        return await RunIncrementalIndexAsync();
    }

    private static RunResult RunCaptured(Action action)
    {
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        CliExitException? failure = null;
        try
        {
            Console.SetOut(stdout);
            action();
        }
        catch (CliExitException ex)
        {
            failure = ex;
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return new RunResult(stdout.ToString(), failure);
    }

    [Fact]
    public async Task ImpactHandler_Paging_MatchesFullListAndSortsGroups()
    {
        await IndexInitialAsync();
        var snap2 = await IndexSecondAsync();

        string barId;
        using (var store = OpenStore(DbPath))
        {
            var ids = store.GetSymbolIdsInSnapshot(snap2);
            barId = ids.First(id => store.GetSymbolInfo(id, snap2)?.FullyQualifiedName?.Contains("ImpactProj.Foo.Bar") == true);
        }

        // 1. A run with a large --max-paths= value gives the full ordered list of paths.
        var fullResult = RunCaptured(() => ImpactHandler.Run([
            "--mode=impact",
            $"--symbol={barId}",
            "--direction=upstream",
            $"--output-dir={TestDir}",
            "--output=json",
            "--max-paths=50"
        ]));

        Assert.Null(fullResult.Failure);

        using var fullDoc = JsonDocument.Parse(fullResult.Stdout);
        var fullRoot = fullDoc.RootElement;

        var list1 = new List<string>();
        foreach (var p in fullRoot.GetProperty("paths").EnumerateArray())
        {
            var chain = string.Join(">", p.GetProperty("hops").EnumerateArray().Select(h => $"{h.GetProperty("source_symbol_id").GetString()}-{h.GetProperty("edge_kind").GetString()}->{h.GetProperty("target_symbol_id").GetString()}"));
            list1.Add(chain);
        }

        Assert.True(list1.Count >= 2, $"Expected at least 2 upstream paths for Bar, got {list1.Count}.");

        // 2. Page through the results with --max-paths=1 and --cursor=<truncated.cursor> until truncated is null.
        var pagedList = new List<string>();
        string? cursor = null;
        while (true)
        {
            var argsList = new List<string>
            {
                "--mode=impact",
                $"--symbol={barId}",
                "--direction=upstream",
                $"--output-dir={TestDir}",
                "--output=json",
                "--max-paths=1"
            };
            if (cursor != null)
            {
                argsList.Add($"--cursor={cursor}");
            }

            var pageResult = RunCaptured(() => ImpactHandler.Run(argsList.ToArray()));
            Assert.Null(pageResult.Failure);

            using var pageDoc = JsonDocument.Parse(pageResult.Stdout);
            var pageRoot = pageDoc.RootElement;

            foreach (var p in pageRoot.GetProperty("paths").EnumerateArray())
            {
                var chain = string.Join(">", p.GetProperty("hops").EnumerateArray().Select(h => $"{h.GetProperty("source_symbol_id").GetString()}-{h.GetProperty("edge_kind").GetString()}->{h.GetProperty("target_symbol_id").GetString()}"));
                pagedList.Add(chain);
            }

            if (pageRoot.TryGetProperty("truncated", out var truncProp) && truncProp.ValueKind != JsonValueKind.Null)
            {
                cursor = truncProp.GetProperty("cursor").GetString();
                Assert.False(string.IsNullOrEmpty(cursor));
            }
            else
            {
                break;
            }
        }

        Assert.Equal(list1, pagedList);

        // 3. groups is sorted by path_count from high to low, then by first_hop_target_symbol_id (ordinal).
        var groups = fullRoot.GetProperty("groups").EnumerateArray().ToList();
        for (var i = 1; i < groups.Count; i++)
        {
            var prev = groups[i - 1];
            var curr = groups[i];
            var prevCount = prev.GetProperty("path_count").GetInt32();
            var currCount = curr.GetProperty("path_count").GetInt32();

            if (prevCount == currCount)
            {
                var prevTarget = prev.GetProperty("first_hop_target_symbol_id").GetString()!;
                var currTarget = curr.GetProperty("first_hop_target_symbol_id").GetString()!;
                Assert.True(string.Compare(prevTarget, currTarget, StringComparison.Ordinal) <= 0);
            }
            else
            {
                Assert.True(prevCount > currCount);
            }
        }

        // 4. path_count_total equals the length of list 1.
        Assert.Equal(list1.Count, fullRoot.GetProperty("path_count_total").GetInt32());
    }

    private sealed record RunResult(string Stdout, CliExitException? Failure);
}
