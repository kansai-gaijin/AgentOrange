"""Re-import an exported GLB into an empty scene and compare it with the source .blend: bones, meshes and actions
counted, and every action posed in both at the same frames and matched vertex by vertex.

What it reports:
  gltf        what the file itself holds (nodes, meshes, skins and their joints, animations, extras), read from its
              JSON chunk, so it's the file and not Blender's reading of it
  bones       source vs GLB bone names: missing, extra
  meshes      source vs GLB mesh objects, and vertex totals
  actions     each source action found in the GLB (by name), with both frame ranges
  pose_match  per action, at --samples frames spread over it: the distance from each GLB vertex to the nearest source
              vertex and back, as median / p99 / max in mm (a lost, scaled or shifted action shows here)
  motion_mm   per GLB action, how far its vertices move over the clip (0 means the action came out empty)
The source is the armatures (and what they deform or carry) when there are any, else every mesh; --objects narrows
it. Actions are those the source objects use (active or in the NLA), plus any posing their bones.
Usage:
  blender -b --factory-startup -P verify_reimport.py -- --source rig.blend --glb export/rig.glb
      [--objects Rig] [--actions Walk Idle] [--samples 5] [--tol-mm 1.0] [--report //verify.json]
It loads files, so run it headless; it never saves anything. Returns {"pass", ...}.
"""
import json
import os
import struct
import sys

import bpy
from mathutils.kdtree import KDTree

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _anyx as ax  # noqa: E402


# ------------------------------------------------------------------ the GLB file itself
def gltf_summary(path):
    with open(path, "rb") as f:
        magic, _version, _length = struct.unpack("<III", f.read(12))
        if magic != 0x46546C67:
            raise ValueError(f"{path} is not a GLB")
        n, kind = struct.unpack("<II", f.read(8))
        doc = json.loads(f.read(n).decode("utf-8"))
    nodes = doc.get("nodes", [])
    return {
        "nodes": len(nodes),
        "meshes": len(doc.get("meshes", [])),
        "primitives": sum(len(m.get("primitives", [])) for m in doc.get("meshes", [])),
        "morph_targets": max((len(p.get("targets", [])) for m in doc.get("meshes", []) for p in m.get("primitives", [])), default=0),
        "skins": [{"name": s.get("name", ""), "joints": len(s.get("joints", []))} for s in doc.get("skins", [])],
        "animations": [{"name": a.get("name", ""), "channels": len(a.get("channels", []))} for a in doc.get("animations", [])],
        "materials": len(doc.get("materials", [])),
        "images": len(doc.get("images", [])),
        "extras_on_nodes": sorted({k for nd in nodes for k in nd.get("extras", {})}),
    }


# ------------------------------------------------------------------ scene helpers
def _fcurve_paths(act):
    paths = set()
    fcs = getattr(act, "fcurves", None)
    if fcs is not None:  # older layout
        return {fc.data_path for fc in fcs}
    for layer in act.layers:
        for strip in layer.strips:
            for bag in strip.channelbags:
                paths |= {fc.data_path for fc in bag.fcurves}
    return paths


def _bones_in(act):
    out = set()
    for p in _fcurve_paths(act):
        if p.startswith('pose.bones["'):
            out.add(p.split('"')[1])
    return out


def _assign(ob, act):
    """Make act the active action of ob (NLA muted), picking the slot that's for ob."""
    ad = ob.animation_data or ob.animation_data_create()
    for t in ad.nla_tracks:
        t.mute = True
    ad.action = act
    slots = list(getattr(act, "slots", []) or [])
    if act is not None and slots and getattr(ad, "action_slot", None) is None:
        mine = [s for s in slots if ob.name in getattr(s, "name_display", "")] or slots
        ad.action_slot = mine[0]


