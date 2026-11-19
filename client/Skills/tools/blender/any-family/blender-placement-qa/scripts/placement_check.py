"""Placement check for a dressed scene: props that float, sink or clip, measured at their real contact points.

Checks, per prop (a root mesh object, or an empty with mesh children such as a glTF import):
  FLOAT      the lowest vertices (feet, legs, base rings) are more than --tol above whatever is under them
  SUNK       they are more than --sink below the surface under them
  NOTHING    nothing at all under them
  GROUND     a ground-only prop (furniture) stands on another prop instead of the ground
  CLIP       two props intersect (more than --clip-pairs triangle pairs, default 0), the higher one tested 3 mm up so
             resting on each other isn't a clip; raise it to about 30 for dense scanned or generated meshes
  STRUCTURE  a prop intersects the fixed set (walls, counters, shelves), also tested 3 mm up
  SCALE      the prop's height is more than 15% off its stated real height (custom property real_h, or --heights)
  OUT        the prop is outside --bounds
Rays skip the prop itself and anything marked not to count as a support: custom property no_support (hanging signs,
lamps, cables), volume objects and volume-only materials (haze, steam), and --skip name patterns.
A report that is right as it is goes in --accept with its reason ("CLIP Wok x Range=the wok sits in the burner well").
Usage:
  blender -b f.blend -P placement_check.py -- [--objects A B | --collection Props] [--fixed Walls Counters]
      [--ground Floor Street] [--ground-only Chair Table] [--hanging Sign Lamp] [--skip Decal_]
      [--heights //heights.json] [--bounds -5 -5 -0.1 5 5 4] [--accept "REGEX=reason" ...]
      [--tol 0.01] [--sink 0.03] [--max-gap 0.6] [--clip-pairs 0] [--fix] [--report //placement.json] [--save out.blend]
  MCP: import placement_check as pc; print(pc.run(collection="Props", fixed=["Walls"], report="//placement.json"))
Helpers for dressing scripts: contacts(o), contact_gap(o), drop(o, start_z), settle(o).
Returns {"pass", "checked", "open", "accepted", "issues": [...]}. --fix lowers each floater by its smallest gap
(never up), then settles it; it doesn't move parented objects.
"""
import itertools
import json
import os
import re
import sys

import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _anyx as ax  # noqa: E402

DOWN = Vector((0, 0, -1))


# ------------------------------------------------------------------ what counts
def _volume_only(o):
    """A volume object, or a mesh whose every material feeds only the Volume output (haze boxes, steam)."""
    if o.type == "VOLUME":
        return True
    if o.type != "MESH" or not o.data.materials:
        return False
    for m in o.data.materials:
        if m is None or not m.node_tree:
            return False
        out = next((n for n in m.node_tree.nodes if n.type == "OUTPUT_MATERIAL" and n.is_active_output), None)
        if out is None or out.inputs["Surface"].is_linked or not out.inputs["Volume"].is_linked:
            return False
    return True


def _not_support(o, skip=()):
    n = o.name.lower()
    return bool(o.get("no_support")) or _volume_only(o) or any(s.lower() in n for s in skip)


def _family(o):
    return [o, *o.children_recursive]


def _meshes(o):
    return [x for x in _family(o) if x.type == "MESH"]


# ------------------------------------------------------------------ contact helpers (usable from dressing scripts)
_SUPPORTS = {}  # object name -> (key, bvh, xy-min, xy-max, z-min): rebuilt when the object moves or the frame changes


