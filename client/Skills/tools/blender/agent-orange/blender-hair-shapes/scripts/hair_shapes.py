"""Hair from shapes: block a hairstyle out as hair locks (curves with a radius per point, or meshes
with a creased root edge), check the blockout, then turn it into strand hair with Geometry Nodes.

Blockout (all in one collection, HairShapes by default):
  lock(name, points, radii)          a lock: a curve from root to tip, thick where the radius is
  braid(name, points, width, ...)    three locks woven along a path, plus an optional loose tail
  on_surface(points, surface, lift)  pulls points onto a surface (a scalp) and lifts them off it
  tie(name, center, axis, radius)    an elastic: a small torus
  strip(name, ...)                   a mesh strip with its root edge creased, for mesh-made hair
  stack(order, surface)              layers the sections, each lifted clear of those under it
  preview(collection, on)            shows the locks as tubes for blockout renders; off before hair()
Hair:
  hair(name, collection, ...)        the strands: guides from every lock, then children, clumps,
                                     tapered tips, frizz and noise, each a live modifier
  strays(name, collection, ...)      a sparse second layer of loose hairs that leave the flow
  baby_hair(name, surface, hairline_points, ...)   short fine hairs along a hairline
  hair_material(name, stops)         Principled Hair, one colour or a root-to-tip gradient
Match the references (hair_match.py):
  ref_camera(ref, head, az)          a camera fitted to a reference picture of this head, from the bare head
  measure(cam)                       the reference's hair in metres: edges, bang line, hairline, lowest points
  sections(cam, groups, out)         each section in its own colour beside the reference: which lies over which
  compare(cam, hair, out, render)    measures the hair or blockout against that reference, in millimetres
Check:
  report(hair_objects, surface)      guide and strand counts, root gaps, roots-first, length range
Hand it over:
  bake_guides(hair, head)            guides as real curves to comb; children, clumps and frizz stay live
  bake(hair, head)                   plain brushable strands; the source is kept, hidden
  unbake(baked)                      back to the source, to edit and bake again
  attach(curves, head) / follow(curves)   root UVs on the head; follow it as it deforms (cut-scenes)
  game_budget(objects, strands)      fewer, wider strands for a game (50,000: MetaHuman LOD0), still live
  export_groom(objects, path)        an Alembic groom for Unreal: points thinned, a group per material, in cm
  (this folder is also the "Hair from Shapes" Blender extension: the same, as sidebar buttons)
Render:
  gpu(scene)                         Cycles on one GPU backend (OptiX first), so heavy hair fits in memory

Usage (Blender 5.3):  blender -b f.blend -P hair_shapes.py -- --selftest out_dir
Or import it in your own script: sys.path.insert(0, <this folder>); import hair_shapes as hs
"""
import math
import os
import random
import sys

import bpy
import numpy as np
from mathutils import Quaternion, Vector
from mathutils import noise as mnoise
from mathutils.bvhtree import BVHTree

try:  # inside the add-on (a package)
    from .hair_match import compare, measure, ref_camera, sections  # noqa: F401  (hs.ref_camera, hs.measure, ...)
except ImportError:  # imported or run as a plain script
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from hair_match import compare, measure, ref_camera, sections  # noqa: E402,F401

SHAPES = "HairShapes"
CORE = "AO Shapes to Hair"
ASSETS = ("Duplicate Hair Curves", "Clump Hair Curves", "Trim Hair Curves", "Frizz Hair Curves", "Hair Curves Noise",
          "Curl Hair Curves", "Set Hair Curve Profile")


# ---------------------------------------------------------------- blockout

def _collection(name):
    coll = bpy.data.collections.get(name)
    if coll is None:
        coll = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(coll)
    return coll


def _smooth(points, count):
    """A Catmull-Rom spline through the points, resampled to count points evenly by length."""
    pts = [Vector(p) for p in points]
    if len(pts) < 2:
        raise ValueError("a lock needs at least two points")
    dense = []
    ext = [pts[0] * 2 - pts[1]] + pts + [pts[-1] * 2 - pts[-2]]
    for i in range(1, len(ext) - 2):
        p0, p1, p2, p3 = ext[i - 1], ext[i], ext[i + 1], ext[i + 2]
        for s in range(16):
            t = s / 16
            dense.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t ** 3))
    dense.append(pts[-1])
    cum = [0.0]
    for a, b in zip(dense, dense[1:]):
        cum.append(cum[-1] + (b - a).length)
    out, j = [], 0
    for k in range(count):
        target = cum[-1] * k / (count - 1)
        while j < len(cum) - 2 and cum[j + 1] < target:
            j += 1
        seg = cum[j + 1] - cum[j] or 1.0
        out.append(dense[j].lerp(dense[j + 1], (target - cum[j]) / seg))
    return out


def _radii(radii, count):
    """Radius per point: one number, (root, tip), (root, mid, tip) or a list, spread over count points."""
    if isinstance(radii, (int, float)):
        return [float(radii)] * count
    r = list(radii)
    if len(r) == count:
        return r
    out = []
    for k in range(count):
        x = k / (count - 1) * (len(r) - 1)
        i = min(int(x), len(r) - 2)
        f = x - i
        out.append(r[i] * (1 - f) + r[i + 1] * f)
    return out


def lock(name, points, radii=(0.006, 0.004, 0.0008), resolution=24, collection=SHAPES):
    """One lock of hair: points from the root (on or just inside the scalp) to the tip, smoothed and
    resampled to `resolution` points. radii sets its thickness: one value, (root, tip), (root, mid,
    tip) or one per point. Its strands fill this tube. Returns the curve object."""
    pts = _smooth(points, resolution)
    rad = _radii(radii, resolution)
    cu = bpy.data.curves.new(name, "CURVE")
    cu.dimensions = "3D"
    sp = cu.splines.new("POLY")
    sp.points.add(len(pts) - 1)
    for p, co, r in zip(sp.points, pts, rad):
        p.co = (co.x, co.y, co.z, 1.0)
        p.radius = r
    ob = bpy.data.objects.new(name, cu)
    _collection(collection).objects.link(ob)
    return ob


def _frame(path, i, up):
    n = len(path)
    tan = (path[min(i + 1, n - 1)] - path[max(i - 1, 0)]).normalized()
    side = tan.cross(Vector(up))
    if side.length < 1e-6:
        side = tan.cross(Vector((1, 0, 0)))
    side.normalize()
    return tan, side, side.cross(tan).normalized()


