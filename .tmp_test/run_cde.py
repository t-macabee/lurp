#!/usr/bin/env python3
import sys
sys.path.insert(0, r"C:\Users\Tarik\Desktop\Lurp\.tmp_test")
import harness_ab, mcp_sections_cde, json

for label in ["eNoteV2", "eCommerce"]:
    info = harness_ab.SOLUTIONS[label]
    client = harness_ab.MCPClient(info["sln"], info["outdir"])
    pinned = mcp_sections_cde.get_pinned(client)
    print(f"\n\n######## TESTING {label} pinned {pinned}")
    c = mcp_sections_cde.section_c(client, label, pinned)
    print(f"C result pass={c['pass']}")
    d = mcp_sections_cde.section_d(client, label, pinned)
    print(f"D findings {d['findings'][:2]}")
    e = mcp_sections_cde.section_e(client, label, pinned)
    print(f"E findings {e['findings']}")
    client.close()
print("DONE CDE for both")
