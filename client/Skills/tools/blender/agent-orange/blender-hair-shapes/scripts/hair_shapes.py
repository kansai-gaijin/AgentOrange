"""Hair from shapes: block a hairstyle out as hair locks (curves with a radius per point, or meshes
with a creased root edge), check the blockout, then turn it into strand hair with Geometry Nodes.

Blockout (all in one collection, HairShapes by default):
  lock(name, points, radii)          a lock: a curve from root to tip, thick where the radius is
  braid(name, points, width, ...)    three locks woven along a path, plus an optional loose tail
  scalp_ring / scalp_spiral(cap, centre, ...)   paths round a centre over the scalp, for crown braids
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
  look(cams, out, cap=cap)           the blockout as hair beside each reference: judge this, not the tubes
  coverage(cap, hair)                bare scalp: its share, each patch and where; the cap coloured red there
  report(hair_objects, surface)      guide and strand counts, root gaps, roots-first, length range
Hand it over:
  bake_guides(hair, head)            guides as real curves to comb; children, clumps and frizz stay live
  bake(hair, head)                   plain brushable strands; the source is kept, hidden
  unbake(baked)                      back to the source, to edit and bake again
  attach(curves, head) / follow(curves)   root UVs on the head; follow it as it deforms (cut-scenes)
  game_budget(objects, strands)      fewer, wider strands for a game (50,000: MetaHuman LOD0), still live
  hairline(head)                     a human hairline found from the head (its ears), for the cap
  hair_cap(head, hairline)           the scalp all hair grows from; make it first
  to_cap(cap, locks)                 every lock rooted on the cap (strays routed back along their lock)
  export_groom(objects, path)        an Alembic groom to Epic's schema: group ids, root UVs, guides, in cm
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
    from .hair_match import _flat, _load, _save
except ImportError:  # imported or run as a plain script
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from hair_match import compare, measure, ref_camera, sections  # noqa: E402,F401
    from hair_match import _flat, _load, _save  # noqa: E402

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


def _nearest_on(ob, p):
    """The nearest point on a mesh object to p, in world space."""
    mw = ob.matrix_world
    ok, loc, _n, _i = ob.closest_point_on_mesh(mw.inverted() @ Vector(p))
    return mw @ loc if ok else Vector(p)


def braid(name, points, width=0.012, turns=4.0, strand_radius=0.32, tail=None, tail_flare=1.6, tail_stagger=0.12,
          tail_locks=0, resolution=96, collection=SHAPES, up=(0, 0, 1), seed=0, root=None):
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
    - root: the hair cap. Hair only grows from the scalp, so each strand then starts on the cap, under
      the hair gathered into the tie, and runs to the tie; the tail locks start there too and run down
      inside the braid to the elastic.
    Returns the strand objects (the three strands, then any tail locks)."""
    rnd = random.Random(seed)
    cap = _ob(root) if root is not None else None
    start = _nearest_on(cap, points[0]) if cap is not None else None
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
        if start is not None:  # from the scalp, under the gathered hair, to the tie
            _tan, side, _d = _frame(path, 0, up)
            r0 = strand_radius * w0 * 0.75
            pts = [start - (start - path[0]).normalized() * 0.0015 + side * (w0 * 0.25 * (k - 1)),
                   start.lerp(path[0], 0.5)] + pts
            rad = [r0 * 0.8, r0] + rad
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
        tip = (w1 * 0.17, w1 * 0.3 * rnd.uniform(0.7, 1.1), 0.0005)
        if start is None:
            obs.append(lock(f"{name}_tail{m}", pts, tip, resolution=24, collection=collection))
            continue
        # from the scalp, thin, down the middle of the braid (hidden in it), out through the elastic
        inside = [start - (start - path[0]).normalized() * 0.0015] + path[::max(1, resolution // 12)] + [path[-1]]
        r_in = [0.0008] * len(inside)
        r_out = _radii(tip, len(pts))
        obs.append(lock(f"{name}_tail{m}", inside + pts[1:], r_in + r_out[1:], resolution=len(inside) + len(pts) + 8,
                        collection=collection))
    return obs


def to_cap(cap, locks, tol=0.002, along=SHAPES, far=0.02):
    """Hair only grows from the scalp: every lock whose root is more than `tol` m off the hair cap gets
    a root on the cap.
    - Near the cap (under `far` m): joined straight to the nearest point on it, under the hair.
    - Further out (a stray leaving a braid or a lock part-way down): routed back along the lock it
      leaves, the nearest one in `along` that is rooted on the cap, thin and inside it, so it shows only
      where it leaves. Run it on the main sections first, so they're rooted when strays follow them.
    - Prints any it could only join straight from further than `far` (route those by hand).
    locks: a collection's name, a collection, or lock objects. Returns how many got a new root."""
    c = _ob(cap)
    hosts = []  # (points world, radii) of locks rooted on the cap, to route far roots along
    for h in (_section(along) if along in bpy.data.collections or not isinstance(along, str) else []):
        if h.type != "CURVE":
            continue
        for sp in h.data.splines:
            if sp.type == "POLY" and len(sp.points) > 1:
                pts = [h.matrix_world @ Vector(p.co[:3]) for p in sp.points]
                if (_nearest_on(c, pts[0]) - pts[0]).length <= tol:
                    hosts.append((h.name, pts))
    moved = 0
    for ob in _section(locks):
        if ob.type != "CURVE":
            continue
        m, mi = ob.matrix_world, ob.matrix_world.inverted()
        for sp in ob.data.splines:
            if sp.type != "POLY" or not sp.points:
                continue
            root = m @ Vector(sp.points[0].co[:3])
            on = _nearest_on(c, root)
            gap = (on - root).length
            if gap <= tol:
                continue
            old = [(m @ Vector(p.co[:3]), p.radius) for p in sp.points]  # world space from here on
            new = [(on - (on - root).normalized() * 0.0015, old[0][1])]
            if gap > far:
                best = None
                for name, pts in hosts:
                    if name == ob.name:
                        continue
                    j = min(range(len(pts)), key=lambda i: (pts[i] - root).length)
                    d = (pts[j] - root).length
                    if best is None or d < best[0]:
                        best = (d, pts, j)
                if best is not None and best[0] < gap and best[2] > 0:
                    _d, pts, j = best
                    thin = min(0.0006, old[0][1])
                    new = [(p, thin) for p in pts[:j]]
                else:
                    print(f"TO CAP {ob.name}: root {gap * 1000:.0f} mm off the cap, joined straight; route it by hand")
            sp.points.add(len(new))
            for p, (co, r) in zip(sp.points, new + old):
                p.co, p.radius = (*(mi @ co), 1.0), r
            moved += 1
    return moved


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


def _surface(ob):
    ob = _ob(ob)
    bvh = BVHTree.FromObject(ob, bpy.context.evaluated_depsgraph_get())
    mw = ob.matrix_world
    return bvh, mw, mw.inverted(), mw.inverted().transposed().to_3x3()


def _onto(srf, p):
    bvh, mw, mi, nm = srf
    loc, nor, _i, _d = bvh.find_nearest(mi @ Vector(p))
    return mw @ loc, (nm @ nor).normalized()


def _walk(srf, start, heading, dist, step=0.002):
    """From start, `dist` metres over the surface, setting off along heading: each step goes along the
    tangent plane and back onto the surface, so the distance follows the scalp's curve."""
    p, n = _onto(srf, start)
    d = Vector(heading)
    d = d - n * d.dot(n)
    left = dist
    while left > 1e-7 and d.length > 1e-9:
        d.normalize()
        s = min(step, left)
        q, n2 = _onto(srf, p + d * s)
        if (q - p).length < s * 0.2:  # stuck at the surface's edge
            break
        d = (q - p) - n2 * (q - p).dot(n2)
        p, n = q, n2
        left -= s
    return p, n


def _polar(surface, centre, zero):
    srf = _surface(surface)
    c, n = _onto(srf, centre)
    u = Vector(zero) - n * Vector(zero).dot(n)
    if u.length < 1e-6:
        u = n.orthogonal()
    u.normalize()
    return srf, c, u, n.cross(u).normalized()


def scalp_ring(surface, centre, radius, lift=0.0, start=0.0, sweep=360.0, points=48, zero=(0.0, -1.0, 1.0)):
    """Points round `centre` on the scalp, each `radius` m from it measured over the scalp, lifted `lift`
    m off it: a path for a braid that wraps round a crown, a bun's base or a halo.
    - surface: the hair cap (best: the scalp alone, so the path can't climb a horn or an ear), or the head.
    - Angles in degrees round the centre. 0 points towards `zero` (default up and to the face, so from
      a crown at the back of the head 0 is towards the top); 90 is a quarter turn towards the head's
      +X side. sweep 360 closes the ring (its last point is its first); less gives an arc.
    - For a braid, lift about 0.55 x its width, so its plaits lie on the scalp, not in it.
    - Rings of one crown: give them the same centre and radii one braid width apart, and they lie edge
      to edge with no scalp between them. Start each where something covers its ends (under the back
      braid, under the next ring)."""
    srf, c, u, v = _polar(surface, centre, zero)
    out = []
    for k in range(points):
        a = math.radians(start + sweep * k / (points - 1))
        p, n = _walk(srf, c, u * math.cos(a) + v * math.sin(a), radius)
        out.append(p + n * lift)
    return out


def scalp_spiral(surface, centre, r0, r1, turns, lift=0.0, start=0.0, points_per_turn=48, zero=(0.0, -1.0, 1.0),
                 clockwise=False):
    """A spiral over the scalp round `centre`, from `r0` to `r1` m out (measured over the scalp) in
    `turns` turns, lifted `lift` m: one braid wound round and round, as in a crown braid or a rosette.
    Set (r1 - r0) / turns to the braid's width and the turns lie edge to edge with no scalp between them.
    Angles and `zero` as in scalp_ring; `clockwise` as seen from outside the head."""
    srf, c, u, v = _polar(surface, centre, zero)
    if clockwise:
        v = -v
    count = max(2, int(points_per_turn * turns) + 1)
    out = []
    for k in range(count):
        t = k / (count - 1)
        a = math.radians(start) + 2 * math.pi * turns * t
        p, n = _walk(srf, c, u * math.cos(a) + v * math.sin(a), r0 + (r1 - r0) * t)
        out.append(p + n * lift)
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


def hairline(head, front=0.45, nape=0.012, sideburn=0.5, show=True):
    """A human hairline for `head` (face looking down -Y, up +Z), found from the head itself, to start
    the hair cap from instead of guessing. It finds the ears (thin flaps sticking out of the sides: a
    ray into the head from their surface comes out again within a centimetre) and builds the line
    round them the way hair grows:
      - the forehead at `front` of the way from the ears' tops to the top of the head;
      - the temples a little higher and further back;
      - a sideburn down in front of each ear to `sideburn` of the way down it;
      - over each ear just above where it joins the head;
      - behind each ear down to its lobe;
      - the nape `nape` m below the lobes.
    Adjust it against the references (the forehead line especially, which shows). Returns the
    points, a closed loop, for hair_cap() and baby_hair(); with `show`, prints the ears it found."""
    h = _ob(head)
    me, mw = h.data, h.matrix_world
    nm = mw.to_3x3().inverted().transposed()
    co = [mw @ v.co for v in me.vertices]
    no = [(nm @ v.normal).normalized() for v in me.vertices]
    bvh = BVHTree.FromPolygons(co, [p.vertices[:] for p in me.polygons])
    zt = max(v.z for v in co)
    band = [i for i, v in enumerate(co) if v.z > zt - 0.25]
    cx = sum(co[i].x for i in band) / len(band)
    ears = {}
    for s in (1, -1):
        found = []
        wide = max(s * (co[i].x - cx) for i in band)
        for i in band:
            v, n = co[i], no[i]
            if s * n.x < 0.35 or s * (v.x - cx) < 0.55 * wide:
                continue
            hit, _n, _f, d = bvh.ray_cast(v - n * 0.0004, -n, 0.02)
            if hit is not None and d < 0.012:
                found.append(i)
        if len(found) < 8:
            raise ValueError(f"no ear found on the {'left' if s > 0 else 'right'}: give hair_cap() your own hairline")
        zs = sorted(co[i].z for i in found)
        ys = sorted(co[i].y for i in found)
        q = lambda a, f: a[min(len(a) - 1, max(0, int(f * (len(a) - 1))))]
        ears[s] = dict(top=q(zs, 0.97), bottom=q(zs, 0.03), front=q(ys, 0.05), back=q(ys, 0.95), verts=set(found))
    ear_top = max(e["top"] for e in ears.values())
    ear_bot = min(e["bottom"] for e in ears.values())
    sk = [co[i] for i in band if abs(co[i].z - ear_top) < 0.01]
    cy = (min(v.y for v in sk) + max(v.y for v in sk)) / 2
    all_ears = set().union(*(e["verts"] for e in ears.values()))
    face_of = {}
    for f in me.polygons:
        face_of[f.index] = f.vertices[:]

    def onto(angle, z):
        """Where a ray from outside, at height z and `angle` degrees round from the front, meets the
        scalp (passing through an ear in the way)."""
        a = math.radians(angle)
        d = Vector((math.sin(a), -math.cos(a), 0.0))
        o = Vector((cx, cy, z)) + d * 0.5
        for _ in range(6):
            hit, _n, fi, _dd = bvh.ray_cast(o, -d, 1.0)
            if hit is None:
                return None
            if not any(v in all_ears for v in face_of[fi]):
                return hit
            o = hit - d * 0.0005
        return hit

    zf = ear_top + front * (zt - ear_top)
    side = []
    for s in (1,):
        e = ears[s]
        angs = sorted(math.degrees(math.atan2(s * (co[i].x - cx), -(co[i].y - cy))) for i in e["verts"])
        af, ab = angs[int(0.05 * (len(angs) - 1))], angs[int(0.95 * (len(angs) - 1))]  # round from the front
        mid = e["bottom"] + (1 - sideburn) * (e["top"] - e["bottom"])
        side = [(0, zf), (28, zf - 0.002), (52, zf + 0.002), (af - 14, e["top"] + 0.01), (af - 9, mid), (af - 3, mid + 0.004),
                (af + 2, e["top"] + 0.004), ((af + ab) / 2, e["top"] + 0.006), (ab - 2, e["top"] + 0.002),
                (ab + 5, (e["top"] + e["bottom"]) / 2), (ab + 9, e["bottom"]), (150, e["bottom"] - nape * 0.6),
                (180, ear_bot - nape)]
    loop = []
    for a, z in side:  # her left, front to back
        p = onto(a, z)
        if p is not None:
            loop.append(p)
    for a, z in reversed(side[1:-1]):  # her right, back to front, mirrored
        p = onto(-a, z)
        if p is not None:
            loop.append(p)
    if show:
        for s, e in ears.items():
            print(f"HAIRLINE ear {'left' if s > 0 else 'right'}: z {e['bottom']:.3f}..{e['top']:.3f}, "
                  f"y {e['front']:.3f}..{e['back']:.3f}; forehead line at z {zf:.3f}, nape at {ear_bot - nape:.3f}")
    return loop


def hair_cap(head, hairline_points, color=(0.012, 0.010, 0.010), apex=None, offset=0.0003, fade=0.006, name="HairCap",
             material=None):
    """The hair cap: the scalp, inside the hairline, as its own mesh just over the head. Make it
    first: all hair grows from it, and between strands it shows hair-coloured scalp, not skin, as a
    game groom's cap does.
    - hairline_points: the hairline all round (forehead, temples, round the ears, the nape), as for
      baby_hair(). The cap is everything on the head's crown side of it, reached from the top of the
      head (`apex`, the highest point by default) without crossing it, so an ear tip above the
      hairline stays bare.
    - The cap's edge fades out over `fade` m across the hairline (a soft hairline, its `hair_cover`
      attribute, 0..1, drives the material's alpha); it sits `offset` m over the head and keeps its UVs.
    - color: the hair's root colour (linear RGB), with a faint variation. Or give your own material.
    It stays in Blender: only the curves go to the engine, and their root UVs come from it (it keeps
    the head's UVs). Returns the cap object."""
    import bmesh
    from mathutils.kdtree import KDTree
    h = _ob(head)
    me, mw = h.data, h.matrix_world
    nm = mw.to_3x3().inverted().transposed()
    co = [mw @ v.co for v in me.vertices]
    no = [(nm @ v.normal).normalized() for v in me.vertices]
    hl = [Vector(p) for p in hairline_points]
    on_head = BVHTree.FromPolygons(co, [p.vertices[:] for p in me.polygons])
    dense = []
    for a, b in zip(hl, hl[1:] + hl[:1]):  # a closed loop, every 2 mm, laid on the head (a straight
        steps = max(1, int((b - a).length / 0.002))  # chord between far points cuts under the skin)
        for s in range(steps):
            p = on_head.find_nearest(a.lerp(b, s / steps))[0]
            dense.append(p if p is not None else a.lerp(b, s / steps))
    kd = KDTree(len(dense))
    for i, p in enumerate(dense):
        kd.insert(p, i)
    kd.balance()
    top = Vector(apex) if apex is not None else max(co, key=lambda v: v.z)
    top_i = min(range(len(co)), key=lambda i: (co[i] - top).length)
    # the hairline cuts the mesh: every edge of a face it runs across is cut, and a flood from the top of
    # the head over the uncut edges is the crown side, however the line winds (down in front of the ear,
    # back up over it)
    cut = set()
    for p in dense:
        for _loc, _n, fi, _d in on_head.find_nearest_range(p, 0.0025):
            cut.update(me.polygons[fi].edge_keys)
    nbr = [[] for _ in co]
    for e in me.edges:
        if e.key in cut:
            continue
        a, b = e.vertices
        nbr[a].append(b)
        nbr[b].append(a)
    inside = [False] * len(co)
    inside[top_i] = True
    todo = [top_i]
    while todo:
        i = todo.pop()
        for j in nbr[i]:
            if not inside[j]:
                inside[j] = True
                todo.append(j)
    if sum(inside) > 0.9 * len(co):
        print("HAIR CAP: the hairline doesn't close (the flood got round it); check its points")
    near = [kd.find(v)[2] for v in co]
    side = [d if inside[i] else -d for i, d in enumerate(near)]  # distance from the hairline, + on the crown side
    keep = [s > -fade * 0.5 for s in side]
    bm = bmesh.new()
    vmap = {}
    uvl = bm.loops.layers.uv.new("UVMap")
    cover = bm.verts.layers.float.new("hair_cover")
    src_uv = me.uv_layers.active.data if me.uv_layers else None
    for poly in me.polygons:
        if not any(keep[v] for v in poly.vertices):  # every face touching the region; the fade trims it
            continue
        vs = []
        for v in poly.vertices:
            if v not in vmap:
                bv = bm.verts.new(co[v] + no[v] * offset)
                s = (side[v] + fade * 0.5) / fade
                s = max(0.0, min(1.0, s))
                bv[cover] = s * s * (3 - 2 * s)
                vmap[v] = bv
            vs.append(vmap[v])
        try:
            f = bm.faces.new(vs)
        except ValueError:
            continue
        f.smooth = True
        if src_uv:
            for loop, li in zip(f.loops, poly.loop_indices):
                loop[uvl].uv = src_uv[li].uv
    # finer than the head's faces, so the hairline is a smooth fade, not a staircase: subdivide, put the
    # new vertices back on the head, work out their cover again, and drop the faces wholly past the fade
    bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=2, use_grid_fill=True)
    from mathutils.interpolate import poly_3d_calc
    polys = [p.vertices[:] for p in me.polygons]
    bvh = BVHTree.FromPolygons(co, polys)
    for bv in bm.verts:
        loc, nrm, fi, _d = bvh.find_nearest(bv.co)
        if loc is None:
            continue
        w = poly_3d_calc([co[v] for v in polys[fi]], loc)  # the side across the face, smoothly
        s = (sum(side[v] * k for v, k in zip(polys[fi], w)) + fade * 0.5) / fade
        s = max(0.0, min(1.0, s))
        bv.co = loc + nrm.normalized() * offset
        bv[cover] = s * s * (3 - 2 * s)
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if all(v[cover] <= 0.0 for v in f.verts)], context="FACES")
    cap_me = bpy.data.meshes.new(name)
    bm.to_mesh(cap_me)
    bm.free()
    cap = bpy.data.objects.new(name, cap_me)
    for col in h.users_collection:
        col.objects.link(cap)
    cap.parent = h
    cap.matrix_parent_inverse = h.matrix_world.inverted()
    if material is None:
        material = _cap_material(name + " Scalp", color)
    cap_me.materials.append(material)
    # it moves with the head however the head is deformed (armature, shape keys), and baked hair
    # attached to it with follow() comes along
    md = cap.modifiers.new("Follow Head", "SURFACE_DEFORM")
    md.target = h
    try:
        with bpy.context.temp_override(object=cap, active_object=cap, selected_objects=[cap]):
            bpy.ops.object.surfacedeform_bind(modifier=md.name)
    except RuntimeError as e:
        print(f"HAIR CAP: couldn't bind it to {h.name} ({e}); it moves with the head only by its parent")
    print(f"HAIR CAP {name}: {len(cap_me.polygons)} faces of {len(me.polygons)} on {h.name}")
    return cap


