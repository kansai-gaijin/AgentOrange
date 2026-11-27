"""Waits for the runner to end and the five-hour window to reset, then runs the rest, stopping again before a limit."""
import glob, json, os, subprocess, sys, threading, time
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from paths import WORK  # noqa: E402  (run logs, results and STOP live in the data folder)
group = sys.argv[1] if len(sys.argv) > 1 else "blender"
STOP = os.path.join(WORK, "STOP")


def latest():
    best = None
    for f in sorted(glob.glob(os.path.join(WORK, "run", "*.jsonl")), key=os.path.getmtime)[-6:]:
        for line in open(f, encoding="utf-8", errors="replace"):
            if '"rate_limit_event"' in line:
                try:
                    info = json.loads(line)["rate_limit_info"]
                    w = info["unifiedWindows"]
                    best = (w["five_hour"]["utilization"], w["seven_day"]["utilization"], w["five_hour"]["resetsAt"])
                except Exception:
                    pass
    return best


def runner_running():
    out = subprocess.run(["powershell", "-NoProfile", "-Command", "Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | Where-Object CommandLine -match 'runner.py' | Measure-Object | Select-Object -ExpandProperty Count"], capture_output=True, text=True).stdout.strip()
    return out not in ("", "0")


while runner_running():
    time.sleep(30)
used, _, reset = latest() or (0, 0, time.time())
while used > 0.88 and time.time() < reset + 90:
    time.sleep(30)
for _ in range(3):  # a round per window, should a window fill again
    if os.path.exists(STOP):
        os.remove(STOP)
    print(time.strftime("%H:%M"), "running", group, flush=True)
    done = threading.Event()

    def watch():
        while not done.is_set():
            got = latest()
            if got and (got[0] > 0.88 or got[1] > 0.60) and got[2] > time.time():
                open(STOP, "w").write(f"limit {got}")
                print(time.strftime("%H:%M"), "stopping at", got, flush=True)
                return
            time.sleep(60)

    t = threading.Thread(target=watch, daemon=True)
    t.start()
    with open(os.path.join(WORK, "progress.log"), "a", encoding="utf-8") as log:
        subprocess.run([sys.executable, "-u", os.path.join(HERE, "runner.py"), "--group", group, "--workers", "2" if group == "unreal" else "3"], stdout=log, stderr=subprocess.STDOUT, env={**os.environ, "PYTHONUTF8": "1"})
    done.set()
    if not os.path.exists(STOP):
        break
    got = latest()
    if got and got[1] > 0.60:
        print("weekly limit near, not going on", got, flush=True)
        break
    while time.time() < (got[2] if got else time.time()) + 90:
        time.sleep(30)
print(time.strftime("%H:%M"), "done", len(glob.glob(os.path.join(WORK, "results", "*.json"))), "results", flush=True)
