"""Stops feet sliding in an action: each foot is held where it lands until it lifts, and its leg solved to match.

Any body (see body.py), any action, keyed or baked. The old action is kept; the result is a new one.
Usage:
  blender -b rig.blend -P plant_feet.py -- --armature Rig --action Walk [--name Walk_Planted] [--check] [--save out.blend]
  MCP: import plant_feet; print(plant_feet.run(armature="Rig", action="Walk"))
--check only measures: each foot's stances and its worst slide in one. A cycle that walks on the spot slides by design,
as the engine moves the body, so it's only for actions that travel or stand.
"""
import os
import sys

import bpy
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _anyx as ax  # noqa: E402
import body as bd  # noqa: E402


def run(armature=None, action=None, name=None, check=False, **_):
    if bpy.context.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    arm = bpy.data.objects[armature] if armature else next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
    rig = bd.Body(arm)
    arm.animation_data_create()
    had, had_slot = arm.animation_data.action, arm.animation_data.action_slot
    if action:
        arm.animation_data.action = bpy.data.actions[action]
        if arm.animation_data.action_slot is None and bpy.data.actions[action].slots:
            arm.animation_data.action_slot = bpy.data.actions[action].slots[0]
    act = arm.animation_data.action
    if act is None:
        raise ValueError("give an action")
    scene = bpy.context.scene
    was, fps = scene.frame_current, scene.render.fps / scene.render.fps_base
    first, last = (int(round(f)) for f in act.frame_range)
    poses = []
    for f in range(first, last + 1):
        scene.frame_set(f)
        poses.append(rig.posed())
    scene.frame_set(was)

    def foot(pose, leg):
        return pose[leg.end].translation.copy() if leg.end else pose[leg.bones[-1]] @ rig.rest[leg.bones[-1]].inverted() @ leg.joints[-1]

    feet, ease = [], max(2, round(0.1 * fps))
    for leg in rig.legs:
        places = [foot(pose, leg) for pose in poses]
        runs = bd.stances(places, leg.length, fps)
        feet.append((leg, places, runs, bd.slip(places, runs)))
    if check:
        arm.animation_data.action = had
        if had:
            arm.animation_data.action_slot = had_slot
        return {"ok": True, "action": act.name, "feet": [
            {"foot": leg.end or leg.bones[-1], "stances": [[first + a, first + b] for a, b in runs], "slide_mm": ax.r(1000 * far, 1)} for leg, _, runs, far in feet]}

    said = []
    for leg, places, runs, far in feet:
        firm = bd.hold(runs, len(poses), ease)
        spots = [sum(places[a:b + 1], Vector()) / (b - a + 1) for a, b in runs]
        missed = 0.0
        for i, pose in enumerate(poses):
            k, share = firm[i]
            if k is None:
                continue
            end = leg.end or leg.bones[-1]
            turn = rig.change(pose, end) if leg.end else Matrix.Identity(3)
            away = bd.set_limb(rig, pose, leg, places[i].lerp(spots[k], share), turn, bd.fold_of(leg))
            missed = max(missed, away if share >= 1 else 0.0)
        now = [foot(pose, leg) for pose in poses]
        said.append({"foot": leg.end or leg.bones[-1], "stances": len(runs), "slide_mm": ax.r(1000 * far, 1),
                     "slide_after_mm": ax.r(1000 * bd.slip(now, runs), 1), "out_of_reach_mm": ax.r(1000 * missed, 1)})
    # Bones the action never moves stay out of the new one.
    still = Matrix.Identity(4)

    def moves(bone):
        return any(max(abs(v) for row in (rig.follow(pose, bone).inverted() @ pose[bone] - still) for v in row) > 1e-5 for pose in poses)

    moved = [n for n in rig.order() if moves(n)]
    made = bd.bake(rig, name or f"{act.name}_Planted", list(range(first, last + 1)), poses, moved)
    return {"ok": True, "action": made.name, "from": act.name, "feet": said}


if __name__ == "__main__":
    p = ax.parser(__doc__)
    p.add_argument("--armature", default=None)
    p.add_argument("--action", default=None)
    p.add_argument("--name", default=None)
    p.add_argument("--check", action="store_true")
    p.add_argument("--save", default=None)

    def main(save=None, **args):
        result = run(**args)
        if save:
            bpy.ops.wm.save_as_mainfile(filepath=ax.path(save))
        return result

    ax.cli(main, p)
