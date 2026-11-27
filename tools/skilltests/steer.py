"""Steers a running retest: stops its agent (and the Blender jobs it started), logs why, and resumes the
same session with a reviewer's note, detached.
    python steer.py <run name> <note file> [effort]
The note is logged to run/<name>.police.md with the time. The resumed part writes run/<name>.resume.jsonl.
"""
import datetime, json, os, subprocess, sys, time
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from paths import WORK  # noqa: E402

name, note = sys.argv[1], sys.argv[2]
effort = sys.argv[3] if len(sys.argv) > 3 else "max"
run = os.path.join(WORK, "run")
logs = [os.path.join(run, name + ".jsonl"), os.path.join(run, name + ".resume.jsonl")]
session, first = None, None
for lg in logs:
    if os.path.exists(lg):
        for line in open(lg, encoding="utf-8", errors="replace"):
            if '"session_id"' in line:
                try:
                    o = json.loads(line)
                except ValueError:
                    continue
                session = o.get("session_id") or session
                if first is None and o.get("timestamp"):
                    first = o["timestamp"]
if not session:
    sys.exit(f"no session found for {name}")
# stop the run: its runner's whole process tree (the agent and the Blender jobs it started), then any
# Blender job still working in the run's folder
skill, n = name.rsplit("_retest", 1)
ps = ("Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | Where-Object { "
      "($_.CommandLine -match 'retest.py' -and $_.CommandLine -match '" + skill + " " + n + " ') -or "
      "($_.CommandLine -match 'resume_retest.py' -and $_.CommandLine -match '" + name + "') } "
      "| ForEach-Object { taskkill /F /T /PID $_.ProcessId }; "
      "Get-CimInstance Win32_Process -Filter \"Name='blender.exe'\" | Where-Object { $_.CommandLine -match '" + name + "' } "
      "| ForEach-Object { Stop-Process -Id $_.ProcessId -Force }")
subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True)
time.sleep(3)
text = open(note, encoding="utf-8").read().strip()
with open(os.path.join(run, name + ".police.md"), "a", encoding="utf-8") as f:
    f.write(f"\n## {datetime.datetime.now():%Y-%m-%d %H:%M}\n\n{text}\n")
minutes = 0.0
if first:
    try:
        t0 = datetime.datetime.fromisoformat(first.replace("Z", "+00:00"))
        minutes = (datetime.datetime.now(datetime.timezone.utc) - t0).total_seconds() / 60
    except ValueError:
        pass
env = {**os.environ, "PYTHONUTF8": "1", "STEER_FILE": os.path.abspath(note)}
flags = 0x00000008 | 0x00000200  # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP: survives this script
subprocess.Popen([sys.executable, os.path.join(HERE, "resume_retest.py"), name, session, f"{minutes:.1f}", "blender-hair-grooming_7.txt", effort],
                 cwd=WORK, env=env, creationflags=flags, stdout=open(os.path.join(WORK, name + "_steer.log"), "a"),
                 stderr=subprocess.STDOUT)
print(f"steered {name} (session {session}) after {minutes:.0f} min")
