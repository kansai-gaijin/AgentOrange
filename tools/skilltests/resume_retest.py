"""Resumes a retest's Claude session that was stopped from outside, and writes its result file.
    python resume_retest.py <run name> <session id> <minutes so far> [prompt file name] [effort]
Writes run/<name>.json (with the minutes of both parts) and run/<name>.done when it ends.
"""
import json, os, sys, time
HERE = os.path.dirname(os.path.abspath(__file__))
sys.argv, args = sys.argv[:1], sys.argv[1:]
import importlib.util
spec = importlib.util.spec_from_file_location("runner_mod", os.path.join(HERE, "runner.py"))
src = open(os.path.join(HERE, "runner.py"), encoding="utf-8").read().replace("\nmain()\n", "\n")
runner = importlib.util.module_from_spec(spec)
exec(compile(src, "runner.py", "exec"), runner.__dict__)

name, session, before = args[0], args[1], float(args[2])
prompt_file = args[3] if len(args) > 3 else "blender-hair-grooming_7.txt"
effort = args[4] if len(args) > 4 else "high"
runner.MODEL = "claude-fable-5-2"
cwd = os.path.join(runner.RUN, name)
log = os.path.join(runner.RUN, name + ".resume.jsonl")
extra = ["--resume", session, "--mcp-config", os.path.join(HERE, "mcp_none.json"), "--plugin-dir", runner.PLUGIN,
         "--append-system-prompt-file", runner.BRIEF, "--settings", runner.SETTINGS, "--max-budget-usd", "200", "--max-turns", "2000",
         "--permission-mode", "bypassPermissions", "--disallowedTools",
         "WebFetch,WebSearch,Agent,Task,Workflow,CronCreate,ScheduleWakeup,RemoteTrigger,PushNotification,SendMessage,Artifact,AskUserQuestion"]
prompt = ("You were stopped partway by the test harness's own time limit, not because of anything you did; there is "
          "still no time limit for you. Carry on from where you were and finish the task: the strands, the renders and "
          "sheets, the close-ups, the .blend and the exported groom in the out folder, then the short honest summary.")
steer = os.environ.get("STEER_FILE")
if steer and os.path.exists(steer):  # a reviewer stopped you to correct course: their note comes first
    prompt = (open(steer, encoding="utf-8").read().strip() + "\n\nThere's still no time limit. Carry on from where you "
              "were with that in mind, and finish the task as asked.")
start = time.time()
done = runner.claude(prompt, cwd, log, extra, 720, effort)
# earlier parts: the first run, then any resumed parts a steer set aside as .resume<k>.jsonl
earlier = [os.path.join(runner.RUN, name + ".jsonl")]
k = 1
while os.path.exists(os.path.join(runner.RUN, f"{name}.resume{k}.jsonl")):
    earlier.append(os.path.join(runner.RUN, f"{name}.resume{k}.jsonl"))
    k += 1
r = runner.read(log)
calls, skills = [], set(r.get("skills", []))
for part in earlier:
    p = runner.read(part)
    calls += p.get("calls", [])
    skills |= set(p.get("skills", []))
r["calls"] = calls + r.get("calls", [])
r["skills"] = sorted(skills)
meta_path = os.path.join(runner.RUN, name + ".json")
asked = open(os.path.join(HERE, "prompts", prompt_file), encoding="utf-8").read().strip()
json.dump({"skill": "blender-hair-grooming", "name": name, "model": "Claude Fable 5.2", "effort": effort, "asked": asked,
           "refs": sorted(os.listdir(os.path.join(cwd, "refs"))) if os.path.isdir(os.path.join(cwd, "refs")) else [],
           "found": "blender-hair-shapes" in r["skills"], "target": "blender-hair-shapes", "in_time": done,
           "minutes": round(before + (time.time() - start) / 60, 1), "run": r},
          open(meta_path, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
open(os.path.join(runner.RUN, name + ".done"), "w").write(str(r.get("end")))
print(name, "resumed", round((time.time() - start) / 60, 1), "min", r.get("end"))
