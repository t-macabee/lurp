using Lurp.Storage;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Lurp.Tests;

public sealed class EdgeWritePathTests : IDisposable
{
    private const string WorkspaceId = "w1";
    private const string SnapshotId = "s1";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lurp-edge-write-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestDatabase.Delete(_dbPath);
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

    private void SaveSnapshotSymbol(string symbolId)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = OFF;";
        pragma.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR IGNORE INTO snapshot_symbols (snapshot_id, symbol_id, fqn, metadata_json)
            VALUES (@snapshotId, @symbolId, @symbolId, NULL);
        ";
        command.Parameters.AddWithValue("@snapshotId", SnapshotId);
        command.Parameters.AddWithValue("@symbolId", symbolId);
        command.ExecuteNonQuery();
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
    public void SaveEdges_ReturnsTheNumberOfRelationsAfterTheCollapse()
    {
        using var store = OpenStore();

        // Hand count: (src, tgt, Calls) twice and (src, other, Calls) once are two relations.
        var written = store.SaveEdges(SnapshotId,
        [
            MakeEdge("src", "tgt", "Calls"),
            MakeEdge("src", "tgt", "Calls", "name_candidate"),
            MakeEdge("src", "other", "Calls")
        ]);

        Assert.Equal(2, written);
        Assert.Equal(2, store.GetEdges(SnapshotId).Count);
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

    // A Calls edge can carry receiver constraints without type arguments.
    // The merge must also preserve the union for this shape (ARCH-003).
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
                SourceIsBoundary = true,
                TargetIsBoundary = true
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
            staleCount = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText =
                "SELECT COUNT(*) FROM snapshot_graph_nodes WHERE snapshot_id = @s AND node_id IN ('src','tgt');";
            cmd2.Parameters.AddWithValue("@s", SnapshotId);
            edgeNodeCount = Convert.ToInt64(cmd2.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        Assert.Equal(0, staleCount);
        Assert.Equal(2, edgeNodeCount);
    }

    [Fact]
    public void EqualRankCollision_TakesTheFieldsOfTheFirstSite()
    {
        using var store = OpenStore();
        store.SaveEdges(SnapshotId, [MakeEdge("src", "tgt", "Calls", sourceStartLine: 10, sourceEndLine: 11)]);

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

        // Ordinal order puts src/Other.cs before src/Test.cs.
        Assert.Equal("src/Other.cs", edge.SourceDocumentPath);
        Assert.Equal(999, edge.SourceStartLine);
        Assert.Equal(1000, edge.SourceEndLine);
        Assert.True(edge.IsCrossGenerated);
        Assert.Equal("1.0.0", edge.ExtractorVersion);
        Assert.Equal("compiler_proved", edge.Provenance);
    }

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

    [Fact]
    public void DeleteOrphanEdges_OpenApiGeneratedEndpoint_ClassifiedCompilerSynthesized()
    {
        using var store = OpenStore();
        SaveSnapshotSymbol("T:Ns.Foo|asm1");

        const string generated =
            "M:Microsoft.AspNetCore.OpenApi.Generated.XmlCommentOperationTransformer.TransformAsync(...)|asm1";

        store.SaveEdges(SnapshotId,
        [
            MakeEdge("T:Ns.Foo|asm1", generated, "Calls"),
            MakeEdge(generated, "T:Ns.Foo|asm1", "Calls")
        ]);

        var summary = store.DeleteOrphanEdges(SnapshotId, []);

        Assert.Equal(2, summary.Total);
        Assert.Equal(0, summary.External);
        Assert.Equal(2, summary.CompilerSynthesized);
        Assert.Equal(0, summary.Other);
    }

    [Fact]
    public void DeleteOrphanEdges_InterceptsLocationEndpoint_ClassifiedCompilerSynthesized()
    {
        using var store = OpenStore();
        SaveSnapshotSymbol("T:Ns.Foo|asm1");

        const string generated = "T:System.Runtime.CompilerServices.InterceptsLocationAttribute|asm1";

        store.SaveEdges(SnapshotId,
        [
            MakeEdge("T:Ns.Foo|asm1", generated, "Calls"),
            MakeEdge(generated, "T:Ns.Foo|asm1", "Calls")
        ]);

        var summary = store.DeleteOrphanEdges(SnapshotId, []);

        Assert.Equal(2, summary.Total);
        Assert.Equal(0, summary.External);
        Assert.Equal(2, summary.CompilerSynthesized);
        Assert.Equal(0, summary.Other);
    }

    [Fact]
    public void DeleteOrphanEdges_ExternalAssemblyEndpoint_ClassifiedExternal()
    {
        using var store = OpenStore();
        SaveSnapshotSymbol("T:Ns.Foo|asm1");

        store.SaveEdges(SnapshotId,
        [
            MakeEdge("T:Ns.Foo|asm1", "T:System.Console|System.Console", "Calls")
        ]);

        var summary = store.DeleteOrphanEdges(SnapshotId, []);

        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.External);
        Assert.Equal(0, summary.CompilerSynthesized);
        Assert.Equal(0, summary.Other);
    }

