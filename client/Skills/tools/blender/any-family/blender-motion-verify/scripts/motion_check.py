"""Clash, limit and foot-slip check for a rig: parts passing through each other during joint sweeps, random poses and
every frame of every action, joints going past their limits, parts going through the ground, and feet that skate.

Parts are grouped by bone: a mesh parented to a bone belongs to it, and a skinned mesh is split by each vertex's
strongest deform bone. Pairs that touch by design are left out: a bone and its parent (unless --no-adjacent), pairs
named in --ignore ("Thigh_L|Shin_L"), and any pair that already overlaps in the rest pose (listed as rest_overlaps).
  sweeps        each bone with a Limit Rotation constraint, one limited axis at a time, from --past degrees beyond
                its lower limit to beyond its upper one: clashes, and how far the evaluated angle is from the clamped
                one (a limit that doesn't hold)
  random_poses  --random poses with every limited bone inside its limits at once; clashes are warnings
  actions       every --step'th frame: clashes, the lowest point of non-foot parts below --ground-z, and per foot bone
                (--feet, with its child bones) the slip: while the foot's lowest vertex is within --contact of the
                ground on two frames running, how far that same vertex moves sideways, summed per stance
Usage:
  blender -b rig.blend -P motion_check.py -- [--armature Rig] [--actions Walk Run] [--feet Foot_L Foot_R]
      [--ground-z 0] [--contact 0.02] [--slip-mm 5] [--in-place Walk_Cycle] [--ignore "A|B"] [--random 30]
      [--past 6] [--sweep-step 3] [--step 1] [--report //motion_check.json]
  MCP: import motion_check; print(motion_check.run(armature="Rig", feet=["Foot_L", "Foot_R"]))
--in-place names cycles that walk on the spot (their feet move backwards by design), so their slip isn't judged.
The pose and active action are put back afterwards; nothing is saved. Returns {"pass", "summary", ...}.
"""
import math
import os
import random
import sys

import bpy
from mathutils import Matrix
from mathutils.bvhtree import BVHTree

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _anyx as ax  # noqa: E402

D = math.degrees


# ------------------------------------------------------------------ parts
class Parts:
    """Mesh triangles grouped by bone, rebuilt as BVH trees for the current pose."""

    def __init__(self, arm):
        self.arm = arm
        self.meshes = []  # (object, triangle groups or a fixed group name, vertex groups)
        bones = set(arm.data.bones.keys())
        deform = {b.name for b in arm.data.bones if b.use_deform}
        for o in bpy.context.scene.objects:
            if o.type != "MESH" or not o.visible_get():
                continue
            skinned = any(m.type == "ARMATURE" and m.object == arm for m in o.modifiers)
            if o.parent == arm and o.parent_type == "BONE" and o.parent_bone in bones and not skinned:
                self.meshes.append((o, o.parent_bone, None))
            elif skinned:
                names = {g.index: g.name for g in o.vertex_groups if g.name in deform}
                vgroup = []
                for v in o.data.vertices:
                    best = max(((g.weight, names[g.group]) for g in v.groups if g.group in names), default=(0, None))
                    vgroup.append(best[1] or o.name)
                o.data.calc_loop_triangles()
                tri = [vgroup[t.vertices[0]] for t in o.data.loop_triangles]
                self.meshes.append((o, tri, vgroup))
            elif o.parent == arm:
                self.meshes.append((o, o.name, None))
        self.names = sorted({g for _, t, _ in self.meshes for g in ([t] if isinstance(t, str) else t)})

    def snapshot(self):
        """{group: (world verts, triangles, source vertex ids)} for the evaluated pose."""
        bpy.context.view_layer.update()
        dg = bpy.context.evaluated_depsgraph_get()
        geo = {}
        for o, tri, vgroup in self.meshes:
            oe = o.evaluated_get(dg)
            me = oe.to_mesh()
            mw = oe.matrix_world
            me.calc_loop_triangles()
            vs = [mw @ v.co for v in me.vertices]
            same = not isinstance(tri, str) and len(me.loop_triangles) == len(tri)
            for i, t in enumerate(me.loop_triangles):
                g = tri if isinstance(tri, str) else (tri[i] if same else o.name)
                verts, tris, ids = geo.setdefault(g, ([], [], []))
                base = len(verts)
                verts += [vs[k] for k in t.vertices]
                ids += [(o.name, k) for k in t.vertices]
                tris.append((base, base + 1, base + 2))
            oe.to_mesh_clear()
        return geo


