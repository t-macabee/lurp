# Example Investigation Capsule

This document walks a real `--mode=context` capsule produced by Lurp, annotated
field by field. The capsule below was generated from the committed sample fixture
`tests/fixtures/Sample` (the `Library.Repository<T>` type). It is **not** hand-written:
it is the verbatim output of a real index + context run.

## Reproduce it

```bash
# From the repo root, after `dotnet build Lurp.slnx`
dotnet run --project src -- --mode=index \
  --solution=tests/fixtures/Sample/Sample.slnx --output-dir=./out

dotnet run --project src -- --mode=context \
  --file=Library/Repository.cs --line=5 --output-dir=./out --quiet
```

The second command writes a file named like
`out/capsule-T_Library.Repository+1_*.json`. The annotated excerpt below is that
file, trimmed to the fields that matter for understanding the contract (paths,
provenance tags, and the most informative tiers). See
`docs/reference/README.md` for the full field reference.

## Anchored capsule (trimmed)

```json
{
  "anchor": {
    "symbolId": "T:Library.Repository`1|Library, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
    "fullyQualifiedName": "global::Library.Repository<T>",
    "kind": "Type",
    "source": "…public class Repository<T> where T : class { … }",
    "scope": "T:Library.Repository`1|…",
    "intent": "inspect",
    "maxHops": 3,
    "snapshotId": "bf348e6ab4f5f9a9dce277a99a82c214",
    "provenance": "compiler_proved",
    "extractorIdentity": "1.4.0",
    "locations": [
      {
        "DocumentPath": "Library/Repository.cs",
        "StartLine": 1,
        "StartColumn": 0,
        "EndLine": 10,
        "EndColumn": 2,
        "IsGenerated": false
      }
    ]
  },
  "surroundingSource": [
    {
      "symbolId": "F:Library.Repository`1._items|…",
      "kind": "Field",
      "fullyQualifiedName": "global::Library.Repository<T>._items",
      "provenance": "compiler_proved",
      "edgeKind": "Declares",
      "source": "_items = []",
      "documentPath": "Library/Repository.cs",
      "startLine": 5,
      "inclusionReason": "Sibling declaration sharing the anchor's containing declaration."
    },
    {
      "symbolId": "M:Library.Repository`1.Add(`0)|…",
      "kind": "Method",
      "fullyQualifiedName": "global::Library.Repository<T>.Add",
      "provenance": "compiler_proved",
      "edgeKind": "Declares",
      "source": "public void Add(T item) => _items.Add(item);",
      "documentPath": "Library/Repository.cs",
      "startLine": 6,
      "inclusionReason": "Sibling declaration sharing the anchor's containing declaration."
    }
  ],
  "outgoingPaths": [
    {
      "Hops": [
        { "SourceSymbolId": "T:Library.Repository`1|…", "TargetSymbolId": "M:Library.Repository`1.Add(`0)|…", "EdgeKind": "Declares", "Provenance": "compiler_proved", "SourceDocument": "Library/Repository.cs", "SourceLine": 7, "SourceColumn": 4 },
        { "SourceSymbolId": "M:Library.Repository`1.Add(`0)|…", "TargetSymbolId": "F:Library.Repository`1._items|…", "EdgeKind": "Reads", "Provenance": "compiler_proved", "SourceDocument": "Library/Repository.cs", "SourceLine": 7, "SourceColumn": 31 }
      ],
      "Truncated": false,
      "TotalSteps": 2,
      "SemanticCauses": []
    }
  ],
  "topology": {
    "current": {
      "incomingReference": "see incomingPaths",
      "outgoingReference": "see outgoingPaths",
      "incomingPathCount": 0,
      "outgoingPathCount": 3,
      "totalHopCount": 5
    }
  },
  "completeness": {
    "extractor_version": "1.4.0",
    "active_tfms": { "App": "net10.0", "Contracts": "net10.0", "Library": "net10.0", "Sample.Tests": "net10.0" },
    "binding_incompleteness_summary": [
      { "ProjectName": "App", "Reason": "filtered_external", "Count": 114 },
      { "ProjectName": "Library", "Reason": "filtered_external", "Count": 66 }
    ],
    "binding_incompleteness_total": 323
  },
  "budget": 8000,
  "estimatedTokens": 1288,
  "estimatedArtifactTokens": 2750,
  "truncated": false,
  "omittedTiers": [
    { "category": "contracts", "reason": "empty" },
    { "category": "directCallers", "reason": "empty" }
  ],
  "uncertainties": [
    {
      "symbolIds": ["T:Library.Repository`1|…"],
      "relationshipKind": "binding_incompleteness",
      "description": "18 binding(s) in Library resolved to symbols in assemblies outside the compilation. Edges to those external targets are intentionally filtered from the persisted graph; their absence is a declared boundary, not an extraction failure."
    }
  ],
  "suggestedVerification": []
}
```

## Field guide

| Field | Meaning | Notes for consumers |
|---|---|---|
| `anchor.provenance` | How the anchor was resolved. | `compiler_proved` means Roslyn resolved it; `gap` means no symbol was found at the location (not an error — see `uncertainties`). |
| `anchor.extractorIdentity` | Extractor version that produced the snapshot. | Use to decide whether a capsule is comparable across runs. |
| `surroundingSource` | Sibling declarations in the same containing type/file. | Included by default for type/file anchors; each has its own `provenance` and `edgeKind`. |
| `outgoingPaths` / `incomingPaths` | Bounded relationship walks up to `maxHops`. | Each hop carries `EdgeKind` (`Declares`, `Reads`, …) and `Provenance`. `Truncated` flags budget-truncated walks. |
| `topology.current` | Hop/perspective counts. | `incomingPathCount` / `outgoingPathCount` summarize the path arrays. |
| `completeness` | What the snapshot did and did not cover. | `binding_incompleteness_*` is reason-coded (here `filtered_external`): absence is a declared boundary, not a failure. |
| `budget` / `estimatedTokens` / `estimatedArtifactTokens` | Token budgeting. | Size a context window from `estimatedArtifactTokens` (whole emitted file), not `estimatedTokens` (content only). |
| `omittedTiers` | Tiers left out and why. | `reason: "empty"` = proved absence; `reason: "unresolved"` = could not be observed (NOT evidence of absence); `reason: "budget_exhausted"` = fetch that tier on its own with `--tier=`. |
| `uncertainties` | Declared unknowns. | Separates "we filtered external bindings" from genuine gaps. Always read this section. |

## Reading the capsule honestly

- An **empty** tier (`"reason": "empty"`) is a proved absence for this snapshot.
- An **unresolved** tier is *not* evidence that no relation exists — the relation
  could not be observed (e.g. no symbol at the requested location).
- `provenance: "compiler_proved"` edges are derived straight from the Roslyn
  compilation. Anything the extractor could not bind is reported under
  `completeness.binding_incompleteness_*` and `uncertainties`, never silently dropped.
