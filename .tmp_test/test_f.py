#!/usr/bin/env python3
import sys, os, shutil, json, time, subprocess, pathlib
sys.path.insert(0, r"C:\Users\Tarik\Desktop\Lurp\.tmp_test")
import harness_ab

DOTNET = r"C:\Program Files\dotnet\dotnet.exe"
DLL = r"C:\Users\Tarik\Desktop\Lurp\src\bin\Release\net10.0\Lurp.dll"

# Section F: Semantic diff via scratch copy
print("=== SECTION F: Semantic diff (scratch copy) ===")
# Choose eCommerce for F (smaller)
label = "eCommerce"
info = harness_ab.SOLUTIONS[label]
orig_sln = info["sln"]
# orig dir is C:\Users\Tarik\Desktop\FIT-RS2-2026\eCommerce
orig_dir = os.path.dirname(orig_sln)  # eCommerce folder
scratch_root = r"C:\Users\Tarik\AppData\Local\Temp\claude\lurp-live-test\scratch-eCommerce-F"
scratch_db = r"C:\Users\Tarik\AppData\Local\Temp\claude\lurp-live-test\scratch-eCommerce-F-db"
# Clean previous
for p in [scratch_root, scratch_db]:
    if os.path.exists(p):
        try:
            shutil.rmtree(p)
        except Exception as e:
            print(f"clean {p} error {e}")
            # try removing via cmd
            subprocess.run(["cmd", "/c", "rmdir", "/s", "/q", p.replace("/", "\\")], timeout=10)

print(f"Copying {orig_dir} to {scratch_root}...")
# Use copytree with ignore for bin, obj, .git, .vs, TestResults etc. to speed up
def ignore_func(dir, names):
    ignored=[]
    for n in names:
        if n in ["bin", "obj", ".git", ".vs", "TestResults", "packages"]:
            ignored.append(n)
    return ignored

shutil.copytree(orig_dir, scratch_root, ignore=ignore_func)
print(f"Copied, listing scratch")
print(os.listdir(scratch_root)[:10])

scratch_sln = os.path.join(scratch_root, "eCommerce.sln")
print(f"scratch_sln exists {os.path.exists(scratch_sln)}")

# Create fresh DB dir
os.makedirs(scratch_db, exist_ok=True)
# Bootstrap via CLI index
print(f"Bootstrapping DB via CLI full index...")
cmd = [DOTNET, DLL, "--mode=index", f"--solution={scratch_sln}", f"--output-dir={scratch_db}", "--strategy=full"]
print("CMD:", " ".join(cmd))
proc = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
print(f"CLI return {proc.returncode}")
print(f"STDERR last 500: {proc.stderr[-500:]}")
print(f"STDOUT last 1000: {proc.stdout[-1000:]}")
if proc.returncode != 0:
    print("Bootstrap failed, abort F")
    sys.exit(1)

# Verify DB created
dbpath = os.path.join(scratch_db, "index.db")
print(f"DB exists {os.path.exists(dbpath)} size {os.path.getsize(dbpath) if os.path.exists(dbpath) else 'N/A'}")

# Now start MCP server for scratch
print(f"Starting MCP serve for scratch...")
client = harness_ab.MCPClient(scratch_sln, scratch_db)
# Get snapshot A (pinned after bootstrap)
rid = harness_ab.next_id()
st = client.call("lurp_status", {}, rid, timeout=10)
print(f"status after bootstrap: {json.dumps(st['result'], indent=2)[:2000] if 'error' not in st else st['error']}")
pinned_a = st["result"].get("snapshot_id") if "error" not in st and st["result"] else None
print(f"Snapshot A = {pinned_a}")

# We also need to ensure lurp_index with solution param interaction check: per section F step 2, we should check via tokensave or just use safe second session (we already did)
# For completeness, we can also test what happens if we call lurp_index with solution override on existing session (eNoteV2) but we won't for safety.

# Make isolated change in scratch copy: add a method to UserService.cs
# Find UserService.cs in scratch
target_file = None
for root, dirs, files in os.walk(scratch_root):
    if "UserService.cs" in files:
        target_file = os.path.join(root, "UserService.cs")
        break
print(f"Target file for modification: {target_file}")
if not target_file or not os.path.exists(target_file):
    print("UserService.cs not found, trying any .cs file")
    for root, dirs, files in os.walk(scratch_root):
        for f in files:
            if f.endswith(".cs") and "Service" in f:
                target_file = os.path.join(root, f)
                break
        if target_file:
            break
print(f"Chosen target {target_file}")
with open(target_file, "r", encoding="utf-8", errors="replace") as f:
    content = f.read()
print(f"Original len {len(content)} first 200: {content[:200]!r}")
# Add a new method inside the class: find last } and insert before
# Simple: append a method before the final closing brace of the class
# We'll look for the last occurrence of "}\n" near end, insert.
# Safer: find "class UserService" and then find its closing brace, insert method.
# For simplicity, we will insert a new public method at end of file before final }
# Find the last } in file
if "class " in content:
    # Insert before last }
    insert_code = "\n    // LURP_DIFF_TEST_MARKER: added method for semantic diff test\n    public void LurpDiffTestMethod AddedForMcpDiff() {}\n"
    # Actually need valid C#: public void LurpDiffTestMethod() {}
    insert_code = "\n    public void LurpDiffTestMethod() { }\n"
    # Find position: last '}' with preceding newline
    idx = content.rfind("}")
    if idx != -1:
        new_content = content[:idx] + insert_code + content[idx:]
        with open(target_file, "w", encoding="utf-8") as f:
            f.write(new_content)
        print(f"Inserted diff method at {idx}, new len {len(new_content)}")
    else:
        print("Could not find } to insert")
