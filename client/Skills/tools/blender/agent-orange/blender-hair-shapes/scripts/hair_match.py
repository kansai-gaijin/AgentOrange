"""Measure hair against its reference pictures, so matching them is numbers as well as an eye.

  ref_camera(ref, head, az, ...)     an orthographic camera fitted to a reference picture drawn or
                                     rendered on this same head: its framing found from the bare head
  measure(cam)                       the reference's hair in metres: top, outer edges, bang line and
                                     hairline, lowest points; what to build the blockout to
  compare(cam, hair, out, ...)       renders the hair's outline from that camera and measures it against
                                     the hair in the reference: where it's missing or extra, the outer
                                     edges, the bang and hairline edge, how solid, and with a render,
                                     the detail (clumps) and colour

hair_shapes imports them, so hs.ref_camera, hs.measure and hs.compare work too. Needs Blender 5.3 (Workbench
renders the outlines) and numpy, which Blender ships.
"""
import math
import os
import tempfile

import bpy
import numpy as np
from mathutils import Vector

FIT = 320  # the reference is fitted at about this many pixels across: fast, and well under a millimetre on a head


# ---------------------------------------------------------------- pictures

def _load(path):
    """A picture as floats 0..1, top row first, (height, width, 4)."""
    img = bpy.data.images.load(os.path.abspath(path), check_existing=False)
    try:
        w, h = img.size
        a = np.empty(w * h * 4, np.float32)
        img.pixels.foreach_get(a)
    finally:
        bpy.data.images.remove(img)
    return np.ascontiguousarray(a.reshape(h, w, 4)[::-1])


def _save(rgb, path):
    h, w = rgb.shape[:2]
    img = bpy.data.images.new("_hs_picture", w, h, alpha=False)
    try:
        a = np.ones((h, w, 4), np.float32)
        a[..., :3] = np.clip(rgb, 0, 1)
        img.pixels.foreach_set(np.ascontiguousarray(a[::-1]).ravel())
        img.filepath_raw = os.path.abspath(path)
        img.file_format = "PNG"
        img.save()
    finally:
        bpy.data.images.remove(img)
    return path


def _background(a):
    """Pixels that are the picture's background: the colour of its border, or transparent."""
    rgb = a[..., :3]
    edge = np.concatenate([rgb[0], rgb[-1], rgb[:, 0], rgb[:, -1]])
    bg = np.abs(rgb - np.median(edge, 0)).max(-1) < 0.07
    return bg | (a[..., 3] < 0.5)


def _grow(m, r):
    out = m.copy()
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            if (dx or dy) and dx * dx + dy * dy <= r * r:
                out |= np.roll(np.roll(m, dy, 0), dx, 1)
    return out


def _thin(m, r):
    return ~_grow(~m, r)


def _hair(a, bg, colors=None, tol=0.12):
    """The reference's hair: dark pixels, greys (skin is warm, even in shadow), or blue-to-purple
    dye, unless you give its colours (sRGB, 0..1 or 0..255, picked from the reference: highlight,
    mid, shadow and any dye)."""
    rgb = a[..., :3]
    if colors:
        c = np.array(colors, np.float32).reshape(-1, 3)
        if c.max() > 1:
            c = c / 255.0
        m = np.zeros(rgb.shape[:2], bool)
        for col in c:
            m |= np.linalg.norm(rgb - col, axis=-1) < tol
    else:
        r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
        lum = 0.2126 * r + 0.7152 * g + 0.0722 * b
        mx, mn = rgb.max(-1), rgb.min(-1)
        sat = (mx - mn) / np.maximum(mx, 1e-3)
        grey = (np.abs(r - g) < 0.05) & (np.abs(g - b) < 0.06) & (lum < 0.6)  # skin is warm, even in shadow
        m = (lum < 0.26) | grey | ((sat > 0.25) & (b > g + 0.04) & (b > 0.6 * r))
    m &= ~bg
    return _grow(_thin(m, 1), 1)  # specks out


