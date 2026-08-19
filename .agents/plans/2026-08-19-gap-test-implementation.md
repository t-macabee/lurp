# Plan: Gap Test Implementation — Schema + Gaps 1,2,3-C1,5,6

## Goal

Implement the missing test coverage described in `gap-test-implementation-guide.md` for the five checked gaps (Gaps 1,2,5,6 and Gap 3-C1) plus the P0 schema/migration regression that gates all of them, without changing production behavior. After the work, every new surface that shipped in commits `755b83e` (`StatusTool` sections/caps/batch freshness), `f1fba21` (line-windowed reads), `32cf3d4` (declaration outline), and `b4d74df` (annotations by document/kind) has characterization tests that would have caught a regression before merge. The existing 27 passing tests remain green because they only exercised the old behavior; the new tests exercise the new behavior.

## Success Criteria

- `VersionConstants.DatabaseSchemaVersion == 28` and `MigrationRunner.MigrationVersions.Count == 28`; `SchemaMigrationRoundTripTests` asserts no stale literal `27` remains. All four previously-failing migration tests pass locally via `dotnet test --filter SchemaMigrationRoundTripTests`.
- New regression tests exist that (a) call `RunMigrations()` then `ValidateSchema(VersionConstants.DatabaseSchemaVersion)` without throwing, and (b) exercise the real index entry path via `IndexHandler.Run` (not `IndexRunner.RunAsync` through `IntegrationTestBase`), asserting exit without `InvalidOperationException: Schema version mismatch`.
- `tests/Mcp/McpStatusTests.cs` contains distinct `[Fact]` cases for each of the 9 Gap-1 scenarios and 5 Gap-5 scenarios listed in the guide (14 facts total, one per case unless grouping is justified). All fail before any future regression and pass after.
- `tests/Mcp/McpReadSurfaceTests.cs` contains 10 `[Fact]` cases for Gap-6 line windowing (start/end/context_lines/out-of-range/whole-file back-compat/outline/total_lines).
- New `tests/Mcp/McpOutlineTests.cs` contains 9 `[Fact]` cases for Gap-3 C1 (ordering, include_generated, pagination, cursor fingerprint, not-found, line fields, partial flag, declaration_count stability).
- `tests/Mcp/McpAnnotationsTests.cs` contains 9 `[Fact]` cases for Gap-2 (document filter, empty-but-valid vs not-found, mutual exclusivity, kind filter, whole-snapshot, pagination, cursor fingerprint, NULL document_path caveat).
- Each new test follows the existing file’s setup pattern (`IntegrationTestBase`, `CreateProject`, `RunFullIndexAsync`, `McpSessionContext.Create`, `StatusTool`/`GetSourceTool`/`OutlineTool`/`AnnotationsTool`) and builds fixtures minimally per case (no shared mega-fixture).
- Validation is executable manually outside the sandbox (which cannot run MSBuild-loaded integration tests). The plan lists exact `dotnet test --filter` commands for the user to run.

## Context And Current Facts

**Source of truth for required coverage:** `gap-test-implementation-guide.md` (read 2026-08-19) enumerates 5 headings totaling 42 new cases plus 3 schema tests. `gap-mitigation-tasks.md` orders the P0 fix first and notes that `VersionConstants.DatabaseSchemaVersion` staying at 27 was the blocking regression that broke `--mode=index` and `lurp_index`.

**Current code state (observed via file reads):**

