using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using Lurp.Workspace;

namespace Lurp.Storage.Tests;

public sealed class GraphNodeMembershipTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"indexer_gnm_{Guid.NewGuid():N}.db");
    private SqliteIndexStore? _store;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private SqliteIndexStore CreateStore()
    {
        _store?.Dispose();
        _store = new SqliteIndexStore(_dbPath);
        _store.Open();
        _store.RunMigrations();
        return _store;
    }

    private T Scalar<T>(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private void SaveSnapshotWithSymbols(SqliteIndexStore store, string snapshotId, params string[] symbolIds)
    {
        store.SaveSnapshot(new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = "workspace:///gnm",
            GitRoot = "/gnm",
            SolutionPath = "/gnm/test.sln",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Documents = [],
        });
        if (symbolIds.Length > 0)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();

            using var command = connection.CreateCommand();
            foreach (var symbolId in symbolIds)
            {
                command.CommandText = @"
                    INSERT OR IGNORE INTO snapshot_symbols (snapshot_id, symbol_id, fqn, metadata_json)
                    VALUES (@snapshotId, @symbolId, @symbolId, NULL);
                ";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                command.Parameters.AddWithValue("@symbolId", symbolId);
                command.ExecuteNonQuery();
            }
        }
    }

    [Fact]
    public void Migrations_ReachCurrentSchemaVersion()
    {
        var store = CreateStore();
        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_metadata ORDER BY version DESC LIMIT 1;";
        var version = Convert.ToInt32(command.ExecuteScalar());
        Assert.Equal(MigrationRunner.MigrationVersions[^1], version);
    }

    [Fact]
    public void SaveEdges_WithSyntheticNodeKind_RegistersGraphNodesAndMembership()
    {
        var store = CreateStore();
        var snapshotId = "snap-gnm-register";
        SaveSnapshotWithSymbols(store, snapshotId, "T:Ns.Controller|asm1");

        store.SaveEdges(snapshotId,
        [
            new EdgeRecord
            {
                SourceSymbolId = "route://api/values",
                TargetSymbolId = "T:Ns.Controller|asm1",
                Kind = "RoutesTo",
                Provenance = "framework_derived",
                SourceNodeKind = GraphNodeKind.Route,
            },
            new EdgeRecord
            {
                SourceSymbolId = "T:Ns.Controller|asm1",
                TargetSymbolId = "convention:assembly_scan:MyLib",
                Kind = "Registers",
                Provenance = "convention",
                TargetNodeKind = GraphNodeKind.Convention,
            },
            new EdgeRecord
            {
                SourceSymbolId = "T:Ns.Controller|asm1",
                TargetSymbolId = "runtime:unknown",
                Kind = "Registers",
                Provenance = "runtime_unknown",
                TargetNodeKind = GraphNodeKind.RuntimePlaceholder,
            },
        ]);

        Assert.Equal(3, Scalar<long>("SELECT COUNT(*) FROM graph_nodes;"));
        Assert.Equal(3, Scalar<long>(
            $"SELECT COUNT(*) FROM snapshot_graph_nodes WHERE snapshot_id = '{snapshotId}';"));
        Assert.Equal(1, Scalar<long>(
            "SELECT COUNT(*) FROM graph_nodes WHERE node_id = 'route://api/values' AND node_kind = 'Route';"));
        Assert.Equal(1, Scalar<long>(
            "SELECT COUNT(*) FROM graph_nodes WHERE node_id = 'convention:assembly_scan:MyLib' AND node_kind = 'Convention';"));
        Assert.Equal(1, Scalar<long>(
            "SELECT COUNT(*) FROM graph_nodes WHERE node_id = 'runtime:unknown' AND node_kind = 'RuntimePlaceholder';"));
    }

    [Fact]
    public void DeleteOrphanEdges_RegisteredSyntheticEndpoints_PreservesOnlyRegisteredEdges()
    {
        var store = CreateStore();
        var snapshotId = "snap-gnm-orphan";
        var knownSymbol = "T:Ns.Controller|asm1";
        SaveSnapshotWithSymbols(store, snapshotId, knownSymbol);

        store.SaveEdges(snapshotId,
        [
            new EdgeRecord
            {
                SourceSymbolId = "route://api/values",
                TargetSymbolId = knownSymbol,
                Kind = "RoutesTo",
                Provenance = "framework_derived",
                SourceNodeKind = GraphNodeKind.Route,
            },
            new EdgeRecord
            {
                SourceSymbolId = knownSymbol,
                TargetSymbolId = "convention:assembly_scan:MyLib",
                Kind = "Registers",
                Provenance = "convention",
                TargetNodeKind = GraphNodeKind.Convention,
            },
            new EdgeRecord
            {
                SourceSymbolId = knownSymbol,
                TargetSymbolId = "runtime:unknown",
                Kind = "Registers",
                Provenance = "runtime_unknown",
                TargetNodeKind = GraphNodeKind.RuntimePlaceholder,
            },
            new EdgeRecord
            {
                SourceSymbolId = knownSymbol,
                TargetSymbolId = "T:Ns.Missing|asm2",
                Kind = "Calls",
                Provenance = "roslyn",
            },
        ]);

        store.DeleteOrphanEdges(snapshotId);

        var edges = store.GetEdges(snapshotId);
        Assert.Equal(3, edges.Count);
        Assert.Contains(edges, e => e.Kind == "RoutesTo" && e.SourceSymbolId == "route://api/values");
        Assert.Contains(edges, e => e.Kind == "Registers" && e.TargetSymbolId == "convention:assembly_scan:MyLib");
        Assert.Contains(edges, e => e.Kind == "Registers" && e.TargetSymbolId == "runtime:unknown");
        Assert.DoesNotContain(edges, e => e.TargetSymbolId == "T:Ns.Missing|asm2");
    }

    [Fact]
    public void CopyEdgesToSnapshot_SyntheticMembership_PreservesCleanupValidity()
    {
        var store = CreateStore();
        var sourceSnapshot = "snap-gnm-source";
        var targetSnapshot = "snap-gnm-target";
        var knownSymbol = "T:Ns.Controller|asm1";

        SaveSnapshotWithSymbols(store, sourceSnapshot, knownSymbol);
        SaveSnapshotWithSymbols(store, targetSnapshot, knownSymbol);

        store.SaveEdges(sourceSnapshot,
        [
            new EdgeRecord
            {
                SourceSymbolId = "route://api/values",
                TargetSymbolId = knownSymbol,
                Kind = "RoutesTo",
                Provenance = "framework_derived",
                ReceiverTypeConstraintsJson = "[[\"T:Ns.IReceiver|asm1\"]]",
                SourceNodeKind = GraphNodeKind.Route,
            },
            new EdgeRecord
            {
                SourceSymbolId = knownSymbol,
                TargetSymbolId = "convention:assembly_scan:MyLib",
                Kind = "Registers",
                Provenance = "convention",
                TargetNodeKind = GraphNodeKind.Convention,
            },
        ]);

        store.CopyEdgesToSnapshot(sourceSnapshot, targetSnapshot);

        store.DeleteOrphanEdges(targetSnapshot);

        var edges = store.GetEdges(targetSnapshot);
        Assert.Equal(2, edges.Count);
        Assert.Contains(edges, e => e.Kind == "RoutesTo");
        Assert.Contains(edges, e => e.Kind == "Registers");
        Assert.Contains(edges, e => e.ReceiverTypeConstraintsJson == "[[\"T:Ns.IReceiver|asm1\"]]");

        Assert.Equal(2, Scalar<long>(
            $"SELECT COUNT(*) FROM snapshot_graph_nodes WHERE snapshot_id = '{targetSnapshot}';"));
    }

    [Fact]
    public void DeleteSnapshotData_LastSyntheticMembership_RemovesGlobalNode()
    {
        var store = CreateStore();
        var snapshotId = "snap-gnm-prune";
        var knownSymbol = "T:Ns.Controller|asm1";

        SaveSnapshotWithSymbols(store, snapshotId, knownSymbol);

        store.SaveEdges(snapshotId,
        [
            new EdgeRecord
            {
                SourceSymbolId = "route://api/values",
                TargetSymbolId = knownSymbol,
                Kind = "RoutesTo",
                Provenance = "framework_derived",
                SourceNodeKind = GraphNodeKind.Route,
            },
        ]);

        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM graph_nodes;"));
        Assert.Equal(1, Scalar<long>(
            $"SELECT COUNT(*) FROM snapshot_graph_nodes WHERE snapshot_id = '{snapshotId}';"));

        store.DeleteSnapshotData(snapshotId);

        Assert.Equal(0, Scalar<long>(
            $"SELECT COUNT(*) FROM snapshot_graph_nodes WHERE snapshot_id = '{snapshotId}';"));
        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM graph_nodes;"));
    }

    [Fact]
    public void DeleteSnapshotData_RetainedSnapshotKeepsGlobalNode()
    {
        var store = CreateStore();
        var snap1 = "snap-gnm-keep1";
        var snap2 = "snap-gnm-keep2";
        var knownSymbol = "T:Ns.Controller|asm1";

        SaveSnapshotWithSymbols(store, snap1, knownSymbol);
        SaveSnapshotWithSymbols(store, snap2, knownSymbol);

        var edge = new EdgeRecord
        {
            SourceSymbolId = "route://api/values",
            TargetSymbolId = knownSymbol,
            Kind = "RoutesTo",
            Provenance = "framework_derived",
            SourceNodeKind = GraphNodeKind.Route,
        };

        store.SaveEdges(snap1, [edge]);
        store.SaveEdges(snap2, [edge]);

        store.DeleteSnapshotData(snap1);

        Assert.Equal(0, Scalar<long>(
            $"SELECT COUNT(*) FROM snapshot_graph_nodes WHERE snapshot_id = '{snap1}';"));
        Assert.Equal(1, Scalar<long>(
            $"SELECT COUNT(*) FROM snapshot_graph_nodes WHERE snapshot_id = '{snap2}';"));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM graph_nodes;"));
    }

    [Fact]
    public void SaveEdges_FailedWrite_PublishesNoPartialGraphNodeMembership()
    {
        // Transaction characterization: SaveEdges writes graph nodes, membership,
        // and edge rows inside one transaction. A mid-write failure must roll the
        // whole write back : a failed edge write can never publish partial
        // graph-node membership for that write.
        var store = CreateStore();
        var snapshotId = "snap-gnm-atomic";

        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TRIGGER fail_inject_edge_insert
                AFTER INSERT ON edges
                WHEN NEW.target_symbol_id = 'fail:inject'
                BEGIN
                    SELECT RAISE(ABORT, 'injected edge write failure');
                END;
            ";
            command.ExecuteNonQuery();
        }

        // First edge completes fully; second edge registers its graph nodes and
        // then fails at the edge insert, so the write has made visible progress
        // before failing.
        Assert.Throws<SqliteException>(() => store.SaveEdges(snapshotId,
        [
            new EdgeRecord
            {
                SourceSymbolId = "route://api/values",
                TargetSymbolId = "T:Ns.Controller|asm1",
                Kind = "RoutesTo",
                Provenance = "framework_derived",
                SourceNodeKind = GraphNodeKind.Route,
            },
            new EdgeRecord
            {
                SourceSymbolId = "convention:assembly_scan:MyLib",
                TargetSymbolId = "fail:inject",
                Kind = "Registers",
                Provenance = "convention",
                SourceNodeKind = GraphNodeKind.Convention,
                TargetNodeKind = GraphNodeKind.Convention,
            },
        ]));

        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM graph_nodes;"));
        Assert.Equal(0, Scalar<long>(
            $"SELECT COUNT(*) FROM snapshot_graph_nodes WHERE snapshot_id = '{snapshotId}';"));
        Assert.Equal(0, Scalar<long>(
            $"SELECT COUNT(*) FROM edges WHERE snapshot_id = '{snapshotId}';"));
    }

    [Fact]
    public void ExternalInterfaceImplementsEdgeSurvives()
    {
        var source = @"
using System;
class Foo : IDisposable
{
    public void Dispose() {}
}
";
        var compilation = CSharpCompilation.Create(
            "TestCompilation",
            [CSharpSyntaxTree.ParseText(source)],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var context = new SymbolExtractionContext(
            compilation,
            new Dictionary<Lurp.Workspace.DocumentId, (byte[] Content, string Encoding, string LineStarts)>(),
            new Dictionary<Lurp.Workspace.DocumentId, DocumentVersionId>(),
            new HashSet<Lurp.Workspace.DocumentId>(),
            "snap-ext-survive");

        var extractor = new SymbolStructuralEdgeExtractor(context);
        var edges = extractor.ExtractEdges();

        var implementsEdge = edges.FirstOrDefault(e => e.Kind == EdgeKind.Implements.ToString());
        Assert.NotNull(implementsEdge);
        Assert.Equal(GraphNodeKind.ExternalType, implementsEdge.TargetNodeKind);
        Assert.Contains("IDisposable", implementsEdge.TargetSymbolId, StringComparison.Ordinal);

        var store = CreateStore();
        SaveSnapshotWithSymbols(store, "snap-ext-survive", implementsEdge.SourceSymbolId);

        store.SaveEdges("snap-ext-survive", edges);
        store.DeleteOrphanEdges("snap-ext-survive");

        var surviving = store.GetEdges("snap-ext-survive");
        Assert.Contains(surviving, e => e.Kind == EdgeKind.Implements.ToString());
        Assert.Equal(1, Scalar<long>(
            $"SELECT COUNT(*) FROM graph_nodes WHERE node_kind = '{GraphNodeKind.ExternalType}'"));
        Assert.Equal(1, Scalar<long>(
            $"SELECT COUNT(*) FROM snapshot_graph_nodes WHERE snapshot_id = 'snap-ext-survive' AND node_id = '{implementsEdge.TargetSymbolId}'"));
    }

    [Fact]
    public void ExternalTypeNode_DedupByOriginalDefinition()
    {
        var source = @"
using System;
using System.Collections.Generic;
class C1 : IEnumerable<int>
{
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null;
    public IEnumerator<int> GetEnumerator() => null;
}
class C2 : IEnumerable<string>
{
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null;
    public IEnumerator<string> GetEnumerator() => null;
}
";
        var compilation = CSharpCompilation.Create(
            "TestCompilation",
            [CSharpSyntaxTree.ParseText(source)],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var context = new SymbolExtractionContext(
            compilation,
            new Dictionary<Lurp.Workspace.DocumentId, (byte[] Content, string Encoding, string LineStarts)>(),
            new Dictionary<Lurp.Workspace.DocumentId, DocumentVersionId>(),
            new HashSet<Lurp.Workspace.DocumentId>(),
            "snap-ext-dedup");

        var extractor = new SymbolStructuralEdgeExtractor(context);
        var edges = extractor.ExtractEdges();

        var implementsEdges = edges.Where(e => e.Kind == EdgeKind.Implements.ToString()
                                              && e.TargetNodeKind == GraphNodeKind.ExternalType).ToList();
        Assert.NotEmpty(implementsEdges);

        var externalIds = implementsEdges.Select(e => e.TargetSymbolId).Distinct().ToList();
        var enumerableId = externalIds.FirstOrDefault(id => id.Contains("IEnumerable", StringComparison.Ordinal));
        Assert.NotNull(enumerableId);
        Assert.Contains("`1", enumerableId);
    }

    [Fact]
    public void DeleteOrphanEdges_CompilerSynthesized_ClassifiedCorrectly()
    {
        var store = CreateStore();
        var snapshotId = "snap-gnm-cs";
        SaveSnapshotWithSymbols(store, snapshotId, "T:Ns.Foo|asm1");

        store.SaveEdges(snapshotId,
        [
            new EdgeRecord
            {
                SourceSymbolId = "T:Ns.Foo|asm1",
                TargetSymbolId = "T:Ns.<>c__DisplayClass0_0|asm1",
                Kind = "Calls",
                Provenance = "roslyn",
            },
        ]);

        var summary = store.DeleteOrphanEdges(snapshotId);

        Assert.Equal(1, summary.Total);
        Assert.Equal(0, summary.External);
        Assert.Equal(1, summary.CompilerSynthesized);
        Assert.Equal(0, summary.Other);
    }

    [Fact]
    public void DeleteOrphanEdges_ExternalAssemblyEndpoint_ClassifiedExternal()
    {
        var store = CreateStore();
        var snapshotId = "snap-gnm-ext";
        SaveSnapshotWithSymbols(store, snapshotId, "T:Ns.Foo|asm1");

        store.SaveEdges(snapshotId,
        [
            new EdgeRecord
            {
                SourceSymbolId = "T:Ns.Foo|asm1",
                TargetSymbolId = "T:System.Console|System.Console",
                Kind = "Calls",
                Provenance = "roslyn",
            },
        ]);

        var summary = store.DeleteOrphanEdges(snapshotId);

        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.External);
        Assert.Equal(0, summary.CompilerSynthesized);
        Assert.Equal(0, summary.Other);
    }

    [Fact]
    public void DeleteOrphanEdges_InScopeEndpointMissing_ClassifiedOther()
    {
        var store = CreateStore();
        var snapshotId = "snap-gnm-other";
        SaveSnapshotWithSymbols(store, snapshotId, "T:Ns.Foo|asm1");

        store.SaveEdges(snapshotId,
        [
            new EdgeRecord
            {
                SourceSymbolId = "T:Ns.Foo|asm1",
                TargetSymbolId = "T:Ns.Vanished|asm1",
                Kind = "Calls",
                Provenance = "roslyn",
            },
        ]);

        var summary = store.DeleteOrphanEdges(snapshotId);

        Assert.Equal(1, summary.Total);
        Assert.Equal(0, summary.External);
        Assert.Equal(0, summary.CompilerSynthesized);
        Assert.Equal(1, summary.Other);
    }

    [Fact]
    public void DeleteOrphanEdges_TotalEqualsSumOfBuckets()
    {
        var store = CreateStore();
        var snapshotId = "snap-gnm-total";
        SaveSnapshotWithSymbols(store, snapshotId, "T:Ns.Foo|asm1");

        store.SaveEdges(snapshotId,
        [
            new EdgeRecord { SourceSymbolId = "T:Ns.Foo|asm1", TargetSymbolId = "T:Ns.<>c__DisplayClass0_0|asm1", Kind = "Calls", Provenance = "roslyn" },
            new EdgeRecord { SourceSymbolId = "T:Ns.Foo|asm1", TargetSymbolId = "T:System.Console|System.Console", Kind = "Calls", Provenance = "roslyn" },
            new EdgeRecord { SourceSymbolId = "T:Ns.Foo|asm1", TargetSymbolId = "T:Ns.Vanished|asm1", Kind = "Calls", Provenance = "roslyn" },
        ]);

        var summary = store.DeleteOrphanEdges(snapshotId);

        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.External);
        Assert.Equal(1, summary.CompilerSynthesized);
        Assert.Equal(1, summary.Other);
        Assert.Equal(summary.Total, summary.External + summary.CompilerSynthesized + summary.Other);
    }
}