def _supports(dg, skip_names):
    """One BVH per object that can hold something up. Per object, not one scene ray, so a prop's own bottom face
    lying exactly on the floor can't hide the floor from a ray that skips the prop."""
    sc = bpy.context.scene
    out = []
    for o in sc.objects:
        if o.type != "MESH" or not o.visible_get() or o.hide_render or _not_support(o, skip_names):
            continue
        key = (sc.frame_current, tuple(round(v, 6) for row in o.matrix_world for v in row), len(o.data.vertices))
        had = _SUPPORTS.get(o.name)
        if had is None or had[0] != key:
            ev = o.evaluated_get(dg)
            me = ev.to_mesh()
            mw = ev.matrix_world
            vs = [mw @ v.co for v in me.vertices]
            me.calc_loop_triangles()
            tris = [tuple(t.vertices) for t in me.loop_triangles]
            ev.to_mesh_clear()
            if not tris:
                _SUPPORTS.pop(o.name, None)
                continue
            lo = Vector((min(v.x for v in vs), min(v.y for v in vs), min(v.z for v in vs)))
            hi = Vector((max(v.x for v in vs), max(v.y for v in vs), max(v.z for v in vs)))
            had = _SUPPORTS[o.name] = (key, BVHTree.FromPolygons(vs, tris), lo, hi)
        out.append((o, had[1], had[2], had[3]))
    return out


def _ray_down(dg, p, skip=(), skip_names=()):
    """Highest surface straight below p, ignoring no_support things, volumes and the objects in skip."""
    best = None
    for o, t, lo, hi in _supports(dg, skip_names):
        if o in skip or lo.z > p.z or not (lo.x - 1e-4 <= p.x <= hi.x + 1e-4 and lo.y - 1e-4 <= p.y <= hi.y + 1e-4):
            continue
        loc, _n, _i, d = t.ray_cast(p, DOWN, 1000.0)
        if loc is not None and (best is None or d < best[0]):
            best = (d, loc.z, o)
    return (best[1], best[2]) if best else (None, None)


def contacts(o, n=40, every=False):
    """World-space contact candidates: the lowest vertices (within 1.5 cm or 3% of the height), at most n of them.
    every=True: a spread of all the vertices instead."""
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    vs = []
    for ob in _meshes(o):
        ev = ob.evaluated_get(dg)
        me = ev.to_mesh()
        mw = ev.matrix_world
        vs += [mw @ v.co for v in me.vertices]
        ev.to_mesh_clear()
    if not vs:
        return []
    if every:
        return vs[:: max(1, len(vs) // n)]
    zmin = min(v.z for v in vs)
    band = max(0.015, 0.03 * (max(v.z for v in vs) - zmin))
    feet = [v for v in vs if v.z <= zmin + band]
    return feet[:: max(1, len(feet) // n)]


def contact_gap(o, pts=None, skip_names=()):
    """(gap, support object) for the contact point closest to what's under it: > 0 floats, < 0 is sunk.
    (None, None) when nothing is below."""
    dg = bpy.context.evaluated_depsgraph_get()
    own = set(_family(o))
    best = None
    for f in pts or contacts(o):
        # start just above the point, so a thin top surface doesn't report its own thickness
        z, hit = _ray_down(dg, f + Vector((0, 0, 0.002)), own, skip_names)
        if z is not None and (best is None or f.z - z < best[0]):
            best = (f.z - z, hit)
    return best or (None, None)


def _bbox(o):
    pts = [x.matrix_world @ Vector(c) for x in _family(o) if x.type in {"MESH", "CURVE", "FONT"} for c in x.bound_box]
    if not pts:
        p = o.matrix_world.translation
        return p.copy(), p.copy()
    return (Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts))),
            Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts))))


def settle(o, skip_names=()):
    """Move down (never up) by the smallest clearance of the feet and a spread of all vertices, so the first point
    to touch stops it. By the feet alone, a plate over a table's raised rim sinks into the rim. Returns how far it
    moved."""
    g, _on = contact_gap(o, contacts(o) + contacts(o, 400, every=True), skip_names)
    if g is None or g <= 0.002:
        return 0.0
    o.location.z -= g - 0.0005
    bpy.context.view_layer.update()
    return g - 0.0005


