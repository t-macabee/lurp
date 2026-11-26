using System.Globalization;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Storage;

namespace Lurp.Tests;

public sealed class ImpactHandlerPagingTests : ImpactIndexFixture
{
    [Fact]
    public async Task ImpactHandler_DefaultTraversal_SkipsDeclaresContainsAndTestedBy()
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

        var structuralKinds = new[] { nameof(EdgeKind.Contains), nameof(EdgeKind.Declares), nameof(EdgeKind.TestedBy) };
        using var downstream = RunImpactJson(targetId, "downstream", maxDepth: 1);
        Assert.DoesNotContain(downstream.RootElement.GetProperty("groups").EnumerateArray(),
            group => structuralKinds.Contains(group.GetProperty("edge_kind").GetString()));

        using var structural = RunImpactJson(targetId, "downstream", kinds: string.Join(",", structuralKinds), maxDepth: 1);
        Assert.Equal(structuralKinds, structural.RootElement.GetProperty("groups").EnumerateArray()
            .Select(group => group.GetProperty("edge_kind").GetString()).Order(StringComparer.Ordinal));
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

        var result = CapturingOutputSink.Capture(sink => ImpactHandler.Run([.. args], sink));
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

        var barId = ResolveSymbolId(snap2, "global::ImpactProj.Foo.Bar");
        var callerId = ResolveSymbolId(snap2, "global::ImpactProj.Foo.Caller");
        var upstreamCallerId = ResolveSymbolId(snap2, "global::ImpactProj.Foo.UpstreamCaller");
        var extraId = ResolveSymbolId(snap2, "global::ImpactProj.Extra.ExtraMethod");
        var anotherId = ResolveSymbolId(snap2, "global::ImpactProj.Another.AnotherMethod");

        // 1. A run with a large --limit= value gives the full ordered symbol list.
        var fullResult = CapturingOutputSink.Capture(sink => ImpactHandler.Run([
            "--mode=impact",
            $"--symbol={barId}",
            "--direction=upstream",
            $"--output-dir={TestDir}",
            "--output=json",
            "--limit=50"
        ], sink));

        Assert.Null(fullResult.Failure);

        using var fullDoc = JsonDocument.Parse(fullResult.Stdout);
        var fullRoot = fullDoc.RootElement;

        var list1 = fullRoot.GetProperty("symbols").EnumerateArray()
            .Select(s => s.GetProperty("symbol_id").GetString()!)
            .ToList();
        Assert.Equal([extraId, callerId, anotherId, upstreamCallerId], list1);

        // Every symbol carries a non-empty witness path and count 1.
        foreach (var symbol in fullRoot.GetProperty("symbols").EnumerateArray())
        {
            Assert.True(symbol.GetProperty("depth").GetInt32() >= 1);
            Assert.Equal(1L, symbol.GetProperty("shortest_path_count").GetInt64());
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

            var pageResult = CapturingOutputSink.Capture(sink => ImpactHandler.Run(argsList.ToArray(), sink));
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

        // 3. groups is sorted by symbol_count from high to low, then by the first-hop neighbor id (D63).
        var groups = fullRoot.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(2, groups.Count);
        Assert.Equal((callerId, barId, 3, 2), (
            groups[0].GetProperty("first_hop_source_symbol_id").GetString()!,
            groups[0].GetProperty("first_hop_target_symbol_id").GetString()!,
            groups[0].GetProperty("symbol_count").GetInt32(),
            groups[0].GetProperty("max_depth").GetInt32()));
        Assert.Equal((extraId, barId, 1, 1), (
            groups[1].GetProperty("first_hop_source_symbol_id").GetString()!,
            groups[1].GetProperty("first_hop_target_symbol_id").GetString()!,
            groups[1].GetProperty("symbol_count").GetInt32(),
            groups[1].GetProperty("max_depth").GetInt32()));

        Assert.Equal(list1.Count, groups.Sum(g => g.GetProperty("symbol_count").GetInt32()));

        // 4. symbol_count_total equals the length of list 1.
        Assert.Equal(list1.Count, fullRoot.GetProperty("symbol_count_total").GetInt32());

        // 5. Tie-break upstream: equal-count groups sort by first_hop_source_symbol_id (ordinal).
        using var callerUpstream = RunImpactJson(callerId, "upstream", maxDepth: 1);
        Assert.Equal([anotherId, upstreamCallerId], SymbolIds(callerUpstream));
        var tiedGroups = callerUpstream.RootElement.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(2, tiedGroups.Count);
        Assert.Equal((anotherId, callerId, 1, 1), (
            tiedGroups[0].GetProperty("first_hop_source_symbol_id").GetString()!,
            tiedGroups[0].GetProperty("first_hop_target_symbol_id").GetString()!,
            tiedGroups[0].GetProperty("symbol_count").GetInt32(),
            tiedGroups[0].GetProperty("max_depth").GetInt32()));
        Assert.Equal((upstreamCallerId, callerId, 1, 1), (
            tiedGroups[1].GetProperty("first_hop_source_symbol_id").GetString()!,
            tiedGroups[1].GetProperty("first_hop_target_symbol_id").GetString()!,
            tiedGroups[1].GetProperty("symbol_count").GetInt32(),
            tiedGroups[1].GetProperty("max_depth").GetInt32()));
    }

