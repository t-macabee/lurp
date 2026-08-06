# Capsule audit scoreboard fixture

This fixture implements the capsule audit regression scoreboard:
it freezes the seven eNoteV2 audit findings (the headline table in
`lurp_audit.txt`) as an in-repo regression scoreboard. The eNoteV2 corpus is
external, so each finding's pattern is reproduced here with stand-in framework
types (`BackgroundService`, `DbContext`, `IServiceCollection`, …) that Lurp's
adapters match by short name — no external package dependencies.

## Finding → pattern map

| # | Finding | Sev | Reproduced by |
|---|---------|-----|---------------|
| 1 | Outbox registered in both hosts | Crit | `Infrastructure/RentalNotificationOutboxPublisher.cs` — `AddHostedService<T>` in a shared `AddApplicationServices()` extension called by both `Api/Program.cs` and `Worker/Program.cs` |
| 2 | Rethrow stops host | High | Same file — `: BackgroundService` override of `ExecuteAsync` with a rethrow (`BackgroundServiceExceptionBehavior.StopHost` contract) |
| 3 | Store reads fail open | High | `Infrastructure/ENoteContext.cs` — `HasQueryFilter` on `InstrumentRental`; `Infrastructure/RentalQueryService.cs` — `CurrentActor.StoreId` always resolves; corroborating `TenantIsolationTests` |
| 4 | Unreachable `GuardInstrumentActive` | Med | Same filter plus `HasIndex(...).IsUnique().HasDatabaseName("UX_InstrumentRental_InstrumentId_ActiveOrApproved")`, string-matched by `InstrumentRentalRepository.SaveWithLockConflictMessageAsync` |
| 5 | `ForStoreAudit` dead | Low | `RentalQueryService.GetForStoreAuditAsync` — declared, never called |
| 6 | Cancel writes `ReturnedAt` | Low | `Infrastructure/RentalStateMachine.cs` — `Cancel` mutator |
| 7 | Note-handling asymmetry | Low | Same file — `Reject` records `RejectionNote`, `Approve` drops the note |

## Runner

`tests/CapsuleAuditScoreboardTests.cs` indexes a temporary copy of this fixture
(full strategy), assembles a `--intent=diagnose` capsule per anchor, and
asserts one scoreboard entry per finding:

- Findings 1, 2, 3, 4, 6, 7 are **acceptance** tests (intended behavior today;
  findings 1, 2, 3, 4 were flipped when Tasks #1, #4, and #5 landed).
- Finding 5 is a **characterization** test of the current boundary
  (accepted declared boundary; dead code has no call edges). It is
  labelled as such in the test source.

Run:

```powershell
dotnet test tests/Lurp.Tests.csproj --filter "FullyQualifiedName~CapsuleAuditScoreboardTests" --verbosity normal
```
