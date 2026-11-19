# The AnyMachine family: what Agent Orange took

Four repositories by Apekshik Panigrahi, all MIT with the same licence text, read on 2026-11-19:
[anymachine](https://github.com/apekshik/anymachine) (`d5e8889`), [anyscene](https://github.com/apekshik/anyscene)
(`88c1daf`), [anykit](https://github.com/apekshik/anykit) (`9ab61ca`) and [anyshot](https://github.com/apekshik/anyshot)
(`21e844c`). Each is an agent (Claude in Claude Code or the Claude Agent SDK) building 3D work in Blender by script:

- **AnyMachine:** experiments, not a service. Moving machines (a 30 m walker, a quadruped, a spider, a laptop hinge,
  backpacks), rigged with every control a 0-1 parameter, and complicated static props (a helicopter, a lifeboat
  station, equipment, light fixtures) textured by a bake. The method is the valuable part.
- **AnyScene:** a skill plus per-scene scripts for photoreal film-style sets (a ramen shop, a Hong Kong street stall,
  a 1970s mission control), from a brief to renders and trailers.
- **AnyKit:** a hosted service turning 1-4 style pictures into a kit of matching props, with a lineup, an asset
  library and a critic.
- **AnyShot:** a hosted "AI cinematographer": a set in, directed shots out.

All four lean on paid services: image generation (GPT Image on fal), image-to-3D (Tripo and Meshy, about $1.70 an
item), video models, a paid Anthropic or OpenRouter call for critics and planners, and a GPU render farm. AnyScene
and AnyShot share the same `camera/` folder (camrig), file for file.

Agent Orange took **one pack of five skills**, `client/Skills/tools/blender/any-family`, from the free parts only:
methods written as skills, and Blender scripts rewritten to be generic. Its README lists file by file what came from
where and what changed. Nothing in it needs a paid service; the skills mention them only as optional.

## What was kept, and how it sits beside AO's other Blender skills

### `blender-reference-first` (AnyMachine `PIPELINE.md`, both parts)
References set by set (an overall six-view sheet, then a close-up sheet per mechanism, each approved), a parts list
as the build script's header, the model as one script, bake-only detail under about 8 mm, budget and size asserts,
compare renders beside each reference, a planned second pass, and machine checks before a person looks. Its script,
`compare_views.py`, renders named or custom views in clay or material and composes each beside its reference inside
Blender.
- **Next to `blender-hardsurface-modeling`** (EL4CTEO): that skill is how to model the parts (modifier stacks,
  bevels, normals); this is the order of work and the review loop around them.
- **Next to `blender-asset-qa`'s `review_shots.py`:** that renders angle sheets of the model alone; this pairs each
  view with its reference and asks for a written list of differences.
- **Next to `blender-references`** (arjun988): matching one picture by eye; this works from a set of them.
- **Next to `blender-texture-baking`:** this says what goes into the bake (the bake-only detail); that skill bakes.

### `blender-kit-cohesion` (AnyKit `KIT.md`, `PROMPT.md`, the critic prompt, `present.py`, `library.py`, `normalize.py`)
One shared style paragraph in every item's brief, the dials as the brief, real sizes looked up, every reference on
one sheet before any 3D, the render-name-script-recheck loop, a lineup with a scale bar, the eight-category critic
rubric with its pass rule (no category under 6, average 7 or more), and an asset library. Scripts: `lineup.py`,
`asset_library.py`, `normalize_import.py`.
- **Next to `blender-art-direction`** (arjun988): one note per genre or mood; this writes the project's own style
  paragraph and holds a whole set to it.
- **Next to `prop-artist`** (arjun988): tiers and kitbash rules for one prop; nothing there makes a set match.
- **Next to `blender-asset-qa`:** a per-asset mechanical check; the critic here scores a whole set.
- **Next to `blender-environment-kits`' `kit_check.py`:** grid, pivots and texel density for modular pieces, which
  AnyKit doesn't have; the skill points there instead of repeating it.
- **Next to `blender-lighting-lookdev`'s `turntable.py`:** one asset turning; `lineup.py` is many side by side at
  true scale, so AnyKit's turntables were left to that script.
- **Next to `blender-game-optimization`'s `make_lods.py`:** LODs and triangle budgets, so `normalize_import.py` dropped
  AnyKit's decimation and LOD1.
- Nothing in AO built a catalogued asset library before; `blender-scene-setup` only mentions `asset_mark`.

### `blender-placement-qa` (AnyScene `grounding.py`, the contact helpers in `dress.py`, `validate.py`)
Float, sink and nothing-below checks at the lowest vertices (not the bounding box), ground-only furniture, prop and
structure clipping, scale against a stated height, bounds, reports accepted with a reason, a JSON report, and
`drop()` / `settle()` for dressing scripts; plus the low-angle still as the visual check.
- **Next to `set-dressing`** (arjun988): prose on density and clusters, with no placement code or check.
- **Next to `blender-level-design`:** player measurements and the grey-box; nothing checks props against supports.
- **Next to `blender-asset-qa`'s `qa_check.py`:** finds detached parts inside one asset, not props against the scene.

### `blender-shot-rig` (camrig from AnyShot and AnyScene, AnyShot's `SHOTS.md`, AnyScene's `grade.sh`)
shots.json to baked camera rigs (push-in, truck, crane, pan, orbit, Catmull-Rom path, dolly zoom with the lens
solved per frame, rack, track and point focus, f-stop keys, anamorphic bokeh, noise handheld and the walk profile),
framing sheets and lens charts, Eevee previews, resumable chunked Cycles finals, a camera clearance check
(`path_clearance.py`, written from AnyShot's description, as the repo has none), the shot grammar with numbers, and
the ffmpeg grades. For trailers, store pages, key art and cutscene previews.
- **Next to `camera-cinematography`** (arjun988): a checklist and three small tables, no code; this makes moves
  executable and checkable, and adds the speeds, heights and lens bands it lacks.
- **Next to `blender-rendering`'s `render_batch.py`** and `blender-asset-qa`'s `review_shots.py`: neither moves a
  camera. `blender-scene-setup`'s `scene_audit.py` doesn't check cameras.

### `blender-motion-verify` (AnyMachine `verify_glb.py` and `check.py`, rewritten)
`verify_reimport.py` re-imports the exported GLB into an empty scene and compares bones, meshes, actions and every
action's pose at sampled frames against the source, vertex to vertex, plus the GLB's own JSON summary.
`motion_check.py` sweeps each limited joint past its limits, tries random poses, checks every frame of every action
for parts passing through each other (ignoring adjacent bones and anything overlapping at rest), checks ground
penetration, and measures foot slip per stance.
- **Next to `blender-character-rigging`'s `rig_check.py` and `stress_poses.py`:** a static check, and poses for a
  person to judge; this judges clashes by machine through motion.
- **Next to `blender-animation`'s `loop_check.py`:** loop pops and hitches, not clashes, limits or slip.
- **Next to `blender-export-pipelines`' `export_asset.py`:** its `glb_summary` reads the file; this compares the
  export back against the source pose by pose, and the skill there only says "re-import and check" for FBX and USD.

### The Blender 5 and Cycles notes (AnyScene), in `blender-shot-rig/references/cycles-notes.md`
glTF imports rotating by quaternion (so `rotation_euler` does nothing), `emission_sampling = "NONE"` for dense
emissive meshes (545,000 emitters cost 290 s a frame), volume cost (homogeneous layers about 4 s a frame, a noise
room volume about 22 s) and object motion blur off on volumes, plus fills through glass, unbevelled glass boxes,
`read_factory_settings()` resetting the GPU choice, the 5.0 compositor nodes that misbehave, and checking frame counts
before encoding. `blender-core` and `blender-rendering` had none of the first three. They went into the shot rig's
references rather than a skill of their own, as that's where render time matters; the quaternion and `pack()` notes
are in `blender-kit-cohesion` too, where imports happen.

## What was left out, and why

**Paid, in every repo:** fal image and video generation (`generate/fal.mjs`, `kit/gen.mjs`, AnyMachine's
`gen_*.mjs`, `edit.ps1`, `detail_fal.mjs`, `video.ps1`, `make_reference_video.sh`), Tripo and Meshy steps and the
two-model compares built on them (AnyKit's `compare.py`, AnyScene's `render_compare.py` and `picks.json`), the critic
and planner API calls (`llm.py`, `planner.py`), the GPU farm (`farm/`, `/scene2video`, `blender_gpu.py`,
`gpu_run.sh`), and the hosted apps around them (`app.py`, `client.py`, `director.py`, `harness.py`, the deploy
plumbing, the gallery and Cloudflare R2 pages). AO already lists Tripo and Meshy under Third Party, as optional.

**Repairs for generated meshes:** AnyKit's and AnyScene's de-branding, fake-screen and opaque-glass fixes, and the
Tripo orientation hack. They exist because image-to-3D gets those wrong; the glass advice is already in
`blender-pbr-materials`.

**Duplicates of AO's skills:** AnyScene's scene-specific shells, lights and grime (`blender-lighting-lookdev`,
`blender-time-of-day`, `blender-pbr-materials`, `blender-texturing`), its blockout and floor plan (`blender-level-design`,
`review_shots.py`), its prep scripts (`blender-export-pipelines`, `realistic-style`'s scan clean-up), AnyKit's
triangle budgets and LODs (`prop-artist`, `make_lods.py`), AnyMachine's budget asserts as scripts (`qa_check.py`,
`blender-game-optimization`), and the plain handheld noise and lens table as ideas (`camera-cinematography`).

**Not generic:**
- **`gait.py`** (AnyMachine's quadruped): analytic two-link IK baked to linear keys, a period snapped to whole frames
  so loops close, stance feet rolling without slipping (0.014 mm measured). It's built for that robot's spherical
  feet and haa/hfe/kfe joints with backward knees. The pack's README names it as a reference only.
- AnyMachine's per-object build scripts and part kits (`hs.py`'s builder is a nice bmesh-primitive kit with a lo/hi
  split, but it's project-shaped; the skill describes the bake-only convention instead), and the per-machine
  `check.py` files beyond their generic core.
- AnyScene's procedural volumes (`fx.py`, `haze.py`, `lights.py`'s flames): they need its `lib.py` helpers. Their
  lessons are in the Cycles notes.
- AnyShot's floor-plan registration and the order handling around it, tied to its hosted API.

## The AnyMachine notes this replaces

An earlier note on AnyMachine alone (2026-11-19, never committed) is folded in here.

**The method, in short:**
1. References first: one concept image leads to a turnaround, single views, then a close-up plate per mechanism,
   each made from the ones before it and approved before the next. Prompts are kept as files, so every image can be
   remade. Image-to-3D only as a skin to measure joints from.
2. A parts list before any geometry, written as the script's header.
3. The model is one script; small detail is bake-only geometry; triangle budgets and size are asserted.
4. Compare renders from each reference's camera, differences written down, a second pass planned.
5. Checked by machine before a person looks: an overlap sweep, foot slip, and the GLB re-imported and counted, each
   writing a JSON report.
6. Bakes and renders as independent headless jobs, from one command each, with relative paths and no network.

Items 1-5 are now `blender-reference-first` and `blender-motion-verify`. Still open from that note, as AO features
rather than skills:
- **An asset card template** laying out the stages on an asset's board: concept, turnaround, close-ups, parts list,
  build script, compare renders, checks, deliverable. The board already holds pictures and notes, so it's a
  template, not a new system; agents still never fill the deliverable.
- **Check reports as evidence:** each script writes a JSON report with a `pass` flag, ready to attach to the asset's
  task (the "attach evidence" gap in the agent weak points) once AO has a place for it.
- **Remote jobs, not only tests:** bakes and renders are the same kind of unattended one-command job as #93's test
  runs on another PC. Nothing engine- or tool-specific is needed for that.
- **Prompts kept as files** beside the references, so a style change can regenerate the set; the skill says to.

**For Animal Planet's art pass (game side, not AO):** the quadruped work is close to what the animals need (trot,
idle, sit and stand as baked linear actions at a measured 0.014 mm slip), and the 0-1 controls baked to glTF actions
give any engine a simple contract (pose by seeking `t`). But those are hard-surface robots; soft, characterful
animals in a Ni no Kuni style are a different problem, and the backpack notes show where soft goods needed new joint
types. A method to follow, not a ready answer.

**Costs and limits:** one big object took 40 reference images, about 2,200 lines of script and about 40 check
renders; the results are game props, still plainer than their sheets. Reference generation used a paid image model;
Codex's own image generation, the project's concept art or the person's pictures stand in for it. The repos need
Blender 5.2 (5.0 for the older runs).

## Checked
Every script was run headless once on Blender 5.3 (Steam build), on a generated scene: a floor, a crate, a floating
box and a box clipping the crate; a two-bone armature with a skinned column, a block on the lower bone, a Limit
Rotation on the upper bone and one action that bends it while the rig slides; a camera move that ends close to the
crate; and GLB exports of the rig and two props. Each found what was planted (the floater, the clip, the camera too
close on its last frames, the sliding foot, a third bone's block in the bend's path, an export without animation, an
export scaled by 2%) and passed what was right. ffmpeg isn't installed on that PC, so the shot rig was run once
without it (frames and every sheet still written, a note instead of an mp4) and once with a standalone ffmpeg 7 in
`$FFMPEG`, which encoded the final and the preview with libx264 and ran the still and clip grades from
`references/grades.md` (24 frames in, 24 out).
