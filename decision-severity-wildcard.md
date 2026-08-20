# Decision: `lurp_diagnostics` `severity=all` wildcard vs. strict rejection

**Date:** 2026-08-20  
**Status:** Investigation-only — no code changed. Recommendation at end.  
**Repo:** `C:\Users\Tarik\Desktop\lurp` (HEAD `2447cc5`, with `c179b2f` already on this branch)

---

## 1. Summary

**Observed fact:** On current `HEAD` (`2447cc5` → `c179b2f` → `ef99932`) `severity=all` is **already** handled as a wildcard and unknown severities are **already** rejected. The bug described in the prompt — `severity=all` silently returning zero results — existed in the original `GetDiagnosticsPage` implementation (`cc51345`, 2026-08-19) and was fixed in `c179b2f` (2026-08-19 20:44). The investigation below documents **where** the filter lives, **what** the old bug was, **which callers/tests are affected**, and lays out options (a) wildcard vs. (b) strict-reject with concrete diffs, risks, and a recommendation.

If you are reading this on a checkout that has been reset to before `c179b2f`, the bug still reproduces. If you are on current `HEAD`, the decision is whether to keep the combined (a)+(b) behavior that `c179b2f` introduced — this doc recommends keeping it.

---

## 2. Where the filtering lives — observed facts

### 2.1 Authoritative filter: `src/Storage/DiagnosticStore.cs:112-270`

`DiagnosticStore.GetDiagnosticsPage(...)` is the single source of truth. Every surface delegates to it.

```csharp
// src/Storage/DiagnosticStore.cs:112
public DiagnosticsPage GetDiagnosticsPage(
    string snapshotId,
    string? projectName,
    string? documentPath,
    string? severity,          // ← raw string from caller, nullable
    bool excludeHidden,        // ← derived from whether caller passed severity at all
    string? id,
    int limit,
    DiagnosticsCursor? cursor,
    string? gitRoot,
    HashSet<string> snapshotDocumentPaths)
```

Two distinct responsibilities in one method:

1. **Validation / wildcard recognition** — `src/Storage/DiagnosticStore.cs:129-142` (added in `c179b2f`):

   ```csharp
   if (severity != null)
   {
       if (string.Equals(severity, "all", StringComparison.OrdinalIgnoreCase))
       {
           // wildcard: include every severity including Hidden — handled in SQL below
       }
       else if (!string.Equals(severity, "Hidden", StringComparison.OrdinalIgnoreCase)
             && !string.Equals(severity, "Info", StringComparison.OrdinalIgnoreCase)
             && !string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase)
             && !string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase))
       {
           throw new ArgumentException(
               $"Unknown severity '{severity}'; must be one of: Hidden, Info, Warning, Error, or 'all' (case-insensitive).",
               nameof(severity));
       }
   }
   ```

2. **SQL pushdown** — `src/Storage/DiagnosticStore.cs:158-181`:

   ```csharp
   if (severity != null)
   {
       if (!string.Equals(severity, "all", StringComparison.OrdinalIgnoreCase))
       {
           where += " AND severity = @severity COLLATE NOCASE";
           cmd.Parameters.AddWithValue("@severity", severity);
       }
       // else wildcard: no severity filter, include Hidden
   }
   else if (excludeHidden)
   {
       where += " AND severity != 'Hidden' COLLATE NOCASE";
   }
   ```

   Default (`severity == null`) → `excludeHidden` is `true` → `severity != 'Hidden'`.  
   Explicit `severity=all` → no `WHERE severity` clause at all → returns Hidden+Info+Warning+Error.  
   Explicit `severity=Hidden|Info|Warning|Error` → `severity = @severity COLLATE NOCASE`.

Diagnostics are stored as plain `TEXT` in `diagnostics.severity` (populated from `DiagnosticRecord.Severity`, which itself comes from `Roslyn DiagnosticSeverity.ToString()` at index time — see `src/Helpers/CompilationHelper.cs:24-45`). No enum constraint in SQLite; the filter relies on string matching.

