# scripts/

- `r1-compare/`: incremental-vs-full parity comparison harness.
- `r1-verify-*.sh`: convergence and per-fixture verification gates.
- `model-view/generate.py`: regenerates `docs/MODEL_VIEW.html` from one index. Python 3 standard library only. With `--solution`, it runs a full index into an empty `--work-dir` and renders the page; with `--db` and `--index-log`, it renders from an index you already have. The page styles come from the first `<style>` block of the current page.

  ```bash
  dotnet build src/Lurp.csproj -c Release
  python scripts/model-view/generate.py --lurp src/bin/Release/net10.0/Lurp.exe --solution ../eNoteV2/eNote/eNote.sln --work-dir <empty folder>
  ```
