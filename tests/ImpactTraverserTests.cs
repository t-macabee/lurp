using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

public sealed class ImpactTraverserTests
{
    private const string SnapshotId = "test-snapshot";

    private static ImpactTraverser CreateTraverser(List<EdgeRecord> edges)
    {
        var store = new InMemoryEdgeStore(edges);
        return new ImpactTraverser(store, SnapshotId);
    }

    private static EdgeRecord MakeEdge(string sourceId, string targetId, string kind = "Calls",
        string provenance = "compiler_proved")
    {
        return new EdgeRecord
        {
            SnapshotId = SnapshotId,
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = kind,
            Provenance = provenance,
            ExtractorVersion = "1.0.0"
        };
    }

    // ── Cycle detection ─────────────────────────────────────────────────

    [Fact]
    public void TraceImpact_Cycle_AB_Terminates()
    {
        var edges = new List<EdgeRecord>
        {
            MakeEdge("A", "B"),
            MakeEdge("B", "A")
        };
        var traverser = CreateTraverser(edges);

        var paths = traverser.TraceImpact("A", ImpactDirection.Downstream, maxDepth: 10);

        Assert.NotEmpty(paths);
        // Must terminate — no infinite loop despite A→B→A cycle
        foreach (var path in paths)
        {
            // A→B→A should terminate after B (one hop to B, then B→A visited)
            Assert.True(path.Hops.Count <= 2,
                $"Cycle path should terminate, got {path.Hops.Count} hops.");
        }
    }

    [Fact]
    public void TraceImpact_Cycle_AAA_SelfLoop()
    {
        var edges = new List<EdgeRecord>
        {
            MakeEdge("A", "A")
        };
        var traverser = CreateTraverser(edges);

        var paths = traverser.TraceImpact("A", ImpactDirection.Downstream, maxDepth: 10);

        // Self-loop: A is already visited, so no edge is followed and no
        // terminal path is emitted (hopsSoFar.Count == 0 path is not added).
        Assert.Empty(paths);
    }

    // ── maxDepth bound ──────────────────────────────────────────────────

    [Fact]
    public void TraceImpact_MaxDepth_TruncatesAtLimit()
    {
        var edges = new List<EdgeRecord>
        {
            MakeEdge("A", "B"),
            MakeEdge("B", "C"),
            MakeEdge("C", "D"),
            MakeEdge("D", "E"),
            MakeEdge("E", "F")
        };
        var traverser = CreateTraverser(edges);

        var paths = traverser.TraceImpact("A", ImpactDirection.Downstream, maxDepth: 2);

        Assert.NotEmpty(paths);
        var truncated = paths.Where(p => p.Truncated).ToList();
        Assert.NotEmpty(truncated);
        Assert.All(truncated, p =>
        {
            Assert.True(p.Hops.Count == 2,
                $"Truncated path should have exactly maxDepth (2) hops, got {p.Hops.Count}.");
            Assert.Equal("max depth reached", p.TruncationReason);
        });
    }

    [Fact]
    public void TraceImpact_MaxDepth_CompletesBeforeLimit()
    {
        var edges = new List<EdgeRecord>
        {
            MakeEdge("A", "B")
        };
        var traverser = CreateTraverser(edges);

        var paths = traverser.TraceImpact("A", ImpactDirection.Downstream, maxDepth: 10);

        Assert.NotEmpty(paths);
        Assert.All(paths, p =>
        {
            Assert.False(p.Truncated);
            Assert.Null(p.TruncationReason);
        });
    }

    // ── allowedEdgeKinds filter ─────────────────────────────────────────

