#!/usr/bin/env python3
import sys, os, shutil, json, time, subprocess
sys.path.insert(0, r"C:\Users\Tarik\Desktop\Lurp\.tmp_test")
import harness_ab
DOTNET = r"C:\Program Files\dotnet\dotnet.exe"
DLL = r"C:\Users\Tarik\Desktop\Lurp\src\bin\Release\net10.0\Lurp.dll"
print("=== SECTION F: Semantic diff (scratch copy) ===")
label="eCommerce"
info=harness_ab.SOLUTIONS[label]
orig_dir=os.path.dirname(info["sln"])
scratch_root=r"C:\Users\Tarik\AppData\Local\Temp\claude\lurp-live-test\scratch-eCommerce-F"
scratch_db=r"C:\Users\Tarik\AppData\Local\Temp\claude\lurp-live-test\scratch-eCommerce-F-db"
for p in [scratch_root, scratch_db]:
    if os.path.exists(p):
        shutil.rmtree(p, ignore_errors=True)
        time.sleep(0.5)
        if os.path.exists(p):
            subprocess.run(["cmd","/c","rmdir","/s","/q",p.replace("/","\\")], timeout=10)
print(f"Copying {orig_dir} to {scratch_root}...")
def ignore_func(d,names):
    return [n for n in names if n in ["bin","obj",".git",".vs","TestResults","packages"]]
shutil.copytree(orig_dir, scratch_root, ignore=ignore_func)
scratch_sln=os.path.join(scratch_root,"eCommerce.sln")
os.makedirs(scratch_db, exist_ok=True)
# Restore scratch solution
print("Restoring scratch solution...")
cmd_restore=[DOTNET,"restore",scratch_sln]
print("CMD restore:", " ".join(cmd_restore))
proc_r=subprocess.run(cmd_restore, capture_output=True, text=True, timeout=120)
print(f"restore return {proc_r.returncode}")
print(f"restore stdout {proc_r.stdout[-1000:]}")
print(f"restore stderr {proc_r.stderr[-500:]}")
if proc_r.returncode!=0:
    print("restore failed, trying to continue anyway")
    # don't exit
time.sleep(1)
print("Bootstrapping DB via CLI full index...")
cmd=[DOTNET,DLL,"--mode=index",f"--solution={scratch_sln}",f"--output-dir={scratch_db}","--strategy=full"]
print("CMD:", " ".join(cmd))
proc=subprocess.run(cmd, capture_output=True, text=True, timeout=180)
print(f"CLI return {proc.returncode}")
print(f"STDERR last 800: {proc.stderr[-800:]}")
print(f"STDOUT last 1500: {proc.stdout[-1500:]}")
if proc.returncode!=0:
    print("Bootstrap failed, abort F")
    sys.exit(1)
dbpath=os.path.join(scratch_db,"index.db")
print(f"DB exists {os.path.exists(dbpath)} size {os.path.getsize(dbpath) if os.path.exists(dbpath) else 'N/A'}")
print("Starting MCP serve for scratch...")
client=harness_ab.MCPClient(scratch_sln, scratch_db)
rid=harness_ab.next_id()
st=client.call("lurp_status",{},rid,timeout=10)
print(f"status after bootstrap: {json.dumps(st['result'], indent=2)[:3000] if 'error' not in st else st['error']}")
pinned_a=st["result"].get("snapshot_id") if "error" not in st and st["result"] else None
print(f"Snapshot A={pinned_a}")
# Find target file
target_file=None
for root,dirs,files in os.walk(scratch_root):
    if "UserService.cs" in files:
        target_file=os.path.join(root,"UserService.cs")
        break
if not target_file:
    for root,dirs,files in os.walk(scratch_root):
        for f in files:
            if f.endswith(".cs") and "Service" in f:
                target_file=os.path.join(root,f)
                break
        if target_file:
            break
print(f"Target {target_file}")
with open(target_file,"r",encoding="utf-8",errors="replace") as f:
    content=f.read()
print(f"Original len {len(content)} contains LurpDiffTestMethod {('LurpDiffTestMethod' in content)}")
insert_code="\n    public void LurpDiffTestMethod() { }\n"
idx=content.rfind("}")
if idx!=-1:
    new_content=content[:idx]+insert_code+content[idx:]
    with open(target_file,"w",encoding="utf-8") as f:
        f.write(new_content)
    print(f"Inserted at {idx}, new len {len(new_content)}")
