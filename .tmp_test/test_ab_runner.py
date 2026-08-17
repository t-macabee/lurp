#!/usr/bin/env python3
import sys
sys.path.insert(0, r"C:\Users\Tarik\Desktop\Lurp\.tmp_test")
import harness_ab
import json, time

# Test for eNoteV2 only first
label = "eNoteV2"
info = harness_ab.SOLUTIONS[label]
client = harness_ab.MCPClient(info["sln"], info["outdir"])
print(f"Pinned at start: check via lurp_status")
rid = harness_ab.next_id()
st = client.call("lurp_status", {}, rid, timeout=10)
print("status:", json.dumps(st["result"], indent=2)[:3000])

# Section A
a_res = harness_ab.run_section_a(client, label)
print("\n=== A RESULT ===")
print(json.dumps({k: str(v)[:2000] if not isinstance(v, bool) else v for k,v in a_res.items()}, indent=2))

# Section B if A passed
if a_res.get("pass"):
    pinned = a_res.get("pinned_after")
    b_res = harness_ab.run_section_b(client, label, pinned)
    print("\n=== B RESULT ===")
    print(json.dumps({k: str(v)[:3000] if not isinstance(v, (bool, int)) else v for k,v in b_res.items()}, indent=2))
else:
    print("A FAILED, skipping B")

client.close()
print("DONE")
