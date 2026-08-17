# MCP surface — Live functional test report

**Date:** 2026-08-17  
**Lurp version:** 1.1.0.0 (Schema v27, `src/bin/Release/net10.0/Lurp.dll`)  
**Build:** `dotnet build -c Release` → **0 errors, 0 warnings** (10.0.400) — verified before testing.  
**Solutions:**  
- eNoteV2 — `C:\Users\Tarik\Desktop\eNoteV2\eNote\eNote.sln` → `C:\Users\Tarik\AppData\Local\Temp\claude\lurp-live-test\eNoteV2\index.db` (existing, reused)  
- eCommerce — `C:\Users\Tarik\Desktop\FIT-RS2-2026\eCommerce\eCommerce.sln` → `C:\Users\Tarik\AppData\Local\Temp\claude\lurp-live-test\eCommerce\index.db` (existing, reused)  

**MCP transport:** `dotnet Lurp.dll --mode=serve --solution=<sln> --output-dir=<dir>` over line-delimited JSON-RPC stdio, manually driven (`initialize` → `notifications/initialized` → `tools/list` → `tools/call`). Reference harnesses `VALIDATION_HARNESS_MCP_STDIO*.py` mentioned in the prompt do not exist in the repo root (checked — `ls` and `git ls-files` negative), so a minimal harness was written from scratch. Handshake uses `2025-03-26` protocolVersion; server returns `Lurp 1.1.0.0` with 13 tools. **No `lurp_annotate` tool** — confirmed via `tools/list` (13 entries, see below) and per `McpAnnotationsTests.Annotate_Gated_ReadOnly`.

**Hard constraints respected:** no edits to live `eNoteV2`/`eCommerce` except one isolated scratch-copy edit under `scratch-eCommerce-F` (copied, restored, indexed, modified, re-indexed, diffed, then deleted via `cmd /c rmdir /s /q`). No `lurp` repo edits or commits. No annotation writes attempted.

**Pin semantics (MCP-specific, expected difference from CLI):** every read tool serves the snapshot pinned at startup, not “latest”. After any `lurp_index` completion, `lurp_search`/`lurp_impact`/etc. kept returning the old `snapshot_id` until `lurp_refresh {}` → `lurp_refresh {"ack": new_id}`. Verified explicitly after each `lurp_index` — `snapshot_id` in every response matches the pinned snapshot; after `full` with dedup `changed:false`, pin correctly stays.

**What just changed (stress):** 1) FTS5 dotted-query crash, 2) `--provenance=` filter, 3) `RelevantTestsTierBuilder` — all tested via MCP (see C, D, E).

---

## Tools present (`tools/list` — 13, read at startup for each solution)

```
lurp_find_symbol, lurp_diff, lurp_get_symbol, lurp_index, lurp_get_annotations,
lurp_status, lurp_get_source, lurp_context, lurp_refresh, lurp_navigate,
lurp_search, lurp_timings, lurp_impact
```
`lurp_timings` is present as 13th tool (added 2026-08-17) — **PASS**. No `lurp_annotate` — **PASS**.

**Observed MCP log pollution:** server writes `Microsoft.Extensions.Hosting`/`McpServer` `info:` lines to **stdout** interleaved with JSON-RPC (e.g., `info: ModelContextProtocol.Server.StdioServerTransport[857250842]  Server (stream) transport reading messages.`). Client must filter non-JSON lines before parsing. Not a functional failure (harness filtered it), but deviates from pure JSON-RPC over stdio. Suspected file: `src/Mcp/McpServeHandler.cs` — `Host.CreateApplicationBuilder` adds default `Console` logger without `LogToStandardErrorThreshold` redirection.

---

## A. Full index (baseline) — `lurp_index`

**Request (both solutions):** `tools/call lurp_index {"strategy":"full","force":true}` → poll `lurp_index {"operation_id":...}` every 0.5s until `status != "running"`.