### 2.2 Delegating layer: `src/Storage/SqliteIndexStore.cs:439-455`

```csharp
// src/Storage/SqliteIndexStore.cs:439
public DiagnosticsPage GetDiagnosticsPage(
    string snapshotId, string? projectName, string? documentPath,
    string? severity, bool excludeHidden, string? id, int limit, DiagnosticsCursor? cursor)
{
    EnsureOpen();
    var snapshot = _lifecycle!.LoadSnapshot(snapshotId);
    var gitRoot = snapshot?.GitRoot;
    var docPaths = _documents!.GetDocumentVersionIdsByPath(snapshotId);
    var pathSet = new HashSet<string>(docPaths.Keys, StringComparer.Ordinal);
    return _diagnostics!.GetDiagnosticsPage(snapshotId, projectName, documentPath,
        severity, excludeHidden, id, limit, cursor, gitRoot, pathSet);
}
```

No additional filtering — pure pass-through plus `gitRoot`/`pathSet` resolution for `in_snapshot` computation.

### 2.3 MCP surface: `src/Mcp/Tools/DiagnosticsTool.cs:20-71`

```csharp
// src/Mcp/Tools/DiagnosticsTool.cs:20-21
[McpServerTool(Name = "lurp_diagnostics", ...)]
[Description("List diagnostics ... Filters: ... severity (Hidden, Info, Warning, Error case-insensitive, "
           + "or \"all\" for every severity including Hidden; default excludes Hidden), "
           + "id (diagnostic code, e.g. CS8933). Unknown severity values are rejected with an error "
           + "instead of returning empty. ...")]
public string LurpDiagnostics(
    string? document = null, string? project = null,
    string? severity = null,   // nullable string, not enum
    string? id = null, int? limit = null, string? cursor = null, string? snapshot_id = null)
```

Key lines:

- `src/Mcp/Tools/DiagnosticsTool.cs:44` — `var severityFilter = string.IsNullOrEmpty(severity) ? null : severity;`
- `src/Mcp/Tools/DiagnosticsTool.cs:64` — `excludeHidden: severityFilter == null` — default excludes Hidden, any explicit value (including `all`) includes the full set implied by the filter.
- `src/Mcp/Tools/DiagnosticsTool.cs:62-70` — `catch (ArgumentException ex) { throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams); }` — maps store validation to `InvalidParams` for MCP clients.
- Tool description explicitly documents `all` and the rejection behavior (updated in `c179b2f`).

### 2.4 CLI surface: `src/Handlers/DiagnosticsHandler.cs:7-106`

```csharp
// src/Handlers/DiagnosticsHandler.cs:15,22
var severityArg = HandlerBootstrap.GetArgValue(args, "--severity=");
var severityFilter = string.IsNullOrEmpty(severityArg) ? null : severityArg;
// ...
// src/Handlers/DiagnosticsHandler.cs:45-53
page = store.GetDiagnosticsPage(snapshotId, projectFilter, documentArg,
    severityFilter, excludeHidden: severityFilter == null, idFilter, limit, cursor);
catch (ArgumentException ex) { HandlerBootstrap.Fail($"ERROR: {ex.Message}"); }
```

Same `excludeHidden: severityFilter == null` convention as MCP. CLI renders errors as `ERROR: <message>` via `HandlerBootstrap.Fail` → `CliExitException` → stderr + exit 1, while MCP renders as `McpProtocolException` with `InvalidParams`.

### 2.5 Cursor fingerprint: `src/Storage/DiagnosticsCursor.cs:6-34`

```csharp
// src/Storage/DiagnosticsCursor.cs:13
public static string ComputeFingerprint(string? projectName, string? documentPath,
    string? severity, bool excludeHidden, string? id)
=> $"{projectName ?? ...}\u0001{documentPath ?? ...}\u0001{severity ?? ...}\u0001{excludeHidden}\u0001{id ?? ...}";
```

