using System.Text;
using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

public sealed class ImpactReachabilityTests
{
    private const string Anchor = "A";
    private const string SnapshotId = "test-snapshot";

    private static ImpactReachability CreateReachability(List<EdgeRecord> edges)
    {
        return new ImpactReachability(new InMemoryEdgeStore(edges), SnapshotId);
    }

    private static EdgeRecord MakeEdge(string sourceId, string targetId, string kind = "Calls",
        string provenance = "compiler_proved", string? documentPath = null, int? startLine = null)
    {
        return new EdgeRecord
        {
            SnapshotId = SnapshotId,
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = kind,
            Provenance = provenance,
            SourceDocumentPath = documentPath,
            SourceStartLine = startLine,
            ExtractorVersion = "1.0.0"
        };
    }

    // ── Chains and diamonds ─────────────────────────────────────────────

    [Fact]
    public void Trace_Chain_VisitsEachSymbolOnceInDepthOrderAndEmitsOneLeafPath()
    {
        var result = CreateReachability([
            MakeEdge("A", "B"),
            MakeEdge("B", "C"),
            MakeEdge("C", "D")
        ]).Trace(Anchor, ImpactDirection.Downstream, maxDepth: 10);

        Assert.Equal(["B", "C", "D"], result.Symbols.Select(symbol => symbol.SymbolId));
        Assert.Equal([1, 2, 3], result.Symbols.Select(symbol => symbol.Depth));
        Assert.All(result.Symbols, symbol => Assert.Equal(1L, symbol.ShortestPathCount));
        Assert.All(result.Symbols, symbol => Assert.False(symbol.IsFrontier));
        Assert.DoesNotContain(result.Symbols, symbol => symbol.SymbolId == Anchor);
        Assert.Empty(result.WitnessPath(Anchor));

        var hops = result.WitnessPath("D");
        Assert.Equal(3, hops.Count);
        Assert.Equal(("A", "B"), (hops[0].SourceSymbolId, hops[0].TargetSymbolId));
        Assert.Equal(("B", "C"), (hops[1].SourceSymbolId, hops[1].TargetSymbolId));
        Assert.Equal(("C", "D"), (hops[2].SourceSymbolId, hops[2].TargetSymbolId));

        var leaf = Assert.Single(result.WitnessLeafPaths());
        Assert.Equal(3, leaf.TotalSteps);
        Assert.False(leaf.Truncated);
    }

    [Fact]
    public void Trace_Diamond_ShortestPathCountCountsBothRoutesAndPrefersLowerParent()
    {
        // Upstream diamond: A ← B, A ← C, B ← D, C ← D.
        var result = CreateReachability([
            MakeEdge("B", "A"),
            MakeEdge("C", "A"),
            MakeEdge("D", "B"),
            MakeEdge("D", "C")
        ]).Trace(Anchor, ImpactDirection.Upstream, maxDepth: 10);

        var d = result.Symbols.Single(symbol => symbol.SymbolId == "D");
        Assert.Equal(2, d.Depth);
        Assert.Equal(2L, d.ShortestPathCount);

        // D was first reached from B because B sorts before C.
        var hops = result.WitnessPath("D");
        Assert.Equal(2, hops.Count);
        Assert.Equal("B", hops[0].SourceSymbolId);
        Assert.Equal(("D", "B"), (hops[1].SourceSymbolId, hops[1].TargetSymbolId));
    }

    [Fact]
    public void Trace_TwoCallsOnOneInstance_ShareTheEdgeCacheAcrossTraces()
    {
        // Upstream diamond D → {B, C} → {A1, A2}: both anchors reach D, and only
        // the first Trace queries D's incoming edges.
        var store = new InMemoryEdgeStore([
            MakeEdge("B", "A1"),
            MakeEdge("C", "A1"),
            MakeEdge("B", "A2"),
            MakeEdge("C", "A2"),
            MakeEdge("D", "B"),
            MakeEdge("D", "C")
        ]);
        var reachability = new ImpactReachability(store, SnapshotId);

        var first = reachability.Trace("A1", ImpactDirection.Upstream, maxDepth: 10);
        var second = reachability.Trace("A2", ImpactDirection.Upstream, maxDepth: 10);

        Assert.Contains(first.Symbols, symbol => symbol.SymbolId == "D");
        Assert.Contains(second.Symbols, symbol => symbol.SymbolId == "D");
        Assert.Equal(1, store.IncomingEdgeQueryCount("D"));
    }

