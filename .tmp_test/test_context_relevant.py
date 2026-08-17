#!/usr/bin/env python3
import sys
sys.path.insert(0, r"C:\Users\Tarik\Desktop\Lurp\.tmp_test")
import harness_ab, json

label="eNoteV2"
info=harness_ab.SOLUTIONS[label]
client=harness_ab.MCPClient(info["sln"], info["outdir"])
# find CourseService.CreateAsync via search
rid = harness_ab.next_id()
sr = client.call("lurp_search", {"query": "CourseService.CreateAsync", "type": "symbol", "limit":5}, rid, timeout=10)
print(json.dumps(sr, indent=2)[:4000])
if "error" not in sr and sr["result"] and sr["result"].get("results"):
    for r in sr["result"]["results"]:
        print(r)
        sym = r["symbol_id"]
        # call context
        rid2 = harness_ab.next_id()
        cr = client.call("lurp_context", {"symbol": sym}, rid2, timeout=10)
        print(json.dumps(cr["result"], indent=2)[:8000] if "error" not in cr else cr["error"])
        if "error" not in cr:
            inner = cr["result"]
            capsule = inner.get("capsule",{})
            rt = capsule.get("relevant_tests",[])
            print(f"relevant_tests count {len(rt)} for {sym[:60]}")
            for t in rt[:2]:
                print(t.get("fully_qualified_name")[:80], t.get("kind"))
        break
else:
    # try find_symbol
    for fqn in ["eNote.Application.Features.Academic.Courses.Services.CourseService.CreateAsync", "M:eNote.Application.Features.Academic.Courses.Services.CourseService.CreateAsync"]:
        rid2 = harness_ab.next_id()
        fr = client.call("lurp_find_symbol", {"symbol": fqn}, rid2, timeout=10)
        print(f"find {fqn}: {fr}")
        if "error" not in fr and fr["result"]:
            print(json.dumps(fr["result"], indent=2)[:4000])

# also try mediatr handler search
rid3 = harness_ab.next_id()
sr3 = client.call("lurp_search", {"query": "IRequestHandler", "type": "symbol", "limit":10}, rid3, timeout=10)
print("IRequestHandler search", json.dumps(sr3["result"], indent=2)[:4000] if "error" not in sr3 else sr3)

client.close()