else:
    print("No class found")

# Verify file changed
with open(target_file, "r", encoding="utf-8", errors="replace") as f:
    newc = f.read()
print(f"New content contains LurpDiffTestMethod: {'LurpDiffTestMethod' in newc}")

# Re-index as snapshot B via MCP lurp_index (incremental)
print(f"Re-indexing scratch via MCP incremental...")
rid2 = harness_ab.next_id()
res = client.call("lurp_index", {"strategy":"incremental","force":True}, rid2, timeout=30)
print(f"lurp_index start: {json.dumps(res['result'], indent=2)[:2000] if 'error' not in res else res['error']}")
if "error" in res:
    print("Failed to start incremental")
    client.close()
    sys.exit(1)
op_id = res["result"].get("operation_id")
print(f"op_id {op_id}")
# Poll
final=None
for attempt in range(300):
    time.sleep(0.5)
    ridp = harness_ab.next_id()
    poll = client.call("lurp_index", {"operation_id": op_id}, ridp, timeout=10)
    if "error" in poll:
        print(f"poll error {poll['error']}")
        break
    pinner = poll["result"]
    if pinner.get("status") != "running":
        final = pinner
        print(f"final after {attempt} polls: {json.dumps(final, indent=2)[:3000]}")
        break
    if attempt % 10==0:
        print(f"poll {attempt} status running progress {len(pinner.get('progress',[]))}")
if not final:
    print("No final")
    client.close()
    sys.exit(1)

snapshot_b = final.get("result_snapshot_id")
print(f"Snapshot B = {snapshot_b}, previous {final.get('previous_snapshot_id')}, status {final.get('status')}")
# Check if B is new vs dedup
if snapshot_b == pinned_a:
    print(f"Snapshot B equals A (dedup?) But we changed file, should be new snapshot. Something wrong: incremental detected 0 changed?")
    # Maybe incremental didn't detect change because we ignored file? Wait we copied without bin/obj but file change should be detected
    # Let's check progress
    print(f"Progress: {final.get('progress')}")
else:
    print(f"Got new snapshot B distinct from A, good")

# Now call lurp_diff with explicit snapshot IDs
print(f"Calling lurp_diff from {pinned_a[:12]} to {snapshot_b[:12]}")
rid_diff = harness_ab.next_id()
diff_res = client.call("lurp_diff", {"from_snapshot": pinned_a, "to_snapshot": snapshot_b}, rid_diff, timeout=15)
if "error" in diff_res:
    print(f"lurp_diff error {diff_res['error']} FAIL")
else:
    inner = diff_res["result"]
    print(f"lurp_diff inner keys {list(inner.keys()) if isinstance(inner, dict) else type(inner)}")
    print(f"lurp_diff result excerpt {json.dumps(inner, indent=2)[:5000] if isinstance(inner, dict) else str(inner)[:5000]}")
    # Check expected change type: symbol_relocated, member added/removed etc per TRUST_KERNEL.md
    # Look for changes array
    changes = inner.get("changes", []) if isinstance(inner, dict) else []
    print(f"changes count {len(changes)}")
    for ch in changes[:5]:
        print(f"  change_type {ch.get('change_type')} symbol {ch.get('symbol_id','')[:80] if isinstance(ch, dict) else ch} detail {str(ch.get('detail'))[:200] if isinstance(ch, dict) else ''}")
    # Also check if diff correctly reports added method
    found = any("LurpDiffTestMethod" in json.dumps(ch) for ch in changes)
    print(f"Found LurpDiffTestMethod in diff changes: {found}")
    if found:
        print("PASS: diff reports expected change")
    else:
        # Check all change types
        types = [c.get("change_type") for c in changes if isinstance(c, dict)]
        print(f"change_types present: {set(types)}")
        # If no changes, maybe diff didn't detect because we need full re-index not incremental? Let's try full
        if len(changes)==0:
            print("No changes detected, trying full re-index then diff again")
            rid_full = harness_ab.next_id()
            res_full = client.call("lurp_index", {"strategy":"full","force":True}, rid_full, timeout=30)
            if "error" not in res_full:
                op2 = res_full["result"].get("operation_id")
                final2=None
                for _ in range(300):
                    time.sleep(0.5)
                    rp = harness_ab.next_id()
                    poll2 = client.call("lurp_index", {"operation_id": op2}, rp, timeout=10)
                    if poll2["result"].get("status") != "running":
                        final2 = poll2["result"]
                        break
                if final2:
                    b2 = final2.get("result_snapshot_id")
                    print(f"Full re-index B2={b2}")
                    rid_diff2 = harness_ab.next_id()
                    diff2 = client.call("lurp_diff", {"from_snapshot": pinned_a, "to_snapshot": b2}, rid_diff2, timeout=15)
                    if "error" not in diff2:
                        inner2 = diff2["result"]
                        changes2 = inner2.get("changes",[]) if isinstance(inner2, dict) else []
                        print(f"After full, changes {len(changes2)}")
                        print(json.dumps(inner2, indent=2)[:5000] if isinstance(inner2, dict) else str(inner2)[:5000])
                    else:
                        print(f"diff2 error {diff2['error']}")
            else:
                print(f"full start error {res_full}")

# Cleanup
client.close()
print(f"Discarding scratch copy...")
try:
    shutil.rmtree(scratch_root)
    shutil.rmtree(scratch_db)
    print("Cleaned")
except Exception as e:
    print(f"cleanup error {e}")

print("DONE F")