    [Fact]
    public void TraceImpact_AllowedEdgeKinds_FiltersByKind()
    {
        var edges = new List<EdgeRecord>
        {
            MakeEdge("A", "B"),
            MakeEdge("A", "C", "Constructs"),
            MakeEdge("B", "D"),
            MakeEdge("C", "D")
        };
        var traverser = CreateTraverser(edges);

        var callsOnly = new HashSet<string> { "Calls" };
        var paths = traverser.TraceImpact("A", ImpactDirection.Downstream, callsOnly, maxDepth: 10);

        Assert.NotEmpty(paths);
        // Only Calls edges should be followed: A→B (Calls), B→D (Calls)
        // A→C (Constructs) should be skipped
        foreach (var path in paths) Assert.All(path.Hops, hop => Assert.Equal("Calls", hop.EdgeKind));
    }

    [Fact]
    public void TraceImpact_AllowedProvenance_FiltersByProvenance()
    {
        var edges = new List<EdgeRecord>
        {
            MakeEdge("A", "B"),
            MakeEdge("A", "C", "Calls", "framework_derived"),
            MakeEdge("B", "D"),
            MakeEdge("C", "D", "Calls", "framework_derived")
        };
        var traverser = CreateTraverser(edges);

        var compilerProvedOnly = new HashSet<string> { "compiler_proved" };
        var paths = traverser.TraceImpact("A", ImpactDirection.Downstream, allowedProvenance: compilerProvedOnly,
            maxDepth: 10);

        Assert.NotEmpty(paths);
        // Only compiler_proved edges should be followed: A→B (compiler_proved), B→D (compiler_proved)
        // A→C (framework_derived) should be skipped
        foreach (var path in paths) Assert.All(path.Hops, hop => Assert.Equal("compiler_proved", hop.Provenance));
    }

    [Fact]
    public void TraceImpact_AllowedEdgeKinds_EmptyFilterFollowsAll()
    {
        var edges = new List<EdgeRecord>
        {
            MakeEdge("A", "B"),
            MakeEdge("A", "C", "Constructs")
        };
        var traverser = CreateTraverser(edges);

        var paths = traverser.TraceImpact("A", ImpactDirection.Downstream);

        Assert.NotEmpty(paths);
        var kinds = paths.SelectMany(p => p.Hops).Select(h => h.EdgeKind).Distinct().ToList();
        Assert.Contains("Calls", kinds);
        Assert.Contains("Constructs", kinds);
    }

    // ── Direction ───────────────────────────────────────────────────────

    [Fact]
    public void TraceImpact_Downstream_FollowsOutgoingEdges()
    {
        var edges = new List<EdgeRecord>
        {
            MakeEdge("A", "B"),
            MakeEdge("B", "C")
        };
        var traverser = CreateTraverser(edges);

        var paths = traverser.TraceImpact("A", ImpactDirection.Downstream, maxDepth: 10);

        Assert.NotEmpty(paths);
        // All hops should flow forward: A→B, B→C
        foreach (var path in paths)
            for (var i = 0; i < path.Hops.Count; i++)
                Assert.Equal(i == 0 ? "A" : path.Hops[i - 1].TargetSymbolId, path.Hops[i].SourceSymbolId);
    }

    [Fact]
    public void TraceImpact_Upstream_FollowsIncomingEdges()
    {
        var edges = new List<EdgeRecord>
        {
            MakeEdge("B", "A"),
            MakeEdge("C", "B")
        };
        var traverser = CreateTraverser(edges);

        var paths = traverser.TraceImpact("A", ImpactDirection.Upstream, maxDepth: 10);

        Assert.NotEmpty(paths);
        // Upstream: traces edges backwards — A gets incoming from B, B gets incoming from C
        foreach (var path in paths)
            for (var i = 0; i < path.Hops.Count; i++)
                switch (i)
                {
                    case 0:
                        Assert.Equal("B", path.Hops[i].SourceSymbolId);
                        break;
                    case 1:
                        Assert.Equal("C", path.Hops[i].SourceSymbolId);
                        break;
                }
    }

    // ── In-memory IEdgeStore implementation ─────────────────────────────

    private sealed class InMemoryEdgeStore(List<EdgeRecord> edges) : IEdgeStore
    {
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