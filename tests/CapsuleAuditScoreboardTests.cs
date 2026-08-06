using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Storage.Tests;

/// <summary>
/// Task #2 of docs/reference/CAPSULE_AUDIT_MITIGATION.md: freezes the seven
/// eNoteV2 audit findings (the headline table in lurp_audit.txt) as a
/// regression scoreboard. The eNoteV2 corpus is external, so each finding's
/// pattern is reproduced by the committed in-repo fixture under
/// tests/fixtures/CapsuleAudit (see its README for the finding→pattern map).
///
/// Test labels follow AGENTS.md:
/// - ACCEPTANCE (findings 1, 2, 3, 4, 6, 7): the intended product behavior. Finding 1
///   became acceptable when Task #1 landed (unmodeled registrations must never
///   report "empty"); finding 2 became acceptable when Task #4 landed
///   (framework contract facts: BackgroundService base type and the
///   ExecuteAsync override in the contracts tier); findings 3 and 4 became
///   acceptable when Task #5 landed (EF declarative constraints:
///   HasQueryFilter and unique index names surfaced in the constraints tier);
///   findings 6 and 7 guard against regression.
/// - CHARACTERIZATION (finding 5): proves the current behavior,
///   including the deficiency. This is NOT an acceptance test. Finding 5 is
///   an accepted declared boundary (dead code has no call edges).
/// </summary>
public sealed class CapsuleAuditScoreboardTests : IDisposable
{
    private const string OutboxPublisherFqn = "CapsuleAudit.Infrastructure.RentalNotificationOutboxPublisher";
    private const string QueryServiceFqn = "CapsuleAudit.Infrastructure.RentalQueryService";
    private const string StateMachineFqn = "CapsuleAudit.Infrastructure.RentalStateMachine";

    private readonly string _outputDir = Path.Combine(
        Path.GetTempPath(), $"lurp-capsule-audit-{Guid.NewGuid():N}");
    private readonly Lazy<Task<IndexedFixture>> _fixture;

    public CapsuleAuditScoreboardTests()
    {
        _fixture = new Lazy<Task<IndexedFixture>>(IndexFixtureAsync);
    }

    private sealed record IndexedFixture(string SnapshotId, SqliteIndexStore Store);

    // ACCEPTANCE — Finding 1 (Critical): the outbox publisher is registered in
    // both hosts via services.AddHostedService<RentalNotificationOutboxPublisher>().
    // After Task #1 the registeredImplementations tier must either surface the
    // registration or declare it as "unmodeled_construct" — never "empty" —
    // and an uncertainties entry must name the unmodeled construct.
    [SkippableFact]
    public async Task Finding1_OutboxRegistration_NeverReportedAsEmpty()
    {
        var capsule = await CapsuleForAsync(OutboxPublisherFqn);

        Assert.DoesNotContain(capsule.OmittedTiers,
            entry => entry.Category == "registeredImplementations" && entry.Reason == "empty");

        if (capsule.RegisteredImplementations.Count == 0)
        {
            Assert.Contains(capsule.OmittedTiers,
                entry => entry.Category == "registeredImplementations"
                      && entry.Reason == "unmodeled_construct");
        }

        Assert.Contains(capsule.Uncertainties,
            u => u.Description.Contains("Unmodeled construct:", StringComparison.Ordinal));
    }

    // ACCEPTANCE — Finding 2 (High), Task #4 landed. The contracts tier now
    // carries the BackgroundService base-type fact (the framework entry point
    // with the StopHost exception contract), and the ExecuteAsync override is
    // identifiable as a framework contract member (edgeKind Overrides) rather
    // than merely a caller of ProcessBatchAsync.
    [SkippableFact]
    public async Task Finding2_RethrowStopsHost_CodeShownContractPresent_Acceptance()
    {
        var capsule = await CapsuleForAsync(OutboxPublisherFqn);

        Assert.Contains("ExecuteAsync", capsule.Anchor.Source, StringComparison.Ordinal);

        // The BackgroundService base-type contract fact.
        Assert.Contains(capsule.Contracts,
            i => i.FullyQualifiedName.Contains("BackgroundService", StringComparison.Ordinal));

        // The ExecuteAsync override is identifiable as a framework entry point:
        // carried by the contracts tier as an override of the framework member,
        // not only as a caller of ProcessBatchAsync.
        Assert.Contains(capsule.Contracts,
            i => i.FullyQualifiedName.Contains("ExecuteAsync", StringComparison.Ordinal)
              && i.EdgeKind == EdgeKind.Overrides.ToString());
    }

