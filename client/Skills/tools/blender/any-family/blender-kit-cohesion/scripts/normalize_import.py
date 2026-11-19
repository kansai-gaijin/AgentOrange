"""Turn an imported or downloaded GLB into a kit item: one object, real metres, bottom-centre origin, front facing -Y,
textures at most --tex pixels, exported as <out>/<id>.glb with a stats.json (dims, triangles, textures).

One item:
  blender -b --factory-startup -P normalize_import.py -- --glb downloads/lamp.glb --id desk_lamp --out kit/items/desk_lamp
      (--h 0.45 | --fit 0.40 0.30 0.32) [--yaw 90] [--tex 2048]
Many items in ONE Blender session, writing kit.json in the same pass (much faster than a launch per item):
  blender -b --factory-startup -P normalize_import.py -- --jobs kit/prep.json [--items kit/items] [--kit kit/kit.json]
  (both default to beside prep.json)
  prep.json: {"name": "...", "style": "<the shared style paragraph>",
              "items": [{"id", "label", "glb", "h" | "fit": [x, y, z], "yaw"?, "tags"?, "why"?}, ...]}
  Items whose GLB isn't there yet are skipped, and ones already done from the same GLB and prep.json are kept, so
  run it again as more arrive. kit.json is rewritten from every item that has a stats.json, in prep.json's order.
--h scales uniformly to that height; --fit scales each axis to those dims (for boxy props whose depth is wrong).
--yaw turns the mesh (degrees about Z) when its front isn't -Y; check the front with the model-facing skill.
Triangle budgets and LODs aren't done here: blender-game-optimization's make_lods.py does those.
It resets the open file, so run it headless. Returns the item's stats, or {"kit", "items", "waiting", "failed"}.
"""
import json
import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _anyx as ax  # noqa: E402


def _bbox(ob):
    vs = [v.co for v in ob.data.vertices]
    return (Vector((min(v.x for v in vs), min(v.y for v in vs), min(v.z for v in vs))),
            Vector((max(v.x for v in vs), max(v.y for v in vs), max(v.z for v in vs))))


def _tris(ob):
    ob.data.calc_loop_triangles()
    return len(ob.data.loop_triangles)


def normalize(glb, iid, out, h=None, fit=None, yaw=0.0, tex=2048):
    glb, out = ax.path(glb), ax.path(out)  # Blender resolves relative paths against the wrong folder: keep them absolute
    os.makedirs(out, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=glb)
    sc = bpy.context.scene
    meshes = [o for o in sc.objects if o.type == "MESH"]
    if not meshes:
        raise RuntimeError(f"no meshes in {glb}")
    # Bake every transform (glTF parents and their QUATERNION rotations: rotation_euler is ignored on glTF imports)
    # into the mesh data, then join everything into one object.
    for o in meshes:
        mw = o.matrix_world.copy()
        o.parent = None
        o.data.transform(mw)
        o.matrix_world = Matrix.Identity(4)
    for o in [o for o in sc.objects if o.type != "MESH"]:
        bpy.data.objects.remove(o)
    for o in sc.objects:
        o.select_set(o in meshes)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    ob.name = ob.data.name = iid
    ob.rotation_mode = "XYZ"
    if yaw:  # turn the mesh data, never the object, so the export carries no rotation
        ob.data.transform(Matrix.Rotation(math.radians(yaw), 4, "Z"))
    lo, hi = _bbox(ob)
    size = hi - lo
    if fit:
        s = Vector([fit[i] / max(size[i], 1e-6) for i in range(3)])
    elif h:
        s = Vector([h / max(size.z, 1e-6)] * 3)
    else:
        s = Vector((1, 1, 1))
    ob.data.transform(Matrix.Translation(-Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z))))
    ob.data.transform(Matrix.Diagonal((s.x, s.y, s.z, 1)))
    lo, hi = _bbox(ob)
    # Textures: shrink anything over tex and repack ONLY those. pack() on an untouched packed image drops its data and
    # the item renders magenta.
    textures = {}
    for img in bpy.data.images:
        if img.size[0] == 0:
            continue
        w, hh = img.size
        if max(w, hh) > tex:
            f = tex / max(w, hh)
            img.scale(max(1, int(w * f)), max(1, int(hh * f)))
            img.pack()
        textures[img.name] = list(img.size)
    for o in sc.objects:
        o.select_set(o is ob)
    path = os.path.join(out, f"{iid}.glb")
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=True)
    stats = {"id": iid, "source_glb": glb, "glb": path, "yaw_deg": yaw or 0.0, "dims_m": ax.r(hi - lo, 4),
             "tris": _tris(ob), "textures": textures}
    ax.write_json(os.path.join(out, "stats.json"), stats)
    return stats


def run(glb=None, id=None, out=None, h=None, fit=None, yaw=0.0, tex=2048, jobs=None, items=None, kit=None,
        force=False):
    ax.headless_only("normalize_import")
    if not jobs:
        if not (glb and id and out):
            raise ValueError("give --glb, --id and --out, or --jobs prep.json")
        return normalize(glb, id, out, h, fit, yaw, tex)
    jobs = ax.path(jobs)
    with open(jobs, encoding="utf-8") as f:
        prep = json.load(f)
    base = os.path.dirname(jobs)
    items_dir = ax.path(items) if items else os.path.join(base, "items")
    kit_path = ax.path(kit) if kit else os.path.join(base, "kit.json")
    waiting, failed = [], {}
    for j in prep["items"]:
        src = j["glb"] if os.path.isabs(j["glb"]) else os.path.join(base, j["glb"])
        d = os.path.join(items_dir, j["id"])
        st = os.path.join(d, "stats.json")
        if not os.path.exists(src):
            waiting.append(j["id"])
            continue
        if not force and os.path.exists(st) and os.path.getmtime(st) > max(os.path.getmtime(src), os.path.getmtime(jobs)):
            continue  # already done from this GLB and these settings
        try:
            normalize(src, j["id"], d, j.get("h"), j.get("fit"), j.get("yaw", 0.0), j.get("tex", tex))
        except Exception as e:  # keep going; report it
            failed[j["id"]] = f"{type(e).__name__}: {e}"
    done = []
    for j in prep["items"]:
        st = os.path.join(items_dir, j["id"], "stats.json")
        if os.path.exists(st):
            with open(st, encoding="utf-8") as f:
                s = json.load(f)
            done.append({"id": j["id"], "label": j.get("label", j["id"]), "glb": s["glb"], "dims_m": s["dims_m"],
                         "tris": s["tris"], "tags": j.get("tags", []), "why": j.get("why", "")})
    ax.write_json(kit_path, {"name": prep.get("name", ""), "style": prep.get("style", ""), "items": done})
    return {"kit": kit_path, "items": len(done), "of": len(prep["items"]), "waiting": waiting, "failed": failed}


if __name__ == "__main__":
    p = ax.parser(__doc__)
    p.add_argument("--glb")
    p.add_argument("--id")
    p.add_argument("--out")
    p.add_argument("--h", type=float)
    p.add_argument("--fit", nargs=3, type=float)
    p.add_argument("--yaw", type=float, default=0.0)
    p.add_argument("--tex", type=int, default=2048)
    p.add_argument("--jobs")
    p.add_argument("--items", help="default: items/ beside prep.json")
    p.add_argument("--kit", help="default: kit.json beside prep.json")
    p.add_argument("--force", action="store_true")
    ax.cli(run, p)
