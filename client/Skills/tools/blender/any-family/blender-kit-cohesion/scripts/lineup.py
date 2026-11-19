"""Lineup render of a kit: every item side by side on a neutral ground at true relative scale, with a scale bar and
a label in front of each, under one studio light. Optionally a three-quarter thumbnail per item.

Items come from GLB files (--glbs, or every *.glb in --folder, or kit.json's items in its order) or from objects in
the open .blend (--objects / --collection: each named object with its children is one item).
Usage:
  blender -b --factory-startup -P lineup.py -- --kit kit/kit.json --out kit/lineup.png [--thumbs kit/thumbs]
  blender -b --factory-startup -P lineup.py -- --folder kit/items --out kit/lineup.png
  blender -b props.blend -P lineup.py -- --collection Props --out //lineup.png
      [--engine eevee|cycles] [--samples 64] [--warmth 0] [--res 2400 1000] [--no-labels]
--warmth: -1 cold to 1 warm key light. Labels come from kit.json ("label"), else the item's name.
It changes the open file (never saving it), so run it headless. Returns {"lineup", "thumbs", "items"}.
Turntables per item: blender-lighting-lookdev's turntable.py.
"""
import glob
import json
import math
import os
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _anyx as ax  # noqa: E402


def _kelvin(k):
    """Rough blackbody tint, enough for a warm or cool key."""
    t = k / 100
    r = 1.0 if t <= 66 else max(0, min(1, 1.292936 * (t - 60) ** -0.1332047592))
    g = max(0, min(1, (0.39008157876 * math.log(t) - 0.63184144) if t <= 66 else 1.129890861 * (t - 60) ** -0.0755148492))
    b = 1.0 if t >= 66 else (0 if t <= 19 else max(0, min(1, 0.54320678911 * math.log(t - 10) - 1.19625408914)))
    return (r, g, b)


def _mat(name, rgb, rough=0.6):
    m = bpy.data.materials.new(name)
    b = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    b.inputs["Base Color"].default_value = (*rgb, 1)
    b.inputs["Roughness"].default_value = rough
    m.diffuse_color = (*rgb, 1)
    return m


def _link(o, coll):
    coll.objects.link(o)
    return o


def _bounds(objs):
    bpy.context.view_layer.update()
    pts = [o.matrix_world @ Vector(c) for o in objs if o.type in {"MESH", "CURVE", "FONT"} for c in o.bound_box]
    return (Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts))),
            Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts))))


def _text(body, loc, size, m, coll, align="CENTER"):
    c = bpy.data.curves.new("label", "FONT")
    c.body, c.size, c.align_x = body, size, align
    o = _link(bpy.data.objects.new("_label_" + body[:20], c), coll)
    o.location = loc
    o.rotation_euler = (math.pi / 2, 0, 0)  # stands up on the ground, facing the camera (-Y)
    o.data.materials.append(m)
    return o


def _light(name, loc, target, energy, size, color, coll):
    d = bpy.data.lights.new(name, "AREA")
    d.energy, d.size, d.color = energy, size, color
    o = _link(bpy.data.objects.new(name, d), coll)
    o.location = loc
    o.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()  # area lights shine down -Z
    return o


def _studio(centre, radius, warmth, coll):
    for o in [o for o in coll.objects if o.type == "LIGHT"]:
        bpy.data.objects.remove(o)
    k = 5600 - 1800 * warmth
    c, s = Vector(centre), max(radius, 0.2)
    _light("_key", c + Vector((1.6, -2.2, 2.4)) * s, c, 220 * s * s, 1.6 * s, _kelvin(k), coll)
    _light("_fill", c + Vector((-2.6, -1.4, 1.2)) * s, c, 60 * s * s, 2.5 * s, _kelvin(k + 1500), coll)
    _light("_rim", c + Vector((-0.8, 2.6, 2.2)) * s, c, 140 * s * s, 1.4 * s, (1, 1, 1), coll)


def _aim(cam, centre, az, el, dist):
    a, e = math.radians(az), math.radians(el)
    cam.location = Vector(centre) + Vector((math.sin(a) * math.cos(e), -math.cos(a) * math.cos(e), math.sin(e))) * dist
    cam.rotation_euler = (Vector(centre) - cam.location).to_track_quat("-Z", "Y").to_euler()


