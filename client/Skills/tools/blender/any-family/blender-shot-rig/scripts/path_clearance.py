"""Camera-path clearance: on every frame, how close the camera comes to the set's solid meshes. One Blender session,
one BVH per mesh, every frame of the move (a camera passing through a wall is the usual flythrough failure).
Left out, as a real camera moves through them: volumes and volume-only materials, particles, objects marked
no_clearance or no_support, anything smaller than --min-size, hidden from render, or matching --skip.
Usage:
  blender -b rigged.blend -P path_clearance.py -- [--camera CR_walk_cam] [--frames 1 120] [--limit 0.45]
      [--min-size 0.05] [--skip Glass Dust] [--report //clearance.json]
  MCP: import path_clearance; print(path_clearance.run(camera="CR_walk_cam", limit=0.45))
Returns {"pass", "camera", "min_clearance_m", "nearest", "frame", "frames_under_limit": [...], "worst": [...]}.
Build a camrig shot's rig first (shoot.py --rig-only --save rigged.blend), or check any animated camera.
"""
import os
import sys

import bpy
from mathutils.bvhtree import BVHTree

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _anyx as ax  # noqa: E402


def _volume_only(o):
    ms = [m for m in o.data.materials if m and m.node_tree] if o.type == "MESH" else []
    outs = [next((n for n in m.node_tree.nodes if n.type == "OUTPUT_MATERIAL" and n.is_active_output), None) for m in ms]
    return bool(ms) and all(x is not None and not x.inputs["Surface"].is_linked and x.inputs["Volume"].is_linked for x in outs)


def run(camera=None, frames=None, limit=0.45, min_size=0.05, skip=(), report=None):
    sc = bpy.context.scene
    cam = bpy.data.objects[camera] if camera else sc.camera
    if cam is None:
        raise ValueError("no camera: pass --camera or set the scene camera")
    f0, f1 = frames or (sc.frame_start, sc.frame_end)
    dg = bpy.context.evaluated_depsgraph_get()
    trees = []
    for o in sc.objects:
        if o.type not in {"MESH", "CURVE", "FONT"} or not o.visible_get() or o.hide_render or o.get("no_clearance") \
                or o.get("no_support") or _volume_only(o) or any(k.lower() in o.name.lower() for k in skip) \
                or max(o.dimensions) < min_size or any(m.type == "PARTICLE_SYSTEM" for m in getattr(o, "modifiers", [])):
            continue
        eo = o.evaluated_get(dg)
        me = eo.to_mesh()
        if me is not None and len(me.polygons):
            me.transform(eo.matrix_world)
            trees.append((o.name, BVHTree.FromPolygons([v.co.copy() for v in me.vertices], [tuple(p.vertices) for p in me.polygons])))
        eo.to_mesh_clear()
    worst, under, per = (float("inf"), None, None), [], []
    for f in range(int(f0), int(f1) + 1):
        sc.frame_set(f)
        p = cam.matrix_world.translation.copy()
        best = (float("inf"), None)
        for name, t in trees:
            hit = t.find_nearest(p, max(limit * 4, 2.0))
            if hit[0] is not None and hit[3] < best[0]:
                best = (hit[3], name)
        per.append((best[0], f, best[1]))
        if best[0] < worst[0]:
            worst = (best[0], best[1], f)
        if best[0] < limit:
            under.append(f)
    near = worst[0] if worst[1] else None
    result = {"pass": not under, "camera": cam.name, "frames": [int(f0), int(f1)], "limit_m": limit,
              "meshes_checked": len(trees), "min_clearance_m": round(near, 3) if near is not None else None,
              "nearest": worst[1], "frame": worst[2], "frames_under_limit": under,
              "worst": [{"frame": f, "clearance_m": round(d, 3), "nearest": n} for d, f, n in sorted(per)[:5] if n]}
    if report:
        result["report"] = ax.write_json(report, result)
    return result


if __name__ == "__main__":
    p = ax.parser(__doc__)
    p.add_argument("--camera")
    p.add_argument("--frames", nargs=2, type=int)
    p.add_argument("--limit", type=float, default=0.45)
    p.add_argument("--min-size", dest="min_size", type=float, default=0.05)
    p.add_argument("--skip", nargs="*", default=[])
    p.add_argument("--report")
    ax.cli(run, p)