def _blur(x, r):
    """A box blur of radius r pixels."""
    k = 2 * r + 1
    p = np.pad(x, r, mode="edge").cumsum(0).cumsum(1)
    p = np.pad(p, ((1, 0), (1, 0)))
    return (p[k:, k:] - p[:-k, k:] - p[k:, :-k] + p[:-k, :-k]) / (k * k)


def _hex(c):
    return "#%02x%02x%02x" % tuple(int(round(max(0, min(1, v)) * 255)) for v in c)


# ---------------------------------------------------------------- scene

def _objects(items):
    items = items if isinstance(items, (list, tuple, set)) else [items]
    out = []
    for it in items:
        if isinstance(it, str):
            if it in bpy.data.objects:
                out.append(bpy.data.objects[it])
            elif it in bpy.data.collections:
                out += list(bpy.data.collections[it].all_objects)
            else:
                raise KeyError(f"no object or collection called {it}")
        elif isinstance(it, bpy.types.Collection):
            out += list(it.all_objects)
        else:
            out.append(it)
    return out


def _aim(cam, az, elev, target, dist):
    a, e = math.radians(az), math.radians(elev)
    d = Vector((math.sin(a) * math.cos(e), -math.cos(a) * math.cos(e), math.sin(e)))
    cam.location = Vector(target) + d * dist
    cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()


def _mask(cam, white, res, alone=False):
    """How much of each pixel the `white` objects cover from `cam`, 0..1. Every other object that
    renders stays in, in black, so it hides what's behind it (the head hides the hair at the back),
    unless `alone`."""
    a = _flat(cam, {o.name: (1.0, 1.0, 1.0) for o in white}, res, alone)
    return a[..., 0] * a[..., 3]


def _flat(cam, colors, res, alone=False, light="FLAT", rest=(0.0, 0.0, 0.0)):
    """A Workbench render from `cam`, each object named in `colors` in its colour, every other object
    in `rest` (or hidden if `alone`). RGBA, top row first."""
    sc = bpy.context.scene
    r, sh, vs, im = sc.render, sc.display.shading, sc.view_settings, sc.render.image_settings
    keep = [(r, "engine"), (r, "resolution_x"), (r, "resolution_y"), (r, "resolution_percentage"), (r, "filepath"),
            (r, "film_transparent"), (sc, "camera"), (sh, "light"), (sh, "color_type"), (sh, "show_shadows"),
            (sh, "show_cavity"), (sh, "show_object_outline"), (sh, "show_specular_highlight"), (sh, "show_xray"),
            (vs, "view_transform"), (vs, "look"), (vs, "exposure"), (vs, "gamma"), (im, "file_format"),
            (im, "color_mode"), (im, "color_depth"), (sc.display, "render_aa")]
    saved = [(o, k, getattr(o, k)) for o, k in keep]
    names = set(colors)
    objs = {o.name: (o.hide_render, tuple(o.color)) for o in sc.objects}
    cols = {}
    for o in sc.objects:
        if o.name not in names:
            continue
        for c in o.users_collection:
            if c.name in bpy.data.collections:  # not the scene's own collection
                cols.setdefault(c.name, c.hide_render)
                c.hide_render = False
    path = os.path.join(tempfile.gettempdir(), f"hs_mask_{os.getpid()}.png")
    try:
        for o in sc.objects:
            if o.name in names:
                o.hide_render = False
            elif alone:
                o.hide_render = True
            o.color = (*colors[o.name], 1) if o.name in names else (*rest, 1)
        r.engine = "BLENDER_WORKBENCH"
        sh.light, sh.color_type = light, "OBJECT"
        sh.show_shadows = sh.show_cavity = sh.show_object_outline = sh.show_specular_highlight = sh.show_xray = False
        vs.view_transform, vs.look, vs.exposure, vs.gamma = "Standard", "None", 0.0, 1.0
        r.film_transparent = True
        im.file_format, im.color_mode, im.color_depth = "PNG", "RGBA", "8"
        sc.display.render_aa = "8"
        r.resolution_x, r.resolution_y, r.resolution_percentage = int(res[0]), int(res[1]), 100
        sc.camera, r.filepath = cam, path
        bpy.ops.render.render(write_still=True)
        a = _load(path)
    finally:
        for o, k, v in saved:
            setattr(o, k, v)
        for o in sc.objects:
            if o.name in objs:
                o.hide_render, o.color = objs[o.name][0], objs[o.name][1]
        for n, v in cols.items():
            bpy.data.collections[n].hide_render = v
        if os.path.exists(path):
            os.remove(path)
    return a


