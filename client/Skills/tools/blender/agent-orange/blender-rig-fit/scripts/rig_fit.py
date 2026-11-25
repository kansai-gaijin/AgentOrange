"""Does a skeleton fit its mesh? Any body (biped, quadruped, bird, tail, wings), judged by shape and never by bone names.

Everything is measured at rest, in world space, with lengths given as a share of the model's height.
  reach    parts of the mesh that no bone they follow runs through. Half-way through the body from each vertex is the
           middle of whatever it belongs to; the bones that vertex follows should pass near there. A leg with no bone
           follows the hips instead, so its middle is far from them. Found vertices are grouped into regions: a long
           one is a limb, tail or wing with no bone (an error), a short one is an ear or horn that moves rigidly (a
           warning, as that's often wanted).
  inside   joints of bones that move the mesh, outside it. The skeleton's top bone and bones with no weight don't count.
  mirror   off-centre bones with no partner at the mirrored place. Only when the mesh itself is symmetric, side to side.
  loose    bones whose skin joins another bone's, though nothing in the skeleton joins the two nearby: a foot left
           hanging from the root when a rig's IK was lost in export. The part stays behind when its limb moves.
  idle     deform bones that no vertex follows (a warning).
Usage:
  blender -b character.blend -P rig_fit.py -- --armature Rig [--meshes Body] [--report //rig_fit.json] [--strict]
  blender -b --factory-startup -P rig_fit.py -- --files try1.glb try2.glb try3.glb [--report rigs.json]
  MCP: import rig_fit; print(rig_fit.run(armature="Rig"))
--files checks rigs of one model against each other, headless, and says which fits best. Otherwise nothing is changed
or saved: a pose or action in use is ignored for the measuring and put back.
"""
import os
import sys

import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.geometry import intersect_point_line
from mathutils.kdtree import KDTree

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _anyx as ax  # noqa: E402

# Not along any axis, so a ray doesn't run along a flat face or an edge of a model built square.
RAYS = [Vector(v).normalized() for v in ((0.83, 0.41, 0.37), (-0.35, 0.79, 0.5), (0.29, -0.44, 0.85),
                                        (-0.61, -0.53, -0.59), (0.47, 0.26, -0.84))]


class Body:
    """The meshes at rest in world space, with their weights and one BVH per connected piece."""

    def __init__(self, arm, meshes):
        self.co, self.normal, self.weights, self.edges, tris = [], [], [], [], []
        deform = {b.name for b in arm.data.bones if b.use_deform}
        for ob in meshes:
            world = ob.matrix_world
            turn = world.to_3x3().inverted_safe().transposed()
            names = {g.index: g.name for g in ob.vertex_groups if g.name in deform}
            rigid = ob.parent_bone if ob.parent == arm and ob.parent_type == "BONE" else None
            # An exported mesh has a vertex per face at every seam and hard edge. Those are one place here, or the
            # model would be thousands of open scraps.
            grid = max(max(ob.dimensions) * 1e-5, 1e-9)
            at, place = {}, []
            for v in ob.data.vertices:
                co = world @ v.co
                key = tuple(round(c / grid) for c in co)
                if key not in at:
                    at[key] = len(self.co)
                    self.co.append(co)
                    self.normal.append(Vector())
                    held = [(names[g.group], g.weight) for g in v.groups if g.group in names and g.weight > 0.05]
                    self.weights.append(held or ([(rigid, 1.0)] if rigid else []))
                self.normal[at[key]] += turn @ v.normal
                place.append(at[key])
            self.edges += [(place[a], place[b]) for a, b in (e.vertices for e in ob.data.edges)]
            ob.data.calc_loop_triangles()
            found = (tuple(place[i] for i in t.vertices) for t in ob.data.loop_triangles)
            tris += [t for t in found if len(set(t)) == 3]
        for n in self.normal:
            n.normalize()
        self.neighbours = [[] for _ in self.co]
        for a, b in self.edges:
            self.neighbours[a].append(b)
            self.neighbours[b].append(a)
        self.piece = groups(range(len(self.co)), self.neighbours)
        of = {v: i for i, piece in enumerate(self.piece) for v in piece}
        split = [[] for _ in self.piece]
        for t in tris:
            split[of[t[0]]].append(t)
        self.bvhs = [BVHTree.FromPolygons(self.co, t) for t in split if t]
        self.bvh = BVHTree.FromPolygons(self.co, tris)
        xs, zs = [c.x for c in self.co], [c.z for c in self.co]
        self.height = max(zs) - min(zs)
        self.centre = (max(xs) + min(xs)) / 2
        if self.height <= 0:
            raise ValueError("the mesh has no height; is it lying flat or empty?")

    def holds(self, p):
        """In any closed piece. Most rays decide, as an open piece (a cloak, an eyelid) can fool one."""
        for bvh in self.bvhs:
            votes = 0
            for d in RAYS:
                hits, start = 0, p
                while hits < 200:
                    at, _, _, _ = bvh.ray_cast(start, d)
                    if at is None:
                        break
                    hits += 1
                    start = at + d * self.height * 1e-5
                votes += hits % 2
            if votes > len(RAYS) // 2:
                return True
        return False

    def thickness(self, i):
        """How thick the body is under a vertex, straight in from its surface."""
        inward = -self.normal[i]
        at, _, _, length = self.bvh.ray_cast(self.co[i] + inward * self.height * 1e-4, inward)
        return length if at is not None else None


