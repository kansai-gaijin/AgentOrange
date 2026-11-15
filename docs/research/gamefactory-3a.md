# GameFactory-3A: what's worth taking

[OpenDCAI/GameFactory-3A](https://github.com/OpenDCAI/GameFactory-3A) (Apache-2.0) has a coding agent
make game assets and engine code from a brief, for UE5, Blender, Unity, Godot 4 and three.js. It's
built from Markdown skills for the agent (`agent_skills/`), Python wrappers for local and cloud
generators (`models/`, `operators/`, `pipeline/`), and per-engine adapters (`engine_adapters/`).
Read in full on 2026-11-15, at commit `f9e927c`.

## Taken now

Four of its skills, rewritten for Agent Orange's pages and tools, now come with Agent Orange
(`client/Skills/`, licence beside them):

- `play-and-review`, from its "Validate, play, and iterate" section: compiling, launching and a clean
  log aren't evidence; play the main operations, capture, review against a fixed failure table, and fix
  each problem in the layer it belongs to.
- `model-facing`, from `asset_qa/3d_object/orientation_review.md`: decide a model's facing from views
  named after the camera's axis, record it, and fix it in the asset or import, never in gameplay code.
- `game-feel`, from `game-feel/SKILL.md`: per-action feedback, one camera writer, damping that doesn't
  depend on frame rate, reduced motion.
- `plan-the-next-slice`, from `game_design/`: plan by the player's experience, an asset list per beat
  with where each asset comes from, whitebox first with a colour legend, tasks in dependency order,
  checks item by item.

## For later, by the feature it belongs to

### Paid generation (Third Party)

- **Never pay twice by accident** (`models/common/task_recovery.py`, `docs/meshy-task-recovery.md`,
  `docs/tripo-task-recovery.md`):
  - Save the provider's task ID as soon as it accepts the task, before polling. Write the record
    atomically to `pending/<scope>.json`. It holds only version, scope, task ID and when; never a
    prompt, key or URL.
  - Scope is sha256(request fingerprint + API base + sha256(key)).
  - Hold one file lock for the whole submit, poll, download and publish. Check the finished cache
    again once the lock is held.
  - On a retry, resume the saved task and fetch a fresh download URL, as the signed ones expire.
  - Timeouts, failures and corrupt records keep the record, and the error names it. Only a person
    deleting it allows a new paid task.
  - Never retry a creation POST automatically: one paid submit per yes.
- **Errors** (`classify_http_error` in `models/common/cloud_api.py`):
  - Retry 408, 409, 425, 429 and 5xx with backoff, honouring Retry-After up to 60 s.
  - Never retry 400, 401, 402, 403, 404, 405, 413, 415 or 422.
  - Read the body for "insufficient", "enough credit", "quota exceeded" or "balance". Tripo says "not
    enough credit" with a 403, which otherwise looks like a bad key; the two need different messages.
  - Tripo can also answer HTTP 200 with a non-zero `code`, which is a failure.
- **Finished-results cache:** keyed by sha256 of the payload, the image's sha256 and the format, never
  the key. Write the meta file first; the artifact existing means done. An identical rerun is free.
- **Tripo:**
  - Base URL `https://openapi.tripo3d.ai/v3`.
  - Calls: `POST /files` (multipart), which returns a `file_token`; then
    `POST /generation/image-to-model` or `/generation/text-to-model`; then `GET /tasks/{id}`.
  - `GET /account/balance` is free.
  - With `smart_low_poly`, `face_limit` must be between 500 and 20000.
  - The model URL can be under `model_url`, `pbr_model`, `model`, `base_model`, `glb` or `gltf`.
    The preview is at `output.rendered_image_url`.
  - Runs have taken up to about 600 s.
- **Meshy:**
  - Base URL `https://api.meshy.ai`.
  - Calls: `POST /openapi/v1/image-to-3d` (the image as a data URI); and
    `/openapi/v2/text-to-3d`, which is two billed tasks: `mode: preview`, then `mode: refine` with the
    `preview_task_id`.
  - `GET /openapi/v1/balance`.
  - Formats come back in `model_urls`. `target_polycount` must be between 100 and 300000.
  - The `msy_dummy_…` test key returns canned results free.
- **Cost and provenance per asset:** keep `meta.json` beside each generated file: provider, model, task
  IDs, seconds taken, credits, cached or resumed, triangles, bytes, preview URL, prompt, seed, source
  image.
- **Concept image before image-to-3D:**
  - Prompt suffix: "one single object standing alone, wide shot, small in the centre of a large empty
    white background, whole object in frame with margin, facing the viewer, soft even light, no shadow".
  - Keep the largest connected shape. Score each candidate on touching the border, on coverage near
    0.34, and on stray shapes. Try up to 6 seeds.
  - A subject touching the border means generating again.
- **Clean-up after image-to-3D:** remove the fake floor plate it adds
  (`operators/gen_3d_object/funcs/mesh_cleanup.py`): thin loose pieces in the bottom quarter, never
  more than 40% of the model.
- **Not worth it:** local GPU models (CUDA and Linux/conda), Woosh audio (non-commercial weights), and
  Volcengine and Tencent services (China regions). MiniMax video (`api.minimax.io`) is international
  and could be added later.

### Assets screen

- **Fields to consider** (`operators/gen_3d_object/funcs/art_plan.py`, `code_asset.py`):
  - Role-based budgets: avatar 40k triangles with a 2048 texture; weapon and prop 20k with 1024;
    scenery 8k with 1024; landmark 20k with 1024.
  - Plausible heights in metres: avatar 0.3–4, weapon 0.05–3, prop 0.02–6, scenery 0.05–40,
    landmark 0.5–200.
  - Facing (±x, ±y or ±z) with no default, and how it was verified: heuristic, declared, spec, or
    agent looked.
  - Where it came from (generate, reuse, licensed, procedural), its licence and provenance, and its
    variants.
- **A per-asset preview sheet:** five orthographic views from the camera axes
  (`engine_adapters/three_js/preview/`: numpy and Pillow, no GPU).

### Tasks

- **States** (`game_design/task_tracking.md`): needs_detail → ready → in_progress → review → done,
  plus blocked with a reason.
  - Ready only when its inputs, acceptance and dependencies are met.
  - Done only when its acceptance passes, with evidence linked.
  - Reopen downstream tasks when the design changes.
  - This fits the planned status labels.
- **Fields:** depends_on, owner (agent or person), inputs and outputs, acceptance, evidence links.
- **A repair loop with named stop reasons** (`correct_spec` in `code_asset.py`): at most 3 tries, every
  check each time, then stop on passed, exhausted, repeating, oscillating or no_progress.

### Agents adding cards

The `plan-the-next-slice` skill has to ask the person to add missing cards, as Agent Orange's MCP has no
add_card. Worth adding with the same rules as write_card.

### Playtesting (agents playing the game)

- **Shared action list and output** (`engine_adapters/ue5/playtest/client.py`):
  - Actions: move, look, jump, attack, interact, dash, pause, restart, wait. Each has a `duration_ms`.
  - Output: `frames/`, `actions.jsonl`, `report.json`, and an optional `video.mp4`.
  - Report checks (`pipeline/code_gen/playtest/eval.py`): completed; frames above 0; actions ran and
    none failed; no page errors; console errors within budget; no crash.
- **Unreal frame recorder** (`engine_adapters/ue5/plugin/A3GamePlayable/.../A3GamePlaytestRecorderSubsystem.cpp`,
  about 150 lines):
  - A world subsystem turned on by `-A3PlaytestOutput= -A3PlaytestFps= -A3PlaytestDuration=`.
  - Writes `play_started.json` once play begins.
  - Captures with `FScreenshotRequest::RequestScreenshot`. ReadPixels gives black frames in `-game`.
  - Exits when done.
  - Their host side injects keys with SendInput and forces the window to the front. Don't take that:
    it takes over the person's desktop.
- **Web playtest recorder** (`engine_adapters/three_js/playtest/record.mjs`):
  - Playwright with SwiftShader. It stops the game's own loop and advances it a fixed step per frame,
    so recordings come out the same every time.
  - Fails on any page or console error. Checks the encoded video with ffprobe.
  - Needs the page to expose `window.__A3GAME_GAME__ = {host:{tick,stop,renderer,scene,camera}, input, getState}`.
    The web template could define that.
- **Unreal automation tests** (`engine_adapters/ue5/testing/client.py`):
  - Command: `UnrealEditor-Cmd.exe <uproject> -unattended -nosplash -NoSound -stdout -FullStdOutLogOutput "-ExecCmds=Automation RunTests <filter>; Quit" "-TestExit=Automation Test Queue Empty" -ReportExportPath=<dir>`.
  - Pass only if all of these hold: the report was rewritten by this run, at least one test ran, none
    failed, was skipped or didn't run, and the counts agree.

### Importing assets into the engine

- **Unreal** (`engine_adapters/ue5/import_generated/import_mesh.py`, `import_motion.py`; checked on
  5.4 and 5.7 only):
  - Tries automated import, then the task API, then Interchange.
  - Moves the asset Interchange buries under `<file>/StaticMeshes/`, and saves every imported package,
    as Interchange leaves textures and materials unsaved.
  - Run it through Unreal's MCP Python, not a second editor. A second editor shows a stale Content
    Browser.
- **Unreal materials** (`assets/_internal/ue/scripts/material_scripts.py`):
  - sRGB off for data maps.
  - Flip the green channel for OpenGL normal maps.
  - Use complex collision as simple, and trace the PlayerStart down to the ground.
- **Godot** (`engine_adapters/godot/_internal/inspection.py`, `_scripts/inspect_resource.gd`):
  - `godot --headless --import` exits 0 even on failure, so scan its log for errors.
  - Then load the resource and report its class, skeleton bones, skinned meshes and bone tracks.
- **Godot installer** (`scripts/engine_install/godot/install.py`): pinned version, checked against
  SHA512-SUMS, safe unzip, version probe. The hash comes from the same release, so it proves the
  download is intact but not where it came from.

## Cautions

- **Secrets:** none in the repo, only test placeholders.
- **Default User-Agent:** their HTTP client names the project and its URL. Use a generic one.
- **Licences:** `models/gen_3d_scene/world_mirror_utils/` is under Tencent's restrictive licence, and
  some sample assets are CC BY-NC. Porting ideas is fine; copied code keeps its attribution.
- **Open network listeners:** their Blender and Unreal UDP listeners bind every address with no
  authentication.
