using System.Globalization;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Storage;

namespace Lurp.Tests;

public sealed class ImpactHandlerPagingTests : ImpactIndexFixture
{
    // Contract/acceptance (R141, decision 20): the upstream default follows every edge kind
    // except Declares, Contains and TestedBy; an explicit --kinds= replaces that default;
    // downstream keeps every kind.
    [Fact]
    public async Task ImpactHandler_UpstreamDefault_SkipsDeclaresContainsAndTestedBy()
    {
        var snapshotId = await IndexDirectionFixtureAsync();
        var hitId = ResolveSymbolId(snapshotId, "global::DirProj.Target.Hit");
        var targetId = ResolveSymbolId(snapshotId, "global::DirProj.Target");
        var callId = ResolveSymbolId(snapshotId, "global::DirProj.Caller.Call");
        var callHitId = ResolveSymbolId(snapshotId, "global::DirProj.TargetTests.CallHit");

        // (a) Nothing reached only over Declares (Target -> Hit) or TestedBy (Target ->
        // CallHit) appears in the default upstream answer.
        using (var upstream = RunImpactJson(hitId, "upstream"))
            Assert.Equal(
                new[] { callId, callHitId }.OrderBy(id => id, StringComparer.Ordinal),
                SymbolIds(upstream).OrderBy(id => id, StringComparer.Ordinal));

        using (var upstreamOfTest = RunImpactJson(callHitId, "upstream"))
            Assert.DoesNotContain(targetId, SymbolIds(upstreamOfTest));

        // (b) An explicit --kinds= replaces the default: Declares is followed again.
        using (var declaresOnly = RunImpactJson(hitId, "upstream", kinds: nameof(EdgeKind.Declares)))
            Assert.Equal([targetId], SymbolIds(declaresOnly));

        // (c) Downstream keeps every kind: declared members, nested types and the tests
        // that cover the type are correct downstream answers.
        using var downstream = RunImpactJson(targetId, "downstream", maxDepth: 1);
        Assert.Equal(
            new[] { nameof(EdgeKind.Contains), nameof(EdgeKind.Declares), nameof(EdgeKind.TestedBy) }
                .OrderBy(kind => kind, StringComparer.Ordinal),
            downstream.RootElement.GetProperty("groups").EnumerateArray()
                .Select(group => group.GetProperty("edge_kind").GetString()!)
                .OrderBy(kind => kind, StringComparer.Ordinal));
    }

    private JsonDocument RunImpactJson(string symbolId, string direction, string? kinds = null, int? maxDepth = null)
    {
        var args = new List<string>
        {
            "--mode=impact",
            $"--symbol={symbolId}",
            $"--direction={direction}",
            $"--output-dir={TestDir}",
            "--output=json",
            "--limit=200"
        };
        if (kinds is not null)
            args.Add($"--kinds={kinds}");
        if (maxDepth is not null)
            args.Add($"--max-depth={maxDepth.Value.ToString(CultureInfo.InvariantCulture)}");

        var result = TestConsole.Capture(() => ImpactHandler.Run([.. args]));
        Assert.Null(result.Failure);
        return JsonDocument.Parse(result.Stdout);
    }

    private static List<string> SymbolIds(JsonDocument document) =>
        document.RootElement.GetProperty("symbols").EnumerateArray()
            .Select(symbol => symbol.GetProperty("symbol_id").GetString()!)
            .ToList();

    // Contract/acceptance: pins the v2 impact symbols paging contract.
    [Fact]
    public async Task ImpactHandler_Paging_MatchesFullSymbolListAndSortsGroups()
    {
        await IndexInitialAsync();
        var snap2 = await IndexSecondAsync();

        string barId;
        using (var store = OpenStore(DbPath))
        {
            var ids = store.GetSymbolIdsInSnapshot(snap2);
            barId = ids.First(id => store.GetSymbolInfo(id, snap2)?.FullyQualifiedName?.Contains("ImpactProj.Foo.Bar") == true);
        }

        // 1. A run with a large --limit= value gives the full ordered symbol list.
        var fullResult = TestConsole.Capture(() => ImpactHandler.Run([
            "--mode=impact",
            $"--symbol={barId}",
            "--direction=upstream",
            $"--output-dir={TestDir}",
            "--output=json",
            "--limit=50"
        ]));

        Assert.Null(fullResult.Failure);

        using var fullDoc = JsonDocument.Parse(fullResult.Stdout);
        var fullRoot = fullDoc.RootElement;

        var list1 = fullRoot.GetProperty("symbols").EnumerateArray()
            .Select(s => s.GetProperty("symbol_id").GetString()!)
            .ToList();
        Assert.True(list1.Count >= 2, $"Expected at least 2 upstream symbols for Bar, got {list1.Count}.");

        // Every symbol carries a non-empty witness path.
        foreach (var symbol in fullRoot.GetProperty("symbols").EnumerateArray())
        {
            Assert.True(symbol.GetProperty("depth").GetInt32() >= 1);
            var witness = symbol.GetProperty("witness_path");
            Assert.True(witness.GetProperty("total_steps").GetInt32() >= 1);
            Assert.NotEmpty(witness.GetProperty("hops").EnumerateArray());
        }

        // 2. Page through the results with --limit=1 and --cursor=<truncated.cursor> until truncated is null.
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
                "--limit=1"
            };
            if (cursor != null)
            {
                argsList.Add($"--cursor={cursor}");
            }