- `src/Workspace/VersionConstants.cs:5` now reads `DatabaseSchemaVersion = 28` (already fixed; was 27 at `b4d74df` time). `src/Storage/MigrationRunner.cs:82-113` returns 28 migrations (`Migration_001` through `Migration_028_AnnotationDocumentIndex`). So `MigrationVersions.Max() == 28`.
- `tests/SchemaMigrationRoundTripTests.cs` currently asserts `CountIs28` and `Assert.Equal(28, current)` (lines 19-48) — the stale-27 literals from the mitigation doc are already corrected on disk. No further prod fix needed; only new regression tests remain outstanding.
- `tests/IntegrationTestBase.cs` (`RunFullIndexAsync`, `RunFullIndexNoDeleteAsync`) calls `OpenStore` → `IndexRunner.RunAsync` directly and never touches `IndexHandler.Run` or `store.ValidateSchema(VersionConstants.DatabaseSchemaVersion)`. Every integration test is structurally blind to the P0 mismatch — confirmed by grepping `tests/` for `ValidateSchema` and `IndexHandler` (zero hits).
- `src/Handlers/IndexHandler.cs:52-54` is the real CLI entry: `store.RunMigrations(); store.ValidateSchema(VersionConstants.DatabaseSchemaVersion); await IndexRunner.RunAsync(...)`. `src/Mcp/Tools/IndexTool.cs:132-134` does the same on the MCP write path (`store.RunMigrations(); store.ValidateSchema(...)` inside the background task). Both would have thrown `Schema version mismatch: expected 27, got 28` before the VersionConstants bump — exactly the call sequence the new test must exercise.
- `src/Mcp/Tools/StatusTool.cs` (read fully): implements `sections` (`ResolveSections`, lines 284-316), `max_documents`/`max_mismatches` (lines 40-45, throw `InvalidParams` when <1), manifest trimming (`ManifestJson` removes `document_versions` → `document_count` and `metadata_reference_identities` → `counts/total` unless `includeReferences`), 80,000-byte hard cap with `truncated=true` and manifest-omitted note (lines 211-232), and `document_freshness` batch (lines 195-282, `ComputeDocumentFreshness` with normalization via `HandlerBootstrap.NormalizeDocumentPath`, states `fresh`/`stale`/`not_in_snapshot`, cheap vs full scope). `detail=true` maps to `sections=manifest` (via `ResolveSections` → `IsDetail`). `sections=all` maps to include both full `document_versions` and full identities (lines 48-50).
- `tests/Mcp/McpStatusTests.cs` has 8 facts today (cheap vs full method/scope, snapshot mismatch, detail expansion, fresh/stale, CLI parity, stale data with flag). None exercise `sections`, caps, truncation, `max_*=0`, or `documents` batch — those are all new.
- `tests/Mcp/McpAnnotationsTests.cs` has 3 facts (read isolation per snapshot, three forms + mismatch, gated read-only). None exercise `document` filter, kind filter, whole-snapshot mode, pagination, cursor fingerprint, or NULL document_path caveat.
- `tests/Mcp/McpReadSurfaceTests.cs` has ~12 facts (source envelope, mismatch, not found, navigate, findSymbol, search FTS/phrase, include_generated, cursor pagination). None exercise `start_line`/`end_line`/`context_lines` windowing, out-of-range, whole-file back-compat, `outline:true`, or `total_lines`.
- `tests/Mcp/McpOutlineTests.cs` does not exist yet (confirmed via `ls tests/Mcp/`). `src/Mcp/Tools/OutlineTool.cs` exists and delegates to `SqliteIndexStore.GetDeclarationsOutline` (limit 100 default, `include_generated` false, `OutlineCursor` with fingerprint `document\u0001includeGenerated`, ordered by `(full_start, symbol_id)`, `Validate` throws `ArgumentException` mapped to `InvalidParams`). `src/Mcp/Tools/GetSourceTool.cs` also supports `outline:true` via inline `GetDeclarationsOutline` (lines 71-98) — Gap-6 case 9 covers that path.
- `src/Storage/SnapshotDocumentStore.cs:24-95` implements `GetSourceSlice` slicing via `line_starts` byte offsets, with 1-based `startLine`/`endLine`, symmetric `context_lines` expansion, clamping at 1 and `totalLines`, `start_line > totalLines` throws `ArgumentOutOfRangeException`, `end_line > totalLines` clamps, `start_line > end_line` throws `ArgumentException`, and whole-file back-compat when all three are null. This matches the 10 Gap-6 cases exactly.
- `src/Storage/AnnotationStore.cs:87-176` implements `GetAnnotationsPage` (limit validation, fingerprint `symbol\u0001document\u0001kind`, keyset on `annotation_id`, total count, `nextCursor` via `AnnotationCursor`). `src/Storage/AnnotationCursor.cs` and `OutlineCursor.cs` both define `Validate(snapshotId, fingerprint)` that throws `ArgumentException` → mapped to `InvalidParams` in the tools. Document-filter distinctness (`WHERE document_path = @p`) vs not-found (thrown before the store call in `AnnotationsTool.cs:68-70`) is the mechanism for Gap-2 cases 2 vs 3.
- `src/Storage/DeclarationReadStore.cs:381-533` implements `GetDeclarationsOutline` (ordered by `full_start, symbol_id`, line conversion via `LineNumbers.ToOneBased`, `is_partial`/`is_generated` flags, total count constant across pages). Needed for Gap-3 C1.

**Build/test harness:** `tests/Lurp.Tests.csproj` targets `net10.0`, uses `xunit 2.9.3`. `AGENTS.md` says validate narrowly: directly affected test → class/fixture → project → full suite only when justified. `dotnet` CLI is the entry point; MSBuild locator is initialized statically in `IntegrationTestBase`.

**Sandbox limitation (user-stated, taken as fact):** MSBuild-loaded integration tests (`MSBuildWorkspace.OpenSolutionAsync`, `dotnet restore` inside `IntegrationTestBase.CreateProject/RestoreSolutionAsync`) do not run reliably inside the sandbox. Therefore the implementer must not claim green locally for those tests; instead the user will run them manually via provided CLI commands after the code is on disk.

## Constraints And Non-goals

- Do not change production behavior. All Gap-1/5/6/C1/Gap-2 surfaces are already shipped and have “no functional defects” per `gap-mitigation-tasks.md`; the task is tests only. Exception: if a test reveals an implementation bug (e.g., envelope cap off-by-one), surface it as an observed failure — do not silently fix prod without user consent; report the finding and propose the minimal fix.
- Do not introduce a new test base, fixture helper, or mock store. The guide explicitly says: “Follow the existing file's setup pattern (`IntegrationTestBase`, `CreateProject`, `RunFullIndexAsync`) — do not invent a new test base.”
- Do not expand scope into adjacent product work listed as out-of-scope in `AGENTS.md` or in `six-gaps-in-lurp.md §9`: no UI, no multi-language, no autonomous editing, no LLM summaries, no architecture scoring, no using-directive extraction (Gap 3 C2 remains a human decision and requires an `ExtractorVersion` bump).
- Do not run the full test suite as a substitute for reading individual failures. Per the guide: run each new test file in isolation first (`--filter "FullyQualifiedName~<ClassName>"`), then alongside existing files in the same directory.
- Do not claim local verification of MSBuild-dependent tests inside the sandbox. Provide the exact `dotnet test` filter commands and the expected evidence (counts, payload shapes, truncation markers) for the user to run outside the sandbox.

