# Blender skills from the AnyMachine family

Five skills Agent Orange brings for Blender, beside EL4CTEO's (`../blender-agent-skills`), arjun988's
(`../blender-skills-arjun988`) and mcp-for-blender's guides. Every agent session gets them while Blender is set up
(`client/ToolSkills.cs`). In them, `<skills_dir>` means this folder.

They're adapted from four repositories by Apekshik Panigrahi, all under the MIT licence with the same text
(`LICENSE-any-family.txt`), read on 2026-11-19:
[anymachine](https://github.com/apekshik/anymachine) at `d5e8889`,
[anyscene](https://github.com/apekshik/anyscene) at `88c1daf`,
[anykit](https://github.com/apekshik/anykit) at `9ab61ca` and
[anyshot](https://github.com/apekshik/anyshot) at `21e844c`.
Those repos are hosted services and experiments that lean on paid image, 3D, video and GPU services. Only their
free methods and Blender scripts are taken here; the skills mention paid services only as optional, and nothing
needs them. `docs/research/any-family.md` says what was kept, what was left out and why.

| Skill | From | What was taken | What changed |
|---|---|---|---|
| `blender-reference-first` | AnyMachine `PIPELINE.md` (parts 1 and 2); `hardsurface/.../shots.py`, `review_compose.py` and `stilt/render_stilt.py` for the compare idea | The order of work: references set by set, the parts list as the script header, the model as one script, bake-only detail, asserts, compare renders, the planned second pass, machine checks first | Rewritten as a skill. Image generation on fal and the video-model motion step are out (references come from the project's concept art, the person, or any image model). `scripts/compare_views.py` is new: named or custom views, clay or material, reference and render side by side, composed inside Blender (no Pillow) |
| `blender-kit-cohesion` | AnyKit `agent/KIT.md`, `agent/PROMPT.md`, the critic prompt in `agent/kit_builder.py`, `kit/tools/present.py`, `library.py`, `normalize.py` | The style paragraph, the plan and dials, the one-sheet gate, the loop, sizes, the critic rubric and pass rule, the three gotchas | Rewritten as a skill; the critic is prose for a second agent, not an API call. `lineup.py` (from `present.py`): no forced GPU, Eevee by default, items from GLBs, a kit.json or the open .blend, standing labels and a scale bar sized to the row; turntables left to `blender-lighting-lookdev`. `asset_library.py` (from `library.py`): kit.json optional, also builds from a .blend, previews with `asset_generate_preview()`. `normalize_import.py` (from `normalize.py`): the Tripo and Meshy branches, decimation and LODs are out (`make_lods.py` does those), paths beside prep.json |
| `blender-placement-qa` | AnyScene `pipeline/grounding.py`; `_ray_down`, `contacts`, `contact_gap`, `_drop` and `settle` from `scenes/hk-dai-pai-dong/scripts/dress.py`; the checks in its `validate.py`; the low-angle sheet from `SKILL.md` | Contact-point float and sink checks, prop and structure clipping, ground-only furniture, scale, bounds, the accept-with-reason list, the dressing helpers | One generic script, `placement_check.py`, with a JSON report. The scene's name lists (`SKIP_PREFIX`, `HANGING`, `NOT_SURFACE`, `FIXED_COLLS`, `GROUND_ONLY`, `ACCEPT`) became arguments and the custom properties `no_support`, `hanging`, `ground_only` and `real_h`; `shell`, `lib` and `assets.json` are gone. Rays use a BVH per support, so a prop's bottom face lying exactly on the floor can't hide the floor. Clipping counts any intersection (AnyScene allowed 30 triangle pairs for dense scans) with the higher prop lifted 3 mm, so stacked props pass |
| `blender-shot-rig` | AnyShot and AnyScene `camera/camrig.py`, `shoot.py` and `README.md` (the same files in both), AnyShot `agent/SHOTS.md`, AnyScene `grade.sh` | The shot rig and its moves, focus, handheld and walk profile, the shoot modes, the shot grammar, the block-first order, the grades | `camrig.py`: a shot's `exposure` is an offset (the scene-specific exposure-drift removal is out), and the stamp bar is opaque so the note reads on bright frames. `shoot.py`: libx264 instead of NVENC and Media Foundation, framing and lens-chart sheets made in Blender (no ffmpeg needed), no mp4 and a note when ffmpeg is missing, `--rig-only`, a closing JSON line. `path_clearance.py` is new, written from the clearance check `SHOTS.md` and the camrig README describe, for any animated camera. The GPU queue, farm, fal and order handling are out; `examples/shots.json` is new |
| `blender-motion-verify` | AnyMachine `quadruped/scripts/verify_glb.py`, `backpacks/scripts/verify_glb.py`, `stilt/verify_glb.py`, `quadruped/scripts/check.py`, `backpacks/scripts/check.py` | The re-import count and pose match, the joint sweep past limits, random poses, per-frame clashes, rest-pose overlaps ignored, ground and foot slip | Rewritten, not copied: those scripts read the `anymachine` property, import `gait.py`, and assume four legs, haa/hfe/kfe joints and a `group` property. `verify_reimport.py` takes `--source`, `--glb` and `--actions`, finds the actions itself, and adds the GLB's own JSON summary. `motion_check.py` groups parts by bone (parented or by strongest weight), reads limits from Limit Rotation constraints, takes feet as bone names, and follows the contacting vertex for slip |

Also from AnyScene: `blender-shot-rig/references/cycles-notes.md` gathers the Blender 5 and Cycles notes that
`blender-core` and `blender-rendering` lack (glTF quaternion rotations, emission sampling on dense emissive meshes,
volume cost and motion blur on volumes, and a few smaller ones), from AnyScene's `SKILL.md`, its notes and the
docstrings of `emitters.py` and `haze.py`.

Left out on purpose:
- **`quadruped/scripts/gait.py`** (AnyMachine's procedural trot, idle, sit and stand: analytic two-link IK, the
  period snapped to whole frames, stance feet that roll without slipping, baked to linear keys). It's built for that
  robot's spherical feet and backward knees, so it isn't generic. Read it as a reference when a procedural gait is
  needed: https://github.com/apekshik/anymachine/blob/main/quadruped/scripts/gait.py
- Everything that calls fal, Tripo, Meshy, OpenRouter, the Anthropic API, video models or the GPU farm, and the
  services built on them.
- Scene-specific builders (shells, lights, grime, de-branding, screens), and AnyMachine's per-object build scripts.

Shared: every skill's `scripts/_anyx.py` is the same small helper file (argument parsing, the `@@JSON@@` result line
the other packs' scripts print, image sheets made inside Blender), so each skill's folder stands alone. The scripts
follow the blender-agent-skills contract: `run(**kwargs)` for live use through `execute_blender_code`, and
`blender -b f.blend -P script.py -- --args` headless; `blender-core`'s `run_in_blender.py` runs them too. Those that
load or reset files refuse to run in a live session. All were run headless on Blender 5.3 against a small test
scene.

To update them, read the upstream repos' changes since the commits above and carry over what applies by hand: the
scripts were rewritten, so there's no import script for this pack.