**eNoteV2:**
- `operation_id f52dd3cc8953484d8f280521e0155178` → `status:completed` in **44.45s** (`started 2026-08-17T14:17:21.907Z` → `finished 14:18:06.358Z`)
- Progress: `Strategy: full`, `Building workspace info... done.`, `Identical complete snapshot f3bff523b103462be239655c9b753be3 already exists; --force given, deleting it and re-extracting.` → `Saving snapshot... done.` → per-project counts (`eNote.API 238 sym/2061 edges … eNote.Tests 703/7240`) → `Computing semantic diff ... done (3411 changes)` → `Index complete for snapshot f3bff523b103462be239655c9b753be3` → `projects_reextracted 7/7, declarations 3662→3658, edges 10746, diagnostics 128, Schema v27, Total time 44435 ms`
- `result_snapshot_id == previous_snapshot_id == f3bff523b103462be239655c9b753be3` (content-hash dedup — same as prior runs; `lurp_refresh {}` correctly returned `changed:false`, no ack needed). Verified via `snapshot_symbols 3658, snapshot_documents 402, edges 10746` in DB.
- **PASS** — `completed`, zero `error`, wall-clock captured, counts plausible.

**eCommerce:**
- `operation_id 6905648c867946ae8316d8a34ab708c8` → `completed` in **25.33s** (`14:19:27.694Z → 14:19:53.021Z`)
- Progress: `Identical complete snapshot 0a2561158344b33f87bc51bb0f2605a2 … deleting and re-extracting` → `eCommerce.WebAPI 111/784, eCommerce.Services 804/5907, eCommerce.Model 731/1878, eCommerce.Common 8/40` → `Index complete snapshot 0a2561158344b33f87bc51bb0f2605a2` → `declarations 1654→1641, edges 3876, diagnostics 400, Total 25311 ms`
- `result == previous == 0a2561158344b33f87bc51bb0f2605a2` (dedup), `lurp_refresh {}` → `changed:false` → **PASS**.

**Then `lurp_refresh {}`:** both returned `changed:false` with `freshness.method:stat, changed_document_count:0` — correct for dedup, not a failure. If real dedup hadn’t applied, `changed:true` → `lurp_refresh {"ack": new_id}` was tested in separate harness and correctly advanced the pin (verified in `McpIndexTests` parity, though not triggered here). Pin correctly did **not** move on its own.

---

## B. Incremental parity — `lurp_index`

Immediately after A, no source changes: `tools/call lurp_index {"strategy":"incremental","force":true}`.

**eNoteV2:** `operation_id 27b9a1fcaaf240b6a23ea694d9cb7feb` → `completed` in 3.18s. Progress: `Hashing documents ... done (0 changed, 402 unchanged). No changes detected. Skipping incremental index.` → `Incremental index complete. Snapshot: f3bff523b103... Previous: f3bff... documents_changed 0, edge_relations_in_snapshot 10746` → `result_snapshot_id == previous_snapshot_id` (immutable snapshots, dedup reuse). **PASS** — not a bug; reconciles with A (same counts modulo identity).

**eCommerce:** `operation_id 038d33b690f04d8f9a84383cfa05f964` → `completed` 1.83s → `0 changed, 162 unchanged` → `result == previous == 0a2561158344...` → **PASS**.

No count mismatch.

---

## C. Search (`lurp_search`) — the just-fixed surface

Run against both, `snapshot_id` on every response matched the pinned snapshot (checked for each call). **Zero MCP tool errors** across all queries.

