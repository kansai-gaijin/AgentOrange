# Blender skills, mcp-for-blender, dream-loop and graphify

Read in full on 2026-11-15. What Agent Orange took, and what's worth knowing for later.

## EL4CTEO/blender-skills (MIT, `b1b21df`)

28 skills for Blender 5.2 LTS and newer, verified on 5.3, each with scripts tested in a real Blender. They
include a table of 5.x API changes that trip agents up, such as `Action.fcurves` moving to channelbags,
`scene.node_tree` to `compositing_node_group`, and `BLENDER_EEVEE_NEXT` back to `BLENDER_EEVEE`.

**Taken whole.** They're Agent Orange's main Blender skills (`client/Skills/tools/blender/blender-agent-skills`,
built by `tools/import-blender-agent-skills.py`; the folder's README says what changed). Their scripts make
no network calls. `run_in_blender.py` starts Blender in the background.

## arjun988/blender-skills (MIT, `904f095`)

**Taken earlier; now trimmed.** 30 of its skills are covered better by EL4CTEO's and were dropped. The 17
it alone has, plus its art-direction notes and references, stay
(`client/Skills/tools/blender/blender-skills-arjun988`).

## apekshik's AnyMachine, AnyScene, AnyKit and AnyShot (MIT)

Read on 2026-11-19, after this note. **Their free methods and scripts were taken, rewritten**, as five skills in
`client/Skills/tools/blender/any-family`. See `any-family.md`.

## ahujasid/mcp-for-blender (MIT, `8f40787`)

The community Blender MCP: a FastMCP server and a Blender add-on talking over TCP on localhost:9876,
with no authentication. Its tools: `get_scene_info`, `execute_blender_code`, `look` (an offscreen
render), `search_assets` and `import_asset` (Poly Haven, Sketchfab, Poly Pizza), `generate_3d` (Tripo,
Hunyuan3D, Hyper3D Rodin, Meshy) and `generate_gaussian_world` (World Labs).

**Taken:** its level design guide, as `blender-level-design`
(`client/Skills/tools/blender/mcp-for-blender-guides`). Its other guides (bpy, scene, materials,
animation, rigging, retopology) are good and right for 5.x, but EL4CTEO's skills cover them. The authors
have switched the guides off while they measure whether they help.

**Don't use the server or the add-on:**

- **Telemetry, always on:** an install ID, tool names, timing and versions go to the authors' Supabase,
  unless `DISABLE_TELEMETRY` is set.
- **Telemetry, with consent:** the person's words (most tools take a `user_prompt` argument "quoted
  verbatim"), code, screenshots, scene contents and their own actions in Blender.
- **The terms:** consenting grants a perpetual licence to that data for AI training and public datasets.
- **Unsafe defaults:**
  - The socket has no authentication.
  - The update check fetches `addon.py` from GitHub with no signature.
  - There's a shared trial key for Rodin.
  - A "premium" hint puts adverts into the model's replies.
- **It would duplicate AO's MCP:** running it beside Blender Lab's MCP would be a second way in to run
  code.

**Asset sources, for when agents fetch assets** (endpoints are in its `addon.py`):

| Source | API | Key | Licence |
|---|---|---|---|
| Poly Haven | `api.polyhaven.com` (thumbnails `cdn.polyhaven.com`) | none | CC0; they ask apps to say where content came from |
| Poly Pizza | `api.poly.pizza/v1.1` | `x-auth-token`, free | CC0 or CC-BY per model |
| Sketchfab | `api.sketchfab.com/v3/search`, `/models/{uid}/download` | `Authorization: Token`, free account | per model, shown in search results |
| World Labs | `api.worldlabs.ai/marble/v1/worlds:generate` | `WLT-Api-Key` | paid, about 5 minutes a world |

- **Poly Haven:**
  - Tile textures by `scale_mm`: the scale is the surface's metres divided by the texture's metres.
  - Import models from their `.blend`, with glTF as the fallback.
  - Map names are case-sensitive (`Diffuse`, `Rough`, `Metal`, `Displacement`, `nor_gl`, `nor_dx`).
    Only the albedo is sRGB.
- **Poly Pizza:** its query parameters must be Capitalised, or they're silently ignored.
- **Sketchfab:** upstream doesn't keep a model's licence with it. Agent Orange should record every
  imported asset's source and licence in its card.
- **Paid generation:** Tripo and Meshy are in `gamefactory-3a.md`. The same lessons apply: stop the same
  request being paid for twice, keep job handles so a wait can resume, count Meshy's text-to-3D as two
  charges, and put the person's yes before each.
- **Ideas:**
  - Its `look` renders an offscreen contact sheet of several angles, or a strip of frames, with
    `gpu.types.GPUOffScreen.draw_view3d` tiled in numpy, and puts every setting back. Cycles draws
    nothing offscreen.
  - A local log of how many tokens each tool result costs would show how much context screenshots take.

## achimala/dream-loop (MIT, `9bddb90`)

Build toward a target picture, with a fresh-context judge scoring composition, lighting, materials and
detail each round, and rules for stopping or rethinking when progress stalls.

**Taken** as Agent Orange's `dream-loop` skill (`client/Skills/dream-loop`).

- **Changed:**
  - The target comes from the card's concept art first, and the person has the final say.
  - Paid generation needs their yes.
- **Left out:**
  - Its Plus mode, which tells big models to stop and ask for a smaller one.
  - Its subscription tiers.
- **fal.ai, for image-to-3D later:**
  - Tripo H3.1: `tripo3d/h3.1/image-to-3d`, `{"texture":true,"pbr":true,"face_limit":200000}`, about
    $0.30 an asset.
  - TRELLIS: `fal-ai/trellis`, with no `/image-to-3d` suffix, `{"mesh_simplify":0.95,"texture_size":1024}`,
    about $0.02.
  - Its `fal-batch.mjs` never repeats an accepted or uncertain submission.
  - The input image is the target picture, cropped by image generation to the one object on a plain
    background, so the model matches the target.

## Graphify-Labs/graphify (Apache-2.0, with code from before 0.9.25 also MIT; `d4a5b32`)

Maps a project into a knowledge graph an agent queries instead of searching files. Code is parsed
locally with tree-sitter. Documents, PDFs and pictures go through an LLM.

**Not taken.**

- **Writes to the person's own setup:** `graphify install` writes into the person's own Claude and Codex
  settings (`~/.claude`, `CLAUDE.md`, `.claude/settings.json` hooks, `AGENTS.md`), which Agent Orange
  never touches.
- **Can spend their keys or subscription:** it picks up whatever API keys are set, and with none it runs
  `claude -p` on their subscription, unless `GRAPHIFY_BACKEND=none`.
- **Weak for games:** it can't read Blueprints, `.uasset`, `.umap`, GDScript or Godot scenes, and
  Unreal's macros trip its C++ parser.
- **Cloud upsell:** its skill text pushes its cloud product.

**Worth building into Agent Orange itself, as ideas:**

- **The design as a graph:** cards, tasks and documents are one already. Add links between card
  documents; a path from one card to another (a character to a level to a mechanic); "explain" (a card
  and what it's tied to); the most-connected cards; clusters; and a search that walks outward from what
  matches, up to a size limit, for an agent's context.
- **Its extraction rules** (`graphify/skills/claude/references/extraction-spec.md`):
  - every link tagged extracted, inferred or ambiguous;
  - a few fixed confidence values rather than any number;
  - each file's coverage recorded, so a cut-off read shows;
  - never a node made from a file name alone.

  Good for an agent that links the design's cards.