def braid(name, points, width=0.012, turns=4.0, strand_radius=0.32, tail=None, tail_flare=1.6, tail_stagger=0.12,
          tail_locks=0, resolution=96, collection=SHAPES, up=(0, 0, 1), seed=0):
    """A three-strand braid along `points` (start at the tie or root, end at the bottom elastic).
    - width: across the weave. One number, or (top, bottom) for a braid that narrows towards the
      end as real ones do. The plaits shrink with it: the weave runs faster where it's narrower.
    - turns: full weave cycles. strand_radius: each strand's radius as a share of the width there.
    - up: the braid's flat face looks along up x path, so point up across the plait's face.
    - tail: points for the loose tail below the bottom elastic, starting at the braid's end. Each
      strand carries on into it, as in a real braid: gathered at the elastic, fanning to
      tail_flare x the bottom width, then closing to a tapered brush. tail_stagger: each strand's
      share ends at its own length (0..1 of the tail), so the tip isn't one cut. tail_locks: extra
      thin locks in the tail, each its own length and lean, for an uneven, natural tip.
    Returns the strand objects (the three strands, then any tail locks)."""
    rnd = random.Random(seed)
    w0, w1 = (width, width) if isinstance(width, (int, float)) else (float(width[0]), float(width[-1]))
    wid = lambda t: w0 + (w1 - w0) * t ** 0.85
    path = _smooth(points, resolution)
    tpath = _smooth(tail, 40) if tail else []
    # weave phase by 1/width, so a narrowing braid gets shorter plaits towards the bottom
    inv = [1.0 / wid(i / (resolution - 1)) for i in range(resolution)]
    cum = [0.0]
    for a, b in zip(inv, inv[1:]):
        cum.append(cum[-1] + (a + b) / 2)
    obs = []
    for k in range(3):
        pts, rad = [], []
        for i, c in enumerate(path):
            t = i / (resolution - 1)
            w = wid(t)
            _tan, side, depth = _frame(path, i, up)
            th = 2 * math.pi * turns * cum[i] / cum[-1] + 2 * math.pi * k / 3
            # the classic plait: side to side once per cycle, front to back twice, so strands pass over and under
            off = side * (0.5 * w * math.sin(th)) + depth * (0.22 * w * math.sin(2 * th))
            ease = min(1.0, t / 0.04, ((1 - t) / 0.05 if tail else 1.0))  # gathered at the tie and at the elastic
            pts.append(c + off * ease)
            rad.append(strand_radius * w * (0.75 + 0.25 * ease))
        if tpath:
            sr = strand_radius * w1
            stop = 1.0 - tail_stagger * k * rnd.uniform(0.8, 1.0)
            a = 2 * math.pi * k / 3 + 0.6
            for j in range(1, len(tpath)):  # the tail: each strand a third of it, flaring, then closing to a point
                u = j / (len(tpath) - 1)
                if u > stop:
                    break
                v = u / stop
                _tan, side, depth = _frame(tpath, j, up)
                spread = w1 * 0.5 * tail_flare * math.sin(math.pi * min(1.0, v * 2.2) * 0.5) * (1 - 0.7 * v * v)
                pts.append(tpath[j] + (side * math.cos(a) + depth * math.sin(a)) * spread * 0.6)
                rad.append((sr * 0.75 + sr * 0.9 * math.sin(math.pi * min(v * 2.4, 1.0) * 0.5)) * (1 - v) ** 0.8 + 0.0006)
        obs.append(lock(f"{name}_strand{k}", pts, rad, resolution=len(pts), collection=collection))
    for m in range(tail_locks if tpath else 0):  # thin extra locks from inside the elastic
        ln = rnd.uniform(0.5, 1.05)
        a = 2 * math.pi * (m + rnd.uniform(-0.3, 0.3)) / tail_locks
        lean = w1 * rnd.uniform(0.3, 0.9)
        pts = []
        for j in range(0, len(tpath), 4):
            u = j / (len(tpath) - 1)
            if u > ln:
                break
            _tan, side, depth = _frame(tpath, j, up)
            pts.append(tpath[j] + (side * math.cos(a) + depth * math.sin(a)) * lean * math.sin(math.pi * min(1.0, u * 1.5) * 0.5))
        if len(pts) < 2:
            continue
        obs.append(lock(f"{name}_tail{m}", pts, (w1 * 0.17, w1 * 0.3 * rnd.uniform(0.7, 1.1), 0.0005), resolution=24,
                        collection=collection))
    return obs


def tie(name, center, axis, radius=0.007, thickness=0.0016, material=None):
    """An elastic or hair tie: a torus at center, its hole along axis (the hair's direction there)."""
    bpy.ops.mesh.primitive_torus_add(major_radius=radius, minor_radius=thickness, major_segments=32, minor_segments=8,
                                     location=center)
    ob = bpy.context.active_object
    ob.name = name
    ob.rotation_mode = "QUATERNION"
    ob.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(Vector(axis).normalized())
    if material:
        ob.data.materials.append(material)
    return ob


def on_surface(points, surface, lift=0.002):
    """Each point moved to the nearest place on surface, then lifted `lift` m along its normal: for
    the part of a lock that lies on the scalp. Lift at least the lock's radius there, or its tube
    (and so its strands) sinks into the head. The root point is best kept 1-2 mm inside instead."""
    ob = bpy.data.objects[surface] if isinstance(surface, str) else surface
    dg = bpy.context.evaluated_depsgraph_get()
    bvh = BVHTree.FromObject(ob, dg)
    mw, mi = ob.matrix_world, ob.matrix_world.inverted()
    out = []
    for p in points:
        loc, nor, _i, _d = bvh.find_nearest(mi @ Vector(p))
        n = (mw.to_3x3() @ nor).normalized()
        out.append(mw @ loc + n * lift)
    return out


def strip(name, root_points, tip_points, rows=20, collection=SHAPES):
    """A hair strip as a mesh: a grid from a row of root points to a row of tip points, `rows` loops
    along its length, its root edge creased (crease_edge = 1) so hair() grows from there. Model or
    sculpt it further as you like; keep the loops running across the hair and the root crease."""
    import bmesh
    a, b = [Vector(p) for p in root_points], [Vector(p) for p in tip_points]
    if len(a) != len(b):
        raise ValueError("root_points and tip_points need the same count")
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    grid = [[bm.verts.new(a[j].lerp(b[j], i / rows)) for j in range(len(a))] for i in range(rows + 1)]
    for i in range(rows):
        for j in range(len(a) - 1):
            bm.faces.new((grid[i][j], grid[i][j + 1], grid[i + 1][j + 1], grid[i + 1][j]))
    bm.to_mesh(me)
    bm.free()
    crease = me.attributes.new("crease_edge", "FLOAT", "EDGE")
    root = set(range(len(a)))
    for e in me.edges:
        if e.vertices[0] in root and e.vertices[1] in root:
            crease.data[e.index].value = 1.0
    ob = bpy.data.objects.new(name, me)
    _collection(collection).objects.link(ob)
    return ob


def _section(item):
    if isinstance(item, str):
        return list(bpy.data.collections[item].all_objects) if item in bpy.data.collections else [bpy.data.objects[item]]
    if isinstance(item, bpy.types.Collection):
        return list(item.all_objects)
    return list(item) if isinstance(item, (list, tuple)) else [item]


def stack(order, surface, gap=0.001, ramp=0.015, reach=0.003, cap=0.008, spread=0.02):
    """Layers the sections like shingles, so no two sections' strands cross through each other.
    order: the sections (collections, or lists of locks) from the one nearest the scalp to the top
    one, e.g. ["Nape", "Around ears", "Sides", "Crown", "Bangs"]. Wherever a lock of a later section
    passes into an earlier one, the later section is lifted along the scalp's normal until its
    underside clears the earlier one's top by `gap`. The lift is one smooth blanket over the section,
    falling off over `spread` m, so neighbouring locks rise together, and it comes in over `ramp` m
    from each root, so roots stay put. Locks in the same section aren't moved against each other:
    they're meant to overlap. `reach`: how far to the side (beyond both radii) a lock below counts.
    Crossings deeper than `cap` aren't lifted, as that would puff the hair out: they're printed with
    where they are, to fix in the blockout (usually two sections rooted on the same patch of scalp).
    Curves (lock, braid) and strips both work. Run it after the blockout, before preview and hair().
    Returns, per section, the points lifted and the largest lift in metres."""
    from mathutils.kdtree import KDTree
    surf = bpy.data.objects[surface] if isinstance(surface, str) else surface
    dg = bpy.context.evaluated_depsgraph_get()
    ev = surf.evaluated_get(dg)
    me = ev.to_mesh()
    mw = surf.matrix_world
    bvh = BVHTree.FromPolygons([mw @ v.co for v in me.vertices], [p.vertices[:] for p in me.polygons])
    ev.to_mesh_clear()

    def height(p):  # (foot on the scalp, its normal, signed height above it)
        loc, nor, _i, _d = bvh.find_nearest(p)
        return loc, nor, (p - loc).dot(nor)

    def strands(ob):
        """Each lock as (points world, radii, root weights, writer)."""
        m = ob.matrix_world
        if ob.type == "CURVE":
            out = []
            for sp in ob.data.splines:
                pts = [m @ Vector(p.co[:3]) for p in sp.points] if sp.type != "BEZIER" else [m @ p.co for p in sp.bezier_points]
                rad = [p.radius for p in (sp.points if sp.type != "BEZIER" else sp.bezier_points)]
                s, w = 0.0, [0.0]
                for a, b in zip(pts, pts[1:]):
                    s += (b - a).length
                    w.append(min(1.0, s / ramp))

                def write(new, sp=sp, mi=m.inverted()):
                    for p, q in zip(sp.points if sp.type != "BEZIER" else sp.bezier_points, new):
                        if sp.type == "BEZIER":
                            d = mi @ q - p.co
                            p.co += d
                            p.handle_left += d
                            p.handle_right += d
                        else:
                            p.co = (*(mi @ q), 1.0)
                out.append((pts, rad, w, write))
            return out
        if ob.type == "MESH":
            pts = [m @ v.co for v in ob.data.vertices]
            roots = set()
            cr = ob.data.attributes.get("crease_edge")
            if cr:
                for e in ob.data.edges:
                    if cr.data[e.index].value > 0.5:
                        roots.update(e.vertices)
            root_pts = [pts[i] for i in roots] or [pts[0]]
            w = [min(1.0, min((p - r).length for r in root_pts) / ramp) for p in pts]

            def write(new, ob=ob, mi=m.inverted()):
                for v, q in zip(ob.data.vertices, new):
                    v.co = mi @ q
                ob.data.update()
            return [(pts, [0.0] * len(pts), w, write)]
        return []

    feet, shell, below, result = [], [], None, {}
    for item in order:
        obs = _section(item)
        locks = [s for ob in obs for s in strands(ob)]
        moved, most, clash = 0, 0.0, []
        if below is not None:
            # what each point needs, then one smooth blanket over the whole section (the largest need
            # nearby, falling off over `spread`), so neighbouring locks rise together: no lumps
            need_at, needs = [], []
            for li, (pts, rad, w, _write) in enumerate(locks):
                for k, (p, r) in enumerate(zip(pts, rad)):
                    foot, _nor, h = height(p)
                    need = 0.0
                    for _co, i, d in below.find_range(foot, r + reach + 0.01):
                        top, rr = shell[i]
                        if d < r + rr + reach:
                            need = max(need, top + gap + r - h)
                    if need > cap:  # not a small crossing: a blockout to fix, not a lift to make
                        clash.append((need, p))
                    elif need > 0:
                        need_at.append(foot)
                        needs.append(need)
            if needs:
                kd = KDTree(len(need_at))
                for i, f in enumerate(need_at):
                    kd.insert(f, i)
                kd.balance()
                for pts, rad, w, write in locks:
                    lift = []
                    for p, wt in zip(pts, w):
                        foot = height(p)[0]
                        env = max((needs[i] * math.exp(-(d / spread) ** 2) for _c, i, d in kd.find_range(foot, spread * 2)),
                                  default=0.0)
                        lift.append(env * wt)
                    if not any(v > 1e-5 for v in lift):
                        continue
                    new = [p + height(p)[1] * v for p, v in zip(pts, lift)]
                    moved += sum(v > 1e-5 for v in lift)
                    most = max(most, max(lift))
                    write(new)
                    pts[:] = new
        name = item if isinstance(item, str) else getattr(item, "name", str(item))
        result[name] = (moved, round(most, 4))
        clash.sort(key=lambda c: -c[0])
        if clash:
            print(f"STACK {name}: {len(clash)} points go through a section under it by more than {cap * 1000:.0f} mm. The deepest:")
        for need, p in clash[:5]:
            print(f"STACK {name} goes {need * 1000:.0f} mm through a section under it at ({p.x:.3f}, {p.y:.3f}, {p.z:.3f}): "
                  f"too much to lift; move it in the blockout")
        # this section joins the layers below: its tops, every ~2 mm along each lock
        for pts, rad, _w, _write in locks:
            for (a, ra), (b, rb) in zip(zip(pts, rad), zip(pts[1:], rad[1:])):
                steps = max(1, int((b - a).length / 0.002))
                for k in range(steps):
                    p, r = a.lerp(b, k / steps), ra + (rb - ra) * k / steps
                    foot, _n, h = height(p)
                    feet.append(foot)
                    shell.append((h + r, r))
        if feet:
            below = KDTree(len(feet))
            for i, foot in enumerate(feet):
                below.insert(foot, i)
            below.balance()
        print(f"STACK {name}: {result[name][0]} points lifted, at most {result[name][1] * 1000:.1f} mm")
    return result