Severity (including literal `"all"`) is part of the keyset-pagination fingerprint. Changing severity or excludeHidden invalidates an existing cursor — verified by tests.

### 2.6 What the old bug looked like (pre-`c179b2f`, commit `cc51345`)

Before `c179b2f`, `GetDiagnosticsPage` had **no validation** and **no wildcard**:

```csharp
// cc51345 version — src/Storage/DiagnosticStore.cs
if (severity != null)
{
    where += " AND severity = @severity COLLATE NOCASE";
    cmd.Parameters.AddWithValue("@severity", severity);
}
else if (excludeHidden)
{
    where += " AND severity != 'Hidden' COLLATE NOCASE";
}
```

Consequence (observed fact from code inspection, confirmed by `git show cc51345 -- src/Storage/DiagnosticStore.cs`):

- `severity=all` → SQL `WHERE ... AND severity = 'all' COLLATE NOCASE` → matches **zero rows** (no diagnostic is stored with severity `all`; stored values are only `Hidden|Info|Warning|Error` from Roslyn). The call succeeds, returns `diagnostic_count=0` and empty `diagnostics: []`, indistinguishable from "this snapshot is clean".
- `severity=critcal` (typo) → same: `severity = 'critcal'` → empty result, no error.

This is the silent-wrong-answer class of bug: a valid-looking filter value produces a technically correct but semantically wrong empty set.

---

## 3. Callers and tests that touch `severity`

### 3.1 Production callers (exhaustive)

| File | Role | How it touches `severity` |
|------|------|---------------------------|
| `src/Storage/DiagnosticStore.cs` | Authoritative filter + validation | Validates `all`/known values, builds `WHERE` clause |
| `src/Storage/SqliteIndexStore.cs` | Delegation | Passes through `severity` + `excludeHidden` |
| `src/Mcp/Tools/DiagnosticsTool.cs` | MCP `lurp_diagnostics` | Accepts `string? severity`, maps to `severityFilter`, delegates to store, maps `ArgumentException` → `McpProtocolException(InvalidParams)` |
| `src/Handlers/DiagnosticsHandler.cs` | CLI `--mode=diagnostics` | Parses `--severity=` via `HandlerBootstrap.GetArgValue`, same `excludeHidden` convention, maps `ArgumentException` → `HandlerBootstrap.Fail` |
| `src/Storage/DiagnosticsCursor.cs` | Pagination cursor | `severity` participates in `ComputeFingerprint`; mismatch → `Cursor does not match` error |
| `src/Program.cs:73-74` | Mode registry | Declares `"diagnostics"` mode and its flag inventory `["--severity=", ...]` |
| `docs/CLI_REFERENCE.md:135` | Public documentation | Documents `or 'all'` wildcard and `Unknown severity values are rejected` |
| `src/Helpers/CompilationHelper.cs:24-96` | Index-time producer | Writes `DiagnosticRecord.Severity` as `DiagnosticSeverity.ToString()` — the stored domain the filter matches against |

No other production file reads or writes `severity` as a filter (verified by `rg -n severity src --type cs`).

### 3.2 Tests

