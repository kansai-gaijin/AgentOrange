"""camrig CLI: builds a rig per shot from shots.json, then renders framing sheets, lens charts, previews or finals.
  blender -b scene.blend -P shoot.py -- --shots shots.json --out <dir>
      [--only a,b] [--rig-only | --preview | --framing | --lenschart NAME | --still N] [--frames a-b]
      [--save rigged.blend] [--no-encode] [--engine eevee|cycles|workbench] [--step 2]
  blender -b scene.blend -P shoot.py -- --list <pattern>
The contract and the modes are in ../references/shots-json.md. The input .blend is never saved.

From github.com/apekshik/anyshot camera/shoot.py (MIT), changed for Agent Orange: encodes with libx264 (no NVENC
or Media Foundation), builds the framing and lens-chart sheets inside Blender so they need no ffmpeg, skips encoding
with a note when ffmpeg is missing, adds --rig-only, and ends with one @@JSON@@ line listing what it wrote.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import time

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _anyx as ax  # noqa: E402
import camrig  # noqa: E402

FF = os.environ.get("FFMPEG") or shutil.which("ffmpeg")
written = []


def args():
    p = argparse.ArgumentParser(description="camrig shot rigs and renders")
    p.add_argument("--shots")
    p.add_argument("--out")
    p.add_argument("--only", default="")
    p.add_argument("--rig-only", action="store_true", help="build the rigs (and --save them), render nothing")
    p.add_argument("--preview", action="store_true")
    p.add_argument("--framing", action="store_true")
    p.add_argument("--lenschart")
    p.add_argument("--still", type=int)
    p.add_argument("--frames")
    p.add_argument("--save")
    p.add_argument("--no-encode", action="store_true")
    p.add_argument("--engine", default="eevee", choices=("eevee", "cycles", "workbench"))
    p.add_argument("--step", type=int, default=1)
    p.add_argument("--list")
    return p.parse_args(ax.argv())


def ff(*cmd):
    r = subprocess.run([FF, "-v", "error", "-y", *cmd], capture_output=True, text=True)
    if r.returncode:
        raise RuntimeError(r.stderr[-800:])


def encode(files, fps, out):
    """files: ordered frame paths (any numbering) -> H.264 mp4 (libx264, CRF 18) via a concat list (no glob)."""
    if not FF:
        print("camrig: ffmpeg not found (PATH or $FFMPEG), so no mp4; the frames are in", os.path.dirname(files[0]))
        return
    lst = out + ".txt"
    with open(lst, "w", encoding="utf-8") as f:
        for p in files:
            f.write(f"file '{p.replace(os.sep, '/')}'\nduration {1 / fps:.6f}\n")
        f.write(f"file '{files[-1].replace(os.sep, '/')}'\n")
    ff("-f", "concat", "-safe", "0", "-i", lst, "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2,format=yuv420p",
       "-r", str(fps), "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-movflags", "+faststart", out)
    os.remove(lst)
    written.append(out)
    print("camrig: encoded", out)


def sheet(files, cols, out, height=360):
    """Equal-height tiles in a grid, made in Blender (no ffmpeg)."""
    px, w, h = ax.sheet([ax.fit_height(ax.load_pixels(p), height) for p in files], cols)
    ax.save_png(px, w, h, out)
    written.append(out)
    print("camrig: sheet", out)


def render_still(path):
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def main():
    a = args()
    if a.list is not None:
        found = []
        for o in sorted(bpy.data.objects, key=lambda o: o.name):
            if a.list.lower() in o.name.lower():
                lo, hi, c = camrig.bounds(o)
                sz = hi - lo
                found.append({"name": o.name, "center": ax.r(c, 3), "size": ax.r(sz, 3)})
                print(f"  {o.name:40s} center ({c.x:.3f}, {c.y:.3f}, {c.z:.3f})  size ({sz.x:.2f}, {sz.y:.2f}, {sz.z:.2f})")
        ax.emit({"objects": found})
        return
    with open(ax.path(a.shots), encoding="utf-8") as f:
        cfg = json.load(f)
    out = ax.path(a.out or "//shots")
    os.makedirs(out, exist_ok=True)
    only = {x for x in a.only.split(",") if x}
    shots = [camrig.merged(cfg.get("defaults", {}), s) for s in cfg["shots"]]
    if a.lenschart:
        only = {a.lenschart}
    if only:
        missing = only - {s["name"] for s in shots}
        if missing:
            raise SystemExit(f"camrig: unknown shots {missing}")
        shots = [s for s in shots if s["name"] in only]
    sc = bpy.context.scene
    camrig.install_handler()
    engine = {"eevee": camrig.eevee_id(), "cycles": "CYCLES", "workbench": "BLENDER_WORKBENCH"}[a.engine]

    cams = {}
    for i, s in enumerate(shots):
        cams[s["name"]] = camrig.build(s, seed=i * 7)
        print(f"camrig: rig {s['name']} ({s.get('move', 'static')}, {s.get('frames', 120)} f)")
    if a.save:
        sc.camera = cams[shots[0]["name"]]
        bpy.ops.wm.save_as_mainfile(filepath=ax.path(a.save), copy=True)
        written.append(ax.path(a.save))
        print("camrig: saved", a.save)
    if a.rig_only:
        ax.emit({"rigs": {n: c.name for n, c in cams.items()}, "written": written})
        return

    framing_tiles = []
    for s in shots:
        name, cam = s["name"], cams[s["name"]]
        d = os.path.join(out, name)
        n = int(s.get("frames", 120))
        if a.framing or a.lenschart:
            w, h = camrig.res_for(s)
            s2 = dict(s, _engine=engine, _pct=100, res=[640, int(640 / (w / h))])
            camrig.setup_render(s2, cam, mode="preview")
            if a.framing:
                for tag, f in (("t0", 1), ("t5", (n + 1) // 2), ("t10", n)):
                    sc.frame_set(f)
                    camrig.stamp_note(sc, bpy.context.evaluated_depsgraph_get())
                    p = os.path.join(d, "framing", tag + ".png")
                    render_still(p)
                    framing_tiles.append(p)
            else:
                sc.frame_set(1)
                tiles = []
                if cam.data.animation_data:
                    cam.data.animation_data.action = None
                for mm in camrig.LENS_CHART:
                    cam.data.lens = mm
                    camrig.stamp_note(sc, bpy.context.evaluated_depsgraph_get())
                    p = os.path.join(d, "lenschart", f"{mm:03d}.png")
                    render_still(p)
                    tiles.append(p)
                sheet(tiles, 3, os.path.join(out, f"{name}_lenschart.png"))
            continue
        if a.preview:
            s2 = dict(s, _engine=engine, _pct=50)
            camrig.setup_render(s2, cam, mode="preview")
            sc.frame_step = max(1, a.step)
            sc.render.filepath = os.path.join(d, "preview", "")
            t0 = time.time()
            bpy.ops.render.render(animation=True)
            files = [os.path.join(d, "preview", f"{f:04d}.png") for f in range(1, n + 1, sc.frame_step)]
            files = [p for p in files if os.path.exists(p)]
            print(f"camrig: preview {name} {len(files)} frames in {time.time() - t0:.0f}s")
            if not a.no_encode and files:
                encode(files, s.get("fps", 24) / sc.frame_step, os.path.join(d, "preview.mp4"))
            continue
        camrig.setup_render(s, cam, mode="final")
        if a.still:
            sc.frame_set(a.still)
            sc.render.use_overwrite = True
            t0 = time.time()
            p = os.path.join(d, f"still_{a.still:04d}.png")
            render_still(p)
            written.append(p)
            print(f"camrig: still {name} f{a.still} in {time.time() - t0:.0f}s")
            continue
        f0, f1 = 1, n
        if a.frames:
            f0, f1 = (int(x) for x in a.frames.split("-"))
            f1 = min(f1, n)
        sc.frame_start, sc.frame_end = f0, f1
        sc.render.filepath = os.path.join(d, "frames", "")
        t0 = time.time()
        bpy.ops.render.render(animation=True)
        print(f"camrig: final {name} frames {f0}-{f1} in {time.time() - t0:.0f}s")
        files = [os.path.join(d, "frames", f"{f:04d}.png") for f in range(1, n + 1)]
        have = sum(os.path.exists(p) for p in files)
        if have == n:
            sheet([files[0], files[n // 2], files[-1]], 3, os.path.join(d, "contact.png"))
            if not a.no_encode:
                encode(files, s.get("fps", 24), os.path.join(d, f"{name}.mp4"))
        else:
            print(f"camrig: {name} incomplete ({have}/{n}); the mp4 and contact sheet come after the last chunk")
    if framing_tiles:
        sheet(framing_tiles, 3, os.path.join(out, "_framing.png"))
    ax.emit({"shots": [s["name"] for s in shots], "out": out, "written": written, "ffmpeg": bool(FF)})


main()
