"""Practice material for the skill tests, each file with known faults or features."""
import math
import os
import random
import bpy
import bmesh
from mathutils import Vector, Matrix
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fx")
os.makedirs(OUT, exist_ok=True)
R = random.Random(3)


def new():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def save(name):
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, name))


def glb(name, **kw):
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT, name), **kw)


def blob(points, name, voxel):
    """A body from (position, radius) balls, joined and remeshed."""
    made = []
    for p, r in points:
        bpy.ops.mesh.primitive_ico_sphere_add(radius=r, subdivisions=2, location=p)
        made.append(bpy.context.object)
    bpy.ops.object.select_all(action="DESELECT")
    for o in made:
        o.select_set(True)
    bpy.context.view_layer.objects.active = made[0]
    bpy.ops.object.join()
    ob = bpy.context.object
    ob.name = ob.data.name = name
    ob.data.remesh_voxel_size = voxel
    bpy.ops.object.voxel_remesh()
    for p in ob.data.polygons:
        p.use_smooth = True
    ob.location = ob.location
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return ob


def line(a, b, ra, rb, step=0.02):
    a, b = Vector(a), Vector(b)
    n = max(2, int((b - a).length / step))
    return [(a.lerp(b, i / n), ra + (rb - ra) * i / n) for i in range(n + 1)]


def rig(name, bones):
    data = bpy.data.armatures.new(name)
    ob = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(ob)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.mode_set(mode="EDIT")
    for n, (h, t, p) in bones.items():
        b = data.edit_bones.new(n)
        b.head, b.tail = h, t
        if p:
            b.parent = data.edit_bones[p]
    bpy.ops.object.mode_set(mode="OBJECT")
    return ob


def bind(body, arm):
    bpy.ops.object.select_all(action="DESELECT")
    body.select_set(True)
    arm.select_set(True)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.parent_set(type="ARMATURE_AUTO")


def both(bones):
    out = {}
    for n, (h, t, p) in bones.items():
        if n.endswith(".L"):
            f = lambda v: (-v[0], v[1], v[2])
            out[n] = (h, t, p)
            out[n[:-2] + ".R"] = (f(h), f(t), p[:-2] + ".R" if p and p.endswith(".L") else p)
        else:
            out[n] = (h, t, p)
    return out


FOX = both({
    "root": ((0, 0, 0), (0, .1, 0), None), "hips": ((0, .25, .42), (0, .1, .44), "root"), "spine": ((0, .1, .44), (0, -.1, .44), "hips"), "chest": ((0, -.1, .44), (0, -.25, .43), "spine"),
    "neck": ((0, -.25, .43), (0, -.38, .55), "chest"), "head": ((0, -.38, .55), (0, -.55, .55), "neck"),
    "tail1": ((0, .27, .44), (0, .42, .42), "hips"), "tail2": ((0, .42, .42), (0, .58, .36), "tail1"), "tail3": ((0, .58, .36), (0, .72, .3), "tail2"),
    "thigh.L": ((.08, .22, .4), (.08, .16, .24), "hips"), "shin.L": ((.08, .16, .24), (.08, .28, .13), "thigh.L"), "hock.L": ((.08, .28, .13), (.08, .25, .04), "shin.L"), "paw.L": ((.08, .25, .04), (.08, .18, .01), "hock.L"),
    "arm.L": ((.08, -.24, .38), (.08, -.2, .22), "chest"), "forearm.L": ((.08, -.2, .22), (.08, -.22, .05), "arm.L"), "hand.L": ((.08, -.22, .05), (.08, -.28, .015), "forearm.L")})


