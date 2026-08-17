#!/usr/bin/env python3
import sys, os, json
sys.path.insert(0, r"C:\Users\Tarik\Desktop\Lurp\.tmp_test")
import harness_ab
scratch_root=r"C:\Users\Tarik\AppData\Local\Temp\claude\lurp-live-test\scratch-eCommerce-F"
scratch_db=r"C:\Users\Tarik\AppData\Local\Temp\claude\lurp-live-test\scratch-eCommerce-F-db"
scratch_sln=os.path.join(scratch_root,"eCommerce.sln")
client=harness_ab.MCPClient(scratch_sln, scratch_db)
# get snapshots
import sqlite3
dbpath=os.path.join(scratch_db,"index.db")
con=sqlite3.connect(f"file:{dbpath}?mode=ro", uri=True)
cur=con.cursor()
cur.execute("SELECT snapshot_id, built_at_utc FROM snapshots ORDER BY built_at_utc DESC")
rows=cur.fetchall()
print(rows)
con.close()
# try diff from oldest to newest
from_snap=rows[1][0] if len(rows)>1 else rows[0][0]
to_snap=rows[0][0]
print(f"Diff from {from_snap[:12]} to {to_snap[:12]}")
rid=harness_ab.next_id()
res=client.call("lurp_diff",{"from_snapshot":from_snap,"to_snapshot":to_snap},rid,timeout=15)
print(json.dumps(res, indent=2)[:8000] if "error" not in res else res["error"])
if "error" not in res:
    inner=res["result"]
    print(f"change_count {inner.get('change_count')} changes {len(inner.get('changes',[]))}")
    for ch in inner.get("changes",[])[:10]:
        print(json.dumps(ch, indent=2)[:1000])
        if "LurpDiffTestMethod" in json.dumps(ch):
            print("FOUND marker")
client.close()
