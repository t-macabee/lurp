# Lurp CLI reference

This is the operational reference for Lurp's command-line interface. For the
product overview, see the [root README](../README.md). For the architecture, see
[ARCHITECTURE.md](ARCHITECTURE.md).

Lurp loads .NET solutions through Roslyn, stores snapshot-bound symbols,
relationships, and source spans in SQLite (`index.db`), and exposes commands
for retrieval, semantic diffing, impact analysis, context capsules,
and annotations.

## Quick Start

```
lurp --mode=index --solution=MySolution.sln --output-dir=./out
```

This creates `./out/index.db` containing all indexed symbols, edges, and source facts.

**Environment variables:** `LURP_SOLUTION_PATH` and `LURP_OUTPUT_DIR` are equivalent to `--solution=` and `--output-dir=` on every mode that reads them — set once and every subsequent command can drop both flags. A database location is otherwise required almost everywhere below (see [ENVIRONMENT VARIABLES](#environment-variables)): pass `--output-dir=` for a specific database, or `--solution=` (or `LURP_SOLUTION_PATH`) to use the default cache location described under [`--mode=index`](#--modeindex). Only `--solution=` and `--output-dir=` have an environment variable; every other required flag has none.

**This document describes the CLI as of <!-- generated:versions -->tool 4.2.0, schema v54, extractor 1.14.0, CLI/MCP contract v9, output schema v8<!-- /generated:versions -->.** The changes of each release are in the release notes that [README.md](../README.md#documentation--license) lists. Run `lurp --version` before relying on anything below — a mode, flag, or field this doc mentions but your installed `lurp` rejects means the two are out of sync (most commonly: `dotnet tool install --global lurp` pulled an older published version than the source this doc ships with). `--version` prints the schema, extractor, and CLI/MCP contract versions the running binary was built with, so a mismatch is a one-command check rather than a guess from trial and error.

`--mode=index` always indexes the entire solution named by `--solution=`; there is
no per-project or per-directory scoping flag. To point Lurp at one part of a larger
codebase, index the whole `.sln`/`.slnx` once and then query narrowly (`--mode=search`,
`--mode=context`, etc.). You cannot index a subset up front.

**MCP tool errors** return a result with `isError: true` and a message in `content[0].text`. This covers invalid arguments, snapshot mismatches, missing symbols or documents, and store failures that prevent the tool from running. Messages name plain parameters, as in `document is required.`, and omit the CLI's `ERROR:` prefix. Unknown tools and malformed requests return JSON-RPC errors.

## Read-command options

Accepted by the read commands: `--mode=search`, `--mode=grep`, `--mode=find-symbol`,
`--mode=impact`, `--mode=context`, `--mode=get-source`, `--mode=get-symbol`,
`--mode=navigate`, `--mode=outline`, `--mode=diagnostics`,
`--mode=get-annotations`, and `--mode=dead-candidates`.

<!-- generated:flags:read-options -->
| Argument | Required | Description |
|---|---|---|
| `--output=<summary\|json\|jsonl>` | No | Payload rendering (default: `json`). `summary` is a digest; `jsonl` emits nested records `{"type":"meta","meta":{...}}` followed by one `{"type":"<t>","<t>":{...}}` record per item (see the JSONL record-types table in docs/CLI_REFERENCE.md). `jsonl` is rejected for a whole capsule, whose payload is a single document. |
| `--quiet` | No | Emit only the payload: suppresses the freshness stderr line, and for `--mode=context` prints just the written capsule path instead of the capsule itself. |
| `--freshness=<auto\|hash\|off>` | No | How hard to check that the snapshot still matches the working tree (default: `auto`). `auto` compares file times with the time of the index run that last built or reused the snapshot; `auto` and `hash` also list a new `.cs` file under the git root (outside `bin`, `obj`, `.git`) that is not in the snapshot and is newer than that run, or that sits in a directory changed after it; `hash` also re-hashes every tracked document; `off` skips the check. An unknown value is an error. |
| `--require-fresh` | No | Exit `2` when the snapshot is not fresh. |
<!-- /generated:flags:read-options -->

`--output=` applies to modes whose payload is a JSON sequence (`search`, `grep`,
`find-symbol`, `impact`, `context`, `outline`, `diagnostics`, `get-annotations`, `dead-candidates`).
`get-source`, `get-symbol`, and `navigate` emit a single fixed shape — raw source
bytes or one JSON document — so they accept `--freshness=`, `--require-fresh`, and
`--quiet` but reject `--output=`.

### JSONL record types

Each stream starts with `{"type":"meta","meta":{...}}`. Each item follows on its
own line as `{"type":"<t>","<t>":{...}}`, with the type listed below. For
`context`, JSONL applies to tier continuation (`--tier=`).

| Mode | First record | Item record |
|---|---|---|
| `search` | `meta` | `result` |
| `grep` | `meta` | `result` |
| `find-symbol` | `meta` | `symbol` |
| `impact` | `meta` | `symbol` |
| `outline` | `meta` | `declaration` |
| `diagnostics` | `meta` | `diagnostic` |
| `get-annotations` | `meta` | `annotation` |
| `dead-candidates` | `meta` | `candidate` |
| `context` | `meta` | `item` |

### Freshness

Freshness is delivered in two tiers, because two payload shapes exist:

- **Every read mode emits a freshness signal**: a stderr line reporting `state` (`fresh`, `stale`, `unknown`) and the `method` used to determine it, plus an exit code. `--require-fresh` exits `2` when the snapshot is not fresh, so a stale read is never presented as a current one.
- **Modes whose payload is JSON additionally embed a `freshness` block** in the payload: `search`, `grep`, `find-symbol`, `impact`, `context` with `--tier=`, `navigate`, `get-symbol --view=metadata`, `outline`, `diagnostics`, `get-annotations`, and `dead-candidates`.
- **Raw-source modes cannot carry a block**: `get-source`, and the `signature`/`body`/`declaration`/`containing-type`/`surrounding` views of `get-symbol`, write source bytes to stdout verbatim by contract (consumers pipe them to files or compilers). Their signal is the stderr line plus the exit code; `--quiet` suppresses the line, never the exit code.

The context capsule has no `freshness` block, because the capsule is also written to a file and must be the same bytes for the same snapshot. Its freshness is the stderr line and the `--require-fresh` exit code. `lurp_context` returns the capsule inside an envelope that carries `freshness`.

| Key | Read | `lurp_status` | Mismatch row | Meaning |
|---|---|---|---|---|
| `state` | 1 | 1 | — | Freshness state: `fresh`, `stale`, or `unknown`. |
| `method` | 2 | 2 | — | Check method used (`stat`, `stat+hash`, `full`, or `skipped`). |
| `changed_document_count` | 3 | 6 | — | Total number of changed, missing, or new `.cs` documents detected. |
| `changed_documents_sample` | 4 | 7 | — | Capped sample of changed document paths (`null` when `mismatches` carries rows). |
| `changed_documents_sample_truncated` | 5 | 8 | — | Whether `changed_documents_sample` was truncated to the sample cap. |
| `checked_at_utc` | 6 | 5 | — | UTC timestamp when the freshness check ran. |
| `snapshot_id` | 7 | 4 | — | Snapshot id evaluated by the freshness check. |
| `scope` | 8 | 3 | — | Check scope (`documents_only` for cheap read checks, `full` for workspace-loaded checks). |
| `is_fresh` | — | 9 | — | Boolean `true`/`false` for `fresh`/`stale`, or `null` when `state` is `unknown`. |
| `mismatches` | — | 10 | — | Capped list of full-check mismatch objects (`null` when no full check ran or omitted). |
| `mismatches_truncated` | — | 11 | — | Whether `mismatches` was truncated to `--max-mismatches` (`null` on cheap checks). |
| `mismatches_total` | — | 12 | — | Total mismatch count from the full check (`null` on cheap checks). |
| `full_check_error` | — | 13 | — | Explanation when a requested full check did not run or failed, otherwise `null`. |
| `kind` | — | — | 1 | Mismatch classification name (`MismatchKind`). |
| `description` | — | — | 2 | Human-readable summary of the mismatch. |
| `document` | — | — | 3 | Affected document path, or `null` when the mismatch is workspace-wide. |
| `detail` | — | — | 4 | Additional mismatch detail (such as hash or version transition), or `null`. |

**Exit codes:** a diagnosed command failure prints one `ERROR:` line to stderr and exits `1`; `--require-fresh` exits `2`; a workspace that cannot be loaded exits `2`. This includes a missing .NET SDK: `--mode=index` and `--mode=status --solution=` print `ERROR: Indexing requires a .NET SDK that can build the target solution. Install a .NET SDK, restore the solution, and retry.` and exit `2`. Read modes need only the .NET runtime; solution-backed status is the exception. An unexpected internal failure also prints one line and exits `1` — a full stack trace is printed only with `--verbose` or `LURP_DEBUG=1`.

**Null fields:** every CLI and MCP JSON response prints every key of its mode; a key with no value is `null`. A yes/no key is `true` or `false`; it is `null` only when the list or check it describes was not produced. The context capsule and the snapshot manifest are separate documents and omit null keys.

**Line-number base:** every emitted line number is **1-based**, matching the `--line=<n>` input convention. A line ends at CR, LF, CRLF, NEL (U+0085), LS (U+2028) or PS (U+2029), as in Roslyn; text that ends with a line break has a last, empty line. This covers edge locations (`impact` hops' `source_line`/`source_end_line`, `diff` `edge_location_changed` details) and declaration locations (`context` capsule `locations`, tier-page `path:start_line`). An edge shows every site of its highest provenance in source order. Impact hops and capsule path hops list up to 20 sites with `site_count`. A reported `start_line` resolves to the same symbol when no other declaration on that line has a smaller span or a better rank; for the other declarations on that line (the parameter properties of a record, the accessors of a property) the answer is the one with the smaller span or the better rank.

**Declaration spans.** Outline, navigation, and symbol locations start at the first attached comment or declaration token and end on the line containing the final token. Byte offsets and end columns mark the exclusive end of the span. Outline's `signature_start_line` is the same as `start_line`, while `name_start_line` identifies the declared name. A field or field-like event stores only its variable declarator span, so the modifiers and type are outside it. An indexer has no identifier name span, so its `name_start_line` equals `start_line`. Use the reported line range with `get-source` or use `get-symbol --view=declaration` for the exact span.

**Column-number base:** unlike line numbers, column numbers are **not** converted to 1-based. `diagnostics`' `start_column`/`end_column` are the raw Roslyn `LinePosition.Character` values and are **0-based**. There is no CLI or MCP input that takes a column, so this only affects how you read the output.

**`--include-generated`:** accepted by `search`, `grep`, `outline`, `get-symbol`, `find-symbol`, `navigate`, `context`, `diagnostics`, and `dead-candidates`. Default excludes generated code decided by one rule: Roslyn's file-name patterns (`TemporaryGeneratedFile_*`, `*.designer.cs`, `*.generated.cs`, `*.g.cs`, `*.g.i.cs`, case-insensitive), `*ModelSnapshot.cs` (case-insensitive), or a single-line or multi-line leading comment before the first token containing `<auto-generated` or `<autogenerated` (case-sensitive); passing the flag includes it.

**Document-path base:** every `--document=<relative-path>` argument (and the matching MCP `document` property) is relative to the directory containing the `--solution=` file, **not** the repository/git root. When the solution lives in a subdirectory of the repo (e.g. `--solution=eNote/eNote.sln`), a git-root-relative path like `eNote/eNote.Tests/Foo.cs` will not resolve — pass `eNote.Tests/Foo.cs` instead. Confirmed 2026-08-20 against a live target: `get-source` fails fast with `ERROR: Document '...' not found in snapshot '<id>'.` on a mismatched path; `dead-candidates --document=` does not error on a mismatch — it's a filter, not a required lookup, so a wrong prefix silently returns `candidate_count: 0` instead of surfacing the mistake. A completeness row for a file outside the solution's git root (for example source-generator output in the build cache, or a NuGet package source) carries a `../` path.

## Modes

Every mode needs a database location: `--output-dir=<path>` (or `LURP_OUTPUT_DIR`), or else `--solution=<path>` (or `LURP_SOLUTION_PATH`), which selects the default cache location (see `--mode=index`). Without one the mode fails. The tables below leave out `--output-dir=`.

A value flag (`--name=`) given more than once is an error. The flags that take a
list may repeat: `--skip-adapter=` and `--affected-project=`.

### `--mode=index`

Index a solution and store facts in the database.

```
--mode=index --solution=<path> [--output-dir=<path>] [options]
```

<!-- generated:flags:index -->
| Argument | Required | Description |
|---|---|---|
| `--solution=<path>` | Yes, or `LURP_SOLUTION_PATH` | Path to the `.sln` or `.slnx` file. |
| `--strategy=<full\|incremental>` | No | `full`: index every document from scratch. `incremental`: re-index changed documents, their reverse-edge closure and type parts; when a document is added, deleted or edited outside member bodies, re-index its project and dependent projects. Default: `full` on first run, `incremental` on subsequent runs. `full` reuses a snapshot with the same id only when a full run built it; `full` is the definition of correctness: use it to recover a known-good index. |
| `--output-json=<path>` | No | Write the selected snapshot manifest as JSON after indexing, including when the run reuses or skips a build. This is a one-way export. |
| `--skip-adapter=<ASP.NET Core\|Dependency Injection\|MediatR\|EF Core\|Serialization\|Test>` | No | Skip a named framework adapter. May be repeated. |
| `--skip-analyzers` | No | Skip running Roslyn analyzer diagnostics during indexing (compiler diagnostics still run). |
| `--skip-diff` | No | Skip computing and persisting the semantic diff against the previous snapshot. `--mode=diff` still computes the diff live. |
| `--verbose` | No | Emit per-extractor timing lines to stderr. |
| `--force` | No | Re-extract even when the deterministic snapshot id already exists as a completed snapshot. The existing snapshot remains readable until its replacement completes. With `--strategy=incremental`, rebuild from the previous snapshot when documents change. A pinned snapshot is not rebuilt and the run stops with an error. User annotations survive `--strategy=full --force`, and annotations whose symbols were deleted are pruned. Does not bypass identity computation: an identical workspace produces the same snapshot id. |
<!-- /generated:flags:index -->

`--output-dir=<path>`: directory where `index.db` is stored. When omitted, `--solution=` (or `LURP_SOLUTION_PATH`) resolves the default cache location: `%LOCALAPPDATA%\lurp\<hash>\` on Windows, `~/.local/share/lurp/<hash>/` on Linux (under `$XDG_DATA_HOME` when it is set). `<hash>` is the first 12 hex characters of the SHA-256 of the canonical solution path. On Windows that path is lower-cased first; on Linux it keeps its case. Also accepted via `LURP_OUTPUT_DIR`.

`--strategy=full` is the definition of correctness for the index; it rebuilds a snapshot that an incremental run built, so it is the recovery mechanism when something looks wrong.

The index summary prints two declaration counters that measure different things. `declarations_extracted_this_run` counts declaration records extracted in this run: a symbol contributes one record per distinct declaration span, so a `partial` type declared in two documents contributes two records, while conditional declarations with an identical span collapse to one. `symbols_in_snapshot` counts the distinct symbols in the snapshot, so a full index reports more extracted records than snapshot symbols when a symbol has more than one distinct span.

Lurp loads projects through MSBuild design-time builds. Those builds regenerate intermediate files, so Lurp redirects them out of the target tree into `obj/<project>/` (per project) under the default cache location above. An index run does not create or change `bin/` or `obj/` files in the target tree, unless the project sets its own intermediate path (the run then reports `intermediate_redirect_not_applied`); restore assets are still read from the real `obj/`. By default the database and capsules are written to the same cache folder. If an explicit `--output-dir=` lies inside the solution directory, Lurp warns on stderr and proceeds. The guarantee is enforced for the packed tool by `TargetTreeIntegrityTests`, which hashes the fixture tree before and after it runs every CLI mode and every MCP tool, and fails if a CLI mode or an MCP tool did not succeed.

**Migrating from the solution-directory default (2.0.0 and earlier):** an old `index.db` next to the solution is no longer found automatically. Point reads at it explicitly with `--output-dir=<old directory>` (or `LURP_OUTPUT_DIR`), or re-index with the new default and delete the old file when convenient.

---

### `--mode=get-source`

Retrieve source text for a document.

```
--mode=get-source --document=<relative-path> --output-dir=<path> [--start-line=<n>] [--end-line=<n>] [--context-lines=<n>] [--snapshot=<id>]
```

<!-- generated:flags:get-source -->
| Argument | Required | Description |
|---|---|---|
| `--document=<relative-path>` | Yes | Relative path of the document within the solution. |
| `--start-line=<n>` | No | 1-based start line for line window (inclusive). |
| `--end-line=<n>` | No | 1-based end line for line window (inclusive). |
| `--context-lines=<n>` | No | Lines of context (symmetric expansion). Requires `--start-line=` or `--end-line=`. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same. |
<!-- /generated:flags:get-source -->

Accepts the shared [read-command options](#read-command-options) except `--output=` (the source is written to stdout verbatim, so freshness is signaled on stderr and via `--require-fresh`'s exit code, never as a JSON wrapper). A `--start-line` after the last line is an error; an `--end-line` after the last line is cut to the last line. There is no CLI `--outline` flag for this mode; the MCP tool `lurp_get_source` offers an `outline` parameter instead.

---

### `--mode=outline`

List declarations in a document with line spans. Linked-file copies of the same span collapse to one row per span, preferring the owning project. Compiler-generated members (record `Equals`, `ToString` and similar) are not listed.

```
--mode=outline --document=<relative-path> --output-dir=<path> [--include-generated] [--limit=<n>] [--cursor=<token>] [--snapshot=<id>]
```

<!-- generated:flags:outline -->
| Argument | Required | Description |
|---|---|---|
| `--document=<relative-path>` | Yes | Relative path of the document within the solution. |
| `--include-generated` | No | Include source-generated declarations. |
| `--limit=<n>` | No | Max declarations per page (default: 100). |
| `--cursor=<token>` | No | Continue from a previous page's `next_cursor`. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same. |
<!-- /generated:flags:outline -->

Also accepts the shared [read-command options](#read-command-options). Output includes `symbol_id`, `kind`, `fully_qualified_name`, `start_line`, `end_line`, `signature_start_line`, `name_start_line`, `is_partial`, `is_generated`, `declaration_count`, and `next_cursor` for pagination. `declaration_count` is the **total** number of declarations matching the filter across every page, not the number of items on the current page — do not use it to drive a pagination loop; use `next_cursor` (a `null` `next_cursor` marks the last page).

---

### `--mode=diagnostics`

List diagnostics captured at index time for the snapshot: compiler diagnostics plus any diagnostics from analyzers referenced by the target project (`Project.AnalyzerReferences`, e.g. built-in SDK analyzers like CA1822). IDE-only code-style rules (e.g. IDE0005) are not included even when the target project's own build enables them (e.g. `EnforceCodeStyleInBuild` plus an `.editorconfig` severity) — confirmed 2026-08-20 against a live target: Roslyn's build-time compilation additionally gates `IDE0005` behind `GenerateDocumentationFile=true`, independent of anything Lurp does. **For unused-using-directive detection, query the compiler diagnostic instead: `--severity=hidden --id=CS8019`** ("Unnecessary using directive") — Lurp always captures this regardless of the target's analyzer/code-style configuration.

```
--mode=diagnostics --output-dir=<path> [--document=] [--project=] [--severity=<level>] [--id=] [--include-generated] [--limit=<n>] [--cursor=<token>] [--snapshot=<id>]
```

<!-- generated:flags:diagnostics -->
| Argument | Required | Description |
|---|---|---|
| `--document=<relative-path>` | No | Filter to diagnostics in this document. |
| `--project=<name>` | No | Filter to diagnostics in this project. |
| `--severity=<Hidden\|Info\|Warning\|Error\|all>` | No | Filter by severity: Roslyn `DiagnosticSeverity` names, matched case-insensitively, or `all` for every severity including `Hidden`. When omitted, `Hidden` diagnostics are excluded. Unknown severity values are rejected with an error instead of returning an empty result. |
| `--id=<code>` | No | Filter by diagnostic ID (e.g. `CS8933`). |
| `--limit=<n>` | No | Max diagnostics per page (default: 100). |
| `--cursor=<token>` | No | Continue from a previous page's `next_cursor`. |
| `--include-generated` | No | Include diagnostics in files matching generated filename patterns, snapshot documents with a leading `<auto-generated` or `<autogenerated` comment, and out-of-snapshot `bin/**` or `obj/**` build outputs. Default excludes them. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same. |
<!-- /generated:flags:diagnostics -->

Also accepts the shared [read-command options](#read-command-options). The response includes `snapshot_id`, `document`, `project`, `severity`, `id`, `include_generated`, `diagnostics` (array with `project_name`, `document_path`, `in_snapshot`, `severity`, `id`, `message`, `start_line`, `start_column`, `end_line`, `end_column`), `diagnostic_count`, and `next_cursor` for pagination. `diagnostic_count` is the **total** number of diagnostics matching the filter across every page, not the number of items on the current page — do not use it to drive a pagination loop; use `next_cursor` (a `null` `next_cursor` marks the last page). Diagnostics reflect compiler and analyzer state at index time, not re-evaluated diagnostics. See the column-number-base note above: `start_column`/`end_column` are 0-based.

`in_snapshot` is `true` when the diagnostic's document is one of the documents tracked by the resolved snapshot, `false` when it resolves outside that set (for example a linked file included via project reference but not itself indexed), and `null` when the diagnostic has no source location at all — a compilation-wide diagnostic (e.g. a missing required argument on the compiler invocation) that Roslyn does not attach to any document.

---

### `--mode=get-symbol`

Look up symbol metadata or source by view kind.

```
--mode=get-symbol --symbol=<id> --view=<kind> --output-dir=<path> [options]
```

<!-- generated:flags:get-symbol -->
| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | Yes | The symbol to look up. Pass the full 'docCommentId\|assemblyIdentity' id, a doc-comment id (T:Some.Type), a fully qualified name (Some.Namespace.Type), or a generic name without its type parameters (Some.Namespace.Repo for Repo<T>). More than one match is an error. |
| `--view=<metadata\|signature\|body\|declaration\|containing-type\|surrounding>` | Yes | What to return. |
| `--context-lines=<n>` | No | Lines of context for `--view=surrounding` (default: 3). |
| `--include-generated` | No | Allow resolving a symbol whose declarations are all source-generated, and include generated declarations in source views (`--view=metadata` always lists every declaration with its own `is_generated`). |
| `--snapshot=<id>` | No | Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same. |
<!-- /generated:flags:get-symbol -->

Accepts the shared [read-command options](#read-command-options) except `--output=`. `--view=metadata` embeds the `freshness` block in its JSON payload and a `locations` array (`{ document_path, start_line, start_column, end_line, end_column, is_generated }` per declaration, lines 1-based). `locations` lists every declaration of the symbol in the snapshot, each with its own `is_generated`, so `declaration_count` equals the number of entries; `--include-generated` decides only whether a symbol whose declarations are all generated resolves. The five source views write source bytes verbatim, so their freshness signal is the stderr line plus `--require-fresh`'s exit code; the source views read generated declarations only with `--include-generated`. `metadata` is an object with the extractor's snake_case metadata fields (for example `accessibility`, `is_static`, `return_type`). For a primary constructor, `declaration` and `signature` print the parameter list, and `body` fails with `ERROR: Source not found for symbol '<id>' with view 'body'.` because the constructor has no body. `outline` and `navigate` give the type name as its name position.

---

### `--mode=search`

Full-text search over source text and symbols.

```
--mode=search --query=<term> --output-dir=<path> [options]
```

<!-- generated:flags:search -->
| Argument | Required | Description |
|---|---|---|
| `--query=<term>` | Yes | Search term. Symbol search ranks exact simple names, name tokens or prefixes, simple-name substrings, then containing names or fully qualified names. Within each tier, production symbols sort first, with types ahead of members. `--cursor` continues the ranked list. |
| `--type=<all\|source\|symbol>` | No | Search scope (default: `all`). An unknown value is an error. |
| `--kind=<Type\|Method\|Property\|Field\|Event>` | No | Filter symbol results by kind (case-insensitive). An unknown value is an error. |
| `--limit=<n>` | No | Max results per scope (default: 20). |
| `--snippet-tokens=<n>` | No | Tokens in the window around the first match for source snippets (default: 64). The window always holds the whole match. |
| `--cursor=<token>` | No | Continue from a previous page's `next_cursor` (`--type=symbol` only). The cursor continues across all symbol match tiers. |
| `--include-generated` | No | Include source-generated symbols. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same. |
<!-- /generated:flags:search -->

Punctuation in queries is quoted as an FTS5 phrase literal (`SearchUtils.ToFtsPhrase`), so dotted names like `CourseService.CreateAsync` and queries containing `"`, `*`, `:`, `()`, `<>` no longer throw `fts5: syntax error`.

A source result holds the first match in the document text. The `snippet` window uses `--snippet-tokens` tokens (runs of letters and digits) around that match. The window always holds the whole match, even if the match uses more tokens. The `<mark>` and `</mark>` tags surround the match. The `…` character marks a cut end.

Also accepts the shared [read-command options](#read-command-options).

---

### `--mode=grep`

Literal/exact-text search over source content with per-occurrence line numbers.

```
--mode=grep --query=<term> --output-dir=<path> [options]
```

<!-- generated:flags:grep -->
| Argument | Required | Description |
|---|---|---|
| `--query=<term>` | Yes | Exact substring to find. The match is byte-exact, including punctuation and spacing. Case-sensitive by default; pass `--ignore-case` for case-insensitive. |
| `--limit=<n>` | No | Max matches per page (default: 50). |
| `--cursor=<token>` | No | Continue from a previous page's `next_cursor`. |
| `--ignore-case` | No | Make the search case-insensitive (default: case-sensitive). |
| `--include-generated` | No | Include source-generated documents. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same. |
<!-- /generated:flags:grep -->

Also accepts the shared [read-command options](#read-command-options).

Output is JSON with `snapshot_id`, `query`, `ignore_case`, `results` (array of `{ document_path, start_line, start_column, end_line, end_column, line_text }` — lines 1-based, columns 0-based), `match_count` (total matches across every page, not the page size), and `next_cursor`. `match_count` is the total, not the page length — do not use it to drive a pagination loop; use `next_cursor` (a `null` `next_cursor` marks the last page). Each result is one exact occurrence, ordered by `document_path` then `start_line`/`start_column`, so the same `start_line` can be passed to `get-source --start-line=` or `navigate --file= --line=` to re-resolve the location. Occurrences do not overlap: the search resumes after the end of each match, so `match_count` equals the number of replacements of a replace-all. `--ignore-case` compares the invariant upper case of each character (`OrdinalIgnoreCase`), so non-ASCII letters match.

---

### `--mode=find-symbol`

Resolve a symbol by full symbol ID, doc-comment ID, fully qualified name, or generic name without type parameters.

```
--mode=find-symbol --symbol=<name> --output-dir=<path> [options]
```

<!-- generated:flags:find-symbol -->
| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | Yes | The symbol to look up. Pass the full 'docCommentId\|assemblyIdentity' id, a doc-comment id (T:Some.Type), a fully qualified name (Some.Namespace.Type), or a generic name without its type parameters (Some.Namespace.Repo for Repo<T>). More than one match is an error. |
| `--include-generated` | No | Allow resolving a symbol whose declarations are all source-generated (`locations` always lists every declaration with its own `is_generated`). |
| `--snapshot=<id>` | No | Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same. |
<!-- /generated:flags:find-symbol -->

Also accepts the shared [read-command options](#read-command-options).

`declaration_count` is scoped to the requested snapshot, so a non-partial type reports `1` however many historical declarations retention still holds; a partial type reports its true multiplicity in that snapshot.

The payload carries a `locations` array: one entry per declaration in the snapshot, each `{ document_path, start_line, start_column, end_line, end_column, is_generated }` (lines 1-based). This answers "where is X defined?" in one call, without a capsule. `locations` lists every declaration of the symbol in the snapshot, each with its own `is_generated`. `declaration_count` equals the number of entries. `--include-generated` decides only whether a symbol whose declarations are all generated resolves. In `--output=summary`, the first location prints as `path:start_line`. `--output=jsonl` prints `{"type":"meta","meta":{...}}` (`snapshot_id`, `freshness`), then one `{"type":"symbol","symbol":{...}}` record. `metadata` is an object with the extractor's snake_case metadata fields (for example `accessibility`, `is_static`, `return_type`).

---

### `--mode=navigate`

Resolve an indexed declaration by file and line.

```
--mode=navigate --file=<path> --line=<n> --output-dir=<path> [options]
```

<!-- generated:flags:navigate -->
| Argument | Required | Description |
|---|---|---|
| `--file=<path>` | Yes | Source file path relative to the solution root. |
| `--line=<n>` | Yes | 1-based line number in the source file. Every emitted line number is 1-based too, so a reported `start_line` feeds straight back into `--line=`. |
| `--include-generated` | No | Include source-generated declarations. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same. |
<!-- /generated:flags:navigate -->

Accepts the shared [read-command options](#read-command-options) except `--output=`; the JSON payload embeds the `freshness` block.

Returns the innermost declaration whose span intersects the line (equal spans: not compiler-made first, then type, constructor, other member, accessor, then the owning project's document, then symbol id), with the symbol ID, the document path and version, and the exact source span. For its name and kind, pass `target.symbol_id` to `--mode=find-symbol` or `--mode=get-symbol --view=metadata`. The `target` carries both the character offsets (`full_start`/`full_end`/`name_start`/`name_end`, the exact-span contract) and 1-based `start_line`/`end_line`; a reported `start_line` resolves to the same symbol when no other declaration on that line has a smaller span or a better rank; for the other declarations on that line (the parameter properties of a record, the accessors of a property) the answer is the one with the smaller span or the better rank.

---

### `--mode=diff`

Show semantic changes between two snapshots.

```
--mode=diff --from-snapshot=<id> --to-snapshot=<id> --output-dir=<path>
```

<!-- generated:flags:diff -->
| Argument | Required | Description |
|---|---|---|
| `--from-snapshot=<id>` | Yes | Base snapshot ID. |
| `--to-snapshot=<id>` | Yes | Target snapshot ID. |
<!-- /generated:flags:diff -->

Output is JSON: `from_snapshot`, `to_snapshot`, `change_count`, `skipped_comparisons` and `changes[]` (`change_id`, `change_type`, `symbol_id`, `detail`, `created_at_utc`). The fields of `detail` depend on `change_type`: `edge_added` and `edge_removed` carry `source`, `target` and `kind`; `edge_location_changed` carries `source`, `target`, `kind`, `before` and `after` spans; for `attribute_changed`, an absent `attributes` key means an empty list. `--output=` is not accepted.

---

### `--mode=impact`

Trace the impact of a changed symbol as a set of reached symbols.

```
--mode=impact --symbol=<id> --output-dir=<path> [options]
```

<!-- generated:flags:impact -->
| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | Yes | The symbol to look up. Pass the full 'docCommentId\|assemblyIdentity' id, a doc-comment id (T:Some.Type), a fully qualified name (Some.Namespace.Type), or a generic name without its type parameters (Some.Namespace.Repo for Repo<T>). More than one match is an error. |
| `--direction=<downstream\|upstream>` | No | Traversal direction (default: `downstream`). Both directions exclude `Declares`, `Contains` and `TestedBy` by default. Use `--kinds=` to supply a different set. |
| `--kinds=<Inherits\|Implements\|References\|Contains\|Overrides\|Hides\|ExtensionReceiver\|Calls\|MethodGroupRef\|Constructs\|Reads\|Writes\|Returns\|Throws\|Declares\|MayDispatchTo\|StaticallyCalls\|RoutesTo\|Registers\|Handles\|MapsTo\|TestedBy\|ReflectionTypeRef\|ReflectionMemberRef\|ReflectionNameCandidate\|ReflectionTargetUnknown>` | No | Comma-separated edge kinds to follow, matched case-insensitively. An unknown kind is rejected with an error. An empty value or an empty item (`Calls,`) is an error. Replaces the default set for the direction (see `--direction=`). |
| `--provenance=<compiler_proved\|framework_derived\|possible\|convention\|name_candidate\|runtime_unknown>` | No | Comma-separated provenance values to follow (e.g. `compiler_proved,framework_derived`). Pass `compiler_proved` to follow only compiler-verified edges: direct interface implementations, virtual/override `MayDispatchTo`, `Calls`, `MethodGroupRef`, `Constructs`, `Implements`, `Inherits`, `Overrides`. Excludes framework-derived DI (`Registers`), the name-candidate and unresolved-reflection edges (`ReflectionNameCandidate`, `ReflectionTargetUnknown`), and inherited-only dispatch edges. `framework_derived` also follows adapter-emitted edges, including middleware `Calls`/`Constructs`. Values are matched case-insensitively; an unknown value is rejected with an error. An empty value or an empty item is an error. |
| `--max-depth=<n>` | No | Maximum traversal depth (default: 3). |
| `--limit=<n>` | No | Symbols per page (default: 50). When more exist, the response carries `truncated.{reason,returned,total,remaining,cursor}`. |
| `--cursor=<token>` | No | Continue from a previous page's `truncated.cursor`. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same. |
<!-- /generated:flags:impact -->

Also accepts the shared [read-command options](#read-command-options).

Every response carries `symbols`: the reached symbols, each with its depth, `shortest_path_count`, a `frontier` flag and a deterministic `witness_path` from the anchor (each hop includes `site_count` and up to 20 ordered `sites`). `groups` buckets those symbols by the first hop of their witness path, computed over *all* symbols before the page is cut, so the fan-out summary stays complete even when the symbol list is truncated; equal groups sort by the first-hop neighbor id: the target downstream, the source upstream. `semantic_causes` sits at the top level because it depends only on the anchor. `shortest_path_count` counts shortest symbol routes: parallel edges between two symbols (such as `Calls` and `StaticallyCalls` on a static call site) count once.

When a downstream traversal excludes `Declares`, the members of a type anchor are additional starting points at depth zero. A property or event anchor always adds its accessors as depth-zero starting points, whatever the edge-kind set. A witness can start at one of those points. For property and event accesses, a witness can cross to an accessor body while keeping the recorded endpoints and source location of each edge.

Top-level fields: `snapshot_id`, `freshness`, `symbol_id`, `direction`, `max_depth`, `symbol_count_total` (reached symbols on all pages; the anchor is not included), `frontier_count` (reached symbols at `--max-depth` that have followable edges beyond their witness path), `offset`, `groups`, `semantic_causes`, `truncated` (`null`, or `{reason:"limit",returned,total,remaining,cursor}`) and `symbols`. Symbols come in depth order, then symbol ID, and the cursor pages over that order. A cursor from a 1.x or 4.1.0 build is rejected.

`--output=summary` prints a header line, then `symbols: <total> total, <returned> in this page (offset N); <groups> distinct first hop(s); <frontier> at max depth`, one row per group with display names, and the truncation hint. `--output=jsonl` prints one `{"type":"meta","meta":{...}}` record (the response without `symbols`), then one `{"type":"symbol","symbol":{...}}` record per symbol on the page.

---

### `--mode=context`

Assemble a context capsule for a symbol or source location.

```
--mode=context (--symbol=<id> | --file=<path> --line=<n>) --output-dir=<path> [options]
```

<!-- generated:flags:context -->
| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | Yes* | The symbol to look up. Pass the full 'docCommentId\|assemblyIdentity' id, a doc-comment id (T:Some.Type), a fully qualified name (Some.Namespace.Type), or a generic name without its type parameters (Some.Namespace.Repo for Repo<T>). More than one match is an error. |
| `--file=<path>` | Yes* | Source file path (use with `--line`). |
| `--line=<n>` | Yes* | Line number in the source file. |
| `--intent=<inspect\|modify\|diagnose>` | No | Intent hint for assembly (default: `inspect`). |
| `--content-budget=<n>` | No | Token budget for capsule content (default: 8000, or 16000 when the anchor resolves to a type (from `--symbol=` or `--file=`/`--line=`) and `--content-budget=` is omitted: a type's callee/caller tiers scale with member fan-out, so the default is kind-aware. An explicit `--content-budget=` is always honored as-is). Even at 16000, a large type anchor can still exhaust the budget before its lowest-priority tiers are reached (typically `relevant_tests` and `second_degree_context`). That is not a failure to budget away: refetch those tiers on their own with `--tier=`, e.g. `lurp --mode=context --symbol=<symbol-id> --tier=relevant_tests` (see `--tier=` below). Reported as `estimated_tokens`: anchor and item source plus the serialized weight of the substantive non-source sections (paths, semantic causes, constraints, topology, completeness, verification, likely change sites, affected public surfaces, inclusion reasons). `uncertainties` is appended after this pass, so it is not counted and is never trimmed. Per-item identity/provenance framing is navigation metadata and is not counted, so the emitted file is larger than `estimated_tokens`: size a context window from `estimated_artifact_tokens`. Over-budget capsules first keep only the first whole paths and bound item source (recorded as `summarized`), then clear the lowest-priority sections greedily (`budget_exhausted`); `suggested_verification` is measured and can be cleared this way, while `uncertainties` is not. Every truncated category is declared in `omitted_tiers`. The anchor is never dropped. |
| `--max-hops=<n>` | No | Maximum graph hops to expand (default: 3). |
| `--scope=<label>` | No | Logical scope label recorded on the anchor (default: the symbol ID). |
| `--affected-project=<name>` | No | Repeatable. A project affected by the change. May be repeated. |
| `--tier=<contracts\|direct_callees\|direct_callers\|registered_implementations\|relevant_tests\|second_degree_context\|surrounding_source>` | No | Fetch ONE tier on its own instead of a capsule, with no token budget applied: this is how a capsule's `omitted_tiers` `budget_exhausted` entry is acted on. |
| `--tier-limit=<n>` | No | Items per tier page (default: 25). |
| `--cursor=<token>` | No | Continue a tier from its `next_cursor` (`--tier` only). |
| `--completeness-detail` | No | Emit per-document `binding_incompleteness` rows. Without it, completeness carries a deterministic reason/project rollup (`binding_incompleteness_summary`) plus the total. |
| `--include-generated` | No | Include source-generated symbols. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same. |
<!-- /generated:flags:context -->

\* Either `--symbol` or both `--file` and `--line` must be provided.

Also accepts the shared [read-command options](#read-command-options).

The capsule is always written to `<output-dir>/capsule-<sanitized-id>.json` and also printed to stdout. Long symbol IDs are shortened with a stable hash suffix, so the file name stays short on every OS; `--quiet` and `--output=summary` replace the stdout copy, never the file. The top-level JSON records its origin (`snapshot_id`, `tool_version`, `output_schema_version`), so a file found on disk can be checked against `status`. The write is atomic (a sibling `.tmp` file is moved over the target), so a failed run leaves the previous file intact and a reader never sees a partial one.

#### Paths, topology and tier order

`incoming_paths` and `outgoing_paths` hold **witness paths**, not every simple path. Lurp keeps a deterministic witness for each symbol reached within `--max-hops`. A property or event can be expanded through different accessors when the traversal reaches it through different access modes. A capsule emits the witness path of each reached symbol that no other emitted path passes through, so no emitted path is a prefix of another. A path that ends at `--max-hops` with followable edges beyond it carries `truncated: true` and `truncation_reason: "max depth reached"`. Over the budget, the capsule keeps the first three paths whole, in this order, and drops the rest (`summarized`). It never cuts a path.

Both path directions use the default impact filter, which excludes `Declares`, `Contains` and `TestedBy`. Outgoing paths start from the anchor and, for a type, property or event, its depth-zero members or accessors.

`topology.current` reports the reach before the budget trim: `incoming_symbol_count` and `outgoing_symbol_count` (reached symbols), `incoming_witness_path_count` and `outgoing_witness_path_count` (witness paths), and `total_hop_count` (hops over those witness paths). `--output=summary` prints the trimmed counts as `incoming_witness_paths` / `outgoing_witness_paths`.

`semantic_causes` lists the anchor's persisted semantic changes in the capsule's snapshot (`from_snapshot_id`, `to_snapshot_id`, `change_type`, `symbol_id`, `detail`) once, at the top level; paths carry none. Over the budget the list is cut to 10, and `omitted_tiers` names `semantic_causes` as `summarized`.

`direct_callers` contains callers and framework entry points (`RoutesTo`, `Handles`) at depth one.
`second_degree_context` contains callers from depth two through `--max-hops`.
Calls, constructions, method-group references, reads and writes count at each hop, and a
framework entry point is kept at every hop it is found.
A `RoutesTo` source is a Route item: it has no source text or declaration location, and its
`route://` id is not a stored symbol, so `--symbol=` rejects it.
Access through an interface or virtual method, property or event adds a dispatch
hop when the access can dispatch. `relevant_tests` uses the shortest distance
to a covered symbol. A type capsule omits callers inside the type and its
nested types. Each tier sorts by depth, then by symbol ID.
Start a new `--tier=` page sequence after upgrading to 4.2.0.

#### Capsule token estimates

A capsule reports two different numbers, and they are not interchangeable:

| Field | What it measures | Use it for |
|---|---|---|
| `estimated_tokens` | **Content only**: anchor and item source plus the serialized weight of the substantive non-source sections (paths, semantic causes, constraints, topology, completeness, suggested verification, likely change sites, affected public surfaces, inclusion reasons). Per-item identity/provenance framing (symbol IDs, fully-qualified names, edge kinds, provenance, coordinates) is navigation metadata and is not counted. | Understanding what `--content-budget` bounded. This is the budget basis. |
| `estimated_artifact_tokens` | The **whole emitted file** (serialized length ÷ 3), framing included. | Sizing a context window. |

Both numbers are a model-neutral, character-based size hint (characters ÷ 3).
They are not a token count for any model: Lurp does not know which model reads
the capsule and ships no tokenizer. The divisor 3 was chosen once, from
measurements on eNoteV2 capsules (whole file, characters per token): OpenAI
`o200k_base` 3.82–4.02, OpenAI `cl100k_base` 3.86–4.08, DeepSeek V4.1-Flash
3.17–3.51. Claude was not measured. 3 is below every measured value, so the
hint errs large, which is the safe side when you size a context window. The
model provider counts the real tokens.

`estimated_artifact_tokens` is always the larger of the two, typically by a wide
margin on capsules with many small items. It is reported, never budgeted
against: budgeting on the whole serialization was measured to force dropping
whole tiers (`direct_callees`, `registered_implementations`, `surrounding_source`)
at realistic budgets, which is a worse capsule for the same context cost.

Population order decides what the budget sees. `suggested_verification` is
assembled before the budget pass, so it counts toward `estimated_tokens` and
can be dropped to fit (`omitted_tiers`, category `suggested_verification`,
reason `budget_exhausted`). When one test class contributes more than one
covering test, the section carries one entry per class with a
`dotnet test --filter "FullyQualifiedName~<Class>"` command instead of one
entry per test; a class with a single test keeps its exact
`FullyQualifiedName=<Test>` entry, and running the class filter runs every
test it holds. Each command names the indexed project of the test. Without a stored project path (a snapshot from before 4.0.0), it names the indexed solution. `uncertainties` is assembled after the budget pass: it is
neither counted against `--content-budget` nor trimmed.
`estimated_artifact_tokens` is stamped last, over the emitted file, so it
includes both sections.

The same two numbers are restated under an advisory `token_estimate` block:
`budget_basis` mirrors `estimated_tokens` and `delivery` mirrors
`estimated_artifact_tokens`, so a consumer reading only the JSON payload can
distinguish the budget basis from the delivery size without the surrounding
prose.

#### Reading `omitted_tiers`

`omitted_tiers` carries **exactly one terminal record per category**, describing
the emitted capsule rather than the history of how it settled. Reasons:

| Reason | Meaning |
|---|---|
| `empty` | Proved absence. The relation was observable and there is none. Safe to act on. |
| `unresolved` | Lurp could not observe the relation. A gap capsule marks every tier this way. |
| `unmodeled_construct` | The tier is empty because an unmodeled registration form exists (see Declared boundaries). It is not proof that no relation exists. |
| `summarized` | Present but bounded: only the first paths kept, or item source truncated at the per-item cap. |
| `budget_exhausted` | Bounded by budget. **With items still present in the section**, the included items are a complete greedy prefix of the tier in its relevance order. **With no items**, the tier was fully omitted. |

For contracts, direct callees and surrounding source, Lurp checks for binding
loss in the anchor documents. For callers, relevant tests, registered
implementations and affected public surfaces, it checks the anchor project and
all projects that reference it directly or transitively. If the binding reader
is unavailable, Lurp cannot confirm absence in either scope. A binding loss
with no document path covers its whole project. If Lurp cannot recover that
project's documents, the loss also makes the anchor documents unobservable.
Assembly names match without regard to case.

Both `budget_exhausted` shapes are recovered the same way: refetch that one tier
unbudgeted with `--tier=<category>` (see `inclusion_reasons["omitted_tiers_budget_exhausted"]`,
which is retained in the capsule even when budget pressure clears every other
inclusion reason).

A missing section is not the same as an empty one. When the enforcer drops
`topology` or `completeness` they are omitted from the JSON entirely rather than
serialized with zeroed counts, because zeroed counts read as a positive claim
("no incoming references") that the capsule has not established. The
corresponding `omitted_tiers` record is the authority.

#### Regeneration is bounded by snapshot retention

A capsule is byte-for-byte regenerable from the snapshot it was built against,
but only while that snapshot is retained: `--mode=index` prunes to the 3 newest
complete snapshots. Once the source snapshot is pruned, the artifact can no
longer be reproduced or re-verified against the store. A snapshot pinned via
[`--mode=pin-snapshot`](#--modepin-snapshot) is excluded from pruning
regardless of recency, so pinning a snapshot also extends how long any capsule
built against it stays reproducible.

---

### `--mode=status`

Show the current database status.

```
--mode=status --output-dir=<path> [options]
```

<!-- generated:flags:status -->
| Argument | Required | Description |
|---|---|---|
| `--solution=<path>` | No | If provided, it must be an existing file; compares the current workspace against the latest snapshot. Every flag is checked before the database is read. |
| `--detail=<documents\|references\|completeness\|all>` | No | Comma-separated sections to expand in `--json` output: `documents` restores the per-document version map, `references` the full metadata reference identities, `completeness` the per-document binding-incompleteness rows. Each is summarized by default; `all` expands every section. Unknown values are an error. |
| `--json` | No | Emit structured JSON instead of plain text. Back-compat alias for `--output=json`. |
| `--output=<summary\|json>` | No | Payload rendering (default: `summary` when neither flag is given). `jsonl` is rejected — the payload is a single document. |
| `--max-mismatches=<n>` | No | Cap on the `mismatches` list in `--json` output (default: 50; positive integer). |
<!-- /generated:flags:status -->

When the latest snapshot failed, `latest_failure` has `snapshot_id`, `reason_code`, `message` and `created_at_utc`; when no snapshot failed, the key is `null` in every branch, the never-indexed branches included. The text output prints a `Latest failed index:` line when a snapshot failed. A failed snapshot is not reported when a later index run succeeded (the selected latest snapshot was selected at or after it).

`status` never migrates the database. When the database schema version is not the one this build needs, `status` still exits 0: the text output prints both versions and the `--mode=index` hint, and `--json` returns the same key set with `schema_version_expected`, `schema_version_mismatch` and `schema_version_note` (`indexed` and `latest_failure` are `null`; see [Schema version of the database](#schema-version-of-the-database)).

`status --json` prints the same keys in every branch; a key with no value in a branch is `null` (`indexed` is `null` in the schema-mismatch branch, `false` when not indexed, and `true` when indexed; `schema_version_mismatch` is `true` in the schema-mismatch branch, `null` when no database exists, and `false` otherwise; `freshness_checked` is `true` only when checked against a workspace; `latest_failure` is `null` in the schema-mismatch branch).

The manifest always carries a completeness block (`binding_incompleteness_summary`
plus `binding_incompleteness_total`); `--detail=completeness` (or `all`) adds the
per-document `binding_incompleteness` rows. This matches the MCP `lurp_status`
`sections=completeness` behavior. MCP `lurp_status` `sections` uses the same union —
`freshness`, `manifest`, `documents`, `references`, `completeness` or `all` — and an
unknown value is a tool error (`isError: true`).

Batch document freshness — checking a specific list of documents against the
snapshot, the MCP `lurp_status` `documents`, `projects` and `max_bytes`
parameters — has **no CLI equivalent**: `--documents=` is not a flag of
`--mode=status` (and would be rejected as unknown). To check one document from the CLI, read its freshness via
a read mode's stderr line / `--require-fresh` exit code instead.

---

### `--mode=timings`

Show step-by-step timing data for a snapshot.

Without `index.db` it fails with exit 1 and `ERROR: Index database not found at <path>`,
and a database whose pin points at an incomplete snapshot prints the usual `WARNING:`
line on stderr.

```
--mode=timings --output-dir=<path> [options]
```

<!-- generated:flags:timings -->
| Argument | Required | Description |
|---|---|---|
| `--snapshot=<id>` | No | Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same. |
| `--json` | No | Emit structured JSON instead of plain text. |
<!-- /generated:flags:timings -->

`--json` always prints `snapshot_id`, `total_ms`, `steps` and `metrics`; with no timing rows `total_ms` is 0 and `steps` is empty. `total_ms` and `percent` are computed from timing rows only. `peak_working_set_bytes` is the exact peak working set of the Lurp process; `peak_working_set_mb` is the same value rounded up to a whole MiB. Both cover the Lurp process only; MSBuild BuildHost child processes are not counted. Text output prints a Metrics section below the table. The MCP `lurp_timings` tool returns the same `metrics` object for the session-pinned snapshot.

---

### `--mode=annotate`

Attach a user-authored annotation to a symbol.

```
--mode=annotate --symbol=<id> --annotation-kind=<kind> --value=<text> --output-dir=<path> [options]
```

<!-- generated:flags:annotate -->
| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | Yes | The symbol to look up. Pass the full 'docCommentId\|assemblyIdentity' id, a doc-comment id (T:Some.Type), a fully qualified name (Some.Namespace.Type), or a generic name without its type parameters (Some.Namespace.Repo for Repo<T>). More than one match is an error. |
| `--annotation-kind=<kind>` | Yes | Annotation kind. |
| `--value=<text>` | Yes | Annotation value. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same. |
<!-- /generated:flags:annotate -->

User annotations persist across full and `--strategy=full --force` re-indexes: a new snapshot copies them forward. An annotation whose symbol no longer exists in the new snapshot is pruned when symbols are removed. Extractor-derived annotations are written again from the new snapshot.

---

### `--mode=get-annotations`

Retrieve annotations by symbol, document, or snapshot (with kind filtering and keyset pagination).

```
--mode=get-annotations (--symbol=<id> | --document=<path>) --output-dir=<path> [--kind=<kind>] [--limit=<n>] [--cursor=<token>] [options]
```

<!-- generated:flags:get-annotations -->
| Argument | Required | Description |
|---|---|---|
| `--symbol=<id>` | No | The symbol to look up. Pass the full 'docCommentId\|assemblyIdentity' id, a doc-comment id (T:Some.Type), a fully qualified name (Some.Namespace.Type), or a generic name without its type parameters (Some.Namespace.Repo for Repo<T>). More than one match is an error. Mutually exclusive with `--document=`. |
| `--document=<relative-path>` | No | Relative path of the document within the solution. Returns the annotations whose `document_path` is that document and the annotations of every symbol declared in it, user annotations included. Mutually exclusive with `--symbol=`. |
| `--kind=<kind>` | No | Filter to one annotation kind. |
| `--limit=<n>` | No | Max annotations per page (default: 100; positive integer). |
| `--cursor=<token>` | No | Continue from a previous page's `next_cursor`. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same. |
<!-- /generated:flags:get-annotations -->

Accepts the shared [read-command options](#read-command-options). `--symbol=` and
`--document=` are enforced as mutually exclusive. `--document=` returns the annotations
whose `document_path` is that document and the annotations of every symbol that has a
declaration in it, user annotations included. With neither, all annotations in the
snapshot are returned.

The JSON payload is `{ snapshot_id, symbol_id, document, kind, annotations,
annotation_count, next_cursor, freshness }`; each annotation is `{ annotation_id, symbol_id,
kind, value, document_path }`. `annotation_count` is the **total** number of
annotations matching the filter across every page, not the number of items on the
current page — do not use it to drive a pagination loop; use `next_cursor` (a
`null` `next_cursor` marks the last page). When more results remain than
fit on the page, `next_cursor` is set and `--output=summary` prints
`-- <shown>/<total> annotation(s); more available (--cursor)`. `--cursor` tokens
are snapshot-scoped continuation tokens, not raw offsets. Each annotation now carries `annotation_id` (surrogate PK) so a caller can address one row with `--mode=retract-annotation`.

---

### `--mode=retract-annotation`

Retract one annotation by surrogate id, scoped to exactly one snapshot. The delete is by `annotation_id` alone — not filtered by provenance — so it removes a user-authored (`--mode=annotate`) row or an extractor-derived one the same way. This is the supported way to correct or remove a mistaken `--mode=annotate` entry, or to drop a bad extractor-derived annotation, without manual SQL.

```
--mode=retract-annotation --annotation-id=<n> --output-dir=<path> [--snapshot=<id>]
```

<!-- generated:flags:retract-annotation -->
| Argument | Required | Description |
|---|---|---|
| `--annotation-id=<n>` | Yes | Surrogate PK from `get-annotations` annotation_id (positive integer). |
| `--snapshot=<id>` | No | Snapshot to act on (default: latest, or the pinned snapshot when one is set). The DELETE is `WHERE snapshot_id=@snapshot AND annotation_id=@id` — one row only, no cross-snapshot effect. Copy-forward clones (`CopyAnnotationsToSnapshot`) allocate fresh ids, so retracting in one snapshot does not delete its clone in another. |
<!-- /generated:flags:retract-annotation -->

JSON output is `{ status:"ok", snapshot_id, annotation_id, retracted:true }`. On no such row in that snapshot the mode exits 1 with `ERROR: annotation_id <n> not found in snapshot '<id>'.` Use `--snapshot=` explicitly when you have more than one snapshot pinned.

---

### `--mode=dead-candidates`

List dead-code candidates: symbols with no incoming LIVE edge after suppression-ladder filtering.

```
--mode=dead-candidates --output-dir=<path> [--project=<name>] [--document=<path>] [--kind=<Type|Method|Property|Field|Event>] [--limit=<n>] [--cursor=<token>] [--snapshot=<id>] [--include-public] [--include-generated] [--include-tests]
```

<!-- generated:flags:dead-candidates -->
| Argument | Required | Description |
|---|---|---|
| `--project=<name>` | No | Filter to this project (assembly simple name, e.g. `eNote.API`). |
| `--document=<relative-path>` | No | Filter to symbols declared in this document (forward-slash, relative to the solution root, not the repo root). |
| `--kind=<Type\|Method\|Property\|Field\|Event>` | No | Filter by symbol kind (case-insensitive). |
| `--limit=<n>` | No | Max dead candidates per page (default: 50, max: 200). |
| `--cursor=<token>` | No | Continue from a previous page's `next_cursor`. |
| `--snapshot=<id>` | No | Snapshot to use (default: latest, or the pinned snapshot when one is set; see `--mode=pin-snapshot`). The literal `latest` means the same. |
| `--include-public` | No | Include public/protected symbols with no internal LIVE incoming edge as `uncertain_dead` (reason: `public_surface`). Default excludes them from `proved_dead`. |
| `--include-generated` | No | Include source-generated symbols (default excludes symbols whose every declaration is generated). Surfaced as `uncertain` reason `generated_excluded` when no other signal. |
| `--include-tests` | No | Include test-project symbols (a project that references a test framework: Microsoft.VisualStudio.TestPlatform.ObjectModel, Microsoft.Testing.Platform, xunit.core, xunit.v3.core, nunit.framework, Microsoft.VisualStudio.TestPlatform.TestFramework, MSTest.TestFramework, TUnit.Core) with no LIVE incoming edge as `uncertain` (reason: `test_harness`). Default excludes them. Without it, test helpers reached only by a test framework would all appear dead. |
<!-- /generated:flags:dead-candidates -->

Also accepts the shared [read-command options](#read-command-options).

<!-- generated:dead-candidates:ladder -->
symbols of kind Type, Method, Property, Field, Event with no incoming LIVE edge (Calls, MethodGroupRef, Constructs, Reads, Writes, Handles, RoutesTo, Registers, MapsTo, MayDispatchTo, StaticallyCalls, TestedBy, ReflectionTypeRef, ReflectionMemberRef, ReflectionNameCandidate) of strong provenance (compiler_proved, framework_derived). Inherited liveness: accessors take their property's or event's edges by role (a getter every edge except Writes; a setter or init only Writes; an event accessor every edge of the event); type candidates also take these edges from outside the type: References, Returns, Inherits, Implements, Throws. A linked declaration also takes incoming edges from its copies in other projects, including when a project filter is set. Suppression ladder, in the order the store checks it: (1) a strong LIVE edge from outside the candidate -> alive, not a candidate. An edge never counts when its source is the candidate, an accessor of the candidate, the property or event of the candidate accessor or another accessor of it, or (for a type) a symbol inside the type; (2) the candidate overlaps a binding_incompleteness row of an unobservable reason (compiler_error, unresolved_metadata, ambiguous_overload, unsupported_syntax, extractor_failure, project_unreadable) -> unresolved, reason binding_incompleteness; (3) the process entry point or the type that contains it (explicit static Main or the compiler-synthesized top-level-statements form; nothing in the repository calls it, whatever its accessibility) -> uncertain_dead, reason entry_point_convention; (4) implementation of an interface member or override of a member declared outside the compilation -> uncertain_dead, reason external_interface_implementation; (5) public surface: Public, Protected or protected internal by effective accessibility, which intersects declared accessibility with every containing type. Declared accessibility is used if effective accessibility is unavailable; an internal or private protected symbol of a project that grants InternalsVisibleTo to an assembly outside the snapshot (or to an assembly that the index cannot name) is public surface too -> excluded from proved_dead without include_public, uncertain_dead with reason public_surface when include_public is set; (6) a non-abstract, non-static class or delegate (or an instance constructor of one) of an assembly that an unmodeled dependency injection scan names -> unresolved, reason binding_incompleteness; (7) a generated symbol (every declaration is in a generated document) with include_generated -> uncertain, reason generated_excluded; (8) symbol of a test project with include_tests -> uncertain, reason test_harness; (9) when the candidate has only weak incoming edges, the strongest weak edge decides: possible provenance -> uncertain_dead, reason possible_dispatch; convention provenance -> uncertain_dead, reason framework_convention; name_candidate provenance -> uncertain_dead, reason name_candidate; runtime_unknown provenance -> unresolved, reason runtime_unknown; (10) when the candidate has no incoming LIVE edge at all, its own convention verdict: EF mapping sources, fields and properties (including accessors) of mapped entities and their base types, and instance constructors of mapped entities and EF mapping source types -> uncertain_dead, reason ef_convention; a field, property or method with a serialization opt-in (such as JsonInclude, JsonProperty, DataMember, JsonConstructor, or a serialization callback attribute) -> uncertain_dead, reason serialization_convention; options binding members, unmodeled MediatR handler types and their constructors, and constructors of types activated by DI, MediatR or MVC -> uncertain_dead, reason framework_convention; then the inherited verdict (an accessor takes the verdict of its property, a type takes the first verdict among its members); then an instance field, an instance property (not an indexer) or its getter of a record, which the compiler reads in the synthesized record members -> uncertain_dead, reason framework_convention (the record type does not inherit this verdict); (11) otherwise proved_dead, reason no_incoming_live_edges. Default excludes compiler-synthesized members (static constructors, implicitly declared members), extension-block marker types, generated symbols (every declaration is in a generated document) and test-project symbols (a project that references a test framework: Microsoft.VisualStudio.TestPlatform.ObjectModel, Microsoft.Testing.Platform, xunit.core, xunit.v3.core, nunit.framework, Microsoft.VisualStudio.TestPlatform.TestFramework, MSTest.TestFramework, TUnit.Core); lift with include_generated/include_tests (surfaced as uncertain reason generated_excluded/test_harness). Each call classifies the whole filtered universe with one batched incoming-edge query (WHERE target_symbol_id IN (...)) and cuts the page after classification, so the totals are exact; never one query per candidate. Liveness is one hop: a member reached only from a dead member is not a candidate.
<!-- /generated:dead-candidates:ladder -->

<!-- generated:dead-candidates:response -->
Response carries snapshot_id, filters, incoming_edge_kinds_checked[15], type_use_edge_kinds_checked[5], live_provenance_rank[2], uncertain_provenance[4], candidate_count (total filtered universe), dead_count/uncertain_count/unresolved_count (totals across all pages), candidates[] page window with symbol_id, fqn, kind, accessibility, effective_accessibility, document_path, locations[], project_name, declaration_count, is_generated, status (proved_dead/uncertain_dead/unresolved/uncertain), reason (handler-enum: no_incoming_live_edges, binding_incompleteness, public_surface, ef_convention, serialization_convention, framework_convention, possible_dispatch, name_candidate, runtime_unknown, generated_excluded, test_harness, entry_point_convention, external_interface_implementation), uncertainties[] (descriptions that state the same fact as a capsule uncertainty share its wording), incoming_edge_summary, declaration_span {start_line, end_line} (null when the symbol has no location), plus next_cursor and freshness.
<!-- /generated:dead-candidates:response -->

`candidate_count`/`dead_count`/`uncertain_count`/`unresolved_count` are totals, not page size — do not use them to drive a loop; use `next_cursor` (`null` marks the last page, keyset over `symbol_id`). Every `reason` is a frozen handler-enum; renaming or removing a code is a contract break (bumps `CliMcpContractVersion`, see [VERSIONING.md](../VERSIONING.md)), adding a new one is not but still updates `tests/CliMcpContractSnapshotTests.cs` and this list. CLI `--output=summary` emits one line per candidate (`kind accessibility fqn document_path:start_line reason status`) plus `-- <shown>/<total> dead candidate(s) shown (proved:N uncertain:N unresolved:N total candidates:N)`, and `; more available (--cursor)` when another page exists. CLI `--output=jsonl` emits `{"type":"meta","meta":{...}}` then `{"type":"candidate","candidate":{...}}` per entry. MCP tool `lurp_dead_candidates` mirrors CLI shape: `limit, cursor, snapshot_id, project, document, kind, include_public, include_generated, include_tests` plain properties; `Description` carries the same suppression ladder as this section.

---

### `--mode=pin-snapshot`

Pin which snapshot reads use by default when `--snapshot=` is omitted. Non-destructive, auditable override — the snapshots themselves remain immutable; a pin never rewrites `built_at_utc` or `selected_at_utc`.

```
--mode=pin-snapshot --snapshot=<id> --output-dir=<path> [--json]
--mode=pin-snapshot --clear --output-dir=<path> [--json]
--mode=pin-snapshot --snapshot=latest --output-dir=<path>  # alias for --clear
```

<!-- generated:flags:pin-snapshot -->
| Argument | Required | Description |
|---|---|---|
| `--snapshot=<id>` | Yes* | Snapshot ID to pin. Only snapshots with `status='complete'` can be pinned. The literal `latest` is an alias for `--clear`. |
| `--clear` | No | Remove every pin in the database; reads will again default to the snapshot that the last successful index run built or reused (`ORDER BY selected_at_utc DESC`). |
| `--json` | No | Emit structured JSON instead of plain text. `--output=json` is an alias. |
| `--output=<summary\|json>` | No | Payload rendering (default: `summary` when neither flag is given). `jsonl` is rejected — the payload is a single document. |
<!-- /generated:flags:pin-snapshot -->

\* Either `--snapshot=<id>` or `--clear` (or `--snapshot=latest`) must be provided.

Behavior:

- Pin is per-workspace (stored as one row per `workspace_id` in `snapshot_pins`: `pinned_snapshot_id`, `pinned_at_utc`, `previous_pinned_snapshot_id`). A database can hold more than one workspace (two solutions indexed into one output folder). Reads then use the most recently set pin whose snapshot is complete, and `--clear` removes every pin.
- When a pin is active, every read mode that defaults to "latest" (`search`, `grep`, `find-symbol`, `get-symbol`, `get-source`, `outline`, `navigate`, `diagnostics`, `get-annotations`, `impact`, `context`, `dead-candidates`, `timings`, `annotate/retract-annotation`) resolves `null`/`latest` to the pinned ID. Explicit `--snapshot=<id>` still bypasses the pin.
- A read command that finds a pin on a snapshot that is not complete uses the latest snapshot and prints `WARNING: pinned snapshot '<id>' is not complete; …` to stderr.
- Only `annotate`, `retract-annotation` and `pin-snapshot` remove a stale pin, and they print `NOTICE: removed the stale pin on snapshot '<id>' (the snapshot is not complete).` on stderr. Every other command, `index` included, leaves the row in place: reads treat the stale pin as absent, and `status` reports it in `stale_pinned_snapshot_id`.
- `status` surfaces the override visibly: text reports `Pinned snapshot: <id> (pinned at <utc>, built at <utc>)` and `Note: effective latest is pinned; selected latest is <id>` when they differ; JSON adds `pinned_snapshot_id`, `pinned_at_utc`, `previous_pinned_snapshot_id`, `effective_latest_snapshot_id`, `selected_latest_snapshot_id`, `pin_active` (`false` when no pin is active).
- Pin is protected from pruning: `PruneOldSnapshots(keep=3)` excludes the pinned ID from deletion (it does not count toward `keep`). A pinned snapshot cannot be deleted with `DeleteSnapshotData` — clear the pin first.
- Indexing is unaffected: incremental and full pipelines still compute the next snapshot's content against the selected latest (not the pin), so a correct pin never skips a needed re-index.
- Recovery from a botched environment: revert the source and re-index (which prints `Identical complete snapshot ... already exists` when content reconverges); the run reuses the identical snapshot and makes it the latest; no pin is needed.

---

## Snapshot Lifecycle

An existing snapshot's indexed facts are immutable. Later runs update `selected_at_utc` to record selection and refresh build-input rows to record freshness. Both are metadata outside the snapshot identity. The latest snapshot of a workspace is the one that the most recent successful index run built or reused. Each indexing run *normally* creates a new snapshot when content changes; incremental indexing creates a new snapshot when content changes and does not modify the previous one. Snapshot identity hashes the workspace id (git root and solution path, so a clone at another path gets other snapshot ids), the .NET SDK and Roslyn compiler versions, `ExtractorVersion` and each extractor and adapter version, the per-project target frameworks, assembly names, project file paths, project references, metadata reference identities, compilation-options fingerprints (including language version and preprocessor symbols), per-project `InternalsVisibleTo` targets and project document lists, the solution's unloaded C# project paths, each document's path and content hash, and the skipped adapters. When all of those inputs are unchanged, the run reuses the existing snapshot via content-addressed dedup instead of writing a duplicate row.

Before skipping the workspace load, incremental indexing compares the paths and SHA-256 hashes of build inputs observed before MSBuild opens the solution. It checks solution and C# project files, props and targets, `global.json`, `nuget.config` and `obj/**/project.assets.json` under the solution directory. It also checks loaded project files outside that directory and `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json` and `nuget.config` in each ancestor. Other build files added outside the solution directory are outside the precheck's scope.

```
Strategy: incremental
No changes detected. Skipping incremental index.
Workspace load skipped: stored snapshot is still current.

Incremental index complete. Snapshot: 3a69acadd9659d0f27d349c73d63fb9c
  Previous snapshot: 3a69acadd9659d0f27d349c73d63fb9c
  documents_changed_this_run:          0      documents_in_snapshot: 402
  projects_reextracted_this_run:       0/7
  declarations_extracted_this_run:     0      symbols_in_snapshot: 3658
  edge_relations_after_dedup_this_run: 0      edges_dropped_outside_snapshot_scope: 0 (external=0, compiler_synthesized=0, excluded_document=0, other=0)
                                                                  edge_relations_in_snapshot:          13195
```

(eNoteV2, 402 documents). The 3 newest complete snapshots are retained; older ones are pruned automatically.

### Schema version of the database

Read commands never write to `index.db`. Every read mode, and the MCP server (at session start, on every `lurp_refresh` call, and before `lurp_retract_annotation`), first checks the database schema version. It must equal the version this build needs (`lurp --version` prints it as `schema vN`). When the database is older, the command fails with `ERROR: Index database at <path> uses schema v<n>; this Lurp needs v<m>. Run 'lurp --mode=index' to update it.` When it is newer, the error says to update Lurp instead. Only `--mode=index`, `--mode=pin-snapshot` and MCP `lurp_index` migrate an older database; they refuse a newer database with the same message before they write. `--mode=status` reports the mismatch and does not fail (see [`--mode=status`](#--modestatus)).

## Environment Variables

Set once (e.g. in the shell profile an agent's session inherits) and every mode below that lists `--solution=`/`--output-dir=` as required accepts the env var instead — a flag always wins if both are present.

| Variable | Purpose |
|---|---|
| `LURP_SOLUTION_PATH` | Equivalent to `--solution=<path>`. |
| `LURP_OUTPUT_DIR` | Equivalent to `--output-dir=<path>`. |

## MCP server (`--mode=serve`)

Lurp runs as an MCP server over stdio, exposing <!-- generated:mcp-tools:read -->16 read tools (`lurp_context`, `lurp_get_source`, `lurp_outline`, `lurp_navigate`, `lurp_find_symbol`, `lurp_search`, `lurp_impact`, `lurp_diff`, `lurp_get_symbol`, `lurp_get_annotations`, `lurp_diagnostics`, `lurp_grep`, `lurp_status`, `lurp_timings`, `lurp_refresh`, `lurp_dead_candidates`)<!-- /generated:mcp-tools:read --> via `tools/list`. Passing `--enable-write-tools` additionally registers <!-- generated:mcp-tools:write -->`lurp_index` and `lurp_retract_annotation`<!-- /generated:mcp-tools:write --> (<!-- generated:mcp-tools:total -->18<!-- /generated:mcp-tools:total --> tools total). There
is no `lurp_annotate` tool by design, and no `lurp_pin_snapshot` tool either —
[`--mode=pin-snapshot`](#--modepin-snapshot) is CLI-only. Each tool call opens its
own short-lived read-only connection (`Mode=ReadOnly` plus `PRAGMA query_only=ON`),
so parallel reads use independent connections. Read tools never write to the
database or target tree. Only `lurp_refresh` with `ack` moves the session pin.
`lurp_index` and `lurp_retract_annotation` use separate writable connections.
`lurp_index` indexes the solution configured at session startup (`--solution=`). See
[`--mode=retract-annotation`](#--moderetract-annotation) for the retract semantics.
Annotation *creation* remains CLI-only (`--mode=annotate`); *retraction* is
available from both surfaces (`--mode=retract-annotation` and
`lurp_retract_annotation`).

Each tool lists its payload properties in `outputSchema`. A successful call
returns the payload object in `structuredContent` and the same payload as JSON
text in `content[0].text`. A tool error returns `isError: true` and a text message.

The `required` array in `inputSchema` lists the parameters each tool always needs. If a caller omits one, the tool returns a named error. Tool descriptions state conditional requirements such as the anchor rules for `lurp_context`. The table derives types, defaults and required markers from the tool attributes.

<!-- generated:mcp-tools:table -->
| Tool | Access | Parameters | Description |
|---|---|---|---|
| `lurp_context` | read | `symbol` (string), `file` (string), `line` (int), `intent` (string), `content_budget` (int), `max_hops` (int), `scope` (string), `affected_project` (string[]), `include_generated` (bool), `completeness_detail` (bool), `tier` (string), `tier_limit` (int), `cursor` (string), `snapshot_id` (string) | Assemble a context capsule for a symbol or file location. Exactly one of symbol or file+line is required. Intent: inspect \| modify \| diagnose (default: inspect). Supports tier continuation via tier/cursor. Passing file without line, or line without file, is an error, also when symbol is given. |
| `lurp_get_source` | read | `document` (string, required), `start_line` (int), `end_line` (int), `context_lines` (int), `snapshot_id` (string), `outline` (bool) | Fetch source text for a document in the pinned snapshot. Supports line windowing: start_line/end_line are 1-based inclusive; context_lines expands symmetrically. When no window is given the whole file is returned. Pass outline=true to include the declaration outline in the same response; compiler-generated members (record Equals, ToString and similar) are not listed. |
| `lurp_outline` | read | `document` (string, required), `include_generated` (bool), `limit` (int), `cursor` (string), `snapshot_id` (string) | List declarations in a document with 1-based line spans. Ordered by full_start. Linked-file copies of the same span collapse to one row per span, preferring the owning project. Supports pagination via limit/cursor and filtering generated declarations. declaration_count is the total match count across all pages, not the page size — use next_cursor for pagination. Compiler-generated members (record Equals, ToString and similar) are not listed. |
| `lurp_navigate` | read | `file` (string, required), `line` (int, required), `include_generated` (bool), `snapshot_id` (string) | Navigate to the declaration containing a file+line in the pinned snapshot. Null target is success, not an error. |
| `lurp_find_symbol` | read | `symbol` (string, required), `include_generated` (bool), `snapshot_id` (string) | Resolve a symbol in the pinned snapshot. Pass the full 'docCommentId\|assemblyIdentity' id, a doc-comment id (T:Some.Type), a fully qualified name (Some.Namespace.Type), or a generic name without its type parameters (Some.Namespace.Repo for Repo<T>). More than one match is an error. |
| `lurp_search` | read | `query` (string, required), `type` (string), `kind` (string), `limit` (int), `snippet_tokens` (int), `cursor` (string), `include_generated` (bool), `snapshot_id` (string) | Search source or symbols in the pinned snapshot. Type picks the store: source, symbol, or all. Symbol search ranks exact simple names first, followed by name tokens or prefixes, name substrings, and containing or fully qualified names. The cursor continues across these tiers. Source snippets keep the whole first match, even if it exceeds snippet_tokens. |
| `lurp_impact` | read | `symbol` (string, required), `direction` (string), `kinds` (string[]), `provenance` (string[]), `max_depth` (int), `limit` (int), `cursor` (string), `snapshot_id` (string) | Trace impact downstream or upstream for a symbol: the reached symbols with depth, shortest-path count, frontier flag and a deterministic witness path, plus first-hop groups. Supports kinds/provenance filtering and cursor pagination. Both directions exclude Declares, Contains and TestedBy by default. Supply kinds to use a different set. |
| `lurp_diff` | read | `from_snapshot` (string, required), `to_snapshot` (string, required) | Compute semantic diff between two snapshots. Both from_snapshot and to_snapshot are required. |
| `lurp_get_symbol` | read | `symbol` (string, required), `view` (string), `context_lines` (int), `include_generated` (bool), `snapshot_id` (string) | Get symbol metadata and optional source. Pass the full 'docCommentId\|assemblyIdentity' id, a doc-comment id (T:Some.Type), a fully qualified name (Some.Namespace.Type), or a generic name without its type parameters (Some.Namespace.Repo for Repo<T>). More than one match is an error. View summary\|source\|all controls payload. |
| `lurp_get_annotations` | read | `symbol` (string), `document` (string), `kind` (string), `limit` (int), `cursor` (string), `snapshot_id` (string) | Get annotations in the pinned snapshot. Supports three modes: by symbol (Pass the full 'docCommentId\|assemblyIdentity' id, a doc-comment id (T:Some.Type), a fully qualified name (Some.Namespace.Type), or a generic name without its type parameters (Some.Namespace.Repo for Repo<T>). More than one match is an error.), by document (solution-relative forward-slashed path; returns annotations whose document_path is that document and annotations of every symbol declared in it, user annotations included), or whole-snapshot when neither is given. Optional kind filter and keyset pagination via limit/cursor (default 100, ordered by annotation_id). annotation_count is the total match count across all pages, not the page size — use next_cursor for pagination. |
| `lurp_diagnostics` | read | `document` (string), `project` (string), `severity` (string), `id` (string), `limit` (int), `cursor` (string), `include_generated` (bool, default `false`), `snapshot_id` (string) | List diagnostics captured at index time for the pinned snapshot: compiler diagnostics plus any diagnostics from analyzers referenced by the target project (e.g. built-in SDK analyzers like CA1822), unless indexed with --skip-analyzers / skip_analyzers (reported as analyzers_skipped: true). IDE-only code-style rules (e.g. IDE0005) are not included even when the target project's own build enables them (e.g. EnforceCodeStyleInBuild plus an .editorconfig severity) — Roslyn's build-time compilation additionally gates IDE0005 behind GenerateDocumentationFile=true, independent of anything Lurp does. For unused-using-directive detection, query the compiler diagnostic instead: severity=hidden, id=CS8019 ("Unnecessary using directive") — Lurp always captures this regardless of the target's analyzer/code-style configuration. Filters: document (solution-relative path, not git-relative), project, severity (Hidden, Info, Warning, Error case-insensitive, or "all" for every severity including Hidden; default excludes Hidden), id (diagnostic code, e.g. CS8933), include_generated (default false). Generated filename patterns apply to all diagnostic paths. For snapshot documents, a leading comment containing `<auto-generated` or `<autogenerated` also marks the document as generated. For paths outside the snapshot, `bin/**` and `obj/**` build outputs are also excluded by default. Unknown severity values are rejected with an error instead of returning empty. Keyset pagination via limit/cursor, ordered by diagnostic_id. diagnostic_count is the total match count across all pages, not the page size. start_column/end_column are 0-based (unlike the 1-based start_line/end_line). Reports what the compiler and analyzers said at index time — not re-evaluated, ranked, or deduplicated. |
| `lurp_grep` | read | `query` (string, required), `limit` (int), `cursor` (string), `ignore_case` (bool), `include_generated` (bool), `snapshot_id` (string) | Literal/exact-text search over source content in the pinned snapshot. Returns per-occurrence results with 1-based start_line/end_line and 0-based start_column/end_column plus the full line_text. Use this instead of lurp_search when you need the exact line where a string appears. Supports pagination via cursor and case-insensitive search via ignore_case. match_count is the total match count across all pages, not the page size — use next_cursor for pagination. |
| `lurp_status` | read | `snapshot_id` (string), `detail` (object), `sections` (string), `max_documents` (int), `max_mismatches` (int), `documents` (string[]), `full` (bool), `projects` (string[]), `max_bytes` (int) | Show pinned snapshot status and freshness. Cheap stat check by default; pass full=true to run the MSBuild workspace freshness check (requires serve --solution=). max_bytes (default 40000) bounds the manifest parts only. Over the target, the largest manifest parts are summarized or left out, truncated is true, and detail.manifest.omitted_fields names them. Freshness and document_freshness are returned in full. The payload appears twice on the wire (content[0].text and structuredContent), and client size limits apply to both copies. Pass projects to narrow the manifest to named projects. |
| `lurp_timings` | read | `snapshot_id` (string) | Show step-by-step timing data and snapshot metrics for the pinned snapshot. |
| `lurp_refresh` | read | `ack` (string), `snapshot_id` (string) | Check for a newer snapshot and optionally advance the session pin. Without ack, changed and requires_ack indicate whether a newer snapshot is pending. To acknowledge, pass the new_snapshot_id from that response as ack. The server checks the database schema version on each call and keeps the pin in session memory. An accepted ack returns pinned=true, changed=false and requires_ack=false, even when the pin was already current. Compare old_snapshot_id and new_snapshot_id in the acknowledged response to see whether the pin moved. |
| `lurp_dead_candidates` | read | `limit` (int), `cursor` (string), `snapshot_id` (string), `project` (string), `document` (string), `kind` (string), `include_public` (bool), `include_generated` (bool), `include_tests` (bool) | List dead-code candidates for the pinned snapshot: symbols of kind Type, Method, Property, Field, Event with no incoming LIVE edge (Calls, MethodGroupRef, Constructs, Reads, Writes, Handles, RoutesTo, Registers, MapsTo, MayDispatchTo, StaticallyCalls, TestedBy, ReflectionTypeRef, ReflectionMemberRef, ReflectionNameCandidate) of strong provenance (compiler_proved, framework_derived). Inherited liveness: accessors take their property's or event's edges by role (a getter every edge except Writes; a setter or init only Writes; an event accessor every edge of the event); type candidates also take these edges from outside the type: References, Returns, Inherits, Implements, Throws. A linked declaration also takes incoming edges from its copies in other projects, including when a project filter is set. Suppression ladder, in the order the store checks it: (1) a strong LIVE edge from outside the candidate -> alive, not a candidate. An edge never counts when its source is the candidate, an accessor of the candidate, the property or event of the candidate accessor or another accessor of it, or (for a type) a symbol inside the type; (2) the candidate overlaps a binding_incompleteness row of an unobservable reason (compiler_error, unresolved_metadata, ambiguous_overload, unsupported_syntax, extractor_failure, project_unreadable) -> unresolved, reason binding_incompleteness; (3) the process entry point or the type that contains it (explicit static Main or the compiler-synthesized top-level-statements form; nothing in the repository calls it, whatever its accessibility) -> uncertain_dead, reason entry_point_convention; (4) implementation of an interface member or override of a member declared outside the compilation -> uncertain_dead, reason external_interface_implementation; (5) public surface: Public, Protected or protected internal by effective accessibility, which intersects declared accessibility with every containing type. Declared accessibility is used if effective accessibility is unavailable; an internal or private protected symbol of a project that grants InternalsVisibleTo to an assembly outside the snapshot (or to an assembly that the index cannot name) is public surface too -> excluded from proved_dead without include_public, uncertain_dead with reason public_surface when include_public is set; (6) a non-abstract, non-static class or delegate (or an instance constructor of one) of an assembly that an unmodeled dependency injection scan names -> unresolved, reason binding_incompleteness; (7) a generated symbol (every declaration is in a generated document) with include_generated -> uncertain, reason generated_excluded; (8) symbol of a test project with include_tests -> uncertain, reason test_harness; (9) when the candidate has only weak incoming edges, the strongest weak edge decides: possible provenance -> uncertain_dead, reason possible_dispatch; convention provenance -> uncertain_dead, reason framework_convention; name_candidate provenance -> uncertain_dead, reason name_candidate; runtime_unknown provenance -> unresolved, reason runtime_unknown; (10) when the candidate has no incoming LIVE edge at all, its own convention verdict: EF mapping sources, fields and properties (including accessors) of mapped entities and their base types, and instance constructors of mapped entities and EF mapping source types -> uncertain_dead, reason ef_convention; a field, property or method with a serialization opt-in (such as JsonInclude, JsonProperty, DataMember, JsonConstructor, or a serialization callback attribute) -> uncertain_dead, reason serialization_convention; options binding members, unmodeled MediatR handler types and their constructors, and constructors of types activated by DI, MediatR or MVC -> uncertain_dead, reason framework_convention; then the inherited verdict (an accessor takes the verdict of its property, a type takes the first verdict among its members); then an instance field, an instance property (not an indexer) or its getter of a record, which the compiler reads in the synthesized record members -> uncertain_dead, reason framework_convention (the record type does not inherit this verdict); (11) otherwise proved_dead, reason no_incoming_live_edges. Default excludes compiler-synthesized members (static constructors, implicitly declared members), extension-block marker types, generated symbols (every declaration is in a generated document) and test-project symbols (a project that references a test framework: Microsoft.VisualStudio.TestPlatform.ObjectModel, Microsoft.Testing.Platform, xunit.core, xunit.v3.core, nunit.framework, Microsoft.VisualStudio.TestPlatform.TestFramework, MSTest.TestFramework, TUnit.Core); lift with include_generated/include_tests (surfaced as uncertain reason generated_excluded/test_harness). Each call classifies the whole filtered universe with one batched incoming-edge query (WHERE target_symbol_id IN (...)) and cuts the page after classification, so the totals are exact; never one query per candidate. Liveness is one hop: a member reached only from a dead member is not a candidate. Response carries snapshot_id, filters, incoming_edge_kinds_checked[15], type_use_edge_kinds_checked[5], live_provenance_rank[2], uncertain_provenance[4], candidate_count (total filtered universe), dead_count/uncertain_count/unresolved_count (totals across all pages), candidates[] page window with symbol_id, fqn, kind, accessibility, effective_accessibility, document_path, locations[], project_name, declaration_count, is_generated, status (proved_dead/uncertain_dead/unresolved/uncertain), reason (handler-enum: no_incoming_live_edges, binding_incompleteness, public_surface, ef_convention, serialization_convention, framework_convention, possible_dispatch, name_candidate, runtime_unknown, generated_excluded, test_harness, entry_point_convention, external_interface_implementation), uncertainties[] (descriptions that state the same fact as a capsule uncertainty share its wording), incoming_edge_summary, declaration_span {start_line, end_line} (null when the symbol has no location), plus next_cursor and freshness. Keyset pagination via limit/cursor ordered by symbol_id ASC; limit default 50 max 200. |
| `lurp_index` | write (needs `--enable-write-tools`) | `strategy` (string), `force` (bool), `operation_id` (string), `cancel` (bool), `skip_analyzers` (bool) | Index or re-index the solution the serve session was started with (`--solution=`). Starts a background run (`strategy` full\|incremental, `force` to re-extract identical snapshot, `skip_analyzers` to skip Roslyn analyzer diagnostics while keeping compiler diagnostics). Returns at once with {operation_id,status:running}. Progress is buffered; poll lurp_index with {operation_id} to read it. Cancel by calling lurp_index with {operation_id,cancel:true} (sets cancel_requested:true on a running operation). If cancellation stops the run, later polls report cancelled. Reports the snapshot id the run built or reused, whatever database pin is set. On completion do not auto-pin — use lurp_refresh to advance. While running, other tools keep answering from the old pin. |
| `lurp_retract_annotation` | write (needs `--enable-write-tools`) | `annotation_id` (long, required), `snapshot_id` (string) | Retract (hard-delete) one annotation by annotation_id, scoped to the pinned snapshot — any provenance, not limited to user-authored (`--mode=annotate`) rows; extractor-derived rows can be removed the same way. The annotation_id is the surrogate PK from lurp_get_annotations. The delete is WHERE snapshot_id=@pinned AND annotation_id=@id — one row only, no cross-snapshot effect. Copy-forward clones allocate fresh ids, so retracting in snapshot A does not affect snapshot B's clone. |
<!-- /generated:mcp-tools:table -->

<!-- generated:flags:serve -->
| Argument | Required | Description |
|---|---|---|
| `--solution=<path>` | No | The solution the session was started with. `lurp_index` indexes it, and `lurp_status` with `full:true` compares it to the snapshot. |
| `--enable-write-tools` | No | Also register the write tools `lurp_index` and `lurp_retract_annotation`. Off by default, so an agent that follows prompt-injected text cannot start an index run or delete annotations. |
<!-- /generated:flags:serve -->

`lurp_index` returns the same operation fields when starting, polling, and cancelling.
It reports the snapshot the run built or reused, even when a database pin is active.
Pass `operation_id` and `cancel: true` to request cancellation while running.
The reply sets `cancel_requested: true` with `status: "running"`. Poll for
`cancelled`, or `completed` if the run finished before cancellation took effect.
A failed run reports `error_code` of `workspace_unreadable`
or `index_failed`. For `workspace_unreadable`, `error_data` carries
`reason_code`, `message` and `remediation`.

`lurp_status` defaults to the cheap stat-based freshness check. Pass `full: true`
to run the MSBuild workspace check with a solution configured through `--solution=`.
`freshness.full_check_error` is always present. It is `null` when no full check
was requested or the check succeeded. If a requested check could not run or
failed, the response gives the cheap-check result and the reason.

`lurp_refresh`, `lurp_index`, `lurp_status`, `lurp_context` and
`pin-snapshot --json` keep the same keys across branches. Inapplicable values
are `null`.

`sections` accepts `freshness`, `manifest`, `documents`, `references`,
`completeness` or `all`, comma-separated and case-insensitive; the values combine
as a union, so `documents` returns the per-document `document_versions` map and
`references` the full identities. An unknown value is a tool error (`isError: true`).

The legacy `detail` parameter accepts `true`, `false`, `1` or `0`; `true` is the
same as `sections=manifest`, and `sections` wins when both are given. Any other
value is a tool error (`isError: true`).

`lurp_status` uses `max_bytes` as a UTF-8 byte target when fitting manifest
parts (default 40,000, maximum 400,000). If the response exceeds the target,
the tool summarizes or omits the largest manifest parts first.
`truncated` is true only when this happens, and `detail.manifest.omitted_fields`
lists each affected part with the reason `summarized` or `budget_exhausted`.
Freshness and document freshness are returned in full within their own input
limits. Client size limits apply to both copies of the payload.

`documents` accepts an array of document paths for the per-document freshness
map. An empty array produces a `null` map. Every entry must be a nonblank string.
`projects` narrows the manifest to the supplied project names (the keys of
`target_frameworks`). When manifest details are requested, `detail.filters`
echoes `projects` and `max_bytes`. CLI `status --json` uses `--detail=` and has no size cap.

Size-driving inputs are bounded. The bounds are the constants in `McpLimits`:
<!-- generated:mcp-limits -->
| Parameter | Maximum |
|---|---|
| `limit` and `tier_limit` on every tool except `lurp_dead_candidates`; `max_documents`, `max_mismatches` and the length of `documents` on `lurp_status` | 500 |
| `limit` on `lurp_dead_candidates` | 200 |
| `content_budget` on `lurp_context` (estimated tokens) | 200000 |
| `max_depth` on `lurp_impact` | 20 |
| `max_hops` on `lurp_context` | 20 |
| `snippet_tokens` on `lurp_search` | 2000 |
| `max_bytes` on `lurp_status` (UTF-8 bytes) | 400000 |
<!-- /generated:mcp-limits -->
A larger value returns a tool error (`isError: true`).

**MCP callers, note:** `view` is **not** shared vocabulary between the CLI and
MCP surfaces. The CLI's `--mode=get-symbol --view=` accepts
`metadata|signature|body|declaration|containing-type|surrounding`; MCP's
`lurp_get_symbol` `view` parameter accepts `summary|source|all` (default
`summary`). Do not pass one surface's view names to the other — each validates
against its own list.

**Two different things are both called "pinned" in this document — don't conflate them.** The MCP *session* pin above is per-connection and in-memory: `McpSessionContext` resolves "latest" once at startup and every read for that session's lifetime answers from that same snapshot, even if a background `lurp_index` produces a newer one, until `lurp_refresh` re-pins the session. The *database* pin from `--mode=pin-snapshot` is persisted, cross-session, and overrides which snapshot "latest" resolves to for every future caller — CLI or MCP — until cleared. A fresh MCP session started after a database pin is set will pin itself to the pinned snapshot, since it resolves "latest" through the same pin-aware `GetLatestSnapshotId()`.

Passing `snapshot_id: "latest"` resolves to the session's pinned snapshot, the
same as omitting `snapshot_id`. On `lurp_diff`, either `from_snapshot` or
`to_snapshot` can use `"latest"` to select the same session pin.

Stdio transport is JSON-RPC pure: every non-empty stdout line parses as JSON
and framework logs are routed to stderr.

At startup, before the stdio handshake, the server writes one line to stderr:
`mcp: pinned snapshot <12-character id> at <utc> freshness:<state>`. When the
database pin points at a snapshot that is not complete, a line of the form
`WARNING: pinned snapshot '<id>' is not complete; this command uses the latest snapshot '<id>'. Clear the pin with --mode=pin-snapshot --clear.` follows it.

`--mode=serve` needs an indexed database. Without `index.db` it fails with
`ERROR: Index database not found at <path>`; with a database that has no
snapshot, with `ERROR: No snapshots found in the database.` Index first
via CLI (`--mode=index`), then serve. (`lurp_index` cannot perform the first
index: it is a background re-index tool available only inside a session whose
database already has a snapshot.)

```bash
# 1. Index once (creates index.db with a snapshot)
lurp --mode=index --solution=path/to/Your.sln --output-dir=./out

# 2. Serve from that output dir (pins that snapshot)
lurp --mode=serve --solution=path/to/Your.sln --output-dir=./out
```

Example MCP client config (stdio, global tool):

```json
{
  "mcpServers": {
    "lurp": {
      "command": "lurp",
      "args": ["--mode=serve", "--solution=path/to/Your.sln", "--output-dir=./out"]
    }
  }
}
```

Pin semantics: every read tool serves the snapshot pinned at startup, not
`latest`. After any `lurp_index` completion, reads keep returning the old
`snapshot_id` until `lurp_refresh {}` (and if `changed:true`, `lurp_refresh
{"ack": new_id}`).

## License

MIT
