# Lurp 4.2.0

Run a full index after the upgrade to schema v53. Extractor 1.14.0 and the adapter versions below change the snapshot id. Use `--strategy=full --force` if you already built a development snapshot with extractor 1.14.0.

## Breaking changes (CLI/MCP contract version 9)

### MCP impact and diff paging

`lurp_impact` returns at most 50 first-hop groups on the first page and none on later pages. `group_count_total` and `groups_truncated` describe the complete group set; CLI impact continues to return all groups. `lurp_diff` returns 100 changes by default; use `limit` and `next_cursor` to page through the complete diff. Change IDs and ordering are stable for the same snapshot pair.

### Navigation and help

`lurp_navigate` rejects a document absent from the snapshot with `InvalidParams`; a line outside declarations in an indexed document still returns a null target. CLI navigation reports an unknown document separately. `--mode=<name> --help` shows that mode only, and an unknown mode exits 1. Cursor mismatch errors request a fresh page on both surfaces.

### MCP indexing requests

`lurp_index` rejects `cancel: true` without an `operation_id` and rejects an empty-string `operation_id`. These requests previously started an index run.

### Dead-candidate classification

Entry points and public surface take precedence over unmodeled dependency injection assembly scans. Public candidates in scanned assemblies are excluded by default and reported as `public_surface` with `include_public`; entry points retain `entry_point_convention`. This also changes verdicts for existing snapshots.

### Workspace load failures

Indexing and solution-backed `--mode=status` exit 2 when the solution cannot be opened. MCP indexing reports `workspace_unreadable` with remediation that names the solution file. An index-write error during extraction fails the run with `full_index_failure` and exit 1, or `index_failed` in MCP.

Indexing records C# projects declared in the solution that MSBuild could not load as `project_unreadable`. A run fails when all C# projects are unreadable. The unloaded project paths contribute to the snapshot ID and full freshness checks.

### Solution file types

Indexing accepts only `.sln` and `.slnx` solution files. A `.slnf` filter file now fails the run with exit 2 (`workspace_unreadable` in MCP) instead of being opened by MSBuild. The declared-project check reads the solution through a reader that supports the two documented types; a filter file is not a solution.

### Declaration spans

Declaration locations include attached comments and end on the line containing the last declaration token. Outline and navigation use the same line boundaries as symbol metadata. Indexing a multi-targeted project keeps each distinct conditional declaration span and collapses copies with identical spans. Fields and field-like events store their variable declarator span only, so `start_line` is the declarator line; an indexer has no identifier name span, so `name_start_line` equals `start_line`.

Run a full index to extract the new declaration spans. The database migration rebuilds the symbol search index for stored snapshots.

### Symbol search

Symbol search ranks exact simple names, name tokens and prefixes, simple-name substrings, then containing or fully qualified names. Within each tier, production symbols precede test symbols and types precede members. The search query and cursor flag descriptions and the `lurp_search` tool description reflect the new order.

The symbol search cursor carries the new tiered sort key. Start a fresh page if you have a search cursor from an earlier version.

### Impact and context traversal

Both `--direction=downstream` and `--direction=upstream` now exclude `Declares`, `Contains` and `TestedBy` by default; 4.1.0 followed every kind downstream. Supply `--kinds=` for a different set. This changes downstream witness paths, groups and counts.

A downstream traversal seeds the walk with the anchor's depth-zero members for a type anchor (when `Declares` is excluded) and with its accessors for a property or event anchor. Reachability visits are keyed by `(symbol, access)`, so a read and a write of the same property can produce separate visits, and a compound assignment still counts as one symbol route.

The caller tiers follow constructions, as well as calls, method-group references, reads and writes, at every hop. Framework entry points (`RoutesTo`, `Handles`) are kept at every hop, not only at depth one. A `RoutesTo` source is a Route item with no source text or declaration location; its `route://` id is not a stored symbol, and `--symbol=` rejects it.