    [Fact]
    public void Trace_Cycle_TerminatesWithEachSymbolOnce()
    {
        var result = CreateReachability([
            MakeEdge("A", "B"),
            MakeEdge("B", "C"),
            MakeEdge("C", "A")
        ]).Trace(Anchor, ImpactDirection.Downstream, maxDepth: 10);

        Assert.Equal(["B", "C"], result.Symbols.Select(symbol => symbol.SymbolId));
        Assert.All(result.Symbols, symbol => Assert.Equal(1L, symbol.ShortestPathCount));
        Assert.Equal(2, result.WitnessPath("C").Count);
    }

    [Fact]
    public void Trace_TwoEdgeRowsBetweenOnePair_CountAsTwoRoutes()
    {
        var result = CreateReachability([
            MakeEdge("A", "B", "Calls"),
            MakeEdge("A", "B", "StaticallyCalls")
        ]).Trace(Anchor, ImpactDirection.Downstream, maxDepth: 10);

        var b = result.Symbols.Single(symbol => symbol.SymbolId == "B");
        Assert.Equal(1, b.Depth);
        Assert.Equal(2L, b.ShortestPathCount);
    }

    // ── Filters ─────────────────────────────────────────────────────────

    [Fact]
    public void Trace_KindFilter_FollowsOnlyAllowedKinds()
    {
        var result = CreateReachability([
            MakeEdge("A", "B", "Calls"),
            MakeEdge("A", "C", "Constructs"),
            MakeEdge("B", "D", "Calls"),
            MakeEdge("C", "D", "Calls")
        ]).Trace(Anchor, ImpactDirection.Downstream, ["Calls"], maxDepth: 10);

        Assert.Equal(["B", "D"], result.Symbols.Select(symbol => symbol.SymbolId));
        Assert.Equal(1L, result.Symbols.Single(symbol => symbol.SymbolId == "D").ShortestPathCount);
    }

    [Fact]
    public void Trace_ProvenanceFilter_FollowsOnlyAllowedProvenance()
    {
        var result = CreateReachability([
            MakeEdge("A", "B"),
            MakeEdge("A", "C", "Calls", "framework_derived"),
            MakeEdge("B", "D"),
            MakeEdge("C", "D", "Calls", "framework_derived")
        ]).Trace(Anchor, ImpactDirection.Downstream, allowedProvenance: ["compiler_proved"], maxDepth: 10);

        Assert.Equal(["B", "D"], result.Symbols.Select(symbol => symbol.SymbolId));
        Assert.Equal(1L, result.Symbols.Single(symbol => symbol.SymbolId == "D").ShortestPathCount);
    }

    // ── Depth bound and frontier ────────────────────────────────────────

    [Fact]
    public void Trace_MaxDepth_FrontierTrueWhenFilteredEdgeLeavesWitnessPath()
    {
        var result = CreateReachability([
            MakeEdge("A", "B"),
            MakeEdge("B", "C"),
            MakeEdge("C", "D")
        ]).Trace(Anchor, ImpactDirection.Downstream, maxDepth: 2);

        Assert.False(result.Symbols.Single(symbol => symbol.SymbolId == "B").IsFrontier);
        Assert.True(result.Symbols.Single(symbol => symbol.SymbolId == "C").IsFrontier);

        var leaf = Assert.Single(result.WitnessLeafPaths());
        Assert.Equal("C", leaf.Hops[^1].TargetSymbolId);
        Assert.True(leaf.Truncated);
        Assert.Equal("max depth reached", leaf.TruncationReason);
    }

