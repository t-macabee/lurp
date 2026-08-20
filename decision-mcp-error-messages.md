# Decision: MCP error messages — CLI flag syntax vs MCP property syntax

**Date:** 2026-08-20
**Status:** Investigated — option (b) already implemented (commit `c179b2f`, 2026-08-19). One residual `--tier` leak remains.
**Scope:** `lurp_find_symbol` and 9+ sibling MCP tools in `src/Mcp/Tools/` vs CLI handlers in `src/Handlers/`

---

## 1. Observed facts

### 1.1 Current MCP tool messages are already MCP-native (not CLI-flag)

Grepping HEAD shows **zero** `is required.` messages with a `--` prefix in `src/Mcp/Tools/` except one residual:

```
src/Mcp/Tools/AnnotationsTool.cs:65   "document is required."
src/Mcp/Tools/ContextTool.cs:80       "Either symbol or file+line is required."
src/Mcp/Tools/DiagnosticsTool.cs:40   "document is required."
src/Mcp/Tools/DiffTool.cs:29          "from-snapshot is required."
src/Mcp/Tools/DiffTool.cs:31          "to-snapshot is required."
src/Mcp/Tools/FindSymbolTool.cs:31    "symbol is required."
src/Mcp/Tools/GetSourceTool.cs:36     "document is required."
src/Mcp/Tools/GetSymbolTool.cs:34     "symbol is required."
src/Mcp/Tools/GrepTool.cs:34          "query is required."          <- reference shape
src/Mcp/Tools/ImpactTool.cs:43        "symbol is required."
src/Mcp/Tools/OutlineTool.cs:34       "document is required."
src/Mcp/Tools/SearchTool.cs:37        "query is required."
```

All match `GrepTool.cs:34`'s pattern:

```csharp
throw new McpProtocolException("query is required.", McpErrorCode.InvalidParams);
```

The prompt's report of `"--symbol is required."` in MCP callers was **true at `c179b2f^`** and is **stale at HEAD**. Verified:

```sh
git show c179b2f^:src/Mcp/Tools/FindSymbolTool.cs | grep "is required"
# -> throw new McpProtocolException("--symbol is required.", McpErrorCode.InvalidParams);
git show c179b2f:src/Mcp/Tools/FindSymbolTool.cs | grep "is required"
# -> throw new McpProtocolException("symbol is required.", McpErrorCode.InvalidParams);
```

### 1.2 The one remaining CLI-prefix leak in MCP

```text
src/Mcp/Tools/ContextTool.cs:121
  throw new McpProtocolException($"no symbol found at {normalizedFile}:{line}; --tier needs an anchor symbol.", ...)
```

Observed: one inline string still uses `--tier`. No other `is required.` or `must be ...` message retains `--` (see exhaustive grep in §1.1). `IndexTool.cs:102` intentionally mentions `Provide --solution=path ...` as remediation help, not a field-name error — kept as-is.

### 1.3 No single shared "is required" builder — two layers, duplicated per tool

**CLI layer — shared helpers in `src/Handlers/HandlerBootstrap.cs` (observed):**

* `HandlerBootstrap.RequireArg(string[] args, string prefix, params string[] errorLines)` — looks up `args.FirstOrDefault(a => StartsWith(prefix))`, calls `Fail(string.Join(Environment.NewLine, errorLines))` on miss. Error lines are literal strings passed by callers, e.g. `AnnotationHandler.cs:13`:

  ```csharp
  HandlerBootstrap.RequireArg(args, "--symbol=", "ERROR: --symbol=<symbolId> is required for --mode=annotate.");
  ```

* `HandlerBootstrap.Fail(string message, int code = 1)` — `throw new CliExitException(message, code)`. `Program.Main` prints to stderr and exits.

* Callers (`AnnotationHandler`, `FindSymbolHandler`, `GetSymbolHandler`, `GetSourceHandler`, `ImpactHandler`, `ContextHandler`, `SearchHandler`, `GrepHandler`, `OutlineHandler`, `IndexHandler`) pass flag-shaped literals like `"ERROR: --symbol=<name> is required for --mode=find-symbol."` (`FindSymbolHandler.cs:10`), `"ERROR: --query=<term> is required ..."` (`GrepHandler.cs:12`), etc. Grep of `ERROR: --` across `src/Handlers/` returns ~30 lines.

* Special-case parsers (`ParsePositiveIntArg:107`, `ResolveSequenceCursor:125`, `ParseOutputMode:83/86`) also `Fail($"ERROR: {prefix.TrimEnd('=')} must be a positive integer.")` or `"ERROR: --cursor is not a valid continuation token."` — prefix is already CLI-shaped (`--limit=`, `--cursor`).