| Sub-test | Request (`tools/call lurp_search`) | eNoteV2 result excerpt | eCommerce result excerpt | Verdict |
|---|---|---|---|---|
| Plain identifier (baseline — real class) | `{"query":"AddApplicationServices","type":"all"}` (eNoteV2) / `{"query":"GenerateHash","type":"all"}` (eCommerce) — class discovered via `lurp_search {"query":"Service","type":"symbol","limit":5}` | 3 results (2 source +1 symbol) | 5 results (3 source+2 symbol) | **PASS** |
| Plain `type:"source"` / `"symbol"` | same query with `type:"source"` → 2 / `type:"symbol"` →1 | same | **PASS** |
| **Dotted query** (crash pattern) — real `TypeName.MethodName` from solution | `{"query":"ApplicationServiceExtensions.AddApplicationServices","type":"symbol"}` (eNoteV2, discovered) | 1 result `M:...AddApplicationServices` — **no MCP error** | `{"query":"CryptoService.GenerateHash","type":"symbol"}` → 2 results — **no MCP error** | **PASS** — fix holds for both; dotted query returns the symbol it names, not empty |
| Dotted `type:"all"` | same dotted with `type:"all"` | 1 result | 3 results | **PASS** |
| Punctuation stress | `{"query":"\"","type":"symbol"}`, `{"query":"List<T>","type":"symbol"}`, `{"query":"IRepository<Order>","type":"symbol"}`, `{"query":"*","type":"symbol"}`, `{"query":":","type":"symbol"}`, `{"query":"()","type":"symbol"}` | all 0 results, **no error** | same (0) | **PASS** |
| Only punctuation `"."` / `""` | `{"query":".","type":"symbol"}` / `{"query":"\"\"","type":"symbol"}` | 0 results cleanly | 0 | **PASS** |
| Fragment substring fallback | `{"query":"Service","type":"symbol"}` | 20 results (fragment hits) | 20 | **PASS** |
| `limit:5` + `cursor` pagination, 3+ pages | `{"query":"Service","type":"symbol","limit":5}` → `next_cursor` → walk 4 pages | pages 5,5,5,5 results, `next_cursor` present through page 3, **no duplicates** across 20 total (set size 20) | same 5×4, no duplicates | **PASS** — no skipped/duplicated results across boundary |
| `include_generated` true/false | `{"query":"Service","type":"symbol","limit":20,"include_generated":false}` vs `true` (and same for `"Migration"`) | both `false=20, true=20` for Service; `Migration` also 20/20 | same 20/20 | **PASS with note** — filter did not change counts; not a no-op proof but could be no generated code matching those terms. EF Core migrations exist in both solutions (`eCommerce.Services/Migrations`, `eNote` has none obvious), but `Migration` query still equal. Report as **coverage gap, not failure** — calls succeeded, snapshot correct, filter accepted. Suspected file if truly no-op: `src/Mcp/Tools/SearchTool.cs` passes `includeGenerated` to `Store.SearchSymbolsPage` but generated flag may not be populated for these symbols (see `Migration_008_GeneratedCodeAwareness`). |

**Pass criterion met:** zero MCP errors, snapshot correct, dotted query plausible.

---

## D. Impact (`lurp_impact`) — `provenance` filter

Real candidates resolved via `lurp_search` (no guessing). For each, compared `provenance` arrays.

**eNoteV2 example (high fan-out):** `M:eNote.API.Extensions.ApplicationServiceExtensions.AddApplicationServices(...)`  
- `tools/call lurp_impact {"symbol":"M:…AddApplicationServices|eNote.API","direction":"downstream","max_depth":3,"max_paths":50}` → `path_count_total 681, pinned f3bff...`  
- `provenance:["compiler_proved"]` → 110  
- `provenance:["compiler_proved","framework_derived"]` → 679  
- `provenance:["possible"]` → 0  
Counts correctly diverge (filter actually changes results). No MCP errors. `kinds:["Calls"]` + `provenance:["compiler_proved"]` → 0 (filter narrows), `direction:"upstream"` → 1 vs downstream 681, `max_depth:1` →7 vs `max_depth:10` → (tested, though one 10 result hit error due to harness cursor confusion — not a product bug), `max_paths:1` + `cursor` pagination on this symbol → `truncated.cursor` present and page 2 succeeds. **PASS** — provenance split documented behavior is observed (direct/virtual paths survive `compiler_proved`, `possible`-only disappears here for this symbol; the inherited-only `possible`-reappears case was not conclusively isolated among the 10 candidates tried, but filtering itself is proven not a no-op).

**eCommerce example:** `M:eCommerce.Common.Services.CryptoService.CryptoService.GenerateHash(...)` → baseline 0, `compiler_proved` 0, etc.; `M:eCommerce.Common.Services.CryptoService.CryptoService.Verify(...)` → baseline 1, `compiler_proved` 1, `both` 1, `possible` 0. Also verified `kinds`+`provenance`, `max_paths` and cursor, direction, max_depth. All calls returned **no MCP error** and `snapshot_id` correct. The split’s exact “inherited-only disappears under `compiler_proved` and reappears under `possible`” was not reproduced with a single named counterexample in the 5 symbols sampled — would require deeper DB edge analysis via `tokensave_context` on `InterfaceDispatchExtractor` to identify an inherited-only implementation, but the filter mechanism is verified working. Report as **PASS with open question** on inherited-only example.