def drop(o, start_z=None, skip_names=()):
    """Rest the object on the highest surface under its footprint (a 5x5 grid of rays from start_z), then settle it
    by its contact points. A footprint drop alone puts a stool on a table top whenever the table is under its box.
    Returns the gap left under it (about 0 when it rests on something; None when nothing is below)."""
    bpy.context.view_layer.update()
    lo, hi = _bbox(o)
    start_z = hi.z + 2.0 if start_z is None else start_z
    dg = bpy.context.evaluated_depsgraph_get()
    own = set(_family(o))
    top = None
    for i in range(5):
        for j in range(5):
            x = lo.x + (hi.x - lo.x) * (0.1 + 0.8 * i / 4)
            y = lo.y + (hi.y - lo.y) * (0.1 + 0.8 * j / 4)
            z, _hit = _ray_down(dg, Vector((x, y, start_z)), own, skip_names)
            if z is not None and (top is None or z > top):
                top = z
    if top is None:
        return None
    o.location.z += top - lo.z + 0.0005
    bpy.context.view_layer.update()
    settle(o, skip_names)
    return contact_gap(o, skip_names=skip_names)[0]


# ------------------------------------------------------------------ the check
def _tree(o, dg, lift=0.0):
    verts, tris = [], []
    for ob in _meshes(o):
        ev = ob.evaluated_get(dg)
        me = ev.to_mesh()
        me.transform(Matrix.Translation((0, 0, lift)) @ ev.matrix_world)
        me.calc_loop_triangles()
        off = len(verts)
        verts += [v.co.copy() for v in me.vertices]
        tris += [tuple(off + i for i in t.vertices) for t in me.loop_triangles]
        ev.to_mesh_clear()
    return BVHTree.FromPolygons(verts, tris) if tris else None


def _overlap(a, b):
    (la, ha), (lb, hb) = a, b
    return not any(ha[i] < lb[i] or hb[i] < la[i] for i in range(3))


def _in(o, names):
    """True when o is named (by substring) or sits in a collection named in `names`."""
    n = o.name.lower()
    return any(k.lower() in n for k in names) or any(c.name in names for c in o.users_collection)


def _props(objects, collection, fixed, ground, skip):
    if objects or collection:
        pool = ax.objects(objects, collection, types=None)
    else:
        pool = list(bpy.context.scene.objects)
    out = []
    for o in pool:
        if o.parent is not None and (objects is None or o.name not in objects):
            continue  # moves with its parent; checked as part of it
        if not o.visible_get() or o.hide_render or not _meshes(o):
            continue
        if _in(o, fixed) or _in(o, ground) or _not_support(o, skip):
            continue
        out.append(o)
    return out


