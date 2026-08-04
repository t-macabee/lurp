# MCP wrapper: open design tasks

Captured 2026-08-04 after testing Lurp's read path against a slice of the
external `eNoteV2` checkout and fixing an unhandled crash in `--mode=context`
(see `TRUST_KERNEL.md`, "External test against eNoteV2 and a `--mode=context`
crash fix"). These are the "real friction, worth fixing" items from that
test — design/implementation tasks for when the CLI is wrapped as an MCP
server, not a forward-looking roadmap for the CLI itself. Ranked by leverage
under an MCP design, not by discovery order.

## 1. Design MCP symbol resolution so bare FQN/file-line never needs a symbolId round-trip

Every CLI query in the eNoteV2 test needed a `search` → `find-symbol` →
`context` round trip, because `search` prints a fully-qualified name but
`context`/`impact`/`simulate-*` need the `docCommentId|assemblyIdentity`
form only `find-symbol` returns. This is the structural cause of the
crash fixed in `ContextHandler.cs`.

`lurp_context`/`lurp_impact`/etc. should accept an FQN or file+line
directly and resolve to the internal `symbolId` server-side, calling the
shared query services rather than shelling out to the CLI. Keep symbol
IDs internal where possible.

## 2. Redesign `lurp_impact`'s compact/summary response shape to name actual hop symbols

`--output=summary` groups currently repeat the anchor's own `symbolId` in
every row instead of showing the caller/callee actually at that hop, so
the compact form tells you counts by edge kind but not who's involved.

Since an MCP response is consumed directly by the calling model with no
human fallback to `--output=json`, whatever `lurp_impact` returns needs
the informative shape as its only shape.

## 3. Reconsider capsule budget defaults/behavior for type-level anchors

A single mid-sized service type (12 methods) at `--budget=4000` zeroed
out `directCallers`, `relevantTests`, and every path/topology section.
Not incorrect (`budget_exhausted` is honestly reported), but it means
`--tier=` follow-ups are the norm rather than the exception for anything
above method granularity.

Decide whether `lurp_context` should raise the default budget, auto-tier
for type anchors, or otherwise change this before it's the first thing
every MCP caller has to work around.

## 4. Reconcile `estimatedTokens` vs `estimatedArtifactTokens` in the MCP capsule contract

The ~2x gap between the budget-basis figure (`estimatedTokens`) and the
whole-emitted-file figure (`estimatedArtifactTokens`) means a caller
can't size a context window from a single number without already knowing
the distinction (currently only documented in `--help` prose).

Decide what `lurp_context` should report as *the* token estimate for an
MCP consumer, and surface the distinction inline in the tool result
itself.
