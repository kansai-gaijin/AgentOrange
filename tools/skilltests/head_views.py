"""Renders the bare head from six sides, square, orthographic, even light, white background.
    blender -b -P head_views.py -- <obj> <out folder>
"""
import bpy, math, sys
from mathutils import Vector

obj_path, out = sys.argv[sys.argv.index("--") + 1:][:2]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.wm.obj_import(filepath=obj_path)
head = [o for o in bpy.context.scene.objects if o.type == "MESH"][0]
pts = [head.matrix_world @ Vector(c) for c in head.bound_box]
lo = Vector([min(p[i] for p in pts) for i in range(3)])
hi = Vector([max(p[i] for p in pts) for i in range(3)])
mid, size = (lo + hi) / 2, max(hi - lo)
print("head bounds", tuple(round(v, 3) for v in lo), tuple(round(v, 3) for v in hi))

scene = bpy.context.scene
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = scene.render.resolution_y = 1024
scene.render.film_transparent = False
scene.view_settings.view_transform = "Standard"
world = bpy.data.worlds.new("w")
scene.world = world
world.use_nodes = True
world.node_tree.nodes["Background"].inputs[0].default_value = (1, 1, 1, 1)
world.node_tree.nodes["Background"].inputs[1].default_value = 1.0

cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
scene.collection.objects.link(cam)
scene.camera = cam
cam.data.type = "ORTHO"
cam.data.ortho_scale = size * 1.9  # room for hair around the head

# The OBJ came from Blender (Z up after import); the face looks down -Y.
views = {"front": 0, "left_45": 45, "left": 90, "back": 180, "right": 270, "right_45": 315}
for name, deg in views.items():
    a = math.radians(deg)
    # subject's left is +X; the left profile camera sits on the subject's left
    d = Vector((math.sin(a), -math.cos(a), 0))
    cam.location = mid + d * size * 4 + Vector((0, 0, size * 0.05))
    cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
    scene.render.filepath = f"{out}/head_{name}.png"
    bpy.ops.render.render(write_still=True)