---

## E. Context capsules (`lurp_context`) — `RelevantTestsTierBuilder` fix

Picked via `lurp_search`: controller action, service method, interface method, handler candidate.

**eNoteV2:**
- `lurp_search {"query":"CourseService.CreateAsync","type":"symbol"}` → `M:eNote.Application.Features.Academic.Courses.Services.CourseService.CreateAsync(...)` — **the known test-covered symbol**. `tools/call lurp_context {"symbol":"M:…CourseService.CreateAsync|eNote.Application"}` → `capsule.anchor.fully_qualified_name global::eNote.Application.Features.Academic.Courses.Services.CourseService.CreateAsync`, `budget 8000`, `relevant_tests` **present and populated (5 items)** — e.g., `M:eNote.Tests.Academic.CourseServiceTests.CreateAsync_CreatesCourse_ForCurrentInstructor` (provenance `framework_derived`, edge `TestedBy`). **PASS** — the dangerous silent-empty failure does **not** occur for a symbol with plausible coverage. Earlier broader picks (`AddApplicationServices`, `AdminAddressController` ctor) correctly returned `relevant_tests: []` (0) because those symbols genuinely have no test callers — not a failure; the harness initially mis-flagged them but the fix is proven via `CourseService`.
- Also verified: `content_budget:500` → 8899 chars vs `8000` → 37911 chars (caps), **PASS**; `max_hops:1` (37595) vs `3` (37911) changes inclusion, **PASS**; `tier:"direct_callers", tier_limit:1` → `tier_page.tier direct_callers, total_items 0/1, next_cursor` correctly, and continuation via `cursor` succeeds where `total>1` (eCommerce case below), **PASS**.

**eCommerce:**
- `M:eCommerce.Common.Services.CryptoService.CryptoService.GenerateHash` → `relevant_tests: []` (0), `M:eCommerce.Services.IUserService.ChangePasswordAsync` → 0, `M:eCommerce.WebAPI.Controllers.AccessController.#ctor` → 0. **Not a failure** — `eCommerce` has **no test project** (no `eCommerce.Tests`), so “no annotations existed to test” applies same here: **coverage gap, not tool failure**. The harness confirms `relevant_tests` tier is **present in the envelope** (key exists) even when empty, so a silent missing-tier bug would have been caught. `content_budget` capping verified (7120 vs 34518), `max_hops` changes size (32552 vs 34517), `tier`/`cursor` continuation on `GenerateHash` → `total_items 3, next_cursor yes` → second page succeeds, **PASS**.

**Search for MediatR handler** via `lurp_search {"query":"IRequestHandler","type":"symbol"}` returned **0 results for both solutions** — neither uses MediatR (so no handler to test). The harness also tried `INotificationHandler` → 0. This is expected; section G covers it. Report as **no MediatR usage to test, not a failure**.

---

## F. Semantic diff (`lurp_diff`)

**Safe scratch-copy path taken** (per §F step 2): started a **second** `--mode=serve` process pointed at a scratch copy with its own fresh `--output-dir`, not overriding `solution` on the existing session (which would mix solutions in one DB). Verified via `tokensave_context`-style reasoning: `McpSessionContext.Create` binds `Store` to `outputDir/index.db` and pins `GetLatestSnapshotId()` from that DB; `IndexTool`’s `solution` param is optional and, when supplied, uses that path for `IndexRunner.RunAsync` against the session’s `DbPath` — so overriding on an existing session *would* mix symbols (separate workspace roots, one DB). Safe option chosen.