    // ACCEPTANCE — Finding 3 (High), Task #5 landed. The global query filter in
    // ENoteContext.OnModelCreating that silently rewrites every
    // Set<InstrumentRental>() is now surfaced in the constraints tier via the
    // EF Core adapter's entity constraint annotations.
    [SkippableFact]
    public async Task Finding3_StoreReadsFailOpen_TestsSurfaced_FilterVisible_Acceptance()
    {
        var capsule = await CapsuleForAsync(QueryServiceFqn);

        Assert.Contains(capsule.RelevantTests,
            t => t.FullyQualifiedName.Contains(
                "TenantIsolationTests.GetPagedForStoreAsync_ExcludesOtherStoreRentals", StringComparison.Ordinal));
        Assert.Contains(capsule.RelevantTests,
            t => t.FullyQualifiedName.Contains(
                "TenantIsolationTests.GetByIdForStoreAsync_Throws_WhenRentalBelongsToOtherStore", StringComparison.Ordinal));

        Assert.Contains(capsule.Constraints,
            c => c.Value.Contains("HasQueryFilter", StringComparison.Ordinal)
              && c.Value.Contains("IsActive", StringComparison.Ordinal));
    }

    // ACCEPTANCE — Finding 4 (Medium), Task #5 landed. The unique-index
    // database name string-matched by SaveWithLockConflictMessageAsync is now
    // surfaced in the constraints tier via the EF Core adapter's entity
    // constraint annotations.
    [SkippableFact]
    public async Task Finding4_UnreachableGuard_GuardShownIndexNameVisible_Acceptance()
    {
        var capsule = await CapsuleForAsync(QueryServiceFqn);

        Assert.Contains(capsule.DirectCallees,
            i => i.FullyQualifiedName.Contains("GuardInstrumentActive", StringComparison.Ordinal)
              && i.Source != null);

        Assert.Contains(capsule.Constraints,
            c => c.Value.Contains("UX_InstrumentRental_InstrumentId_ActiveOrApproved", StringComparison.Ordinal));
    }

    // CHARACTERIZATION — Finding 5 (Low), accepted declared boundary per the
    // mitigation document. GetForStoreAuditAsync is dead: no host, handler, or
    // test calls it. Observed current behavior: the declaration is indexed and
    // the member IS visible in the capsule — but only through the anchor
    // type's Declares edges. Nothing surfaces that it is dead: no caller edges
    // exist and no tier carries a reachability verdict. That missing deadness
    // signal is the declared boundary this test pins, so a future dead-code
    // capability shows up as a deliberate scoreboard change, not silent drift.
    [SkippableFact]
    public async Task Finding5_ForStoreAuditDead_DeadnessNotSurfaced_DeclaredBoundary()
    {
        var fixture = await _fixture.Value;
        var dead = fixture.Store.ResolveSymbolByFqn(
            QueryServiceFqn + ".GetForStoreAuditAsync", fixture.SnapshotId);

        // The declaration itself is indexed.
        Assert.NotNull(dead);

        // No call edge reaches it.
        Assert.DoesNotContain(
            fixture.Store.GetIncomingEdges(fixture.SnapshotId, dead.SymbolId.Value),
            e => e.Kind == EdgeKind.Calls.ToString());

        // The capsule shows it only as a declared member of the anchor type;
        // no tier marks it as uncalled.
        var capsule = await CapsuleForAsync(QueryServiceFqn);
        var appearances = AllItems(capsule)
            .Where(i => i.FullyQualifiedName.Contains("GetForStoreAuditAsync", StringComparison.Ordinal))
            .ToList();
        Assert.All(appearances,
            i => Assert.Equal(EdgeKind.Declares.ToString(), i.EdgeKind));
    }

    // ACCEPTANCE — Finding 6 (Low), derivable per the audit: the
    // RentalStateMachine capsule inlines the Cancel mutator with its body, and
    // the ReturnedAt write is readable straight off the capsule.
    [SkippableFact]
    public async Task Finding6_CancelWritesReturnedAt_DerivableFromCapsule()
    {
        var capsule = await CapsuleForAsync(StateMachineFqn);

        Assert.Contains(capsule.DirectCallees,
            i => i.FullyQualifiedName.Contains(".Cancel", StringComparison.Ordinal)
              && i.Source != null
              && i.Source.Contains("ReturnedAt =", StringComparison.Ordinal));
    }