def run(objects=None, collection=None, fixed=(), ground=(), ground_only=(), hanging=(), skip=(), heights=None,
        bounds=None, accept=(), tol=0.01, sink=0.03, max_gap=0.6, clip_pairs=0, structure_pairs=0, fix=False,
        report=None, save=None):
    fixed, ground, ground_only, hanging, skip = (list(x or ()) for x in (fixed, ground, ground_only, hanging, skip))
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    if isinstance(heights, str):
        with open(ax.path(heights), encoding="utf-8") as f:
            heights = json.load(f)
    heights = heights or {}
    rules = []
    for a in accept or ():
        pat, _, why = a.partition("=")
        rules.append((re.compile(pat), why or "accepted"))
    props = _props(objects, collection, fixed, ground, skip)
    issues = []

    def add(kind, o, text, **kw):
        why = next((w for p, w in rules if p.search(f"{kind} {text}")), None)
        issues.append(dict(kind=kind, object=o.name, text=f"{kind} {text}", accepted=why, **kw))

    for o in props:
        if not (o.get("hanging") or _in(o, hanging)):
            g, on = contact_gap(o, skip_names=skip)
            if g is None:
                add("NOTHING", o, f"{o.name}: nothing below it")
            elif tol < g <= max_gap:
                add("FLOAT", o, f"{o.name}: {g * 100:.1f} cm above {on.name}", gap_m=round(g, 4), on=on.name)
                if fix and o.parent is None:
                    o.location.z -= g
                    settle(o, skip)
                    issues[-1]["fixed"] = True
            elif g > max_gap:
                add("FLOAT", o, f"{o.name}: {g:.2f} m above {on.name} (over --max-gap: hung on purpose? mark it hanging)",
                    gap_m=round(g, 4), on=on.name)
            elif g < -sink:
                add("SUNK", o, f"{o.name}: {-g * 100:.1f} cm into {on.name}", gap_m=round(g, 4), on=on.name)
            if g is not None and (o.get("ground_only") or _in(o, ground_only)) and ground and not _in(on, ground):
                add("GROUND", o, f"{o.name}: stands on {on.name}, not the ground", on=on.name)
        want = o.get("real_h") or heights.get(o.name)
        if want:
            lo, hi = _bbox(o)
            got = hi.z - lo.z
            if abs(got - want) / want > 0.15:
                add("SCALE", o, f"{o.name}: {got:.2f} m tall, should be {want:.2f} m", height_m=round(got, 3))
        if bounds:
            lo, hi = _bbox(o)
            if any(lo[i] < bounds[i] for i in range(3)) or any(hi[i] > bounds[i + 3] for i in range(3)):
                add("OUT", o, f"{o.name}: outside the set's bounds", min=ax.r(lo, 3), max=ax.r(hi, 3))

    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    boxes = {o.name: _bbox(o) for o in props}
    trees = {}

    def tree(o, lift=0.0):
        k = (o.name, lift)
        if k not in trees:
            trees[k] = _tree(o, dg, lift)
        return trees[k]

    for a, b in itertools.combinations(props, 2):
        if not _overlap(boxes[a.name], boxes[b.name]):
            continue
        # lift the higher one 3 mm, so a mug resting on a crate is contact, not a clip
        up, base = (a, b) if boxes[a.name][0].z >= boxes[b.name][0].z else (b, a)
        ta, tb = tree(up, 0.003), tree(base)
        n = len(ta.overlap(tb)) if ta and tb else 0
        if n > clip_pairs:
            add("CLIP", a, f"{a.name} x {b.name}: {n} triangle pairs", other=b.name, pairs=n)
    structure = [o for o in bpy.context.scene.objects if o.type == "MESH" and o.visible_get() and _in(o, fixed)]
    for o in props:
        for s in structure:
            if not _overlap(boxes[o.name], _bbox(s)):
                continue
            to, ts = tree(o, 0.003), tree(s)
            n = len(to.overlap(ts)) if to and ts else 0
            if n > structure_pairs:
                add("STRUCTURE", o, f"{o.name} x {s.name}: {n} triangle pairs", other=s.name, pairs=n)

    open_ = [i for i in issues if not i["accepted"] and not i.get("fixed")]
    result = {"pass": not open_, "checked": len(props), "open": len(open_),
              "accepted": sum(1 for i in issues if i["accepted"]), "fixed": sum(1 for i in issues if i.get("fixed")),
              "issues": issues}
    if report:
        result["report"] = ax.write_json(report, result)
    if save:
        bpy.ops.wm.save_as_mainfile(filepath=ax.path(save), copy=True)
        result["saved"] = ax.path(save)
    return result


if __name__ == "__main__":
    p = ax.parser(__doc__)
    p.add_argument("--objects", nargs="*")
    p.add_argument("--collection")
    p.add_argument("--fixed", nargs="*", default=[], help="collections or name parts of the fixed set")
    p.add_argument("--ground", nargs="*", default=[], help="collections or name parts that are the ground")
    p.add_argument("--ground-only", dest="ground_only", nargs="*", default=[])
    p.add_argument("--hanging", nargs="*", default=[])
    p.add_argument("--skip", nargs="*", default=[])
    p.add_argument("--heights")
    p.add_argument("--bounds", nargs=6, type=float)
    p.add_argument("--accept", nargs="*", default=[], help='"REGEX=reason" pairs')
    p.add_argument("--tol", type=float, default=0.01)
    p.add_argument("--sink", type=float, default=0.03)
    p.add_argument("--max-gap", dest="max_gap", type=float, default=0.6)
    p.add_argument("--clip-pairs", dest="clip_pairs", type=int, default=0)
    p.add_argument("--structure-pairs", dest="structure_pairs", type=int, default=0)
    p.add_argument("--fix", action="store_true")
    p.add_argument("--report")
    p.add_argument("--save")
    ax.cli(run, p)