def preview(collection=SHAPES, on=True, material=None):
    """Show curve locks as tubes of their radius (for blockout renders), or turn that off. Always off
    before hair(): the conversion reads the bare curves."""
    for ob in bpy.data.collections[collection].all_objects:
        if ob.type == "CURVE":
            ob.data.bevel_depth = 1.0 if on else 0.0  # the point radius scales the bevel
            ob.data.bevel_resolution = 4
            ob.data.use_fill_caps = on
            if material and on and not ob.data.materials:
                ob.data.materials.append(material)
            if not on:
                ob.data.materials.clear()  # it would ride into the hair ahead of the hair's own material


# ---------------------------------------------------------------- the conversion node group

def _in(node, name, i=0):
    """A node's input by name, the i-th enabled one (nodes like Compare hide sockets per type)."""
    found = [s for s in node.inputs if s.name == name and s.enabled]
    return found[i]


def _out(node, name, i=0):
    found = [s for s in node.outputs if s.name == name and s.enabled]
    return found[i]


def core_group():
    """The node group that turns a collection of locks into guide hairs, built from scratch:
    curves -> resample -> mark the start ring -> tube mesh (Curve Circle profile, Scale = radius) ->
    crease_edge on the start ring; meshes come in with their own crease_edge. Then on the joined mesh:
    hops from the creased root (Shortest Edge Paths), drop edges whose ends are equally far (the ones
    running across the hair), Mesh to Curve, and reverse any strand that doesn't start at its root.
    The lock centre lines join the result, so a lock is filled, not hollow."""
    if CORE in bpy.data.node_groups:
        return bpy.data.node_groups[CORE]
    ng = bpy.data.node_groups.new(CORE, "GeometryNodeTree")
    io = ng.interface
    io.new_socket("Geometry", in_out="INPUT", socket_type="NodeSocketGeometry")
    s = io.new_socket("Shapes", in_out="INPUT", socket_type="NodeSocketCollection")
    s = io.new_socket("Guides per Lock", in_out="INPUT", socket_type="NodeSocketInt")
    s.default_value, s.min_value = 8, 3
    s = io.new_socket("Points per Guide", in_out="INPUT", socket_type="NodeSocketInt")
    s.default_value, s.min_value = 24, 4
    s = io.new_socket("Fill", in_out="INPUT", socket_type="NodeSocketFloat")
    s.default_value, s.min_value, s.max_value = 0.55, 0.0, 1.0
    io.new_socket("Material", in_out="INPUT", socket_type="NodeSocketMaterial")
    io.new_socket("Geometry", in_out="OUTPUT", socket_type="NodeSocketGeometry")
    N, L = ng.nodes.new, ng.links.new
    gin, gout = N("NodeGroupInput"), N("NodeGroupOutput")

    coll = N("GeometryNodeCollectionInfo")
    coll.transform_space = "RELATIVE"
    _in(coll, "Separate Children").default_value = False
    L(_out(gin, "Shapes"), _in(coll, "Collection"))
    real = N("GeometryNodeRealizeInstances")
    L(coll.outputs[0], _in(real, "Geometry"))
    sep = N("GeometryNodeSeparateComponents")
    L(real.outputs[0], sep.inputs[0])

    # curves: even points, a start flag, then a tube around each
    res = N("GeometryNodeResampleCurve")
    _in(res, "Mode").default_value = "Count"
    L(_out(sep, "Curve"), _in(res, "Curve"))
    L(_out(gin, "Points per Guide"), _in(res, "Count"))
    start = N("GeometryNodeCurveEndpointSelection")
    _in(start, "Start Size").default_value, _in(start, "End Size").default_value = 1, 0
    cap = N("GeometryNodeCaptureAttribute")
    cap.domain = "POINT"
    cap.capture_items.new("BOOLEAN", "Root")
    L(res.outputs[0], cap.inputs[0])
    L(start.outputs[0], _in(cap, "Root"))
    # the profile: an outer ring of Guides per Lock, and two inner rings (Fill, Fill x 0.4 of the radius)
    # with fewer guides, so each lock is a filled bundle rather than a hollow sleeve
    profile = N("GeometryNodeJoinGeometry")
    half = N("ShaderNodeMath")
    half.operation = "DIVIDE"
    L(_out(gin, "Guides per Lock"), half.inputs[0])
    half.inputs[1].default_value = 2.0
    inner2 = N("ShaderNodeMath")
    inner2.operation = "MULTIPLY"
    L(_out(gin, "Fill"), inner2.inputs[0])
    inner2.inputs[1].default_value = 0.4
    for res_src, scale_src, turn in ((_out(gin, "Guides per Lock"), None, 0.0), (half.outputs[0], _out(gin, "Fill"), 0.4),
                                     (None, inner2.outputs[0], 1.1)):
        circ = N("GeometryNodeCurvePrimitiveCircle")
        circ.mode = "RADIUS"
        _in(circ, "Radius").default_value = 1.0
        if res_src is not None:
            L(res_src, _in(circ, "Resolution"))
        else:
            _in(circ, "Resolution").default_value = 3
        if scale_src is None:
            L(_out(circ, "Curve"), profile.inputs[0])
            continue
        xf = N("GeometryNodeTransform")
        L(_out(circ, "Curve"), _in(xf, "Geometry"))
        L(scale_src, _in(xf, "Scale"))
        _in(xf, "Rotation").default_value = (0.0, 0.0, turn)  # inner rings offset so guides don't line up
        L(xf.outputs[0], profile.inputs[0])
    rad = N("GeometryNodeInputRadius")
    tube = N("GeometryNodeCurveToMesh")
    L(cap.outputs[0], _in(tube, "Curve"))
    L(profile.outputs[0], _in(tube, "Profile Curve"))
    L(rad.outputs[0], _in(tube, "Scale"))
    crease = N("GeometryNodeStoreNamedAttribute")
    crease.data_type, crease.domain = "FLOAT", "EDGE"
    _in(crease, "Name").default_value = "crease_edge"
    L(tube.outputs[0], _in(crease, "Geometry"))
    L(_out(cap, "Root"), _in(crease, "Value"))  # read on edges: true only where both ends are on the start ring

    join_mesh = N("GeometryNodeJoinGeometry")
    L(crease.outputs[0], join_mesh.inputs[0])
    L(_out(sep, "Mesh"), join_mesh.inputs[0])

    # mesh -> strands: hops from the creased root, keep the edges that run along the hair
    ca = N("GeometryNodeInputNamedAttribute")
    ca.data_type = "FLOAT"
    _in(ca, "Name").default_value = "crease_edge"
    is_root = N("FunctionNodeCompare")
    is_root.data_type, is_root.operation = "FLOAT", "GREATER_THAN"
    L(_out(ca, "Attribute"), _in(is_root, "A"))
    _in(is_root, "B").default_value = 0.01  # read on points: >0 on any vertex of a creased edge
    paths = N("GeometryNodeInputShortestEdgePaths")
    L(is_root.outputs[0], _in(paths, "End Vertex"))
    hops = N("GeometryNodeCaptureAttribute")
    hops.domain = "POINT"
    hops.capture_items.new("FLOAT", "Cost")
    L(join_mesh.outputs[0], hops.inputs[0])
    L(_out(paths, "Total Cost"), _in(hops, "Cost"))
    ev = N("GeometryNodeInputMeshEdgeVertices")
    at1, at2 = N("GeometryNodeFieldAtIndex"), N("GeometryNodeFieldAtIndex")
    for at, idx in ((at1, "Vertex Index 1"), (at2, "Vertex Index 2")):
        at.data_type, at.domain = "FLOAT", "POINT"
        L(_out(hops, "Cost"), _in(at, "Value"))
        L(_out(ev, idx), _in(at, "Index"))
    across = N("FunctionNodeCompare")
    across.data_type, across.operation = "FLOAT", "EQUAL"
    L(at1.outputs[0], _in(across, "A"))
    L(at2.outputs[0], _in(across, "B"))
    _in(across, "Epsilon").default_value = 0.001
    dele = N("GeometryNodeDeleteGeometry")
    dele.domain, dele.mode = "EDGE", "ALL"
    L(hops.outputs[0], _in(dele, "Geometry"))
    L(across.outputs[0], _in(dele, "Selection"))
    m2c = N("GeometryNodeMeshToCurve")
    L(dele.outputs[0], _in(m2c, "Mesh"))

    # roots first: reverse a strand when its first point is farther from the root than its last
    first = N("GeometryNodePointsOfCurve")
    _in(first, "Sort Index").default_value = 0
    last_idx = N("ShaderNodeMath")
    last_idx.operation = "ADD"
    L(_out(first, "Point Index"), last_idx.inputs[0])
    tot = N("ShaderNodeMath")
    tot.operation = "SUBTRACT"
    L(_out(first, "Total"), tot.inputs[0])
    tot.inputs[1].default_value = 1.0
    L(tot.outputs[0], last_idx.inputs[1])
    c_first, c_last = N("GeometryNodeFieldAtIndex"), N("GeometryNodeFieldAtIndex")
    for at, idx in ((c_first, _out(first, "Point Index")), (c_last, last_idx.outputs[0])):
        at.data_type, at.domain = "FLOAT", "POINT"
        L(_out(hops, "Cost"), _in(at, "Value"))
        L(idx, _in(at, "Index"))
    backwards = N("FunctionNodeCompare")
    backwards.data_type, backwards.operation = "FLOAT", "GREATER_THAN"
    L(c_first.outputs[0], _in(backwards, "A"))
    L(c_last.outputs[0], _in(backwards, "B"))
    rev = N("GeometryNodeReverseCurve")
    L(m2c.outputs[0], _in(rev, "Curve"))
    L(backwards.outputs[0], _in(rev, "Selection"))
    even = N("GeometryNodeResampleCurve")
    _in(even, "Mode").default_value = "Count"
    L(rev.outputs[0], _in(even, "Curve"))
    L(_out(gin, "Points per Guide"), _in(even, "Count"))

    # plus each lock's centre line as a guide
    centre = N("GeometryNodeResampleCurve")
    _in(centre, "Mode").default_value = "Count"
    L(res.outputs[0], _in(centre, "Curve"))
    L(_out(gin, "Points per Guide"), _in(centre, "Count"))
    join = N("GeometryNodeJoinGeometry")
    L(even.outputs[0], join.inputs[0])
    L(centre.outputs[0], join.inputs[0])
    thin = N("GeometryNodeSetCurveRadius")
    _in(thin, "Radius").default_value = 0.00005
    L(join.outputs[0], _in(thin, "Curve"))
    clean = N("GeometryNodeRemoveAttribute")  # the shapes' own materials would otherwise come along
    _in(clean, "Name").default_value = "material_index"
    L(thin.outputs[0], _in(clean, "Geometry"))
    setmat = N("GeometryNodeSetMaterial")
    L(clean.outputs[0], _in(setmat, "Geometry"))
    L(_out(gin, "Material"), _in(setmat, "Material"))
    L(setmat.outputs[0], gout.inputs[0])
    return ng