def _source(objects):
    sc = bpy.context.scene
    pool = [bpy.data.objects[n] for n in objects] if objects else list(sc.objects)
    if objects:
        pool = [x for o in pool for x in (o, *o.children_recursive)]
    arms = [o for o in pool if o.type == "ARMATURE"]
    if arms:
        meshes = [o for o in pool if o.type == "MESH" and (
            any(m.type == "ARMATURE" and m.object in arms for m in o.modifiers)
            or any(p in arms for p in _parents(o)))]
    else:
        meshes = [o for o in pool if o.type == "MESH"]
    return arms, meshes


def _parents(o):
    while o.parent is not None:
        o = o.parent
        yield o


def _actions_for(arms, meshes, only):
    found = []
    for o in [*arms, *meshes]:
        ad = o.animation_data
        if ad:
            if ad.action:
                found.append(ad.action)
            found += [s.action for t in ad.nla_tracks for s in t.strips if s.action]
    bones = {b.name for a in arms for b in a.data.bones}
    found += [a for a in bpy.data.actions if _bones_in(a) and _bones_in(a) <= bones]
    seen, out = set(), []
    for a in found:
        if a.name not in seen and (not only or a.name in only):
            seen.add(a.name)
            out.append(a)
    return out


def _snap(meshes):
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    pts = []
    for o in meshes:
        oe = o.evaluated_get(dg)
        me = oe.to_mesh()
        mw = oe.matrix_world
        pts += [mw @ v.co for v in me.vertices]
        oe.to_mesh_clear()
    return pts


def _frames(act, n):
    f0, f1 = act.frame_range
    if n <= 1 or f1 <= f0:
        return [int(round(f0))]
    return sorted({int(round(f0 + (f1 - f0) * i / (n - 1))) for i in range(n)})