    [Fact]
    public void DeleteOrphanEdges_InScopeEndpointMissing_ClassifiedOther()
    {
        using var store = OpenStore();
        SaveSnapshotSymbol("T:Ns.Foo|asm1");

        store.SaveEdges(SnapshotId,
        [
            MakeEdge("T:Ns.Foo|asm1", "T:Ns.Vanished|asm1", "Calls")
        ]);

        var summary = store.DeleteOrphanEdges(SnapshotId, []);

        Assert.Equal(1, summary.Total);
        Assert.Equal(0, summary.External);
        Assert.Equal(0, summary.CompilerSynthesized);
        Assert.Equal(1, summary.Other);
    }

    [Fact]
    public void DeleteOrphanEdges_TotalEqualsSumOfBuckets()
    {
        using var store = OpenStore();
        SaveSnapshotSymbol("T:Ns.Foo|asm1");

        store.SaveEdges(SnapshotId,
        [
            MakeEdge("T:Ns.Foo|asm1", "T:Ns.<>c__DisplayClass0_0|asm1", "Calls"),
            MakeEdge("T:Ns.Foo|asm1", "T:System.Console|System.Console", "Calls"),
            MakeEdge("T:Ns.Foo|asm1", "T:Ns.ExcludedGen|asm1", "Calls"),
            MakeEdge("T:Ns.Foo|asm1", "T:Ns.Vanished|asm1", "Calls")
        ]);

        var summary = store.DeleteOrphanEdges(SnapshotId, ["T:Ns.ExcludedGen|asm1"]);

        Assert.Equal(4, summary.Total);
        Assert.Equal(1, summary.External);
        Assert.Equal(1, summary.CompilerSynthesized);
        Assert.Equal(1, summary.ExcludedDocument);
        Assert.Equal(1, summary.Other);
        Assert.Equal(summary.Total, summary.External + summary.CompilerSynthesized + summary.ExcludedDocument + summary.Other);
    }

    private static List<(string Doc, int Sl, int Sc, int El, int Ec)> ReadSites(
        SqliteIndexStore store, string snapshotId, string source, string target, string kind)
    {
        var key = (source, target, kind);
        return store.GetEdgeSites(snapshotId, [key]).TryGetValue(key, out var sites)
            ? sites.Select(site => (site.DocumentPath, site.StartLine, site.StartColumn, site.EndLine, site.EndColumn)).ToList()
            : [];
    }