# ---------------------------------------------------------------- fitting a camera to a reference

def _fit(head, skin, bg):
    """Scale k and shift t (pixels) that lay the bare head's outline on the reference: every skin
    pixel inside the head, and none of the head over the background. Hair is left out of it, as the
    hair in the reference is what's being matched."""
    def level(st):
        sk, hd, bk = skin[::st, ::st], head[::st, ::st], bg[::st, ::st]
        ys, xs = np.mgrid[0:sk.shape[0], 0:sk.shape[1]]
        cx, cy = sk.shape[1] / 2, sk.shape[0] / 2
        ns = max(1, sk.sum())

        def warp(k, tx, ty):  # shifts in full fit-size pixels
            px = np.floor((xs - cx - tx / st) / k + cx).astype(np.int32)
            py = np.floor((ys - cy - ty / st) / k + cy).astype(np.int32)
            ok = (px >= 0) & (px < hd.shape[1]) & (py >= 0) & (py < hd.shape[0])
            out = np.zeros(sk.shape, bool)
            out[ok] = hd[py[ok], px[ok]]
            return out

        def err(k, tx, ty):
            h = warp(k, tx, ty)
            return (sk & ~h).sum() / ns + (h & bk).sum() / max(1, h.sum())
        return warp, err

    size = skin.shape[1]
    _w, err = level(4)  # coarse: any scale from half to double, any shift up to a third of the picture
    best = (9.0, 1.0, 0.0, 0.0)
    for k in np.geomspace(0.5, 2.0, 17):
        for tx in np.linspace(-0.33, 0.33, 17) * size:
            for ty in np.linspace(-0.33, 0.33, 17) * size:
                e = err(k, tx, ty)
                if e < best[0]:
                    best = (e, k, tx, ty)
    warp, err = level(1)
    best = (err(*best[1:]), *best[1:])
    for dk, dt in ((0.06, 0.03), (0.02, 0.01), (0.006, 0.003), (0.002, 0.001)):
        _e, k0, tx0, ty0 = best
        for k in k0 * (1 + np.linspace(-dk, dk, 7)):
            for tx in tx0 + np.linspace(-dt, dt, 7) * size:
                for ty in ty0 + np.linspace(-dt, dt, 7) * size:
                    e = err(k, tx, ty)
                    if e < best[0]:
                        best = (e, k, tx, ty)
    return best, warp(*best[1:])