def _cap_material(name, color):
    """The cap's colour: the hair's root colour, a faint variation, its edge fading out by `hair_cover`."""
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.diffuse_color = (*(c * 4 + 0.03 for c in color), 1.0)  # how it shows in the solid viewport
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bs = nt.nodes.new("ShaderNodeBsdfPrincipled")
    at = nt.nodes.new("ShaderNodeAttribute")
    at.attribute_name = "hair_cover"
    nz = nt.nodes.new("ShaderNodeTexNoise")
    nz.inputs["Scale"].default_value = 180.0
    nz.inputs["Detail"].default_value = 4.0
    mix = nt.nodes.new("ShaderNodeMix")
    mix.data_type = "RGBA"
    mix.inputs["A"].default_value = (*color, 1.0)
    mix.inputs["B"].default_value = (*(c * 1.8 + 0.004 for c in color), 1.0)
    nt.links.new(nz.outputs["Fac"], mix.inputs["Factor"])
    nt.links.new(mix.outputs["Result"], bs.inputs["Base Color"])
    bs.inputs["Roughness"].default_value = 0.85
    nt.links.new(at.outputs["Fac"], bs.inputs["Alpha"])
    nt.links.new(bs.outputs[0], out.inputs["Surface"])
    if hasattr(mat, "surface_render_method"):
        mat.surface_render_method = "DITHERED"
    return mat


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


