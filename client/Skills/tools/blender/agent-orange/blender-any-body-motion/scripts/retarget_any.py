"""Puts a person's motion on any standing body: a biped, a bird, a four-legged animal, whatever its bones are called.

Where each limb reaches from what carries it, and how stretched it is, goes over from source to target, and the
target's limb is solved to reach the same way, so bone counts, rolls and rest poses don't have to match. Feet the
source has planted are held where they are. A body that isn't upright follows only part of the source's leaning, as a
person's bow would put its nose in the ground. Hind legs follow the source's legs; a four-legged body's front legs
follow the opposite ones, as in a trot, half as far. Six legs and more alternate, with shorter steps. Tails lag behind the body's turning.

Usage:
  blender -b rig.blend -P retarget_any.py -- --target Rig (--source Mocap | --bvh walk.bvh) [--frames 1 120]
      [--name Walk] [--lean 0.35] [--in-place] [--no-arms] [--plan] [--save out.blend]
  MCP: import retarget_any; print(retarget_any.run(target="Rig", bvh="//walk.bvh", name="Walk"))
--plan says which of the target's bones follow what, and changes nothing. A BVH is stood up and turned to face -Y by
its own shape, and removed afterwards; sizes don't matter, as travel is scaled by leg length.
"""
import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _anyx as ax  # noqa: E402
import body as bd  # noqa: E402


def stand(arm):
    """Turns a skeleton so it stands with Z up, its left and right along X and its toes towards -Y."""
    points = [p for b in arm.data.bones for p in (arm.matrix_world @ b.head_local, arm.matrix_world @ b.tail_local)]
    root = arm.matrix_world @ next(b for b in arm.data.bones if b.parent is None).head_local
    extent = [max(p[i] for p in points) - min(p[i] for p in points) for i in range(3)]
    size = max(extent)

    def lopsided(i):
        """How badly the points mirror across the root along an axis."""
        total = 0.0
        for p in points:
            q = p.copy()
            q[i] = 2 * root[i] - q[i]
            total += min((q - other).length for other in points)
        return total / len(points) / size + (1 if extent[i] < 0.1 * size else 0)

    side = min(range(3), key=lopsided)
    up = max((i for i in range(3) if i != side), key=lambda i: extent[i])
    over = sum(p[up] > root[up] for p in points) >= len(points) / 2
    z = Vector([float(i == up) * (1 if over else -1) for i in range(3)])
    x = Vector([float(i == side) for i in range(3)])
    turn = Matrix((x, z.cross(x), z)).to_4x4()
    arm.matrix_world = turn @ arm.matrix_world
    bpy.context.view_layer.update()
    feet = Vector()
    for leg in bd.Body(arm).legs:
        feet += arm.matrix_world @ arm.data.bones[leg.end or leg.bones[-1]].tail_local - leg.joints[-1]
        for low in bd.Body(arm).below(leg.end or leg.bones[-1]):
            feet += arm.matrix_world @ arm.data.bones[low].tail_local - leg.joints[-1]
    if feet.y > 0:
        arm.matrix_world = Matrix.Rotation(math.pi, 4, "Z") @ arm.matrix_world
        bpy.context.view_layer.update()


def load(path):
    before = set(bpy.data.objects)
    bpy.ops.import_anim.bvh(filepath=ax.path(path), axis_forward="Y", axis_up="Z", rotate_mode="NATIVE",
                            use_fps_scale=False, update_scene_fps=False, update_scene_duration=False)
    arm = next(o for o in bpy.data.objects if o not in before and o.type == "ARMATURE")
    stand(arm)
    return arm


def crowded(tgt):
    """Six legs or more, which would cross each other at a person's stride."""
    return any(leg.place > 1 for leg in tgt.legs)


def plan_of(src, tgt, arms):
    """{target limb: (source limb, share)}."""
    legs = {leg.side: leg for leg in src.legs if leg.place == 0}
    other = {"L": "R", "R": "L"}
    out = {}
    for leg in tgt.legs:
        crossed = leg.place % 2 == 1
        from_ = legs.get(other[leg.side] if crossed else leg.side)
        if from_:
            out[leg] = (from_, 0.5 if leg.place or crowded(tgt) else 1.0)
    if arms and tgt.upright:  # a wing or a hanging forelimb isn't an arm: it goes with the body
        for side in "LR":
            mine = max((limb for limb in tgt.arms if limb.side == side), key=lambda limb: limb.length, default=None)
            theirs = max((limb for limb in src.arms if limb.side == side), key=lambda limb: limb.length, default=None)
            if mine and theirs:
                out[mine] = (theirs, 1.0)
    return out