    [Fact]
    public void Trace_MaxDepth_FrontierFalseWhenEdgesStayOnWitnessPath()
    {
        var result = CreateReachability([
            MakeEdge("A", "B"),
            MakeEdge("B", "C"),
            MakeEdge("C", "B")
        ]).Trace(Anchor, ImpactDirection.Downstream, maxDepth: 2);

        var c = result.Symbols.Single(symbol => symbol.SymbolId == "C");
        Assert.Equal(2, c.Depth);
        Assert.False(c.IsFrontier);

        var leaf = Assert.Single(result.WitnessLeafPaths());
        Assert.False(leaf.Truncated);
        Assert.Null(leaf.TruncationReason);
    }

    // ── Directions, determinism, saturation, cross-check ────────────────

    [Fact]
    public void Trace_BothDirections_FollowTheCorrectEdgeEnd()
    {
        var edges = new List<EdgeRecord>
        {
            MakeEdge("X", "Y"),
            MakeEdge("Y", "Z")
        };

        var downstream = CreateReachability(edges).Trace("X", ImpactDirection.Downstream, maxDepth: 10);
        Assert.Equal(["Y", "Z"], downstream.Symbols.Select(symbol => symbol.SymbolId));
        Assert.Equal(("X", "Y"), (downstream.Symbols[0].ParentHop.SourceSymbolId, downstream.Symbols[0].ParentHop.TargetSymbolId));

        var upstream = CreateReachability(edges).Trace("Z", ImpactDirection.Upstream, maxDepth: 10);
        Assert.Equal(["Y", "X"], upstream.Symbols.Select(symbol => symbol.SymbolId));
        Assert.Equal(("Y", "Z"), (upstream.Symbols[0].ParentHop.SourceSymbolId, upstream.Symbols[0].ParentHop.TargetSymbolId));

        // Step 3 relies on the reached symbol being the near end of its own
        // parent hop: upstream, the reached symbol is the hop source; downstream,
        // it is the hop target.
        Assert.All(upstream.Symbols, symbol => Assert.Equal(symbol.SymbolId, symbol.ParentHop.SourceSymbolId));
        Assert.All(downstream.Symbols, symbol => Assert.Equal(symbol.SymbolId, symbol.ParentHop.TargetSymbolId));
    }

    [Fact]
    public void Trace_ReversedEdgeInputOrder_ProducesByteIdenticalResults()
    {
        var edges = new List<EdgeRecord>
        {
            MakeEdge("A", "B", "Calls", "compiler_proved", "src/b.cs", 10),
            MakeEdge("A", "C", "Constructs", "compiler_proved"),
            MakeEdge("A", "C", "Calls", "framework_derived", "src/c.cs", 5),
            MakeEdge("B", "C", "Calls"),
            MakeEdge("B", "D", "Calls"),
            MakeEdge("C", "D", "Calls"),
            MakeEdge("D", "E", "Calls"),
            MakeEdge("C", "E", "StaticallyCalls")
        };

        var forwardResult = CreateReachability(edges).Trace(Anchor, ImpactDirection.Downstream, maxDepth: 3);
        var reversedResult = CreateReachability(Enumerable.Reverse(edges).ToList())
            .Trace(Anchor, ImpactDirection.Downstream, maxDepth: 3);

        Assert.Equal(Encoding.UTF8.GetBytes(Dump(forwardResult)), Encoding.UTF8.GetBytes(Dump(reversedResult)));
    }

    [Fact]
    public void Trace_LayeredGraph_OverflowingCountsSaturateAtLongMaxValue()
    {
        const int links = 70;
        var edges = new List<EdgeRecord>();
        for (var i = 0; i < links; i++)
        {
            edges.Add(MakeEdge($"N{i:D3}", $"N{i + 1:D3}", "Calls"));
            edges.Add(MakeEdge($"N{i:D3}", $"N{i + 1:D3}", "StaticallyCalls"));
        }

        var result = CreateReachability(edges).Trace("N000", ImpactDirection.Downstream, maxDepth: links + 1);

        var last = result.Symbols.Single(symbol => symbol.SymbolId == $"N{links:D3}");
        Assert.Equal(links, last.Depth);
        Assert.Equal(long.MaxValue, last.ShortestPathCount);
    }