def clashes(geo, pairs):
    bvh = {g: BVHTree.FromPolygons(v, t) for g, (v, t, _) in geo.items() if t}
    out = {}
    for a, b in pairs:
        if a in bvh and b in bvh:
            n = len(bvh[a].overlap(bvh[b]))
            if n:
                out[f"{a}|{b}"] = n
    return out


# ------------------------------------------------------------------ pose helpers
def _rest(arm):
    for pb in arm.pose.bones:
        pb.matrix_basis = Matrix.Identity(4)


def _assign(ob, act):
    ad = ob.animation_data or ob.animation_data_create()
    for t in ad.nla_tracks:
        t.mute = True
    ad.action = act
    slots = list(getattr(act, "slots", []) or []) if act else []
    if slots and getattr(ad, "action_slot", None) is None:
        ad.action_slot = ([s for s in slots if ob.name in getattr(s, "name_display", "")] or slots)[0]


def _local_euler(pb):
    """The evaluated rotation of a pose bone in its own rest frame (after constraints)."""
    b = pb.bone
    if pb.parent:
        rest = pb.parent.bone.matrix_local.inverted() @ b.matrix_local
        local = (pb.parent.matrix @ rest).inverted() @ pb.matrix
    else:
        local = b.matrix_local.inverted() @ pb.matrix
    order = pb.rotation_mode if pb.rotation_mode in {"XYZ", "XZY", "YXZ", "YZX", "ZXY", "ZYX"} else "XYZ"
    return local.to_euler(order)


def _limits(arm):
    """{bone: {axis index: (min, max)}} from LOCAL-space Limit Rotation constraints."""
    out = {}
    for pb in arm.pose.bones:
        for c in pb.constraints:
            if c.type != "LIMIT_ROTATION" or c.mute or c.owner_space != "LOCAL":
                continue
            for i, ax_ in enumerate("xyz"):
                if getattr(c, f"use_limit_{ax_}"):
                    out.setdefault(pb.name, {})[i] = (getattr(c, f"min_{ax_}"), getattr(c, f"max_{ax_}"))
    return out


def _set_rot(pb, i, a):
    if pb.rotation_mode == "QUATERNION" or pb.rotation_mode == "AXIS_ANGLE":
        pb.rotation_mode = "XYZ"
    e = pb.rotation_euler.copy()
    e[i] = a
    pb.rotation_euler = e


def _actions(arm, only):
    bones = set(arm.data.bones.keys())
    found = []
    ad = arm.animation_data
    if ad:
        if ad.action:
            found.append(ad.action)
        found += [s.action for t in ad.nla_tracks for s in t.strips if s.action]
    for a in bpy.data.actions:
        paths = set()
        fcs = getattr(a, "fcurves", None)
        if fcs is not None:
            paths = {fc.data_path for fc in fcs}
        else:
            for layer in a.layers:
                for strip in layer.strips:
                    for bag in strip.channelbags:
                        paths |= {fc.data_path for fc in bag.fcurves}
        used = {p.split('"')[1] for p in paths if p.startswith('pose.bones["')}
        if used and used <= bones:
            found.append(a)
    seen, out = set(), []
    for a in found:
        if a.name not in seen and (not only or a.name in only):
            seen.add(a.name)
            out.append(a)
    return out


