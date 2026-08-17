#!/usr/bin/env python3
import subprocess, json, sys, os, time, threading

dotnet = r"C:\Program Files\dotnet\dotnet.exe"
dll = r"C:\Users\Tarik\Desktop\Lurp\src\bin\Release\net10.0\Lurp.dll"
# Try one solution
solution = r"C:\Users\Tarik\Desktop\eNoteV2\eNote\eNote.sln"
outdir = r"C:\Users\Tarik\AppData\Local\Temp\claude\lurp-live-test\eNoteV2"

cmd = [dotnet, dll, "--mode=serve", f"--solution={solution}", f"--output-dir={outdir}"]
print(f"CMD: {' '.join(cmd)}")
proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)

def read_stderr():
    for line in proc.stderr:
        print(f"STDERR: {line.rstrip()}", file=sys.stderr)

t = threading.Thread(target=read_stderr, daemon=True)
t.start()

time.sleep(1)

def send(obj):
    line = json.dumps(obj)
    print(f">>> {line}")
    proc.stdin.write(line + "\n")
    proc.stdin.flush()

def recv(timeout=5):
    # read one line from stdout with timeout via polling?
    import select
    # On Windows, select not work for pipes. Use thread queue.
    return None

# Try reading stdout via blocking read in thread
import queue
q = queue.Queue()
def read_stdout():
    for line in proc.stdout:
        q.put(line)

t2 = threading.Thread(target=read_stdout, daemon=True)
t2.start()

def recv_line(timeout=5):
    try:
        line = q.get(timeout=timeout)
        print(f"<<< {line.rstrip()}")
        return json.loads(line)
    except queue.Empty:
        print("TIMEOUT waiting for response")
        return None

# 1 initialize
send({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}})
r = recv_line(5)
print("INIT RESP:", r)

# 2 initialized notification
if r:
    send({"jsonrpc":"2.0","method":"notifications/initialized"})
    time.sleep(0.5)
    # 3 tools/list
    send({"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}})
    r2 = recv_line(5)
    print("TOOLS LIST:", json.dumps(r2, indent=2)[:5000] if r2 else "none")
    if r2:
        try:
            tools = r2["result"]["tools"]
            print(f"Found {len(tools)} tools")
            for t in tools:
                print(f"  - {t['name']}")
        except Exception as e:
            print("parse error", e)

# cleanup
proc.stdin.close()
time.sleep(1)
try:
    proc.terminate()
    proc.wait(timeout=3)
except:
    proc.kill()

print("DONE")