            var pageResult = TestConsole.Capture(() => ImpactHandler.Run(argsList.ToArray()));
            Assert.Null(pageResult.Failure);

            using var pageDoc = JsonDocument.Parse(pageResult.Stdout);
            var pageRoot = pageDoc.RootElement;

            foreach (var symbol in pageRoot.GetProperty("symbols").EnumerateArray())
                pagedList.Add(symbol.GetProperty("symbol_id").GetString()!);

            if (pageRoot.TryGetProperty("truncated", out var truncProp) && truncProp.ValueKind != JsonValueKind.Null)
            {
                Assert.Equal("limit", truncProp.GetProperty("reason").GetString());
                cursor = truncProp.GetProperty("cursor").GetString();
                Assert.False(string.IsNullOrEmpty(cursor));
            }
            else
            {
                break;
            }
        }

        Assert.Equal(list1, pagedList);

        // 3. groups is sorted by symbol_count from high to low, then by first_hop_target_symbol_id (ordinal);
        //    every reached symbol is counted exactly once across the groups.
        var groups = fullRoot.GetProperty("groups").EnumerateArray().ToList();
        for (var i = 1; i < groups.Count; i++)
        {
            var prev = groups[i - 1];
            var curr = groups[i];
            var prevCount = prev.GetProperty("symbol_count").GetInt32();
            var currCount = curr.GetProperty("symbol_count").GetInt32();

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

        Assert.Equal(list1.Count, groups.Sum(g => g.GetProperty("symbol_count").GetInt32()));

        // 4. symbol_count_total equals the length of list 1.
        Assert.Equal(list1.Count, fullRoot.GetProperty("symbol_count_total").GetInt32());
    }

    // Contract/acceptance: pins the v2 summary and jsonl renderings.
    [Fact]
    public async Task ImpactHandler_SummaryAndJsonl_UseV2Shape()
    {
        await IndexInitialAsync();
        var snap2 = await IndexSecondAsync();

        string barId;
        using (var store = OpenStore(DbPath))
        {
            var ids = store.GetSymbolIdsInSnapshot(snap2);
            barId = ids.First(id => store.GetSymbolInfo(id, snap2)?.FullyQualifiedName?.Contains("ImpactProj.Foo.Bar") == true);
        }

        // Summary: header, symbols line, then one row per group.
        var summaryResult = TestConsole.Capture(() => ImpactHandler.Run([
            "--mode=impact",
            $"--symbol={barId}",
            "--direction=upstream",
            $"--output-dir={TestDir}",
            "--output=summary",
            "--limit=50"
        ]));
        Assert.Null(summaryResult.Failure);
        var summaryLines = summaryResult.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("impact upstream of ", summaryLines[0]);
        Assert.Contains("symbols: ", summaryLines[1]);
        Assert.Contains("distinct first hop(s)", summaryLines[1]);
        Assert.True(summaryLines.Length >= 3);

        // JSONL: one meta line (the response minus symbols), then one symbol line per page symbol.
        var jsonlResult = TestConsole.Capture(() => ImpactHandler.Run([
            "--mode=impact",
            $"--symbol={barId}",
            "--direction=upstream",
            $"--output-dir={TestDir}",
            "--output=jsonl",
            "--limit=50"
        ]));
        Assert.Null(jsonlResult.Failure);
        var jsonlLines = jsonlResult.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        using var metaDoc = JsonDocument.Parse(jsonlLines[0]);
        Assert.Equal("meta", metaDoc.RootElement.GetProperty("type").GetString());
        var meta = metaDoc.RootElement.GetProperty("meta");
        Assert.True(meta.GetProperty("symbol_count_total").GetInt32() >= 2);
        Assert.False(meta.TryGetProperty("symbols", out _));
        Assert.Equal(meta.GetProperty("symbol_count_total").GetInt32(), jsonlLines.Length - 1);

        foreach (var line in jsonlLines.Skip(1))
        {
            using var symbolDoc = JsonDocument.Parse(line);
            Assert.Equal("symbol", symbolDoc.RootElement.GetProperty("type").GetString());
            Assert.True(symbolDoc.RootElement.GetProperty("symbol").TryGetProperty("symbol_id", out _));
        }
    }

}