def _items(folder, glbs, kit, objects, collection):
    """[(id, label, objects)] with the scene holding only those items."""
    labels, order = {}, None
    if kit:
        with open(ax.path(kit), encoding="utf-8") as f:
            k = json.load(f)
        base = os.path.dirname(ax.path(kit))
        order = []
        for it in k.get("items", []):
            labels[it["id"]] = it.get("label", it["id"])
            g = it.get("glb") or os.path.join(base, "items", it["id"], f"{it['id']}.glb")
            order.append((it["id"], g if os.path.isabs(g) else os.path.join(base, g)))
    files = order or [(os.path.splitext(os.path.basename(p))[0], p) for p in
                      (glbs or sorted(glob.glob(os.path.join(ax.path(folder), "**", "*.glb"), recursive=True)) if (glbs or folder) else [])]
    sc = bpy.context.scene
    if files:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        sc = bpy.context.scene
        out = []
        for iid, p in files:
            p = ax.path(p)
            if not os.path.exists(p):
                raise FileNotFoundError(p)
            before = set(bpy.data.objects)
            bpy.ops.import_scene.gltf(filepath=p)
            new = [o for o in bpy.data.objects if o not in before]
            root = _link(bpy.data.objects.new(iid, None), sc.collection)
            for o in new:
                if o.parent is None:
                    o.parent = root
            out.append((iid, labels.get(iid, iid), [root, *root.children_recursive]))
        return out
    roots = ax.objects(objects, collection, types=None)
    roots = [o for o in roots if o.parent is None or o.parent not in roots]
    keep = {x for o in roots for x in (o, *o.children_recursive)}
    for o in list(sc.objects):
        if o not in keep:
            bpy.data.objects.remove(o)  # never saved: the file on disk keeps them
    out = []
    for o in roots:
        if o.parent is not None:
            mw = o.matrix_world.copy()
            o.parent = None
            o.matrix_world = mw
        out.append((o.name, labels.get(o.name, o.get("label", o.name)), [o, *o.children_recursive]))
    return out


