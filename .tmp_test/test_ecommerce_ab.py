#!/usr/bin/env python3
import sys
sys.path.insert(0, r"C:\Users\Tarik\Desktop\Lurp\.tmp_test")
import harness_ab, json, time
label="eCommerce"
info=harness_ab.SOLUTIONS[label]
client=harness_ab.MCPClient(info["sln"], info["outdir"])
print(f"Connected to {label}, tools={client.tools}")
rid=harness_ab.next_id()
st=client.call("lurp_status", {}, rid, timeout=10)
print("status", json.dumps(st["result"], indent=2)[:4000])
# Section A
a_res=harness_ab.run_section_a(client, label)
print("A", json.dumps({k: str(v)[:3000] if not isinstance(v, bool) else v for k,v in a_res.items()}, indent=2))
if a_res.get("pass"):
    b_res=harness_ab.run_section_b(client, label, a_res.get("pinned_after"))
    print("B", json.dumps({k: str(v)[:3000] if not isinstance(v, (bool,int)) else v for k,v in b_res.items()}, indent=2))
client.close()
print("DONE eCommerce")