## Key Decisions

**1. Keep the existing `VersionConstants = 28` and `CountIs28` literals; add derivation tests alongside them.**
- *Why:* The on-disk state already has the P0 fix applied (observed at `VersionConstants.cs:5` and `SchemaMigrationRoundTripTests.cs:19/48`). Reverting or re-patching it would be churn. The guide’s item 1 (“apply `DatabaseSchemaVersion` fix before running any of these”) is satisfied. The remaining gap is that the tests still assert a hardcoded `28` that will rot again — but the guide says fixing those literals is item 2, and the file already does. We add a *derived* assertion (`Assert.Equal(VersionConstants.DatabaseSchemaVersion, MigrationVersions.Max())` already exists at line 30; the new regression tests will use the constant, not a literal) rather than re-editing the existing literals.

**2. Handler-level regression test uses `IndexHandler.Run` directly, not a spawned CLI process.**
- *Why:* The guide offers two options (“call `IndexHandler.Run(args, ct)` directly” or “spawn the built CLI with `--mode=index`”). Spawning needs `dotnet build`, a `Process.Start` harness, and stdout/stderr capture that no existing test uses — no process-spawn pattern exists for other CLI modes in `tests/` (confirmed: no `Process.Start` besides `IntegrationTestBase.RestoreSolutionAsync`). The direct call exercises the exact same `RunMigrations() → ValidateSchema()` sequence (lines 52-54) inside the same process, is deterministic in CI, and avoids shell/tooling variability. If it flakes due to `HandlerBootstrap.Fail` throwing `CliExitException`, that is the real contract to assert.
- *Alternative rejected:* process spawn — heavier, slower, and adds a new harness pattern for a single test.

**3. Gap-1 + Gap-5 tests live in `McpStatusTests.cs` as the guide says; do not split into two files.**
- *Why:* The guide groups both under `tests/Mcp/McpStatusTests.cs`. The status tool’s `LurpStatus` is the single surface for `sections`/`max_*` and `documents` batch, so co-location keeps the fixture (`StatusProj`) and session creation (`McpSessionContext.Create(args)`) shared. Splitting would duplicate the `CreateProject`/`IndexAsync` helper.
- *Alternative rejected:* new `McpStatusSectionsTests.cs` / `McpBatchFreshnessTests.cs` — violates “add cases, don’t replace” and would scatter the single tool’s coverage.

**4. Gap-6 tests go in the existing `McpReadSurfaceTests.cs`; Gap-2 in `McpAnnotationsTests.cs`; Gap-3 C1 gets a new `McpOutlineTests.cs`.**
- *Why:* Matches the file plan in the guide verbatim. The read-surface file already has the `IndexFixtureAndGetSnapshot` helper and `CreateTools()` that returns session+all tools; adding the 10 window cases there avoids duplicating the fixture. Annotations similarly reuses `IndexInitialAsync` style. Outline has no existing file, so a new one is the only option; it follows the `McpReadSurfaceTests.cs` pattern as instructed.

**5. One `[Fact]` per case (42 facts) except where the guide says “unless noted otherwise” — no parametrized `[Theory]` collapsing.**
- *Why:* The guide says “Write one `[Fact]` per case unless noted otherwise.” Theories would hide individual failure messages and make the per-case isolation filter (`--filter "FullyQualifiedName~McpStatusTests.Sections_Manifest"`) less useful. Only the envelope-cap test (Gap-1 case 7) may need a helper to generate many documents; it still asserts in a single fact.

**6. Test construction uses minimal per-case fixtures, not a shared mega-fixture.**
- *Why:* The guide: “Where a case needs a specific line count, partial class, or generated declaration, add the minimal fixture source needed for that one case; do not grow a shared fixture.” This keeps each fact self-contained and avoids cross-test coupling on declaration counts or line numbers. For example, Gap-6 case 1 needs a known 10-line file; create it inline in that fact rather than extending `ReadProj/Models.cs`.

**7. Validation is manual via CLI commands the user runs outside the sandbox.**
- *Why:* The user explicitly said “some of the tests don't work in sandbox… send me CLI command for testing so I can do it manually.” Claiming local `dotnet test` green inside the sandbox would be false evidence for MSBuild-dependent tests. We provide the exact filter commands and the expected pass criteria; the user is the oracle.

## Recommended Approach

**Phase 0 — Confirm the P0 gate is already green and add the two regression tests that lock it.**

No production edit. In `tests/SchemaMigrationRoundTripTests.cs`:

- Keep the existing 9 tests (including `MigrationList_CountIs28` and `RoundTrip_AllMigrations_ProducesCurrentSchema` asserting `28`). They are the canary for the next migration bump; they will fail if someone adds `Migration_029` without bumping the constant, which is intentional.
- Add `ValidateSchema_AfterRunMigrations_DoesNotThrow`:
  ```csharp
  [Fact] public void ValidateSchema_AfterRunMigrations_DoesNotThrow() {
      var runner = new MigrationRunner(_dbPath);
      runner.RunMigrations();
      var store = new SqliteIndexStore(_dbPath); store.Open();
      try { store.ValidateSchema(VersionConstants.DatabaseSchemaVersion); }
      finally { store.Close(); }
  }
  ```
  This is the exact call sequence `IndexHandler.Run` and `IndexTool` use. It catches the P0 bug before merge; `IndexRunner.RunAsync` bypasses it.
