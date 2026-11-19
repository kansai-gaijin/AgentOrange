# shots.json and shoot.py

From AnyShot's `camera/README.md` (MIT), trimmed: its farm, GPU queue and timing tables are left out, and the
encoder is now libx264.

## Modes

```
blender -b scene.blend -P scripts/shoot.py -- --shots shots.json --out <dir>
    [--only a,b] [--rig-only | --preview | --framing | --lenschart NAME | --still N] [--frames a-b]
    [--save rigged.blend] [--no-encode] [--engine eevee|cycles|workbench] [--step N]
blender -b scene.blend -P scripts/shoot.py -- --list <pattern>
```

| Mode | What it does | Output (`<out>/<shot>/…`) |
|---|---|---|
| *(default)* final | Cycles at the scene's settings unless a shot field overrides them. Frames that already exist are skipped, so a run resumes. After the last frame: a contact sheet, and an mp4 when ffmpeg is there. | `frames/####.png`, `contact.png`, `<shot>.mp4` |
| `--preview` | Eevee at 50%, every frame (`--step N`: every Nth), 16 samples, no motion blur, stamped with shot, frame, lens, f-stop and focus distance. `--engine cycles`: 16 samples, denoised, the real lighting. | `preview/####.png`, `preview.mp4` |
| `--framing` | A stamped 640 px still of each shot at t = 0, 0.5 and 1, and one sheet of all of them. | `framing/t0.png`, `t5.png`, `t10.png`, `<out>/_framing.png` |
| `--lenschart NAME` | Shot NAME's first frame at 18, 24, 35, 50, 85 and 135 mm. | `lenschart/<mm>.png`, `<out>/<NAME>_lenschart.png` |
| `--frames a-b` | Only frames a to b of the shot (1-based): keep each command short. | |
| `--still N` | One final-quality frame. | `still_####.png` |
| `--rig-only` | Builds the rigs and renders nothing (with `--save`, for `path_clearance.py` or a person). | |
| `--save path.blend` | Writes the rigged scene elsewhere. The input file is never saved. | |
| `--no-encode` | No mp4. | |

Each run ends with one `@@JSON@@{...}` line listing what it wrote. ffmpeg is found on PATH or in `$FFMPEG`; without
it the frames and sheets are still written.

## The file

```jsonc
{
  "defaults": { "fps": 24, "res": [1920, 1080], "aspect": "16:9", "samples": 256,
                "shutter": 180, "sensor": "s35", "fstop": 2.8, "ease": "inout", "handheld": 0.0 },
  "shots": [
    {
      "name": "counter_push",                 // [a-z0-9_], used for folders and the rig name
      "frames": 120,
      "fps": 24,
      "res": [1920, 1080],
      "aspect": "2.39",                       // "16:9" | "1.85" | "2.39" | "4:3" | a number: height recomputed from width
      "samples": 256,                         // Cycles samples for finals
      "shutter": 180,                         // motion blur shutter in degrees (0 = off)
      "sensor": "s35",                        // "s35" (24.89 mm) | "ff" (36 mm) | sensor width in mm
      "move": "push_in",                      // see Moves
      "ease": "inout",                        // linear | in | out | inout
      "keys": [
        { "t": 0.0, "pos": [x,y,z], "aim": [x,y,z], "lens": 35, "roll": 0 },
        { "t": 1.0, "pos": [x,y,z], "aim_obj": "Hero", "lens": 35 }
      ],
      "focus": { "mode": "rack", "keys": [ { "t": 0.0, "obj": "Lamp" }, { "t": 0.6, "pos": [x,y,z] } ] },
      "fstop": 2.0,                           // or "fstop_keys": [{ "t": 0, "v": 4 }, { "t": 1, "v": 1.4 }]
      "blades": 0, "ratio": 1.0,              // aperture blades (0 = round), anamorphic ratio (>1 = oval bokeh)
      "handheld": 0.35,                       // 0..1 shake on top of the move, or {"profile": "walk"}
      "breathing": 0.5,                       // 0..1 slow drift (defaults to handheld)
      "exposure": 0.0,                        // EV offset from the scene's own exposure
      "lens_fx": { "distortion": 0.01, "dispersion": 0.01, "glare": "fog" }
    }
  ]
}
```

