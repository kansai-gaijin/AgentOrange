"""Render the model from each reference's view and put it beside that reference, one compare image per view.

Views are named. The built-in ones look at the objects' bounds: front (from -Y, the model faces -Y), back, left
(from -X), right, top (orthographic unless --persp), three_quarter (perspective, front left, from above). Give your
own cameras, matched to a reference's, in a JSON file: {"name": {"eye": [x,y,z], "target": [x,y,z], "lens": 50}}
or {"name": {"eye": ..., "target": ..., "ortho_scale": 4.0}}.
A view with a reference image (--refs front=refs/front.png ...) becomes <out>/<view>_compare.png, reference on the
left and the render on the right at the same height. Every view goes on <out>/compare_sheet.png.
Usage:
  blender -b f.blend -P compare_views.py -- --out //compare [--objects A B | --collection Model]
      [--views front left three_quarter] [--views-json cams.json] [--refs front=//refs/front.png left=...]
      [--look clay|material] [--engine workbench|eevee|cycles] [--res 900] [--persp] [--samples 32]
  MCP: import compare_views; print(compare_views.run(out="//compare", refs={"front": "//refs/front.png"}))
Returns {"views": {name: {"render", "compare"?}}, "sheet"}. Open the images and write down what differs.
The scene's camera, engine and shading are put back afterwards; nothing is saved.
"""
import json
import math
import os
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _anyx as ax  # noqa: E402

BUILT_IN = {  # direction from the target to the eye, ortho?
    "front": ((0, -1, 0), True), "back": ((0, 1, 0), True), "left": ((-1, 0, 0), True), "right": ((1, 0, 0), True),
    "top": ((0, 0, 1), True), "three_quarter": ((-0.8, -1.0, 0.65), False),
}


def _bounds(objs):
    dg = bpy.context.evaluated_depsgraph_get()
    pts = []
    for o in objs:
        oe = o.evaluated_get(dg)
        pts += [oe.matrix_world @ Vector(c) for c in oe.bound_box]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return lo, hi


def _cameras(objs, names, custom, persp):
    lo, hi = _bounds(objs)
    c, size = (lo + hi) / 2, hi - lo
    radius = max(size.length / 2, 0.01)
    out = {}
    for name in names:
        if name in custom:
            v = custom[name]
            out[name] = (Vector(v["eye"]), Vector(v["target"]), v.get("lens", 50), v.get("ortho_scale"))
            continue
        if name not in BUILT_IN:
            raise KeyError(f"unknown view {name!r}: built-in {sorted(BUILT_IN)}, or add it with --views-json")
        d, ortho = BUILT_IN[name]
        d = Vector(d).normalized()
        ortho = ortho and not persp
        # what the view sees across: the two box extents perpendicular to it
        across = max(abs(size.x) if abs(d.x) < 0.5 else 0, abs(size.y) if abs(d.y) < 0.5 else 0,
                     abs(size.z) if abs(d.z) < 0.5 else 0)
        lens = 50
        if ortho:
            out[name] = (c + d * radius * 4, c, lens, across * 1.15)
        else:
            fov = 2 * math.atan(36 / 2 / lens)
            out[name] = (c + d * (radius * 1.1 / math.sin(fov / 2)), c, lens, None)
    return out


def _clay(sc, look, engine):
    sh = sc.display.shading
    if engine == "BLENDER_WORKBENCH":
        sh.light = "STUDIO"
        sh.color_type = "SINGLE" if look == "clay" else "TEXTURE"
        sh.single_color = (0.62, 0.62, 0.62)
        sh.show_cavity = True
        sh.cavity_type = "BOTH"
        sh.show_object_outline = False
        sh.show_shadows = False
        return None
    if look != "clay":
        return None
    m = bpy.data.materials.new("_compare_clay")
    b = ax_node(m, "BSDF_PRINCIPLED")
    b.inputs["Base Color"].default_value = (0.42, 0.42, 0.42, 1)
    b.inputs["Roughness"].default_value = 0.6
    bev = m.node_tree.nodes.new("ShaderNodeBevel")  # shows edges a little, as clay renders should
    bev.inputs["Radius"].default_value = 0.004
    m.node_tree.links.new(bev.outputs[0], b.inputs["Normal"])
    return m


def ax_node(m, type_):
    return next(n for n in m.node_tree.nodes if n.type == type_)