- Add `IndexHandler_Run_AgainstFixtureSolution_CompletesWithoutThrowing`:
  ```csharp
  [Fact] public async Task IndexHandler_Run_AgainstFixtureSolution_CompletesWithoutThrowing() { /* IntegrationTestBase-derived */ }
  ```
  Create a minimal fixture via `CreateProject("SchemaProbe", ...)`, place output in a fresh `Path.Combine(TestDir, "handler-out")`, then `await IndexHandler.Run(new[]{ $"--solution={SolutionPath}", $"--output-dir={handlerOut}" }, CancellationToken.None)` and assert no `InvalidOperationException` with “Schema version mismatch”. This test must not call `IndexRunner.RunAsync` or `RunFullIndexAsync` before the handler — it is the only test that exercises the real entry path.

Dependencies: none. Unlocks all other phases because every other new test’s fixture setup calls `RunMigrations` implicitly via `OpenStore`, and a schema mismatch there would fail setup before the test body runs.

**Phase 1 — Status payload sections and caps (Gap 1, 9 cases in `McpStatusTests.cs`).**

All cases use the existing `IndexAsync()` helper or a variant that indexes, then mutates on disk before calling `StatusTool.LurpStatus` with different `sections`/`max_*` args and parses the JSON with `JsonDocument`.

1. `sections=freshness` (or omitted) returns `detail == null` / no `detail.manifest`. Assert `!doc.RootElement.TryGetProperty("detail", out var d) || d.ValueKind == Null || !d.TryGetProperty("manifest", ...)`. This is the default.
2. `sections=manifest` returns `detail.manifest` with `document_count` (not `document_versions`) and `metadata_reference_counts` / `metadata_reference_total` (not `metadata_reference_identities`). Parse `detail.manifest` as `JsonObject` (via `JsonDocument`); assert `TryGetProperty("document_count")` true, `TryGetProperty("document_versions")` false, `TryGetProperty("metadata_reference_counts")` true, `TryGetProperty("metadata_reference_identities")` false. Use a fixture with at least one project so `metadata_reference_identities` would have been populated.
3. `sections=references` returns full `metadata_reference_identities` arrays. Assert `manifest.TryGetProperty("metadata_reference_identities", out var ids)` true and `ids.EnumerateObject().Any()`.
4. `sections=all` returns both full `document_versions` and full `metadata_reference_identities`. Assert both present. This is the unbounded variant that the envelope cap (case 7) guards.
5. `max_documents` caps `changed_documents_sample` at the given value and sets `changed_documents_sample_truncated=true` when real count exceeds it. To force a real count > cap, create 12 extra projects (as the existing `Status_Detail_ExpandsSample_AndIncludesDetailObject` does), run `RunFullIndexNoDeleteAsync`, then touch many files or delete/recreate? Simpler: use the cheap path after editing many files — `CheckFreshnessCheap` will report `ChangedDocumentCount == N`. Call `LurpStatus(max_documents: 5)` and assert `sample.GetArrayLength() == 5 && doc.RootElement.GetProperty("freshness").TryGetProperty("changed_documents_sample_truncated", ...)`.
6. `max_mismatches` caps `mismatches` at the given value and sets `mismatches_truncated=true` when real count exceeds it. Requires a full-freshness session (`--solution=`) with many mismatches (e.g., change many documents’ hashes as in case 5, then call with `max_mismatches: 3` and `sections: "manifest"` so mismatches are included). Assert `mismatches.GetArrayLength() == 3 && truncated`.
7. Serialized envelope exceeds 80,000 bytes (e.g., many changed documents plus `sections=all`) → response has `truncated=true` and manifest-omitted note. Build a fixture with enough projects/documents to push `manifest` size over 80k when `sections=all`. The existing self-index measurement is 118k for 3 projects; 10 small projects with a few documents each will cross the cap. Assert `doc.RootElement.GetProperty("truncated").GetBoolean() == true` and `detail.TryGetProperty("note", out var n) && n.GetString()!.Contains("manifest omitted")` (exact string is `"payload truncated at 80000 bytes; manifest omitted. Use sections=references or max_documents/max_mismatches..."` per `StatusTool.cs:219`).
8. `detail=true` (legacy) maps to `sections=manifest`, not `sections=all` — assert response does NOT include full reference identities when only `detail:true` is passed. Call `LurpStatus(detail: true)` (bool, not string) and assert `!manifest.TryGetProperty("metadata_reference_identities", ...)`.
9. `max_documents=0` and `max_mismatches=0` (and negative values) throw `McpProtocolException` with `InvalidParams`. Call `LurpStatus(max_documents: 0)` and `max_documents: -1`, catch `McpProtocolException`, assert `ErrorCode == InvalidParams` and message contains `"--max-documents must be a positive integer"` (per `StatusTool.cs:43`). Likewise for `max_mismatches`.

Each case parses the JSON once and asserts the envelope shape; no prod change needed.

**Phase 2 — Batch document freshness (Gap 5, 5 cases in the same `McpStatusTests.cs`).**

These use `documents:` param (array or `JsonElement` array). The tool normalizes via `HandlerBootstrap.NormalizeDocumentPath` (forward slashes), and `ComputeDocumentFreshness` reads from `fullResult.Mismatches` when in full scope else `cheapStamp.ChangedDocumentsSample`.