    // Frozen characterization: reached sets pinned as literals after the
    // path-enumerating cross-check that established them was deleted.
    [Fact]
    public void Trace_RandomGraphs_ReachedSetsMatchFrozenExpectations()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["1:Downstream"] = "N07,N17,N03,N06,N10,N13,N02,N05,N11,N16,N19,N20,N24,N28,N01,N04,N09,N12,N14,N21,N22,N26,N27,N29",
            ["1:Upstream"] = "N02,N11,N20,N03,N04,N10,N13,N14,N25,N28,N01,N07,N09,N12,N18,N26,N29,N05,N08,N16,N19,N21,N23,N24,N27",
            ["2:Downstream"] = "N03,N14,N15,N01,N07,N09,N23,N04,N08,N10,N12,N16,N19,N21,N22,N24,N28,N02,N05,N06,N11,N13,N17,N18,N25,N26,N27,N29",
            ["2:Upstream"] = "N06,N22,N26,N01,N02,N05,N19,N20,N21,N27,N03,N07,N08,N09,N10,N11,N12,N13,N15,N16,N17,N18,N24,N23,N28",
            ["3:Downstream"] = "N11,N19,N14,N20,N02,N07,N13,N21,N22,N26,N05,N06,N10,N12,N17,N18,N27,N28",
            ["3:Upstream"] = "N02,N20,N08,N11,N14,N15,N21,N22,N24,N29,N03,N04,N06,N10,N12,N17,N23,N28,N05,N07,N13,N16,N27"
        };

        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var seed in new[] { 1, 2, 3 })
        {
            var edges = GenerateRandomEdges(seed, nodeCount: 30, edgeCount: 90);
            foreach (var direction in new[] { ImpactDirection.Downstream, ImpactDirection.Upstream })
            {
                var reached = new ImpactReachability(new InMemoryEdgeStore(edges), SnapshotId)
                    .Trace("N00", direction, maxDepth: 4)
                    .Symbols.Select(symbol => symbol.SymbolId);
                actual[$"{seed}:{direction}"] = string.Join(",", reached);
            }
        }

        Assert.Equal(
            expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} => {pair.Value}"),
            actual.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} => {pair.Value}"));
    }

    private static List<EdgeRecord> GenerateRandomEdges(int seed, int nodeCount, int edgeCount)
    {
        var random = new Random(seed);
        var nodes = Enumerable.Range(0, nodeCount).Select(index => $"N{index:D2}").ToList();
        var edges = new List<EdgeRecord>(edgeCount);
        for (var i = 0; i < edgeCount; i++)
            edges.Add(MakeEdge(nodes[random.Next(nodeCount)], nodes[random.Next(nodeCount)]));
        return edges;
    }

    private static string Dump(ImpactReachabilityResult result)
    {
        return JsonSerializer.Serialize(new
        {
            anchor = result.AnchorSymbolId,
            symbols = result.Symbols.Select(symbol => new
            {
                symbol.SymbolId,
                symbol.Depth,
                symbol.ShortestPathCount,
                symbol.IsFrontier,
                parent_hop = new
                {
                    symbol.ParentHop.SourceSymbolId,
                    symbol.ParentHop.TargetSymbolId,
                    symbol.ParentHop.EdgeKind,
                    symbol.ParentHop.Provenance,
                    symbol.ParentHop.SourceDocument,
                    symbol.ParentHop.SourceLine,
                    symbol.ParentHop.SourceColumn,
                    symbol.ParentHop.SourceEndLine,
                    symbol.ParentHop.SourceEndColumn
                }
            }),
            leaves = result.WitnessLeafPaths().Select(path => new
            {
                path.Truncated,
                path.TruncationReason,
                path.TotalSteps,
                hops = path.Hops.Select(hop => new
                {
                    hop.SourceSymbolId,
                    hop.TargetSymbolId,
                    hop.EdgeKind,
                    hop.Provenance,
                    hop.SourceDocument,
                    hop.SourceLine,
                    hop.SourceColumn,
                    hop.SourceEndLine,
                    hop.SourceEndColumn
                })
            })
        });
    }

    // ── In-memory IEdgeStore implementation ──

    private sealed class InMemoryEdgeStore(List<EdgeRecord> edges) : IEdgeStore
    {
        private readonly Dictionary<string, int> _incomingEdgeQueries = [];

        public int IncomingEdgeQueryCount(string symbolId) => _incomingEdgeQueries.GetValueOrDefault(symbolId);

        public List<EdgeRecord> GetEdges(string snapshotId, string? symbolId = null)
        {
            var filtered = edges.Where(e => e.SnapshotId == snapshotId);
            if (symbolId != null)
                filtered = filtered.Where(e =>
                    e.SourceSymbolId == symbolId || e.TargetSymbolId == symbolId);
            return [.. filtered];
        }

        public List<EdgeRecord> GetIncomingEdges(string snapshotId, string symbolId)
        {
            _incomingEdgeQueries[symbolId] = _incomingEdgeQueries.GetValueOrDefault(symbolId) + 1;
            return [.. edges.Where(e => e.SnapshotId == snapshotId && e.TargetSymbolId == symbolId)];
        }

        public List<EdgeRecord> GetOutgoingEdges(string snapshotId, string symbolId)
        {
            return [.. edges.Where(e => e.SnapshotId == snapshotId && e.SourceSymbolId == symbolId)];
        }

        // Unused members — throw to catch accidental usage
        public void SaveEdges(string snapshotId, IEnumerable<EdgeRecord> edges)
        {
            throw new NotSupportedException();
        }

        public void SaveDiagnostics(string snapshotId, IEnumerable<DiagnosticRecord> diagnostics)
        {
            throw new NotSupportedException();
        }

        public void SaveAnnotations(string snapshotId, IEnumerable<AnnotationRecord> annotations)
        {
            throw new NotSupportedException();
        }

        public List<DiagnosticRecord> GetDiagnostics(string snapshotId, string? projectName = null)
        {
            throw new NotSupportedException();
        }

        public List<AnnotationRecord> GetAnnotations(string snapshotId, string? symbolId = null)
        {
            throw new NotSupportedException();
        }

        public int CountEdges(string snapshotId)
        {
            throw new NotSupportedException();
        }

        public int CountDiagnostics(string snapshotId)
        {
            throw new NotSupportedException();
        }

        public List<EdgeRecord> GetEdgesByKind(string snapshotId, string kind)
        {
            throw new NotSupportedException();
        }

        public void DeleteEdgesByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
        {
            throw new NotSupportedException();
        }

        public void DeleteEdgesWithNullDocumentPathForAssemblies(string snapshotId,
            IEnumerable<string> assemblyIdentities)
        {
            throw new NotSupportedException();
        }

        public void DeleteEdgesWithNullDocumentPathForSymbols(string snapshotId, IEnumerable<string> symbolIds)
        {
            throw new NotSupportedException();
        }

        public void CopyEdgesToSnapshot(string fromSnapshotId, string toSnapshotId)
        {
            throw new NotSupportedException();
        }

        public void CopySnapshotDiagnostics(string fromSnapshotId, string toSnapshotId)
        {
            throw new NotSupportedException();
        }

        public void DeleteDiagnosticsByProjectNames(string snapshotId, IEnumerable<string> projectNames)
        {
            throw new NotSupportedException();
        }

        public void CopyAnnotationsToSnapshot(string fromSnapshotId, string toSnapshotId)
        {
            throw new NotSupportedException();
        }

        public bool TryRetractAnnotation(string snapshotId, long annotationId)
        {
            throw new NotSupportedException();
        }

        public void DeleteAnnotationsByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
        {
            throw new NotSupportedException();
        }

        public OrphanEdgeDropSummary DeleteOrphanEdges(string snapshotId)
        {
            throw new NotSupportedException();
        }

        public void PruneSnapshotGraphNodes(string snapshotId)
        {
            throw new NotSupportedException();
        }

        public void UpsertExtractors(IEnumerable<(string Name, string Version, string Description)> extractors)
        {
            throw new NotSupportedException();
        }

        public bool HasStaleExtractorVersions(string snapshotId)
        {
            throw new NotSupportedException();
        }
    }
}