KEEP_OUT = "AO Keep Out Of Head"
KEEP_OUT_VERSION = 2


def keep_out_group():
    """Geometry Nodes: every strand's root onto the cap (or the head), and every other point that
    has gone into the head (by up to Reach) back out to Margin over it. Hair grows only from the cap,
    and none of it goes through the model.
    A point counts as inside only when it's within Reach of the surface and mostly under it, along
    the normal. Hair hanging past a bust's open rim, or far off to the side of a horn, sits beside
    the nearest surface, not under it, and is left alone (version 1 snapped such points onto it)."""
    ng = bpy.data.node_groups.get(KEEP_OUT)
    if ng is not None and ng.get("hs_version", 1) >= KEEP_OUT_VERSION:
        return ng
    if ng is not None:  # an older one in this file: keep it for the modifiers that use it, build the new one
        ng.name = f"{KEEP_OUT} (v{ng.get('hs_version', 1)})"
    ng = bpy.data.node_groups.new(KEEP_OUT, "GeometryNodeTree")
    ng["hs_version"] = KEEP_OUT_VERSION
    ng.is_modifier = True
    io = ng.interface
    io.new_socket("Geometry", in_out="INPUT", socket_type="NodeSocketGeometry")
    io.new_socket("Head", in_out="INPUT", socket_type="NodeSocketObject")
    io.new_socket("Cap", in_out="INPUT", socket_type="NodeSocketObject")
    io.new_socket("Avoid", in_out="INPUT", socket_type="NodeSocketCollection")
    s = io.new_socket("Margin", in_out="INPUT", socket_type="NodeSocketFloat")
    s.default_value, s.subtype = 0.0005, "DISTANCE"
    s = io.new_socket("Reach", in_out="INPUT", socket_type="NodeSocketFloat")
    s.default_value, s.subtype = 0.03, "DISTANCE"
    io.new_socket("Geometry", in_out="OUTPUT", socket_type="NodeSocketGeometry")
    N, L = ng.nodes, ng.links
    gi, go = N.new("NodeGroupInput"), N.new("NodeGroupOutput")

    def surface(sock_name):
        info = N.new("GeometryNodeObjectInfo")
        info.transform_space = "RELATIVE"
        L.new(gi.outputs[sock_name], info.inputs["Object"])
        return info.outputs["Geometry"]

    def nearest(geo):
        """(nearest position, its normal, signed height of the point above it, distance to it) on a mesh."""
        pos = N.new("GeometryNodeInputPosition").outputs[0]
        prox = N.new("GeometryNodeProximity")
        prox.target_element = "FACES"
        L.new(geo, prox.inputs["Geometry"])
        L.new(pos, prox.inputs["Sample Position"])
        sn = N.new("GeometryNodeSampleNearestSurface")
        sn.data_type = "FLOAT_VECTOR"
        L.new(geo, sn.inputs["Mesh"])
        L.new(N.new("GeometryNodeInputNormal").outputs["Normal"], sn.inputs["Value"])
        L.new(pos, sn.inputs["Sample Position"])
        d = N.new("ShaderNodeVectorMath")
        d.operation = "SUBTRACT"
        L.new(pos, d.inputs[0])
        L.new(prox.outputs["Position"], d.inputs[1])
        dot = N.new("ShaderNodeVectorMath")
        dot.operation = "DOT_PRODUCT"
        L.new(d.outputs["Vector"], dot.inputs[0])
        L.new(sn.outputs["Value"], dot.inputs[1])
        return prox.outputs["Position"], sn.outputs["Value"], dot.outputs["Value"], prox.outputs["Distance"]

    def under_it(height, dist):
        """True where a point is really under the surface: within Reach of it, and its offset from the
        nearest point mostly against the normal (height <= -dist / 2, give or take Margin). A point off
        to the side of an open rim or a horn's base has a small height but a large distance."""
        within = N.new("FunctionNodeCompare")
        within.data_type, within.operation = "FLOAT", "LESS_THAN"
        L.new(dist, within.inputs["A"])
        L.new(gi.outputs["Reach"], within.inputs["B"])
        half = N.new("ShaderNodeMath")
        half.operation = "MULTIPLY_ADD"  # dist * 0.5 + height
        L.new(dist, half.inputs[0])
        half.inputs[1].default_value = 0.5
        L.new(height, half.inputs[2])
        along = N.new("FunctionNodeCompare")
        along.data_type, along.operation = "FLOAT", "LESS_EQUAL"
        L.new(half.outputs[0], along.inputs["A"])
        L.new(gi.outputs["Margin"], along.inputs["B"])
        both = N.new("FunctionNodeBooleanMath")
        both.operation = "AND"
        L.new(within.outputs[0], both.inputs[0])
        L.new(along.outputs[0], both.inputs[1])
        return both.outputs[0]

    def lifted(near, nor):
        sc = N.new("ShaderNodeVectorMath")
        sc.operation = "SCALE"
        L.new(nor, sc.inputs[0])
        L.new(gi.outputs["Margin"], sc.inputs["Scale"])
        add = N.new("ShaderNodeVectorMath")
        add.operation = "ADD"
        L.new(near, add.inputs[0])
        L.new(sc.outputs["Vector"], add.inputs[1])
        return add.outputs["Vector"]

    def push(geo_in, solid):
        """Points of geo_in inside `solid` (by up to Reach) set back out to Margin over it."""
        near, nor, height, dist = nearest(solid)
        under = N.new("FunctionNodeCompare")
        under.data_type, under.operation = "FLOAT", "LESS_THAN"
        L.new(height, under.inputs["A"])
        L.new(gi.outputs["Margin"], under.inputs["B"])
        both = N.new("FunctionNodeBooleanMath")
        both.operation = "AND"
        L.new(under.outputs[0], both.inputs[0])
        L.new(under_it(height, dist), both.inputs[1])
        # not by an open edge (a bust's cut-off rim, an eye hole, a horn's buried base): there "under
        # the surface" means nothing, and a braid hanging past the rim would be pulled onto it
        en = N.new("GeometryNodeInputMeshEdgeNeighbors")
        one = N.new("FunctionNodeCompare")
        one.data_type, one.operation = "INT", "EQUAL"
        L.new(en.outputs["Face Count"], one.inputs["A"])
        one.inputs["B"].default_value = 1
        on_edge = N.new("GeometryNodeFieldOnDomain")
        on_edge.domain, on_edge.data_type = "EDGE", "BOOLEAN"
        L.new(one.outputs[0], next(s for s in on_edge.inputs if s.enabled))
        rim = N.new("GeometryNodeSampleNearestSurface")
        rim.data_type = "FLOAT"
        L.new(solid, rim.inputs["Mesh"])
        L.new(next(s for s in on_edge.outputs if s.enabled), rim.inputs["Value"])
        L.new(N.new("GeometryNodeInputPosition").outputs[0], rim.inputs["Sample Position"])
        inner = N.new("FunctionNodeCompare")
        inner.data_type, inner.operation = "FLOAT", "LESS_THAN"
        L.new(rim.outputs["Value"], inner.inputs["A"])
        inner.inputs["B"].default_value = 0.001
        sel = N.new("FunctionNodeBooleanMath")
        sel.operation = "AND"
        L.new(both.outputs[0], sel.inputs[0])
        L.new(inner.outputs[0], sel.inputs[1])
        sp = N.new("GeometryNodeSetPosition")
        L.new(geo_in, sp.inputs["Geometry"])
        L.new(sel.outputs[0], sp.inputs["Selection"])
        L.new(lifted(near, nor), sp.inputs["Position"])
        return sp.outputs[0]

    head_only = surface("Head")
    # what else hair stays out of (horns, a helmet), in Avoid: its own pass, as where it sinks into the
    # head the nearest surface is the head's, and one test over both would miss the inside of a horn
    ci = N.new("GeometryNodeCollectionInfo")
    ci.transform_space = "RELATIVE"
    L.new(gi.outputs["Avoid"], ci.inputs["Collection"])
    real = N.new("GeometryNodeRealizeInstances")
    L.new(ci.outputs[0], real.inputs[0])
    has_avoid = N.new("GeometryNodeAttributeDomainSize")
    has_avoid.component = "MESH"
    L.new(real.outputs[0], has_avoid.inputs[0])
    any_avoid = N.new("FunctionNodeCompare")
    any_avoid.data_type, any_avoid.operation = "INT", "GREATER_THAN"
    L.new(has_avoid.outputs["Face Count"], any_avoid.inputs["A"])
    any_avoid.inputs["B"].default_value = 0
    first = push(gi.outputs["Geometry"], head_only)
    second = push(first, real.outputs[0])
    third = push(second, head_only)  # pushed out of a horn into the head: back out again
    skip = N.new("GeometryNodeSwitch")
    skip.input_type = "GEOMETRY"
    L.new(any_avoid.outputs[0], skip.inputs["Switch"])
    L.new(first, skip.inputs["False"])
    L.new(third, skip.inputs["True"])

    class _Out:  # the rest of the group takes its geometry from out.outputs[0]
        outputs = [skip.outputs[0]]
    out = _Out()
    # roots: onto the cap when there is one (else the head), whatever their height
    cap = surface("Cap")
    size = N.new("GeometryNodeAttributeDomainSize")
    size.component = "MESH"
    L.new(cap, size.inputs[0])
    has_cap = N.new("FunctionNodeCompare")
    has_cap.data_type, has_cap.operation = "INT", "GREATER_THAN"
    L.new(size.outputs["Face Count"], has_cap.inputs["A"])
    has_cap.inputs["B"].default_value = 0
    pick = N.new("GeometryNodeSwitch")
    pick.input_type = "GEOMETRY"
    L.new(has_cap.outputs[0], pick.inputs["Switch"])
    L.new(head_only, pick.inputs["False"])
    L.new(cap, pick.inputs["True"])
    rnear, rnor, _rh, rdist = nearest(pick.outputs[0])
    ends = N.new("GeometryNodeCurveEndpointSelection")
    ends.inputs["Start Size"].default_value = 1
    ends.inputs["End Size"].default_value = 0
    # only roots near it, by real distance: one far off (a braid started at its tie) is a blockout to fix
    close = N.new("FunctionNodeCompare")
    close.data_type, close.operation = "FLOAT", "LESS_THAN"
    L.new(rdist, close.inputs["A"])
    L.new(gi.outputs["Reach"], close.inputs["B"])
    root_sel = N.new("FunctionNodeBooleanMath")
    root_sel.operation = "AND"
    L.new(ends.outputs[0], root_sel.inputs[0])
    L.new(close.outputs[0], root_sel.inputs[1])
    roots = N.new("GeometryNodeSetPosition")
    L.new(out.outputs[0], roots.inputs["Geometry"])
    L.new(root_sel.outputs[0], roots.inputs["Selection"])
    L.new(lifted(rnear, rnor), roots.inputs["Position"])
    L.new(roots.outputs[0], go.inputs[0])
    return ng


