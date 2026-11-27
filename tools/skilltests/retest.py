"""Reruns one skill with the person's own prompt, set up as the first tests were but with room for a real attempt.
    python retest.py <skill> <n> <prompt file> [reference files...]
Its run folder is run/<skill>_retest<n>; no judge: the person grades it.
"""
import json, os, shutil, sys, time
HERE = os.path.dirname(os.path.abspath(__file__))
sys.argv, args = sys.argv[:1], sys.argv[1:]
import importlib.util
spec = importlib.util.spec_from_file_location("runner_mod", os.path.join(HERE, "runner.py"))
src = open(os.path.join(HERE, "runner.py"), encoding="utf-8").read().replace("\nmain()\n", "\n")
runner = importlib.util.module_from_spec(spec)
exec(compile(src, "runner.py", "exec"), runner.__dict__)

MODELS = {"fable": ("claude-fable-5-2", "Claude Fable 5.2"), "opus": ("claude-opus-5-5", "Claude Opus 5.5")}
model_key, effort = args[0], args[1]  # art: fable medium or high by difficulty; code: opus high
runner.MODEL, model_name = MODELS[model_key]
args = args[2:]
skill, n, prompt_file, refs = args[0], args[1], args[2], args[3:]
name = f"{skill}_retest{n}"
cwd = os.path.join(runner.RUN, name)
shutil.rmtree(cwd, ignore_errors=True)
os.makedirs(os.path.join(cwd, "out"))
os.makedirs(os.path.join(cwd, "refs"))
for r in refs:
    if os.path.isdir(r):  # a folder of references goes in whole
        shutil.copytree(r, os.path.join(cwd, "refs", os.path.basename(r)))
    else:
        shutil.copy(r, os.path.join(cwd, "refs"))
asked = open(prompt_file, encoding="utf-8").read().strip()
RULES = """

How to work here: stay in this folder ({cwd}); anything you make goes in it, and the finished files, with pictures that show the result, go in its out folder.{refs} Blender is {blender}; run it without its window (blender -b). Its MCP isn't connected, nor are Agent Orange's own tools, so do the work by running scripts. Don't use the internet, install anything or commit to Git. Nobody can answer questions, so decide. {time} End with a short, honest summary: what you made, how you checked it, and what you couldn't do."""
# UNLIMITED=1: the person said no time limit, so the run gets 12 hours and is told it has no limit.
unlimited = os.environ.get("UNLIMITED") == "1"
minutes = 720 if unlimited else 70
prompt = asked + RULES.format(cwd=cwd, blender=runner.BLENDER, time="There's no time limit: take the time the result needs." if unlimited else "Take the time the result needs, up to about an hour.",refs=f" The reference is in {os.path.join(cwd, 'refs')}." if refs else "")
log = os.path.join(runner.RUN, name + ".jsonl")
mcp = os.path.join(HERE, "mcp_mm.json" if skill == "material-maker" else "mcp_none.json")
extra = ["--mcp-config", mcp, "--plugin-dir", runner.PLUGIN, "--append-system-prompt-file", runner.BRIEF, "--settings", runner.SETTINGS, "--max-budget-usd", "200" if unlimited else "20", "--max-turns", "2000" if unlimited else "200",
         "--permission-mode", "bypassPermissions", "--disallowedTools", "WebFetch,WebSearch,Agent,Task,Workflow,CronCreate,ScheduleWakeup,RemoteTrigger,PushNotification,SendMessage,Artifact,AskUserQuestion"]
start = time.time()
done = runner.claude(prompt, cwd, log, extra, minutes, effort)
r = runner.read(log)
# When the account can't use Fable (no access, or its limit is reached), the person's rule is Opus 5.5 on high.
if model_key == "fable" and r["end"].get("is_error") and (r["end"].get("num_turns") or 0) <= 2:
    print("Fable unusable:", r["said"][:300], "- running on Opus 5.5, high", flush=True)
    runner.MODEL, model_name, effort = MODELS["opus"][0], MODELS["opus"][1], "high"
    shutil.rmtree(cwd, ignore_errors=True)
    os.makedirs(os.path.join(cwd, "out")); os.makedirs(os.path.join(cwd, "refs"))
    for ref in refs:
        if os.path.isdir(ref):
            shutil.copytree(ref, os.path.join(cwd, "refs", os.path.basename(ref)))
        else:
            shutil.copy(ref, os.path.join(cwd, "refs"))
    start = time.time()
    done = runner.claude(prompt, cwd, log, extra, minutes, effort)
    r = runner.read(log)
target = os.environ.get("TARGET_SKILL", skill)  # a retest may be told to use another skill than the one it's filed under
json.dump({"skill": skill, "name": name, "model": model_name, "effort": effort, "prompt": prompt, "asked": asked, "refs": [os.path.basename(x) for x in refs], "found": target in r["skills"], "target": target, "in_time": done,
           "minutes": round((time.time() - start) / 60, 1), "run": r}, open(os.path.join(runner.RUN, name + ".json"), "w", encoding="utf-8"), indent=1, ensure_ascii=False)
print(name, "found" if skill in r["skills"] else "NOT FOUND", r["skills"], f"{round((time.time() - start) / 60, 1)} min", r["end"])
