"""Runs a task per skill with a fresh agent set up as Agent Orange sets a session up, then a fresh judge grades it.
    python runner.py [--only a,b] [--workers 2] [--group blender|unreal|all]
Results: results/<skill>.json. One that's there isn't run again. A file called STOP ends it after what's running.
"""
import argparse, ctypes, glob, json, os, queue, re, shutil, subprocess, sys, threading, time
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from tests import TASKS, UNREAL
from paths import BLENDER, SKDATA, SKILLS, WORK
PLUGIN = glob.glob(os.path.join(SKDATA, "agent/providers/claude/*/agent-orange"))[0]
BRIEF = [f for f in glob.glob(os.path.join(SKDATA, "agent/briefings/*.md")) if "helpers" not in f][0]
SETTINGS = os.path.join(HERE, "settings.json")
MODEL = "claude-opus-5-5"
FX, UE = os.path.join(WORK, "fx"), os.path.join(WORK, "unreal")
RUN, RES = os.path.join(WORK, "run"), os.path.join(WORK, "results")
os.makedirs(RES, exist_ok=True)
RULES = """

How to work here: stay in this folder ({cwd}); anything you make goes in it, and the finished files, with pictures that show the result where there's something to see, go in its out folder. Practice files are in {fx}: copy what you need and never change them. Blender is {blender}; run it without its window (blender -b). Its MCP isn't connected, nor are Agent Orange's own tools, so do the work by running scripts. Don't use the internet, install anything or commit to Git. Nobody can answer questions, so decide. Keep it small: aim to finish in about 15 minutes. End with a short, honest summary: what you made, how you checked it, and what you couldn't do."""


def free_gb():
    class M(ctypes.Structure):
        _fields_ = [("n", ctypes.c_ulong), ("load", ctypes.c_ulong)] + [(k, ctypes.c_ulonglong) for k in "abcdefg"]
    m = M(); m.n = ctypes.sizeof(M)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m))
    return m.b / 2 ** 30


def claude(prompt, cwd, log, extra, minutes, effort="medium"):
    args = [shutil.which("claude"), "-p", "--model", MODEL, "--effort", effort, "--setting-sources", "project,local", "--strict-mcp-config",
            "--output-format", "stream-json", "--verbose", *extra]
    with open(log, "w", encoding="utf-8") as out:
        p = subprocess.Popen(args, cwd=cwd, stdin=subprocess.PIPE, stdout=out, stderr=subprocess.STDOUT)
        p.stdin.write(prompt.encode("utf-8")); p.stdin.close()
        try:
            p.wait(minutes * 60)
            return True
        except subprocess.TimeoutExpired:
            subprocess.run(["taskkill", "/F", "/T", "/PID", str(p.pid)], capture_output=True)
            return False


def read(log):
    skills, calls, errors, said, end, scripts = [], [], [], "", {}, []
    for line in open(log, encoding="utf-8", errors="replace"):
        try:
            o = json.loads(line)
        except ValueError:
            continue
        if o.get("type") == "assistant":
            for c in o["message"]["content"]:
                if c["type"] == "tool_use":
                    i = c["input"]
                    if c["name"] == "Skill":
                        skills.append(str(i.get("skill", "")).replace("agent-orange:", ""))
                    text = str(i.get("command") or i.get("file_path") or i.get("skill") or i.get("pattern") or "")
                    hit = re.findall(r"skills[/\\]+(?:tools[/\\]+blender[/\\]+[\w-]+[/\\]+)?([\w-]+)[/\\]+SKILL\.md", text)
                    skills += hit
                    scripts += re.findall(r"([\w-]+[/\\]+scripts[/\\]+[\w]+\.py)", text.replace("\\\\", "/"))
                    calls.append(f"{c['name']}: {text[:160]}")
                elif c["type"] == "text":
                    said = c["text"]
        elif o.get("type") == "user" and isinstance(o.get("message", {}).get("content"), list):
            for c in o["message"]["content"]:
                if c.get("type") == "tool_result" and c.get("is_error"):
                    errors.append(str(c.get("content"))[:300])
        elif o.get("type") == "result":
            end = {k: o.get(k) for k in ("subtype", "is_error", "num_turns", "duration_ms", "total_cost_usd")}
            said = o.get("result") or said
    return {"skills": list(dict.fromkeys(skills)), "calls": calls, "errors": errors, "said": said, "end": end, "scripts": list(dict.fromkeys(scripts))}