# ---------------------------------------------------------------- hair objects and detail

def _assets():
    path = os.path.join(bpy.utils.system_resource("DATAFILES"), "assets", "nodes", "procedural_hair_node_assets.blend")
    need = [n for n in ASSETS if n not in bpy.data.node_groups]
    if need:
        with bpy.data.libraries.load(path, link=False) as (src, dst):
            dst.node_groups = need
    return {n: bpy.data.node_groups[n] for n in ASSETS}


def _modifier(ob, group, vals, name=None):
    md = ob.modifiers.new(name or group.name, "NODES")
    md.node_group = group
    ids = {}
    for i in group.interface.items_tree:
        if getattr(i, "in_out", "") == "INPUT":
            ids.setdefault(i.name, i.identifier)
            ids[f"{i.name}:{i.socket_type[len('NodeSocket'):]}"] = i.identifier
    for k, v in vals.items():
        sock = getattr(md.properties.inputs, ids[k])
        if isinstance(v, tuple) and v and v[0] == "ATTR":
            sock.type, sock.attribute_name = "ATTRIBUTE", v[1]
        else:
            sock.value = v
    return md


def _curves_object(name, parent=None):
    cv = bpy.data.hair_curves.new(name)
    ob = bpy.data.objects.new(name, cv)
    bpy.context.scene.collection.objects.link(ob)
    if parent is not None:
        ob.parent = parent if not isinstance(parent, str) else bpy.data.objects[parent]
        ob.matrix_parent_inverse = ob.parent.matrix_world.inverted()
    return ob


