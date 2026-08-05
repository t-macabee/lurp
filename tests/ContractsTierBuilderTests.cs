using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Storage.Tests;

/// <summary>
/// Contract tests for Task #4 of docs/reference/CAPSULE_AUDIT_MITIGATION.md
/// (framework contract facts): the contracts tier must surface base types
/// (Inherits), implemented interfaces (Implements), and member-level overrides
/// (Overrides) for a type anchor, and must surface framework targets declared
/// outside the snapshot as externally marked items instead of dropping them
/// silently (an external contract is never reported as absent).
/// </summary>
public sealed class ContractsTierBuilderTests : IDisposable
{
    private const string SnapshotId = "snap-contracts-tier";

    private const string AnchorType = "T:App.Worker|prod";
    private const string BaseType = "T:App.FrameworkService|prod";
    private const string Interface = "T:App.IHosted|prod";
    private const string OverrideMember = "M:App.Worker.ExecuteAsync|prod";
    private const string OverriddenMember = "M:App.FrameworkService.ExecuteAsync|prod";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"contracts_tier_{Guid.NewGuid():N}.db");
    private SqliteIndexStore? _store;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void TypeAnchor_BaseTypeInterfaceAndOverride_SurfaceInContractsTier()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, AnchorType);

        var baseItem = Assert.Single(capsule.Contracts, item => item.SymbolId == BaseType);
        Assert.Equal(EdgeKind.Inherits.ToString(), baseItem.EdgeKind);
        Assert.Equal("compiler_proved", baseItem.Provenance);

        var interfaceItem = Assert.Single(capsule.Contracts, item => item.SymbolId == Interface);
        Assert.Equal(EdgeKind.Implements.ToString(), interfaceItem.EdgeKind);

        // Member-level Overrides edges are reached through the effective anchor
        // scope: a type anchor surfaces the contracts its members carry.
        var overrideItem = Assert.Single(capsule.Contracts, item => item.SymbolId == OverriddenMember);
        Assert.Equal(EdgeKind.Overrides.ToString(), overrideItem.EdgeKind);
    }

    [Fact]
    public void ExternalFrameworkBaseType_SurfacesAsExternallyMarkedItem_NotDropped()
    {
        var store = CreateSeededStore(externalBase: true);
        var capsule = Assemble(store, AnchorType);

        // The external base type has no declaration in the snapshot; the
        // persisted Inherits edge still surfaces it under its canonical name.
        var item = Assert.Single(capsule.Contracts, item => item.SymbolId == ExternalBaseTypeId);
        Assert.Equal(EdgeKind.Inherits.ToString(), item.EdgeKind);
        Assert.Contains("BackgroundService", item.FullyQualifiedName, StringComparison.Ordinal);
        Assert.Equal("compiler_proved", item.Provenance);
        Assert.Null(item.Source);
        Assert.Contains("External contract", item.InclusionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void MethodAnchor_OwnOverride_SurfacesInContractsTier()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, OverrideMember);

        var item = Assert.Single(capsule.Contracts, item => item.SymbolId == OverriddenMember);
        Assert.Equal(EdgeKind.Overrides.ToString(), item.EdgeKind);
    }

    private const string ExternalBaseTypeId = "T:Microsoft.Extensions.Hosting.BackgroundService|Ext";

    private static ContextCapsule Assemble(SqliteIndexStore store, string symbolId)
        => new ContextAssembler
        {
            EdgeStore = store,
            DeclarationStore = store,
            BindingIncompletenessStore = store,
            SnapshotId = SnapshotId,
            SymbolId = SymbolId.Parse(symbolId),
            Intent = ContextIntent.Inspect,
            Budget = 100_000,
            MaxHops = 3,
            IncludeGenerated = false,
        }.Assemble();

    private SqliteIndexStore CreateSeededStore(bool externalBase = false)
    {
        _store?.Dispose();
        _store = new SqliteIndexStore(_dbPath);
        _store.Open();
        _store.RunMigrations();
        SeedFkReferences();
        _store.SaveDeclarations(SnapshotId, Symbols.Select(MakeDecl));
        _store.SaveEdges(SnapshotId, Edges(externalBase));
        return _store;
    }

    private static SymbolDeclaration MakeDecl(string symbolId)
    {
        var sid = SymbolId.Parse(symbolId);
        var kind = sid.DocCommentId[0] == 'T' ? IndexedSymbolKind.Type : IndexedSymbolKind.Method;
        return new SymbolDeclaration
        {
            SymbolId = sid,
            Kind = kind,
            DocumentVersionId = "doc-v-contracts-tier",
            FullSpan = new DeclarationSpan(null, null),
            SignatureSpan = new DeclarationSpan(null, null),
            BodySpan = new DeclarationSpan(null, null),
            NameSpan = new DeclarationSpan(null, null),
            MetadataJson = """{"accessibility":"Public"}""",
        };
    }

    private static EdgeRecord Edge(string source, string target, string kind)
        => new()
        {
            SourceSymbolId = source,
            TargetSymbolId = target,
            Kind = kind,
            Provenance = "compiler_proved",
            SnapshotId = SnapshotId,
            ExtractorVersion = "v1",
        };

    private static IEnumerable<EdgeRecord> Edges(bool externalBase)
    {
        // Type declares its members (the effective anchor scope).
        yield return Edge(AnchorType, OverrideMember, EdgeKind.Declares.ToString());

        // Base type and implemented interface (framework contract facts).
        yield return Edge(AnchorType, externalBase ? ExternalBaseTypeId : BaseType, EdgeKind.Inherits.ToString());
        yield return Edge(AnchorType, Interface, EdgeKind.Implements.ToString());

        // Member-level override of the framework entry point.
        yield return Edge(OverrideMember, OverriddenMember, EdgeKind.Overrides.ToString());
    }

    private static readonly string[] Symbols =
    [
        AnchorType,
        BaseType,
        Interface,
        OverrideMember,
        OverriddenMember,
    ];

    private void SeedFkReferences()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO workspaces (workspace_id, git_root, solution_path)
            VALUES ('ws-contracts-tier', '/fake/root', 'test.sln');
            INSERT OR IGNORE INTO snapshots (snapshot_id, workspace_id, built_at_utc)
            VALUES (@sid, 'ws-contracts-tier', '2026-01-01T00:00:00Z');
            INSERT OR IGNORE INTO documents (document_id, relative_path)
            VALUES ('doc-contracts-tier', 'test.cs');
            INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash)
            VALUES ('doc-v-contracts-tier', 'doc-contracts-tier', 'hash');
        ";
        cmd.Parameters.AddWithValue("@sid", SnapshotId);
        cmd.ExecuteNonQuery();
    }
}