def avoid_collection(objects, name="HairAvoid"):
    """A collection of the meshes hair must stay out of besides the head (horns, a helmet), for
    `avoid=`. Takes objects or names; returns the collection (not rendered, only referenced)."""
    coll = bpy.data.collections.get(name) or bpy.data.collections.new(name)
    for o in objects:
        o = _ob(o)
        if o.name not in coll.objects:
            coll.objects.link(o)
    return coll


def _keep_out(ob, head, cap=None, avoid=None):
    if head is None:
        return None
    vals = {"Head": _ob(head), "Margin": 0.0005, "Reach": 0.03}
    if cap is not None:
        vals["Cap"] = _ob(cap)
    if avoid is not None:
        vals["Avoid"] = bpy.data.collections[avoid] if isinstance(avoid, str) else avoid
    return _modifier(ob, keep_out_group(), vals, name="Keep Out Of Head")


def hair(name, collection=SHAPES, guides=10, points=24, fill=0.55, children=60, spread=0.0025, clump=0.55, clump_shape=0.3,
         tip_spread=0.0, trim=0.02, frizz=0.0006, noise=0.0012, noise_scale=40.0, curl=0.0, strand_radius=0.00006,
         material=None, parent=None, viewport=0.1, seed=0, cumulative=False, cap=None, avoid=None):
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
    Last before the profile, Keep Out Of Head (with `parent`, the head): each strand's root onto the
    hair cap (`cap`, from hair_cap()), and any point that frizz, noise or clumping pushed into the head
    back out of it. Tune them on the modifiers afterwards. Returns the hair object."""
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
    head = ob.parent if ob.parent is not None and ob.parent.type == "MESH" else None
    _keep_out(ob, head, cap, avoid)
    _modifier(ob, g["Set Hair Curve Profile"], {"Radius": strand_radius, "Shape": 0.5})
    if material:
        ob.data.materials.append(material)
    return ob


def strays(name, collection=SHAPES, amount=2, spread=0.006, frizz=0.004, noise=0.006, curl=0.004, trim=0.06, guides=6,
           points=24, strand_radius=0.000035, material=None, parent=None, seed=7, cap=None, avoid=None):
    """Loose hairs over the whole style: a sparse copy of the guides, pushed off the flow by strong
    frizz, noise and curl and cut to random lengths, so some strands leave their lock and end early."""
    return hair(name, collection, guides=guides, points=points, fill=0.9, children=amount, spread=spread, clump=0.0, trim=trim,
                frizz=frizz, noise=noise, noise_scale=12.0, curl=curl, strand_radius=strand_radius, material=material,
                parent=parent, seed=seed, cap=cap, avoid=avoid)


def baby_hair(name, surface, hairline_points, band=0.008, length=0.012, density=300000.0, flow=(0.0, 0.0, -1.0),
              fan=35.0, rise=0.25, frizz=None, curl=0.0, strand_radius=0.00003, material=None, seed=3, cap=None,
              avoid=None):
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
    on_cap = _ob(cap) if cap is not None else None
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
            if on_cap is not None and (_nearest_on(on_cap, root) - root).length > 0.001:
                continue  # hair grows only from the cap: none on the bare skin past the hairline
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
    most = 0.1 * length
    frizz = 0.06 * length if frizz is None else min(frizz, most)
    if frizz > 0:
        _modifier(ob, g["Frizz Hair Curves"], {"Factor": 1.0, "Distance": frizz, "Shape": 0.8, "Cumulative Offset": False, "Seed": seed})
    if curl > 0:  # each hair curls on its own: a shared guide winds every hair nearby round one point (starbursts)
        _modifier(ob, g["Curl Hair Curves"], {"Factor": 1.0, "Radius": min(curl, most), "Frequency": 1.0, "Guide Distance": 0.0002,
                                              "Seed": seed})
    _keep_out(ob, surf, cap, avoid)
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

def _solid(ob, dg):
    """(BVH in the object's space, world-to-object matrix, open-edge KD or None) for an inside test."""
    from mathutils.kdtree import KDTree as _KD
    import collections
    bvh = BVHTree.FromObject(ob, dg)
    use = collections.Counter(k for p in ob.data.polygons for k in p.edge_keys)
    dots = []  # along the open edges (a cut-off bust, eye holes, a horn's base): "inside" means nothing there
    for (a, b), k in use.items():
        if k == 1:
            pa, pb = ob.data.vertices[a].co, ob.data.vertices[b].co
            steps = max(1, int((pb - pa).length / 0.001))
            dots += [pa.lerp(pb, s / steps) for s in range(steps + 1)]
    rim = None
    if dots:
        rim = _KD(len(dots))
        for i, p in enumerate(dots):
            rim.insert(p, i)
        rim.balance()
    return bvh, ob.matrix_world.inverted(), rim


def report(hair_objects, surface=None, shapes=SHAPES, cap=None, avoid=None):
    """Counts and checks on the evaluated hair: strands, length range, how far roots are from the
    surface, and `backwards`: strands whose tip is nearer a lock's start than their root is (should
    be about 0). With the head as `surface`, `inside`: strands that go into it, or into anything in
    `avoid` (horns...), by more than 0.5 mm (must be 0: no hair through the model). With `cap`,
    `off_cap`: strands whose root is more than 1 mm off the hair cap (must be 0: hair grows only
    from the cap)."""
    dg = bpy.context.evaluated_depsgraph_get()
    bvh = mi = cap_bvh = cap_mi = None
    rim = None
    solids = []
    if surface:
        surf = bpy.data.objects[surface] if isinstance(surface, str) else surface
        bvh, mi, rim = _solid(surf, dg)
        solids.append((bvh, mi, rim))
    if avoid is not None:
        coll = bpy.data.collections[avoid] if isinstance(avoid, str) else avoid
        objs = coll.all_objects if isinstance(coll, bpy.types.Collection) else [_ob(o) for o in coll]
        solids += [_solid(o, dg) for o in objs if o.type == "MESH"]
    if cap is not None:
        c = _ob(cap)
        cap_bvh, cap_mi = BVHTree.FromObject(c, dg), c.matrix_world.inverted()
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
            lengths, gaps, backwards, checked, inside, deepest, off_cap = [], [], 0, 0, 0, 0.0, 0
            step = max(1, n // 4000)
            for c in range(0, n, step):
                a, b = offs[c], offs[c + 1]
                pts = [ob.matrix_world @ Vector(pos[i].vector) for i in range(a, b)]
                lengths.append(sum((q - p).length for p, q in zip(pts, pts[1:])))
                checked += 1
                if bvh:
                    gaps.append(bvh.find_nearest(mi @ pts[0])[3])
                if solids:
                    deep = 0.0
                    for p in pts[1:]:
                        for sb, smi, srim in solids:
                            loc, nor, _i, _d = sb.find_nearest(smi @ p)
                            s = (smi @ p - loc).dot(nor)
                            if s < -0.0005 and not (srim and srim.find(loc)[2] < 0.002):
                                deep = max(deep, -s)
                    if deep:
                        inside += 1
                        deepest = max(deepest, deep)
                if cap_bvh is not None and cap_bvh.find_nearest(cap_mi @ pts[0])[3] > 0.001:
                    off_cap += 1
                if kd:
                    backwards += kd.find(pts[-1])[2] < kd.find(pts[0])[2]
            if solids:
                info.update(inside=inside, inside_deepest_mm=round(deepest * 1000, 1))
            if cap_bvh is not None:
                info["off_cap"] = off_cap
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

def _expand(items):
    """Objects from collections, their names, object names or objects (one or a list)."""
    out = []
    for it in (items if isinstance(items, (list, tuple, set)) else [items]):
        if isinstance(it, str):
            it = bpy.data.collections.get(it) or bpy.data.objects[it]
        out += list(it.all_objects) if isinstance(it, bpy.types.Collection) else [it]
    return out


def _where(p, lo, hi):
    """Where p is on the head, in words (the face looks down -Y, up is +Z, her left is +X)."""
    c, h = (lo + hi) / 2, (hi - lo) / 2
    d = [(p[i] - c[i]) / max(h[i], 1e-6) for i in range(3)]
    words = ["top" if d[2] > 0.35 else "low" if d[2] < -0.35 else "middle height"]
    words.append("front" if d[1] < -0.3 else "back" if d[1] > 0.3 else "over the ear line")
    words.append("her left" if d[0] > 0.25 else "her right" if d[0] < -0.25 else "centre")
    return ", ".join(words)


def coverage(cap, hair=SHAPES, reach=0.03, gap=0.0015, name="hs_bare"):
    """How much of the hair cap no hair covers, seen from outside: from each point of the cap a line
    runs out along the scalp's normal for `reach` m, and the point is covered when that line passes
    through hair: a lock's or a braid's tube (centre line and radius), a mesh strip, or strands within
    `gap` m. Use it on the blockout, and again on the strands (thin hair counts low there).
    hair: collections or their names, or objects (hair objects count their evaluated strands).
    Writes a colour attribute `name` on the cap (red: bare) and makes it the active one, so a
    Workbench render coloured by Attribute shows where. Prints each bare patch over 1 cm2 and where it
    is on the head. Returns {"bare": share of the cap's area, "patches": [(cm2, centre, where)]}.
    Inside the hairline no scalp may show: aim for no patches at all."""
    from mathutils.kdtree import KDTree
    cap = _ob(cap)
    dg = bpy.context.evaluated_depsgraph_get()
    tubes, trad, strands, meshes = [], [], [], []
    for ob in _expand(hair):
        if ob == cap:
            continue
        mw = ob.matrix_world
        if ob.type == "CURVE":
            for sp in ob.data.splines:
                pts = ([(mw @ Vector(p.co[:3]), p.radius) for p in sp.points] if sp.type == "POLY" else
                       [(mw @ p.co, p.radius) for p in sp.bezier_points])
                for (a, ra), (b, rb) in zip(pts, pts[1:]):
                    n = max(1, int((b - a).length / 0.002))
                    for i in range(n):
                        t = i / n
                        tubes.append(a.lerp(b, t))
                        trad.append(ra + (rb - ra) * t)
                if pts:
                    tubes.append(pts[-1][0])
                    trad.append(pts[-1][1])
        elif ob.type == "CURVES":
            ev = ob.evaluated_get(dg).data
            pos = ev.attributes.get("position")
            if pos is None or not len(pos.data):
                continue
            a = np.empty(len(pos.data) * 3, np.float32)
            pos.data.foreach_get("vector", a)
            a = a.reshape(-1, 3) @ np.array(mw.to_3x3()).T + np.array(mw.translation)
            strands.append(a)
        elif ob.type == "MESH":
            meshes.append((BVHTree.FromObject(ob, dg), mw, mw.inverted()))
    kt = None
    if tubes:
        kt = KDTree(len(tubes))
        for i, p in enumerate(tubes):
            kt.insert(p, i)
        kt.balance()
    ks = None
    if strands:
        allp = np.concatenate(strands)
        ks = KDTree(len(allp))
        for i, p in enumerate(allp):
            ks.insert(p, i)
        ks.balance()
    ce = cap.evaluated_get(dg)
    me = ce.to_mesh()
    try:
        mw = cap.matrix_world
        nm = mw.inverted().transposed().to_3x3()
        vs = [mw @ v.co for v in me.vertices]
        ns = [(nm @ v.normal).normalized() for v in me.vertices]
        area = [0.0] * len(vs)
        for poly in me.polygons:
            ids = list(poly.vertices)
            ar = sum(((vs[ids[k]] - vs[ids[0]]).cross(vs[ids[k + 1]] - vs[ids[0]])).length / 2
                     for k in range(1, len(ids) - 1))
            for i in ids:
                area[i] += ar / len(ids)
        steps = [0.0005 + reach * k / 20 for k in range(21)]
        bare = []
        for p, n in zip(vs, ns):
            hit = False
            for s in steps:
                q = p + n * s
                if kt is not None and any(d <= trad[j] for _c, j, d in kt.find_n(q, 6)):
                    hit = True
                elif ks is not None and ks.find(q)[2] <= gap:
                    hit = True
                if hit:
                    break
            for bvh, bw, bi in meshes:
                if hit:
                    break
                o, e = bi @ (p + n * 0.0005), bi @ (p + n * reach)
                hit = bvh.ray_cast(o, (e - o).normalized(), (e - o).length)[0] is not None
            bare.append(not hit)
        # bare patches: bare points joined by edges
        parent = list(range(len(vs)))

        def root(i):
            while parent[i] != i:
                parent[i] = parent[parent[i]]
                i = parent[i]
            return i
        for e in me.edges:
            a, b = e.vertices
            if bare[a] and bare[b]:
                parent[root(a)] = root(b)
        groups = {}
        for i, b in enumerate(bare):
            if b:
                groups.setdefault(root(i), []).append(i)
        lo = Vector([min(v[k] for v in vs) for k in range(3)])
        hi = Vector([max(v[k] for v in vs) for k in range(3)])
        patches = []
        for ids in groups.values():
            ar = sum(area[i] for i in ids)
            if ar < 1e-4:
                continue
            c = sum((vs[i] * area[i] for i in ids), Vector()) / max(ar, 1e-12)
            patches.append((round(ar * 1e4, 1), tuple(round(x, 4) for x in c), _where(c, lo, hi)))
        patches.sort(key=lambda t: -t[0])
        total = sum(area) or 1.0
        share = sum(a for a, b in zip(area, bare) if b) / total
        if len(cap.data.vertices) == len(vs):  # colour the cap: red where bare
            ca = cap.data.color_attributes.get(name) or cap.data.color_attributes.new(name, "FLOAT_COLOR", "POINT")
            cols = np.array([(0.9, 0.08, 0.04, 1.0) if b else (0.18, 0.18, 0.2, 1.0) for b in bare], np.float32)
            ca.data.foreach_set("color", cols.ravel())
            cap.data.color_attributes.active_color = ca
    finally:
        ce.to_mesh_clear()
    print(f"COVERAGE {cap.name}: {share:.1%} of the scalp is bare, {len(patches)} patch(es) over 1 cm2")
    for ar, c, w in patches[:12]:
        print(f"  {ar:6.1f} cm2 at {c}: {w}")
    return {"bare": round(share, 4), "patches": patches}


def look(cams, out, collection=SHAPES, hair_objects=None, cap=None, head=None, color=(0.16, 0.12, 0.1), **hair_args):
    """What the blockout will look like as hair, beside the references. Tubes don't show it: a groove
    between two tubes, a ring of scalp, a crown in two halves all look fine as tubes and only show once
    they're strands. So look at this after every change to the blockout, and judge it, not the tubes.
    - Converts `collection` to strands with hair() (pass its settings as keywords, e.g. children=30),
      or takes `hair_objects` you've already converted.
    - Renders them from each ref_camera() camera in `cams` beside its reference picture: writes
      out/look_<camera>.png (reference | hair).
    - Runs coverage() on `cap`, if given, and adds it to the result.
    The temporary hair is removed again, and the tubes' preview is left as it was.
    Returns {"pictures": {camera: path}, "coverage": ...}."""
    os.makedirs(out, exist_ok=True)
    shown = [o for o in bpy.data.collections[collection].all_objects if o.type == "CURVE" and o.data.bevel_depth > 0]
    tmp = None
    if hair_objects is None:
        if shown:
            preview(collection, on=False)
        hair_args.setdefault("viewport", 1.0)
        tmp = hair("_hs_look", collection, cap=cap, parent=head, **hair_args)
        objs = [tmp]
    else:
        objs = _expand(hair_objects)
    pics = {}
    try:
        for cam in (cams if isinstance(cams, (list, tuple)) else [cams]):
            cam = _ob(cam)
            res = tuple(cam["hs_res"]) if "hs_res" in cam else (900, 900)
            shot = _flat(cam, {o.name: color for o in objs}, res, light="STUDIO", rest=(0.72, 0.7, 0.68))
            pic = shot[..., :3] * shot[..., 3:4] + (1.0 - shot[..., 3:4])
            panels = [pic]
            if "hs_ref" in cam and os.path.exists(cam["hs_ref"]):
                r = _load(cam["hs_ref"])[..., :3]
                if r.shape[:2] == pic.shape[:2]:
                    panels = [r, np.ones((pic.shape[0], 8, 3), np.float32), pic]
            s = max(1, math.ceil(pic.shape[0] / 900))
            path = os.path.join(out, f"look_{cam.name}.png")
            _save(np.concatenate([p[::s, ::s] for p in panels], 1), path)
            pics[cam.name] = path
        cov = coverage(cap, objs) if cap is not None else None
    finally:
        if tmp is not None:
            data = tmp.data
            bpy.data.objects.remove(tmp)
            bpy.data.hair_curves.remove(data)
        if shown:
            preview(collection, on=True)
    print("LOOK: " + ", ".join(pics.values()))
    return {"pictures": pics, "coverage": cov}


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
    if not surf.data.uv_layers:
        raise ValueError(f"{surf.name} needs a UV map")
    cv = ob.data
    cv.surface = surf
    cv.surface_uv_map = surf.data.uv_layers.active.name
    n = len(cv.curves)
    offs = [0] * (n + 1)
    cv.curve_offset_data.foreach_get("value", offs)
    pos = [0.0] * (len(cv.points) * 3)
    cv.attributes["position"].data.foreach_get("vector", pos)
    m = ob.matrix_world
    out = _root_uvs([m @ Vector(pos[offs[c] * 3:offs[c] * 3 + 3]) for c in range(n)], surf)
    attr = cv.attributes.get("surface_uv_coordinate") or cv.attributes.new("surface_uv_coordinate", "FLOAT2", "CURVE")
    attr.data.foreach_set("vector", [x for u in out for x in u])
    return ob


def _root_uvs(roots, surface):
    """Each world-space root's UV on the surface, from the nearest point on it."""
    from mathutils.interpolate import poly_3d_calc
    me = surface.data
    me.calc_loop_triangles()
    uv = me.uv_layers.active.data
    sw = surface.matrix_world
    verts = [sw @ v.co for v in me.vertices]
    tris = list(me.loop_triangles)
    bvh = BVHTree.FromPolygons(verts, [t.vertices[:] for t in tris])
    out = []
    for p in roots:
        loc, _nor, t, _d = bvh.find_nearest(p)
        tri = tris[t]
        w = poly_3d_calc([verts[v] for v in tri.vertices], loc)
        u = sum((uv[l].uv * k for l, k in zip(tri.loops, w)), Vector((0, 0)))
        out.append((u.x, u.y))
    return out


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
    sock = getattr(md.properties.inputs, ids["Viewport Amount"])
    va = sock.value
    sock.value = 1.0  # counted at render density, then put back
    ob.update_tag()
    n = len(ob.evaluated_get(bpy.context.evaluated_depsgraph_get()).data.curves)
    sock.value = va
    ob.update_tag()
    return n


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


def _curves_of(ob, dg, scale, tolerance, max_points, surface):
    """One object's evaluated curves for export: world positions x scale, thinned points, radii x
    scale, and each curve's root UV on `surface` (or from its own surface_uv_coordinate)."""
    cv = ob.evaluated_get(dg).data
    n, npts = len(cv.curves), len(cv.points)
    if not n:
        return [], [], [], []
    offs = np.zeros(n + 1, np.int32)
    cv.curve_offset_data.foreach_get("value", offs)
    pos = np.zeros(npts * 3, np.float32)
    cv.attributes["position"].data.foreach_get("vector", pos)
    pos = pos.reshape(-1, 3).astype(np.float64)
    m = np.array(ob.matrix_world, np.float64)
    world = pos @ m[:3, :3].T + m[:3, 3]
    pos = world * scale
    rad = np.zeros(npts, np.float32)
    if "radius" in cv.attributes:
        cv.attributes["radius"].data.foreach_get("value", rad)
    if "surface_uv_coordinate" in cv.attributes:
        uv = np.zeros(n * 2, np.float32)
        cv.attributes["surface_uv_coordinate"].data.foreach_get("vector", uv)
        uvs = uv.reshape(-1, 2).tolist()
    elif surface is not None and surface.data.uv_layers:
        uvs = _root_uvs([Vector(world[offs[c]]) for c in range(n)], surface)
    else:
        uvs = [(0.0, 0.0)] * n
    sizes, flat, rads = [], [], []
    for c in range(n):
        a, b = offs[c], offs[c + 1]
        k = _simplify(pos[a:b], tolerance * scale)
        if len(k) > max_points:  # evenly along it, ends kept
            k = np.asarray(k)[np.round(np.linspace(0, len(k) - 1, max_points)).astype(int)]
        sizes.append(len(k))
        flat.append(pos[a:b][k])
        rads.append(rad[a:b][k] * scale)
    return sizes, flat, rads, uvs


def export_groom(objects, path, scale=100.0, frame=None, tolerance=0.0002, max_points=64, merge=True, guides=True,
                 surface=None, max_guides=2000):
    """Exports hair as an Alembic groom for Unreal, to Epic's "Alembic for Grooms" schema:
      - one curves object, every strand tagged with `groom_group_id` (int, per curve): a group per
        material with `merge` (Unreal gives each group its own material slot, at most 15, and every
        group costs its own passes), else per object;
      - `groom_root_uv` (float2, per curve): each root's UV on the head, from the hair's
        surface_uv_coordinate (after attach or bake) or else from `surface`, its parent head or its
        own surface. Without it Unreal invents a spherical projection, and materials that fade root to
        tip or vary across the scalp come out wrong;
      - `groom_width` (per point, the diameter) besides the native widths, and `groom_version_major`/
        `groom_version_minor` 1/5 and `groom_tool` on the object;
      - with `guides`, the live objects' guides (one per lock edge, ring and centre line) go in too,
        thinned evenly to `max_guides` in all, tagged `groom_guide` = 1, for Unreal's hair simulation;
        without them it picks some itself;
      - each strand keeps only the points it needs to stay within `tolerance` m of its shape (0.2 mm: a
        straight strand drops to a few points, a braid keeps its weave), at most `max_points` (Unreal cuts
        curves past 255). Roots first, all in centimetres (`scale` 100 from metres), transforms baked in,
        so the character needn't be at the origin.
    Takes baked objects or live ones (their evaluated hair, at render density; nothing unapplied reaches
    the file). Only curves are exported: the hair cap stays in Blender. Unreal: import the .abc as a
    Groom with its default conversion settings, then bind it to the head's skeletal mesh and set up
    LODs and physics there. Prints what it wrote, per group, against Epic's MetaHuman budget."""
    obs = [_ob(o) for o in (objects if isinstance(objects, (list, tuple)) else [objects])]
    clean = [ob.name.replace(" (baked)", "").replace(" (guides)", "") for ob in obs]
    groups = {}
    for ob, name in zip(obs, clean):
        key = (ob.data.materials[0].name if ob.data.materials and ob.data.materials[0] else name) if merge else name
        groups.setdefault(key, []).append((ob, name))
    if len(groups) > 15:
        print(f"GROOM: {len(groups)} groups; Unreal takes at most 15 per groom. Merge sections that share a look.")
    vl = bpy.context.view_layer
    picked, active = [o for o in vl.objects if o.select_get()], vl.objects.active
    renders = {}
    for ob in obs:  # export at render density: lift the viewport share for the evaluation
        for md, ids in _duplicates(ob):
            sock = getattr(md.properties.inputs, ids["Viewport Amount"])
            renders[(ob.name, md.name)] = sock.value
            sock.value = 1.0
    stats, temp = [], None

    def surf_of(ob):
        s = surface or getattr(ob.data, "surface", None) or (ob.parent if ob.parent and ob.parent.type == "MESH" else None)
        return _ob(s) if s is not None else None

    try:
        dg = bpy.context.evaluated_depsgraph_get()
        sizes, flat, rads, uvs, gid, guide, mats, guide_parts = [], [], [], [], [], [], [], []
        for g, (key, members) in enumerate(groups.items()):
            n0, p0 = len(sizes), sum(sizes)
            for ob, _name in members:
                s, f, r, u = _curves_of(ob, dg, scale, tolerance, max_points, surf_of(ob))
                sizes += s
                flat += f
                rads += r
                uvs += u
                gid += [g] * len(s)
                guide += [0] * len(s)
                if guides and ob.modifiers and ob.modifiers[0].name == "Shapes to Hair" and _duplicates(ob):
                    # the guides alone: everything after the conversion switched off for a moment
                    shown = [(m, m.show_viewport) for m in ob.modifiers[1:]]
                    for m, _v in shown:
                        if m.name != "Keep Out Of Head":
                            m.show_viewport = False
                    dg.update()
                    s, f, _r, u = _curves_of(ob, dg, scale, tolerance, max_points, surf_of(ob))
                    for m, v in shown:
                        m.show_viewport = v
                    dg.update()
                    guide_parts.append((g, s, f, u))
                for mat in ob.data.materials:
                    if mat and mat not in mats:
                        mats.append(mat)
            stats.append([g, key, len(sizes) - n0, 0, sum(sizes) - p0, max(sizes[n0:]) if len(sizes) > n0 else 0,
                          [o.name for o, _n in members]])
        # the guides, thinned evenly to at most max_guides in all: they only drive the simulation
        total = sum(len(s) for _g, s, _f, _u in guide_parts)
        step = max(1, -(-total // max_guides))
        for g, s, f, u in guide_parts:
            keep = range(0, len(s), step)
            sizes += [s[i] for i in keep]
            flat += [f[i] for i in keep]
            rads += [np.full(len(f[i]), 0.00006 * scale, np.float32) for i in keep]
            uvs += [u[i] for i in keep]
            gid += [g] * len(keep)
            guide += [1] * len(keep)
            stats[g][3] += len(keep)
        data = bpy.data.hair_curves.new("Groom")
        data.add_curves(sizes)
        data.attributes["position"].data.foreach_set("vector", np.concatenate(flat).astype(np.float32).ravel())
        radius = np.concatenate(rads).astype(np.float32)
        (data.attributes.get("radius") or data.attributes.new("radius", "FLOAT", "POINT")).data.foreach_set("value", radius)
        data.attributes.new("groom_width", "FLOAT", "POINT").data.foreach_set("value", radius * 2)
        data.attributes.new("groom_group_id", "INT", "CURVE").data.foreach_set("value", gid)
        data.attributes.new("groom_guide", "INT", "CURVE").data.foreach_set("value", guide)
        data.attributes.new("groom_root_uv", "FLOAT2", "CURVE").data.foreach_set("vector", [x for u in uvs for x in u])
        for mat in mats:
            data.materials.append(mat)
        temp = bpy.data.objects.new("Groom", data)
        temp["groom_version_major"], temp["groom_version_minor"] = 1, 5
        temp["groom_tool"] = "Agent Orange hair_shapes"
        bpy.context.scene.collection.objects.link(temp)
        for o in vl.objects:
            o.select_set(o == temp)
        vl.objects.active = temp
        f = bpy.context.scene.frame_current if frame is None else frame
        bpy.ops.wm.alembic_export(filepath=os.path.abspath(path), selected=True, global_scale=1.0, start=f, end=f,
                                  uvs=False, normals=False, export_hair=False, export_particles=False,
                                  export_custom_properties=True)
    finally:
        if temp is not None:
            data = temp.data
            bpy.data.objects.remove(temp)
            bpy.data.hair_curves.remove(data)
        for ob in obs:
            for md, ids in _duplicates(ob):
                if (ob.name, md.name) in renders:
                    getattr(md.properties.inputs, ids["Viewport Amount"]).value = renders[(ob.name, md.name)]
        for o in picked:
            o.select_set(True)
        vl.objects.active = active
    strands = sum(s[2] for s in stats)
    points = sum(s[4] for s in stats)
    print(f"GROOM {path}: {len(stats)} groups, {strands} strands (+{sum(s[3] for s in stats)} guides), "
          f"{points} points ({points / max(1, strands):.1f} a strand)")
    for g, key, n, ng, p, most, members in stats:
        print(f"  group {g} ({key}): {n} strands, {ng} guides, {p} points, at most {most} on one; {', '.join(members)}")
    if strands > GAME_STRANDS * 1.2:
        print(f"  Over a game budget: Epic's MetaHuman head hair is {GAME_STRANDS} strands at LOD0. game_budget() cuts it.")
    return {"path": path, "groups": len(stats), "strands": strands, "points": points,
            "group_names": {g: key for g, key, *_r in stats}}


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
    # the hair cap first: a hairline all round, high over the face (-Y), low at the back (+Y)
    ring = [sph(0.45 + 1.45 * (0.5 + 0.5 * math.sin(a)), a, R) for a in [k * 2 * math.pi / 48 for k in range(48)]]
    cap = hair_cap(head, ring, color=(0.01, 0.008, 0.008))
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
    down = tie_pt + Vector((0.0, -0.01, -0.05))  # down past the head, not into it
    end = tie_pt + Vector((0.0, -0.06, -0.15))
    braid("Braid", [tie_pt, down, end], width=(0.022, 0.013), turns=4, tail_locks=6,
          tail=[end, end + Vector((0, -0.005, -0.03)), end + Vector((0, -0.006, -0.06))], root=cap)
    tie("Tie2", end, (0, -0.15, -1), radius=0.006)
    tie("Tie", tie_pt, (0, -0.4, -1), radius=0.008)
    # a mesh strip with its root creased, behind the other ear
    strip("Strip", [sph(0.5, -0.4 + k * 0.08, R * 0.985) for k in range(5)],
          [sph(1.5, -0.4 + k * 0.08, R * 1.05) + Vector((0, 0, -0.03)) for k in range(5)], rows=20)

    print("TO CAP", to_cap(cap, SHAPES))
    preview(on=True, material=hair_material("M_Blockout", ((0.0, (0.2, 0.2, 0.25)),)))
    _render(out_dir, "blockout")
    preview(on=False)
    bpy.data.collections[SHAPES].hide_render = True

    mat = hair_material("M_Hair", ((0.0, (0.01, 0.008, 0.008)), (0.55, (0.012, 0.009, 0.010)), (1.0, (0.18, 0.03, 0.30))))
    main = hair("Hair", material=mat, parent=head, cap=cap)
    loose = strays("Strays", material=mat, parent=head, cap=cap)
    baby = baby_hair("BabyHair", head, ring[:25], material=mat, cap=cap)
    rep = report([main, loose, baby], head, cap=cap)
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