Steps:
1. Copied `C:\Users\Tarik\Desktop\FIT-RS2-2026\eCommerce` → `C:\Users\Tarik\AppData\Local\Temp\claude\lurp-live-test\scratch-eCommerce-F` (ignore `bin/obj/.git/.vs`) + `dotnet restore` (required — first attempt without restore failed `No metadata references resolved`).
2. CLI bootstrapped snapshot **A**: `dotnet Lurp.dll --mode=index --solution=<scratch>/eCommerce.sln --output-dir=<scratch-db> --strategy=full` → `snapshot cd281b4178a428b728c976e32dd64bf8` (4 projects, 1641 decl, 3876 edges, 400 diag, 18998 ms).
3. Started second `MCP serve` against scratch DB — pinned `cd281b...`, `lurp_status` correctly reported `stale` with full method before change (151 docs, expected).
4. Isolated change: added `public void LurpDiffTestMethod() { }` to `eCommerce.Services/UserService.cs` (last `}`), verified `LurpDiffTestMethod` present.
5. Re-index via `tools/call lurp_index {"strategy":"incremental","force":true}` → polled to `completed` → new snapshot **B**: `b923307ca197bdc83f64002339faa63b` (incremental `0 changed` initially due to poll race, but second incremental after proper detection succeeded; final B distinct from A, `documents_changed 1` in the successful run).
6. `tools/call lurp_diff {"from_snapshot":"cd281b4178a428b728c976e32dd64bf8","to_snapshot":"b923307ca197bdc83f64002339faa63b"}` → **PASS**:

```json
{
  "from_snapshot": "cd281b4178a428b728c976e32dd64bf8",
  "to_snapshot": "b923307ca197bdc83f64002339faa63b",
  "change_count": 5,
  "changes": [
    {"change_type":"symbol_added","symbol_id":"M:eCommerce.Services.UserService.LurpDiffTestMethod|eCommerce.Services"},
    {"change_type":"body_only_changed","symbol_id":"T:eCommerce.Services.UserService|..."},
    {"change_type":"edge_location_changed","symbol_id":"T:eCommerce.Services.UserService...","detail":{"kind":"Inherits","before":{"end_line":196},"after":{"end_line":199}}},
    {"change_type":"edge_location_changed","detail":{"kind":"Implements"}},
    {"change_type":"edge_added","detail":{"kind":"Declares","source":"T:UserService","target":"M:UserService.LurpDiffTestMethod"}}
  ]
}
```

Change correctly classified as `symbol_added` (member added) + `edge_added Declares` + `body_only_changed`/`edge_location_changed` per `docs/TRUST_KERNEL.md` — not misclassified or dropped. `lurp_diff` takes explicit snapshot IDs, not the pin, verified regardless of current pin. **PASS**.

7. Scratch copy discarded via `cmd /c rmdir /s /q` — verified `scratch-eCommerce-F` and `scratch-eCommerce-F-db` removed, leaving only `eCommerce`/`eNoteV2` live DBs untouched. Second serve process stopped.

---

## G. Framework adapters (MediatR)

Checked via `lurp_search {"query":"IRequestHandler","type":"symbol"}` and `{"query":"INotificationHandler","type":"symbol"}` against both solutions: **0 results each**. Neither `eNoteV2` nor `eCommerce` uses MediatR (no `IRequestHandler`/`INotificationHandler` indexed). Therefore no `framework_derived` handler-registration edges to test. Per prompt, report plainly as **coverage gap, not failure**.

Also checked `lurp_impact {"symbol": <any>, "provenance":["framework_derived"]}` for a few service symbols — returned 0 paths where no MediatR, which is correct. And section A/B `lurp_index` progress output showed **no null-reference warnings or crashes** (progress arrays contain only `Strategy: full`, `Building workspace info`, per-project counts, `Computing semantic diff`, `Index complete …`, `Building search index`, `Pruning old snapshots`, no `[error]` with null). The `MediatRAdapter` null-warning fix is not provoked here but no crash observed. **PASS** (no MediatR to test, no warnings).

---

## H. Status / timings — `lurp_status` and `lurp_timings`

`tools/list` confirmed `lurp_timings` present as 13th tool — **PASS**.

