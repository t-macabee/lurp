using Lurp.Storage;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public sealed class EdgeWritePathTests : IDisposable
{
    private const string WorkspaceId = "w1";
    private const string SnapshotId = "s1";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lurp-edge-write-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private SqliteIndexStore OpenStore()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();
        store.SaveWorkspace(WorkspaceId, "gitroot", "solution.sln");
        store.SaveSnapshot(new SnapshotRow
        {
            SnapshotId = SnapshotId,
            WorkspaceId = WorkspaceId,
            GitRoot = "gitroot",
            SolutionPath = "solution.sln",
            CreatedAtUtc = DateTime.UtcNow
        });
        return store;
    }

    private static EdgeRecord MakeEdge(string source, string target, string kind,
        string provenance = "compiler_proved", string? typeArgs = null,
        string? receiverConstraints = null,
        int? sourceStartLine = 1, int? sourceEndLine = 2)
    {
        return new EdgeRecord
        {
            SourceSymbolId = source,
            TargetSymbolId = target,
            Kind = kind,
            Provenance = provenance,
            TypeArgumentsJson = typeArgs,
            ReceiverTypeConstraintsJson = receiverConstraints,
            ExtractorVersion = "1.0.0",
            SourceDocumentPath = "src/Test.cs",
            SourceStartLine = sourceStartLine,
            SourceStartColumn = 1,
            SourceEndLine = sourceEndLine,
            SourceEndColumn = 10
        };
    }

    [Fact]
    public void SaveEdges_SameTripleDifferentProvenance_KeepsHigherRank()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "Calls", "name_candidate", sourceStartLine: 10)]);
        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "Calls", sourceStartLine: 20)]);

        var persisted = store.GetEdges(SnapshotId);
        var edge = Assert.Single(persisted);
        Assert.Equal("compiler_proved", edge.Provenance);
        Assert.Equal(20, edge.SourceStartLine);
    }

    [Fact]
    public void SaveEdges_SameTripleDifferentTypeArguments_MergesVariants()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "MayDispatchTo", typeArgs: """[["TA"]]""")]);
        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "MayDispatchTo", typeArgs: """[["TB"]]""")]);

        var persisted = store.GetEdges(SnapshotId);
        var edge = Assert.Single(persisted);
        Assert.NotNull(edge.TypeArgumentsJson);

        var variants = EdgeMerge.DeserializeTypeArguments(edge.TypeArgumentsJson);
        Assert.Equal(2, variants.Count);
    }

    [Fact]
    public void SaveEdges_CalledTwiceAcrossBatches_MergesAgainstPersistedRow()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "MayDispatchTo", typeArgs: """[["A","B"]]""")]);
        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "MayDispatchTo", typeArgs: """[["C","D"]]""")]);

        var persisted = store.GetEdges(SnapshotId);
        var edge = Assert.Single(persisted);
        var variants = EdgeMerge.DeserializeTypeArguments(edge.TypeArgumentsJson);
        Assert.Equal(2, variants.Count);
        var allArgs = variants.SelectMany(v => v).OrderBy(s => s).ToList();
        Assert.Equal(["A", "B", "C", "D"], allArgs);
    }

    [Fact]
    public void SaveEdges_UnknownProvenance_NeverBeatsCanonical()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "Calls")]);
        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "Calls", "unknown_provenance")]);

        var persisted = store.GetEdges(SnapshotId);
        var edge = Assert.Single(persisted);
        Assert.Equal("compiler_proved", edge.Provenance);
    }

    [Fact]
    public void SaveEdges_FlatTypeArgumentEncoding_NormalizesToNested()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "MayDispatchTo", typeArgs: """["A","B"]""")]);

        var persisted = store.GetEdges(SnapshotId);
        var edge = Assert.Single(persisted);
        Assert.NotNull(edge.TypeArgumentsJson);

        var variants = EdgeMerge.DeserializeTypeArguments(edge.TypeArgumentsJson);
        Assert.Single(variants);
        Assert.Equal(["A", "B"], variants[0].OrderBy(s => s));
    }

    [Fact]
    public void SaveEdges_IncomingNullTypeArgs_DoesNotOverwritePersistedTypeArgs()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "MayDispatchTo", typeArgs: """[["TA"]]""")]);
        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "MayDispatchTo", typeArgs: null)]);

        var persisted = store.GetEdges(SnapshotId);
        var edge = Assert.Single(persisted);
        Assert.NotNull(edge.TypeArgumentsJson);
        var variants = EdgeMerge.DeserializeTypeArguments(edge.TypeArgumentsJson);
        Assert.Single(variants);
        Assert.Contains("TA", variants[0]);
    }

    [Fact]
    public void SaveEdges_CrossProjectReemit_MergesAgainstCopiedForwardRow()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "MayDispatchTo", typeArgs: """[["First"]]""")]);

        // Cross-document re-emit via same API — different project emits edge with
        // additional type-argument variant for the same triple.
        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "MayDispatchTo", typeArgs: """[["Second"]]""")]);

        var persisted = store.GetEdges(SnapshotId);
        var edge = Assert.Single(persisted);

        var variants = EdgeMerge.DeserializeTypeArguments(edge.TypeArgumentsJson);
        Assert.Equal(2, variants.Count);
        var allArgs = variants.SelectMany(v => v).OrderBy(s => s).ToList();
        Assert.Equal(["First", "Second"], allArgs);
    }

    // ARCH-003 regression: receiver_type_constraints_json must union-merge across
    // re-emits (split declaration / incremental pass) exactly like type_arguments_json.
    // Previously it used a rank-gated overwrite, so an equal-or-lower-rank re-emit
    // silently dropped the persisted receiver constraints.
    [Fact]
    public void SaveEdges_SameSplitEdgeDifferentReceiverConstraints_UnionMerges()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId, [
            MakeEdge("src", "tgt", "MayDispatchTo",
                typeArgs: """[["TA"]]""", receiverConstraints: """[["R1"]]""")
        ]);
        store.SaveEdges(SnapshotId, [
            MakeEdge("src", "tgt", "MayDispatchTo",
                typeArgs: """[["TB"]]""", receiverConstraints: """[["R2"]]""")
        ]);

        var persisted = store.GetEdges(SnapshotId);
        var edge = Assert.Single(persisted);

        var variants = EdgeMerge.DeserializeTypeArguments(edge.TypeArgumentsJson);
        Assert.Equal(2, variants.Count);

        var constraints = EdgeMerge.DeserializeTypeArguments(edge.ReceiverTypeConstraintsJson);
        Assert.Equal(2, constraints.Count);
        var allConstraints = constraints.SelectMany(c => c).OrderBy(s => s).ToList();
        Assert.Equal(["R1", "R2"], allConstraints);
    }

    // Equal-rank re-emit must still union-merge receiver constraints (matching the
    // unconditional type-argument merge) rather than dropping the incoming ones.
    [Fact]
    public void SaveEdges_EqualRankReemit_ReceiverConstraintsUnionMergedNotOverwritten()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId, [
            MakeEdge("src", "tgt", "MayDispatchTo",
                "compiler_proved", """[["TA"]]""", """[["R1"]]""")
        ]);
        store.SaveEdges(SnapshotId, [
            MakeEdge("src", "tgt", "MayDispatchTo",
                "compiler_proved", """[["TB"]]""", """[["R2"]]""")
        ]);

        var edge = Assert.Single(store.GetEdges(SnapshotId));

        var constraints = EdgeMerge.DeserializeTypeArguments(edge.ReceiverTypeConstraintsJson);
        Assert.Equal(2, constraints.Count);
        var allConstraints = constraints.SelectMany(c => c).OrderBy(s => s).ToList();
        Assert.Equal(["R1", "R2"], allConstraints);
    }

    // ARCH-003 regression, bulk-path gap: real CallsEdgeExtractor output is a "Calls"
    // edge with ReceiverTypeConstraintsJson set and TypeArgumentsJson null — unlike the
    // two tests above, which use a "MayDispatchTo" edge with type arguments and so only
    // exercise WriteSplitEdges. A Calls edge with no type arguments used to route through
    // WriteBulkEdges, whose receiver_type_constraints_json column was still a rank-gated
    // overwrite, silently dropping constraints on an equal-or-lower-rank re-emit even
    // after the WriteSplitEdges merge fix landed.
    [Fact]
    public void SaveEdges_CallsEdgeNoTypeArgs_ReceiverConstraintsUnionMergeNotOverwritten()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "Calls", receiverConstraints: """[["R1"]]""")]);
        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "Calls", receiverConstraints: """[["R2"]]""")]);

        var edge = Assert.Single(store.GetEdges(SnapshotId));
        Assert.Null(edge.TypeArgumentsJson);

        var constraints = EdgeMerge.DeserializeTypeArguments(edge.ReceiverTypeConstraintsJson);
        Assert.Equal(2, constraints.Count);
        var allConstraints = constraints.SelectMany(c => c).OrderBy(s => s).ToList();
        Assert.Equal(["R1", "R2"], allConstraints);
    }

    // ARCH-006 regression: PruneSnapshotGraphNodes must drop synthetic/route nodes
    // that CopyEdgesToSnapshot carried forward but which no longer have any edge or
    // declaration in this snapshot, while keeping nodes that are still edge endpoints.
    [Fact]
    public void PruneSnapshotGraphNodes_RemovesUnreferenced_KeepsEdgeEndpoints()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId,
        [
            new EdgeRecord
            {
                SourceSymbolId = "src",
                TargetSymbolId = "tgt",
                Kind = "Calls",
                Provenance = "compiler_proved",
                ExtractorVersion = "1.0.0",
                SourceDocumentPath = "src/Test.cs",
                SourceStartLine = 1,
                SourceStartColumn = 1,
                SourceEndLine = 2,
                SourceEndColumn = 10,
                SourceNodeKind = GraphNodeKind.Route,
                TargetNodeKind = GraphNodeKind.ExternalType
            }
        ]);

        // Inject a stale synthetic node that no longer has any edge or declaration.
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO snapshot_graph_nodes (snapshot_id, node_id) VALUES (@s, @n);";
            cmd.Parameters.AddWithValue("@s", SnapshotId);
            cmd.Parameters.AddWithValue("@n", "stale::synthetic::route");
            cmd.ExecuteNonQuery();
        }

        store.PruneSnapshotGraphNodes(SnapshotId);

        long staleCount, edgeNodeCount;
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM snapshot_graph_nodes WHERE snapshot_id = @s AND node_id = @n;";
            cmd.Parameters.AddWithValue("@s", SnapshotId);
            cmd.Parameters.AddWithValue("@n", "stale::synthetic::route");
            staleCount = Convert.ToInt64(cmd.ExecuteScalar());

            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText =
                "SELECT COUNT(*) FROM snapshot_graph_nodes WHERE snapshot_id = @s AND node_id IN ('src','tgt');";
            cmd2.Parameters.AddWithValue("@s", SnapshotId);
            edgeNodeCount = Convert.ToInt64(cmd2.ExecuteScalar());
        }

        Assert.Equal(0, staleCount);
        Assert.Equal(2, edgeNodeCount);
    }

    // Characterization tests for the strict-`>` tie-break at
    // EdgeOperationsStore.WriteBulkEdges: `@incomingRank > persistedRank`.
    // An equal-rank incoming edge must NOT overwrite the persisted
    // (copied-forward) row's provenance/path/coordinates/is_cross_generated,
    // so a future `> → >=` loosening or non-deterministic extraction
    // regression trips these tests. Only type_arguments_json merges
    // unconditionally (EdgeOperationsStore.cs:163).
    [Fact]
    public void EqualRankCollision_KeepsCopiedForwardRow_OverIncomingWithDifferentLocation()
    {
        using var store = OpenStore();

        // Seed the "copied-forward" row (MakeEdge defaults SourceDocumentPath = "src/Test.cs").
        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "Calls", sourceStartLine: 10, sourceEndLine: 11)]);

        // Re-emit an equal-rank incoming edge with *different* location/path/
        // cross-generated to give the `>` clause something it must NOT take.
        // EdgeRecord is a class with init-only setters, so the differing edge
        // is constructed explicitly instead of via a `with` expression.
        store.SaveEdges(SnapshotId,
        [
            new EdgeRecord
            {
                SourceSymbolId = "src",
                TargetSymbolId = "tgt",
                Kind = "Calls",
                Provenance = "compiler_proved",
                ExtractorVersion = "1.0.0",
                SourceDocumentPath = "src/Other.cs",
                SourceStartLine = 999,
                SourceStartColumn = 1,
                SourceEndLine = 1000,
                SourceEndColumn = 10,
                IsCrossGenerated = true
            }
        ]);

        var edge = Assert.Single(store.GetEdges(SnapshotId));

        // The persisted (copied-forward) values won the equal-rank collision.
        Assert.Equal("src/Test.cs", edge.SourceDocumentPath);
        Assert.Equal(10, edge.SourceStartLine);
        Assert.Equal(11, edge.SourceEndLine);
        Assert.False(edge.IsCrossGenerated);
        Assert.Equal("compiler_proved", edge.Provenance);
    }

    // Inverse direction: a higher-rank row persisted first must survive a
    // lower-rank incoming (name_candidate after compiler_proved). Together
    // with the equal-rank test this pins the strict-`>` invariant from both
    // orderings. The name reflects what the test verifies: the lower-rank
    // incoming does NOT overwrite the already-persisted higher-rank row.
    [Fact]
    public void LowerRankIncoming_DoesNotOverwrite_HigherRankPersisted()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "Calls", sourceStartLine: 10)]);
        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "Calls", "name_candidate", sourceStartLine: 999)]);

        var edge = Assert.Single(store.GetEdges(SnapshotId));
        Assert.Equal("compiler_proved", edge.Provenance);
        Assert.Equal(10, edge.SourceStartLine);
    }
}