def ref_camera(ref, head, az=0.0, elev=0.0, search=0.0, name=None, hair_colors=None, out=None, extras=()):
    """An orthographic camera that sees `head` the way the reference picture `ref` does, for
    references drawn or rendered on this same head. az is the camera's angle round the head in
    degrees, for a face looking down -Y: 0 the front, 90 her left side (+X), 180 the back, -90 her
    right. Pictures called "45" are often nearer 50: give search=10 to try az +-10 and keep the best.
    The framing is fitted from the bare head: skin in the reference must fall inside it, and none of
    it may cover the background. Hair is left out, so the hair can be measured against it after.
    `extras`: other objects that are part of the bare head in the picture (horns, ears modelled apart).
    The camera keeps the reference with it, for compare(). `out`: a picture of the fit, the head's
    outline in green over the reference. Prints how good the fit is; a few % is good."""
    head = bpy.data.objects[head] if isinstance(head, str) else head
    bare_extras = [bpy.data.objects[o] if isinstance(o, str) else o for o in extras]  # horns...: part of the bare head
    a = _load(ref)
    H, W = a.shape[:2]
    bg = _background(a)
    skin = _thin(~bg & ~_hair(a, bg, hair_colors), 1)
    s = max(1, math.ceil(max(W, H) / FIT))
    skin_s, bg_s = skin[::s, ::s], bg[::s, ::s]
    fh, fw = skin_s.shape
    cs = [head.matrix_world @ Vector(c) for c in head.bound_box]
    target = sum(cs, Vector()) / 8
    size = max(max(c[i] for c in cs) - min(c[i] for c in cs) for i in range(3))
    scale0 = size * 1.5
    name = name or "Ref " + os.path.splitext(os.path.basename(ref))[0]
    cam = bpy.data.objects.get(name)
    if cam is None or cam.type != "CAMERA":
        cam = bpy.data.objects.new(name, bpy.data.cameras.new(name))
        bpy.context.scene.collection.objects.link(cam)
    cam.data.type, cam.data.sensor_fit = "ORTHO", "AUTO"
    cam.data.clip_start, cam.data.clip_end = 0.001, 100.0
    tried = {}

    def trial(angle):
        if angle not in tried:
            _aim(cam, angle, elev, target, size * 5)
            cam.data.ortho_scale = scale0
            m = _mask(cam, [head, *bare_extras], (fw, fh), alone=True) > 0.5
            tried[angle] = _fit(m, skin_s, bg_s)
        return tried[angle][0][0]

    angles = [az] if not search else [az + search * f for f in (-1, -0.5, 0, 0.5, 1)]
    best = min(angles, key=trial)
    if search:
        best = min([best + search * f for f in (-0.25, -0.125, 0, 0.125, 0.25)], key=trial)
    (e, k, tx, ty), warped = tried[best]
    e, k, tx, ty = float(e), float(k), float(tx), float(ty)
    _aim(cam, best, elev, target, size * 5)
    rot = cam.rotation_euler.to_matrix()
    u0 = scale0 / max(fw, fh)
    px, py = fw / 2 - tx / k, fh / 2 - ty / k
    off = (px - fw / 2) * u0 * rot.col[0] - (py - fh / 2) * u0 * rot.col[1]
    cam.location += off
    cam.data.ortho_scale = scale0 / k
    cam["hs_ref"], cam["hs_res"], cam["hs_target"] = os.path.abspath(ref), (W, H), tuple(target + off)
    cam["hs_az"], cam["hs_fit"], cam["hs_head"] = best, round(e, 4), head.name
    cam["hs_extras"] = ",".join(o.name for o in bare_extras)
    print(f"REF CAMERA {name}: az {best:.1f}, {cam.data.ortho_scale * 1000 / max(W, H):.2f} mm a pixel, "
          f"misfit {e * 100:.1f}% (skin outside the head + head over the background; a few % is good)")
    if out:
        pic = a[::s, ::s, :3].copy() * 0.8 + 0.2
        edge = warped & ~_thin(warped, 1)
        pic[edge] = (0.0, 0.85, 0.2)
        _save(np.repeat(np.repeat(pic, 2, 0), 2, 1), out)
    return cam


# ---------------------------------------------------------------- measuring the reference

def _reference(cam, ref=None, hair_colors=None):
    """The camera, the reference, its background and hair, its size, mm a pixel, and where a pixel is."""
    cam = bpy.data.objects[cam] if isinstance(cam, str) else cam
    ref = ref or cam.get("hs_ref")
    if not ref:
        raise ValueError("give ref=, or make the camera with ref_camera()")
    a = _load(ref)
    H, W = a.shape[:2]
    bg = _background(a)
    theirs = _hair(a, bg, hair_colors)
    head = bpy.data.objects.get(cam.get("hs_head", ""))
    if head is not None:  # whatever isn't background outside the bare head is hair, whatever its colour
        extra = [bpy.data.objects[n] for n in cam.get("hs_extras", "").split(",") if n in bpy.data.objects]
        bare = _grow(_mask(cam, [head, *extra], (W, H), alone=True) > 0.5, 1)
        theirs |= _grow(_thin(~bg & ~bare, 1), 1)
    if not theirs.any():
        raise ValueError(f"no hair found in {ref}: give hair_colors= picked from it")
    up = cam.data.ortho_scale / max(W, H)
    rot = cam.matrix_world.to_3x3()
    centre = Vector(cam["hs_target"]) if "hs_target" in cam else cam.matrix_world.translation

    def world(x, y):
        p = centre + ((float(x) + 0.5) - W / 2) * up * rot.col[0] - ((float(y) + 0.5) - H / 2) * up * rot.col[1]
        return "(%.3f, %.3f, %.3f)" % tuple(p)
    return cam, ref, a, bg, theirs, W, H, up, world