def groups(members, neighbours):
    """Connected groups among some vertices."""
    left, out = set(members), []
    while left:
        todo, group = [left.pop()], []
        while todo:
            v = todo.pop()
            group.append(v)
            for n in neighbours[v]:
                if n in left:
                    left.remove(n)
                    todo.append(n)
        out.append(group)
    return out


def segments(arm, weighted):
    """Each bone as lines: from its joint to each child's joint, or to its tail when it ends a chain. An imported
    glTF's tails are guesses, so they're used only there."""
    world, out = arm.matrix_world, {}
    for b in arm.data.bones:
        head = world @ b.head_local
        ends = [world @ c.head_local for c in b.children if c.name in weighted]
        out[b.name] = [(head, e) for e in ends if (e - head).length > 1e-9] or [(head, world @ b.tail_local)]
    return out


def distance(p, line):
    a, b = line
    if (b - a).length < 1e-9:
        return (p - a).length
    at, t = intersect_point_line(p, a, b)
    return (p - (a if t < 0 else b if t > 1 else at)).length


def check_reach(body, lines, limb, sample):
    """Regions whose middle none of the bones they follow comes near."""
    h, far = body.height, set()
    step = max(1, len(body.co) // sample)
    tested = range(0, len(body.co), step)
    for i in tested:
        thick = body.thickness(i) if body.weights[i] else None
        if not thick:
            continue
        middle = body.co[i] - body.normal[i] * thick / 2
        gap = min(distance(middle, line) for name, _ in body.weights[i] for line in lines[name])
        if gap > thick and gap > 0.03 * h:
            far.add(i)
    if step > 1:  # sampled vertices rarely share an edge, so nearby ones are joined instead
        kd = KDTree(len(far))
        order = sorted(far)
        for n, i in enumerate(order):
            kd.insert(body.co[i], n)
        kd.balance()
        near = {i: [order[n] for _, n, _ in kd.find_range(body.co[i], 0.08 * h)] for i in order}
    else:
        near = body.neighbours
    regions = []
    for group in groups(far, near):
        if len(group) < 4:
            continue
        low = [min(body.co[i][k] for i in group) for k in range(3)]
        top = [max(body.co[i][k] for i in group) for k in range(3)]
        count = {}
        for i in group:
            name = max(body.weights[i], key=lambda w: w[1])[0]
            count[name] = count.get(name, 0) + 1
        length = max(t - lo for t, lo in zip(top, low)) / h
        regions.append({"centre": ax.r([(t + lo) / 2 for t, lo in zip(top, low)], 3), "length": ax.r(length, 3),
                        "follows": max(count, key=count.get), "limb": length >= limb})
    regions.sort(key=lambda each: -each["length"])
    return regions, len(far), len(tested)


def check_inside(arm, body, weighted, slack):
    out = []
    for b in arm.data.bones:
        if b.name not in weighted or b.parent is None:
            continue
        p = arm.matrix_world @ b.head_local
        _, _, _, gap = body.bvh.find_nearest(p)
        if gap / body.height > slack and not body.holds(p):
            out.append({"bone": b.name, "outside": ax.r(gap / body.height)})
    return sorted(out, key=lambda each: -each["outside"])


def check_loose(arm, body):
    """Bones the skin joins to others the skeleton only joins a long way round."""
    parents = {b.name: [b.name, *(p.name for p in b.parent_recursive)] for b in arm.data.bones}

    def steps(a, b):
        """Bones along the way between two, up to what they share and down again; none when they share nothing."""
        up, down = parents[a], parents[b]
        return next((i + down.index(n) for i, n in enumerate(up) if n in down), 99)

    strongest = [max(held, key=lambda w: w[1])[0] if held else None for held in body.weights]
    touching = {}
    for a, b in body.edges:
        one, other = strongest[a], strongest[b]
        if one and other and one != other:
            touching.setdefault(one, {}).setdefault(other, 0)
            touching.setdefault(other, {}).setdefault(one, 0)
            touching[one][other] += 1
            touching[other][one] += 1
    out = []
    for name, others in touching.items():
        # The limb's end is the loose one, not the many it might belong to, so only bones nothing nearer joins.
        if min(steps(name, other) for other in others) > 2 and len(others) == 1:
            other = max(others, key=others.get)
            if len(parents[name]) <= len(parents[other]):
                out.append({"bone": name, "joins": other, "steps": min(steps(name, other), 99)})
    return out


def check_mirror(arm, body, weighted, slack):
    """Off-centre bones without a partner, and how symmetric the mesh is, side to side."""
    h, centre = body.height, body.centre

    def flip(p):
        return Vector((2 * centre - p.x, p.y, p.z))

    step = max(1, len(body.co) // 2000)
    probe = body.co[::step]
    off = sum(body.bvh.find_nearest(flip(p))[3] > 0.02 * h for p in probe) / len(probe)
    if off > 0.05:
        return None, ax.r(1 - off, 3)
    joints = {b.name: arm.matrix_world @ b.head_local for b in arm.data.bones if b.name in weighted}
    lonely = []
    for name, p in joints.items():
        if abs(p.x - centre) <= 0.02 * h:
            continue
        gap, other = min(((flip(p) - q).length, n) for n, q in joints.items())
        if gap / h > slack:
            lonely.append({"bone": name, "nearest": other, "gap": ax.r(gap / h)})
    return sorted(lonely, key=lambda each: -each["gap"]), ax.r(1 - off, 3)


def compare(files, report=None, **given):
    """Each file on its own in an empty scene, the best first: fewest errors, then the smallest."""
    ax.headless_only("--files")
    found = []
    for file in files:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        path = ax.path(file)
        try:
            if path.lower().endswith((".glb", ".gltf")):
                bpy.ops.import_scene.gltf(filepath=path)
            elif path.lower().endswith(".fbx"):
                bpy.ops.import_scene.fbx(filepath=path)
            elif path.lower().endswith(".blend"):
                bpy.ops.wm.open_mainfile(filepath=path)
            else:
                raise ValueError("give a .glb, .gltf, .fbx or .blend")
            arms = [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]
            if not arms:
                raise ValueError("no armature in it")
            result = run(armature=max(arms, key=lambda o: len(o.data.bones)).name, **given)
            size = (sum(each["length"] for each in result["reach"] if each["limb"]) + sum(each["outside"] for each in result["inside"])
                    + sum(each["gap"] for each in result["mirror"] or []) + len(result["loose"]))
            found.append({"file": file, "pass": result["pass"], "errors": result["errors"], "warnings": result["warnings"],
                          "size_of_errors": ax.r(float(size), 3)})
        except Exception as error:  # one unreadable try shouldn't lose the others
            found.append({"file": file, "pass": False, "errors": [f"{type(error).__name__}: {error}"], "warnings": [], "size_of_errors": 999.0})
    found.sort(key=lambda each: (each["size_of_errors"] == 999.0, len(each["errors"]), each["size_of_errors"]))
    result = {"ok": True, "pass": found[0]["pass"], "best": found[0]["file"], "rigs": found}
    if report:
        result["report"] = ax.write_json(report, result)
    return result


def run(armature=None, meshes=None, report=None, limb=0.15, joint_slack=0.01, mirror_slack=0.03, sample=20000, files=None, **_):
    if files:
        return compare(files, report, limb=limb, joint_slack=joint_slack, mirror_slack=mirror_slack, sample=sample)
    arm = bpy.data.objects[armature] if armature else next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
    bodies = ax.objects(meshes) if meshes else [
        o for o in bpy.context.scene.objects if o.type == "MESH" and (
            any(m.type == "ARMATURE" and m.object == arm for m in o.modifiers)
            or (o.parent == arm and o.parent_type == "BONE"))]
    if not bodies:
        raise ValueError(f"no mesh follows {arm.name}")
    was = arm.data.pose_position
    arm.data.pose_position = "REST"  # bone-parented meshes stand where the rest pose puts them
    bpy.context.view_layer.update()
    try:
        body = Body(arm, bodies)
        weighted = {name for held in body.weights for name, _ in held}
        regions, far, tested = check_reach(body, segments(arm, weighted), limb, sample)
        outside = check_inside(arm, body, weighted, joint_slack)
        lonely, symmetry = check_mirror(arm, body, weighted, mirror_slack)
        loose_bones = check_loose(arm, body)
    finally:
        arm.data.pose_position = was
        bpy.context.view_layer.update()

    idle = sorted(b.name for b in arm.data.bones if b.use_deform and b.name not in weighted)
    loose = sum(not held for held in body.weights)
    errors, warnings = [], []
    for each in regions:
        words = f"{each['length']:.0%} of the height long at {each['centre']}, following {each['follows']}"
        (errors if each["limb"] else warnings).append(
            f"a limb, tail or wing with no bone through it: {words}" if each["limb"] else f"a small part moved rigidly: {words}")
    if outside:
        errors.append(f"joints outside the mesh: {[each['bone'] for each in outside][:10]}")
    if lonely:
        errors.append(f"bones with no partner on the other side: {[each['bone'] for each in lonely][:10]}")
    for each in loose_bones:
        errors.append(f"{each['bone']} isn't attached to {each['joins']}, though its skin is: parent it to {each['joins']} or it stays behind")
    if loose:
        errors.append(f"{loose} vertices follow no bone")
    if lonely is None:
        warnings.append(f"the mesh isn't symmetric ({symmetry:.0%} of it matches), so sides weren't compared")
    if idle:
        warnings.append(f"deform bones nothing follows: {idle[:10]}")
    result = {"ok": True, "pass": not errors, "errors": errors, "warnings": warnings,
              "height": ax.r(body.height), "bones_weighted": len(weighted), "reach": regions[:20],
              "reach_vertices": [far, tested], "inside": outside[:20], "mirror": lonely[:20] if lonely else lonely,
              "symmetry": symmetry, "loose": loose_bones, "idle": idle[:20]}
    if report:
        result["report"] = ax.write_json(report, result)
    return result


if __name__ == "__main__":
    p = ax.parser(__doc__)
    p.add_argument("--armature", default=None)
    p.add_argument("--meshes", nargs="*", default=None)
    p.add_argument("--files", nargs="*", default=None)
    p.add_argument("--report", default=None)
    p.add_argument("--limb", type=float, default=0.15, help="share of the height from which a region counts as a limb")
    p.add_argument("--joint-slack", type=float, default=0.01)
    p.add_argument("--mirror-slack", type=float, default=0.03)
    p.add_argument("--sample", type=int, default=20000, help="about how many vertices to measure")
    ax.cli(run, p)