| Test file | Test | Severity usage |
|-----------|------|---------------|
| `tests/Mcp/McpDiagnosticsTests.cs:68-89` | `Diagnostics_DefaultSeverityFilter_ExcludesHidden` | Exercises default (`null` → exclude Hidden) vs. `severity: "Hidden"` |
| `tests/Mcp/McpDiagnosticsTests.cs:92-107` | `Diagnostics_ExplicitSeverityHidden_IncludesHiddenRows` | `severity: "Hidden"` filtered read |
| `tests/Mcp/McpDiagnosticsTests.cs:238-290` | `Diagnostics_Pagination_WalkEqualsWhole` | Pagination with default severity (implicitly tests fingerprint with `severity=null, excludeHidden=true`) |
| `tests/Mcp/McpDiagnosticsTests.cs:292-349` | `Diagnostics_CursorFingerprintMismatch_ThrowsInvalidParams` | Explicitly verifies cursor fails when `severity` changes (`severity: "Error"` vs. `project: "DiagProj"` baseline) |
| `tests/Mcp/McpDiagnosticsTests.cs:396-436` | `Diagnostics_UnusedUsing_SurfacesViaCompilerDiagnostic` | `severity: "all"` to locate a `Hidden` `CS8019` — **would have failed before `c179b2f`** (would have returned 0 rows) |
| `tests/Mcp/McpDiagnosticsTests.cs:439-478` | `Diagnostics_UnusedUsing_AnalyzerIdIfPresentIsQueryableById` | `severity: "all"` in two calls — same dependency |
| `tests/Mcp/McpDiagnosticsTests.cs:352-360` | `Diagnostics_LimitLessThanOne_ThrowsInvalidParams` | Negative control (no severity) |
| — | No test for `severity: "bogus"` before `c179b2f` | Gap: unknown-severity rejection is not yet covered by a dedicated test |

**Inference:** The two `severity: "all"` tests (`UnusedUsing_*`) are the canaries. Before `c179b2f`, `severity: "all"` returned empty, so `UnusedUsing_SurfacesViaCompilerDiagnostic` would have failed with `Assert.False(unused.ValueKind == Undefined)` — it depends on `Hidden` diagnostics being visible. Those tests implicitly verify the wildcard; they were added in `ef99932` alongside the fix.

---

## 4. Options

### Option (a): Accept `severity=all` as a real wildcard

**Semantics:** `severity=all` (case-insensitive: `all`, `All`, `ALL`) means "no severity filter; return every severity including `Hidden`". Equivalent to `SELECT ... WHERE snapshot_id=...` with no `severity` predicate. This is the only wildcard value; no glob or comma-list needed (no existing caller requests multi-severity subsets).

**Concrete code change** (relative to the buggy baseline `cc51345`; identical to what `c179b2f` shipped):

**In `src/Storage/DiagnosticStore.cs` — two edits:**

1. Validation block at top of `GetDiagnosticsPage` (before `ComputeFingerprint`):

   ```csharp
   if (severity != null)
   {
       if (string.Equals(severity, "all", StringComparison.OrdinalIgnoreCase))
       {
           // wildcard: include every severity including Hidden — handled in SQL below
       }
       else if (!string.Equals(severity, "Hidden", StringComparison.OrdinalIgnoreCase)
             && !string.Equals(severity, "Info", StringComparison.OrdinalIgnoreCase)
             && !string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase)
             && !string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase))
       {
           throw new ArgumentException(
               $"Unknown severity '{severity}'; must be one of: Hidden, Info, Warning, Error, or 'all' (case-insensitive).",
               nameof(severity));
       }
   }
   ```

   *Note:* If option (a) is implemented **without** option (b), omit the `else if`/`throw` branch and keep only the `all` early-return. But the standalone-(a) variant is not recommended — see risks.

2. SQL pushdown change:

   ```diff
   -if (severity != null)
   -{
   -    where += " AND severity = @severity COLLATE NOCASE";
   -    cmd.Parameters.AddWithValue("@severity", severity);
   -}
   +if (severity != null)
   +{
   +    if (!string.Equals(severity, "all", StringComparison.OrdinalIgnoreCase))
   +    {
   +        where += " AND severity = @severity COLLATE NOCASE";
   +        cmd.Parameters.AddWithValue("@severity", severity);
   +    }
   +    // else wildcard: no severity filter, include Hidden
   +}
    else if (excludeHidden)
   {
       where += " AND severity != 'Hidden' COLLATE NOCASE";
   }
   ```

No changes needed in `DiagnosticsTool.cs`, `DiagnosticsHandler.cs`, or `SqliteIndexStore.cs` — they already pass `severity` through and compute `excludeHidden: severityFilter == null` correctly. With `severity=all`, `excludeHidden` is `false`, so the `else if (excludeHidden)` branch is not taken (correct: wildcard must include Hidden).