def _err(a, b):
    """Distances from each point of b to the nearest of a, and back: (median, p99, max) in mm."""
    def one(src, dst):
        kd = KDTree(len(src))
        for i, p in enumerate(src):
            kd.insert(p, i)
        kd.balance()
        return [kd.find(p)[2] for p in dst]
    if not a or not b:
        return {"median_mm": None, "p99_mm": None, "max_mm": None}
    e = sorted(one(a, b) + one(b, a))
    return {"median_mm": round(e[len(e) // 2] * 1000, 3), "p99_mm": round(e[int(len(e) * 0.99)] * 1000, 3),
            "max_mm": round(e[-1] * 1000, 3)}


# ------------------------------------------------------------------ the check
def run(glb, source=None, objects=None, actions=None, samples=5, tol_mm=1.0, report=None):
    ax.headless_only("verify_reimport")
    glb = ax.path(glb)
    if source:
        bpy.ops.wm.open_mainfile(filepath=ax.path(source))
    sc = bpy.context.scene
    fps, fps_base = sc.render.fps, sc.render.fps_base
    arms, meshes = _source(objects)
    acts = _actions_for(arms, meshes, set(actions or ()))
    src = {
        "bones": sorted(b.name for a in arms for b in a.data.bones),
        "meshes": sorted(o.name for o in meshes),
        "vertices": sum(len(o.data.vertices) for o in meshes),
        "actions": {a.name: ax.r(list(a.frame_range), 2) for a in acts},
    }
    poses = {}
    if acts:
        for act in acts:
            for a in arms:
                _assign(a, act)
            f0 = act.frame_range[0]
            for f in _frames(act, samples):
                sc.frame_set(f)
                poses[(act.name, f - f0)] = _snap(meshes)
            for a in arms:
                _assign(a, None)
    else:
        sc.frame_set(sc.frame_start)
        poses[("rest", 0)] = _snap(meshes)

    # the GLB, alone in an empty scene at the source's frame rate (glTF keeps seconds)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.render.fps, sc.render.fps_base = fps, fps_base
    bpy.ops.import_scene.gltf(filepath=glb)
    g_arms = [o for o in sc.objects if o.type == "ARMATURE"]
    shapes = {pb.custom_shape for a in g_arms for pb in a.pose.bones if pb.custom_shape}
    g_meshes = [o for o in sc.objects if o.type == "MESH" and o not in shapes]  # the importer's bone-shape sphere
    animated = [o for o in sc.objects if o.animation_data]
    g = {
        "bones": sorted(b.name for a in g_arms for b in a.data.bones),
        "meshes": sorted(o.name for o in g_meshes),
        "vertices": sum(len(o.data.vertices) for o in g_meshes),
        "actions": {a.name: ax.r(list(a.frame_range), 2) for a in bpy.data.actions},
    }

    def match(name):
        if name in bpy.data.actions:
            return bpy.data.actions[name]
        return next((a for a in bpy.data.actions if a.name.startswith(name + "_") or a.name.startswith(name + ".")), None)

    res = {"glb": glb, "size_kb": os.path.getsize(glb) // 1024, "gltf": gltf_summary(glb),
           "bones": {"source": len(src["bones"]), "glb": len(g["bones"]),
                     "missing": sorted(set(src["bones"]) - set(g["bones"])), "extra": sorted(set(g["bones"]) - set(src["bones"]))},
           "meshes": {"source": src["meshes"], "glb": g["meshes"], "source_vertices": src["vertices"], "glb_vertices": g["vertices"]},
           "actions": {}, "pose_match": {}, "motion_mm": {}}
    worst = 0.0
    for name in src["actions"] or ["rest"]:
        if name == "rest":
            gact = None
        else:
            gact = match(name)
            res["actions"][name] = {"source_range": src["actions"][name], "glb": gact.name if gact else None,
                                    "glb_range": ax.r(list(gact.frame_range), 2) if gact else None}
            if gact is None:
                continue
            for o in animated:
                _assign(o, gact)
        g0 = gact.frame_range[0] if gact else sc.frame_start
        for (an, off), pts in poses.items():
            if an != name:
                continue
            sc.frame_set(int(round(g0 + off)))
            e = _err(pts, _snap(g_meshes))
            res["pose_match"][f"{name}@+{int(off)}"] = e
            worst = max(worst, e["max_mm"] or 0.0)
        if gact:
            sc.frame_set(int(round(gact.frame_range[0])))
            a = _snap(g_meshes)
            moved = 0.0
            for f in _frames(gact, samples)[1:]:
                sc.frame_set(f)
                b = _snap(g_meshes)
                moved = max(moved, max(((p - q).length for p, q in zip(a, b)), default=0.0))
            res["motion_mm"][gact.name] = round(moved * 1000, 2)
    missing_actions = [n for n, v in res["actions"].items() if v["glb"] is None]
    empty = [n for n, v in res["motion_mm"].items() if v == 0.0]
    problems = []
    if res["bones"]["missing"]:
        problems.append(f"bones missing in the GLB: {res['bones']['missing']}")
    if len(g["meshes"]) < len(src["meshes"]):
        problems.append(f"{len(src['meshes'])} source meshes, {len(g['meshes'])} in the GLB")
    if missing_actions:
        problems.append(f"actions missing in the GLB: {missing_actions}")
    if empty:
        problems.append(f"actions that don't move anything: {empty}")
    if worst > tol_mm:
        problems.append(f"pose mismatch up to {worst:.2f} mm (tolerance {tol_mm} mm)")
    res["problems"] = problems
    res["pass"] = not problems
    if report:
        res["report"] = ax.write_json(report, res)
    return res


if __name__ == "__main__":
    p = ax.parser(__doc__)
    p.add_argument("--glb", required=True)
    p.add_argument("--source")
    p.add_argument("--objects", nargs="*")
    p.add_argument("--actions", nargs="*")
    p.add_argument("--samples", type=int, default=5)
    p.add_argument("--tol-mm", dest="tol_mm", type=float, default=1.0)
    p.add_argument("--report")
    ax.cli(run, p)
