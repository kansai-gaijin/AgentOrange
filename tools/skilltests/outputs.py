"""Builds the outputs page's data: per skill, everything its agent made and did, from run/ and results/.
    python outputs.py   ->  outpage/s/<skill>.json, outpage/f/<files>, outpage/index.json
"""
import base64, glob, io, json, os, shutil, sys
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from paths import WORK  # noqa: E402
RUN, RES, OUT = os.path.join(WORK, "run"), os.path.join(WORK, "results"), os.path.join(WORK, "outpage")
for d in ("s", "f"):  # the page itself (index.html) stays
    shutil.rmtree(os.path.join(OUT, d), ignore_errors=True)
    os.makedirs(os.path.join(OUT, d))
PICS = {".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tga"}
TEXT = {".md", ".json", ".py", ".txt", ".log", ".diff", ".cpp", ".h", ".cs", ".ini", ".csv", ".toml", ".sh", ".ps1", ".bat", ".cmd",
        ".html", ".mjs", ".js", ".xml", ".svg", ".conf", ".config", ".htaccess", ".uproject", ".bvh", ".mmcr", ".ptex"}
VIEW = {".glb": "model/gltf-binary", ".mp4": "video/mp4"}
MAXBIN = 14 * 2 ** 20


def picture(path):
    im = Image.open(path)
    im.load()
    w, h = im.size
    if im.mode not in ("RGB", "L"):
        bg = Image.new("RGB", im.size, (128, 128, 128))
        bg.paste(im.convert("RGBA"), mask=im.convert("RGBA").split()[3])
        im = bg
    if max(w, h) > 1100:
        im.thumbnail((1100, 1100), Image.LANCZOS)
    b = io.BytesIO()
    im.convert("RGB").save(b, "JPEG", quality=78)
    return "data:image/jpeg;base64," + base64.b64encode(b.getvalue()).decode(), f"{w}×{h}"


def steps(log):
    out, calls = [], {}
    if not os.path.exists(log):
        return out
    for line in open(log, encoding="utf-8", errors="replace"):
        try:
            o = json.loads(line)
        except ValueError:
            continue
        if o.get("type") == "assistant":
            for c in o["message"]["content"]:
                if c["type"] == "text" and c["text"].strip():
                    out.append({"k": "say", "t": c["text"][:4000]})
                elif c["type"] == "tool_use":
                    i = c["input"]
                    if c["name"] == "Bash" or c["name"] == "PowerShell":
                        t = i.get("command", "")
                    elif c["name"] in ("Write",):
                        t = i.get("file_path", "") + "\n\n" + str(i.get("content", ""))
                    elif c["name"] == "Edit":
                        t = i.get("file_path", "") + "\n- " + str(i.get("old_string", ""))[:800] + "\n+ " + str(i.get("new_string", ""))[:800]
                    else:
                        t = json.dumps(i, ensure_ascii=False)
                    s = {"k": "call", "n": c["name"], "t": t[:3000]}
                    calls[c["id"]] = s
                    out.append(s)
        elif o.get("type") == "user" and isinstance(o.get("message", {}).get("content"), list):
            for c in o["message"]["content"]:
                if c.get("type") == "tool_result" and c.get("tool_use_id") in calls:
                    r = c.get("content")
                    if isinstance(r, list):
                        r = "\n".join(x.get("text", "[picture]") if x.get("type") == "text" else "[picture]" for x in r)
                    s = calls[c["tool_use_id"]]
                    s["r"] = str(r)[:1500]
                    if c.get("is_error"):
                        s["e"] = 1
    return out


def files_of(folder, key):
    pics, texts, views, others = [], [], [], []
    o = os.path.join(folder, "out")
    if not os.path.isdir(o):
        return pics, texts, views, others
    for p in sorted(glob.glob(os.path.join(o, "**", "*"), recursive=True)):
        if not os.path.isfile(p) or "__pycache__" in p:
            continue
        rel = os.path.relpath(p, o).replace("\\", "/")
        ext = os.path.splitext(p)[1].lower() or os.path.basename(p).lower()
        size = os.path.getsize(p)
        try:
            if ext in PICS:
                src, dims = picture(p)
                pics.append({"name": rel, "src": src, "dims": dims})
                continue
            if ext in VIEW and size <= MAXBIN:
                name = f"{key}--{rel.replace('/', '--')}"
                if ext == ".glb":  # the page host doesn't serve .glb, so models go as base64 text
                    name += ".txt"
                    open(os.path.join(OUT, "f", name), "w").write(base64.b64encode(open(p, "rb").read()).decode())
                else:
                    shutil.copy(p, os.path.join(OUT, "f", name))
                views.append({"name": rel, "file": "f/" + name, "type": VIEW[ext], "size": size})
                continue
            if ext in TEXT and size < 400_000:
                t = open(p, encoding="utf-8", errors="replace").read()
                texts.append({"name": rel, "text": t[:60000], "cut": len(t) > 60000, "size": size})
                continue
        except Exception as e:
            others.append({"name": rel, "size": size, "why": f"couldn't be shown ({type(e).__name__})"})
            continue
        others.append({"name": rel, "size": size})
    return pics, texts, views, others