def attempt(skill, name, hint):
    cwd = os.path.join(RUN, name)
    shutil.rmtree(cwd, ignore_errors=True)
    os.makedirs(os.path.join(cwd, "out"))
    task = TASKS[skill].replace("FX/", FX.replace("\\", "/") + "/").replace("UNREAL/", UE.replace("\\", "/") + "/")
    prompt = (f"Use the skill agent-orange:{skill}. " if hint else "") + task + RULES.format(cwd=cwd, fx=FX, blender=BLENDER)
    mcp = os.path.join(HERE, "mcp_mm.json" if skill == "material-maker" else "mcp_none.json")
    extra = ["--mcp-config", mcp, "--plugin-dir", PLUGIN, "--append-system-prompt-file", BRIEF, "--settings", SETTINGS, "--max-budget-usd", "6", "--max-turns", "90",
             "--permission-mode", "bypassPermissions", "--disallowedTools", "WebFetch,WebSearch,Agent,Task,Workflow,CronCreate,ScheduleWakeup,RemoteTrigger,PushNotification,SendMessage,Artifact,AskUserQuestion"]
    log = os.path.join(RUN, name + ".jsonl")
    start = time.time()
    done = claude(prompt, cwd, log, extra, 45 if skill in UNREAL else 30)
    return task, cwd, {**read(log), "in_time": done, "minutes": round((time.time() - start) / 60, 1), "hinted": hint}


JUDGE = """You're grading how well a SKILL (a written guide, some with scripts, that an agent loads) did its job. You didn't write it and weren't there. Be strict, fair and specific. Change nothing outside this folder.

The skill: {skill}. Its guide: {guide} (its own files: {own}). Read the guide first; its description says what it's for.
The task an agent was given: {task}
The folder it worked in: {cwd} (finished files in out/).
What it loaded, in order: {loaded}. It {found}.
Scripts of skills it ran: {scripts}
Tool errors it met ({nerr}): {errors}
Its run ended: {end}; {minutes} minutes.
Its own summary:
<summary>
{said}
</summary>

Look at every picture in out/ yourself. Check claims by measuring where that's cheap (Blender is {blender}, run with -b; write your scripts in {jdir}); don't take the summary's word. At most about 10 minutes.

Grade against what the skill is trying to accomplish, separating the skill's faults from the agent's. Reply with ONLY a JSON object:
{{"aim": "what the skill is for, in a line",
 "result": 0-10 (how well what came out meets that aim),
 "guidance": 0-10 (was what it says right, complete and usable here; did it clearly shape the result),
 "scripts": 0-10 or null (its scripts, if any were run: did they work as it says),
 "grade": "A".."F" with + or - (the skill overall),
 "good": ["what it did well, specific"],
 "bad": ["what went wrong or is missing, specific; say (skill) or (agent)"],
 "wrong_in_skill": ["anything the skill states that proved false, outdated or broken here, with evidence"],
 "checked": "what you measured or looked at yourself",
 "best_picture": "path of the picture that shows the result best, or empty"}}"""


