"""Builds Agent Orange's main Blender skills from a checkout of EL4CTEO/blender-skills (MIT).

Usage: python -I tools/import-blender-agent-skills.py <checkout> client/Skills/tools/blender/blender-agent-skills

They're tested on Blender 5.3 and come with scripts, so they're copied whole. Front matter is cut to
what loaders read. They were written for ahujasid's Blender MCP, so the few places that name its tools
are changed for Blender Lab's, which Agent Orange uses, and each skill that asks for its `look` tool
gets a note on what to use instead. The destination's README.md says what changed.
"""
import os
import re
import shutil
import sys

repo, dest = sys.argv[1:3]
src = os.path.join(repo, "skills")


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


FRONT = re.compile(r"\A---\r?\n(.*?)\r?\n---\r?\n", re.S)
LOOK = re.compile(r"`look[`(/]|\blook\(|MCP look")
NOTE = ("\n> **In Agent Orange:** Blender's MCP is Blender Lab's, which has no `look` tool. Where this says `look`, render a review"
        " sheet with `blender-asset-qa`'s `scripts/review_shots.py` (import it through `execute_blender_code` like any skill"
        " script; its mode is material, solid, wire or random), take a viewport screenshot with"
        " `get_screenshot_of_area_as_image`, or, for animation, render the frames you need with `execute_blender_code`. Then look at"
        " the pictures yourself.\n")
FIXES = [
    ("Optional blender-mcp server for live sessions.", "Optional Blender MCP for live sessions."),
    ("**Live (blender-mcp `execute_blender_code`)**", "**Live (Blender's MCP `execute_blender_code`)**"),
    ("$(cygpath -m ~/.claude/skills)/...", "$(cygpath -m <skills_dir>)/..."),
    ("Poly Haven (CC0) via the blender-mcp `search_assets`/`import_asset`",
     "Poly Haven (CC0), downloaded from polyhaven.com with its source and licence recorded"),
]

own = read(os.path.join(dest, "README.md")) if os.path.exists(os.path.join(dest, "README.md")) else ""
if os.path.exists(dest):
    shutil.rmtree(dest)

left = list(FIXES)
noted = []
for root, _, files in os.walk(src):
    for f in files:
        if f.endswith((".pyc",)) or "__pycache__" in root:
            continue
        rel = os.path.relpath(os.path.join(root, f), src)
        path = os.path.join(dest, rel)
        if not f.endswith(".md"):
            os.makedirs(os.path.dirname(path), exist_ok=True)
            shutil.copy(os.path.join(root, f), path)
            continue
        text = read(os.path.join(root, f))
        for old, new in FIXES:
            if old in text:
                text = text.replace(old, new)
                if (old, new) in left:
                    left.remove((old, new))
        if f == "SKILL.md":
            m = FRONT.match(text)
            fields = {}
            for line in m.group(1).splitlines():
                k, _, v = line.partition(":")
                if k in ("name", "description") and v.strip():
                    fields[k] = v.strip()
            body = text[m.end():]
            if LOOK.search(body):
                body = NOTE + body
                noted.append(fields["name"])
            text = f"---\nname: {fields['name']}\ndescription: {fields['description']}\nlicense: MIT\n---\n" + body
        write(path, text)

if left:
    sys.exit(f"These changes found nothing to change, so the source has moved on: {left}")
shutil.copy(os.path.join(repo, "LICENSE"), os.path.join(dest, "LICENSE-blender-agent-skills.txt"))
if own:
    write(os.path.join(dest, "README.md"), own)
print(f"copied {len(os.listdir(dest))} entries; noted look in {len(noted)} skills: {', '.join(sorted(noted))}")