else:
    print("No } found")
with open(target_file,"r",encoding="utf-8",errors="replace") as f:
    newc=f.read()
print(f"New contains LurpDiffTestMethod {('LurpDiffTestMethod' in newc)}")
print("Re-indexing via MCP incremental...")
rid2=harness_ab.next_id()
res=client.call("lurp_index",{"strategy":"incremental","force":True},rid2,timeout=30)
print(f"start {json.dumps(res['result'], indent=2)[:2000] if 'error' not in res else res['error']}")
if "error" in res:
    client.close()
    sys.exit(1)
op_id=res["result"].get("operation_id")
final=None
for attempt in range(300):
    time.sleep(0.5)
    ridp=harness_ab.next_id()
    poll=client.call("lurp_index",{"operation_id":op_id},ridp,timeout=10)
    if "error" in poll:
        print(f"poll error {poll['error']}")
        break
    pinner=poll["result"]
    if pinner.get("status")!="running":
        final=pinner
        print(f"final {json.dumps(final, indent=2)[:3500]}")
        break
    if attempt%10==0:
        print(f"poll {attempt} running {len(pinner.get('progress',[]))}")
if not final:
    print("No final")
    client.close()
    sys.exit(1)
snapshot_b=final.get("result_snapshot_id")
print(f"Snapshot B={snapshot_b} prev {final.get('previous_snapshot_id')} status {final.get('status')}")
if snapshot_b==pinned_a:
    print("B equals A (dedup) - but we changed file, should be new")
    print(f"Progress {final.get('progress')}")
else:
    print("B distinct from A good")
print(f"Calling lurp_diff from {pinned_a[:12]} to {snapshot_b[:12]}")
rid_diff=harness_ab.next_id()
diff_res=client.call("lurp_diff",{"from_snapshot":pinned_a,"to_snapshot":snapshot_b},rid_diff,timeout=15)
if "error" in diff_res:
    print(f"diff error {diff_res['error']} FAIL")
else:
    inner=diff_res["result"]
    print(f"diff keys {list(inner.keys()) if isinstance(inner, dict) else type(inner)}")
    print(f"diff excerpt {json.dumps(inner, indent=2)[:6000] if isinstance(inner, dict) else str(inner)[:6000]}")
    changes=inner.get("changes",[]) if isinstance(inner, dict) else []
    print(f"changes count {len(changes)}")
    for ch in changes[:5]:
        print(f"  type {ch.get('change_type')} symbol {ch.get('symbol_id','')[:80]}")
    found=any("LurpDiffTestMethod" in json.dumps(ch) for ch in changes)
    print(f"Found LurpDiffTestMethod {found}")
    if found:
        print("PASS diff reports expected change")
    else:
        types=[c.get("change_type") for c in changes if isinstance(c, dict)]
        print(f"types {set(types)}")
        if len(changes)==0:
            print("No changes, trying full re-index")
            rid_full=harness_ab.next_id()
            res_full=client.call("lurp_index",{"strategy":"full","force":True},rid_full,timeout=30)
            if "error" not in res_full:
                op2=res_full["result"].get("operation_id")
                final2=None
                for _ in range(300):
                    time.sleep(0.5)
                    rp=harness_ab.next_id()
                    poll2=client.call("lurp_index",{"operation_id":op2},rp,timeout=10)
                    if poll2["result"].get("status")!="running":
                        final2=poll2["result"]
                        break
                if final2:
                    b2=final2.get("result_snapshot_id")
                    print(f"B2 {b2}")
                    rid_diff2=harness_ab.next_id()
                    diff2=client.call("lurp_diff",{"from_snapshot":pinned_a,"to_snapshot":b2},rid_diff2,timeout=15)
                    if "error" not in diff2:
                        inner2=diff2["result"]
                        changes2=inner2.get("changes",[]) if isinstance(inner2, dict) else []
                        print(f"After full changes {len(changes2)}")
                        print(json.dumps(inner2, indent=2)[:6000] if isinstance(inner2, dict) else str(inner2)[:6000])
client.close()
print("Discarding scratch...")
try:
    shutil.rmtree(scratch_root, ignore_errors=True)
    shutil.rmtree(scratch_db, ignore_errors=True)
    print("Cleaned")
except Exception as e:
    print(f"cleanup {e}")
print("DONE F")