def fox_body():
    pts = line((0, .28, .42), (0, -.24, .42), .1, .11) + line((0, -.22, .44), (0, -.38, .55), .07, .06) + line((0, -.36, .56), (0, -.55, .54), .075, .03) + line((0, .27, .44), (0, .72, .3), .04, .05)
    for s in (1, -1):
        pts += line((s * .08, .22, .4), (s * .08, .16, .24), .065, .045) + line((s * .08, .16, .24), (s * .08, .28, .13), .04, .03) + line((s * .08, .28, .13), (s * .08, .25, .04), .028, .028) + line((s * .08, .25, .035), (s * .08, .18, .03), .032, .03)
        pts += line((s * .08, -.24, .38), (s * .08, -.2, .22), .05, .038) + line((s * .08, -.2, .22), (s * .08, -.22, .05), .034, .028) + line((s * .08, -.22, .035), (s * .08, -.28, .03), .032, .03)
        pts += line((s * .045, -.38, .62), (s * .06, -.37, .7), .025, .01)
    return blob(pts, "Fox", .012)


# 1. A rigged fox with a walk (it slides a little, and its left front paw dips under the ground).
new()
body = fox_body()
body.data.materials.append(bpy.data.materials.new("M_Fox"))
body.data.materials[0].use_nodes = True
body.data.materials[0].node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (.7, .25, .06, 1)
arm = rig("FoxRig", FOX)
bind(body, arm)
sc = bpy.context.scene
sc.render.fps = 24
for f in range(1, 26):
    ph = 2 * math.pi * (f - 1) / 24
    for n, k, o in (("thigh.L", .5, 0), ("thigh.R", .5, math.pi), ("arm.L", .45, math.pi), ("arm.R", .45, 0)):
        pb = arm.pose.bones[n]
        pb.rotation_mode = "XYZ"
        pb.rotation_euler = (k * math.sin(ph + o), 0, 0)
        pb.keyframe_insert("rotation_euler", frame=f)
    h = arm.pose.bones["hips"]
    h.location = (0, 0, .012 * math.cos(2 * ph))
    h.keyframe_insert("location", frame=f)
arm.animation_data.action.name = "Walk"
arm.animation_data.action.use_fake_user = True
sc.frame_start, sc.frame_end = 1, 25
save("fox_rigged.blend")
glb("fox_rigged.glb")
# The same fox with faults in its rig: no bones in one hind leg, and a paw parented to the root.
new()
body = fox_body()
bones = {n: v for n, v in FOX.items() if n not in ("thigh.R", "shin.R", "hock.R", "paw.R")}
bones["hand.L"] = (bones["hand.L"][0], bones["hand.L"][1], "root")
arm = rig("FoxRig", bones)
bind(body, arm)
save("fox_badrig.blend")
new()
fox_body()
save("fox_unrigged.blend")

# 2. A person: an unrigged body in an A-pose, and a walk as a BVH in centimetres.
new()
pts = line((0, 0, .95), (0, 0, 1.4), .13, .15) + line((0, 0, 1.45), (0, 0, 1.55), .06, .06) + [((0, -.01, 1.68), .115)]
for s in (1, -1):
    pts += line((s * .09, 0, .95), (s * .11, 0, .5), .085, .06) + line((s * .11, 0, .5), (s * .12, .01, .08), .055, .04) + line((s * .12, .01, .045), (s * .12, -.16, .035), .045, .035)
    pts += line((s * .17, 0, 1.42), (s * .4, 0, 1.2), .055, .042) + line((s * .4, 0, 1.2), (s * .62, -.02, 1.0), .04, .03) + line((s * .62, -.02, 1.0), (s * .7, -.03, .93), .035, .025)