The outgoing capsule paths now use the same default edge filter as incoming paths and default upstream impact (`Declares`, `Contains` and `TestedBy` excluded), starting at the anchor and its depth-zero seeds. They previously followed every edge kind.

Direct callee dispatch candidates for property and event accesses are reported once per member, using the accessors that can execute. The member-level fallback is used only when no accessor matches the access, so a write does not include an override that only declares a getter. Production anchors omit dispatch candidates from test projects; test anchors retain them. The same rules apply to `--tier=direct_callees`. Generated edges no longer contribute relation uncertainties unless generated code is included; generated exclusion notes remain available.

### Cursor resets

Start a fresh page when you continue a search, outline, impact or `--tier=` context sequence that began before 4.2.0. Each of those cursors binds its request to a fingerprint of the 4.2.0 sequence layout and rejects a 4.1.0 cursor instead of resuming at a misaligned offset.

### Output schema v8

The context capsule's `OutputSchemaVersion` moves from 7 to 8. The capsule field names are unchanged, but the meaning of the path fields changed: outgoing paths use the default impact filter, caller tiers include framework entry points at every hop and constructions, and a Route item can appear in a caller tier. Direct callee candidates and generated relation uncertainties also follow the filtering rules above. A consumer that reads outgoing paths as "every edge kind" must update.

### Annotation lifecycle

User annotations now survive full and `--strategy=full --force` re-indexes: the new snapshot copies them forward. An annotation whose symbol no longer exists in the new snapshot is pruned. Extractor-derived annotations are written again from the new snapshot. A same-ID rebuild keeps the complete snapshot readable until it can publish the replacement in one transaction. Cancellation or failure leaves the complete snapshot available, and existing user annotation IDs survive a successful replacement.

### Snapshot manifest export

`--output-json` exports the selected snapshot after indexing, including reused snapshots and incremental runs that skip the workspace load. If the export fails, the command exits 1 and the selected snapshot stays complete.

### Changed descriptions

- `lurp_navigate` describes the error for an unknown document and when a null target is returned.

- `lurp_impact` describes its group limit; `lurp_diff` describes paging and its default page size.
- `lurp_dead_candidates`: the description reflects the revised classification ladder, and the assembly-scan step now comes after the entry-point and public-surface steps. Linked declarations share incoming liveness, public surface uses effective accessibility for every symbol, and record and framework conventions protect members.
- `lurp_index`: cancellation requires an operation ID, and an empty ID is rejected.
- `--query=` (search): the new ranking tiers.
- `--cursor=` (search): continues across all symbol match tiers.
- `lurp_search`: the new ranking order.
- `--direction=` (impact) and `lurp_impact`: both directions exclude `Declares`, `Contains` and `TestedBy` by default.
- `--force` keeps the complete snapshot readable until its replacement is ready. User annotations survive the rebuild and orphan annotations are pruned.
- `--output-json=` writes the selected snapshot manifest after indexing, including on reuse.
- `--provenance=`: excludes `ReflectionNameCandidate` and `ReflectionTargetUnknown` (not every `Reflection*` kind) and follows adapter-emitted `framework_derived` edges, including middleware `Calls`/`Constructs`.
- `lurp_outline`: linked-file copies of one span collapse to one row, preferring the owning project.
- `lurp_refresh`: `ack` must equal the `new_snapshot_id` of a call without `ack` (the literal `latest` is not accepted); a valid `ack` checks the database schema version and advances the session pin without closing or reopening a store.
- `lurp_diagnostics` and `diagnostics --include-generated`: generated filename patterns apply to all diagnostic paths. A leading `<auto-generated` or `<autogenerated` comment also marks snapshot documents as generated. The default filter also excludes out-of-snapshot `bin/**` and `obj/**` build outputs.
- `lurp_status` `documents`: selects the paths of the per-document freshness list (`document_freshness`); an empty array returns `null`.

## Incremental indexing