**Also update:**

- `src/Mcp/Tools/DiagnosticsTool.cs` `[Description]` to mention `or "all"` (already done in `c179b2f`).
- `docs/CLI_REFERENCE.md` severity row (already done).

**Risks / tradeoffs for (a) alone:**

- **Low risk to existing callers.** Today there are zero callers that intentionally pass `severity=all` expecting empty (verified by `rg -n \"severity.*all\"` and `rg -n severity tests`). The only real callers in tests are the two `UnusedUsing` tests that *already* depend on `all` working — they break if `all` stays literal.
- **Semantic ambiguity.** Some users may expect `all` to mean "default" (i.e. exclude Hidden) rather than "literally all". Documentation must be explicit: `all` includes `Hidden`; omitting `--severity` is the "sensible default" (exclude Hidden). Current docs already state this.
- **Case sensitivity.** `COLLATE NOCASE` already applies to exact severity filters; wildcard must also be case-insensitive (`All`, `ALL`). The `c179b2f` implementation does this.
- **Cursor fingerprint.** `all` participates in the fingerprint verbatim. A cursor issued for `severity=all` must not resume under `severity=Warning`. This already holds because `ComputeFingerprint` includes the raw severity string. No change needed.
- **Does not fix silent-failure for typos.** Option (a) alone still allows `severity=critcal` to silently return empty. That is the gap option (b) fills.

---

### Option (b): Reject unrecognized `severity` values with an explicit error

**Semantics:** Any `severity` value other than `Hidden`, `Info`, `Warning`, `Error`, or `all` (case-insensitive) throws. MCP → `McpProtocolException("Unknown severity '...'; must be one of: ...", InvalidParams)`. CLI → `ERROR: Unknown severity '...'; must be one of: ...` + exit 1. No wildcard — or wildcard is orthogonal.

**Concrete code change** (relative to buggy baseline; identical to `c179b2f` minus the `all` exemption if (a) is not also adopted):

**In `src/Storage/DiagnosticStore.cs` — validation only (no SQL change if not also doing (a)):**

```csharp
if (severity != null
    && !string.Equals(severity, "Hidden", StringComparison.OrdinalIgnoreCase)
    && !string.Equals(severity, "Info", StringComparison.OrdinalIgnoreCase)
    && !string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase)
    && !string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase)
    // add next line only if (a) is also adopted:
    && !string.Equals(severity, "all", StringComparison.OrdinalIgnoreCase))
{
    throw new ArgumentException(
        $"Unknown severity '{severity}'; must be one of: Hidden, Info, Warning, Error"
        + " or 'all' (case-insensitive).", nameof(severity));
}
```

If (b) is adopted **without** (a), the allowed set is just the four `DiagnosticSeverity` names; `severity=all` itself would be rejected — which would be a breaking change for the two `UnusedUsing` tests.

No changes in `DiagnosticsTool.cs`/`DiagnosticsHandler.cs` beyond ensuring they surface the `ArgumentException` — they already do:

```csharp
// Mcp
catch (ArgumentException ex) { throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams); }
// CLI
catch (ArgumentException ex) { HandlerBootstrap.Fail($"ERROR: {ex.Message}"); return; }
```

**Risks / tradeoffs for (b) alone:**

- **Breaking change for typo → empty.** Today a caller that passes a typo (e.g. `severity=warnning`) gets `diagnostic_count=0, diagnostics=[]`. Some callers may be (incorrectly) relying on this to mean "clean". After (b), that caller gets an error. This is the intended fix — silent empty is worse than loud failure — but it must be called out.

