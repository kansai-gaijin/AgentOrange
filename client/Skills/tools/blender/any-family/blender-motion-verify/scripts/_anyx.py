"""_anyx - small shared helpers for the any-family scripts (Blender 5.2+, checked on 5.3).

Every copy in this pack's */scripts/ folders is the same file. The scripts follow the contract the
blender-agent-skills pack uses, so blender-core's run_in_blender.py runs them too:
  run(**kwargs) -> dict                          # live: import the module through execute_blender_code
  blender -b f.blend -P x.py -- --arg v          # headless: prints one line "@@JSON@@{...}"
"""
import argparse
import json
import os
import sys

import bpy

MARK = "@@JSON@@"


def argv():
    """Arguments after '--' (Blender ignores them)."""
    return sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def _default(o):
    if hasattr(o, "__len__") and not isinstance(o, str):
        try:
            return [round(v, 5) if isinstance(v, float) else v for v in o]
        except TypeError:
            pass
    if isinstance(o, (set, frozenset)):
        return sorted(o)
    return str(o)


def emit(data):
    """Print compact JSON on one marked line."""
    print(MARK + json.dumps(data, separators=(",", ":"), default=_default), flush=True)
    return data


def parser(doc):
    """ArgumentParser whose help is the script docstring's first line."""
    return argparse.ArgumentParser(description=(doc or "").strip().splitlines()[0])


def cli(run, p):
    """CLI entry: parse args after '--', run, emit JSON. Exits 1 on failure, or when the result says "pass": false
    and --strict was given."""
    p.add_argument("--strict", action="store_true", help="exit 1 when the result does not pass")
    args = vars(p.parse_args(argv()))
    strict = args.pop("strict")
    try:
        result = run(**args)
        emit(result)
        if strict and result.get("pass") is False and bpy.app.background:
            sys.exit(1)
    except SystemExit:
        raise
    except Exception as e:  # report, don't dump a traceback into the agent's context
        emit({"ok": False, "error": f"{type(e).__name__}: {e}"})
        if bpy.app.background:
            sys.exit(1)


def headless_only(what):
    """Scripts that open or reset files must not run inside a person's live session."""
    if not bpy.app.background:
        raise RuntimeError(f"{what} replaces the open file, so run it headless (blender -b ... -P), not in a live session")


def objects(names=None, collection=None, types=("MESH",)):
    """Names or a collection -> objects. Neither: the selected ones of `types`, else every visible one."""
    if names:
        missing = [n for n in names if n not in bpy.data.objects]
        if missing:
            raise KeyError(f"objects not found: {missing}")
        return [bpy.data.objects[n] for n in names]
    if collection:
        c = bpy.data.collections.get(collection)
        if c is None:
            raise KeyError(f"collection not found: {collection}")
        return [o for o in c.all_objects if not types or o.type in types]
    pool = [o for o in bpy.context.scene.objects if not types or o.type in types]
    sel = [o for o in pool if o.select_get()]
    return sel or [o for o in pool if o.visible_get()]


def path(p):
    """Absolute path; '//' is relative to the open .blend."""
    return os.path.abspath(bpy.path.abspath(p)) if p else p


def write_json(p, data):
    p = path(p)
    os.makedirs(os.path.dirname(p) or ".", exist_ok=True)
    with open(p, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1, default=_default)
    return p


def r(v, n=4):
    """Round a float or a vector for compact JSON."""
    if isinstance(v, float):
        return round(v, n)
    return [round(x, n) for x in v]


def save_png(pixels, w, h, p):
    """Write a float RGBA numpy array (h, w, 4), bottom row first as Blender stores it, to a PNG."""
    img = bpy.data.images.new("_anyx_out", w, h, alpha=True)
    try:
        img.pixels.foreach_set(pixels.astype("float32").ravel())
        img.filepath_raw = path(p)
        img.file_format = "PNG"
        os.makedirs(os.path.dirname(img.filepath_raw) or ".", exist_ok=True)
        img.save()
    finally:
        bpy.data.images.remove(img)
    return path(p)


def load_pixels(p):
    """A PNG or JPEG -> float RGBA numpy array (h, w, 4), bottom row first."""
    import numpy as np
    img = bpy.data.images.load(path(p), check_existing=False)
    try:
        w, h = img.size
        px = np.empty(w * h * 4, dtype="float32")
        img.pixels.foreach_get(px)
        return px.reshape(h, w, 4)
    finally:
        bpy.data.images.remove(img)


def fit_height(px, h):
    """Nearest-neighbour resize of an (h, w, 4) array to height h, keeping the aspect."""
    import numpy as np
    sh, sw = px.shape[:2]
    w = max(1, round(sw * h / sh))
    ys = np.minimum((np.arange(h) * sh / h).astype(int), sh - 1)
    xs = np.minimum((np.arange(w) * sw / w).astype(int), sw - 1)
    return px[ys][:, xs]


def sheet(tiles, cols, gap=8, bg=(0.12, 0.12, 0.12, 1.0)):
    """Lay (h, w, 4) arrays out in a grid, first tile top left; None leaves a cell empty. Returns (pixels, w, h)."""
    import numpy as np
    rows = (len(tiles) + cols - 1) // cols
    cw = max(t.shape[1] for t in tiles if t is not None)
    ch = max(t.shape[0] for t in tiles if t is not None)
    W, H = cols * cw + (cols + 1) * gap, rows * ch + (rows + 1) * gap
    out = np.empty((H, W, 4), dtype="float32")
    out[:] = bg
    for i, t in enumerate(tiles):
        if t is None:
            continue
        c, rw = i % cols, i // cols
        x = gap + c * (cw + gap)
        top = H - (gap + rw * (ch + gap))  # rows are stored bottom first
        out[top - t.shape[0]:top, x:x + t.shape[1]] = t
    return out, W, H