def run(folder=None, glbs=None, kit=None, objects=None, collection=None, out="//lineup.png", thumbs=None,
        engine="eevee", samples=64, warmth=0.0, res=(2400, 1000), labels=True):
    ax.headless_only("lineup")
    out = ax.path(out)
    thumbs = ax.path(thumbs) if thumbs else None
    items = _items(folder, glbs, kit, objects, collection)
    if not items:
        raise ValueError("no items: give --kit, --folder, --glbs, --objects or --collection")
    sc = bpy.context.scene
    r = sc.render
    r.engine = {"eevee": "BLENDER_EEVEE", "cycles": "CYCLES"}[engine]
    if engine == "cycles":
        sc.cycles.samples = samples
        sc.cycles.use_denoising = True
    else:
        sc.eevee.taa_render_samples = samples
    try:
        sc.view_settings.view_transform = "AgX"
        sc.view_settings.look = "AgX - Medium High Contrast"
    except TypeError:
        pass
    sc.world = sc.world or bpy.data.worlds.new("_lineup")
    bg = next((n for n in sc.world.node_tree.nodes if n.type == "BACKGROUND"), None)
    if bg:
        bg.inputs["Color"].default_value = (0.36, 0.37, 0.38, 1)
        bg.inputs["Strength"].default_value = 0.35
    stage = bpy.data.collections.new("_lineup_stage")
    sc.collection.children.link(stage)

    # one row, left to right, a gap that scales with the tallest item
    for _i, _l, objs in items:
        for o in objs:
            o.animation_data_clear()  # rest pose, and a keyed location can't pull an item out of the row
    meshes = {iid: [o for o in objs if o.type == "MESH"] for iid, _l, objs in items}
    items = [it for it in items if meshes[it[0]]]
    tallest = max(_bounds(meshes[iid])[1].z - _bounds(meshes[iid])[0].z for iid, _l, _o in items)
    gap = max(0.06, 0.12 * tallest)
    x, depth = 0.0, 0.0
    for iid, _l, objs in items:
        root = objs[0]
        lo, hi = _bounds(meshes[iid])
        root.location.x += x - lo.x
        root.location.y -= (lo.y + hi.y) / 2
        root.location.z -= lo.z  # stand it on the ground
        x += (hi.x - lo.x) + gap
        depth = max(depth, hi.y - lo.y)
    allm = [o for v in meshes.values() for o in v]
    lo, hi = _bounds(allm)
    total = hi.x - lo.x
    ground = _link(bpy.data.objects.new("_ground", bpy.data.meshes.new("_ground")), stage)
    s = max(total, tallest) * 20 + 10
    ground.data.from_pydata([(-s, -s, 0), (s, -s, 0), (s, s, 0), (-s, s, 0)], [], [(0, 1, 2, 3)])
    ground.data.materials.append(_mat("_ground", (0.42, 0.42, 0.42), 0.85))

    ink = _mat("_ink", (0.04, 0.04, 0.04))
    if labels:
        # scale bar: a "nice" length about a fifth of the row, in alternating blocks
        nice = min((0.05, 0.1, 0.2, 0.25, 0.5, 1.0, 2.0, 5.0, 10.0), key=lambda v: abs(v - max(total, tallest) / 5))
        lsize = max(0.01, min(0.06, gap * 0.3, tallest * 0.08))
        step = nice / 10 if nice <= 0.25 else (0.1 if nice <= 1 else nice / 10)
        dark, light = _mat("_bar_dark", (0.03, 0.03, 0.03)), _mat("_bar_light", (0.85, 0.85, 0.85))
        by = lo.y - max(0.06, depth * 0.35, lsize * 4)  # in front of the item labels
        th = max(0.004, nice / 40)
        for i in range(round(nice / step)):
            b = _link(bpy.data.objects.new(f"_bar{i}", bpy.data.meshes.new("_bar")), stage)
            x0, x1 = lo.x + step * i, lo.x + step * (i + 1)
            b.data.from_pydata([(x0, by - th, 0.001), (x1, by - th, 0.001), (x1, by + th, 0.001), (x0, by + th, 0.001)],
                               [], [(0, 1, 2, 3)])
            b.data.materials.append(dark if i % 2 == 0 else light)
        _text(f"{nice * 100:.0f} cm" if nice < 1 else f"{nice:g} m", (lo.x + nice + th * 3, by, 0.002), max(th * 4, lsize),
              ink, stage, "LEFT")
        for iid, label, _objs in items:
            l2, h2 = _bounds(meshes[iid])
            _text(str(label)[:28], ((l2.x + h2.x) / 2, l2.y - lsize * 1.6, 0.002), lsize, ink, stage)

    cam = _link(bpy.data.objects.new("_lineup_cam", bpy.data.cameras.new("_lineup_cam")), stage)
    cam.data.lens = 50
    cam.data.clip_end = 1000
    sc.camera = cam
    r.resolution_x, r.resolution_y = res
    r.resolution_percentage = 100
    r.image_settings.file_format = "PNG"
    lo2, hi2 = _bounds(allm)
    centre = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2 - 0.1 * depth, hi2.z * 0.42))
    width = max(total * 1.2, hi2.z * 2.4 * res[0] / res[1] / 2)
    _aim(cam, centre, 0, 14, (width / 2) / math.tan(cam.data.angle_x / 2) + depth)
    _studio(centre, max(total, hi2.z) * 0.6, warmth, stage)
    r.filepath = out
    bpy.ops.render.render(write_still=True)
    result = {"lineup": out, "items": [iid for iid, _l, _o in items], "row_width_m": round(total, 3),
              "tallest_m": round(tallest, 3)}

    if thumbs:
        os.makedirs(thumbs, exist_ok=True)
        r.resolution_x = r.resolution_y = 1024
        result["thumbs"] = {}
        for o in stage.objects:
            if o.name.startswith(("_bar", "_label")):
                o.hide_render = True
        cam.data.lens = 70
        for iid, _l, objs in items:
            for jid, _l2, others in items:
                for o in others:
                    o.hide_render = jid != iid
            lo3, hi3 = _bounds(meshes[iid])
            c3, rad = (lo3 + hi3) / 2, (hi3 - lo3).length / 2
            _studio(c3, rad * 1.4, warmth, stage)
            _aim(cam, c3, -30, 20, rad / math.sin(cam.data.angle / 2) * 1.05)
            r.filepath = os.path.join(thumbs, f"{iid}.png")
            bpy.ops.render.render(write_still=True)
            result["thumbs"][iid] = r.filepath
    result["note"] = "Open the lineup: same world, sizes that make sense side by side, nothing cut off."
    return result


if __name__ == "__main__":
    p = ax.parser(__doc__)
    p.add_argument("--folder")
    p.add_argument("--glbs", nargs="*")
    p.add_argument("--kit")
    p.add_argument("--objects", nargs="*")
    p.add_argument("--collection")
    p.add_argument("--out", default="//lineup.png")
    p.add_argument("--thumbs")
    p.add_argument("--engine", default="eevee", choices=("eevee", "cycles"))
    p.add_argument("--samples", type=int, default=64)
    p.add_argument("--warmth", type=float, default=0.0)
    p.add_argument("--res", nargs=2, type=int, default=[2400, 1000])
    p.add_argument("--no-labels", dest="labels", action="store_false")
    ax.cli(run, p)