def run(out="//compare", objects=None, collection=None, views=None, views_json=None, refs=None, look="clay",
        engine="workbench", res=900, persp=False, samples=32):
    objs = ax.objects(objects, collection)
    if not objs:
        raise ValueError("no mesh objects to render")
    out = ax.path(out)
    os.makedirs(out, exist_ok=True)
    custom = {}
    if views_json:
        with open(ax.path(views_json), encoding="utf-8") as f:
            custom = json.load(f)
    if isinstance(refs, (list, tuple)):
        refs = dict(x.split("=", 1) for x in refs)
    refs = refs or {}
    names = views or ([n for n in custom] + [n for n in refs if n not in custom]) or list(BUILT_IN)
    eng = {"workbench": "BLENDER_WORKBENCH", "eevee": "BLENDER_EEVEE", "cycles": "CYCLES"}[engine]
    sc = bpy.context.scene
    r = sc.render
    keep = dict(camera=sc.camera, engine=r.engine, x=r.resolution_x, y=r.resolution_y, pct=r.resolution_percentage,
                path=r.filepath, fmt=r.image_settings.file_format, override=sc.view_layers[0].material_override)
    sh = sc.display.shading
    keep_sh = {k: getattr(sh, k) for k in ("light", "color_type", "single_color", "show_cavity", "cavity_type",
                                           "show_object_outline", "show_shadows")}
    hidden = [o for o in sc.objects if o.type in {"MESH", "CURVE", "FONT"} and o not in objs and not o.hide_render]
    cam = bpy.data.objects.new("_compare_cam", bpy.data.cameras.new("_compare_cam"))
    sc.collection.objects.link(cam)
    clay = None
    result = {"views": {}}
    try:
        for o in hidden:
            o.hide_render = True
        r.engine = eng
        r.resolution_x = r.resolution_y = res
        r.resolution_percentage = 100
        r.image_settings.file_format = "PNG"
        if eng == "CYCLES":
            sc.cycles.samples = samples
        clay = _clay(sc, look, eng)
        if clay:
            sc.view_layers[0].material_override = clay
        sc.camera = cam
        cams = _cameras(objs, names, custom, persp)
        tiles = []
        for name, (eye, target, lens, ortho) in cams.items():
            cam.location = eye
            cam.rotation_euler = (target - eye).to_track_quat("-Z", "Y" if abs((target - eye).normalized().z) < 0.99 else "X").to_euler()
            cam.data.type = "ORTHO" if ortho else "PERSP"
            cam.data.lens = lens
            cam.data.clip_end = max(100.0, (target - eye).length * 4)
            if ortho:
                cam.data.ortho_scale = ortho
            shot = os.path.join(out, f"{name}.png")
            r.filepath = shot
            bpy.ops.render.render(write_still=True)
            entry = {"render": shot}
            px = ax.load_pixels(shot)
            if name in refs:
                ref = ax.fit_height(ax.load_pixels(refs[name]), px.shape[0])
                pair, w, h = ax.sheet([ref, px], 2)
                entry["compare"] = ax.save_png(pair, w, h, os.path.join(out, f"{name}_compare.png"))
                tiles += [ref, px]
            else:
                tiles += [None, px] if refs else [px]
            result["views"][name] = entry
        if tiles:
            cols = 2 if refs else min(3, len(tiles))
            px, w, h = ax.sheet([None if t is None else ax.fit_height(t, 400) for t in tiles], cols)
            result["sheet"] = ax.save_png(px, w, h, os.path.join(out, "compare_sheet.png"))
        result["note"] = "Open each compare image and list what differs from the reference before the second pass."
        return result
    finally:
        for o in hidden:
            o.hide_render = False
        sc.camera = keep["camera"]
        r.engine, r.resolution_x, r.resolution_y = keep["engine"], keep["x"], keep["y"]
        r.resolution_percentage, r.filepath, r.image_settings.file_format = keep["pct"], keep["path"], keep["fmt"]
        sc.view_layers[0].material_override = keep["override"]
        for k, v in keep_sh.items():
            setattr(sh, k, v)
        data = cam.data
        bpy.data.objects.remove(cam)
        bpy.data.cameras.remove(data)
        if clay:
            bpy.data.materials.remove(clay)


if __name__ == "__main__":
    p = ax.parser(__doc__)
    p.add_argument("--out", default="//compare")
    p.add_argument("--objects", nargs="*")
    p.add_argument("--collection")
    p.add_argument("--views", nargs="*")
    p.add_argument("--views-json", dest="views_json")
    p.add_argument("--refs", nargs="*", help="view=image pairs")
    p.add_argument("--look", default="clay", choices=("clay", "material"))
    p.add_argument("--engine", default="workbench", choices=("workbench", "eevee", "cycles"))
    p.add_argument("--res", type=int, default=900)
    p.add_argument("--persp", action="store_true")
    p.add_argument("--samples", type=int, default=32)
    ax.cli(run, p)
