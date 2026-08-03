using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Tests;

/// <summary>
/// A capsule anchored on an unresolvable <c>--file/--line</c> is still a capsule
/// and obeys the same finalization contract as any other. It is emitted on a
/// routine consumer miss : a comment, whitespace, an unindexed region, a wrong
/// path : so its trust properties are load-bearing, not an edge case.
/// </summary>
public sealed class GapCapsuleContractTests : IDisposable
{
    private const string SnapshotId = "snap-gap-capsule";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"gap_capsule_{Guid.NewGuid():N}.db");
    private SqliteIndexStore? _store;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void UnresolvableLocation_ProducesAFinalizedReasonCodedCapsule()
    {
        var capsule = AssembleGapCapsule();

        // The snapshot that answered is recorded: without it the capsule's
        // claims cannot be traced back to anything.
        Assert.Equal(SnapshotId, capsule.Anchor.SnapshotId);
        Assert.Equal("gap", capsule.Anchor.Kind);

        // The anchor asserts the ABSENCE of a symbol, so it carries no evidence
        // grade. "compiler_proved" on that assertion would be a false claim.
        Assert.NotEqual("compiler_proved", capsule.Anchor.Provenance);
        Assert.Equal(string.Empty, capsule.Anchor.Provenance);

        // Every tier is reason-coded "unresolved". A bare [] would read as a
        // proved absence under the capsule's own empty/unresolved contract, and
        // nothing was proved here.
        Assert.All(ContextAssembler.TierNames, tier =>
        {
            var record = Assert.Single(capsule.OmittedTiers, entry => entry.Category == tier);
            Assert.Equal("unresolved", record.Reason);
        });
        Assert.Contains(capsule.OmittedTiers, entry => entry.Reason == "unresolved");
        Assert.DoesNotContain(capsule.OmittedTiers, entry => entry.Reason == "empty");

        // The interpretation of "unresolved" travels with the capsule.
        Assert.Contains("omittedTiers.unresolved", capsule.InclusionReasons.Keys);
        Assert.Contains("NOT evidence", capsule.InclusionReasons["omittedTiers.unresolved"]);

        // The gap itself stays declared.
        var uncertainty = Assert.Single(capsule.Uncertainties);
        Assert.Equal("location_gap", uncertainty.RelationshipKind);

        // Finalized on the ordinary path: the estimate is the settled content
        // measure, not a placeholder zero next to present content.
        Assert.Equal(CapsuleBudgetEnforcer.Measure(capsule), capsule.EstimatedTokens);
        var emitted = ContextCapsuleJson.Serialize(capsule);
        Assert.InRange(capsule.EstimatedArtifactTokens, emitted.Length / 4 - 1, emitted.Length / 4 + 1);
    }

    [Fact]
    public void GapCapsule_SerializesNoTierAsAProvedEmptyCollection()
    {
        var capsule = AssembleGapCapsule();

        var document = JsonDocument.Parse(ContextCapsuleJson.Serialize(capsule));
        var omitted = document.RootElement.GetProperty("omittedTiers")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("category").GetString())
            .ToHashSet(StringComparer.Ordinal);

        // Each tier serializes as [] : the honest reading of that [] comes from
        // the paired omittedTiers record, which must exist for every one of them.
        foreach (var tier in ContextAssembler.TierNames)
        {
            Assert.Equal(0, document.RootElement.GetProperty(tier).GetArrayLength());
            Assert.Contains(tier, omitted);
        }
    }

    private ContextCapsule AssembleGapCapsule()
    {
        var store = CreateStore();
        return ContextAssembler.ResolveAndAssemble(
            store,
            store,
            new ContextLookup(SnapshotId, null, "src/NotIndexed.cs", 4200),
            new ContextAssemblyOptions(ContextIntent.Modify, Budget: 4000, MaxHops: 3),
            store,
            store);
    }

    private SqliteIndexStore CreateStore()
    {
        _store?.Dispose();
        _store = new SqliteIndexStore(_dbPath);
        _store.Open();
        _store.RunMigrations();
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO workspaces (workspace_id, git_root, solution_path)
            VALUES ('ws-gap-capsule', '/fake/root', 'test.sln');
            INSERT OR IGNORE INTO snapshots (snapshot_id, workspace_id, built_at_utc)
            VALUES (@sid, 'ws-gap-capsule', '2026-01-01T00:00:00Z');
        ";
        cmd.Parameters.AddWithValue("@sid", SnapshotId);
        cmd.ExecuteNonQuery();
        return _store;
    }
}
