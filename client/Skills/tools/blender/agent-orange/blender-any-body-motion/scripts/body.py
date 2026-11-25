"""A skeleton read by shape: which bones are legs, arms, spine, neck and tail, on any body, without its bones' names.

Shared by retarget_any.py and plant_feet.py. Bodies stand on the ground with Z up and face -Y, so left is +X.
    blender -b rig.blend -P body.py -- --armature Rig     (says what it made of a skeleton)
"""
import math
import os
import sys

import bpy
from bpy_extras import anim_utils
from mathutils import Matrix, Quaternion, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _anyx as ax  # noqa: E402

AHEAD = Vector((0, -1, 0))
UP = Vector((0, 0, 1))


class Limb:
    """A leg or arm: its bones that bend, its end (a foot or hand, with what's below it), and what carries it."""

    def __init__(self, kind, side, bones, end, joints, carried):
        self.kind, self.side, self.bones, self.end, self.joints, self.carried = kind, side, bones, end, joints, carried
        self.length = sum((b - a).length for a, b in zip(joints, joints[1:]))
        self.reach = joints[-1] - joints[0]
        self.place = 0  # among the legs of its side, from the back

    def say(self):
        return {"kind": self.kind, "side": self.side, "bones": self.bones, "end": self.end, "carried_by": self.carried}