**MCP layer — no shared helper, per-tool inline throws (observed):**

Every `src/Mcp/Tools/*.cs` validates with direct checks and literal MCP-shaped strings:

```csharp
// FindSymbolTool.cs:31, GetSymbolTool.cs:34, ImpactTool.cs:43, GrepTool.cs:34, etc.
if (string.IsNullOrEmpty(symbol))
    throw new McpProtocolException("symbol is required.", McpErrorCode.InvalidParams);
if (limitVal < 1)
    throw new McpProtocolException("limit must be a positive integer.", McpErrorCode.InvalidParams);
if (cursorObj == null)
    throw new McpProtocolException("cursor is not a valid continuation token.", McpErrorCode.InvalidParams);
```

There is no `McpValidation.Require("symbol", value)` helper. The duplication is intentional per-tool; the validation logic is the same shape but the literal is tool-local. Grep `throw new McpProtocolException` across `src/Mcp/Tools/` confirms ~40+ sites with MCP-native phrasing post-`c179b2f`.

**Cross-surface shared code that still leaks CLI text into MCP (observed):**

* `HandlerBootstrap.ResolveSymbolArg` (`HandlerBootstrap.cs:290`) is called by both CLI and MCP (`AnnotationsTool:62`, `ContextTool:118`, `GetSymbolTool`, `ImpactTool`). On failure it calls `Fail("ERROR: Could not resolve '...' ...")` — an `ERROR:`-prefixed CLI message. MCP catches via `McpErrorMapper.Map(CliExitException)` (`Mcp/McpErrorMapper.cs:9`) which preserves `cli.Message` verbatim as an `McpProtocolException`. Result: MCP client sees `"ERROR: Could not resolve ..."` rather than a property-shaped MCP error. This is the only *true* shared builder after `c179b2f`.

