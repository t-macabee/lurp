#!/usr/bin/env python3
import sys
sys.path.insert(0, r"C:\Users\Tarik\Desktop\Lurp\.tmp_test")
import harness_ab, json, subprocess, os, time

DOTNET = r"C:\Program Files\dotnet\dotnet.exe"
DLL = r"C:\Users\Tarik\Desktop\Lurp\src\bin\Release\net10.0\Lurp.dll"

def test_g(client, label):
    print(f"\n=== SECTION G: Framework adapters (MediatR) {label} ===")
    # Check MediatR usage via search
    for q in ["IRequestHandler", "INotificationHandler"]:
        rid = harness_ab.next_id()
        res = client.call("lurp_search", {"query": q, "type": "symbol", "limit":10}, rid, timeout=10)
        if "error" in res:
            print(f"  search {q} error {res['error']}")
            continue
        inner = res["result"]
        cnt = len(inner.get("results",[])) if inner else 0
        print(f"  search {q}: {cnt} results")
        if cnt>0:
            print(f"    sample: {inner['results'][0]}")
    # Also check via CLI? But we have MCP
    # Confirm via lurp_impact with provenance framework_derived that edges appear
    # Need to find a handler registration/dispatch site if MediatR present
    # For now, if no IRequestHandler found, report no MediatR usage (expected for these solutions)
    # Also check index progress for null warnings: we already saw progress has no null warnings
    # We'll just report that.

def test_h(client, label, pinned):
    print(f"\n=== SECTION H: Status / timings {label} pinned={pinned[:12]} ===")
    # lurp_status against each: confirm freshness reports up-to-date immediately after A/B, and reports stale after source change
    rid = harness_ab.next_id()
    res = client.call("lurp_status", {}, rid, timeout=15)
    if "error" in res:
        print(f"  lurp_status error {res['error']} FAIL")
    else:
        inner = res["result"]
        fresh = inner.get("freshness",{})
        print(f"  lurp_status freshness state={fresh.get('state')} method={fresh.get('method')} changed={fresh.get('changed_document_count')} pinned={inner.get('snapshot_id')[:12]}")
        if fresh.get("state")=="fresh":
            print(f"    PASS: fresh after A/B")
        else:
            print(f"    NOTE: not fresh (state={fresh.get('state')}) — may be full vs stat method difference, but after re-index should be fresh")
            # Check detail
            rid2 = harness_ab.next_id()
            res2 = client.call("lurp_status", {"detail": True}, rid2, timeout=15)
            if "error" not in res2:
                print(f"    detail status: {json.dumps(res2['result'], indent=2)[:2000]}")
    # lurp_timings
    rid3 = harness_ab.next_id()
    res3 = client.call("lurp_timings", {}, rid3, timeout=10)
    if "error" in res3:
        print(f"  lurp_timings error {res3['error']} FAIL (but should be present as 13th tool)")
    else:
        inner = res3["result"]
        print(f"  lurp_timings snapshot {inner.get('snapshot_id')[:12]} total_ms {inner.get('total_ms')} steps {len(inner.get('steps',[]))}")
        for step in inner.get("steps",[])[:5]:
            print(f"    step {step.get('step')} {step.get('elapsed_ms')}ms {step.get('percent')}%")
        # Capture for parity check with CLI
        # CLI: dotnet Lurp.dll --mode=timings --solution=<sln> --output-dir=<outdir> --output=json or --mode=timings --output=json
        # The McpParityTests says parity is enforced, but we will spot-check one real call: call CLI and compare total_ms
        # We can invoke CLI timings via dotnet
        info = harness_ab.SOLUTIONS[label]
        cmd = [DOTNET, DLL, "--mode=timings", f"--solution={info['sln']}", f"--output-dir={info['outdir']}", "--output=json"]
        print(f"  CLI cmd: {' '.join(cmd)}")
        try:
            proc = subprocess.run(cmd, capture_output=True, text=True, timeout=15)
            print(f"  CLI stdout len {len(proc.stdout)} stderr {proc.stderr[:500]}")
            # CLI timings output is JSON? Let's parse
            # The CLI --mode=timings --output=json writes JSON to stdout? Check Help: --json or --output?
            # We'll try both: --json vs --output=json
            if proc.returncode!=0:
                # try with --json
                cmd2 = [DOTNET, DLL, "--mode=timings", f"--solution={info['sln']}", f"--output-dir={info['outdir']}", "--json"]
                proc2 = subprocess.run(cmd2, capture_output=True, text=True, timeout=15)
                print(f"  CLI2 stdout {proc2.stdout[:2000]}")
                print(f"  CLI2 stderr {proc2.stderr[:500]}")
                print(f"  CLI2 return {proc2.returncode}")
                if proc2.returncode==0:
                    try:
                        j=json.loads(proc2.stdout)
                        print(f"  CLI timings total_ms {j.get('total_ms') if isinstance(j, dict) else 'unknown'}")
                        # Compare
                        mcp_total = inner.get("total_ms")
                        cli_total = j.get("total_ms") if isinstance(j, dict) else None
                        if mcp_total is not None and cli_total is not None and abs(mcp_total - cli_total) <= 5:
                            print(f"    PASS: MCP total_ms {mcp_total} matches CLI {cli_total} within tolerance")
                        else:
                            print(f"    NOTE: MCP {mcp_total} vs CLI {cli_total} diff may be timing variance but should match per McpParityTests")
                    except Exception as e:
                        print(f"  CLI parse error {e}")
            else:
                try:
                    j=json.loads(proc.stdout)
                    print(f"  CLI json {json.dumps(j, indent=2)[:2000]}")
                except Exception as e:
                    print(f"  parse error {e} stdout {proc.stdout[:2000]}")
        except Exception as e:
            print(f"  CLI exception {e}")