def _edge(m, x, gap):
    """Going down column x from the top of the hair, the first row where gap rows in a row are bare."""
    col = m[:, x]
    top = np.argmax(col)
    if not col[top]:
        return None
    run = 0
    for y in range(top, len(col)):
        run = 0 if col[y] else run + 1
        if run >= gap:
            return y - gap + 1
    return None


def _face_edge(theirs, bg, up):
    """(x, y) every 5 mm across where the reference's hair first ends on skin going down: the bang
    line and the hairline. Not braid ends (the lower 40% of the hair) or a part (under 1 cm of hair above)."""
    H = theirs.shape[0]
    gap, step = max(2, round(0.003 / up)), max(1, round(0.005 / up))
    yy, xx = np.where(theirs)
    y0, y1 = yy.min(), yy.max() + 1
    skin = ~bg & ~theirs
    out = []
    for x in range(xx.min() + step // 2, xx.max() + 1, step):
        et = _edge(theirs, x, gap)
        if et is None or et + gap >= H or not skin[et + gap, x]:
            continue
        if et > y0 + (y1 - y0) * 0.6 or et - np.argmax(theirs[:, x]) < 0.01 / up:
            continue
        out.append((x, et))
    return out


def measure(cam, ref=None, hair_colors=None):
    """The reference's hair in numbers, from a ref_camera() camera, to build the blockout to: the
    top of the hair, its outer edges every 2 cm down, where it first ends on skin (the bang line,
    the hairline), and its lowest points (braid and lock ends). Positions in metres on the plane
    through the head's middle, so in a front or back view x and z are exact, in a side view y and z.
    Prints and returns the lines."""
    cam, ref, a, bg, theirs, W, H, up, world = _reference(cam, ref, hair_colors)
    yy, xx = np.where(theirs)
    said = [f"{os.path.basename(ref)}, 1 pixel = {up * 1000:.2f} mm; left and right as in the picture."]
    said.append(f"Top of the hair: {world(xx[np.argmin(yy)], yy.min())}")
    said.append("Outer edges, every 2 cm down (left; right):")
    step = max(1, round(0.02 / up))
    for y in range(yy.min() + step // 2, yy.max() + 1, step):
        t = np.where(theirs[y])[0]
        if len(t):
            said.append(f"  {world(t[0], y)}; {world(t[-1], y)}")
    fe = _face_edge(theirs, bg, up)
    if fe:
        said.append("Where it first ends on skin going down (bang line, hairline), every 5 mm across:")
        said += ["  " + world(x, y) for x, y in fe]
    mid = (xx.min() + xx.max()) / 2
    for side, sel in (("left", xx < mid), ("right", xx >= mid)):
        if sel.any():
            i = np.argmax(np.where(sel, yy, -1))
            said.append(f"Lowest hair on the picture's {side}: {world(xx[i], yy[i])}")
    print("\n".join(said))
    return said


# ---------------------------------------------------------------- which section shows where

PALETTE = (("orange", (1.0, 0.45, 0.05)), ("blue", (0.1, 0.35, 1.0)), ("green", (0.15, 0.75, 0.2)),
           ("magenta", (0.9, 0.1, 0.75)), ("yellow", (1.0, 0.85, 0.05)), ("cyan", (0.05, 0.8, 0.9)),
           ("red", (0.9, 0.08, 0.08)), ("purple", (0.45, 0.15, 0.9)), ("brown", (0.5, 0.3, 0.1)),
           ("pink", (1.0, 0.6, 0.7)), ("teal", (0.0, 0.45, 0.4)), ("lime", (0.6, 1.0, 0.2)))


def sections(cam, groups, out, res=None):
    """Each section in its own colour from `cam`, beside its reference when the camera has one, to
    see which section shows where and which lies over which. groups: collections or their names (the
    blockout's sections, with preview on), or {label: objects} (hair objects after converting).
    Prints each section's colour and how much of the visible hair it is. Where the reference shows
    one section's hair, say the crown sweeping over the ears, its colour should be the one there."""
    cam = bpy.data.objects[cam] if isinstance(cam, str) else cam
    if isinstance(groups, dict):
        items = list(groups.items())
    else:
        items = [(g if isinstance(g, str) else g.name, g) for g in groups]
    if res is None:
        res = tuple(cam["hs_res"]) if "hs_res" in cam else (1000, 1000)
    colors, legend = {}, []
    for (label, g), (cname, rgb) in zip(items, PALETTE * 4):
        for o in _objects(g):
            colors[o.name] = rgb
        legend.append((label, cname, rgb))
    flat = _flat(cam, colors, res, rest=(0.75, 0.75, 0.75))
    shaded = _flat(cam, colors, res, light="STUDIO", rest=(0.75, 0.75, 0.75))
    seen = flat[..., 3] > 0.5
    total = 0
    counts = []
    for label, cname, rgb in legend:
        lin = np.array(rgb, np.float32)  # object colours are linear; the picture is in sRGB
        shown = np.where(lin <= 0.0031308, lin * 12.92, 1.055 * lin ** (1 / 2.4) - 0.055)
        n = int((seen & (np.abs(flat[..., :3] - shown).max(-1) < 0.08)).sum())
        counts.append(n)
        total += n
    said = [f"Sections from {cam.name} (share of the visible hair):"]
    said += [f"  {cname}: {label}, {n / max(1, total):.0%}" for (label, cname, _), n in zip(legend, counts)]
    pic = np.where(seen[..., None], shaded[..., :3], 1.0)
    panels = [pic]
    if "hs_ref" in cam and os.path.exists(cam["hs_ref"]):
        r = _load(cam["hs_ref"])[..., :3]
        if r.shape[:2] == pic.shape[:2]:
            panels = [r, np.ones((pic.shape[0], 8, 3), np.float32), pic]
    s = max(1, math.ceil(pic.shape[0] / 900))
    _save(np.concatenate([p[::s, ::s] for p in panels], 1), out)
    print("\n".join(said))
    return said


# ---------------------------------------------------------------- comparing

def compare(cam, hair, out, render=None, ref=None, hair_colors=None, tol=0.004):
    """Measures the hair against the reference, from a ref_camera() camera.
    hair: the hair objects, or collections (the blockout: "HairShapes", with preview on).
    render: optional, your render from the same camera (same framing) for a colour check.
    Writes `out`: the reference | yours | where they differ (red: hair in the reference that yours
    lacks; blue: yours where the reference has none; green dots: the reference's bang and hairline
    edge, yellow: yours). Prints and returns what it measured; differences under `tol` metres
    aren't listed."""
    cam, ref, a, bg, theirs, W, H, up, world = _reference(cam, ref, hair_colors)
    cover = _mask(cam, _objects(hair), (W, H))
    mine = cover > 0.4
    solid = _grow(_thin(mine, 1), 1)  # without single strays, for edges

    def mm(v):
        return "%+.0f" % (v * 1000)

    said = [f"Compared with {os.path.basename(ref)}: 1 pixel = {up * 1000:.2f} mm. Positions are in metres, on the plane "
            f"through the head's middle; + below means yours is further out, longer or lower."]
    res = {"mm_per_pixel": round(up * 1000, 3)}
    both, miss, extra = theirs & mine, theirs & ~mine, mine & ~theirs
    nt = theirs.sum()
    res.update(overlap=round(both.sum() / max(1, (theirs | mine).sum()), 3), missing=round(miss.sum() / nt, 3),
               extra=round(extra.sum() / nt, 3))
    said.append(f"Overlap {res['overlap']:.2f} (1 is a perfect match). Of the reference's hair, {res['missing']:.0%} is "
                f"missing from yours; yours covers another {res['extra']:.0%} where the reference has none.")
    inner = _thin(theirs, max(1, round(0.002 / up)))  # the reference's hair, away from its edges
    b = None
    if render:
        b = _load(render)[..., :3]
        if b.shape[:2] != (H, W):
            iy = (np.arange(H) * b.shape[0] / H).astype(int)
            ix = (np.arange(W) * b.shape[1] / W).astype(int)
            b = b[iy][:, ix]
        lw = np.array([0.2126, 0.7152, 0.0722], np.float32)
        rr = max(1, round(0.002 / up))
        fine_t = a[..., :3] @ lw - _blur(a[..., :3] @ lw, rr)  # light and dark across a few mm: clumps, gaps, strands
        fine_m = b @ lw - _blur(b @ lw, rr)
        mi = _thin(mine, 1)
    # where: a 3 x 3 grid over the reference's hair
    yy, xx = np.where(theirs)
    y0, y1, x0, x1 = yy.min(), yy.max() + 1, xx.min(), xx.max() + 1
    said.append("By part of the reference's hair (left and right as in the picture). Solid: how much of the reference's "
                "solid hair yours covers, from the coverage of every pixel, so thin, see-through hair counts low" +
                ("; detail: light and dark within a few mm (clumps, the gaps between them, strands), yours against the "
                 "reference's: under 0.7 reads as one smooth sheet." if render else "."))
    for i, rn in enumerate(("top", "middle", "bottom")):
        for j, cn in enumerate(("left", "centre", "right")):
            sl = (slice(y0 + (y1 - y0) * i // 3, y0 + (y1 - y0) * (i + 1) // 3),
                  slice(x0 + (x1 - x0) * j // 3, x0 + (x1 - x0) * (j + 1) // 3))
            n = theirs[sl].sum()
            if n < 0.02 * nt:
                if extra[sl].sum() * up * up > 1e-4:
                    said.append(f"  {rn} {cn}: little hair in the reference; yours covers {extra[sl].sum() * up * up * 1e4:.0f} cm²")
                continue
            line = f"  {rn} {cn}: {miss[sl].sum() / n:.0%} missing, {extra[sl].sum() / n:.0%} extra"
            if inner[sl].any():
                line += f", {cover[sl][inner[sl]].mean():.0%} solid"
            if b is not None and inner[sl].any() and mi[sl].any():
                ratio = fine_m[sl][mi[sl]].std() / max(1e-4, fine_t[sl][inner[sl]].std())
                line += f", detail {ratio:.1f}x"
                res[f"detail_{rn}_{cn}"] = round(float(ratio), 2)
            said.append(line)
    # outer edges, every centimetre of height
    step, rows, n = max(1, round(0.01 / up)), [], 0
    for y in range(y0 + step // 2, y1, step):
        t, m = np.where(theirs[y])[0], np.where(solid[y])[0]
        if not len(t):
            continue
        n += 1
        if not len(m):
            rows.append((1.0, f"  at {world(t[0], y)}: none of yours at this height"))
            continue
        dl, dr = (t[0] - m[0]) * up, (m[-1] - t[-1]) * up
        if abs(dl) > tol or abs(dr) > tol:
            rows.append((max(abs(dl), abs(dr)), f"  at {world((t[0] + t[-1]) / 2, y)}: left edge {mm(dl)} mm, right edge {mm(dr)} mm"))
    res["edges_off"] = f"{len(rows)} of {n}"
    said.append(f"Outer edge, every 1 cm of height: off by more than {tol * 1000:.0f} mm at {len(rows)} of {n} heights" +
                (". The worst, top to bottom:" if rows else "."))
    worst = sorted(rows, key=lambda r: -r[0])[:8]
    said += [r[1] for r in rows if r in worst]
    # where the hair first gives way to skin, going down: the bang line, the hairline
    gap, cstep = max(2, round(0.003 / up)), max(1, round(0.005 / up))
    lines, dots = [], []
    for x, et in _face_edge(theirs, bg, up):
        em = _edge(mine, x, gap)
        dots.append((x, et, em))
        if em is not None:
            lines.append(((em - et) * up, x, et))
    far = [v for v in lines if abs(v[0]) > 0.05]
    lines = [v for v in lines if abs(v[0]) <= 0.05]
    if dots:
        said.append(f"Where the hair first gives way to skin going down (the bang line, the hairline), at {len(dots)} "
                    f"places 5 mm apart:")
    if lines:
        d = sorted(v for v, _, _ in lines)
        res["bang_line_median_mm"] = round(d[len(d) // 2] * 1000, 1)
        said.append(f"  yours is {mm(d[len(d) // 2])} mm from the reference's at the median (+ lower, - higher).")
        worst = sorted(lines, key=lambda v: -abs(v[0]))[:4]
        said += [f"  {mm(v)} mm at {world(x, y)}" for v, x, y in sorted(worst, key=lambda v: v[1]) if abs(v) > tol]
    if len(dots) > len(lines):
        said.append(f"  at {len(dots) - len(lines)} of them yours has no edge within 5 cm "
                    f"(it carries on, or there's none of yours there).")
    # the hair just above that edge: the bangs' ends, where clumps and see-through show most
    near = np.zeros_like(theirs)
    for x, et, _em in dots:
        near[max(0, et - round(0.025 / up)): et, max(0, x - cstep // 2): x + cstep // 2 + 1] = True
    near &= inner
    if near.any():
        line = f"  the 2.5 cm of hair above it (the bangs' ends): {cover[near].mean():.0%} solid"
        if b is not None and (near & mi).any():
            ratio = fine_m[near & mi].std() / max(1e-4, fine_t[near].std())
            line += f", detail {ratio:.1f}x"
            res["detail_bangs"] = round(float(ratio), 2)
        res["solid_bangs"] = round(float(cover[near].mean()), 3)
        said.append(line)
    # see-through: skin or background showing through yours where the reference's hair is solid
    if inner.any():
        res["solid"] = round(float(cover[inner].mean()), 3)
        said.append(f"Solid: yours covers {res['solid']:.0%} of the reference's solid hair (100% is no skin or "
                    f"background showing through).")
    # colour
    if b is not None:
        ti = inner
        said.append("Colour of the hair (sRGB; L is lightness 0..1), top, middle and bottom third:")
        for i, rn in enumerate(("top", "middle", "bottom")):
            band = np.zeros_like(ti)
            band[y0 + (y1 - y0) * i // 3: y0 + (y1 - y0) * (i + 1) // 3] = True
            if not (ti & band).any() or not (mi & band).any():
                continue
            ct, cm = a[..., :3][ti & band].mean(0), b[mi & band].mean(0)
            lt, lm = (float(c @ np.array([0.2126, 0.7152, 0.0722])) for c in (ct, cm))
            word = "lighter" if lm > lt + 0.04 else "darker" if lm < lt - 0.04 else "about the same"
            res[f"lightness_{rn}"] = (round(lt, 3), round(lm, 3))
            said.append(f"  {rn}: reference {_hex(ct)} L {lt:.2f}, yours {_hex(cm)} L {lm:.2f}: {word}")
    # the picture
    s = max(1, math.ceil(H / 900))
    p1 = a[::s, ::s, :3]
    if render:
        p2 = b[::s, ::s]
    else:
        p2 = np.ones_like(p1) - cover[::s, ::s, None] * 0.85
    g = a[..., :3].mean(-1, keepdims=True) * 0.3 + 0.65
    p3 = np.repeat(g, 3, -1)
    p3[both] = (0.3, 0.3, 0.32)
    p3[miss] = (0.92, 0.15, 0.1)
    p3[extra] = (0.15, 0.4, 0.95)
    r = max(1, s)
    for x, et, em in dots:
        p3[max(0, et - r): et + r + 1, max(0, x - r): x + r + 1] = (0.0, 0.8, 0.2)
        if em is not None:
            p3[max(0, em - r): em + r + 1, max(0, x - r): x + r + 1] = (1.0, 0.85, 0.0)
    p3 = p3[::s, ::s]
    sep = np.ones((p1.shape[0], 8, 3), np.float32)
    _save(np.concatenate([p1, sep, p2, sep, p3], 1), out)
    res["said"] = "\n".join(said)
    print(res["said"])
    return res