* `src/Storage/DiagnosticStore.cs:126` and similar `ArgumentException` messages use `"--limit must be a positive integer."` and are re-thrown as `new McpProtocolException(ex.Message, ...)` in `DiagnosticsTool`, `SearchTool`, `AnnotationsTool` catch blocks — another leak path, though now mostly shadowed by earlier MCP-side validation (the MCP tools validate `limit < 1` *before* calling storage, so storage's `--limit` is fallback-only). `c179b2f` also patched `DiagnosticStore` to emit `"Unknown severity '...'"` without `--`.

### 1.4 Canonical comparison for (b): `GrepTool` as reference

`src/Mcp/Tools/GrepTool.cs` (introduced in `2447cc5` after the fix) sets the target shape for all tools:

```csharp
if (string.IsNullOrEmpty(query))
    throw new McpProtocolException("query is required.", McpErrorCode.InvalidParams);
if (limitVal < 1)
    throw new McpProtocolException("limit must be a positive integer.", McpErrorCode.InvalidParams);
if (cursorObj == null)
    throw new McpProtocolException("cursor is not a valid continuation token.", McpErrorCode.InvalidParams);
```

Notable traits: bare kebab property name (`query`, `limit`, `cursor`), lower-case, period-terminated, `McpErrorCode.InvalidParams`, no `ERROR:` prefix, no `--`, no `for --mode=...` suffix. Docs (`docs/CLI_REFERENCE.md` post-`c179b2f`) explicitly codify: "CLI validation errors name flags (e.g. `ERROR: --document is required.`), while MCP errors name plain properties (e.g. `document is required.`) without the `--` prefix."

---

## 2. Options

### Option (a) — Keep one shared builder between CLI and MCP and accept the mismatch (pre-`c179b2f` state)

**What it means:** Reintroduce or keep a single formatter that produces CLI-flag text and use it from MCP tools (e.g., a shared `ValidationMessages.Required("--symbol")` -> `"--symbol is required."` or direct reuse of CLI handler strings via `HandlerBootstrap.RequireArg`-style messages mapped through `McpErrorMapper`).

**Observed shape before `c179b2f` (the "current state" in the prompt's wording):**

```csharp
// MCP tools before c179b2f — 12 files shared CLI phrasing
throw new McpProtocolException("--symbol is required.", ...);
throw new McpProtocolException("--query is required.", ...);
throw new McpProtocolException("--limit must be a positive integer.", ...);
throw new McpProtocolException("--cursor is not a valid continuation token.", ...);
// ...
```

**Pros**

* One validation message source — no drift when phrasing changes.
* CLI and MCP error tests can share assertions (single literal per field).
* Minimal MCP-specific code; reuse of existing `HandlerBootstrap` helpers and storage `ArgumentException` messages.

**Cons**

* Breaks MCP transport contract: MCP callers know JSON property names (`symbol`, `query`, `document`, `limit`, `cursor`), never CLI flags (`--symbol`, `--query=`, `--document=`). An error saying `"--symbol is required."` is misleading — there is no `--symbol` to send over MCP. Model-context clients (OpenAI/Anthropic tool-call layer, `ModelContextProtocol` SDK) surface the property name in the tool schema; the error's suggested remediation points to a flag the caller cannot use, increasing LLM retry errors.
* Documented mismatch becomes permanent debt. `docs/CLI_REFERENCE.md` already documents the split as intentional ("two surfaces share the same validation logic but render the argument name in the form that matches the caller"). Accepting the mismatch re-introduces a known UX bug the team already fixed and documented.
* The "shared builder" would need to emit either CLI form or MCP form — to keep one builder you'd need a branch on surface (`bool isMcp`), which is already option (b) in disguise, or you'd need to post-process (`message.Replace("--","")`), which is fragile and still requires per-surface handling for the `ERROR:` prefix and `for --mode=` suffix that CLI adds.
* Validation semantics differ: MCP uses kebab `limit`, snake `content_budget`/`max_mismatches`, and bare `query` alongside complex rules (`"Either symbol or file+line is required."`, `"symbol and document are mutually exclusive"`). A CLI-first builder cannot model these without MCP-specific literals anyway — the shared helper would degenerate to `RequireMcp("symbol", value)` with separate string tables.

**When (a) would be justified:** CLI-only product with MCP as a thin wrapper invoked via `lurp --mode=...` subprocess (i.e., MCP just shells CLI). Lurp is the opposite: `McpSessionContext` bypasses CLI `args` parsing and calls stores directly; handlers and MCP tools are parallel entry points to the same stores.

### Option (b) — Give MCP tools their own error text, matching GrepTool's pattern, across all affected tools

**What it means:** MCP tools own their literals: `"symbol is required."`, `"query is required."`, `"document is required."`, `"limit must be a positive integer."`, `"cursor is not a valid continuation token."`, etc. CLI handlers keep `ERROR: --symbol is required for --mode=...` etc. No shared literal for `is required.` — only shared *validation constants* (numeric bounds, enum lists) via non-string helpers.

This is the state **already reached** in `c179b2f "Correct MCP property-style messages and severity handling"`.

#### Concrete diff scope for (b) — what was changed (and what a re-application would touch)

Commit `c179b2f` is the authoritative diff for (b). Applying (b) from the pre-fix state touches **12 MCP files**, **~48 literal strings**, **~80 insertions / ~48 deletions**, no handler or storage changes beyond the severity fix that rode along. Restoring (b) now (from the prompt's hypothesized 9-tool regression) is a mechanical literal swap per tool.

| File | Before (`c179b2f^`) | After (`c179b2f` / HEAD) — GrepTool shape | Count |
|------|---------------------|--------------------------------------------|-------|
| `src/Mcp/Tools/AnnotationsTool.cs` | `"--symbol and --document are mutually exclusive..."` / `"--limit must be ..."` / `"--cursor is not ..."` / `"--document is required."` | `"symbol and document ..."` / `"limit must be ..."` / `"cursor is not ..."` / `"document is required."` | 4 |
| `src/Mcp/Tools/ContextTool.cs` | `"--intent must be ..."` / `"--line must be ..."` / `"--cursor is not ..."` | `"intent must be ..."` / `"line must be ..."` / `"cursor is not ..."` | 3 (+ 1 leak fix ` --tier needs...` -> `tier needs...`) |
| `src/Mcp/Tools/DiagnosticsTool.cs` | `"--document is required."` / `"--limit ..."` / `"--cursor ..."` | `"document is required."` / `"limit ..."` / `"cursor ..."` | 3 |
| `src/Mcp/Tools/DiffTool.cs` | `"--from-snapshot..."` / `"--to-snapshot..."` (x2) | `"from-snapshot..."` / `"to-snapshot..."` | 2 |
| `src/Mcp/Tools/FindSymbolTool.cs` | `"--symbol is required."` | `"symbol is required."` | 1 |
| `src/Mcp/Tools/GetSourceTool.cs` | `"--document ..."` / `"--start-line ..."` / `"--end-line ..."` / `"--context-lines ..."` / `"--start-line must be <= --end-line"` | `"document ..."` / `"start-line ..."` / `"end-line ..."` / `"context-lines ..."` / `"start-line must be <= end-line"` | 6 |
| `src/Mcp/Tools/GetSymbolTool.cs` | `"--symbol ..."` / `"--view ..."` / `"--context-lines ..."` | `"symbol ..."` / `"view ..."` / `"context-lines ..."` | 3 |
| `src/Mcp/Tools/ImpactTool.cs` | `"--symbol ..."` / `"--direction ..."` / `"--max-depth ..."` / `"--max-paths ..."` / `"--cursor ..."` | `"symbol ..."` / `"direction ..."` / `"max-depth ..."` / `"max-paths ..."` / `"cursor ..."` | 5 |
| `src/Mcp/Tools/NavigateTool.cs` | `"--file and --line are required."` / `"--line must be ..."` | `"file and line are required."` / `"line must be ..."` | 2 |
| `src/Mcp/Tools/OutlineTool.cs` | `"--document ..."` / `"--limit ..."` / `"--cursor ..."` | `"document ..."` / `"limit ..."` / `"cursor ..."` | 3 |
| `src/Mcp/Tools/SearchTool.cs` | `"--query ..."` / `"--type ..."` / `"--limit ..."` / `"--snippet-tokens ..."` / `"--cursor is only supported with --type=symbol"` / `"--cursor is not ..."` | `"query ..."` / `"type ..."` / `"limit ..."` / `"snippet-tokens ..."` / `"cursor is only supported with type=symbol"` / `"cursor is not ..."` | 6 |
| `src/Mcp/Tools/StatusTool.cs` | `"--max-documents ..."` / `"--max-mismatches ..."` / `"--documents ..."` / `"--documents contains ..."` | `"max-documents ..."` / `"max-mismatches ..."` / `"documents ..."` / `"documents contains ..."` | 4 |
| `src/Mcp/Tools/IndexTool.cs` | `"--strategy must be ..."` | `"strategy must be ..."` | 1 |
| **Also assessed but unaffected** | `GrepTool.cs` (new after fix) — already MCP-native; `RefreshTool`, `TimingsTool` — no required-field validation | — | — |
| **Residual after `c179b2f`** | `ContextTool.cs:121` `"... --tier needs an anchor symbol."` | Should be `"... tier needs an anchor symbol."` (kebab, no `--`) | 1 |

**Full unified diff (representative excerpt) — can be reproduced with `git show c179b2f`:**

```diff
- throw new McpProtocolException("--symbol is required.", McpErrorCode.InvalidParams);
+ throw new McpProtocolException("symbol is required.", McpErrorCode.InvalidParams);
- throw new McpProtocolException("--query is required.", McpErrorCode.InvalidParams);
+ throw new McpProtocolException("query is required.", McpErrorCode.InvalidParams);
- throw new McpProtocolException("--limit must be a positive integer.", McpErrorCode.InvalidParams);
+ throw new McpProtocolException("limit must be a positive integer.", McpErrorCode.InvalidParams);
- throw new McpProtocolException("--cursor is not a valid continuation token.", McpErrorCode.InvalidParams);
+ throw new McpProtocolException("cursor is not a valid continuation token.", McpErrorCode.InvalidParams);
- throw new McpProtocolException("--start-line must be <= --end-line.", McpErrorCode.InvalidParams);
+ throw new McpProtocolException("start-line must be <= end-line.", McpErrorCode.InvalidParams);
- throw new McpProtocolException("--cursor is only supported with --type=symbol.", McpErrorCode.InvalidParams);
+ throw new McpProtocolException("cursor is only supported with type=symbol.", McpErrorCode.InvalidParams);
```

**Work to (re-)apply (b) if starting from the 9-tool regression:**

1. For each of the 9 listed tools (plus the 3 ancillary ones that share the pattern — `NavigateTool`, `StatusTool`, `IndexTool` — for completeness), replace the MCP-side literal's `--<name>` with `<name>` (preserving kebab/snake as the tool's JSON property name). Automated form: `rg '"--' src/Mcp/Tools --replace '"' | rg 'is required|must be'` then edit; ~30-min mechanical change, no control-flow change.
2. Fix the one remaining leak: `ContextTool.cs:121` — change `"--tier needs an anchor symbol."` -> `"tier needs an anchor symbol."`
3. (Optional but recommended for full separation) Make storage `ArgumentException` messages surface-agnostic or add MCP-side translation before re-throw (currently fallback-only). Not required for the `is required.` scope.

**Pros**

* MCP errors reference the identifier the caller actually has (JSON property `symbol`, `query`). LLMs and MCP clients can self-correct without guessing that `--symbol` means JSON `symbol`.
* Consistent with `GrepTool` and the `lurp_status`/`lurp_diff` envelope — one vocabulary, zero `--` in the MCP surface.
* Keeps handler CLI errors untouched; no risk to CLI snapshot-freshness or `ERROR:` prefix behavior that scripts depend on.
* Cost is trivial and localized (string literals only), zero schema or API breakage, covered by existing MCP tests (adjust assertions from `"--symbol..."` to `"symbol..."` as `c179b2f` did).
* Aligns with the architecture's "handlers and read modes consume persisted facts; they must not create a second semantic engine" invariant — validation messages are per-surface rendering, not shared derivation.

**Cons**

* Two small string tables to maintain (CLI vs MCP). Mitigated because the strings are short, per-tool, and rarely change — and because the *validation thresholds* (e.g., `< 1`) remain shared via code, not strings.
* Copy drift risk if a new MCP tool is added by copying a handler literal. Mitigated by `GrepTool` as a discoverable reference and by the `docs/CLI_REFERENCE.md` note added in `c179b2f`.

**Validation cost for (b):** Narrow — MCP tool tests only (e.g., `McpToolTests`, `FindSymbolToolTests`, `SearchToolTests`, `GrepToolTests`). CLI handler tests are unaffected except the shared docs snapshot. Full-suite not required unless storage messages are also made agnostic.

---

## 3. Recommendation

**Adopt (b) — already adopted — and close the one remaining gap.**

*Inference:* Option (b) strictly dominates (a) for an MCP-first product. The shared-builder economy is ~50 literal characters of deduplication versus a permanent transport-layer lie (`--symbol` over a JSON property). The fix already shipped, is documented, and `GrepTool` proves the target shape. Reverting to (a) would reintroduce the bug the prompt describes.

*Recommended actions (no code change required for the 9 `is required.` messages — they are already correct):*

1. **Close the residual `--tier` leak** — single-line edit:

   ```csharp
   // src/Mcp/Tools/ContextTool.cs:121 — before
   $"no symbol found at {normalizedFile}:{line}; --tier needs an anchor symbol."
   // after
   $"no symbol found at {normalizedFile}:{line}; tier needs an anchor symbol."
   ```

   This is the only `is required.`-adjacent leak still present. (The remediation text in `IndexTool.cs:102` that mentions `--solution=path` is intentional CLI help referenced from MCP; leave it.)

2. **Codify guardrail (docs/tests):** Add a repo grep-gate to CI/no-new-`--` test for `src/Mcp/Tools` so future tools cannot copy a handler string:

   ```sh
   # Fails if any MCP tool emits a CLI flag in an error literal
   ! rg -n '"--[a-z-]+=?.*is required|"--[a-z-].*must be' src/Mcp/Tools
   # Allowlist only: IndexTool remediation mentioning --solution is exempt
   ```

   Or an `McpErrorMessageTests` assertion scanning `McpProtocolException` sites for leading `--`.

3. **(Deferred, low priority)** Make the cross-surface shared path surface-agnostic: change `HandlerBootstrap.ResolveSymbolArg`'s failure to throw a valence-neutral `ArgumentException("...")` or return `null` to MCP callers, letting each surface render its own prefix. Currently the `ERROR: ...` prefix leaks via `McpErrorMapper.Map(CliExitException)` for unresolved symbols — not an `is required.` case, so out of scope for this decision, but it is the next leak to address.

**Non-goals:** No schema/migration, no new CLI mode, no LLM-authored summaries, no architecture scoring — per `docs/ARCHITECTURE.md` §9.

---

## 4. Commands run (to verify — read-only per instructions)

```sh
grep -rn "is required" src --include="*.cs"                 # located all 10 MCP sites + handler sites
grep -rn '"--' src/Mcp --include="*.cs"                      # located residual --tier
git show c179b2f --stat                                       # confirmed 12-file fix scope
git show c179b2f | grep "McpProtocolException"               # enumerated before/after literals
cat src/Mcp/Tools/GrepTool.cs                                # verified reference shape "query is required."
cat src/Mcp/Tools/FindSymbolTool.cs                          # verified current "symbol is required."
cat src/Mcp/Tools/ContextTool.cs                             # verified residual --tier leak at :121
cat src/Handlers/HandlerBootstrap.cs:290-334                  # verified shared ResolveSymbolArg leak
cat src/Handlers/HandlerBootstrap.cs:42-250                  # verified RequireArg/Fail are CLI-only
cat docs/CLI_REFERENCE.md                                     # verified post-fix documentation of split
```

*All evidence is observed from inspected source, SQL, and git output. Inferences and recommendations are labeled above.*

---

## 5. Open questions

None for this decision. All literals were inspected. If a new MCP tool is introduced that validates a novel property (e.g., `max_hops`, `content_budget`), the same rule applies: use the JSON property name without `--`.