blob(pts, "Person", .016)
save("person_unrigged.blend")
glb("person_unrigged.glb")
new()
P = both({
    "Hips": ((0, 0, .95), (0, 0, 1.1), None), "Spine": ((0, 0, 1.1), (0, 0, 1.3), "Hips"), "Chest": ((0, 0, 1.3), (0, 0, 1.5), "Spine"), "Neck": ((0, 0, 1.5), (0, 0, 1.6), "Chest"), "Head": ((0, 0, 1.6), (0, -.02, 1.8), "Neck"),
    "Shoulder.L": ((.02, 0, 1.45), (.15, 0, 1.45), "Chest"), "UpperArm.L": ((.17, 0, 1.45), (.45, 0, 1.45), "Shoulder.L"), "LowerArm.L": ((.45, 0, 1.45), (.7, 0, 1.45), "UpperArm.L"), "Hand.L": ((.7, 0, 1.45), (.85, 0, 1.45), "LowerArm.L"),
    "UpperLeg.L": ((.1, 0, .92), (.1, -.02, .5), "Hips"), "LowerLeg.L": ((.1, -.02, .5), (.1, .02, .08), "UpperLeg.L"), "Foot.L": ((.1, .02, .08), (.1, -.12, .02), "LowerLeg.L"), "Toe.L": ((.1, -.12, .02), (.1, -.2, .02), "Foot.L")})
arm = rig("Mocap", P)
sc = bpy.context.scene
sc.render.fps = 30
for f in range(1, 91):
    ph = 2 * math.pi * f / 30
    h = arm.pose.bones["Hips"]
    h.location = (0, .03 * math.cos(2 * ph) - .03, 1.2 * f / 30)
    h.keyframe_insert("location", frame=f)
    for side, o in (("L", 0), ("R", math.pi)):
        for n, a in ((f"UpperLeg.{side}", -.45 * math.sin(ph + o)), (f"LowerLeg.{side}", .55 * max(0, math.sin(ph + o + 1.2))), (f"Foot.{side}", .2 * math.sin(ph + o)), (f"UpperArm.{side}", 0)):
            pb = arm.pose.bones[n]
            pb.rotation_mode = "XYZ"
            pb.rotation_euler = (a, 0, 0) if "Arm" not in n else (0, 0, 0)
            if "UpperArm" in n:
                pb.matrix_basis = Matrix.Identity(4)
                down = Matrix.Rotation(1.2 if side == "L" else -1.2, 3, "Y") @ Matrix.Identity(3)
                rest = arm.data.bones[n].matrix_local.to_3x3()
                pb.rotation_mode = "QUATERNION"
                pb.rotation_quaternion = (rest.inverted() @ Matrix.Rotation(.4 * math.sin(ph + o), 3, "X") @ down @ rest).to_quaternion()
                pb.keyframe_insert("rotation_quaternion", frame=f)
            else:
                pb.keyframe_insert("rotation_euler", frame=f)
arm.select_set(True)
bpy.context.view_layer.objects.active = arm
bpy.ops.export_anim.bvh(filepath=os.path.join(OUT, "person_walk.bvh"), global_scale=100, frame_start=1, frame_end=90, root_transform_only=True)

# 3. A barrel prop with faults: flipped faces, doubles, a floating part, an n-gon, unapplied scale, two faces in one place, no UVs.
new()
bpy.ops.mesh.primitive_cylinder_add(radius=.35, depth=1, location=(0, 0, .5), vertices=20)
b = bpy.context.object
b.name = "Barrel"
bm = bmesh.new()
bm.from_mesh(b.data)
bm.faces.ensure_lookup_table()
for i in (2, 5, 7):
    bm.faces[i].normal_flip()
g = bmesh.ops.duplicate(bm, geom=[bm.faces[3]])
bm.to_mesh(b.data)
bm.free()
bpy.ops.mesh.primitive_torus_add(major_radius=.37, minor_radius=.02, location=(0, 0, .25))
bpy.ops.mesh.primitive_cube_add(size=.1, location=(.6, 0, 1.3))
bpy.ops.object.select_all(action="SELECT")
bpy.context.view_layer.objects.active = b
bpy.ops.object.join()
b.scale = (1.4, 1.4, 1.2)
for layer in list(b.data.uv_layers):
    b.data.uv_layers.remove(layer)
save("barrel_faulty.blend")

# 4. A dressed room: a lamp floats 6 cm, a chair sinks 5 cm, a vase goes through a shelf, a book hovers.
new()