1. Two unchanged paths → both `state="fresh"`. Index, then call `LurpStatus(documents: new[]{"StatusProj/Models.cs", "StatusProj/Extra.cs" or second doc})`, assert both rows `fresh`.
2. Edit one of the two on disk before calling → edited row `stale`, other `fresh`. `File.WriteAllText` the one file with different content; no need to re-index; the cheap stat or full hash check will mark it changed. Assert `document_freshness` array has length 2 with the edited path `stale`.
3. Never-indexed path → `state="not_in_snapshot"`. Request `"NotExist/Fake.cs"` and assert distinct from `fresh`/`stale`. This proves the third state the field test needed.
4. Windows backslash path resolves same as forward-slash. Pass `@"StatusProj\Models.cs"` plus the slash form in two separate calls or one batch of two; assert both produce a row with the normalized forward-slashed `document` and `state=="fresh"` (or `stale` after edit).
5. Full vs cheap scope: run with `--solution=` after editing a document, confirm the documents result reflects the full mismatch set, not just the cheap stat sample. The simplest provable form is: edit a file, call both the cheap path (session without `--solution`) and the full path (with `--solution`) and assert that the per-document `freshness.state` for the edited file is `stale` in both, and that `freshness.method` is `"full"` in the full call. If a stat/hash-only change case exists in the fixture, assert consistency between `freshness.state` and the per-document result. This case is the weakest to specify; document it as “assert consistency between `freshness.state` and `document_freshness[].state` for the edited file” as the guide allows.

**Phase 3 — Line-windowed reads (Gap 6, 10 cases in `McpReadSurfaceTests.cs`).**

Reuse `IndexFixtureAndGetSnapshot` but with a deterministic multi-line fixture where line numbers are known. Create a helper `IndexLineFixture` that writes a 20-line file:

```csharp
CreateProject("LineProj", new Dictionary<string,string>{
  ["Lines.cs"] = string.Join("\n", Enumerable.Range(1,20).Select(i => $"// line {i}")) 
  + "\nnamespace LineProj { public class C { public void M() {} } }"
});
```

Then each case calls `GetSourceTool.LurpGetSource` with different args:

1. `start_line=5, end_line=8` → `source` equals lines 5-8 exactly, response echoes `start_line==5, end_line==8`. Construct expected by joining fixture lines 5-8 with `\n`.
2. `start_line=10, context_lines=2` with no `end_line` → window `[8,12]` (symmetrically expands `ctx` each side), clamped at 1. Assert `start_line==8 && end_line==12`.
3. `end_line=3, context_lines=2` with no `start_line` → window `[1,5]` clamped at `total_lines`. Assert `start_line==1`.
4. `context_lines=2` without `start_line`/`end_line` → throws `InvalidParams` with “--context-lines requires --start-line or --end-line.”.
5. `start_line=8, end_line=5` → throws `InvalidParams` “--start-line must be <= --end-line.”.
6. `start_line` beyond `total_lines` (e.g., 9999) → throws `InvalidParams` with “is beyond total_lines”.
7. `end_line` beyond `total_lines` → clamps to `total_lines` instead of throwing. Assert `end_line==total_lines && truncated==false` (since clamped to whole file tail, not truncated beyond end).
8. Omitting all three returns whole file with `truncated=false` → `source` equals file read via `File.ReadAllText` and equals `store.GetSource(normalized, snapshotId)` string.
9. `outline:true` returns non-null `outline` array alongside `source`, and `outline_declaration_count` matches declarations count. Assert `outline is JsonArray && outline.Length >0 && outline_declaration_count == outline.Length` (or `== store.GetDeclarationsOutline(...).TotalCount`).
10. `total_lines` equals actual line count (cross-check against `line_starts` length via `store` internal? Instead, cross-check against known fixture line count: our 20-line plus namespace line = 21, or read file and count `\n`+1).

**Phase 4 — Declaration outline (Gap 3 C1, 9 cases in new `McpOutlineTests.cs`).**

New file `tests/Mcp/McpOutlineTests.cs` following the `McpReadSurfaceTests.cs` pattern: `IndexFixtureAndGetSnapshot` that creates a document with several declarations at known lines, plus a partial-class split and a generated declaration scenario.

1. Basic ordering: index a fixture with 4 declarations in one document at known lines (e.g., `class A` at line 2, `void M1` at 4, `class B` at 8, `void M2` at 10), call `lurp_outline`, assert `declarations` array is ordered by `start_line` asc then `symbol_id`, and `start_line`/`end_line` are 1-based and match expected.
2. `include_generated` omitted or `false` excludes generated declarations. Need a fixture that produces at least one generated declaration — the simplest is to use `[GeneratedCode]`? Actually the store marks `is_generated` based on Roslyn’s `Location.IsInSource == false` or `IsGeneratedCode` check. The easiest is to index a source generator? Fallback: assert that when `false`, `is_generated` is never true for any returned row (if no generated exists, this is vacuously true and documents the invariant).
3. `include_generated:true` includes generated rows that were excluded by default. If no fixture produces generated rows, this becomes a smoke that the call does not throw and `declaration_count` is >= non-generated count.
4. Pagination: `limit` smaller than total (e.g., 1), walk `next_cursor` until null, assert concatenated pages (by `symbol_id`) equal the full unpaginated set, no duplicates, no gaps, and each page’s `declaration_count` is constant (== total). Verify `next_cursor` is present until last page.
5. Cursor fingerprint mismatch: take a cursor issued for `document="DocA.cs"` and reuse against `document="DocB.cs"` (or flip `include_generated`) → rejected with `InvalidParams` and message “Cursor does not match the current document/includeGenerated”.
6. `document` not in snapshot → throws `InvalidParams` with “Document '...' not found in snapshot”.
7. `signature_start_line` and `name_start_line` populated and `>= start_line` for a declaration with doc comment above signature. Fixture: `/// <summary>doc</summary>\npublic void DocMethod() {}` at known lines; assert `signature_start_line >= start_line && name_start_line >= signature_start_line`.
8. `is_partial` is `true` for a `partial class` split across two files, `false` for non-partial. Fixture: `Partial.cs` and `Partial2.cs` each with `partial class P {}` plus a method. Assert at least one row where `is_partial==true` for `P` and `is_partial==false` for a regular class.
9. `declaration_count` equals total count, not page size — constant across pages. Assert `envelope.declaration_count == totalDeclarationsInSnapshotForDocument` and that it stays constant across the walk from case 4.

