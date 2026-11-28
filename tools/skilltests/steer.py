"""Steers a running retest: stops its agent (and the Blender jobs it started), logs why, and resumes the
same session with a reviewer's note, detached.
    python steer.py <run name> <note file> [effort] [fix]
The note is logged to run/<name>.police.md with the time. The resumed part writes run/<name>.resume.jsonl.
With "fix", the run has already finished and the note is one fix from the art director: the agent makes it
in the same project and re-renders what it changes, and the minutes count on from the finished run's.
"""
import datetime, json, os, subprocess, sys, time
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from paths import WORK  # noqa: E402

name, note = sys.argv[1], sys.argv[2]
effort = sys.argv[3] if len(sys.argv) > 3 else "max"
fix = len(sys.argv) > 4 and sys.argv[4] == "fix"
run = os.path.join(WORK, "run")
parts = sorted((f for f in os.listdir(run) if f.startswith(name + ".resume") and f.endswith(".jsonl")),
               key=lambda f: os.path.getmtime(os.path.join(run, f)))
logs = [os.path.join(run, name + ".jsonl")] + [os.path.join(run, f) for f in parts]
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
# the resumed part writes run/<name>.resume.jsonl from scratch: keep the last part's log as resume<k>
current = os.path.join(run, name + ".resume.jsonl")
if os.path.exists(current):
    k = 1
    while os.path.exists(os.path.join(run, f"{name}.resume{k}.jsonl")):
        k += 1
    os.replace(current, os.path.join(run, f"{name}.resume{k}.jsonl"))
text = open(note, encoding="utf-8").read().strip()
with open(os.path.join(run, name + ".police.md"), "a", encoding="utf-8") as f:
    f.write(f"\n## {datetime.datetime.now():%Y-%m-%d %H:%M}{' (fix)' if fix else ''}\n\n{text}\n")
minutes = 0.0
meta = os.path.join(run, name + ".json")
if fix and os.path.exists(meta):  # count on from the finished run, not the idle time since it ended
    minutes = json.load(open(meta, encoding="utf-8")).get("minutes", 0.0)
elif first:
    try:
        t0 = datetime.datetime.fromisoformat(first.replace("Z", "+00:00"))
        minutes = (datetime.datetime.now(datetime.timezone.utc) - t0).total_seconds() / 60
    except ValueError:
        pass
env = {**os.environ, "PYTHONUTF8": "1", "STEER_FILE": os.path.abspath(note), "STEER_KIND": "fix" if fix else "steer"}
flags = 0x00000008 | 0x00000200  # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP: survives this script
subprocess.Popen([sys.executable, os.path.join(HERE, "resume_retest.py"), name, session, f"{minutes:.1f}", "blender-hair-grooming_7.txt", effort],
                 cwd=WORK, env=env, creationflags=flags, stdout=open(os.path.join(WORK, name + "_steer.log"), "a"),
                 stderr=subprocess.STDOUT)
print(f"steered {name} (session {session}) after {minutes:.0f} min")