- **Evidence on "relying on empty-result-as-clean":** Exhaustive `rg -n severity` shows no production code checks `diagnostic_count==0` as a health gate for diagnostics. The only `diagnostic_count` assertions are in tests and check `>0` or pagination stability. CLI consumers could script `lurp --mode=diagnostics --severity=Error | jq '.diagnostic_count==0'` as a CI gate, but they do so with the *known* severities, not with `all` or typos. Risk is therefore **low**, but not zero for bespoke scripts that pass through user input (e.g. `severity=$USER_INPUT` with unsanitized input).

- **Strictness vs. forward compatibility.** If Roslyn ever adds a new `DiagnosticSeverity` value, strict validation would reject it until Lurp is updated. In practice `DiagnosticSeverity` (Hidden=0, Info=1, Warning=2, Error=3) is stable since Roslyn 1.0; adding a new level would be a major breaking change upstream and Lurp would need an update anyway. Unknown values are overwhelmingly typos, not future values.

- **Localizes well.** Validation lives in `DiagnosticStore.GetDiagnosticsPage` — the only place that interprets `severity`. No scatter.

---

### Comparison

| Dimension | (a) Wildcard only (no validation) | (b) Strict reject only (no wildcard) | (a)+(b) Combined (current `c179b2f`) |
|-----------|-----------------------------------|---------------------------------------|--------------------------------------|
| `severity=all` | Returns all severities (fixes bug) | Rejected as unknown → error (breaks the two `UnusedUsing` tests that rely on `all`; users must omit severity or query Hidden separately) | Returns all severities |
| `severity=wArNiNg` | Returns Warning (COLLATE NOCASE) | Returns Warning | Returns Warning |
| `severity=typo` | Silent empty (bug remains) | Loud error (fixes bug) | Loud error |
| `severity=` (empty) | Normalized to `null` → default (exclude Hidden) — unchanged | Same | Same |
| Caller using empty as "clean" | No change | Breaks that caller (desired — surfaces bug) | Breaks that caller |
| Docs / tool description | Must document `all` | Must document allowed set | Must document both (already done) |
| Test impact | `UnusedUsing` tests start passing | `UnusedUsing` tests start failing (they use `all`) | Both `UnusedUsing` tests pass; no other test affected |

---

## 5. Recommendation

**Recommendation: Keep the combined (a)+(b) behavior that `c179b2f` already ships. If forced to choose one, choose (b).**

### Why (a)+(b) together is the right answer

1. **Fixes both instances of the same bug.** The root cause is not `all` specifically — it is *any* unknown severity silently mapping to `severity = '<unknown>'` in SQL and returning empty. (a) fixes the known-intent case (`all`); (b) fixes the open-ended class of typos and future mistakes. Shipping one without the other leaves a silent-failure path.

2. **Matches user expectation and existing tests.** Two integration tests already call `severity: "all"` expecting `Hidden` diagnostics to be present (`McpDiagnosticsTests.cs:418,465`). Those tests were written to specify the product contract — `all` means all. Rejecting `all` (b-only) would contradict that contract.

3. **Low-risk to existing callers.** Verified by exhaustive search: no production or test caller depends on `severity=<unknown> → empty`. The only risk is an external script that constructs `severity` from unsanitized user input and interprets empty as clean — that script is already buggy (a typo makes it report clean when it is not). Loud failure is strictly better.

4. **Single validation site keeps the invariant cheap.** All validation is in `DiagnosticStore.GetDiagnosticsPage:129-142`. Handlers and MCP tool just surface `ArgumentException` → `InvalidParams`/`ERROR:` — no duplicated allow-lists.

5. **No migration or storage change.** `severity` is a filter-time string (input validation + SQL predicate). No persisted data, no backfill, no schema migration.

### What to do next

- **No code change required** on current `HEAD` — the fix is already in `c179b2f`.