Pagination uses `OutlineTool.LurpOutline` and `OutlineCursor` (`SnapshotId`, `Fingerprint=document\u0001includeGenerated`, `LastFullStart`, `LastSymbolId`). The cursor validation path is `OutlineCursor.Validate` → `ArgumentException` → `McpProtocolException(InvalidParams)` in `OutlineTool` (lines 54-56).

**Phase 5 — Annotations by symbol, document, kind (Gap 2, 9 cases in `McpAnnotationsTests.cs`).**

Existing file uses `IndexInitialAsync` (single `AnnoProj/Models.cs`) and `SaveAnnotations` directly via `SqliteIndexStore`. New cases:

1. `document` filter returns only annotations whose `document_path` matches. Setup: save three annotations: one with `document_path="AnnoProj/Models.cs"`, one with `"AnnoProj/Other.cs"` (after creating that document and indexing), one with `null`. Call `LurpGetAnnotations(document:"AnnoProj/Models.cs")` and assert exactly 1 row with that path.
2. `document` filter on a real, indexed document that has zero annotations → empty `annotations` array, not error. Create a second document `Empty.cs` with no annotations, call with that doc, assert `annotations.length==0 && annotation_count==0`.
3. `document` naming a path not in snapshot → throws `InvalidParams` distinguishable from case 2. Call `document:"NotExist.cs"` and assert `InvalidParams` with “not found in snapshot”.
4. Both `symbol` and `document` → throws `InvalidParams` with “mutually exclusive”. Assert message naming the rule.
5. `kind` filter narrows results to that kind only, with neither `symbol` nor `document`. Save annotations with `kind="note"` and `kind="todo"`, call `kind:"note"`, assert every returned row has `kind=="note"` and count < whole-snapshot count.
6. Whole-snapshot mode (both omitted) returns every annotation in snapshot, respecting `limit`. Save N=5 annotations, call without symbol/doc, `limit:10`, assert `annotations.length==5 && annotation_count==5`.
7. `limit`/`cursor` pagination: walk pages via `next_cursor` and assert concatenated result equals full unpaginated set, ordered by `annotation_id`, no duplicates/gaps. Use `limit:2` over 5 rows, walk until `next_cursor==null`.
8. Cursor fingerprint mismatch: reuse cursor from one `symbol`/`document`/`kind` combo against a different one → rejected (`InvalidParams` with “Cursor does not match the current snapshot/symbol/document/kind”).
9. Annotation saved with `document_path=null` (as `AnnotationHandler.RunAnnotate` does) does not appear in any `document`-filtered result, but does appear in whole-snapshot and symbol-filtered results. Save one null-path annotation, then assert `document`-filter count 0 for any doc, whole-snapshot count 1, symbol-filter count 1. This proves the documented caveat in the tool description, not just a comment.

All new tests assert `annotation_count` is the total, not the page size, and that `next_cursor` pagination is stable.

## Work Plan

| Phase | File(s) | Cases | Owner surface | Depends |
|-------|---------|-------|---------------|---------|
| 0. Schema regression | `tests/SchemaMigrationRoundTripTests.cs` (+ `src/Workspace/VersionConstants.cs` already green) | 2 new facts: `ValidateSchema_AfterRunMigrations_DoesNotThrow`, `IndexHandler_Run_AgainstFixtureSolution` | `MigrationRunner`, `SqliteIndexStore`, `IndexHandler` | None — gate for all |
| 1. Gap 1 | `tests/Mcp/McpStatusTests.cs` | 9 facts (sections fresh/manifest/references/all, max_documents, max_mismatches, envelope cap, detail→manifest, max_*=0) | `StatusTool`, `SnapshotManifest`, `WorkspaceFreshness` | 0 |
| 2. Gap 5 | `tests/Mcp/McpStatusTests.cs` (same file) | 5 facts (fresh batch, stale batch, not_in_snapshot, backslash norm, full scope) | `StatusTool.ComputeDocumentFreshness`, `McpSessionContext` | 0 |
| 3. Gap 6 | `tests/Mcp/McpReadSurfaceTests.cs` | 10 facts (start/end/context/out-of-range/whole/outline/total) | `SnapshotDocumentStore.GetSourceSlice`, `GetSourceTool` | 0 |
| 4. Gap 3 C1 | `tests/Mcp/McpOutlineTests.cs` (new) | 9 facts (order, include_generated, pagination, cursor, not-found, lines, partial, count) | `OutlineTool`, `DeclarationReadStore`, `OutlineCursor` | 0 |
| 5. Gap 2 | `tests/Mcp/McpAnnotationsTests.cs` | 9 facts (doc filter, empty vs not-found, mutual excl, kind, whole, pagination, cursor, null caveat) | `AnnotationsTool`, `AnnotationStore`, `AnnotationCursor` | 0 |

