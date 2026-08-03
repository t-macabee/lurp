using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Storage.Tests;

/// <summary>
/// Regression tests for the capsule dispatch-provenance contract.
///
/// A caller that reaches a concrete implementation through
/// Calls → MayDispatchTo must be presented as an indirect dispatch candidate
/// (direct: false, effective provenance: possible), never as a direct
/// compiler-proved caller. The persisted MayDispatchTo edge itself stays
/// compiler_proved: it is a compiler-established structural implementation
/// candidate, and the downgrade happens only when the edge is composed into a
/// call-site claim. Genuine direct calls remain direct compiler-proved callers.
/// </summary>
public sealed class CapsuleProvenanceCompositionTests : IDisposable
{
    private const string SnapshotId = "snap-provenance-compose";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"indexer_provenance_compose_{Guid.NewGuid():N}.db");
    private SqliteIndexStore? _store;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void DirectConcreteCall_IsDirectCompilerProvedCaller()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "T:MyApp.Service|prod");

        // Controller.Run calls Service.Execute directly (compiler-proved Calls edge).
        var directCaller = Assert.Single(capsule.DirectCallers,
            item => item.SymbolId == "M:MyApp.Controller.Run|prod");
        Assert.Equal(EdgeKind.Calls.ToString(), directCaller.EdgeKind);
        Assert.Equal("compiler_proved", directCaller.Provenance);
        Assert.Equal(CapsuleRelationship.DirectCaller, directCaller.Relationship);
        Assert.True(directCaller.Direct);
    }

    [Fact]
    public void InterfaceMediatedCaller_IsIndirectDispatchCandidate()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "T:MyApp.HelperImpl|prod");

        // IRootHelper.Do --MayDispatchTo/compiler_proved--> HelperImpl.Do.
        // Service.Execute calls IRootHelper.Do, so Service.Execute is an
        // interface-mediated caller of HelperImpl — not a direct caller.
        var mediatedCaller = Assert.Single(capsule.DirectCallers,
            item => item.SymbolId == "M:MyApp.Service.Execute|prod");
        Assert.Equal(EdgeKind.Calls.ToString(), mediatedCaller.EdgeKind);
        Assert.Equal("possible", mediatedCaller.Provenance);
        Assert.Equal(CapsuleRelationship.IndirectDispatchCandidate, mediatedCaller.Relationship);
        Assert.False(mediatedCaller.Direct);
    }

    [Fact]
    public void DispatchMediatedItems_AreNeverDirectCompilerProved()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "T:MyApp.HelperImpl|prod");

        // No capsule entry reached through dispatch may carry the direct-caller
        // relationship, a direct:true flag, or compiler_proved call-site
        // provenance.
        var dispatchMediatedItems = new[] { capsule.DirectCallers, capsule.SecondDegreeContext }
            .SelectMany(static tier => tier)
            .Where(item => item.Relationship == CapsuleRelationship.IndirectDispatchCandidate)
            .ToList();
        Assert.NotEmpty(dispatchMediatedItems);
        foreach (var item in dispatchMediatedItems)
        {
            Assert.NotEqual(CapsuleRelationship.DirectCaller, item.Relationship);
            Assert.False(item.Direct);
            Assert.NotEqual("compiler_proved", item.Provenance);
        }
    }

    [Fact]
    public void DirectItems_AreNeverDowngraded()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "T:MyApp.Service|prod");

        // A genuine direct call keeps direct:true and compiler_proved.
        var directCallers = capsule.DirectCallers
            .Where(item => item.Relationship == CapsuleRelationship.DirectCaller)
            .ToList();
        Assert.NotEmpty(directCallers);
        foreach (var item in directCallers)
        {
            Assert.True(item.Direct);
            Assert.Equal("compiler_proved", item.Provenance);
        }
    }

    [Fact]
    public void CapsulePreservesBothSteps_InInclusionReason()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "T:MyApp.HelperImpl|prod");

        // Every composed caller's inclusion reason must make both underlying
        // steps visible: the Calls edge to the interface member and the
        // MayDispatchTo edge to the implementation.
        var dispatchCallers = capsule.DirectCallers
            .Where(item => item.Relationship == CapsuleRelationship.IndirectDispatchCandidate)
            .ToList();
        Assert.NotEmpty(dispatchCallers);
        foreach (var caller in dispatchCallers)
        {
            Assert.False(string.IsNullOrEmpty(caller.InclusionReason),
                $"Direct caller '{caller.SymbolId}' is missing an inclusion reason.");
            Assert.Contains("Calls", caller.InclusionReason);
            Assert.Contains("MayDispatchTo", caller.InclusionReason);
        }
    }

    [Fact]
    public void MayDispatchToEdge_StaysCompilerProvedInTheGraph()
    {
        var store = CreateSeededStore();

        // The stored fact is not downgraded: the MayDispatchTo edge remains a
        // compiler-proved structural implementation candidate.
        var dispatchEdges = store.GetIncomingEdges(SnapshotId, "M:MyApp.HelperImpl.Do|prod")
            .Where(edge => edge.Kind == EdgeKind.MayDispatchTo.ToString())
            .ToList();
        var dispatchEdge = Assert.Single(dispatchEdges);
        Assert.Equal("compiler_proved", dispatchEdge.Provenance);
    }

    [Fact]
    public void EdgeDedup_ComposeDispatchClaimProvenance_GradesTheClaimNotTheEdges()
    {
        // Individually compiler-proved structural edges compose to a possible
        // runtime-target claim: the compiler proves the implementation exists,
        // not that this call site selects it at runtime.
        Assert.Equal("possible", EdgeDedup.ComposeDispatchClaimProvenance(
            ["compiler_proved"], "compiler_proved"));
        Assert.Equal("possible", EdgeDedup.ComposeDispatchClaimProvenance(
            ["compiler_proved"], "possible"));
        Assert.Equal("possible", EdgeDedup.ComposeDispatchClaimProvenance(
            ["possible"], "compiler_proved"));

        // Framework participation anywhere in the composed path is the only
        // grade that survives dispatch mediation.
        Assert.Equal("framework_derived", EdgeDedup.ComposeDispatchClaimProvenance(
            ["framework_derived"], "compiler_proved"));
        Assert.Equal("framework_derived", EdgeDedup.ComposeDispatchClaimProvenance(
            ["compiler_proved"], "framework_derived"));
        Assert.Equal("framework_derived", EdgeDedup.ComposeDispatchClaimProvenance(
            ["compiler_proved", "framework_derived"], "compiler_proved"));
    }

    [Fact]
    public void EdgeDedup_ProvenanceRank_ExplicitlyOrdersEveryCanonicalValue()
    {
        Assert.True(EdgeDedup.ProvenanceRank("compiler_proved") > EdgeDedup.ProvenanceRank("framework_derived"));
        Assert.True(EdgeDedup.ProvenanceRank("framework_derived") > EdgeDedup.ProvenanceRank("global_implementation_relation"));
        Assert.True(EdgeDedup.ProvenanceRank("global_implementation_relation") > EdgeDedup.ProvenanceRank("possible"));
        Assert.True(EdgeDedup.ProvenanceRank("possible") > EdgeDedup.ProvenanceRank("convention"));
        Assert.True(EdgeDedup.ProvenanceRank("convention") > EdgeDedup.ProvenanceRank("name_candidate"));
        Assert.True(EdgeDedup.ProvenanceRank("name_candidate") > EdgeDedup.ProvenanceRank("runtime_unknown"));
        Assert.True(EdgeDedup.ProvenanceRank("runtime_unknown") > EdgeDedup.ProvenanceRank("unrecognized"));
    }

    [Fact]
    public void CapsuleItem_DispatchFieldsSerializeAndDeserialize()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "T:MyApp.HelperImpl|prod");
        var expected = Assert.Single(capsule.DirectCallers,
            item => item.SymbolId == "M:MyApp.Service.Execute|prod");

        var json = JsonSerializer.Serialize(expected, ContextCapsuleJson.Options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(CapsuleRelationship.IndirectDispatchCandidate,
            document.RootElement.GetProperty("relationship").GetString());
        Assert.False(document.RootElement.GetProperty("direct").GetBoolean());
        Assert.Equal("possible", document.RootElement.GetProperty("provenance").GetString());

        var roundTripped = JsonSerializer.Deserialize<CapsuleItem>(json, ContextCapsuleJson.Options);
        Assert.NotNull(roundTripped);
        Assert.Equal(expected.SymbolId, roundTripped.SymbolId);
        Assert.Equal(expected.Relationship, roundTripped.Relationship);
        Assert.Equal(expected.Direct, roundTripped.Direct);
        Assert.Equal(expected.Provenance, roundTripped.Provenance);
        Assert.Equal(expected.InclusionReason, roundTripped.InclusionReason);
    }

    [Fact]
    public void FrameworkEdgeInPath_KeepsComposedClaimFrameworkDerived()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "T:MyApp.HelperImpl|prod");

        // FrameworkEntry.Run reaches the dispatch source through a
        // framework-derived Calls edge, so its composed claim is
        // framework_derived — the only case where dispatch mediation does not
        // collapse the claim to possible.
        var frameworkCaller = Assert.Single(capsule.DirectCallers,
            item => item.SymbolId == "M:MyApp.FrameworkEntry.Run|prod");
        Assert.Equal("framework_derived", frameworkCaller.Provenance);
        Assert.Equal(CapsuleRelationship.IndirectDispatchCandidate, frameworkCaller.Relationship);
        Assert.False(frameworkCaller.Direct);
    }

    [Fact]
    public void DirectConcreteCallee_StaysCompilerProvedDirectCallee()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "T:MyApp.Service|prod");

        // Execute directly calls the interface member: a direct compiler-proved
        // callee, never downgraded by the dispatch projection rules.
        var interfaceCallee = Assert.Single(capsule.DirectCallees,
            item => item.SymbolId == "M:MyApp.IRootHelper.Do|prod");
        Assert.Equal(EdgeKind.Calls.ToString(), interfaceCallee.EdgeKind);
        Assert.Equal("compiler_proved", interfaceCallee.Provenance);
        Assert.Equal(CapsuleRelationship.DirectCallee, interfaceCallee.Relationship);
        Assert.True(interfaceCallee.Direct);
    }

    [Fact]
    public void DispatchProjectedCallee_IsGlobalImplementationRelationNotDirectCallee()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, "T:MyApp.Service|prod");

        // HelperImpl.Do is included only because it globally implements the
        // called interface member. The projection is a
        // global_implementation_relation — never a direct callee — and both
        // underlying steps stay visible in the inclusion reason.
        var dispatchTarget = Assert.Single(capsule.DirectCallees,
            item => item.SymbolId == "M:MyApp.HelperImpl.Do|prod");
        Assert.Equal(EdgeKind.MayDispatchTo.ToString(), dispatchTarget.EdgeKind);
        Assert.Equal("global_implementation_relation", dispatchTarget.Provenance);
        Assert.NotEqual(CapsuleRelationship.DirectCallee, dispatchTarget.Relationship);
        Assert.Equal(CapsuleRelationship.IndirectDispatchCandidate, dispatchTarget.Relationship);
        Assert.False(dispatchTarget.Direct);
        Assert.Contains("Calls", dispatchTarget.InclusionReason);
        Assert.Contains("MayDispatchTo", dispatchTarget.InclusionReason);
        Assert.DoesNotContain(capsule.DirectCallees, item => item.SymbolId == "T:MyApp.HelperImpl|prod");
    }

    [Fact]
    public void DirectCalleeThatIsAlsoDispatchTarget_KeepsDirectLabel()
    {
        var store = CreateSeededStore();

        // The anchor both directly calls the concrete implementation and calls
        // the interface member that may dispatch to it. The direct call wins:
        // the target is a direct callee, never downgraded to a dispatch
        // projection by edge enumeration order.
        store.SaveEdges(SnapshotId,
        [
            Edge("M:MyApp.Service.Execute|prod", "M:MyApp.HelperImpl.Do|prod", EdgeKind.Calls.ToString(), "compiler_proved"),
        ]);
        var capsule = Assemble(store, "T:MyApp.Service|prod");

        var callee = Assert.Single(capsule.DirectCallees,
            item => item.SymbolId == "M:MyApp.HelperImpl.Do|prod");
        Assert.Equal(EdgeKind.Calls.ToString(), callee.EdgeKind);
        Assert.Equal("compiler_proved", callee.Provenance);
        Assert.Equal(CapsuleRelationship.DirectCallee, callee.Relationship);
        Assert.True(callee.Direct);
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
            DocumentVersionId = "doc-v-provenance",
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
        // Service declares its members.
        yield return Edge("T:MyApp.Service|prod", "M:MyApp.Service.Execute|prod", EdgeKind.Declares.ToString(), "compiler_proved");

        // HelperImpl declares Do.
        yield return Edge("T:MyApp.HelperImpl|prod", "M:MyApp.HelperImpl.Do|prod", EdgeKind.Declares.ToString(), "compiler_proved");

        // Service.Execute calls an interface member (compiler-proved Calls edge)
        // with a persisted static receiver constraint naming HelperImpl.
        yield return Edge("M:MyApp.Service.Execute|prod", "M:MyApp.IRootHelper.Do|prod", EdgeKind.Calls.ToString(), "compiler_proved",
            ReceiverTypeConstraints.SerializeForTests("T:MyApp.HelperImpl|prod"));

        // A framework entry point calls the same interface member through a
        // framework-derived edge.
        yield return Edge("M:MyApp.FrameworkEntry.Run|prod", "M:MyApp.IRootHelper.Do|prod", EdgeKind.Calls.ToString(), "framework_derived");

        // The interface member dispatches to the concrete implementation. The
        // stored fact is a compiler-established structural implementation
        // candidate and stays compiler_proved.
        yield return Edge("M:MyApp.IRootHelper.Do|prod", "M:MyApp.HelperImpl.Do|prod", EdgeKind.MayDispatchTo.ToString(), "compiler_proved");

        // A genuine direct call to the concrete implementation for comparison.
        yield return Edge("M:MyApp.Controller.Run|prod", "M:MyApp.Service.Execute|prod", EdgeKind.Calls.ToString(), "compiler_proved");
    }

    private static readonly string[] Symbols =
    [
        "T:MyApp.Service|prod",
        "M:MyApp.Service.Execute|prod",
        "T:MyApp.HelperImpl|prod",
        "M:MyApp.HelperImpl.Do|prod",
        "M:MyApp.IRootHelper.Do|prod",
        "T:MyApp.Controller|prod",
        "M:MyApp.Controller.Run|prod",
        "T:MyApp.FrameworkEntry|prod",
        "M:MyApp.FrameworkEntry.Run|prod",
    ];

    private void SeedFkReferences()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO workspaces (workspace_id, git_root, solution_path)
            VALUES ('ws-provenance', '/fake/root', 'test.sln');
            INSERT OR IGNORE INTO snapshots (snapshot_id, workspace_id, built_at_utc)
            VALUES (@sid, 'ws-provenance', '2026-01-01T00:00:00Z');
            INSERT OR IGNORE INTO documents (document_id, relative_path)
            VALUES ('doc-provenance', 'test.cs');
            INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash)
            VALUES ('doc-v-provenance', 'doc-provenance', 'hash');
        ";
        cmd.Parameters.AddWithValue("@sid", SnapshotId);
        ExecuteNonQuery(cmd);
    }

    private static void ExecuteNonQuery(SqliteCommand cmd)
    {
        cmd.ExecuteNonQuery();
    }
}