# The person's retest list, from their notes on 2026-11-26: what they said, or what their note implies.
RETEST = {
    "blender-asset-qa": "needs more testing", "blender-clothing": "on a real model, realistic clothes",
    "blender-hair-grooming": "with a reference, on a real head", "blender-retopology-uv": "on a real model", "blender-sculpting": "with skills chained",
    "horror-style": "with your own prompt", "prop-artist": "without the triangle limit", "realistic-style": "with a reference image",
    "blender-character-rigging": "show the skeleton", "blender-shot-rig": "animation too choppy to judge", "blender-texture-baking": "hard to judge on that asset",
    "blender-weather-fx": "with a better prompt", "dream-loop": "scene too simple to judge", "psx-horror-style": "difficult to judge", "set-dressing": "needs skills chained",
}
index = []
for f in sorted(glob.glob(os.path.join(RES, "*.json"))):
    r = json.load(open(f, encoding="utf-8"))
    skill = r["skill"]
    attempts = []
    names = [skill] if r["picked_up"] else [skill, skill + "_told"]
    for n in names:
        folder = os.path.join(RUN, n)
        pics, texts, views, others = files_of(folder, n)
        st = steps(os.path.join(RUN, n + ".jsonl"))
        said = next((s["t"] for s in reversed(st) if s["k"] == "say"), "")
        attempts.append({"name": n, "told": n.endswith("_told"), "pics": pics, "texts": texts, "views": views, "others": others, "steps": st, "said": said})
    attempts[-1]["said"] = r["run"]["said"] or attempts[-1]["said"]
    for a in attempts:
        a["label"] = "Second try: told to use it" if a["told"] else ("First try: not told" if len(attempts) > 1 else "First test")
    for meta in sorted(glob.glob(os.path.join(RUN, skill + "_retest*.json"))):
        m = json.load(open(meta, encoding="utf-8"))
        folder = os.path.join(RUN, m["name"])
        pics, texts, views, others = files_of(folder, m["name"])
        refs = []
        for ref in sorted(glob.glob(os.path.join(folder, "refs", "**", "*"), recursive=True)):
            if os.path.splitext(ref)[1].lower() not in PICS or os.sep + "previous" + os.sep in ref:
                continue  # a head model isn't a picture, and the last pass's renders are on its own tab
            src, dims = picture(ref)
            refs.append({"name": os.path.relpath(ref, os.path.join(folder, "refs")).replace("\\", "/"), "src": src, "dims": dims})
        st = steps(os.path.join(RUN, m["name"] + ".jsonl"))
        n = m["name"].rsplit("_retest", 1)[1]
        label = f"Retest {n}: your prompt" if m.get("target", skill) == skill else f"Retest {n}: new skill {m['target']}"
        attempts.append({"name": m["name"], "model": m.get("model", "Claude Opus 5.5"), "effort": m.get("effort", "medium"), "told": False, "label": label, "retest": True, "asked": m["asked"], "refs": refs, "found": m["found"],
                         "loaded": m["run"]["skills"], "minutes": m["minutes"], "in_time": m["in_time"], "cost": (m["run"]["end"] or {}).get("total_cost_usd"),
                         "pics": pics, "texts": texts, "views": views, "others": others, "steps": st, "said": m["run"]["said"] or next((s["t"] for s in reversed(st) if s["k"] == "say"), "")})
    g = r["grade"]
    for a in attempts:  # runner.py ran every first test, and the judge, on these; retests say their own
        if not a.get("retest"):
            a["model"], a["effort"] = "Claude Opus 5.5", "medium"
    data = {"skill": skill, "task": r["task"], "judge_model": "Claude Opus 5.5", "judge_effort": "medium", "picked": r["picked_up"], "first": r.get("first_skills"), "loaded": r["run"]["skills"], "scripts": r["run"]["scripts"],
            "minutes": r["run"]["minutes"], "in_time": r["run"]["in_time"], "cost": (r["run"]["end"] or {}).get("total_cost_usd"), "turns": (r["run"]["end"] or {}).get("num_turns"),
            "attempts": attempts, "judge": {k: g.get(k) for k in ("aim", "result", "guidance", "scripts", "grade", "good", "bad", "wrong_in_skill", "checked")}}
    json.dump(data, open(os.path.join(OUT, "s", skill + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
    last = attempts[-1]
    index.append({"skill": skill, "aim": g.get("aim", ""), "retest": RETEST.get(skill, ""), "retests": sum(1 for a in attempts if a.get("retest")), "pics": sum(len(a["pics"]) for a in attempts), "views": sum(len(a["views"]) for a in attempts),
                  "texts": sum(len(a["texts"]) for a in attempts), "thumb": last["pics"][0]["name"] if last["pics"] else "", "picked": r["picked_up"], "minutes": r["run"]["minutes"]})
json.dump(index, open(os.path.join(OUT, "index.json"), "w", encoding="utf-8"), ensure_ascii=False)
total = sum(os.path.getsize(p) for p in glob.glob(os.path.join(OUT, "**", "*"), recursive=True) if os.path.isfile(p))
print(len(index), "skills;", len(os.listdir(os.path.join(OUT, "f"))), "viewable files;", round(total / 2 ** 20, 1), "MB")
big = sorted(((os.path.getsize(p), os.path.basename(p)) for p in glob.glob(os.path.join(OUT, "s", "*.json"))), reverse=True)[:5]
print("biggest:", [(b, round(s / 2 ** 20, 1)) for s, b in big])
