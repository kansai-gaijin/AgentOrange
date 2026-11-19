"""Build a Blender asset library .blend from a kit: every item one object marked as an asset, with its label as the
description, tags, a catalog, its size and triangles as properties, a preview where Blender can make one, and its
LOD1 (<id>_lod1.glb beside the item, if there is one) in a hidden LOD1 collection. Everything is packed, and a
blender_assets.cats.txt is written beside the .blend, so the folder opens in the Asset Browser as a library.

Items come from GLB files (--kit kit.json, --glbs, or every *.glb in --folder that isn't a _lod1) or from objects in
the open .blend (--objects / --collection, each joined with its children).
Usage:
  blender -b --factory-startup -P asset_library.py -- --kit kit/kit.json --out kit/library/kit.blend [--name "Night market"]
  blender -b props.blend -P asset_library.py -- --collection Props --out //library/props.blend [--tags prop street]
It resets the open file (never saving over it), so run it headless. Returns {"library", "catalog", "assets"}.
"""
import glob
import json
import os
import sys
import uuid

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _anyx as ax  # noqa: E402


def _join(objs, name, coll):
    """Join meshes into one object with transforms applied, named `name`, in `coll`. Returns it."""
    meshes = [o for o in objs if o.type == "MESH"]
    if not meshes:
        raise ValueError(f"{name}: no meshes")
    for o in bpy.context.scene.objects:
        o.select_set(False)
    for o in meshes:
        mw = o.matrix_world.copy()
        o.parent = None
        o.matrix_world = mw
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    for o in objs:
        if o.name in bpy.data.objects and o is not ob and o.type != "MESH":
            bpy.data.objects.remove(o)
    ob.name = ob.data.name = name
    for c in list(ob.users_collection):
        c.objects.unlink(ob)
    coll.objects.link(ob)
    return ob


def _import(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    return [o for o in bpy.data.objects if o not in before]


def _tris(ob):
    ob.data.calc_loop_triangles()
    return len(ob.data.loop_triangles)


def run(kit=None, glbs=None, folder=None, objects=None, collection=None, out="//library/kit.blend", name=None, tags=None):
    ax.headless_only("asset_library")
    out = ax.path(out)
    entries, kit_name = [], None
    if kit:
        kp = ax.path(kit)
        with open(kp, encoding="utf-8") as f:
            k = json.load(f)
        kit_name = k.get("name")
        base = os.path.dirname(kp)
        for it in k.get("items", []):
            g = it.get("glb") or os.path.join(base, "items", it["id"], f"{it['id']}.glb")
            entries.append(dict(it, glb=g if os.path.isabs(g) else os.path.join(base, g)))
    elif glbs or folder:
        paths = glbs or sorted(glob.glob(os.path.join(ax.path(folder), "**", "*.glb"), recursive=True))
        entries = [{"id": os.path.splitext(os.path.basename(p))[0], "glb": ax.path(p)} for p in paths
                   if not p.endswith("_lod1.glb")]
    name = name or kit_name or "Kit"
    cat_id = str(uuid.uuid5(uuid.NAMESPACE_URL, "agent-orange-kit/" + name))
    if entries:
        bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    main = bpy.data.collections.new(name)
    sc.collection.children.link(main)
    lods = bpy.data.collections.new("LOD1")
    sc.collection.children.link(lods)
    made = []
    if entries:
        for it in entries:
            ob = _join(_import(it["glb"]), it["id"], main)
            lod = os.path.splitext(it["glb"])[0] + "_lod1.glb"
            made.append((ob, it, _join(_import(lod), it["id"] + "_lod1", lods) if os.path.exists(lod) else None))
    else:
        roots = ax.objects(objects, collection, types=None)
        roots = [o for o in roots if o.parent is None or o.parent not in roots]
        for o in roots:
            iid = o.name
            made.append((_join([o, *o.children_recursive], iid, main), {"id": iid, "label": o.get("label", iid)}, None))
        keep = {ob for ob, _i, _l in made}
        for o in list(bpy.data.objects):
            if o not in keep:
                bpy.data.objects.remove(o)  # the library holds only the assets; the open file isn't saved over
    x = 0.0
    previews = 0
    for ob, it, lod in made:
        bpy.context.view_layer.update()
        xs = [(ob.matrix_world @ Vector(c)).x for c in ob.bound_box]
        ob.location.x += x - min(xs)  # a row, so the file is browsable too
        x += (max(xs) - min(xs)) + 0.1
        ob.asset_mark()
        ad = ob.asset_data
        ad.description = it.get("label", it["id"])
        ad.catalog_id = cat_id
        for t in list(it.get("tags", [])) + list(tags or []) + [name]:
            ad.tags.new(t, skip_if_exists=True)
        ob["dims_m"] = ax.r(list(ob.dimensions), 4)
        ob["tris"] = _tris(ob)
        ob["label"] = it.get("label", it["id"])
        if lod is not None:
            lod.location = ob.location
            ob["lod1"] = lod.name
        try:  # previews need a GPU context in some builds; the asset works without one
            ob.asset_generate_preview()
            previews += 1
        except Exception as e:
            print("asset_library: no preview for", ob.name, e)
    lods.hide_render = lods.hide_viewport = True
    try:
        bpy.ops.file.pack_all()
    except RuntimeError as e:
        print("asset_library: pack_all:", e)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=out, compress=True, copy=not entries)
    cats = os.path.join(os.path.dirname(out), "blender_assets.cats.txt")
    with open(cats, "w", encoding="utf-8") as f:
        f.write(f"VERSION 1\n\n{cat_id}:{name}:{name}\n")
    return {"library": out, "catalog": cats, "assets": [ob.name for ob, _i, _l in made],
            "lods": [lod.name for _o, _i, lod in made if lod], "previews": previews}


if __name__ == "__main__":
    p = ax.parser(__doc__)
    p.add_argument("--kit")
    p.add_argument("--glbs", nargs="*")
    p.add_argument("--folder")
    p.add_argument("--objects", nargs="*")
    p.add_argument("--collection")
    p.add_argument("--out", default="//library/kit.blend")
    p.add_argument("--name")
    p.add_argument("--tags", nargs="*")
    ax.cli(run, p)
