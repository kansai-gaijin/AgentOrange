---
name: blender-level-design
description: Game levels in Blender - player measurements first (capsule, doors, corridors, steps, ledges, cover, grid), a grey-box blockout of the whole level, modular kits on the grid with linked duplicates, simple collision named for the engine, and export. Use when blocking out or building a level, a room or a modular kit in Blender for Unreal, Godot or the web.
license: MIT
---

# Level design

## Measurements first

Agree on the player's size and movement with the design (the level's and mechanics' cards), then build
everything to them. Defaults for a human-scale third- or first-person game, to adjust to the game's
engine and genre:

| Thing | Size |
|---|---|
| Player capsule | 1.8 m tall, 0.6 m wide |
| Doorway | 1.2–1.5 m wide, 2.4–3 m tall (wider than real life) |
| Corridor | 3–4 m wide |
| Step | 0.2 m rise, 0.3 m run |
| Highest ledge to climb | about 1 m (jump) |
| Cover (crouching / standing) | 1.0 m / 2.0 m |
| Grid unit | 1 m, with modules on 2 m or 4 m |

Put a scale reference in the scene, a 1.8 m capsule named `PlayerScale`, and keep it there. A small
or stylised character (a cute animal on a tiny planet, say) changes every number in this table: scale
the table to the character, and write the numbers you settle on into the level's card.

## Blockout

- Grey-box the whole level with simple shapes before any art: rooms, paths, heights, sightlines. Use
  one collection per area (`Area_Entrance`, `Area_Arena` and so on).
- Snap to the grid: round positions and sizes to the module size in your scripts.
- Design the critical path first, then branches, loops and secrets. Landmarks (tall, distinct shapes)
  help players find their way. Check they can be seen from where the player needs them, from the top
  and at eye height.
- A top view is the fastest way to review flow and spacing. Render one with `execute_blender_code`, or
  a review sheet with `blender-asset-qa`'s `review_shots.py` (its views include `top`), and look at it.
- Then play it in the engine: the `play-and-review` skill. Distances feel different with the real
  camera and speed.

## Modular kits

- Build pieces on the grid: a 4×3 m wall, a 4×4 m floor, a corner, a doorway, stairs. Put the origin at
  a corner or the bottom centre so pieces snap together.
- Place them as linked duplicates (`obj.copy()` without copying the data) or collection instances, so
  fixing one piece fixes every copy and the file stays small.
- Scatter props with a little random rotation and scale; keep props that matter to gameplay exact.

## Ready for the engine

- Apply transforms (scale 1, rotation 0) on every mesh you export.
- Name things for the engine. Unreal: `SM_` meshes and `UCX_<mesh>` convex collision. Godot: `-col`,
  `-convcol`, `-colonly` or `-convcolonly` suffixes. The web: whatever the game's loader expects.
- Keep collision simple: boxes and convex hulls, not the render mesh.
- Triangle budgets depend on the platform. Get the counts with `get_objects_summary` or
  `blender-game-optimization`, and keep within the game's budget.
- Export with `blender-export-pipelines`' presets (glTF for Godot and the web, FBX or glTF for
  Unreal). Check the arguments in the running Blender first, as they change between versions.
- Put lightmap UVs in a second UV map when the engine bakes lighting.

---

Adapted for Agent Orange from mcp-for-blender's level design guide
(`src/blender_mcp/guides/level-design.md`, github.com/ahujasid/mcp-for-blender), MIT licence
(`../LICENSE-mcp-for-blender.txt`): its tool names changed for the Blender MCP Agent Orange uses, and
Agent Orange's cards, skills and Godot's other collision suffixes added.