    // ACCEPTANCE — Finding 7 (Low), derivable per the audit: both mutators are
    // inlined with bodies, so the asymmetry (Reject persists the note, Approve
    // drops it) is readable straight off the capsule.
    [SkippableFact]
    public async Task Finding7_NoteHandlingAsymmetry_DerivableFromCapsule()
    {
        var capsule = await CapsuleForAsync(StateMachineFqn);

        var reject = capsule.DirectCallees.FirstOrDefault(
            i => i.FullyQualifiedName.Contains(".Reject", StringComparison.Ordinal));
        Assert.NotNull(reject?.Source);
        Assert.Contains("RejectionNote = command.Note", reject.Source, StringComparison.Ordinal);

        var approve = capsule.DirectCallees.FirstOrDefault(
            i => i.FullyQualifiedName.Contains(".Approve", StringComparison.Ordinal));
        Assert.NotNull(approve?.Source);
        Assert.DoesNotContain("ApprovalNote =", approve.Source, StringComparison.Ordinal);
    }

    // ACCEPTANCE — Finding 1, member-anchor form. DI registration is a
    // type-level fact (AddHostedService<TPublisher>()), so a capsule anchored
    // on a *member* of the registered type must still surface the registration
    // in the registeredImplementations tier. Before this test the tier
    // consulted only the anchor's own symbol ids, so a member anchor saw the
    // registration only as an uncertainty — honest, but buried outside the
    // tier named after it.
    [SkippableFact]
    public async Task Finding1_MemberAnchor_SurfacesTypeLevelRegistration()
    {
        var capsule = await CapsuleForAsync(OutboxPublisherFqn + ".ExecuteAsync");

        Assert.DoesNotContain(capsule.OmittedTiers,
            entry => entry.Category == "registeredImplementations" && entry.Reason == "empty");

        Assert.Contains(capsule.RegisteredImplementations,
            i => i.FullyQualifiedName.Contains("AddApplicationServices", StringComparison.Ordinal)
              && i.EdgeKind == EdgeKind.Registers.ToString());
    }

    private async Task<ContextCapsule> CapsuleForAsync(string anchorFqn)
    {
        var fixture = await _fixture.Value;
        var anchor = fixture.Store.ResolveSymbolByFqn(anchorFqn, fixture.SnapshotId)
            ?? throw new InvalidOperationException($"Scoreboard anchor was not indexed: {anchorFqn}");

        return ContextAssembler.ResolveAndAssemble(
            fixture.Store,
            fixture.Store,
            new ContextLookup(fixture.SnapshotId, anchor.SymbolId.Value, null, null),
            new ContextAssemblyOptions(
                // The audit ran --intent=diagnose over these anchors.
                ContextIntent.Diagnose,
                // Generous enough to hold each capsule untrimmed: the
                // scoreboard measures tier content, not budget truncation.
                Budget: 500_000),
            fixture.Store);
    }

    private async Task<IndexedFixture> IndexFixtureAsync()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run the capsule audit scoreboard.");

        Directory.CreateDirectory(_outputDir);
        var solutionPath = IntegrationHarness.CopyNamedFixtureToTemp(
            Path.Combine(_outputDir, "fixture"), "CapsuleAudit");
        var dbPath = Path.Combine(_outputDir, "index.db");
        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, _outputDir);
        return new IndexedFixture(snapshotId, IntegrationHarness.OpenReadStore(dbPath));
    }

    private static IEnumerable<CapsuleItem> AllItems(ContextCapsule capsule)
        => capsule.Contracts
            .Concat(capsule.DirectCallees)
            .Concat(capsule.DirectCallers)
            .Concat(capsule.RegisteredImplementations)
            .Concat(capsule.RelevantTests)
            .Concat(capsule.SecondDegreeContext)
            .Concat(capsule.SurroundingSource);

    public void Dispose()
    {
        if (_fixture.IsValueCreated && _fixture.Value is { IsCompletedSuccessfully: true } task)
            task.Result.Store.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_outputDir))
        {
            try { Directory.Delete(_outputDir, recursive: true); }
            catch { }
        }
    }
}
