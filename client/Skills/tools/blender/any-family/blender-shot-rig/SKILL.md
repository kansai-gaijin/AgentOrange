---
name: blender-shot-rig
description: Trailer, store-page and key-art shots from Blender - a shots.json baked into camera rigs (push-in, orbit, path, dolly zoom, rack focus, handheld walk), blocked, clearance-checked, rendered and graded. Framing sheets and lens charts come before any final; finals render in resumable chunks and grade in ffmpeg. Use when a game needs trailer footage, store screenshots, key art or cutscene previews of a level, character or prop.
license: MIT
---

# Shot rig

Shots as data: each camera move is a few lines of JSON, baked one key per frame into a small rig, so every shot can
be re-run after the level changes and every move type behaves the same. Cheap framing sheets come before any
expensive render. From AnyShot's and AnyScene's shared `camera/` (camrig), AnyShot's shot language and grades, and
the clearance check AnyShot describes, written for Agent Orange.

## Order of work
1. **Survey the set** once: its bounds, floor height, the big objects. `shoot.py --list <part of a name>` prints names
   with their bounds centre and size. Note what the shots should show.
2. **Write `shots.json`**: one entry per shot, words turned into moves with
   [references/shot-grammar.md](references/shot-grammar.md). The contract is
   [references/shots-json.md](references/shots-json.md); `examples/shots.json` has a push-in with focus tracking, an
   orbit, a handheld walk and a key-art still.
3. **Block first, render later.** Framing sheets (each shot at its start, middle and end, stamped with lens, f-stop
   and focus) and lens charts are seconds each:
   ```
   blender -b level.blend -P <skills_dir>/blender-shot-rig/scripts/shoot.py -- --shots shots.json --out //shots --framing
   blender -b level.blend -P <skills_dir>/blender-shot-rig/scripts/shoot.py -- --shots shots.json --out //shots --lenschart hero_push
   blender -b level.blend -P <skills_dir>/blender-shot-rig/scripts/shoot.py -- --shots shots.json --out //shots --preview --step 2
   ```
   Open `<out>/_framing.png`. Check the framing, the focus, and that nothing reads wrong from the new angle (floating
   props, missing backs, holes, untextured faces). Fix the shot or the set and look again.
4. **Check the camera's clearance** on every frame, in one Blender session:
   ```
   blender -b level.blend -P <skills_dir>/blender-shot-rig/scripts/shoot.py -- --shots shots.json --rig-only --save //rigged.blend
   blender -b rigged.blend -P <skills_dir>/blender-shot-rig/scripts/path_clearance.py -- --camera CR_level_walk_cam --limit 0.45 --report //clearance.json
   ```
   It reports the nearest solid mesh and its frame, and every frame under the limit. It leaves out what a real camera
   moves through: volumes, particles, things marked `no_clearance` or `no_support`, anything under 5 cm, and
   `--skip` name parts (dust, glass panes the move passes). The floor counts as solid, so a deliberate low shot needs
   `--skip Floor` or a lower `--limit`. It works on any animated camera, not just camrig's.
5. **Finals** in Cycles at the scene's own look, in chunks short enough for one command each (a run skips frames
   that already exist, so it resumes):
   ```
   blender -b level.blend -P <skills_dir>/blender-shot-rig/scripts/shoot.py -- --shots shots.json --out //shots --only level_walk --frames 1-48
   blender -b level.blend -P <skills_dir>/blender-shot-rig/scripts/shoot.py -- --shots shots.json --out //shots --still 1 --only key_art
   ```
   When the last chunk lands it writes `contact.png` (first, middle, last frame) and, with ffmpeg on PATH or in
   `$FFMPEG`, `<shot>.mp4` (libx264, CRF 18). Set the GPU first (`blender-rendering`'s `render_batch.py` does), or
   Cycles renders on the CPU in background mode.
6. **Grade in ffmpeg**, the same grade on every shot of a set, and check every clip's frame count:
   [references/grades.md](references/grades.md).

The input .blend is never saved; `--save` writes the rigged scene elsewhere for a person to open and adjust.

## Store pages and trailers
- **Store screenshots:** stills at the store's sizes (`"res"`, and `"aspect": "16:9"`), one idea per shot, the
  character or the level's best view, with a shallow `fstop` only where it helps read the subject.
- **Trailer:** an establishing wide, a walk through the space (`move: "path"`, `handheld: {"profile": "walk"}`), then
  close reveals with rack focus. Keep moves slow: 0.2-0.4 m/s reads as deliberate.
- **Key art:** a `static` shot at 85 mm or longer, low and slightly below the subject's eye line, rendered with
  `--still`, more samples (`"samples"`) and no motion blur (`"shutter": 0`).
- Previews in Eevee differ from Cycles: glass reads dark and light falloff is flatter. Use them for framing, move
  and focus, not for the look.

## Notes
- [references/cycles-notes.md](references/cycles-notes.md): Blender 5 and Cycles gotchas that `blender-core` doesn't
  cover: glTF quaternion rotations, emission sampling on dense emissive meshes, the cost of volumes and of motion
  blur on them, and the compositor nodes that misbehave.
- Track To flips when the aim is exactly above or below the camera: keep aim points off its plumb line.
- `lens_fx` (Glare, Lens Distortion) is spliced into the scene's own compositor; colour balance, vignettes and grain
  belong in the ffmpeg grade instead.

## Related skills
- `camera-cinematography` (arjun988's pack): lens choice, composition and the shot checklist; this skill makes the
  moves executable and checkable.
- `blender-rendering`: Cycles and Eevee settings, GPU setup, batch renders.
- `blender-lighting-lookdev`: a turntable of one asset; `blender-video-editing`: cutting the clips together.
- `blender-placement-qa` in this pack: run it before shooting, as new angles show floating props.