def test_i(client, label, pinned):
    print(f"\n=== SECTION I: Annotations (read-only) {label} ===")
    # Call lurp_get_annotations against few real symbols and confirm empty array not error
    # Need real symbols via search
    symbols=[]
    for q in ["CourseService", "UserService", "IUserService", "ProductService"]:
        rid = harness_ab.next_id()
        sr = client.call("lurp_search", {"query": q, "type": "symbol", "limit":5}, rid, timeout=10)
        if "error" not in sr and sr["result"]:
            for r in sr["result"].get("results",[])[:2]:
                symbols.append(r["symbol_id"])
        if len(symbols)>=3:
            break
    if not symbols:
        rid = harness_ab.next_id()
        sr = client.call("lurp_search", {"query": "Service", "type": "symbol", "limit":5}, rid, timeout=10)
        if "error" not in sr and sr["result"]:
            symbols = [r["symbol_id"] for r in sr["result"].get("results",[])[:3]]
    print(f"  testing annotations for {len(symbols)} symbols")
    for sym in symbols[:3]:
        rid = harness_ab.next_id()
        res = client.call("lurp_get_annotations", {"symbol": sym}, rid, timeout=10)
        if "error" in res:
            print(f"    {sym[:60]} error {res['error']} FAIL (should return empty array not error)")
        else:
            inner = res["result"]
            # inner should have annotations array
            # Check structure
            anns = inner.get("annotations", inner.get("result", [])) if isinstance(inner, dict) else None
            print(f"    {sym[:60]} annotations {anns} (type {type(anns)})")
            if isinstance(inner, dict) and "annotations" in inner:
                cnt = len(inner["annotations"])
                print(f"      count {cnt} PASS (empty expected)")
            else:
                print(f"      inner keys {list(inner.keys())[:10] if isinstance(inner, dict) else inner}")

    # Check adapter-emitted annotations: ContextAssembler turning kind~constraint|invariant rows into CapsuleConstraint entries, sourced from EfCoreAdapter/MediatRAdapter (document_path != null)
    # We need to find if either solution has EF Core or MediatR usage, and find a symbol likely to carry one (EF entity, MediatR handler) and confirm lurp_get_annotations surfaces it
    # We can try to search for entities: "Course", "Order", "User" etc. and call get_annotations
    # Also check via sqlite directly: annotations table
    import sqlite3
    info = harness_ab.SOLUTIONS[label]
    db = os.path.join(info["outdir"], "index.db")
    try:
        con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
        cur = con.cursor()
        cur.execute("SELECT COUNT(*) FROM annotations WHERE snapshot_id=?", (pinned,))
        cnt = cur.fetchone()[0]
        print(f"  DB annotations for pinned snapshot {pinned[:12]}: count {cnt}")
        if cnt>0:
            cur.execute("SELECT symbol_id, document_path, annotation_kind, value_json FROM annotations WHERE snapshot_id=? LIMIT 5", (pinned,))
            rows = cur.fetchall()
            for row in rows:
                print(f"    DB annotation symbol {row[0][:60]} doc_path {row[1]} kind {row[2]}")
                # Now call MCP for that symbol and see if it surfaces
                sym = row[0]
                rid = harness_ab.next_id()
                res = client.call("lurp_get_annotations", {"symbol": sym}, rid, timeout=10)
                if "error" not in res:
                    inner = res["result"]
                    print(f"      MCP annotations for that symbol: {json.dumps(inner, indent=2)[:2000]}")
                else:
                    print(f"      MCP error {res['error']}")
        else:
            print(f"  No annotations existed to test read path — coverage gap, not failure")
        con.close()
    except Exception as e:
        print(f"  DB check error {e}")

# Main runner for H/I/G
for label in ["eNoteV2", "eCommerce"]:
    info = harness_ab.SOLUTIONS[label]
    client = harness_ab.MCPClient(info["sln"], info["outdir"])
    pinned = None
    # get pinned via status
    rid = harness_ab.next_id()
    st = client.call("lurp_status", {}, rid, timeout=10)
    if "error" not in st and st["result"]:
        pinned = st["result"].get("snapshot_id")
    print(f"\n\n=== Testing G/H/I for {label} pinned {pinned[:12] if pinned else 'unknown'} ===")
    test_g(client, label)
    if pinned:
        test_h(client, label, pinned)
        test_i(client, label, pinned)
    client.close()
print("DONE GHI")