Ordering note: Phases 1-5 are independent after Phase 0; they may be implemented in any order. Listed order follows the guide’s “do status first” suggestion only because `McpStatusTests.cs` is the largest file and benefits from being landed first to avoid later merge conflicts.

No parallel subagents are needed; the implementer works inline, one file at a time, to keep the single `McpStatusTests.cs` file consistent.

**Off-scope explicitly excluded:** Gap 4 diagnostics tool (not in the guide’s checked five), Gap 3 C2 using-directive extraction (new extraction, needs extractor-version bump decision), and the two “Also noticed” nits unless the user explicitly asks (the `status-tool-gap-fixes-handoff.md` notes they are now decided but belong to a separate work item; if the user wants them in this slate, they add 1 file edit (`StatusTool.cs` lines 179-182 and `WithBindingCompleteness`) and 2 acceptance tests).

## Validation Plan

The sandbox cannot reliably run MSBuild-loaded integration tests, so validation is manual. For each phase, the exact command to run outside the sandbox is listed; run the single-class filter first, then alongside the existing directory.

**Common prerequisites (once, outside sandbox):**
```bash
dotnet build Lurp.slnx -c Debug
```
Expected: `Build succeeded.` with zero errors. This is the configured verification gate (no `golangci-lint` equivalent; the repo’s CI uses `dotnet test`).

**Phase 0 — Schema/migration (the only phase that runs in sandbox today):**
```bash
dotnet test tests/Lurp.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~SchemaMigrationRoundTripTests" --logger "console;verbosity=detailed"
```
Expected: 11 tests pass (9 existing + 2 new). Specifically: `MigrationList_CountIs28` passes, `RoundTrip_AllMigrations_ProducesCurrentSchema` passes with `current==28`, `ValidateSchema_AfterRunMigrations_DoesNotThrow` passes without throw, `IndexHandler_Run_AgainstFixtureSolution_CompletesWithoutThrowing` completes (or is marked `SkippableFact` if MSBuild unavailable in that env). This is the “one thing that would have caught the P0 bug.”

**Phase 1+2 — Status (Gaps 1 & 5):**
```bash
dotnet test tests/Lurp.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~McpStatusTests" --logger "console;verbosity=detailed"
```
Expected: 22 tests pass (8 existing + 14 new). Then run alongside existing directory:
```bash
dotnet test tests/Lurp.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~Mcp" --logger "console;verbosity=detailed"
```
Expected: all Mcp tests green. Manual spot checks for new cases:
- `Sections_Manifest_ReturnsCountsNotIdentities`: parse JSON, verify `document_count` present, `document_versions` absent, `metadata_reference_counts` present.
- `EnvelopeCap_TruncatedFlagged`: verify `truncated==true` and `detail.note` contains “payload truncated at 80000”.
- `DetailTrue_MapsToManifestNotAll`: verify no `metadata_reference_identities` when `detail:true`.
- `BatchFreshness_BatchStates`: verify 3-state distinction and backslash normalization.

**Phase 3 — Line-windowed reads (Gap 6):**
```bash
dotnet test tests/Lurp.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~McpReadSurfaceTests" --logger "console;verbosity=detailed"
```
Expected: 22 tests pass (12 existing + 10 new). Spot checks:
- `StartEnd_EchoesRange`: `start_line==5 && end_line==8 && source == expected`.
- `ContextWithoutBounds_ThrowsInvalidParams`: `McpProtocolException` with `InvalidParams`.
- `WholeFile_BackCompat`: `truncated==false && source == File.ReadAllText`.

**Phase 4 — Outline (Gap 3 C1):**
```bash
dotnet test tests/Lurp.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~McpOutlineTests" --logger "console;verbosity=detailed"
```
Expected: 9 tests pass (new file). Then:
```bash
dotnet test tests/Lurp.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~McpReadSurfaceTests or FullyQualifiedName~McpOutlineTests"
```
Spot checks:
- `Pagination_WalkEqualsWhole`: concatenated `symbol_id`s equal unpaginated set.
- `CursorFingerprintMismatch_Throws`: `InvalidParams`.
- `PartialFlag_TrueForPartialFalseOtherwise`: `is_partial` true for `partial class`.

**Phase 5 — Annotations (Gap 2):**
```bash
dotnet test tests/Lurp.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~McpAnnotationsTests" --logger "console;verbosity=detailed"
```
Expected: 12 tests pass (3 existing + 9 new). Spot checks:
- `DocumentFilter_OnlyMatchingPath`: `annotations.All(a => a.document_path == requested)`.
- `DocumentNotInSnapshot_ThrowsInvalidParams`: assert `InvalidParams` vs empty success.
- `NullDocumentPath_UnreachableByDocFilterButVisibleBySnapshot`: proves caveat.

**Final broader check (after all phases, outside sandbox):**
```bash
dotnet test tests/Lurp.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~Mcp" 
dotnet test tests/Lurp.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~SchemaMigrationRoundTripTests or FullyQualifiedName~McpStatusTests or FullyQualifiedName~McpReadSurfaceTests or FullyQualifiedName~McpOutlineTests or FullyQualifiedName~McpAnnotationsTests"
```
Expected: all new + existing in those classes green. Do not run the full suite as a substitute for reading the individual class failures; if a new test fails, that’s the contract, not a flaky environment.