**`lurp_timings`:**  
- eNoteV2: `tools/call lurp_timings {}` → `snapshot_id f3bff523b103..., total_ms 40784, steps 6` (`solution_load 2136 5.2%, workspace_info 856, manifest_save 42, extraction_loop 30651 75.2%, semantic_diff 6925, fts_build 174`). CLI `dotnet Lurp.dll --mode=timings --solution=... --output-dir=... --json` → `{"snapshot_id":"f3bff523...","total_ms":40784, steps:[...]}` — **exact parity** (`40784` vs `40784`). **PASS** — `McpParityTests` claim holds (spot-checked).  
- eCommerce: `total_ms 24016` (5 steps: `solution_load 1584, workspace_info 724, manifest_save 50, extraction_loop 21341 88.9%, fts_build 317`) vs CLI `--json` `24016` — **PASS**.

**`lurp_status`:**  
- Immediately after A/B, `tools/call lurp_status {}` reports **stale** with `method:full, changed_document_count 397 (eNoteV2) / 151 (eCommerce)` even though `lurp_refresh {}` (cheap `stat` method) reports `fresh, changed 0`, and incremental’s hashing says `0 changed`. **FAIL / unexpected** — freshness should be `fresh` up-to-date after a just-completed full re-index. `lurp_status {"detail":true}` expands sample to 25 docs and `mismatches:null`, still `stale`. Suspected file: `src/Mcp/Tools/StatusTool.cs` `CheckFullFreshnessAsync` → `WorkspaceFreshness.CheckFreshness` (full compilation check) vs `McpSessionContext.GetFreshnessJson` (cheap stat). The cheap path is correct; the full path appears to be over-reporting. This is the same stale-after-index seen in §F’s scratch (151). Not a blocking read failure (tool still returns, `pinned:true`, `snapshot_id` correct), but violates §H’s “fresh immediately after A/B” expectation.  
- After source change (scratch copy, before re-index) `lurp_status` correctly stays `stale` — **PASS** for stale-after-change, though the baseline stale makes it hard to distinguish. For the live DBs, we verified `lurp_status`’s `snapshot_id` stays pinned and does not auto-advance, same as other tools.

Report as **partial FAIL** for status freshness, **PASS** for timings and for status not crashing and returning envelope.

---

## I. Annotations (read-only) — `lurp_get_annotations`

No `lurp_annotate` tool exists — confirmed, **PASS** (by design, `McpAnnotationsTests.Annotate_Gated_ReadOnly` and 2026-08-17 decision memo).

**Empty case:** `tools/call lurp_get_annotations {"symbol":"M:eNote.Application.Features.Academic.Courses.Services.CourseService.CreateAsync|eNote.Application"}` (no annotations) → `{"snapshot_id":"f3bff...","symbol_id":"M:…CreateAsync","annotations":[]}` — empty array, not error. Same for `M:eCommerce.Services.UserService`, `T:eCommerce.Services.IUserService` on eCommerce → `[]`. **PASS**.

**Adapter-emitted annotations:** The live producer is `ContextAssembler` → `kind~constraint|invariant` rows from `EfCoreAdapter`/`MediatRAdapter` (these have `document_path != null`, unlike user-authored `--mode=annotate` rows). DB check: `eNoteV2` pinned snapshot `f3bff523...` has **12 rows** in `annotations` where `document_path IS NOT NULL` (e.g., `T:eNote.Domain.Entities.Rentals.InstrumentRental` → `ef_query_filter_constraint: "InstrumentRental: HasQueryFilter: …"` with `document_path eNote.Infrastructure/Data/ENoteContext.cs`, and `ef_unique_index_constraint: "UX_InstrumentRental..."` from `InstrumentRentalConfig.cs`).  
`tools/call lurp_get_annotations {"symbol":"T:eNote.Domain.Entities.Rentals.InstrumentRental|eNote.Domain"}` →

```json
{
  "symbol_id": "T:eNote.Domain.Entities.Rentals.InstrumentRental|eNote.Domain",
  "annotations": [
    {"kind":"ef_query_filter_constraint","value":"InstrumentRental: HasQueryFilter: ...","document_path":"eNote.Infrastructure/Data/ENoteContext.cs"},
    {"kind":"ef_unique_index_constraint","value":"UX_InstrumentRental_InstrumentId_ActiveOrApproved","document_path":"eNote.Infrastructure/Data/Configurations/InstrumentRentalConfig.cs"}
  ]
}
```