class Body:
    def __init__(self, arm):
        self.arm = arm
        self.world = arm.matrix_world.copy()
        scale = self.world.to_scale()
        if max(scale) / max(min(scale), 1e-9) > 1.001:
            raise ValueError(f"{arm.name} is scaled unevenly: apply its scale first")
        bones = arm.data.bones
        self.rest = {b.name: self.world @ b.matrix_local for b in bones}
        self.turn = {n: m.to_3x3().normalized() for n, m in self.rest.items()}
        self.parent = {b.name: b.parent.name if b.parent else None for b in bones}
        self.children = {b.name: [c.name for c in b.children] for b in bones}
        self.head = {n: m.translation.copy() for n, m in self.rest.items()}
        self.tail = {b.name: self.world @ b.tail_local for b in bones}
        points = [*self.head.values(), *self.tail.values()]
        self.floor = min(p.z for p in points)
        self.height = max(p.z for p in points) - self.floor
        self.size = max(max(p[i] for p in points) - min(p[i] for p in points) for i in range(3))
        self.middle = sorted(p.x for p in self.head.values())[len(bones) // 2]
        self._read()

    def below(self, name):
        out = [name]
        for n in out:
            out.extend(self.children[n])
        return out

    def above(self, name):
        out = []
        while name is not None:
            out.append(name)
            name = self.parent[name]
        return out

    def ahead_of(self, name, then):
        """Where a bone ends: the next one's joint, or its own tail when nothing follows."""
        return self.head[then] if then else self.tail[name]

    def central(self, p):
        return abs(p.x - self.middle) < 0.02 * self.size

    def far(self, name):
        """The longest way down from a bone, as (length, bones)."""
        ways = [self.far(c) for c in self.children[name]]
        if not ways:
            return (self.tail[name] - self.head[name]).length, [name]
        length, way = max(ways, key=lambda each: (self.head[each[1][0]] - self.head[name]).length + each[0])
        return (self.head[way[0]] - self.head[name]).length + length, [name, *way]

    def _read(self):
        limbs, seen = [], set()
        for name in sorted(self.head, key=lambda n: len(self.above(n))):  # from the top down
            if name in seen:
                continue
            length, way = self.far(name)
            leaves = not self.central(self.tail[name]) or any(not self.central(self.head[c]) for c in self.children[name]) and len(self.children[name]) == 1
            if not leaves or self.parent[name] is None or not self.central(self.head[self.parent[name]]):
                continue
            seen.update(self.below(name))
            limb = self._limb(way, length)
            if limb:
                limbs.append(limb)
        self.limbs = limbs
        self.legs = [limb for limb in limbs if limb.kind == "leg"]
        self.arms = [limb for limb in limbs if limb.kind == "arm"]
        for side in "LR":
            for i, leg in enumerate(sorted((leg for leg in self.legs if leg.side == side), key=lambda leg: -leg.joints[0].y)):
                leg.place = i
        if not self.legs:
            raise ValueError(f"{self.arm.name}: no legs found. It has to stand on the ground with Z up, left and right of its middle in X")

        # The hips carry the hind legs and the chest whatever else there is in front or above.
        hind = [leg for leg in self.legs if leg.place == 0]
        fore = [limb for limb in self.arms if limb.length > 0.3 * hind[0].length] or [leg for leg in self.legs if leg.place > 0]
        self.hips = self._shared([leg.carried for leg in hind])
        self.chest = self._shared([limb.carried for limb in fore]) if fore else self.hips
        if self.hips not in self.above(self.chest):
            self.chest = self.hips
        self.spine = [n for n in reversed(self.above(self.chest)) if self.hips in self.above(n) and n != self.hips]

        limbed = {n for limb in limbs for n in self.below(limb.root)}
        ends = [n for n in self.below(self.hips) if n not in limbed and not self.children[n] and self.central(self.tail[n])]
        self.neck, self.tail_bones = [], []
        fronts = [n for n in ends if self.chest in self.above(n) and n != self.chest and (self.tail[n] - self.head[self.hips]).dot(UP + AHEAD) > 0]
        if fronts:
            head = max(fronts, key=lambda n: (self.tail[n] - self.head[self.hips]).length)
            self.neck = [n for n in reversed(self.above(head)) if self.chest in self.above(n) and n != self.chest]
        taken = {*self.spine, *self.neck, self.hips}
        backs = [n for n in ends if n not in taken and self.tail[n].y > self.head[self.hips].y]
        if backs:
            end = max(backs, key=lambda n: (self.tail[n] - self.head[self.hips]).length)
            self.tail_bones = [n for n in reversed(self.above(end)) if self.hips in self.above(n) and n not in taken]
        top = self.tail[self.neck[-1]] if self.neck else self.head[self.chest]
        trunk = top - self.head[self.hips]
        self.upright = trunk.length < 1e-6 or trunk.normalized().z > 0.7

    def _limb(self, way, length):
        # A hand or paw that fans out ends the limb; its fingers go with it.
        for i, name in enumerate(way[:-1]):
            if i >= 1 and len(self.children[name]) >= 3:
                way = way[:i + 1]
                break
        if len(way) < 2 or length < 0.12 * self.size:
            return None
        root = way[0]
        carried = self.parent[root]
        low = min(min(self.tail[n].z, self.head[n].z) for n in self.below(root))
        kind = "leg" if low < self.floor + 0.06 * self.height else "arm"
        # A collarbone or half of the pelvis only carries the limb.
        if len(way) > 2 and (self.head[way[1]] - self.head[way[0]]).length < 0.6 * (self.head[way[2]] - self.head[way[1]]).length and kind == "arm" or (
                len(way) > 3 and self.central(self.head[way[0]])):
            way = way[1:]
            carried = self.parent[way[0]]
        if len(way) < 2:
            return None
        if kind == "leg":
            hip = self.head[way[0]].z - self.floor
            cut = next((i for i in range(2, len(way)) if self.head[way[i]].z - self.floor < 0.2 * hip), None)
        else:
            cut = len(way) - 1 if len(way) >= 3 else None
        bones = way[:cut] if cut else way
        end = way[cut] if cut else None
        joints = [self.head[n] for n in bones] + [self.head[end] if end else self.tail[bones[-1]]]
        side = "L" if sum(p.x for p in joints) / len(joints) > self.middle else "R"
        limb = Limb(kind, side, bones, end, joints, carried)
        limb.root = root
        return limb

    def _shared(self, names):
        lines = [self.above(n) for n in names]
        return next((n for n in lines[0] if all(n in line for line in lines)), lines[0][0])

    def say(self):
        return {"hips": self.hips, "spine": self.spine, "chest": self.chest, "neck": self.neck, "tail": self.tail_bones,
                "upright": self.upright, "legs": [leg.say() for leg in sorted(self.legs, key=lambda leg: (leg.place, leg.side))],
                "arms": [limb.say() for limb in self.arms]}

    # -------------------------------------------------------------- poses: {bone: world matrix}
    def order(self):
        return sorted(self.head, key=lambda n: len(self.above(n)))

    def follow(self, pose, name):
        """A bone where its parent takes it, unposed itself."""
        up = self.parent[name]
        return pose[up] @ self.rest[up].inverted() @ self.rest[name] if up else self.rest[name].copy()

    def posed(self):
        return {n: self.world @ self.arm.pose.bones[n].matrix for n in self.head}

    def change(self, pose, name):
        """How far a bone has turned from rest, in world axes."""
        return pose[name].to_3x3().normalized() @ self.turn[name].transposed()


def at(position, turn):
    return Matrix.Translation(position) @ turn.to_4x4()


def part_of(turn, share):
    q = turn.to_quaternion()
    if q.w < 0:
        q.negate()
    return Quaternion().slerp(q, share).to_matrix()


def solve(joints, end, fold):
    """The joints of a limb moved so its last one is at end: lengths kept, the first one fixed (FABRIK)."""
    lengths = [(b - a).length for a, b in zip(joints, joints[1:])]
    total, root = sum(lengths), joints[0].copy()
    way = end - root
    if way.length > 0.9995 * total:
        end = root + way.normalized() * 0.9995 * total
    # A limb straight to begin with has no way to bend, so it's told one.
    line = (joints[-1] - root)
    bent = max((p - root).cross(line).length / max(line.length, 1e-9) for p in joints[1:-1]) if len(joints) > 2 else 0
    out = [p.copy() for p in joints]
    if len(out) > 2 and bent < 0.01 * total:
        for i in range(1, len(out) - 1):
            out[i] += fold * 0.04 * total
    for _ in range(60):
        out[-1] = end.copy()
        for i in range(len(out) - 2, -1, -1):
            out[i] = out[i + 1] + (out[i] - out[i + 1]).normalized() * lengths[i]
        out[0] = root.copy()
        for i in range(1, len(out)):
            out[i] = out[i - 1] + (out[i] - out[i - 1]).normalized() * lengths[i - 1]
        if (out[-1] - end).length < 1e-5 * total:
            break
    return out


def set_limb(body, pose, limb, end, end_turn, fold):
    """Poses a limb so its end is at a place, from how its carrier stands, and its end bone with a turn of its own."""
    root = body.follow(pose, limb.bones[0]).translation
    carried = body.change(pose, limb.carried) if limb.carried else Matrix.Identity(3)
    want = end - root
    swing = (carried @ limb.reach).rotation_difference(want).to_matrix() @ carried if want.length > 1e-6 else carried
    joints = solve([root + swing @ (p - limb.joints[0]) for p in limb.joints], end, carried @ fold)
    for i, name in enumerate(limb.bones):
        was = swing @ (limb.joints[i + 1] - limb.joints[i])
        turn = was.rotation_difference(joints[i + 1] - joints[i]).to_matrix() @ swing @ body.turn[name]
        pose[name] = at(joints[i], turn)
    if limb.end:
        pose[limb.end] = at(joints[-1], end_turn @ body.turn[limb.end])
    for name in body.below(limb.bones[0]):
        if name not in limb.bones and name != limb.end:
            pose[name] = body.follow(pose, name)
    return (joints[-1] - end).length


def fold_of(limb):
    return AHEAD if limb.kind == "leg" else -AHEAD


def stances(places, reach, fps, shortest=3):
    """The runs of frames a foot is planted: near its lowest and hardly moving along the ground."""
    n = len(places)
    low = min(p.z for p in places)
    slow = []
    for i in range(n):
        a, b = places[max(0, i - 1)], places[min(n - 1, i + 1)]
        steps = max(1, min(n - 1, i + 1) - max(0, i - 1))
        slow.append(math.hypot(b.x - a.x, b.y - a.y) / steps * fps < 0.6 * reach and places[i].z - low < 0.07 * reach)
    runs, start = [], None
    for i, down in enumerate([*slow, False]):
        if down and start is None:
            start = i
        elif not down and start is not None:
            if i - start >= shortest:
                runs.append((start, i - 1))
            start = None
    return runs


def hold(runs, n, ease):
    """For each frame, which run holds the foot and how firmly, eased in and out around it."""
    out = [(None, 0.0)] * n
    for k, (a, b) in enumerate(runs):
        for i in range(max(0, a - ease), min(n, b + ease + 1)):
            away = max(a - i, i - b, 0)
            firm = 1 - away / (ease + 1)
            firm = firm * firm * (3 - 2 * firm)
            if firm > out[i][1]:
                out[i] = (k, firm)
    return out


def slip(places, runs):
    """How far a foot travels along the ground while planted, in metres: the worst stance."""
    return float(max((sum(math.hypot(q.x - p.x, q.y - p.y) for p, q in zip(places[a:b + 1], places[a + 1:b + 1])) for a, b in runs), default=0))


def bake(body, name, frames, poses, moved):
    """Writes poses to a new action: turns for every bone in moved, and travel for the hips and any that travel."""
    arm = body.arm
    act = bpy.data.actions.new(name)
    act.use_fake_user = True
    arm.animation_data_create()
    for track in arm.animation_data.nla_tracks:
        track.mute = True
    arm.animation_data.action = act
    slot = arm.animation_data.action_slot or act.slots.new(id_type="OBJECT", name=arm.name)
    arm.animation_data.action_slot = slot
    bag = anim_utils.action_ensure_channelbag_for_slot(act, slot)

    def write(path, index, group, values):
        curve = bag.fcurves.ensure(path, index=index, group_name=group)
        curve.keyframe_points.add(len(frames))
        curve.keyframe_points.foreach_set("co", [v for pair in zip(frames, values) for v in pair])
        for key in curve.keyframe_points:
            key.interpolation = "LINEAR"
        curve.update()

    for pb in arm.pose.bones:
        pb.matrix_basis = Matrix.Identity(4)
    for bone in moved:
        turns, places = [], []
        for pose in poses:
            local = body.follow(pose, bone).inverted() @ pose[bone]
            q = local.to_quaternion().normalized()
            if turns and turns[-1].dot(q) < 0:
                q.negate()
            turns.append(q)
            places.append(local.translation.copy())
        arm.pose.bones[bone].rotation_mode = "QUATERNION"
        for i in range(4):
            write(f'pose.bones["{bone}"].rotation_quaternion', i, bone, [q[i] for q in turns])
        if bone == body.hips or max(p.length for p in places) > 1e-5 * body.size:
            for i in range(3):
                write(f'pose.bones["{bone}"].location', i, bone, [p[i] for p in places])
    return act


def run(armature=None, **_):
    arm = bpy.data.objects[armature] if armature else next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
    return {"ok": True, "armature": arm.name, **Body(arm).say()}


if __name__ == "__main__":
    p = ax.parser(__doc__)
    p.add_argument("--armature", default=None)
    ax.cli(run, p)