# ------------------------------------------------------------------ the check
def run(armature=None, actions=None, feet=None, ground_z=0.0, contact=0.02, slip_mm=5.0, in_place=None, ignore=None,
        adjacent=True, random_poses=30, past=6.0, sweep_step=3.0, step=1, penetration=0.01, report=None):
    sc = bpy.context.scene
    arms = [o for o in sc.objects if o.type == "ARMATURE"]
    if armature:
        arm = bpy.data.objects[armature]
    elif len(arms) == 1:
        arm = arms[0]
    else:
        raise ValueError(f"pass --armature: the scene has {len(arms)} armatures")
    parts = Parts(arm)
    if not parts.meshes:
        raise ValueError(f"no meshes are parented to or skinned by {arm.name}")
    G = parts.names
    skip = set()
    if adjacent:
        skip |= {frozenset((b.name, b.parent.name)) for b in arm.data.bones if b.parent}
    for pair in ignore or ():
        skip.add(frozenset(pair.split("|")))
    keep_action = arm.animation_data.action if arm.animation_data else None
    keep_slot = getattr(arm.animation_data, "action_slot", None) if arm.animation_data else None
    keep_pose = {pb.name: pb.matrix_basis.copy() for pb in arm.pose.bones}
    keep_modes = {pb.name: pb.rotation_mode for pb in arm.pose.bones}
    keep_frame = sc.frame_current
    res = {"armature": arm.name, "parts": G}
    try:
        _assign(arm, None)
        _rest(arm)
        rest = clashes(parts.snapshot(), [(a, b) for i, a in enumerate(G) for b in G[i + 1:]])
        res["rest_overlaps"] = rest
        skip |= {frozenset(k.split("|")) for k in rest}
        pairs = [(a, b) for i, a in enumerate(G) for b in G[i + 1:] if frozenset((a, b)) not in skip]
        res["pairs_checked"] = len(pairs)

        # 1. sweeps past each limit
        lims = _limits(arm)
        sweeps = {}
        for bone, axes in lims.items():
            pb = arm.pose.bones[bone]
            for i, (lo, hi) in axes.items():
                _rest(arm)
                rows, a = [], lo - math.radians(past)
                while a <= hi + math.radians(past) + 1e-9:
                    _set_rot(pb, i, a)
                    hit = clashes(parts.snapshot(), pairs)
                    got = _local_euler(pb)[i]
                    rows.append((D(a), D(abs(got - max(lo, min(hi, a)))), hit))
                    a += math.radians(sweep_step)
                _set_rot(pb, i, 0.0)
                sweeps[f"{bone}.{'xyz'[i]}"] = {
                    "limits_deg": [round(D(lo), 1), round(D(hi), 1)],
                    "clash_at_deg": [round(c, 1) for c, _, h in rows if h],
                    "clash_pairs": sorted({k for _, _, h in rows for k in h}),
                    "max_clamp_err_deg": round(max(e for _, e, _ in rows), 3)}
        _rest(arm)
        res["sweeps"] = sweeps

        # 2. random poses inside the limits
        rnd = random.Random(7)
        bad, hits = 0, {}
        for _ in range(random_poses if lims else 0):
            _rest(arm)
            for bone, axes in lims.items():
                for i, (lo, hi) in axes.items():
                    _set_rot(arm.pose.bones[bone], i, rnd.uniform(lo, hi))
            hit = clashes(parts.snapshot(), pairs)
            if hit:
                bad += 1
                for k in hit:
                    hits[k] = hits.get(k, 0) + 1
        _rest(arm)
        res["random_poses"] = {"n": random_poses if lims else 0, "with_clash": bad, "pairs": hits}

        # 3. actions
        foot_groups = {}
        for f in feet or ():
            if f not in arm.data.bones:
                raise KeyError(f"no bone {f!r} in {arm.name}")
            foot_groups[f] = {f, *(c.name for c in arm.data.bones[f].children_recursive)}
        all_feet = set().union(*foot_groups.values()) if foot_groups else set()
        acts = {}
        for act in _actions(arm, set(actions or ())):
            _assign(arm, act)
            f0, f1 = (int(round(x)) for x in act.frame_range)
            frames = list(range(f0, f1 + 1, max(1, step)))
            clash_frames, pair_hits, low, hist = [], set(), float("inf"), []
            for fr in frames:
                sc.frame_set(fr)
                geo = parts.snapshot()
                hit = clashes(geo, pairs)
                if hit:
                    clash_frames.append(fr)
                    pair_hits |= set(hit)
                for g, (v, _t, _ids) in geo.items():
                    if g not in all_feet and v:
                        low = min(low, min(p.z for p in v))
                # per foot: the lowest vertex, and every foot vertex by id, to follow the one in contact
                snap = {}
                for foot, members in foot_groups.items():
                    pts = {}
                    for g in members:
                        if g in geo:
                            v, _t, ids = geo[g]
                            pts.update(zip(ids, v))
                    if not pts:  # no mesh on the foot: its tail stands in for it
                        pts = {("tail", 0): arm.matrix_world @ arm.pose.bones[foot].tail}
                    k = min(pts, key=lambda key: pts[key].z)
                    snap[foot] = (k, pts)
                hist.append(snap)
            slip = None
            if foot_groups and act.name not in set(in_place or ()):
                per_stance, worst_frame = [], 0.0
                for foot in foot_groups:
                    acc, run_ = 0.0, 0
                    for a_, b_ in zip(hist, hist[1:]):
                        k, pa = a_[foot]
                        _k1, pb_ = b_[foot]
                        on_a = pa[k].z <= ground_z + contact
                        on_b = min(p.z for p in pb_.values()) <= ground_z + contact
                        if on_a and on_b and k in pb_:
                            d = pb_[k] - pa[k]
                            s = math.hypot(d.x, d.y)
                            acc += s
                            worst_frame = max(worst_frame, s)
                            run_ += 1
                        elif run_:
                            per_stance.append(acc)
                            acc, run_ = 0.0, 0
                    if run_:
                        per_stance.append(acc)
                slip = {"stances": len(per_stance),
                        "max_slip_per_stance_mm": round(max(per_stance) * 1000, 2) if per_stance else None,
                        "mean_slip_per_stance_mm": round(sum(per_stance) / len(per_stance) * 1000, 2) if per_stance else None,
                        "max_slip_per_frame_mm": round(worst_frame * 1000, 2)}
            acts[act.name] = {"frames": [f0, f1], "checked": len(frames), "clash_frames": clash_frames,
                              "clash_pairs": sorted(pair_hits),
                              "lowest_nonfoot_z": round(low, 4) if low != float("inf") else None,
                              "ground_penetration_mm": round(max(0.0, ground_z - low) * 1000, 1) if low != float("inf") else None,
                              "slip": slip}
        res["actions"] = acts
    finally:
        if arm.animation_data:
            arm.animation_data.action = keep_action
            if keep_action is not None and keep_slot is not None:
                arm.animation_data.action_slot = keep_slot
        for pb in arm.pose.bones:
            pb.rotation_mode = keep_modes[pb.name]
            pb.matrix_basis = keep_pose[pb.name]
        sc.frame_set(keep_frame)

    problems = []
    for k, v in res["sweeps"].items():
        if v["clash_at_deg"]:
            problems.append(f"sweep {k}: clash at {v['clash_at_deg'][:6]} deg ({', '.join(v['clash_pairs'][:4])})")
        if v["max_clamp_err_deg"] > 0.5:
            problems.append(f"sweep {k}: the limit doesn't hold (off by {v['max_clamp_err_deg']} deg)")
    for k, v in res["actions"].items():
        if v["clash_frames"]:
            problems.append(f"action {k}: clash on {len(v['clash_frames'])} frames ({', '.join(v['clash_pairs'][:4])})")
        if (v["ground_penetration_mm"] or 0) > penetration * 1000:
            problems.append(f"action {k}: a part goes {v['ground_penetration_mm']} mm into the ground")
        s = v["slip"]
        if s and s["max_slip_per_stance_mm"] is not None and s["max_slip_per_stance_mm"] > slip_mm:
            problems.append(f"action {k}: a foot slips {s['max_slip_per_stance_mm']} mm in one stance")
    warnings = []
    if res["random_poses"]["with_clash"]:
        warnings.append(f"{res['random_poses']['with_clash']}/{res['random_poses']['n']} random poses clash")
    if feet is None:
        warnings.append("no --feet given, so foot slip wasn't measured")
    res["summary"] = {"problems": problems, "warnings": warnings}
    res["pass"] = not problems
    if report:
        res["report"] = ax.write_json(report, res)
    return res


if __name__ == "__main__":
    p = ax.parser(__doc__)
    p.add_argument("--armature")
    p.add_argument("--actions", nargs="*")
    p.add_argument("--feet", nargs="*")
    p.add_argument("--ground-z", dest="ground_z", type=float, default=0.0)
    p.add_argument("--contact", type=float, default=0.02)
    p.add_argument("--slip-mm", dest="slip_mm", type=float, default=5.0)
    p.add_argument("--in-place", dest="in_place", nargs="*")
    p.add_argument("--ignore", nargs="*")
    p.add_argument("--no-adjacent", dest="adjacent", action="store_false")
    p.add_argument("--random", dest="random_poses", type=int, default=30)
    p.add_argument("--past", type=float, default=6.0)
    p.add_argument("--sweep-step", dest="sweep_step", type=float, default=3.0)
    p.add_argument("--step", type=int, default=1)
    p.add_argument("--penetration", type=float, default=0.01)
    p.add_argument("--report")
    ax.cli(run, p)
