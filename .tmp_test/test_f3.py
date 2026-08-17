#!/usr/bin/env python3
import sys, os, shutil, json, time, subprocess
sys.path.insert(0, r"C:\Users\Tarik\Desktop\Lurp\.tmp_test")
import harness_ab
DOTNET = r"C:\Program Files\dotnet\dotnet.exe"
DLL = r"C:\Users\Tarik\Desktop\Lurp\src\bin\Release\net10.0\Lurp.dll"
# Re-run F with longer timeout, using existing scratch if exists else recreate
scratch_root=r"C:\Users\Tarik\AppData\Local\Temp\claude\lurp-live-test\scratch-eCommerce-F"
scratch_db=r"C:\Users\Tarik\AppData\Local\Temp\claude\lurp-live-test\scratch-eCommerce-F-db"
scratch_sln=os.path.join(scratch_root,"eCommerce.sln")
# Check if scratch exists and DB exists
if not os.path.exists(scratch_sln):
    print("Scratch not exists, need to recreate from orig")
    # recreate
    orig_dir=os.path.dirname(harness_ab.SOLUTIONS["eCommerce"]["sln"])
    if os.path.exists(scratch_root):
        shutil.rmtree(scratch_root, ignore_errors=True)
    if os.path.exists(scratch_db):
        shutil.rmtree(scratch_db, ignore_errors=True)
    def ignore_func(d,names):
        return [n for n in names if n in ["bin","obj",".git",".vs","TestResults","packages"]]
    shutil.copytree(orig_dir, scratch_root, ignore=ignore_func)
    os.makedirs(scratch_db, exist_ok=True)
    print("Restoring...")
    subprocess.run([DOTNET,"restore",scratch_sln], capture_output=True, text=True, timeout=120)
    print("Bootstrapping...")
    proc=subprocess.run([DOTNET,DLL,"--mode=index",f"--solution={scratch_sln}",f"--output-dir={scratch_db}","--strategy=full"], capture_output=True, text=True, timeout=180)
    print(proc.stdout[-1000:])
    print(proc.stderr[-500:])
if not os.path.exists(scratch_sln):
    print("Failed to create scratch")
    sys.exit(1)
# Check if UserService already has LurpDiffTestMethod
target=None
for root,dirs,files in os.walk(scratch_root):
    if "UserService.cs" in files:
        target=os.path.join(root,"UserService.cs")
        break
print(f"Target {target}")
with open(target,"r",encoding="utf-8",errors="replace") as f:
    c=f.read()
print(f"Contains marker {('LurpDiffTestMethod' in c)} len {len(c)}")
if 'LurpDiffTestMethod' not in c:
    print("Inserting marker...")
    idx=c.rfind("}")
    new=c[:idx]+"\n    public void LurpDiffTestMethod() { }\n"+c[idx:]
    with open(target,"w",encoding="utf-8") as f:
        f.write(new)
    print("Inserted")
else:
    print("Marker already present, will try incremental again")

# Now connect via MCP and try incremental with longer timeout
client=harness_ab.MCPClient(scratch_sln, scratch_db)
rid=harness_ab.next_id()
st=client.call("lurp_status",{},rid,timeout=15)
print(f"status {json.dumps(st['result'], indent=2)[:2000] if 'error' not in st else st['error']}")
pinned_a=st["result"].get("snapshot_id") if "error" not in st else None
print(f"A {pinned_a}")
# Need to get previous snapshots list to know A
# If marker already present, then incremental may have already been attempted and failed; we can check current DB snapshot via sqlite
import sqlite3
dbpath=os.path.join(scratch_db,"index.db")
con=sqlite3.connect(f"file:{dbpath}?mode=ro", uri=True)
cur=con.cursor()
cur.execute("SELECT snapshot_id, built_at_utc, status FROM snapshots ORDER BY built_at_utc DESC LIMIT 5")
print(cur.fetchall())
con.close()
# Try incremental poll with longer timeout 30
rid2=harness_ab.next_id()
res=client.call("lurp_index",{"strategy":"incremental","force":True},rid2,timeout=30)
print(f"start {res}")
if "error" in res:
    print("start error")
    client.close()
    sys.exit(1)
op_id=res["result"].get("operation_id")
print(f"op {op_id}")
final=None
for attempt in range(600):
    time.sleep(0.7)
    ridp=harness_ab.next_id()
    poll=client.call("lurp_index",{"operation_id":op_id},ridp,timeout=30)
    if "error" in poll:
        print(f"poll {attempt} error {poll['error']}")
        # retry? break?
        # Let's wait a bit and continue
        time.sleep(2)
        continue
    pinner=poll["result"]
    status=pinner.get("status")
    prog_len=len(pinner.get("progress",[]))
    if attempt%5==0:
        print(f"poll {attempt} status {status} prog {prog_len} progress last: {pinner.get('progress',[])[-2:] if prog_len>1 else ''}")
    if status!="running":
        final=pinner
        print(f"final status {status}")
        print(json.dumps(final, indent=2)[:5000])
        break
if not final:
    print("No final after 600 attempts")
    client.close()
    # Try to get DB latest
    con=sqlite3.connect(f"file:{dbpath}?mode=ro", uri=True)
    cur=con.cursor()
    cur.execute("SELECT snapshot_id, built_at_utc, status FROM snapshots ORDER BY built_at_utc DESC LIMIT 5")
    print(cur.fetchall())
    con.close()
    sys.exit(1)
snapshot_b=final.get("result_snapshot_id")
print(f"B {snapshot_b}")
# diff
rid_diff=harness_ab.next_id()
diff_res=client.call("lurp_diff",{"from_snapshot":pinned_a,"to_snapshot":snapshot_b},rid_diff,timeout=15)
print(f"diff {diff_res}")
if "error" not in diff_res:
    inner=diff_res["result"]
    print(json.dumps(inner, indent=2)[:8000])
    changes=inner.get("changes",[])
    print(f"changes {len(changes)}")
    for ch in changes[:10]:
        print(ch)
    found=any("LurpDiffTestMethod" in json.dumps(ch) for ch in changes)
    print(f"found marker {found}")
client.close()
print("DONE")