def hair(name, collection=SHAPES, guides=10, points=24, fill=0.55, children=60, spread=0.0025, clump=0.55, clump_shape=0.3,
         tip_spread=0.0, trim=0.02, frizz=0.0006, noise=0.0012, noise_scale=40.0, curl=0.0, strand_radius=0.00006,
         material=None, parent=None, viewport=0.1, seed=0, cumulative=False):
    """Strand hair from every lock in `collection`. Guides: `guides` per lock round its edge, two inner
    rings (at `fill` and fill x 0.4 of its radius) and the centre line, each `points` long. Then live
    modifiers, top to bottom:
      Duplicate (children per guide, spread m around it; `viewport`: the share of them shown in the
      viewport, 0.1 by default, as a full head in Material Preview can crash Blender; renders get all) -> Clump (factor, shape,
      tip spread) -> Trim (each strand shortened by a random 0..`trim` metres: tapered clump tips, no
      blunt ends; keep it under the shortest lock's length) ->
      Frizz (m) -> Noise (m, scale) -> Curl (m radius, 0 = none) -> Profile (strand radius, thin tip).
    Frizz and noise are plain offsets in metres (`cumulative` False), the same on a short bang and a
    long braid; True gives the bundled groups' default, where offsets add up along the strand.
    Tune them on the modifiers afterwards. Returns the hair object."""
    g = _assets()
    ob = _curves_object(name, parent)
    vals = {"Guides per Lock": guides, "Points per Guide": points, "Fill": fill}
    if material:
        vals["Material"] = material
    md = _modifier(ob, core_group(), vals, name="Shapes to Hair")
    ident = next(i.identifier for i in core_group().interface.items_tree if getattr(i, "name", "") == "Shapes")
    getattr(md.properties.inputs, ident).value = bpy.data.collections[collection]
    if children > 0:
        _modifier(ob, g["Duplicate Hair Curves"], {"Amount": children, "Viewport Amount": viewport, "Radius": spread,
                                                   "Even Thickness": True, "Seed": seed})
    if clump > 0:
        _modifier(ob, g["Clump Hair Curves"], {"Factor": clump, "Shape": clump_shape, "Tip Spread": tip_spread,
                                               "Guide Distance": spread * 2.5, "Seed": seed})
    if trim > 0:  # Replace Length defaults on, which sets every strand to `Length` (1 m); Random Offset is in metres
        _modifier(ob, g["Trim Hair Curves"], {"Replace Length": False, "Scale Uniform": False, "Length Factor": 1.0,
                                              "Random Offset": trim, "Pin at Parameter": 0.0, "Seed": seed})
    # Cumulative Offset (on by default in the bundled groups) adds each point's offset onto the last
    # one's, so a long strand (a 170-point braid) drifts centimetres off its lock; off, the distances
    # are real offsets in metres whatever the strand's length or point count
    if frizz > 0:
        _modifier(ob, g["Frizz Hair Curves"], {"Factor": 1.0, "Distance": frizz, "Shape": 0.6, "Cumulative Offset": cumulative,
                                               "Seed": seed})
    if noise > 0:
        _modifier(ob, g["Hair Curves Noise"], {"Factor": 1.0, "Distance": noise, "Scale": noise_scale,
                                               "Cumulative Offset": cumulative, "Seed": seed})
    if curl > 0:
        _modifier(ob, g["Curl Hair Curves"], {"Factor": 1.0, "Radius": curl, "Frequency": 4.0, "Guide Distance": spread * 2.5,
                                              "Seed": seed})
    _modifier(ob, g["Set Hair Curve Profile"], {"Radius": strand_radius, "Shape": 0.5})
    if material:
        ob.data.materials.append(material)
    return ob


def strays(name, collection=SHAPES, amount=2, spread=0.006, frizz=0.004, noise=0.006, curl=0.004, trim=0.06, guides=6,
           points=24, strand_radius=0.000035, material=None, parent=None, seed=7):
    """Loose hairs over the whole style: a sparse copy of the guides, pushed off the flow by strong
    frizz, noise and curl and cut to random lengths, so some strands leave their lock and end early."""
    return hair(name, collection, guides=guides, points=points, fill=0.9, children=amount, spread=spread, clump=0.0, trim=trim,
                frizz=frizz, noise=noise, noise_scale=12.0, curl=curl, strand_radius=strand_radius, material=material,
                parent=parent, seed=seed)


def baby_hair(name, surface, hairline_points, band=0.008, length=0.012, density=300000.0, flow=(0.0, 0.0, -1.0),
              fan=35.0, rise=0.25, frizz=None, curl=0.0, strand_radius=0.00003, material=None, seed=3):
    """Short fine hairs along a hairline, lying along the skin, not standing out of it like stubble.
    Roots are scattered over `surface` near the hairline points (all within band/2 of the line, fewer
    out to band), `density` hairs per m². Each hair leaves the skin at a low angle, then lies down
    along it in the `flow` direction (down by default: down the forehead, the temples and the nape),
    each turned by up to `fan` degrees either way; `rise` is how high it arches, as a share of its
    length. Lengths vary from 60% to 120% of `length`.
    frizz and curl are capped at 10% of `length`: on a 1 cm hair, a couple of millimetres of either
    is a kink, and the hairline reads as bristles. frizz defaults to 6% of `length`.
    hairline_points: points along the forehead, temples, round the ears and the nape, where the
    scalp meets skin."""
    surf = bpy.data.objects[surface] if isinstance(surface, str) else surface
    mw = surf.matrix_world
    nm = mw.to_3x3().inverted().transposed()
    hl = [Vector(p) for p in hairline_points]
    dense = []
    for a, b in zip(hl, hl[1:]):  # treat the points as a polyline
        steps = max(1, int((b - a).length / (band * 0.25)))
        dense += [a.lerp(b, s / steps) for s in range(steps)]
    dense.append(hl[-1])
    from mathutils.kdtree import KDTree
    kd = KDTree(len(dense))
    for i, p in enumerate(dense):
        kd.insert(p, i)
    kd.balance()
    me = surf.data
    me.calc_loop_triangles()
    vco = [mw @ v.co for v in me.vertices]
    vno = [(nm @ v.normal).normalized() for v in me.vertices]
    rnd = random.Random(seed)
    down = Vector(flow).normalized()
    curves = []
    for tri in me.loop_triangles:
        a, b, c = (vco[i] for i in tri.vertices)
        cen = (a + b + c) / 3
        d = kd.find(cen)[2]
        if d > band:
            continue
        w = 1.0 if d < band * 0.5 else 1 - (d - band * 0.5) / (band * 0.5)
        want = density * w * ((b - a).cross(c - a)).length * 0.5
        count = int(want) + (rnd.random() < want - int(want))
        na, nb, nc = (vno[i] for i in tri.vertices)
        for _ in range(count):
            u, v = rnd.random(), rnd.random()
            if u + v > 1:
                u, v = 1 - u, 1 - v
            root = a + (b - a) * u + (c - a) * v
            n = (na * (1 - u - v) + nb * u + nc * v).normalized()
            t = down - n * down.dot(n)
            if t.length < 1e-4:  # the flow runs straight into the skin here: any tangent
                t = n.orthogonal()
            t = t.normalized()
            # neighbours turn together (a smooth field about 3 cm across), plus a little of their own,
            # so the hairs flow in soft swirls rather than criss-crossing
            turn = fan * max(-1.0, min(1.0, 1.6 * mnoise.noise(root * 30.0 + Vector((seed, 0, 0))))) + rnd.uniform(-8, 8)
            t = Quaternion(n, math.radians(turn)) @ t
            L = length * rnd.uniform(0.6, 1.2)
            h = rise * rnd.uniform(0.6, 1.2)
            # 6 points: out of the skin at a low angle, over a low arch, the tip back near the skin
            curves.append([root - n * 0.0003 + t * (L * s) + n * (L * h * math.sin(math.pi * min(1.0, s * 1.4)) * (1 - 0.6 * s))
                           for s in (0.0, 0.2, 0.4, 0.6, 0.8, 1.0)])
    ob = _curves_object(name, parent=surf)
    if curves:
        ob.data.add_curves([6] * len(curves))
        ob.data.attributes["position"].data.foreach_set("vector", [x for cv in curves for p in cv for x in p])
    g = _assets()
    cap = 0.1 * length
    frizz = 0.06 * length if frizz is None else min(frizz, cap)
    if frizz > 0:
        _modifier(ob, g["Frizz Hair Curves"], {"Factor": 1.0, "Distance": frizz, "Shape": 0.8, "Cumulative Offset": False, "Seed": seed})
    if curl > 0:  # each hair curls on its own: a shared guide winds every hair nearby round one point (starbursts)
        _modifier(ob, g["Curl Hair Curves"], {"Factor": 1.0, "Radius": min(curl, cap), "Frequency": 1.0, "Guide Distance": 0.0002,
                                              "Seed": seed})
    _modifier(ob, g["Set Hair Curve Profile"], {"Radius": strand_radius, "Shape": 0.5})
    if material:
        ob.data.materials.append(material)
    return ob