def box(name, size, at):
    bpy.ops.mesh.primitive_cube_add(size=1, location=at)
    o = bpy.context.object
    o.name = name
    o.scale = size
    bpy.ops.object.transform_apply(scale=True, location=False)
    return o


box("Floor", (5, 4, .1), (0, 0, -.05))
box("WallBack", (5, .1, 2.6), (0, 2, 1.3))
box("WallLeft", (.1, 4, 2.6), (-2.5, 0, 1.3))
box("TableTop", (1.4, .8, .05), (0, .4, .74))
for x in (-.62, .62):
    for y in (.1, .7):
        box(f"TableLeg_{x}_{y}", (.06, .06, .715), (x, y, .3575))
box("ChairSeat", (.45, .45, .05), (0, -.4, .40))
for x in (-.19, .19):
    for y in (-.59, -.21):
        box(f"ChairLeg_{x}_{y}", (.04, .04, .425), (x, y, .1625))
box("Shelf", (1.2, .3, .04), (-1.5, 1.8, 1.4))
bpy.ops.mesh.primitive_cylinder_add(radius=.09, depth=.3, location=(-1.5, 1.8, 1.48))
bpy.context.object.name = "Vase"
bpy.ops.mesh.primitive_cone_add(radius1=.15, radius2=.08, depth=.3, location=(.4, .5, .975))
bpy.context.object.name = "Lamp"
box("Book", (.2, .28, .04), (-.35, .4, .83))
box("Mug", (.09, .09, .1), (.1, .3, .815))
save("room_dressed.blend")

# 5. A high rock and the low one to bake it to; a high sculpt-like head with no clean topology.
new()
bpy.ops.mesh.primitive_ico_sphere_add(radius=.5, subdivisions=6, location=(0, 0, .5))
hi = bpy.context.object
hi.name = "Rock_high"
for v in hi.data.vertices:
    from mathutils import noise
    v.co += v.normal * (.12 * noise.fractal(v.co * 3, 1, 2, 5))
for p in hi.data.polygons:
    p.use_smooth = True
lo = hi.copy()
lo.data = hi.data.copy()
lo.name = "Rock_low"
bpy.context.scene.collection.objects.link(lo)
m = lo.modifiers.new("d", "DECIMATE")
m.ratio = .02
bpy.context.view_layer.objects.active = lo
bpy.ops.object.select_all(action="DESELECT")
lo.select_set(True)
bpy.ops.object.modifier_apply(modifier="d")
save("rock_high_low.blend")
new()
blob([((0, 0, 1.6), .16), ((0, -.05, 1.52), .13), ((0, -.19, 1.58), .03), ((0, -.17, 1.5), .04), ((.06, -.14, 1.63), .025), ((-.06, -.14, 1.63), .025), ((.17, 0, 1.58), .035), ((-.17, 0, 1.58), .035), ((0, .02, 1.4), .08), ((0, -.15, 1.455), .03)], "Head", .006)
save("head_highpoly.blend")

# 6. A cart of unknown facing: lying on its side, facing +X, a hundred times too big, origin off.
new()
parts = [box("bed", (.8, 1.4, .2), (0, 0, .5)), box("seat", (.7, .2, .4), (0, -.5, .8)), box("lamp", (.12, .1, .12), (0, -.76, .62))]
for x in (-.45, .45):
    for y in (-.45, .5):
        bpy.ops.mesh.primitive_cylinder_add(radius=.28 if y > 0 else .2, depth=.1, location=(x, y, .28 if y > 0 else .2), rotation=(0, math.pi / 2, 0))
        parts.append(bpy.context.object)
bpy.ops.object.select_all(action="DESELECT")
for o in parts:
    o.select_set(True)
bpy.context.view_layer.objects.active = parts[0]
bpy.ops.object.join()
o = bpy.context.object
o.name = "Cart"
o.matrix_world = Matrix.Translation((300, 40, 0)) @ Matrix.Rotation(math.pi / 2, 4, "X") @ Matrix.Rotation(math.pi / 2, 4, "Z") @ Matrix.Scale(100, 4)
bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
glb("cart_unknown.glb")