def run(target=None, source=None, bvh=None, frames=None, name=None, lean=0.35, in_place=False, arms=True, plan=False, **_):
    if bpy.context.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    if not target or not (source or bvh):
        raise ValueError("give target, and source or bvh")
    tgt = bd.Body(bpy.data.objects[target])
    src_arm = load(bvh) if bvh else bpy.data.objects[source]
    scene = bpy.context.scene
    was = scene.frame_current
    try:
        src = bd.Body(src_arm)
        if {"L", "R"} - {leg.side for leg in src.legs}:
            raise ValueError(f"{src_arm.name} isn't a standing person: {src.say()}")
        pairs = plan_of(src, tgt, arms)
        said = {"target": tgt.say(), "source": src.say(), "pairs": [
            {"target": mine.bones, "follows": theirs.bones, "share": share} for mine, (theirs, share) in pairs.items()]}
        if plan:
            return {"ok": True, **said}

        act = src_arm.animation_data.action if src_arm.animation_data else None
        if not frames and not act:
            raise ValueError("the source has no action: give frames")
        first, last = (int(round(f)) for f in (frames or act.frame_range))
        moving = []
        for f in range(first, last + 1):
            scene.frame_set(f)
            moving.append(src.posed())
        poses, report = transfer(src, tgt, pairs, moving, scene.render.fps / scene.render.fps_base, lean, in_place)
        moved = [tgt.hips, *tgt.spine, *tgt.neck, *tgt.tail_bones]
        moved += [n for limb in pairs for n in [*limb.bones, *([limb.end] if limb.end else [])]]
        action = bd.bake(tgt, f"{tgt.arm.name}|{name or (act.name if act else 'Motion')}", list(range(first, last + 1)), poses, moved)
        return {"ok": True, "action": action.name, "frames": [first, last], **report, **said}
    finally:
        if bvh:
            data, act = src_arm.data, src_arm.animation_data.action if src_arm.animation_data else None
            bpy.data.objects.remove(src_arm, do_unlink=True)
            bpy.data.armatures.remove(data)
            if act and not act.users:
                bpy.data.actions.remove(act)
        scene.frame_set(was)


