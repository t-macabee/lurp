#!/usr/bin/env python3
import subprocess, json, sys, os, time, threading, queue

dotnet = r"C:\Program Files\dotnet\dotnet.exe"
dll = r"C:\Users\Tarik\Desktop\Lurp\src\bin\Release\net10.0\Lurp.dll"
solution = r"C:\Users\Tarik\Desktop\eNoteV2\eNote\eNote.sln"
outdir = r"C:\Users\Tarik\AppData\Local\Temp\claude\lurp-live-test\eNoteV2"

cmd = [dotnet, dll, "--mode=serve", f"--solution={solution}", f"--output-dir={outdir}"]
print(f"CMD: {' '.join(cmd)}")
proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)

stderr_lines=[]
def read_stderr():
    for line in proc.stderr:
        print(f"STDERR: {line.rstrip()}", file=sys.stderr, flush=True)
        stderr_lines.append(line)

t = threading.Thread(target=read_stderr, daemon=True)
t.start()

q = queue.Queue()
def read_stdout():
    for line in proc.stdout:
        # push raw line
        q.put(line)

t2 = threading.Thread(target=read_stdout, daemon=True)
t2.start()

time.sleep(1.5)

def send(obj):
    line = json.dumps(obj)
    print(f">>> {line}", flush=True)
    proc.stdin.write(line + "\n")
    proc.stdin.flush()

def recv_json(timeout=5):
    deadline = time.time()+timeout
    while time.time() < deadline:
        try:
            line = q.get(timeout=0.5)
        except queue.Empty:
            continue
        stripped = line.strip()
        print(f"<<< RAW: {stripped[:500]}", flush=True)
        if not stripped:
            continue
        if not stripped.startswith("{"):
            print(f"    (skipping non-JSON log line)", flush=True)
            continue
        try:
            obj = json.loads(stripped)
            print(f"    => JSON parsed id={obj.get('id')} method={obj.get('method')}", flush=True)
            return obj
        except json.JSONDecodeError as e:
            print(f"    (json parse error: {e})", flush=True)
            continue
    print("TIMEOUT waiting for JSON response", flush=True)
    return None

# 1 initialize
send({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}})
r = recv_json(5)
print("INIT RESP:", json.dumps(r, indent=2) if r else "none")

if r and "result" in r:
    # 2 initialized notification
    send({"jsonrpc":"2.0","method":"notifications/initialized"})
    time.sleep(0.5)
    # drain any notifications
    while not q.empty():
        try:
            line = q.get_nowait()
            print(f"DRAIN RAW: {line.strip()[:200]}")
            if line.strip().startswith("{"):
                try:
                    print(json.loads(line.strip()))
                except: pass
        except: break

    # 3 tools/list
    send({"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}})
    r2 = recv_json(5)
    print("TOOLS LIST:", json.dumps(r2, indent=2)[:8000] if r2 else "none")
    if r2 and "result" in r2:
        tools = r2["result"].get("tools", [])
        print(f"Found {len(tools)} tools")
        for t in tools:
            print(f"  - {t.get('name')}")
        # try tools/call lurp_status
        send({"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"lurp_status","arguments":{}}})
        r3 = recv_json(10)
        print("STATUS RESP:", json.dumps(r3, indent=2)[:8000] if r3 else "none")
        # try lurp_search
        send({"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"lurp_search","arguments":{"query":"User","type":"symbol","limit":5}}})
        r4 = recv_json(10)
        print("SEARCH RESP truncated:", json.dumps(r4, indent=2)[:8000] if r4 else "none")

proc.stdin.close()
time.sleep(1)
try:
    proc.terminate()
    proc.wait(timeout=3)
except:
    proc.kill()

print("DONE")
