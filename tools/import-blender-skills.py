"""Builds Agent Orange's Blender skills from a checkout of arjun988/blender-skills (MIT).

Usage: python -I tools/import-blender-skills.py <checkout> client/Skills/tools/blender/blender-skills-arjun988

The two MCP references Agent Orange rewrote (mcp-tools.md, mcp-integration.md) are kept from the
destination's blender-references. See the destination's README.md for what this changes.

The skills EL4CTEO/blender-skills covers better (tested on Blender 5.3, with scripts) are left out,
and links to them point to EL4CTEO's (tools/import-blender-agent-skills.py).
"""
import os
import re
import shutil
import sys

repo, dest = sys.argv[1:3]
OWN = ("mcp-tools.md", "mcp-integration.md")

src = os.path.join(repo, ".claude", "skills")
STUB = 1500  # A skill with less than this in all its files is a stub: a line or two of direction.

# Covered by EL4CTEO's skills, by the one that covers each.
COVERED = {
    "blender-director": "blender-core", "blender-modeler": "blender-hardsurface-modeling",
    "hard-surface": "blender-hardsurface-modeling", "sculpting": "blender-sculpting",
    "retopology": "blender-retopology-uv", "uv-workflow": "blender-retopology-uv",
    "materials": "blender-pbr-materials", "texture-workflow": "blender-texture-baking",
    "hair-groom": "blender-hair-grooming", "cloth-sim": "blender-simulation",
    "geometry-nodes": "blender-geometry-nodes", "lighting": "blender-lighting-lookdev",
    "rendering": "blender-rendering", "compositing": "blender-rendering",
    "animation": "blender-animation", "rigging": "blender-character-rigging",
    "physics-sim": "blender-simulation", "procedural-modeling": "blender-geometry-nodes",
    "scene-assembly": "blender-scene-setup", "asset-optimization": "blender-game-optimization",
    "lod-pipeline": "blender-game-optimization", "export-pipeline": "blender-export-pipelines",
    "unity-export": "blender-export-pipelines", "unreal-export": "blender-export-pipelines",
    "godot-export": "blender-export-pipelines", "qa-review": "blender-asset-qa",
    "lookdev": "blender-lighting-lookdev", "environment-artist": "blender-environment-kits",
    "character-artist": "blender-workflow-recipes", "archviz": "blender-lighting-lookdev",
}


def size(folder):
    return sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(folder) for f in fs)


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


FRONT = re.compile(r"\A---\r?\n(.*?)\r?\n---\r?\n", re.S)


def split(text):
    m = FRONT.match(text)
    if not m:
        return {}, text
    fields = {}
    for line in m.group(1).splitlines():
        k, _, v = line.partition(":")
        if k in ("name", "description") and v.strip():
            fields[k] = v.strip()
    return fields, text[m.end():]


def front(name, description):
    return f"---\nname: {name}\ndescription: {description}\nlicense: MIT\n---\n"


skills = sorted(d for d in os.listdir(src) if os.path.isfile(os.path.join(src, d, "SKILL.md")))
stubs = [d for d in skills if size(os.path.join(src, d)) < STUB]
kept = [d for d in skills if d not in stubs and d not in COVERED]

readme = read(os.path.join(dest, "README.md"))
own = {f: read(os.path.join(dest, "blender-references", f)) for f in OWN}
if os.path.exists(dest):
    shutil.rmtree(dest)

# Each kept skill as it is, with front matter cut to what loaders read (the director's repeats a key).
for d in kept:
    for root, _, files in os.walk(os.path.join(src, d)):
        for f in files:
            rel = os.path.relpath(os.path.join(root, f), src)
            text = read(os.path.join(root, f))
            if f == "SKILL.md" and root == os.path.join(src, d):
                fields, body = split(text)
                text = front(fields.get("name", d), fields["description"]) + body
            write(os.path.join(dest, rel), text)

# The shared references, as a skill of their own so they travel with the rest.
refs = os.path.join(dest, "blender-references")
for f in os.listdir(os.path.join(src, "references")):
    write(os.path.join(refs, f), read(os.path.join(src, "references", f)))
for f in OWN:
    write(os.path.join(refs, f), own[f])
write(os.path.join(refs, "SKILL.md"), front(
    "blender-references",
    "Shared references for the Blender skills: Blender's MCP tools in Agent Orange and how to work through them, the asset pipeline, naming conventions, polycount budgets, the validation checklist, and matching a reference picture. Read when another Blender skill points here, or before exporting any asset from Blender.")
    + "\n# Blender references\n\nShared by the other Blender skills.\n\n"
    + "".join(f"- `{f}`\n" for f in sorted(os.listdir(refs)) if f != "SKILL.md"))