def judge(skill, task, cwd, r):
    own = None
    for d in glob.glob(os.path.join(SKILLS, "**", skill), recursive=True):
        if os.path.exists(os.path.join(d, "SKILL.md")):
            own = d
    jdir = os.path.join(RUN, skill + "_judge")
    os.makedirs(jdir, exist_ok=True)
    prompt = JUDGE.format(skill=skill, guide=os.path.join(PLUGIN, "skills", skill, "SKILL.md"), own=own or "in the same place", task=task, cwd=cwd, loaded=", ".join(r["skills"]) or "no skill",
                          found="found the skill by itself" if not r["hinted"] else "only used the skill when told to; before that it " + ("loaded " + ", ".join(r["first_skills"]) if r.get("first_skills") else "loaded none"),
                          scripts=", ".join(r["scripts"]) or "none seen", nerr=len(r["errors"]), errors=" | ".join(e.replace("\n", " ")[:160] for e in r["errors"][:8]) or "none",
                          end=json.dumps(r["end"]), minutes=r["minutes"], said=r["said"][:5000], blender=BLENDER, jdir=jdir)
    log = os.path.join(RUN, skill + "_judge.jsonl")
    for _ in range(2):
        claude(prompt, jdir, log, ["--mcp-config", os.path.join(HERE, "mcp_none.json"), "--max-budget-usd", "3", "--max-turns", "40", "--permission-mode", "bypassPermissions",
                                   "--disallowedTools", "WebFetch,WebSearch,Agent,Task,Workflow,Skill,Artifact"], 15)
        got = read(log)
        m = re.search(r"\{.*\}", got["said"], re.S)
        try:
            return json.loads(m.group(0)), got["end"]
        except Exception:
            continue
    return {"grade": "?", "bad": ["the judge gave no answer that could be read"], "raw": got["said"][:800]}, got["end"]


LIMIT = re.compile(r"usage limit|limit reached|hit your .*limit|resets? at", re.I)


def one(skill):
    task, cwd, r = attempt(skill, skill, False)
    if LIMIT.search(r["said"][:300]) or (r["end"].get("is_error") and (r["end"].get("num_turns") or 0) <= 1):
        open(os.path.join(WORK, "STOP"), "w").write(f"{skill}: {r['said'][:300]}")
        return
    if skill not in r["skills"]:
        first = r["skills"]
        task, cwd, r = attempt(skill, skill + "_told", True)
        r["first_skills"] = first
    grade, jend = judge(skill, task, cwd, r)
    json.dump({"skill": skill, "task": task, "picked_up": not r["hinted"], "first_skills": r.get("first_skills"), "run": {k: r[k] for k in ("skills", "scripts", "errors", "end", "in_time", "minutes", "said")},
               "grade": grade, "judge_end": jend, "folder": cwd}, open(os.path.join(RES, skill + ".json"), "w", encoding="utf-8"), indent=1, ensure_ascii=False)
    print(time.strftime("%H:%M"), skill, "picked" if not r["hinted"] else "TOLD", grade.get("grade"), grade.get("result"), f"{r['minutes']} min", f"${(r['end'].get('total_cost_usd') or 0):.2f}", flush=True)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--only"); p.add_argument("--workers", type=int, default=2); p.add_argument("--group", default="blender")
    a = p.parse_args()
    names = a.only.split(",") if a.only else [n for n in TASKS if a.group == "all" or (n in UNREAL) == (a.group == "unreal")]
    q = queue.Queue()
    for n in names:
        if not os.path.exists(os.path.join(RES, n + ".json")):
            q.put(n)

    def work():
        while not os.path.exists(os.path.join(WORK, "STOP")):
            try:
                n = q.get_nowait()
            except queue.Empty:
                return
            while free_gb() < 2.0:
                time.sleep(20)
            try:
                one(n)
            except Exception as e:
                print("FAILED", n, repr(e)[:300], flush=True)

    ts = [threading.Thread(target=work) for _ in range(a.workers)]
    for t in ts:
        t.start(); time.sleep(5)
    for t in ts:
        t.join()
    print("ENDED", len(glob.glob(os.path.join(RES, "*.json"))), "results;", "STOPPED" if os.path.exists(os.path.join(WORK, "STOP")) else "", flush=True)


main()