# 7. Four kit props that don't match: sizes, origins and colours all over the place.
os.makedirs(os.path.join(OUT, "kit"), exist_ok=True)
for name, make, color, scale, off in (
        ("mug", lambda: bpy.ops.mesh.primitive_cylinder_add(radius=.04, depth=.1), (.1, .4, .9, 1), 30, (1, 2, 0)), ("stool", lambda: bpy.ops.mesh.primitive_cylinder_add(radius=.18, depth=.45), (.9, .1, .6, 1), .1, (0, 0, 3)),
        ("crate", lambda: bpy.ops.mesh.primitive_cube_add(size=.5), (.5, .35, .2, 1), 1, (5, 0, 0)), ("lantern", lambda: bpy.ops.mesh.primitive_cone_add(radius1=.1, radius2=.06, depth=.3), (.2, .9, .2, 1), 7, (0, -2, 1))):
    new()
    make()
    o = bpy.context.object
    o.name = name
    mat = bpy.data.materials.new("M")
    mat.use_nodes = True
    mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = color
    o.data.materials.append(mat)
    o.scale = (scale,) * 3
    o.location = off
    glb(os.path.join("kit", name + ".glb"))

# 8. A folder of mixed models for batch work.
os.makedirs(os.path.join(OUT, "models"), exist_ok=True)
for i, name in enumerate(("pot", "post", "ball", "ring", "wedge", "slab")):
    new()
    [lambda: bpy.ops.mesh.primitive_cylinder_add(), lambda: bpy.ops.mesh.primitive_cube_add(), lambda: bpy.ops.mesh.primitive_uv_sphere_add(segments=64, ring_count=48), lambda: bpy.ops.mesh.primitive_torus_add(), lambda: bpy.ops.mesh.primitive_cone_add(vertices=3), lambda: bpy.ops.mesh.primitive_grid_add(x_subdivisions=60, y_subdivisions=60)][i]()
    bpy.context.object.name = name
    p = os.path.join(OUT, "models", name)
    if i % 3 == 0:
        bpy.ops.wm.obj_export(filepath=p + ".obj")
    elif i % 3 == 1:
        bpy.ops.export_scene.fbx(filepath=p + ".fbx")
    else:
        bpy.ops.export_scene.gltf(filepath=p + ".glb")

# 9. Reference pictures of a lantern, front, side and three-quarter, and a small scene to match.
new()