# The stubs, gathered into one skill with a reference each, so they cost one skill rather than dozens.
art = os.path.join(dest, "blender-art-direction")
index = []
for d in stubs:
    fields, body = split(read(os.path.join(src, d, "SKILL.md")))
    parts = [body.strip()]
    rdir = os.path.join(src, d, "references")
    if os.path.isdir(rdir):
        parts += [read(os.path.join(rdir, f)).strip() for f in sorted(os.listdir(rdir))]
    write(os.path.join(art, "references", f"{d}.md"), "\n\n".join(parts) + "\n")
    index.append(f"| `references/{d}.md` | {fields.get('description', '')} |")
write(os.path.join(art, "SKILL.md"), front(
    "blender-art-direction",
    "Short art-direction notes for Blender work in one style, mood, world, era or game genre: retro 8-bit and 16-bit, HD-2D, chibi, noir, cozy, dark and gritty, brutalist, neon retro-futurism, cosmic and folk horror, liminal spaces, fantasy, sci-fi, apocalypse, biomes, and genres such as RPG, shooter, soulslike, stealth, racing and strategy. Use when the game's look or genre is one of these, alongside the modelling, materials and lighting skills.")
    + "\n# Blender art direction\n\nOne note per direction. Read the one that matches the game's look or genre, then use the craft skills"
    + " (`blender-core` routes to them) to carry it out.\n\n| Note | What it's for |\n|---|---|\n"
    + "\n".join(index) + "\n")

# Fixes, across everything copied.
stub_names = "|".join(re.escape(s) for s in sorted(stubs, key=len, reverse=True))
fixes = [
    (r"\.\./references/", "../blender-references/"),
    (r"`get_viewport_screenshot`", "`get_screenshot_of_area_as_image` (the 3D viewport)"),
    (r"get_viewport_screenshot", "get_screenshot_of_area_as_image"),
    (r"`get_scene_info`", "`get_objects_summary`"),
    (r"(?<![\w-])(" + stub_names + r")(?![\w-])", r"blender-art-direction (references/\1.md)"),
    (r"- Keep collision meshes clearly named `COL_` / `UCX_` style",
     "- Name collision meshes with Godot's import hints, as suffixes: `-col` (the mesh plus a trimesh collision), "
     "`-convcol` (plus a convex one), `-colonly` or `-convcolonly` (collision only). `UCX_` is Unreal's, not Godot's"),
    (r"- Subsurface Color: warmer than base color",
     "- Subsurface: Blender 4 and later have no separate Subsurface Color (it uses the base colour); "
     "set Subsurface Weight, with a warm Subsurface Radius"),
    (r"- \[ \] Object faces correct forward axis for target engine \(typically -Y or \+X\)",
     "- [ ] Object faces the right way for the target engine, checked by looking (the `model-facing` skill)"),
]
# Links to a covered skill go to the one that covers it. A hyphenated name can't be anything else;
# a one-word one (materials, lighting) only when it's written as a skill: in backticks, a table cell or a chain.
for name, to in COVERED.items():
    if "-" in name:
        fixes.append((r"(?<![\w/-])" + re.escape(name) + r"(?![\w-])", to))
    else:
        fixes.append((r"`" + re.escape(name) + r"`", f"`{to}`"))
        fixes.append((r"(?<=\| )" + re.escape(name) + r"(?= \||,)", to))
        fixes.append((r"(?<=, )" + re.escape(name) + r"(?= \||,|$)", to))
        fixes.append((r"(?<=→ )" + re.escape(name) + r"(?=\s|$)", to))

for root, _, files in os.walk(dest):
    if os.path.basename(root) == "references" and os.path.dirname(root) == art:
        continue  # The stubs' own notes keep their names.
    for f in files:
        p = os.path.join(root, f)
        text = read(p)
        fixed = text
        for pattern, to in fixes:
            if p.endswith(os.path.join("blender-art-direction", "SKILL.md")) and "blender-art-direction" in to:
                continue
            fixed = re.sub(pattern, to, fixed)
        if fixed != text:
            write(p, fixed)

shutil.copy(os.path.join(repo, "LICENSE"), os.path.join(dest, "LICENSE-blender-skills.txt"))
write(os.path.join(dest, "README.md"), readme)
print(f"kept {len(kept)}, gathered {len(stubs)} stubs into blender-art-direction, plus blender-references")