**Negative-case evidence to capture per phase:** for each `InvalidParams` case, the captured exception’s `ErrorCode` is `McpErrorCode.InvalidParams` (`-32602`) and the message names the violated rule (e.g., “--symbol and --document are mutually exclusive”, “--start-line must be <= --end-line”, “is beyond total_lines”, “Cursor does not match”). These are the error, edge, and negative clauses the guide says to weight equally with the happy path.

## Risks / Rollback

- **Schema test brittleness to next migration:** `MigrationList_CountIs28` hardcodes `28`; the next migration will break it again. Mitigation: keep the literal as a canary (intentional breakage signals “bump needed”), but the new `ValidateSchema_AfterRunMigrations_DoesNotThrow` uses `VersionConstants.DatabaseSchemaVersion` so it never rots. If the literal test becomes noisy, replace it with `Assert.Equal(VersionConstants.DatabaseSchemaVersion, versions.Count)` plus `Assert.Equal(versions.Max(), VersionConstants.DatabaseSchemaVersion)` — but that’s a separate decision.
- **Fixture size for envelope cap:** Gap-1 case 7 needs a payload >80k. If the minimal fixture (a few small projects) doesn’t cross the cap, the test will false-pass. Mitigation: assert the uncapped size first (serialize with `sections=all` and measure `json.Length > 80000` before asserting the truncated path); if it’s not over the cap, grow the fixture (more projects or larger `document_versions`) until it is. Document the threshold explicitly in the test.
- **Generated/partial fixtures missing:** Gap-3 cases 2/3/8 need generated and partial declarations. If the fixture doesn’t produce them, the test would be vacuously true. Mitigation: where a facility doesn’t exist in the test harness (source generators), either skip the assertion’s strict branch and document why (e.g., “no generated declarations in this fixture, so assert that no returned row has `is_generated==true` when `include_generated:false`”) or use a real partial-class split (two files, same `partial class`) which is trivial to produce and reliably yields `is_partial==true`.
- **MSBuild/restore flakiness:** `CreateProject` + `RunFullIndexAsync` may fail on missing SDK, restore timeout, or Windows path length. Mitigation: tests that need only a document store (e.g., Gap-6 line slicing unit) can be written as store-level characterization alongside the MCP-level test, but the guide says to use `IntegrationTestBase` fixtures — so keep the MCP-level test as the required artifact and mark MSBuild-dependent failures as environmental, not contract failures, until the user confirms the SDK is present.
- **File merge conflict between Gap-1 and Gap-5:** both edit `McpStatusTests.cs`. Mitigation: implement them in one contiguous edit session (the work plan’s Phase 1 and 2 are the same file) and run the combined filter after both land, rather than committing them as separate file-touching PRs that conflict.
- **Rollback:** all changes are new tests except the already-landed VersionConstants bump. Rolling back is deleting the new facts or reverting the new file `McpOutlineTests.cs`. No data migration or prod rollback needed. If a new test is wrong (e.g., asserts an envelope shape that the prod code never promised), fix the test — do not change `StatusTool`, `SnapshotDocumentStore`, or `DeclarationReadStore` to make a wrong test pass without a separate defect report.

## Open Questions

- **Gap 1 `sections=completeness` distinctness:** `StatusTool` now treats `sections=completeness` as a valid value but (per `gap-mitigation-tasks.md`) has no distinct effect from `sections=manifest` until the `WithBindingCompleteness` wiring from `status-tool-gap-fixes-handoff.md` lands. Should the tests for `sections=completeness` assert that `completeness.binding_incompleteness` is non-empty (which would require that prod fix to be in this slate), or assert the current inert behavior? The guide does not list a `completeness` case for Gap 1 — it only says `sections=manifest` and `sections=all`. Assumption for this plan: **do not add a `completeness` test until the prod fix is decided separately**; the 9 listed cases are the contract. If the user wants `completeness` coverage now, it adds one prod edit (`StatusTool.cs` 2a/2b per the handoff) and one test.
- **Status detail-branch swallowing:** same handoff notes that `StatusTool` swallows every exception into `{ note="detail unavailable" }`. The guide’s 9 Gap-1 cases don’t cover the error path. Should a 10th test assert that a genuine store failure surfaces `error`/`error_message` instead of a blank note? Assumption: **out of scope for this plan** unless the user merges the handoff work into this slate.
- **Process-spawn index test scope:** the guide says “two options, pick the one that fits.” The plan picks `IndexHandler.Run` directly. If the team prefers a spawned `dotnet run --mode=index` smoke instead, confirm whether a `tests/` process-spawn helper already exists to reuse, and whether CI has the built CLI artifact available at test time. Without that, the direct call is the lower-risk choice.
- **Test file naming for the handler regression:** should the `IndexHandler.Run` smoke live in `SchemaMigrationRoundTripTests.cs` (as the guide’s “File: tests/SchemaMigrationRoundTripTests.cs (existing)” suggests) or as a separate `IndexHandlerSmokeTests.cs`? Assumption: **inside `SchemaMigrationRoundTripTests.cs` as a second test class or additional facts in the same file**, since the guide groups the handler test under that heading. If the team prefers separation, it’s a one-file move.