def hair_material(name, stops=((0.0, (0.012, 0.010, 0.010)),), roughness=0.3, radial_roughness=0.3, coat=0.0):
    """Principled Hair BSDF coloured directly. stops: (position 0 root .. 1 tip, linear RGB) pairs; one
    stop is a flat colour, more make a root-to-tip gradient along each strand (Hair Info Intercept).
    For a gradient that starts part way down a braid, put the change where it should be on that strand."""
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    h = nt.nodes.new("ShaderNodeBsdfHairPrincipled")
    h.model, h.parametrization = "CHIANG", "COLOR"
    h.inputs["Roughness"].default_value = roughness
    h.inputs["Radial Roughness"].default_value = radial_roughness
    h.inputs["Coat"].default_value = coat
    if len(stops) == 1:
        h.inputs["Color"].default_value = (*stops[0][1], 1.0)
    else:
        info = nt.nodes.new("ShaderNodeHairInfo")
        ramp = nt.nodes.new("ShaderNodeValToRGB")
        els = ramp.color_ramp.elements
        els[0].position, els[0].color = stops[0][0], (*stops[0][1], 1.0)
        els[1].position, els[1].color = stops[1][0], (*stops[1][1], 1.0)
        for pos, col in stops[2:]:
            e = els.new(pos)
            e.color = (*col, 1.0)
        nt.links.new(info.outputs["Intercept"], ramp.inputs[0])
        nt.links.new(ramp.outputs[0], h.inputs["Color"])
    nt.links.new(h.outputs[0], out.inputs["Surface"])
    return mat


# ---------------------------------------------------------------- checks