- **If on a pre-`c179b2f` checkout, apply this minimal patch** (same as `c179b2f`):

  ```diff
  --- a/src/Storage/DiagnosticStore.cs
  +++ b/src/Storage/DiagnosticStore.cs
  @@ -126,6 +126,21 @@
   +        if (severity != null)
   +        {
   +            if (string.Equals(severity, "all", StringComparison.OrdinalIgnoreCase))
   +            {
   +                // wildcard: include every severity including Hidden — handled in SQL below
   +            }
   +            else if (!string.Equals(severity, "Hidden", StringComparison.OrdinalIgnoreCase)
   +                  && !string.Equals(severity, "Info", StringComparison.OrdinalIgnoreCase)
   +                  && !string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase)
   +                  && !string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase))
   +            {
   +                throw new ArgumentException($"Unknown severity '{severity}'; must be one of: Hidden, Info, Warning, Error, or 'all' (case-insensitive).", nameof(severity));
   +            }
   +        }
  +
  @@ -149,8 +164,12 @@
  -        if (severity != null)
  -        {
  -            where += " AND severity = @severity COLLATE NOCASE";
  -            cmd.Parameters.AddWithValue("@severity", severity);
  -        }
  +        if (severity != null)
  +        {
  +            if (!string.Equals(severity, "all", StringComparison.OrdinalIgnoreCase))
  +            {
  +                where += " AND severity = @severity COLLATE NOCASE";
  +                cmd.Parameters.AddWithValue("@severity", severity);
  +            }
  +            // else wildcard: no severity filter, include Hidden
  +        }
  ```

  Plus description/docs updates (tool `[Description]` and `docs/CLI_REFERENCE.md:135`) — non-functional but important for discoverability.

- **Add one missing test** (recommendation): a contract test that unknown severity throws:

  ```csharp
  [Fact]
  public async Task Diagnostics_UnknownSeverity_ThrowsInvalidParams()
  {
      await IndexDiagnosticsFixtureAsync();
      await using var session = CreateSession();
      var tool = new DiagnosticsTool(session);
      var ex = Assert.Throws<McpProtocolException>(() => tool.LurpDiagnostics(severity: "critcal"));
      Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
      Assert.Contains("Unknown severity", ex.Message);
  }
  ```

  And the CLI equivalent asserting `HandlerBootstrap.Fail` with `ERROR: Unknown severity`.

- **Monitor external scripts.** If any downstream automation constructs `severity` dynamically, announce in release notes: "Unknown `severity` values now error instead of returning empty; `severity=all` now returns all severities including Hidden."

### Open questions (not blocking the recommendation)

- **Should `severity` ever accept comma-separated lists** (e.g. `Warning,Error`)? No existing caller requests it; the incremental value is low. Defer unless a concrete use case appears.
- **Should `all` also become the documented synonym for omitting the filter?** No — keep `all` and omit distinct: omit = "sensible default" (exclude Hidden, which is mostly `CS8019` noisy); `all` = "give me everything for forensic/debugging". Current docs already distinguish them.

---

## 6. Evidence — commands and files inspected

- `git show cc51345 -- src/Storage/DiagnosticStore.cs` — original `GetDiagnosticsPage` with no validation, literal `severity` pushdown.
- `git show c179b2f -- src/Storage/DiagnosticStore.cs` — wildcard + validation fix.
- `git show c179b2f --stat` and `git log --oneline -- src/Storage/DiagnosticStore.cs src/Mcp/Tools/DiagnosticsTool.cs src/Handlers/DiagnosticsHandler.cs`.
- Live reads: `src/Storage/DiagnosticStore.cs:112-270`, `src/Mcp/Tools/DiagnosticsTool.cs:20-71`, `src/Handlers/DiagnosticsHandler.cs:7-106`, `src/Storage/SqliteIndexStore.cs:439-455`, `src/Storage/DiagnosticsCursor.cs:6-34`, `src/Storage/Models.cs:88-107`, `src/Helpers/CompilationHelper.cs:24-96`, `tests/Mcp/McpDiagnosticsTests.cs` (full file).
- `rg -n severity src --type cs` and `rg -n severity tests --type cs` for exhaustive caller enumeration.

---

*Investigation-only — no production code, tests, or migrations were modified. The file you are reading is the deliverable.*