The incremental precheck compares build-input paths and SHA-256 hashes captured before the solution loads. Added, deleted and backdated build files trigger a workspace load. Touching a build file with identical content keeps the shortcut. Snapshots migrated from schema v52 load once to record build inputs.

The check covers build files and `project.assets.json` under the solution directory, loaded project files outside it, and five named configuration files in its ancestors. Other build files added outside the solution directory are outside this check. An external project first discovered by MSBuild is checked on the next run, once its path is known.

## Framework adapters

Every adapter version moves, so 4.1.0 snapshots are not reused.

| Adapter | Version | New facts |
| --- | --- | --- |
| ASP.NET Core | `aspnetcore-v4` | `UseMiddleware` emits `Constructs` to the middleware type and `Calls` to its entry points and explicit public constructors. Custom `HttpMethodAttribute` classes resolve their template and verbs through constructor chains, metadata-only constructors and array constants, and a verb that cannot be read becomes `CUSTOM`. A constructor with no initializer, or an implicit constructor, follows the base constructor that C# selects, including an optional parameter and the parameterless constructor when both exist. |
| Dependency Injection | `di-v3` | `Configure<T>` and `AddOptions<T>` add `options_binding` annotations. External single-type extensions claim the named type. An `IServiceCollection` extension method of another project in the solution is not external, so its caller gets no runtime-unknown claim. Assembly scans (`FromAssemblyOf`/`FromAssemblyContaining`, `AddValidatorsFromAssembly*`) emit a `convention:assembly_scan:<assembly>` placeholder instead of a runtime-unknown claim; EF Core `MigrationsAssembly` is not a scan. |
| MediatR | `mediatr-v3` | Stream requests (`IStreamRequest`, `IStreamRequestHandler`) produce `Handles`. |
| EF Core | `efcore-v4` | `MapsTo` covers fluent, owned and complex mappings, types discovered through `model.Entity(typeof(T))`, and extension-method chains. Concrete contexts map their inherited sets, solution builder helpers, method-group configurations, applied configurations and reachable navigations, with ignores applied across the model. Closed solution `DbSet` properties also map their entities. Configurations map their entity and explicitly configured owned and complex types. Applied configurations add `References` edges. Query filter annotations reflect the last call per entity and filter name within each root. Unique indexes add constraint annotations. |
| Serialization | `serialization-v3` | `[JsonConstructor]`, the DataContract callbacks and Newtonsoft `[OnError]` receive `included` annotations on an instance, non-generic method. The four System.Text.Json `IJsonOn*` interfaces are resolved through the interface map, so implicit, explicit and inherited implementations count; an inherited callback whose base does not implement the interface carries the derived type's document. |
| Test | `test-v5` | `TestedBy` follows `MemberData`, `ClassData`, `TestCaseSource` and `DynamicData` sources, `IClassFixture`/`ICollectionFixture` setup and their initializers. Reflected setup also emits `framework_derived` `References` from the test to its dependency, so an edit to the dependency refreshes the coverage facts. `TestedBy` starts only at solution types, so edges from external types are gone. |

## Dead-code candidates

A linked declaration takes the incoming edges of its copies in other projects, including when a project filter is set, so one project's use keeps every copy alive. Public surface uses effective accessibility for every symbol, with declared accessibility only as the fallback. An unmodeled DI assembly scan makes a non-public class, delegate and instance constructor unresolved after entry-point and public-surface checks. Constructors of EF mapping source types get `ef_convention`. An instance field, an instance property (not an indexer) or its getter of a record gets `framework_convention`, because the compiler reads it in the synthesized record members; the record type does not inherit that verdict. Annotations on external types are not stored.

## Versions

| Component | 4.1.0 | 4.2.0 |
| --- | --- | --- |
| Tool | 4.1.0 | 4.2.0 |
| Database schema | v50 | v53 |
| Extractor | 1.13.0 | 1.14.0 |
| CLI/MCP contract | v8 | v9 |
| Output schema | v7 | v8 |