def mat(name, color, rough=.6, metal=0, emit=0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    n = m.node_tree.nodes["Principled BSDF"]
    n.inputs["Base Color"].default_value = (*color, 1)
    n.inputs["Roughness"].default_value = rough
    n.inputs["Metallic"].default_value = metal
    if emit:
        n.inputs["Emission Color"].default_value = (*color, 1)
        n.inputs["Emission Strength"].default_value = emit
    return m


iron, glass, wood = mat("iron", (.05, .05, .06), .5, .8), mat("glass", (1, .65, .2), .2, 0, 3), mat("wood", (.35, .18, .07))


def put(o, m):
    o = bpy.context.object
    o.data.materials.append(m)
    return o


bpy.ops.mesh.primitive_cylinder_add(radius=.16, depth=.05, location=(0, 0, .025), vertices=6); put(0, iron)
bpy.ops.mesh.primitive_cylinder_add(radius=.11, depth=.28, location=(0, 0, .19), vertices=6); put(0, glass)
for i in range(6):
    a = math.pi / 3 * i
    bpy.ops.mesh.primitive_cylinder_add(radius=.012, depth=.28, location=(.125 * math.cos(a), .125 * math.sin(a), .19)); put(0, iron)
bpy.ops.mesh.primitive_cone_add(radius1=.18, radius2=.05, depth=.12, location=(0, 0, .39), vertices=6); put(0, iron)
bpy.ops.mesh.primitive_torus_add(major_radius=.06, minor_radius=.01, location=(0, 0, .5), rotation=(math.pi / 2, 0, 0)); put(0, iron)
bpy.ops.mesh.primitive_cylinder_add(radius=.018, depth=.14, location=(0, 0, .56), rotation=(0, math.pi / 2, 0)); put(0, wood)
sc = bpy.context.scene
sc.render.engine = "BLENDER_EEVEE"
sc.render.resolution_x, sc.render.resolution_y = 640, 640
w = bpy.data.worlds.new("w")
sc.world = w
w.use_nodes = True
w.node_tree.nodes["Background"].inputs[0].default_value = (.5, .5, .5, 1)
sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
sun.data.energy = 3
sun.rotation_euler = (.8, .2, .5)
sc.collection.objects.link(sun)
cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
sc.collection.objects.link(cam)
sc.camera = cam


def shot(name, eye, at, lens=50):
    cam.data.lens = lens
    cam.location = eye
    cam.rotation_euler = (Vector(at) - Vector(eye)).to_track_quat("-Z", "Y").to_euler()
    sc.render.filepath = os.path.join(OUT, name)
    bpy.ops.render.render(write_still=True)


shot("lantern_front.png", (0, -1.5, .3), (0, 0, .3))
shot("lantern_side.png", (1.5, 0, .3), (0, 0, .3))
shot("lantern_threequarter.png", (1, -1.1, .75), (0, 0, .28))
# The scene to match: an evening clearing, warm light, blue shadow, a hut, trees and a path.
bpy.ops.mesh.primitive_plane_add(size=30); put(0, mat("grass", (.12, .22, .06), .9))
bpy.ops.mesh.primitive_plane_add(size=1, location=(0, -2, .01)); o = put(0, mat("path", (.35, .25, .15), .9)); o.scale = (1.2, 9, 1)
bpy.ops.mesh.primitive_cube_add(size=2, location=(0, 5, 1)); put(0, mat("wall", (.75, .6, .42), .8))
bpy.ops.mesh.primitive_cone_add(radius1=2, radius2=0, depth=1.6, vertices=4, location=(0, 5, 2.8), rotation=(0, 0, math.pi / 4)); put(0, mat("roof", (.4, .12, .08), .8))
for x, y, s in ((-4, 4, 1.2), (4.2, 6, 1.5), (-6, 8, 1.6), (6.5, 3, 1.1), (3, 10, 1.7)):
    bpy.ops.mesh.primitive_cylinder_add(radius=.15 * s, depth=1.4 * s, location=(x, y, .7 * s)); put(0, wood)
    bpy.ops.mesh.primitive_uv_sphere_add(radius=1 * s, location=(x, y, 2 * s)); put(0, mat("leaf", (.2, .35, .08), .9)); bpy.ops.object.shade_smooth()
sun.data.energy = 4
sun.data.color = (1, .65, .35)
sun.rotation_euler = (1.25, .1, .9)
w.node_tree.nodes["Background"].inputs[0].default_value = (.25, .35, .65, 1)
sc.render.resolution_x, sc.render.resolution_y = 960, 540
shot("scene_target.png", (0, -9, 2.2), (0, 4, 1.4), 32)
# A clip with a moving camera over it, for tracking.
os.makedirs(os.path.join(OUT, "track"), exist_ok=True)
for i in range(40):
    R.seed(i)
    bpy.ops.mesh.primitive_cube_add(size=.18, location=(R.uniform(-5, 5), R.uniform(-5, 4), .09), rotation=(0, 0, R.uniform(0, 3)))
    put(0, mat(f"c{i}", (R.random(), R.random(), R.random())))
sc.render.resolution_x, sc.render.resolution_y = 960, 540
for f in range(60):
    t = f / 59
    shot(os.path.join("track", f"f_{f + 1:04d}.png"), (-2 + 4 * t, -9 + 1.5 * t, 2.2 + .4 * t), (0, 4, 1), 32)

# 10. A ground and a hut to light, weather and dress.
save("clearing.blend")
print("FIXTURES DONE")
