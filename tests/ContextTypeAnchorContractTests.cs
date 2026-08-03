using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Storage.Tests;

/// <summary>
/// Contract tests for the effective anchor scope: a type anchor must reach the
/// same member-level facts (Calls, Constructs, MayDispatchTo, TestedBy) that the
/// same anchor at a member would reach, by expanding the type to its directly
/// declared members through the persisted Declares edges.
/// </summary>
public sealed class ContextTypeAnchorContractTests : IDisposable
{
    private const string SnapshotId = "snap-type-anchor";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"indexer_type_anchor_{Guid.NewGuid():N}.db");
    private SqliteIndexStore? _store;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void TypeAnchor_ReachesTheMemberFactsOfAMethodAnchor()
    {
        var store = CreateSeededStore();

        var methodCapsule = Assemble(store, "M:MyApp.Service.Execute|prod");
        var typeCapsule = Assemble(store, "T:MyApp.Service|prod");

        AssertMethodReachedByType(methodCapsule.DirectCallees, typeCapsule.DirectCallees);
        AssertMethodReachedByType(methodCapsule.DirectCallers, typeCapsule.DirectCallers);
        AssertMethodReachedByType(methodCapsule.RegisteredImplementations, typeCapsule.RegisteredImplementations);
        AssertMethodReachedByType(methodCapsule.RelevantTests, typeCapsule.RelevantTests);
        AssertMethodReachedByType(methodCapsule.SecondDegreeContext, typeCapsule.SecondDegreeContext);
    }

    [Fact]
    public void TypeAnchor_DirectCallees_IncludeInterfaceMemberAndItsDispatchImplementations()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "T:MyApp.Service|prod");

        // The directly called interface member is surfaced via the member-level Calls edge.
        Assert.Contains(capsule.DirectCallees, item => item.SymbolId == "M:MyApp.IRootHelper.Do|prod");

        // The called member is declared on the broader IRootHelper contract, but
        // the call receiver is statically IHelper. Only HelperImpl is assignable to
        // IHelper; RootOnlyHelper remains a valid global implementation relation but
        // is not a receiver-compatible candidate for this call site.
        var dispatchTarget = Assert.Single(capsule.DirectCallees, item => item.SymbolId == "M:MyApp.HelperImpl.Do|prod");
        Assert.Equal(EdgeKind.MayDispatchTo.ToString(), dispatchTarget.EdgeKind);
        Assert.Equal("compiler_proved", dispatchTarget.Provenance);
        Assert.DoesNotContain(capsule.DirectCallees, item => item.SymbolId == "M:MyApp.RootOnlyHelper.Do|prod");
        Assert.DoesNotContain(capsule.DirectCallees, item => item.SymbolId == "T:MyApp.Service|prod");

        // Facts on a sibling member (GetById) are also reached from the type anchor.
        Assert.Contains(capsule.DirectCallees, item => item.SymbolId == "M:MyApp.Repo.GetById|prod");
    }

    [Fact]
    public void TypeAnchor_ReachesDispatchRegistrationCallersAndTests()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "T:MyApp.Service|prod");

        Assert.Contains(capsule.RegisteredImplementations, item => item.SymbolId == "M:MyApp.IService.Execute|prod");
        Assert.Contains(capsule.DirectCallers, item => item.SymbolId == "M:MyApp.Controller.Run|prod");
        Assert.Contains(capsule.RelevantTests, item => item.SymbolId == "M:MyApp.Tests.ServiceTests.Execute_Works|test");
        Assert.Contains(capsule.SecondDegreeContext, item => item.SymbolId == "M:MyApp.Controller.Run|prod");
    }

    [Fact]
    public void TypeAnchor_DeclaresMissingReceiverEvidenceInsteadOfEmittingGlobalCandidates()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "T:MyApp.Service|prod");

        Assert.Contains(capsule.Uncertainties, uncertainty =>
            uncertainty.RelationshipKind == "receiver_constraints_unavailable" &&
            uncertainty.SymbolIds.Contains("M:MyApp.Service.Helper|prod"));
    }

    [Fact]
    public void MethodAnchor_DoesNotExpandAcrossSiblingMembers()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "M:MyApp.Service.Execute|prod");

        Assert.Contains(capsule.DirectCallees, item => item.SymbolId == "M:MyApp.IRootHelper.Do|prod");
        Assert.DoesNotContain(capsule.DirectCallees, item => item.SymbolId == "M:MyApp.Repo.GetById|prod");
    }

    [Fact]
    public void TypeAnchor_SurroundingSource_ReturnsItsDeclaredMembers()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "T:MyApp.Service|prod");

        var siblingIds = capsule.SurroundingSource.Select(item => item.SymbolId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("M:MyApp.Service.Execute|prod", siblingIds);
        Assert.Contains("M:MyApp.Service.GetById|prod", siblingIds);
        Assert.Contains("M:MyApp.Service.Helper|prod", siblingIds);
        Assert.DoesNotContain("T:MyApp.Service|prod", siblingIds);
        Assert.DoesNotContain("M:MyApp.Repo.GetById|prod", siblingIds);
        Assert.All(capsule.SurroundingSource, item => Assert.Equal(EdgeKind.Declares.ToString(), item.EdgeKind));
    }

    [Fact]
    public void MethodAnchor_SurroundingSource_ReturnsSiblingMembersOfContainingType()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "M:MyApp.Service.Execute|prod");

        var siblingIds = capsule.SurroundingSource.Select(item => item.SymbolId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("M:MyApp.Service.GetById|prod", siblingIds);
        Assert.Contains("M:MyApp.Service.Helper|prod", siblingIds);
        Assert.DoesNotContain("M:MyApp.Service.Execute|prod", siblingIds);
        Assert.DoesNotContain("M:MyApp.Repo.GetById|prod", siblingIds);
    }

    private static void AssertMethodReachedByType(List<CapsuleItem> methodItems, List<CapsuleItem> typeItems)
    {
        var typeIds = typeItems.Select(item => item.SymbolId).ToHashSet(StringComparer.Ordinal);
        foreach (var item in methodItems)
        {
            Assert.True(
                typeIds.Contains(item.SymbolId),
                $"Type anchor should reach member-level fact '{item.SymbolId}' surfaced by the method anchor.");
        }
    }

    private SqliteIndexStore CreateSeededStore()
    {
        _store?.Dispose();
        _store = new SqliteIndexStore(_dbPath);
        _store.Open();
        _store.RunMigrations();
        SeedFkReferences();
        _store.SaveDeclarations(SnapshotId, Symbols.Select(MakeDecl));
        _store.SaveEdges(SnapshotId, Edges());
        return _store;
    }

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

    private static SymbolDeclaration MakeDecl(string symbolId)
    {
        var sid = SymbolId.Parse(symbolId);
        var kind = sid.DocCommentId[0] == 'T' ? IndexedSymbolKind.Type : IndexedSymbolKind.Method;
        return new SymbolDeclaration
        {
            SymbolId = sid,
            Kind = kind,
            DocumentVersionId = "doc-v-type-anchor",
            FullSpan = new DeclarationSpan(null, null),
            SignatureSpan = new DeclarationSpan(null, null),
            BodySpan = new DeclarationSpan(null, null),
            NameSpan = new DeclarationSpan(null, null),
            MetadataJson = """{"accessibility":"Public"}""",
        };
    }

    private static EdgeRecord Edge(string source, string target, string kind, string provenance, string? receiverConstraintsJson = null)
        => new()
        {
            SourceSymbolId = source,
            TargetSymbolId = target,
            Kind = kind,
            Provenance = provenance,
            SnapshotId = SnapshotId,
            ExtractorVersion = "v1",
            ReceiverTypeConstraintsJson = receiverConstraintsJson,
        };

    private static IEnumerable<EdgeRecord> Edges()
    {
        // Type declares its members (the effective scope for a type anchor).
        yield return Edge("T:MyApp.Service|prod", "M:MyApp.Service.Execute|prod", EdgeKind.Declares.ToString(), "compiler_proved");
        yield return Edge("T:MyApp.Service|prod", "M:MyApp.Service.GetById|prod", EdgeKind.Declares.ToString(), "compiler_proved");
        yield return Edge("T:MyApp.Service|prod", "M:MyApp.Service.Helper|prod", EdgeKind.Declares.ToString(), "compiler_proved");

        // Execute calls an interface member, which dispatches to a concrete impl.
        yield return Edge("M:MyApp.Service.Execute|prod", "M:MyApp.IRootHelper.Do|prod", EdgeKind.Calls.ToString(), "compiler_proved",
            ReceiverTypeConstraints.SerializeForTests("T:MyApp.IHelper|prod"));
        yield return Edge("M:MyApp.Service.Helper|prod", "M:MyApp.IRootHelper.Do|prod", EdgeKind.Calls.ToString(), "compiler_proved");
        yield return Edge("M:MyApp.IRootHelper.Do|prod", "M:MyApp.HelperImpl.Do|prod", EdgeKind.MayDispatchTo.ToString(), "compiler_proved");
        yield return Edge("M:MyApp.IRootHelper.Do|prod", "M:MyApp.RootOnlyHelper.Do|prod", EdgeKind.MayDispatchTo.ToString(), "compiler_proved");
        yield return Edge("T:MyApp.IHelper|prod", "T:MyApp.IRootHelper|prod", EdgeKind.Implements.ToString(), "compiler_proved");
        yield return Edge("T:MyApp.HelperImpl|prod", "T:MyApp.IHelper|prod", EdgeKind.Implements.ToString(), "compiler_proved");
        yield return Edge("T:MyApp.RootOnlyHelper|prod", "T:MyApp.IRootHelper|prod", EdgeKind.Implements.ToString(), "compiler_proved");
        yield return Edge("T:MyApp.HelperImpl|prod", "M:MyApp.HelperImpl.Do|prod", EdgeKind.Declares.ToString(), "compiler_proved");
        yield return Edge("T:MyApp.RootOnlyHelper|prod", "M:MyApp.RootOnlyHelper.Do|prod", EdgeKind.Declares.ToString(), "compiler_proved");

        // GetById calls a concrete member on a sibling member.
        yield return Edge("M:MyApp.Service.GetById|prod", "M:MyApp.Repo.GetById|prod", EdgeKind.Calls.ToString(), "compiler_proved");

        // The service's Execute implements the interface member (dispatch registration).
        yield return Edge("M:MyApp.IService.Execute|prod", "M:MyApp.Service.Execute|prod", EdgeKind.MayDispatchTo.ToString(), "compiler_proved");

        // A controller calls the service member directly.
        yield return Edge("M:MyApp.Controller.Run|prod", "M:MyApp.Service.Execute|prod", EdgeKind.Calls.ToString(), "compiler_proved");

        // The service type carries TestedBy evidence to a test method.
        yield return Edge("T:MyApp.Service|prod", "M:MyApp.Tests.ServiceTests.Execute_Works|test", EdgeKind.TestedBy.ToString(), "framework_derived");
    }

    private static readonly string[] Symbols =
    [
        "T:MyApp.Service|prod",
        "M:MyApp.Service.Execute|prod",
        "M:MyApp.Service.GetById|prod",
        "M:MyApp.Service.Helper|prod",
        "T:MyApp.IHelper|prod",
        "T:MyApp.IRootHelper|prod",
        "M:MyApp.IRootHelper.Do|prod",
        "T:MyApp.HelperImpl|prod",
        "M:MyApp.HelperImpl.Do|prod",
        "T:MyApp.RootOnlyHelper|prod",
        "M:MyApp.RootOnlyHelper.Do|prod",
        "T:MyApp.Repo|prod",
        "M:MyApp.Repo.GetById|prod",
        "T:MyApp.IService|prod",
        "M:MyApp.IService.Execute|prod",
        "T:MyApp.Controller|prod",
        "M:MyApp.Controller.Run|prod",
        "M:MyApp.Tests.ServiceTests.Execute_Works|test",
    ];

    private void SeedFkReferences()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO workspaces (workspace_id, git_root, solution_path)
            VALUES ('ws-type-anchor', '/fake/root', 'test.sln');
            INSERT OR IGNORE INTO snapshots (snapshot_id, workspace_id, built_at_utc)
            VALUES (@sid, 'ws-type-anchor', '2026-01-01T00:00:00Z');
            INSERT OR IGNORE INTO documents (document_id, relative_path)
            VALUES ('doc-type-anchor', 'test.cs');
            INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash)
            VALUES ('doc-v-type-anchor', 'doc-type-anchor', 'hash');
        ";
        cmd.Parameters.AddWithValue("@sid", SnapshotId);
        cmd.ExecuteNonQuery();
    }
}