    private List<(string Provenance, string Path, int Line)> ReadEvidence(string source, string target, string kind)
    {
        using var connection = TestDatabase.OpenReadOnly(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.provenance, s.document_path, s.start_line
            FROM edges e JOIN edge_sites s ON e.edge_id = s.edge_id
            WHERE e.snapshot_id = @snapshot AND e.source_symbol_id = @source AND e.target_symbol_id = @target AND e.kind = @kind
            ORDER BY s.provenance, s.document_path, s.start_line;
            """;
        command.Parameters.AddWithValue("@snapshot", SnapshotId);
        command.Parameters.AddWithValue("@source", source);
        command.Parameters.AddWithValue("@target", target);
        command.Parameters.AddWithValue("@kind", kind);
        var evidence = new List<(string, string, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            evidence.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return evidence;
    }

    private long CountAllEdgeSites()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM edge_sites;";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static EdgeRecord MakeSiteEdge(string source, string target, string kind, string provenance,
        string? docPath, int? sl, int? sc, int? el, int? ec, string version = "1.0.0", bool crossGenerated = false)
    {
        return new EdgeRecord
        {
            SourceSymbolId = source,
            TargetSymbolId = target,
            Kind = kind,
            Provenance = provenance,
            ExtractorVersion = version,
            IsCrossGenerated = crossGenerated,
            SourceDocumentPath = docPath,
            SourceStartLine = sl,
            SourceStartColumn = sc,
            SourceEndLine = el,
            SourceEndColumn = ec
        };
    }

    [Fact]
    public void SaveEdges_KeepsEverySiteOfARelation_InSourceOrder()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId,
        [
            MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, "src/A.cs", 9, 4, 9, 12),
            MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, "src/A.cs", 2, 8, 2, 16),
            MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, "src/A.cs", 2, 3, 2, 11)
        ]);

        var edge = Assert.Single(store.GetEdges(SnapshotId));
        Assert.Equal("src/A.cs", edge.SourceDocumentPath);
        Assert.Equal((2, 3, 2, 11), (edge.SourceStartLine, edge.SourceStartColumn, edge.SourceEndLine, edge.SourceEndColumn));

        var sites = ReadSites(store, SnapshotId, "src", "tgt", "Calls");
        Assert.Equal(
            [
                ("src/A.cs", 2, 3, 2, 11),
                ("src/A.cs", 2, 8, 2, 16),
                ("src/A.cs", 9, 4, 9, 12)
            ],
            sites);
    }

    [Fact]
    public void SaveEdges_SitesFollowProvenanceRank()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId,
        [
            MakeSiteEdge("src", "tgt", "Calls", Provenance.NameCandidate, "src/A.cs", 1, 0, 1, 5),
            MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, "src/A.cs", 7, 0, 7, 5),
            MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, "src/A.cs", 3, 0, 3, 5)
        ]);

        var edge = Assert.Single(store.GetEdges(SnapshotId));
        Assert.Equal(Provenance.CompilerProved, edge.Provenance);
        Assert.Equal((3, 0, 3, 5), (edge.SourceStartLine, edge.SourceStartColumn, edge.SourceEndLine, edge.SourceEndColumn));
        Assert.Equal(
            [
                ("src/A.cs", 3, 0, 3, 5),
                ("src/A.cs", 7, 0, 7, 5)
            ],
            ReadSites(store, SnapshotId, "src", "tgt", "Calls"));

        // A lower rank adds evidence. The shown sites stay the same.
        store.SaveEdges(SnapshotId,
        [
            MakeSiteEdge("src", "tgt", "Calls", Provenance.Possible, "src/A.cs", 0, 0, 0, 5)
        ]);

        Assert.Equal(
            [
                ("src/A.cs", 3, 0, 3, 5),
                ("src/A.cs", 7, 0, 7, 5)
            ],
            ReadSites(store, SnapshotId, "src", "tgt", "Calls"));

        // Equal rank adds one site and refreshes the first-site columns if earlier.
        store.SaveEdges(SnapshotId,
        [
            MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, "src/A.cs", 5, 0, 5, 5)
        ]);

        Assert.Equal(
            [
                ("src/A.cs", 3, 0, 3, 5),
                ("src/A.cs", 5, 0, 5, 5),
                ("src/A.cs", 7, 0, 7, 5)
            ],
            ReadSites(store, SnapshotId, "src", "tgt", "Calls"));

        // D155.3: the edge columns hold the first site in source order, not the last one written.
        var afterEqualRank = Assert.Single(store.GetEdges(SnapshotId));
        Assert.Equal((3, 0, 3, 5),
            (afterEqualRank.SourceStartLine, afterEqualRank.SourceStartColumn, afterEqualRank.SourceEndLine, afterEqualRank.SourceEndColumn));

        // A higher rank changes the shown sites.
        store.SaveEdges(SnapshotId,
        [
            MakeSiteEdge("src2", "tgt2", "Calls", Provenance.NameCandidate, "src/A.cs", 1, 0, 1, 5)
        ]);
        store.SaveEdges(SnapshotId,
        [
            MakeSiteEdge("src2", "tgt2", "Calls", Provenance.CompilerProved, "src/A.cs", 4, 0, 4, 5)
        ]);
        Assert.Equal(
            [
                ("src/A.cs", 4, 0, 4, 5)
            ],
            ReadSites(store, SnapshotId, "src2", "tgt2", "Calls"));
        Assert.Equal(
            [
                (Provenance.CompilerProved, "src/A.cs", 3),
                (Provenance.CompilerProved, "src/A.cs", 5),
                (Provenance.CompilerProved, "src/A.cs", 7),
                (Provenance.NameCandidate, "src/A.cs", 1),
                (Provenance.Possible, "src/A.cs", 0)
            ],
            ReadEvidence("src", "tgt", "Calls"));
        Assert.Equal(
            [(Provenance.CompilerProved, "src/A.cs", 4), (Provenance.NameCandidate, "src/A.cs", 1)],
            ReadEvidence("src2", "tgt2", "Calls"));
    }

    [Fact]
    public void EdgeSites_FollowTheirEdge()
    {
        using var store = OpenStore();
        store.SaveSnapshot(new SnapshotRow
        {
            SnapshotId = "s2",
            WorkspaceId = WorkspaceId,
            GitRoot = "gitroot",
            SolutionPath = "solution.sln",
            CreatedAtUtc = DateTime.UtcNow
        });

        store.SaveEdges(SnapshotId,
        [
            MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, "src/A.cs", 2, 0, 2, 5),
            MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, "src/A.cs", 8, 0, 8, 5)
        ]);

        store.CopyEdgesToSnapshot(SnapshotId, "s2");
        Assert.Equal(2, ReadSites(store, "s2", "src", "tgt", "Calls").Count);

        store.DeleteFactsByDocumentPaths("s2", ["src/A.cs"]);
        Assert.Empty(ReadSites(store, "s2", "src", "tgt", "Calls"));
        Assert.Equal(2, ReadSites(store, SnapshotId, "src", "tgt", "Calls").Count);

        store.DeleteSnapshotData(SnapshotId);
        Assert.Equal(0L, CountAllEdgeSites());
    }

    [Fact]
    public void RetireSites_KeepsTheEdgeOfAnUnchangedDocument_AndMovesThePrimary()
    {
        using var store = OpenStore();
        store.SaveEdges(SnapshotId,
        [
            MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, "a.cs", 1, 0, 1, 5),
            MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, "b.cs", 2, 3, 4, 6)
        ]);

        store.DeleteFactsByDocumentPaths(SnapshotId, ["a.cs"]);

        var edge = Assert.Single(store.GetEdges(SnapshotId));
        Assert.Equal("b.cs", edge.SourceDocumentPath);
        Assert.Equal((2, 3, 4, 6), (edge.SourceStartLine, edge.SourceStartColumn, edge.SourceEndLine, edge.SourceEndColumn));
        Assert.Equal([("b.cs", 2, 3, 4, 6)], ReadSites(store, SnapshotId, "src", "tgt", "Calls"));
    }

    [Fact]
    public void RetireSites_FallsBackToTheRemainingLowerProvenance()
    {
        using var store = OpenStore();
        EdgeRecord[] records =
        [
            MakeSiteEdge("src", "tgt", "Calls", Provenance.FrameworkDerived, "a.cs", 1, 0, 1, 5, "fw-1"),
            MakeSiteEdge("src", "tgt", "Calls", Provenance.Convention, "b.cs", 2, 0, 2, 5, "cv-1", true)
        ];
        store.SaveEdges(SnapshotId, records);

        AssertWinner(store, SnapshotId, Provenance.FrameworkDerived, "fw-1", false, "a.cs", 1);
        Assert.Equal([("a.cs", 1, 0, 1, 5)], ReadSites(store, SnapshotId, "src", "tgt", "Calls"));

        store.SaveEdges(SnapshotId, records);

        AssertWinner(store, SnapshotId, Provenance.FrameworkDerived, "fw-1", false, "a.cs", 1);

        store.DeleteFactsByDocumentPaths(SnapshotId, ["a.cs"]);

        AssertWinner(store, SnapshotId, Provenance.Convention, "cv-1", true, "b.cs", 2);
        Assert.Equal([("b.cs", 2, 0, 2, 5)], ReadSites(store, SnapshotId, "src", "tgt", "Calls"));

        store.DeleteFactsByDocumentPaths(SnapshotId, ["b.cs"]);

        Assert.Empty(store.GetEdges(SnapshotId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveEdges_TheWinnerDoesNotDependOnTheWriteOrder(bool sameSite)
    {
        using var store = OpenStore();
        var firstPath = sameSite ? "a.cs" : "c.cs";
        var first = MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, firstPath, 0, 0, 0, 5, "2.0");
        var second = MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, "a.cs", 0, 0, 0, 5, "1.0", true);

        store.SaveEdges(SnapshotId, [first, second]);
        SaveSnapshot("s2");
        store.SaveEdges("s2", [first]);
        store.SaveEdges("s2", [second]);
        SaveSnapshot("s3");
        store.SaveEdges("s3", [second]);
        store.SaveEdges("s3", [first]);

        (string, int, int, int, int)[] sites = sameSite
            ? [("a.cs", 0, 0, 0, 5)]
            : [("a.cs", 0, 0, 0, 5), ("c.cs", 0, 0, 0, 5)];
        foreach (var id in new[] { SnapshotId, "s2", "s3" })
        {
            AssertWinner(store, id, Provenance.CompilerProved, "1.0", true, "a.cs", 0);
            Assert.Equal(sites, ReadSites(store, id, "src", "tgt", "Calls"));
        }
        return;

        void SaveSnapshot(string id) => store.SaveSnapshot(new SnapshotRow
        {
            SnapshotId = id,
            WorkspaceId = WorkspaceId,
            GitRoot = "gitroot",
            SolutionPath = "solution.sln",
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    [Fact]
    public void NullPathWinner_KeepsItsRow_AndFallsBackToItsEvidence()
    {
        using var store = OpenStore();
        var unlocated = MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, null, null, null, null, null, "u-1");
        var located = MakeSiteEdge("src", "tgt", "Calls", Provenance.Convention, "b.cs", 2, 0, 2, 5, "cv-1");
        store.SaveEdges(SnapshotId, [unlocated, located]);

        AssertWinner(store, SnapshotId, Provenance.CompilerProved, "u-1", false, null, null);
        Assert.Empty(ReadSites(store, SnapshotId, "src", "tgt", "Calls"));

        store.DeleteEdgesWithNullDocumentPathForSymbols(SnapshotId, ["src"]);

        AssertWinner(store, SnapshotId, Provenance.Convention, "cv-1", false, "b.cs", 2);
        Assert.Equal([("b.cs", 2, 0, 2, 5)], ReadSites(store, SnapshotId, "src", "tgt", "Calls"));

        store.SaveEdges(SnapshotId, [unlocated]);

        AssertWinner(store, SnapshotId, Provenance.CompilerProved, "u-1", false, null, null);
        Assert.Empty(ReadSites(store, SnapshotId, "src", "tgt", "Calls"));

        store.DeleteFactsByDocumentPaths(SnapshotId, ["b.cs"]);

        AssertWinner(store, SnapshotId, Provenance.CompilerProved, "u-1", false, null, null);
    }

    [Fact]
    public void GetEdgeSites_UsesTheOrdinalSiteOrder()
    {
        using var store = OpenStore();
        const string supplementary = "src/\U0001F600.cs";
        const string privateUse = "src/\uE000.cs";
        store.SaveEdges(SnapshotId,
        [
            MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, privateUse, 0, 0, 0, 1),
            MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, supplementary, 0, 0, 0, 1)
        ]);

        // The first UTF-16 code unit D83D sorts before E000.
        Assert.Equal([(supplementary, 0, 0, 0, 1), (privateUse, 0, 0, 0, 1)],
            ReadSites(store, SnapshotId, "src", "tgt", "Calls"));
    }

    private static void AssertWinner(SqliteIndexStore store, string snapshotId, string provenance,
        string version, bool crossGenerated, string? path, int? line)
    {
        var edge = Assert.Single(store.GetEdges(snapshotId));
        Assert.Equal(provenance, edge.Provenance);
        Assert.Equal(version, edge.ExtractorVersion);
        Assert.Equal(crossGenerated, edge.IsCrossGenerated);
        Assert.Equal(path, edge.SourceDocumentPath);
        Assert.Equal(line, edge.SourceStartLine);
    }

    [Fact]
    public void RetireSites_DeletesOnlyEdgesThatLoseTheirLastSite()
    {
        using var store = OpenStore();
        store.SaveEdges(SnapshotId,
        [
            MakeSiteEdge("changed", "tgt", "Calls", Provenance.CompilerProved, "a.cs", 1, 0, 1, 5),
            MakeSiteEdge("unchanged", "tgt", "Calls", Provenance.CompilerProved, "b.cs", null, null, null, null)
        ]);

        store.DeleteFactsByDocumentPaths(SnapshotId, ["a.cs"]);

        var edge = Assert.Single(store.GetEdges(SnapshotId));
        Assert.Equal("unchanged", edge.SourceSymbolId);
        Assert.Equal("b.cs", edge.SourceDocumentPath);
    }

    [Fact]
    public void SaveEdges_AndRetireSites_UseOrdinalPathOrder()
    {
        using var store = OpenStore();
        const string supplementary = "\U00010000.cs";
        const string privateUse = "\uE000.cs";

        store.SaveEdges(SnapshotId,
            [MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, privateUse, 1, 0, 1, 5)]);
        store.SaveEdges(SnapshotId,
            [MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, supplementary, 1, 0, 1, 5)]);

        // In ordinal order the first UTF-16 code unit D800 sorts before E000.
        Assert.Equal(supplementary, Assert.Single(store.GetEdges(SnapshotId)).SourceDocumentPath);

        store.SaveEdges(SnapshotId,
            [MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, "a.cs", 1, 0, 1, 5)]);
        store.DeleteFactsByDocumentPaths(SnapshotId, ["a.cs"]);

        Assert.Equal(supplementary, Assert.Single(store.GetEdges(SnapshotId)).SourceDocumentPath);
    }

    [Fact]
    public void SaveEdges_NullLocation_HasNoSite()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId,
        [
            MakeSiteEdge("src", "tgt", "Calls", Provenance.CompilerProved, null, null, null, null, null)
        ]);

        var edge = Assert.Single(store.GetEdges(SnapshotId));
        Assert.Null(edge.SourceDocumentPath);
        Assert.Empty(ReadSites(store, SnapshotId, "src", "tgt", "Calls"));
    }

    [Fact]
    public void SaveEdges_LargeBulkBatchAcrossChunks_PreservesRanksAndSites()
    {
        using var store = OpenStore();
        const int count = 2100;

        var firstBatch = Enumerable.Range(0, count)
            .Select(i => MakeSiteEdge($"src{i}", "tgt", "Calls",
                i % 2 == 0 ? Provenance.NameCandidate : Provenance.CompilerProved,
                "src/A.cs", i, 0, i, 5))
            .ToList();

        Assert.Equal(count, store.SaveEdges(SnapshotId, firstBatch));

        var secondBatch = new List<EdgeRecord>
        {
            // A higher rank changes the shown sites of src0.
            MakeSiteEdge("src0", "tgt", "Calls", Provenance.CompilerProved, "src/B.cs", 99, 0, 99, 5),
            // An equal rank adds a shown site for src1.
            MakeSiteEdge("src1", "tgt", "Calls", Provenance.CompilerProved, "src/A.cs", 0, 0, 0, 5),
            // A lower rank adds evidence for src3.
            MakeSiteEdge("src3", "tgt", "Calls", Provenance.NameCandidate, "src/Z.cs", 1, 0, 1, 5),
            // Brand new edge
            MakeSiteEdge("srcNew", "tgt", "Calls", Provenance.CompilerProved, "src/C.cs", 4, 0, 4, 5)
        };

        store.SaveEdges(SnapshotId, secondBatch);

        var edgesBySource = store.GetEdges(SnapshotId).ToDictionary(e => e.SourceSymbolId);
        Assert.Equal(count + 1, edgesBySource.Count);

        Assert.Equal(Provenance.CompilerProved, edgesBySource["src0"].Provenance);
        Assert.Equal([("src/B.cs", 99, 0, 99, 5)], ReadSites(store, SnapshotId, "src0", "tgt", "Calls"));

        Assert.Equal(
            [("src/A.cs", 0, 0, 0, 5), ("src/A.cs", 1, 0, 1, 5)],
            ReadSites(store, SnapshotId, "src1", "tgt", "Calls"));
        Assert.Equal(0, edgesBySource["src1"].SourceStartLine);

        Assert.Equal(Provenance.CompilerProved, edgesBySource["src3"].Provenance);
        Assert.Equal([("src/A.cs", 3, 0, 3, 5)], ReadSites(store, SnapshotId, "src3", "tgt", "Calls"));
        Assert.Equal(
            [(Provenance.CompilerProved, "src/A.cs", 3), (Provenance.NameCandidate, "src/Z.cs", 1)],
            ReadEvidence("src3", "tgt", "Calls"));
        Assert.Equal(
            [(Provenance.CompilerProved, "src/B.cs", 99), (Provenance.NameCandidate, "src/A.cs", 0)],
            ReadEvidence("src0", "tgt", "Calls"));

        Assert.Equal([("src/C.cs", 4, 0, 4, 5)], ReadSites(store, SnapshotId, "srcNew", "tgt", "Calls"));
    }

    [Fact]
    public void SaveEdges_SplitEdgeLowerRank_UnionsPayloadsWithoutOverwritingProvenanceOrSites()
    {
        using var store = OpenStore();

        store.SaveEdges(SnapshotId, [
            MakeEdge("src", "tgt", "MayDispatchTo", Provenance.CompilerProved,
                typeArgs: """[["TA"]]""", receiverConstraints: """[["R1"]]""",
                sourceStartLine: 10, sourceEndLine: 10)
        ]);
        store.SaveEdges(SnapshotId, [
            MakeEdge("src", "tgt", "MayDispatchTo", Provenance.Possible,
                typeArgs: """[["TB"]]""", receiverConstraints: """[["R2"]]""",
                sourceStartLine: 1, sourceEndLine: 1)
        ]);

        var edge = Assert.Single(store.GetEdges(SnapshotId));
        Assert.Equal(Provenance.CompilerProved, edge.Provenance);
        Assert.Equal(10, edge.SourceStartLine);
        Assert.Equal([("src/Test.cs", 10, 1, 10, 10)], ReadSites(store, SnapshotId, "src", "tgt", "MayDispatchTo"));
        Assert.Equal(
            [(Provenance.CompilerProved, "src/Test.cs", 10), (Provenance.Possible, "src/Test.cs", 1)],
            ReadEvidence("src", "tgt", "MayDispatchTo"));
        Assert.Equal([["TA"], ["TB"]], EdgeMerge.DeserializeTypeArguments(edge.TypeArgumentsJson));
        Assert.Equal([["R1"], ["R2"]], EdgeMerge.DeserializeTypeArguments(edge.ReceiverTypeConstraintsJson));
    }

    [Fact]
    public void ProvenanceAll_IsTheSixEdgeLevels_InStrictlyDescendingRank()
    {
        string[] expected =
        [
            "compiler_proved",
            "framework_derived",
            "possible",
            "convention",
            "name_candidate",
            "runtime_unknown"
        ];

        Assert.Equal(expected, Provenance.All);

        for (var i = 1; i < Provenance.All.Count; i++)
        {
            var higher = EdgeMerge.ProvenanceRank(Provenance.All[i - 1]);
            var lower = EdgeMerge.ProvenanceRank(Provenance.All[i]);
            Assert.True(higher > lower, $"Expected rank({Provenance.All[i - 1]})={higher} > rank({Provenance.All[i]})={lower}");
        }

        Assert.True(
            EdgeMerge.ProvenanceRank("global_implementation_relation") < EdgeMerge.ProvenanceRank(Provenance.RuntimeUnknown),
            "Capsule grade global_implementation_relation must rank below runtime_unknown.");
    }
}