Metres and Blender's axes (Z up). Wherever a position goes, an object name works:
- `aim_obj`, `pos_obj` and `focus.obj` mean the centre of that object's world bounding box, children included (so a
  glTF import's root works). Add `aim_offset` / `pos_offset` `[dx, dy, dz]` to nudge from it.
- Names match exactly, then by the start of the name, then anywhere in it, ignoring case.
- A key that leaves out `pos` or `aim` holds the previous one.
- Position and aim take one ease over the whole shot (passing through middle keys without stopping); lens, roll,
  focus and f-stop keys ease per segment (hold, then pull).

### Moves
| move | keys | notes |
|---|---|---|
| `static` | 1 | Locked off. Add `handheld` for life. |
| `dolly`, `truck`, `pedestal`, `push_in`, `crane` | 2+ | Each key has a position and an aim. The names say intent; they all interpolate the same way. |
| `pan`, `tilt` | 2+ | Same `pos`, changing `aim`. |
| `orbit` | 0 (1 for lens and roll) | `"orbit": {"center": [x,y,z] or "center_obj": "name", "radius": 1.2, "start_deg": -30, "end_deg": 30, "height": 1.3}`. Aims at the centre (or at an `aim` / `aim_obj` inside the orbit). Angles from +X, counter-clockwise seen from above. |
| `dolly_zoom` | 2 (start and end `pos`; aim and lens at t = 0) | The lens is solved per frame to hold the subject's size: `lens(t) = lens0 · dist(t) / dist0`, measured to `"subject"` (a position or object; defaults to the aim). |
| `path` | 3+ | A smooth Catmull-Rom path through every key, eased in time. |

### Focus
- `{"mode": "track", "obj": "name"}`: the focus follows that object.
- `{"mode": "point", "pos": [x,y,z]}`, or `"obj"`.
- `{"mode": "rack", "keys": [...]}`: pulls between keys with the shot's ease. To hold and then pull, repeat a key:
  `t 0 → a`, `t 0.4 → a`, `t 0.6 → b`. Use a shallow `fstop` (1.4-2.8) so it reads.
- `{"mode": "none"}`: no depth of field. With no `focus` at all, focus is where the camera aims.

### Handheld
- **Shake (`handheld`: a number):** noise on the shake empty, between aim and camera, so it moves in camera space.
  A hand bob of about 11 frames (4 mm, 0.27°), a fine jitter of about 3.5 frames (1.2 mm, 0.07°), and with
  `breathing` a body sway of about 2 s (14 mm, 0.4°). Roll runs at about 60% of pan and tilt. It reads stronger on
  long lenses: at 85 mm, 0.4 is clearly handheld; at 24 mm, 0.3-0.5 feels like a steady operator.
- **Walk (`"handheld": {"profile": "walk", "amount": 1.0}`):** a baked operator walking. Footsteps (a 1 cm dip per
  step at `step_hz` 1.8, a 0.3° roll alternating left and right) scale with the move's speed, full at `walk_speed`
  (0.55 m/s) and none when standing; a body sway of 0.17-0.57 Hz; the aim follows a spring (`aim_lag_hz` 0.9,
  `aim_damping` 0.62), so pans trail and overshoot slightly; and `corrections` (2) small reframes. Nothing is above
  about 1.9 Hz, so there's no jitter. Use it with `move: "path"`, 4-6 keys at 0.3-0.6 m/s, `ease: "inout"`.

## The rig
```
CR_<shot>_base       position, one linear key per frame
 └ CR_<shot>_aim     Track To CR_<shot>_target
    └ CR_<shot>_shake   handheld noise or the walk
       └ CR_<shot>_cam  lens, sensor, depth of field (lens keyed)
CR_<shot>_target     aim point, keyed
CR_<shot>_focus      depth-of-field target, keyed or parented
```
Each shot's rig is in its own collection `camrig/CamRig_<shot>`, rebuilt on every run. Change a move by editing
shots.json and running again; in a saved file a person can still tweak the shake's noise modifiers, move the focus
empty, or change the lens and f-stop keys.

## Inside Blender
```python
import sys; sys.path.insert(0, r"<skills_dir>/blender-shot-rig/scripts")
import camrig
camrig.bounds("Hero")      # (min, max, centre) Vectors, children included
cam = camrig.build(camrig.merged(defaults, shot))   # one rig, returns the camera
```