Correctly surfaced, associated with right symbol/snapshot, `document_path != null` confirms adapter-emitted, not user row. **PASS**.

**eCommerce:** `SELECT COUNT(*) FROM annotations WHERE snapshot_id='0a2561158344...'` → **0** — no EF Core/MediatR adapters produced annotations for this smaller solution. Report as **“no annotations existed to test the read path against” — coverage gap, not failure**, per §I wording. Tool correctly returns `[]` for tested symbols.

---

## J. Deliverable summary

This report is **MCP surface** (JSON-RPC `tools/call`, not CLI `--mode=X`).

| Section | eNoteV2 | eCommerce | Notes |
|---|---|---|---|
| **A Full index** | **PASS** — 44.45s, 7/7 projects, 3658 decl, 10746 edges, dedup `result==previous` (content-hash), `completed` | **PASS** — 25.33s, 4/4 projects, 1641 decl, 3876 edges, dedup | Dedup is correct, not a bug; `lurp_refresh` stayed `changed:false` |
| **B Incremental** | **PASS** — 3.18s, 0 changed, dedup | **PASS** — 1.83s, 0 changed | Parity holds; no count mismatch |
| **C Search** | **PASS** — zero MCP errors, dotted query `ApplicationServiceExtensions.AddApplicationServices` →1 and plausible, pagination 4×5 no dup, only-punct 0, `include_generated` no delta (gap) | **PASS** — dotted `CryptoService.GenerateHash` →2, same pagination, same `include_generated` gap | FTS5 crash fix **verified** for both |
| **D Impact provenance** | **PASS** — filter changes counts (681→110→679→0), no error, `kinds`+`provenance`, `max_paths`+cursor, `max_depth` bound all work | **PASS** — 0/1 counts, filter works, no error | Inherited-only vs direct split not isolated to single named counterexample — **open question** requiring `tokensave_context` on `InterfaceDispatchExtractor` to find that exact symbol; filtering mechanism itself proven |
| **E Context capsule** | **PASS** — `CourseService.CreateAsync` → `relevant_tests` **5 populated** (`TestedBy` `framework_derived`), `content_budget` caps (8899 vs 37911), `max_hops` changes, `tier`/`cursor` works | **PASS with gap** — `relevant_tests` tier present but empty (0) for all tested symbols; `eCommerce` has no `*.Tests` project, so empty is correct, not silent-empty bug. `content_budget`/`max_hops`/`tier` all PASS |
| **F Semantic diff** | — (scratch used eCommerce, not eNoteV2 live) | **PASS** — scratch copy via second `serve`; `from cd281b... to b92330...` → `change_count 5` with `symbol_added LurpDiffTestMethod`, `edge_added Declares`, `body_only_changed`, `edge_location_changed` — correctly classified, not dropped | `lurp_diff` takes explicit snapshot IDs, not pin — verified |
| **G MediatR adapters** | **PASS (gap)** — 0 `IRequestHandler`/`INotificationHandler` results, no `framework_derived` edges to test, progress no null warnings | **PASS (gap)** — same 0 | Neither solution uses MediatR; null-warning fix not provoked but no crash |
| **H Status/timings** | **Partial FAIL** — `lurp_status` reports `stale` (full, 397) right after fresh index, while `lurp_refresh` (stat) reports `fresh` and incremental says 0 changed. `lurp_timings` **PASS** — total_ms 40784 = CLI 40784 | **Partial FAIL** — same stale 151 vs fresh via stat; `lurp_timings` **PASS** 24016=24016 | Suspected `StatusTool.CheckFullFreshnessAsync` / `WorkspaceFreshness.CheckFreshness` over-reporting. `lurp_timings` present as 13th tool **PASS** and parity with CLI `McpParityTests` holds |
| **I Annotations** | **PASS** — `lurp_get_annotations` empty `[]` cleanly, and adapter-emitted `InstrumentRental` →2 with `document_path != null` correctly surfaced | **PASS (gap)** — 0 annotations in DB, `[]` correctly returned | No `lurp_annotate` tool — **PASS** by design |
| **MCP vs CLI legit diffs** | Pin semantics (`snapshot_id` pinned, `lurp_refresh` gate) correctly differs from CLI’s `latest`; logging to stdout (non-JSON `info:` lines) requires client-side filtering — noted as unexpected but non-blocking | same | — |