    [Fact]
    public async Task ImpactHandler_CursorFromPreviousSequenceLayout_IsRejected()
    {
        var snapshotId = await IndexInitialAsync();
        var barId = ResolveSymbolId(snapshotId, "global::ImpactProj.Foo.Bar");

        var first = CapturingOutputSink.Capture(sink => ImpactHandler.Run([
            "--mode=impact", $"--symbol={barId}", "--direction=upstream",
            $"--output-dir={TestDir}", "--output=json", "--limit=1"
        ], sink));
        Assert.Null(first.Failure);
        using var doc = JsonDocument.Parse(first.Stdout);
        var current = SequenceCursor.TryDecode(
            doc.RootElement.GetProperty("truncated").GetProperty("cursor").GetString()!)!;

        // The 4.1.0 fingerprint had no sequence marker.
        var stale = current with
        {
            Fingerprint = SequenceCursor.ComputeFingerprint(barId, "Upstream", "3", null, null, "symbols")
        };
        Assert.NotEqual(current.Fingerprint, stale.Fingerprint);

        var rejected = CapturingOutputSink.Capture(sink => ImpactHandler.Run([
            "--mode=impact", $"--symbol={barId}", "--direction=upstream",
            $"--output-dir={TestDir}", "--output=json", "--limit=1", $"--cursor={stale.Encode()}"
        ], sink));
        Assert.NotNull(rejected.Failure);
        Assert.Contains("request a fresh page", rejected.Failure.Message);
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
        var summaryResult = CapturingOutputSink.Capture(sink => ImpactHandler.Run([
            "--mode=impact",
            $"--symbol={barId}",
            "--direction=upstream",
            $"--output-dir={TestDir}",
            "--output=summary",
            "--limit=50"
        ], sink));
        Assert.Null(summaryResult.Failure);
        var summaryLines = summaryResult.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("impact upstream of ", summaryLines[0]);
        Assert.Contains("symbols: ", summaryLines[1]);
        Assert.Contains("distinct first hop(s)", summaryLines[1]);
        Assert.True(summaryLines.Length >= 3);

        // JSONL: one meta line (the response minus symbols), then one symbol line per page symbol.
        var jsonlResult = CapturingOutputSink.Capture(sink => ImpactHandler.Run([
            "--mode=impact",
            $"--symbol={barId}",
            "--direction=upstream",
            $"--output-dir={TestDir}",
            "--output=jsonl",
            "--limit=50"
        ], sink));
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

    [Fact]
    public async Task Impact_Upstream_HopShowsEverySiteOfTheCall()
    {
        // 1-based lines: Target() on line 4 (0-based 3), Twice() calls Target() on lines 7 and 8 (0-based 6 and 7).
        var snapshotId = await IndexFullAsync("App", new Dictionary<string, string>
        {
            ["C.cs"] = """
                namespace N;
                public class C
                {
                    public void Target() { }
                    public void Twice()
                    {
                        Target();
                        Target();
                    }
                }
                """
        });

        var targetId = ResolveSymbolId(snapshotId, "global::N.C.Target");
        var twiceId = ResolveSymbolId(snapshotId, "global::N.C.Twice");

        var result = CapturingOutputSink.Capture(sink => ImpactHandler.Run([
            "--mode=impact",
            $"--symbol={targetId}",
            "--direction=upstream",
            $"--output-dir={TestDir}",
            "--output=json",
            "--limit=50"
        ], sink));

        Assert.Null(result.Failure);
        using var doc = JsonDocument.Parse(result.Stdout);
        var twiceSymbol = doc.RootElement.GetProperty("symbols").EnumerateArray()
            .Single(s => s.GetProperty("symbol_id").GetString() == twiceId);
        var hop = Assert.Single(twiceSymbol.GetProperty("witness_path").GetProperty("hops").EnumerateArray());

        Assert.Equal(7, hop.GetProperty("source_line").GetInt32());
        Assert.Equal(2, hop.GetProperty("site_count").GetInt32());

        var sites = hop.GetProperty("sites").EnumerateArray()
            .Select(s => (
                Line: s.GetProperty("line").GetInt32(),
                Column: s.GetProperty("column").GetInt32(),
                EndLine: s.GetProperty("end_line").GetInt32(),
                EndColumn: s.GetProperty("end_column").GetInt32()))
            .ToList();

        Assert.Equal(
            [
                (7, 8, 7, 16),
                (8, 8, 8, 16)
            ],
            sites);
    }

    [Fact]
    public async Task Impact_HopWithManySites_IsCappedAndCounted()
    {
        var calls = string.Join(Environment.NewLine, Enumerable.Repeat("        Target();", 25));
        var source = $$"""
            namespace N;
            public class C
            {
                public void Target() { }
                public void Many()
                {
            {{calls}}
                }
            }
            """;

        var snapshotId = await IndexFullAsync("App", new Dictionary<string, string>
        {
            ["C.cs"] = source
        });

        var targetId = ResolveSymbolId(snapshotId, "global::N.C.Target");
        var manyId = ResolveSymbolId(snapshotId, "global::N.C.Many");

        var result = CapturingOutputSink.Capture(sink => ImpactHandler.Run([
            "--mode=impact",
            $"--symbol={targetId}",
            "--direction=upstream",
            $"--output-dir={TestDir}",
            "--output=json",
            "--limit=50"
        ], sink));

        Assert.Null(result.Failure);
        using var doc = JsonDocument.Parse(result.Stdout);
        var manySymbol = doc.RootElement.GetProperty("symbols").EnumerateArray()
            .Single(s => s.GetProperty("symbol_id").GetString() == manyId);
        var hop = Assert.Single(manySymbol.GetProperty("witness_path").GetProperty("hops").EnumerateArray());

        // First Target(); is on 1-based line 7; 20th is on 1-based line 26; 25th is on line 31.
        Assert.Equal(25, hop.GetProperty("site_count").GetInt32());
        var sites = hop.GetProperty("sites").EnumerateArray().ToList();
        Assert.Equal(20, sites.Count);
        Assert.Equal(7, sites[0].GetProperty("line").GetInt32());
        Assert.Equal(26, sites[^1].GetProperty("line").GetInt32());
    }

}