def report(hair_objects, surface=None, shapes=SHAPES):
    """Counts and checks on the evaluated hair: strands, length range, how far roots are from the
    surface (braids and tails start at a tie, so a few are far by design), and `backwards`: strands
    whose tip is nearer a lock's start than their root is (should be about 0)."""
    dg = bpy.context.evaluated_depsgraph_get()
    bvh = mi = None
    if surface:
        surf = bpy.data.objects[surface] if isinstance(surface, str) else surface
        bvh, mw = BVHTree.FromObject(surf, dg), surf.matrix_world
        mi = mw.inverted()
    from mathutils.kdtree import KDTree
    starts = []
    if shapes in bpy.data.collections:
        for s in bpy.data.collections[shapes].all_objects:
            if s.type == "CURVE" and s.data.splines:
                starts.append(s.matrix_world @ Vector(s.data.splines[0].points[0].co[:3]))
            elif s.type == "MESH" and "crease_edge" in s.data.attributes:
                cr = s.data.attributes["crease_edge"].data
                starts += [s.matrix_world @ s.data.vertices[v].co for e in s.data.edges if cr[e.index].value > 0.5 for v in e.vertices]
    kd = None
    if starts:
        kd = KDTree(len(starts))
        for i, p in enumerate(starts):
            kd.insert(p, i)
        kd.balance()
    out = {}
    for ob in hair_objects:
        ob = bpy.data.objects[ob] if isinstance(ob, str) else ob
        ev = ob.evaluated_get(dg).data
        pos = ev.attributes["position"].data
        offs = [c.first_point_index for c in ev.curves] + [len(pos)]
        n = len(ev.curves)
        info = {"strands": n}
        if n:
            lengths, gaps, backwards, checked = [], [], 0, 0
            step = max(1, n // 4000)
            for c in range(0, n, step):
                a, b = offs[c], offs[c + 1]
                pts = [ob.matrix_world @ Vector(pos[i].vector) for i in range(a, b)]
                lengths.append(sum((q - p).length for p, q in zip(pts, pts[1:])))
                checked += 1
                if bvh:
                    gaps.append(bvh.find_nearest(mi @ pts[0])[3])
                if kd:
                    backwards += kd.find(pts[-1])[2] < kd.find(pts[0])[2]
            lengths.sort()
            info.update(length_min_m=round(lengths[0], 4), length_median_m=round(lengths[len(lengths) // 2], 4),
                        length_max_m=round(lengths[-1], 4), checked=checked)
            if gaps:
                gaps.sort()
                info.update(root_gap_median_m=round(gaps[len(gaps) // 2], 4), root_gap_p95_m=round(gaps[int(len(gaps) * 0.95)], 4))
            if kd:
                info["backwards"] = backwards
        out[ob.name] = info
    return out


# ---------------------------------------------------------------- bake, brush, follow, export

def _ob(x):
    return bpy.data.objects[x] if isinstance(x, str) else x


def _copy(ob, name):
    c = ob.copy()
    c.data = ob.data.copy()
    c.name = c.data.name = name
    for col in ob.users_collection:
        col.objects.link(c)
    return c


def _apply(ob, names):
    for n in names:
        with bpy.context.temp_override(object=ob, active_object=ob, selected_objects=[ob], selected_editable_objects=[ob]):
            bpy.ops.object.modifier_apply(modifier=n)


def _hide(ob, hidden):
    ob.hide_viewport = ob.hide_render = hidden


def attach(curves, surface):
    """Ties hair curves to a surface for Blender's hair tools and for follow(): sets the surface and
    its UV map, and gives each curve its root's place on it (surface_uv_coordinate). The surface
    needs a UV map. bake() and bake_guides() call it when given a surface."""
    ob, surf = _ob(curves), _ob(surface)
    me = surf.data
    if not me.uv_layers:
        raise ValueError(f"{surf.name} needs a UV map")
    from mathutils.interpolate import poly_3d_calc
    cv = ob.data
    cv.surface = surf
    cv.surface_uv_map = me.uv_layers.active.name
    me.calc_loop_triangles()
    uv = me.uv_layers.active.data
    sw = surf.matrix_world
    verts = [sw @ v.co for v in me.vertices]
    tris = list(me.loop_triangles)
    bvh = BVHTree.FromPolygons(verts, [t.vertices[:] for t in tris])
    n = len(cv.curves)
    offs = [0] * (n + 1)
    cv.curve_offset_data.foreach_get("value", offs)
    pos = [0.0] * (len(cv.points) * 3)
    cv.attributes["position"].data.foreach_get("vector", pos)
    m = ob.matrix_world
    out = [0.0] * (n * 2)
    for c in range(n):
        i = offs[c] * 3
        loc, _nor, t, _d = bvh.find_nearest(m @ Vector(pos[i:i + 3]))
        tri = tris[t]
        w = poly_3d_calc([verts[v] for v in tri.vertices], loc)
        u = sum((uv[l].uv * k for l, k in zip(tri.loops, w)), Vector((0, 0)))
        out[c * 2], out[c * 2 + 1] = u.x, u.y
    attr = cv.attributes.get("surface_uv_coordinate") or cv.attributes.new("surface_uv_coordinate", "FLOAT2", "CURVE")
    attr.data.foreach_set("vector", out)
    return ob


def bake_guides(hair, surface=None):
    """The first step of baking, for brushing: a copy of the hair object whose guides (one per lock
    edge, ring and centre line) are real curves you can comb, grow and smooth with Blender's hair
    sculpt brushes, while the children, clumps, trim, frizz, noise and profile stay live modifiers on
    top of them. The live object is hidden, not removed: unbake() brings it back. Returns the copy."""
    src = _ob(hair)
    if not src.modifiers or src.modifiers[0].name != "Shapes to Hair":
        raise ValueError(f"{src.name} isn't a live hair() object")
    g = _copy(src, src.name + " (guides)")
    _apply(g, [g.modifiers[0].name])
    g["hs_source"], g["hs_stage"] = src.name, "guides"
    if surface is not None:
        attach(g, surface)
    _hide(src, True)
    return g


def bake(hair, surface=None):
    """Bakes hair to plain strands: a copy with every modifier applied, real curves throughout, to
    brush by hand, export or render without the shapes. Works on a live hair() object or on its
    bake_guides() copy (after brushing the guides). The source is hidden, not removed: unbake()
    brings it back, so you can change the shapes or the guides and bake again. Returns the copy."""
    src = _ob(hair)
    b = _copy(src, src.get("hs_source", src.name).replace(" (guides)", "") + " (baked)")
    _apply(b, [m.name for m in b.modifiers])
    b["hs_source"], b["hs_stage"] = src.name, "strands"
    if surface is not None:
        attach(b, surface)
    _hide(src, True)
    return b


def unbake(baked):
    """Removes a bake and shows what it was baked from again. Returns that object."""
    b = _ob(baked)
    src = bpy.data.objects.get(b.get("hs_source", ""))
    if src is None:
        raise ValueError(f"{b.name} wasn't made by bake() or bake_guides(), or its source is gone")
    data = b.data
    bpy.data.objects.remove(b)
    if data.users == 0:
        bpy.data.hair_curves.remove(data)
    _hide(src, False)
    return src


def follow(curves, surface=None):
    """Makes baked hair (or brushed guides) follow its surface as it deforms: an armature, shape keys
    or any modifier on the head, for cut-scenes rendered in Blender. Adds a Deform Curves on Surface
    modifier, last, and turns on the surface's rest position. The curves need attach() first."""
    ob = _ob(curves)
    surf = _ob(surface) if surface is not None else ob.data.surface
    if surf is None or "surface_uv_coordinate" not in ob.data.attributes:
        raise ValueError(f"attach({ob.name}, head) first")
    surf.add_rest_position_attribute = True
    name = "AO Follow Surface"
    ng = bpy.data.node_groups.get(name)
    if ng is None:
        ng = bpy.data.node_groups.new(name, "GeometryNodeTree")
        ng.interface.new_socket("Geometry", in_out="INPUT", socket_type="NodeSocketGeometry")
        ng.interface.new_socket("Geometry", in_out="OUTPUT", socket_type="NodeSocketGeometry")
        gi, go = ng.nodes.new("NodeGroupInput"), ng.nodes.new("NodeGroupOutput")
        de = ng.nodes.new("GeometryNodeDeformCurvesOnSurface")
        ng.links.new(gi.outputs[0], de.inputs[0])
        ng.links.new(de.outputs[0], go.inputs[0])
        ng.is_modifier = True
    md = ob.modifiers.get("Follow Surface") or ob.modifiers.new("Follow Surface", "NODES")
    md.node_group = ng
    return md


GAME_STRANDS = 50000  # Epic's MetaHuman head hair at LOD0 (25,000 at LOD1); Unreal cuts curves past 255 points


def _duplicates(ob):
    """The Duplicate Hair Curves modifiers on a live hair object, with their input identifiers."""
    out = []
    for md in ob.modifiers:
        if md.type == "NODES" and md.node_group and md.node_group.name.startswith("Duplicate Hair Curves"):
            ids = {i.name: i.identifier for i in md.node_group.interface.items_tree if getattr(i, "in_out", "") == "INPUT"}
            out.append((md, ids))
    return out


def _render_strands(ob):
    """Strands the object renders: guides x (1 + children), from the guides and its Duplicate modifier."""
    dg = bpy.context.evaluated_depsgraph_get()
    dup = _duplicates(ob)
    if not dup:
        return len(ob.evaluated_get(dg).data.curves)
    md, ids = dup[0]
    va = getattr(md.properties.inputs, ids["Viewport Amount"]).value
    shown = len(ob.evaluated_get(dg).data.curves)
    amount = getattr(md.properties.inputs, ids["Amount"]).value
    guides = shown / max(1e-6, 1 + amount * va)
    return int(round(guides * (1 + amount)))


def game_budget(objects, strands=GAME_STRANDS, widen=True):
    """Fits live hair to a game's strand budget, non-destructively: each object's children (its
    Duplicate modifier's Amount) are scaled down together so all of them render about `strands`
    strands, keeping their proportions. With `widen`, each strand gets wider by the square root of
    the cut (at most 2x) so the hair keeps its cover. Edit, bake and export as before. Measure the
    result with compare() (solid and detail) against the full hair: fewer strands, if too few, read
    as see-through or stringy. Returns (strands before, after)."""
    obs = [_ob(o) for o in (objects if isinstance(objects, (list, tuple)) else [objects])]
    live = [o for o in obs if _duplicates(o)]
    fixed = sum(_render_strands(o) for o in obs if o not in live)
    before = fixed + sum(_render_strands(o) for o in live)
    room = max(1, strands - fixed)
    now = sum(_render_strands(o) for o in live)
    ratio = min(1.0, room / max(1, now))
    for ob in live:
        md, ids = _duplicates(ob)[0]
        amount = getattr(md.properties.inputs, ids["Amount"]).value
        total = _render_strands(ob)
        guides = total / (1 + amount)
        new_amount = max(0, int(round((total * ratio) / max(1e-6, guides) - 1)))
        getattr(md.properties.inputs, ids["Amount"]).value = new_amount
        if widen and ratio < 1:
            for m in ob.modifiers:
                if m.type == "NODES" and m.node_group and m.node_group.name.startswith("Set Hair Curve Profile"):
                    pid = {i.name: i.identifier for i in m.node_group.interface.items_tree if getattr(i, "in_out", "") == "INPUT"}
                    sock = getattr(m.properties.inputs, pid["Radius"])
                    base = ob.get("hs_radius", sock.value)
                    ob["hs_radius"] = base
                    sock.value = base * min(2.0, (1 / ratio) ** 0.5)
        ob.update_tag()
    bpy.context.view_layer.update()
    after = fixed + sum(_render_strands(o) for o in live)
    print(f"GAME BUDGET {before} -> {after} strands (target {strands})")
    return before, after


def _simplify(p, tol):
    """Indices of the points to keep on one polyline (n x 3 array) so it stays within tol of itself."""
    n = len(p)
    if n <= 2:
        return list(range(n))
    keep = np.zeros(n, bool)
    keep[0] = keep[-1] = True
    todo = [(0, n - 1)]
    while todo:
        a, b = todo.pop()
        if b - a < 2:
            continue
        seg = p[b] - p[a]
        ln = np.linalg.norm(seg)
        mid = p[a + 1:b] - p[a]
        if ln < 1e-12:
            d = np.linalg.norm(mid, axis=1)
        else:
            d = np.linalg.norm(np.cross(mid, seg / ln), axis=1)
        i = int(np.argmax(d))
        if d[i] > tol:
            k = a + 1 + i
            keep[k] = True
            todo += [(a, k), (k, b)]
    return np.nonzero(keep)[0]


def export_groom(objects, path, scale=100.0, frame=None, tolerance=0.0002, max_points=64, merge=True):
    """Exports hair as an Alembic groom for Unreal, made light enough for a game:
      - each strand keeps only the points it needs to stay within `tolerance` m of its shape (0.2 mm:
        a straight strand drops to a few points, a braid keeps its weave), at most `max_points`
        (Unreal cuts curves past 255);
      - with `merge`, objects that share a material become one group (one material slot each in
        Unreal; every group costs its own passes), named after the first object in it;
      - roots first, widths from each point's radius, all in centimetres (`scale` 100 from metres) with
        the transforms baked in.
    Takes baked objects or live ones (their evaluated hair is written, at render density). Prints
    strands and points per group and in all, against Epic's MetaHuman budget. Unreal: import the .abc
    as a Groom with its default conversion settings (rotate X 90, scale Y -1), then bind it to the
    head's skeletal mesh, and set up its LODs there."""
    obs = [_ob(o) for o in (objects if isinstance(objects, (list, tuple)) else [objects])]
    temp, old = [], [ob.name for ob in obs]
    clean = [ob.name.replace(" (baked)", "").replace(" (guides)", "") for ob in obs]
    groups = {}
    for ob, name in zip(obs, clean):
        key = (ob.data.materials[0].name if ob.data.materials and ob.data.materials[0] else name) if merge else name
        groups.setdefault(key, []).append((ob, name))
    vl = bpy.context.view_layer
    picked, active = [o for o in vl.objects if o.select_get()], vl.objects.active
    renders = {}
    for ob in obs:  # export at render density: lift the viewport share for the evaluation
        for md, ids in _duplicates(ob):
            sock = getattr(md.properties.inputs, ids["Viewport Amount"])
            renders[(ob.name, md.name)] = sock.value
            sock.value = 1.0
    stats = []
    try:
        for ob in obs:  # the copies take the sections' own names: the engine shows them as group names
            ob.name = ob.name + " ~export"
        dg = bpy.context.evaluated_depsgraph_get()
        for _key, members in groups.items():
            sizes, flat, rads, mats = [], [], [], []
            for ob, _name in members:
                cv = ob.evaluated_get(dg).data
                n, npts = len(cv.curves), len(cv.points)
                offs = np.zeros(n + 1, np.int32)
                cv.curve_offset_data.foreach_get("value", offs)
                pos = np.zeros(npts * 3, np.float32)
                cv.attributes["position"].data.foreach_get("vector", pos)
                pos = pos.reshape(-1, 3).astype(np.float64)
                m = np.array(ob.matrix_world, np.float64)
                pos = (pos @ m[:3, :3].T + m[:3, 3]) * scale
                rad = np.zeros(npts, np.float32)
                if "radius" in cv.attributes:
                    cv.attributes["radius"].data.foreach_get("value", rad)
                for c in range(n):
                    a, b = offs[c], offs[c + 1]
                    k = _simplify(pos[a:b], tolerance * scale)
                    if len(k) > max_points:  # evenly along it, ends kept
                        k = np.asarray(k)[np.round(np.linspace(0, len(k) - 1, max_points)).astype(int)]
                    sizes.append(len(k))
                    flat.append(pos[a:b][k])
                    rads.append(rad[a:b][k] * scale)
                for mat in ob.data.materials:
                    if mat and mat not in mats:
                        mats.append(mat)
            name = members[0][1]
            data = bpy.data.hair_curves.new(name)
            data.add_curves(sizes)
            data.attributes["position"].data.foreach_set("vector", np.concatenate(flat).astype(np.float32).ravel())
            ra = data.attributes.get("radius") or data.attributes.new("radius", "FLOAT", "POINT")
            ra.data.foreach_set("value", np.concatenate(rads).astype(np.float32))
            for mat in mats:
                data.materials.append(mat)
            t = bpy.data.objects.new(name, data)
            bpy.context.scene.collection.objects.link(t)
            temp.append(t)
            stats.append((name, len(sizes), int(sum(sizes)), max(sizes) if sizes else 0, [o.name for o, _n in members]))
        for o in bpy.context.view_layer.objects:
            o.select_set(o in temp)
        bpy.context.view_layer.objects.active = temp[0]
        f = bpy.context.scene.frame_current if frame is None else frame
        bpy.ops.wm.alembic_export(filepath=os.path.abspath(path), selected=True, global_scale=1.0, start=f, end=f,
                                  uvs=False, normals=False, export_hair=False, export_particles=False)
    finally:
        for t in temp:
            data = t.data
            bpy.data.objects.remove(t)
            bpy.data.hair_curves.remove(data)
        for ob, name in zip(obs, old):
            ob.name = name
        for ob in obs:
            for md, ids in _duplicates(ob):
                if (ob.name, md.name) in renders:
                    getattr(md.properties.inputs, ids["Viewport Amount"]).value = renders[(ob.name, md.name)]
        for o in picked:
            o.select_set(True)
        vl.objects.active = active
    strands, points = sum(s[1] for s in stats), sum(s[2] for s in stats)
    print(f"GROOM {path}: {len(stats)} groups, {strands} strands, {points} points ({points / max(1, strands):.1f} a strand)")
    for name, n, p, most, members in stats:
        print(f"  {name}: {n} strands, {p} points, at most {most} on one ({len(members)} objects)")
    if strands > GAME_STRANDS * 1.2:
        print(f"  Over a game budget: Epic's MetaHuman head hair is {GAME_STRANDS} strands at LOD0. game_budget() cuts it.")
    return {"path": path, "groups": len(stats), "strands": strands, "points": points}


# ---------------------------------------------------------------- self test

def _selftest(out_dir):
    """A sphere head with bangs, side locks, a pigtail into a braid with a tail and a tie, and a
    creased mesh strip; converts it, reports, and renders the blockout and the hair."""
    os.makedirs(out_dir, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.mesh.primitive_uv_sphere_add(radius=0.1, segments=64, ring_count=32, location=(0, 0, 0))
    head = bpy.context.active_object
    head.name = "Head"
    bpy.ops.object.shade_smooth()
    R = 0.1
    sph = lambda th, ph, r=R: Vector((r * math.sin(th) * math.cos(ph), r * math.sin(th) * math.sin(ph), r * math.cos(th)))
    # bangs: roots on the front of the crown, falling over the forehead (face looks down -Y)
    for i in range(7):
        ph = -math.pi / 2 + (i - 3) * 0.16
        root = sph(0.25, ph, R * 0.985)
        top = sph(0.55, ph, R * 1.03)
        mid = sph(0.95, ph, R * 1.06)
        tip = sph(1.25, ph, R * 1.07)
        lock(f"Bang{i}", [root, top, mid, tip], (0.006, 0.008, 0.002))
    # side locks
    for s in (-1, 1):
        ph = -math.pi / 2 + s * 0.75
        lock(f"Side{s}", [sph(0.3, ph, R * 0.985), sph(0.8, ph, R * 1.04), sph(1.4, ph, R * 1.05), sph(1.9, ph, R * 1.02)],
             (0.006, 0.007, 0.0015))
    # one pigtail: locks from the crown down the back to a tie, then a braid and its tail
    tie_pt = sph(2.0, math.pi / 2 + 0.6, R * 1.05)
    for i in range(6):
        ph = math.pi / 2 + 0.05 + i * 0.12
        root = sph(0.2, ph, R * 0.985)
        lock(f"Pig{i}", [root] + on_surface([sph(0.9, ph), sph(1.5, ph * 0.9 + 0.06)], head, 0.0075) + [tie_pt],
             (0.006, 0.007, 0.006))
    down = tie_pt + Vector((0.0, -0.04, -0.03))
    end = tie_pt + Vector((0.0, -0.06, -0.15))
    braid("Braid", [tie_pt, down, end], width=(0.022, 0.013), turns=4, tail_locks=6,
          tail=[end, end + Vector((0, -0.005, -0.03)), end + Vector((0, -0.006, -0.06))])
    tie("Tie2", end, (0, -0.15, -1), radius=0.006)
    tie("Tie", tie_pt, (0, -0.4, -1), radius=0.008)
    # a mesh strip with its root creased, behind the other ear
    strip("Strip", [sph(0.5, -0.4 + k * 0.08, R * 0.985) for k in range(5)],
          [sph(1.5, -0.4 + k * 0.08, R * 1.05) + Vector((0, 0, -0.03)) for k in range(5)], rows=20)

    preview(on=True, material=hair_material("M_Blockout", ((0.0, (0.2, 0.2, 0.25)),)))
    _render(out_dir, "blockout")
    preview(on=False)
    bpy.data.collections[SHAPES].hide_render = True

    mat = hair_material("M_Hair", ((0.0, (0.01, 0.008, 0.008)), (0.55, (0.012, 0.009, 0.010)), (1.0, (0.18, 0.03, 0.30))))
    main = hair("Hair", material=mat, parent=head)
    loose = strays("Strays", material=mat, parent=head)
    hairline = [sph(0.42, -math.pi / 2 + a, R) for a in [x * 0.1 for x in range(-8, 9)]]
    baby = baby_hair("BabyHair", head, hairline, material=mat)
    rep = report([main, loose, baby], head)
    for k, v in rep.items():
        print("REPORT", k, v)
    _render(out_dir, "hair")
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out_dir, "selftest.blend"))
    return rep


def gpu(scene=None):
    """Cycles on the GPU with one backend only: OptiX, else CUDA, else HIP or Metal; CPU if none.
    Saved preferences often tick several backends and the CPU at once, and with hundreds of
    thousands of strands a 6 GB card then runs out of memory. Returns the device used."""
    sc = scene or bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    try:
        prefs = bpy.context.preferences.addons["cycles"].preferences
        for kind in ("OPTIX", "CUDA", "HIP", "METAL", "ONEAPI"):
            try:
                prefs.compute_device_type = kind
            except TypeError:
                continue
            prefs.get_devices()
            if any(d.type == kind for d in prefs.devices):
                for d in prefs.devices:
                    d.use = d.type == kind
                sc.cycles.device = "GPU"
                return kind
    except Exception as e:
        print("GPU setup failed, rendering on the CPU:", e)
    return "CPU"


def _render(out_dir, tag):
    sc = bpy.context.scene
    gpu(sc)
    sc.cycles.samples = 64
    sc.render.resolution_x, sc.render.resolution_y = 900, 900
    if not sc.world:
        sc.world = bpy.data.worlds.new("W")
    sc.world.use_nodes = True
    sc.world.node_tree.nodes["Background"].inputs[0].default_value = (0.8, 0.8, 0.8, 1)
    if "Key" not in bpy.data.objects:
        lt = bpy.data.objects.new("Key", bpy.data.lights.new("Key", "AREA"))
        lt.data.energy, lt.data.size = 60, 0.4
        lt.location = (0.3, -0.4, 0.4)
        lt.rotation_euler = (math.radians(50), 0, math.radians(35))
        sc.collection.objects.link(lt)
    cam = bpy.data.objects.get("Cam") or bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    if cam.name not in sc.collection.objects:
        sc.collection.objects.link(cam)
    sc.camera = cam
    cam.data.lens = 60
    for view, d in (("front", Vector((0, -1, 0.1))), ("side", Vector((1, 0, 0.05))), ("back", Vector((-0.3, 1, 0.1)))):
        cam.location = d.normalized() * 0.75 + Vector((0, 0, -0.04))
        cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
        sc.render.filepath = os.path.join(out_dir, f"{tag}_{view}.png")
        bpy.ops.render.render(write_still=True)


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if argv[:1] == ["--selftest"]:
        _selftest(argv[1] if len(argv) > 1 else os.path.join(os.getcwd(), "hair_shapes_selftest"))