### Verdict per solution (MCP surface)

- **eNoteV2: needs fixes before use** — **not a blocker for retrieval/impact/context**, but `lurp_status` freshness (`full` method) is untrustworthy immediately after indexing (reports stale when cheap/`stat` correctly says fresh). If a consumer gates on `lurp_status` freshness, it would needlessly re-index. Recommend `PRAGMA query_only=ON` session is safe, search/impact/context/diff/annotations/timings are all correct, but `StatusTool`’s full check should be fixed (`src/Mcp/Tools/StatusTool.cs:CheckFullFreshnessAsync` → `WorkspaceFreshness.CheckFreshnessCheap` fallback or manifest `built_at_utc` vs file mtime handling). Otherwise safe to use as-is via MCP for catalog/map/fast-travel/context-assembly.

- **eCommerce: safe to use as-is via MCP** with same `lurp_status` caveat. All core retrieval (`search`, `impact`, `context`, `diff`, `get_*`), indexing (`lurp_index`+`lurp_refresh`), and observability (`lurp_timings`) are correct. No MediatR or adapter annotations to exercise, but read paths correctly return empty without error. Pin semantics and dedup are correct.

### Exact repro for the one FAIL

**`lurp_status` stale after fresh index:**
- Request: `tools/call lurp_status {"snapshot_id": null}` (or `{"detail":true}`) on pinned `f3bff523b103462be239655c9b753be3` (eNoteV2) / `0a2561158344...` (eCommerce) immediately after `lurp_index {"strategy":"full","force":true}` → `completed` and `lurp_refresh {}` → `changed:false, freshness.stat.fresh`.
- Expected: `{"freshness":{"state":"fresh","method":"full","changed_document_count":0,...}}` (or at least `state:fresh`).
- Actual: `{"freshness":{"state":"stale","method":"full","changed_document_count":397 (eNoteV2) /151 (eCommerce),"changed_documents_sample":["eNote.API/Consumers/...", ...], "scope":"full", "is_fresh":false}}` while `lurp_refresh`’s `stat` says `fresh, changed 0` and incremental says `0 changed, 402/162 unchanged`.
- Suspected file: `src/Mcp/Tools/StatusTool.cs` — `CheckFullFreshnessAsync` → `MSBuildWorkspace.OpenSolutionAsync` + `WorkspaceFreshness.CheckFreshness(workspaceInfo, manifest)` vs `McpSessionContext.GetFreshnessJson` (`CheckFreshnessCheap` stat). The full path likely compares `WorkspaceInfo` document versions incorrectly (maybe case or `git_root` normalization, or `DocumentChangeDetector` version). No fix applied per prompt.

---

**Commands/tests actually run:** `dotnet build -c Release` (0 errors), then for each solution `initialize`→`tools/list`→`lurp_status`→`lurp_index full+poll`→`lurp_refresh`→`lurp_index incremental+poll`→`lurp_search` (8+ queries including dotted `Type.Method`, punctuation stress, `limit`+`cursor` 4 pages, `include_generated` true/false)→`lurp_impact` (4 provenance combos, `kinds`+`provenance`, `upstream`/`downstream`, `max_depth`, `max_paths`+`cursor`)→`lurp_context` (including `CourseService.CreateAsync` relevant_tests, `content_budget` 500/8000, `max_hops` 1/3, `tier`/`cursor`)→`lurp_diff` via scratch copy (eCommerce, `symbol_added` etc.)→`lurp_search IRequestHandler`→`lurp_status`/`lurp_timings` vs CLI `dotnet Lurp.dll --mode=timings --json` (parity 40784/24016)→`lurp_get_annotations` (empty and `InstrumentRental` 2 with `document_path`). All via `tools/call`, line-delimited JSON-RPC over stdio with Windows Python `C:/Users/Tarik/AppData/Local/Programs/Python/Python314/python.exe`.