def transfer(src, tgt, pairs, moving, fps, lean, in_place):
    n = len(moving)
    still = Matrix.Identity(3)
    lean = 1.0 if tgt.upright else lean
    legs_s = [leg for leg in src.legs if leg.place == 0]
    legs_t = [leg for leg in tgt.legs if leg.place == 0]
    ratio = sum(leg.length for leg in legs_t) / len(legs_t) / (sum(leg.length for leg in legs_s) / len(legs_s))
    ratio *= 0.4 if crowded(tgt) else 1

    def end_of(pose, limb):
        return pose[limb.end].translation if limb.end else pose[limb.bones[-1]] @ src.rest[limb.bones[-1]].inverted() @ limb.joints[-1]

    # Mocap often stands a little above or below its own rest pose's ground.
    ankles = {leg: [end_of(pose, leg).copy() for pose in moving] for leg in legs_s}
    ground = min(p.z - leg.joints[-1].z for leg in legs_s for p in ankles[leg])
    heading = []
    for pose in moving:
        f = src.change(pose, src.hips) @ bd.AHEAD
        angle = math.atan2(f.x, -f.y)
        if heading:
            angle += 2 * math.pi * round((heading[-1] - angle) / (2 * math.pi))
        heading.append(angle)
    start = moving[0][src.hips].translation
    hips_s, hips_t = src.head[src.hips], tgt.head[tgt.hips]

    # First the body, and where each limb's end would be if nothing were planted.
    bodies, wanted, ends = [], {limb: [] for limb in pairs}, {}
    for i, pose in enumerate(moving):
        yaw = Matrix.Rotation(heading[i], 3, "Z")
        hips_turn = src.change(pose, src.hips)
        turned = yaw @ bd.part_of(yaw.transposed() @ hips_turn, lean)
        move = (pose[src.hips].translation - start) * ratio
        rise = (pose[src.hips].translation.z - ground - hips_s.z) / max(hips_s.z - src.floor, 1e-6)
        place = hips_t + Vector((0 if in_place else move.x, 0 if in_place else move.y, rise * (hips_t.z - tgt.floor)))
        out = {}
        for name in tgt.order():
            out[name] = tgt.follow(out, name)
            if name == tgt.hips:
                out[name] = bd.at(place, turned @ tgt.turn[name])
        bend = bd.part_of(hips_turn.transposed() @ src.change(pose, src.chest), lean)
        nod = bd.part_of(hips_turn.transposed() @ src.change(pose, src.neck[-1]), lean) if src.neck else bend
        for k, name in enumerate(tgt.spine):
            out[name] = bd.at(tgt.follow(out, name).translation, turned @ bd.part_of(bend, (k + 1) / len(tgt.spine)) @ tgt.turn[name])
        up_to = bend if tgt.spine else still
        for k, name in enumerate(tgt.neck):
            part = up_to @ bd.part_of(up_to.transposed() @ nod, (k + 1) / len(tgt.neck))
            out[name] = bd.at(tgt.follow(out, name).translation, turned @ part @ tgt.turn[name])
        for k, name in enumerate(tgt.tail_bones):
            late = heading[max(0, i - round((k + 1) * 0.06 * fps))] - heading[i]
            out[name] = bd.at(tgt.follow(out, name).translation, Matrix.Rotation(max(-0.6, min(0.6, late * 0.5)), 3, "Z") @ turned @ tgt.turn[name])
        settle(tgt, out, skip={*tgt.spine, *tgt.neck, *tgt.tail_bones})
        bodies.append(out)

        for mine, (theirs, share) in pairs.items():
            carrier = src.change(pose, theirs.carried)
            reach = carrier.transposed() @ (end_of(pose, theirs) - pose[theirs.bones[0]].translation)
            stretch = (reach.length / theirs.length) / (theirs.reach.length / theirs.length)
            stretch = 1 + (stretch - 1) * share
            if mine.kind == "arm":
                way = reach.normalized()
            else:
                way = bd.part_of(theirs.reach.rotation_difference(reach).to_matrix(), share) @ mine.reach.normalized()
            length = max(0.25 * mine.length, min(0.999 * mine.length, mine.reach.length * stretch))
            carried = tgt.change(out, mine.carried)
            wanted[mine].append(tgt.follow(out, mine.bones[0]).translation + carried @ way * length)

    # Then the planted feet, held at one place each time, on the ground.
    ease = max(2, round(0.1 * fps))
    held, slipping = {}, {}
    for mine, (theirs, _) in pairs.items():
        if mine.kind != "leg":
            continue
        runs = [] if in_place else bd.stances(ankles[theirs], theirs.length, fps)
        spots = []
        for a, b in runs:
            middle = wanted[mine][a:b + 1]
            spots.append(Vector((sum(p.x for p in middle) / len(middle), sum(p.y for p in middle) / len(middle), mine.joints[-1].z)))
        firm = bd.hold(runs, n, ease)
        held[mine] = firm
        slipping[mine] = runs
        for i in range(n):
            free = wanted[mine][i]
            free.z = max(free.z, mine.joints[-1].z)
            k, share = firm[i]
            if k is not None:
                wanted[mine][i] = free.lerp(spots[k], share)

    # A planted foot out of reach brings the body down rather than sliding.
    sink = [0.0] * n
    for mine in held:
        for i in range(n):
            if held[mine][i][1] < 0.5:
                continue
            root = tgt.follow(bodies[i], mine.bones[0]).translation
            away = wanted[mine][i] - root
            flat = math.hypot(away.x, away.y)
            most = 0.99 * mine.length
            if away.length > most and flat < most:
                sink[i] = max(sink[i], -away.z - math.sqrt(most * most - flat * flat))
    spread = max(1, ease // 2)
    sink = [max(sink[max(0, i - spread):i + spread + 1]) for i in range(n)]
    sink = [sum(sink[max(0, i - spread):i + spread + 1]) / len(sink[max(0, i - spread):i + spread + 1]) for i in range(n)]

    missed = 0.0
    for i, out in enumerate(bodies):
        if sink[i] > 0:
            down = Matrix.Translation((0, 0, -sink[i]))
            for name in tgt.below(tgt.hips):
                out[name] = down @ out[name]
        yaw = Matrix.Rotation(heading[i], 3, "Z")
        for mine, (theirs, share) in pairs.items():
            if mine.kind == "leg":
                # Flat on the ground while planted, and part of the source foot's tilt as it swings.
                tilt = yaw.transposed() @ src.change(moving[i], theirs.end) if theirs.end else still
                turn = yaw @ bd.part_of(tilt, 0.7 * share * (1 - held[mine][i][1]))
            else:
                turn = tgt.change(out, mine.carried)
            away = bd.set_limb(tgt, out, mine, wanted[mine][i], turn, bd.fold_of(mine))
            if mine.kind == "leg" and held[mine][i][1] >= 1:
                missed = max(missed, away)
            ends.setdefault(mine, []).append((out[mine.end].translation if mine.end else out[mine.bones[-1]] @ tgt.rest[mine.bones[-1]].inverted() @ mine.joints[-1]).copy())
    return bodies, {
        "travel_scale": ax.r(ratio), "lean": lean,
        "stances": sum(len(runs) for runs in slipping.values()),
        "planted_slip_mm": ax.r(1000 * max((bd.slip(ends[leg], runs) for leg, runs in slipping.items()), default=0.0), 2),
        "planted_out_of_reach_mm": ax.r(1000 * missed, 2), "most_lowered_m": ax.r(max(sink))}


def settle(tgt, out, skip):
    """Everything not posed itself goes where its parent takes it."""
    for name in tgt.order():
        if name != tgt.hips and name not in skip:
            out[name] = tgt.follow(out, name)


if __name__ == "__main__":
    p = ax.parser(__doc__)
    p.add_argument("--target", required=True)
    p.add_argument("--source", default=None)
    p.add_argument("--bvh", default=None)
    p.add_argument("--frames", type=float, nargs=2, default=None)
    p.add_argument("--name", default=None)
    p.add_argument("--lean", type=float, default=0.35, help="how much of the leaning a body that isn't upright follows")
    p.add_argument("--in-place", action="store_true")
    p.add_argument("--no-arms", dest="arms", action="store_false")
    p.add_argument("--plan", action="store_true")
    p.add_argument("--save", default=None)

    def main(save=None, **args):
        result = run(**args)
        if save:
            bpy.ops.wm.save_as_mainfile(filepath=ax.path(save))
        return result

    ax.cli(main, p)
