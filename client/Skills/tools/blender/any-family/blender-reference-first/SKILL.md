---
name: blender-reference-first
description: Model a complicated object reference-first - reference sheets, a parts list, then the whole model as one script - checked beside each reference and by machine before anyone looks. Covers the close-up sheet per mechanism, bake-only detail, budget and size asserts, the compare renders (compare_views.py) and the planned second pass. Use for props, vehicles, machines and buildings with many parts, or any model built to concept art.
license: MIT
---

# Reference-first modelling

A model built from one whole-body picture comes out with the right outline and nothing underneath it: no hinges, no
fasteners, no mechanism you can follow. This method spends its effort on references first, so that every part is
modelled from a picture of that part, and makes the model a script, so a design change is a script change and the
whole thing rebuilds. From AnyMachine's `PIPELINE.md` (both parts), with the paid image and video services taken out.

Moving machines and complicated static objects use the same order. Static ones skip the rig and animation steps.

## Order of work

### 1. References, approved set by set
- **Where they come from:** the project's own concept art first (in Agent Orange, the asset's or character's card:
  agent-orange's `read_card` and `read_picture`), then pictures the person gives you, then any image model you have.
  Codex's own image generation works, and so do paid image services if the person has set one up, but nothing here
  needs them. Image-to-3D services are no substitute for references: a generated mesh is fine as a background prop
  nobody walks up to, or as a skin to measure joints off, never as a model with working parts.
- **An overall sheet first:** one object from six directions on plain grey, five of them true orthographic
  elevations at one scale, no labels, with the real size in metres stated.
- **Then one close-up per mechanism, not per body area:** hip, knee, hinge, handwheel, latch. Then front, rear and
  side or top angles of each, because a joint can't be modelled from one side. Simple fixtures need two sheets;
  something with several mechanisms needs many more.
- **State the scale in every request** ("the joint housing is 0.4 m across"), or details come back sized for a toy.
- **Each new picture is made from the ones before it** (the turnaround from the concept, the close-ups from the
  turnaround), so the design doesn't drift.
- **A person approves each set before the next.** A wrong turnaround makes every later picture wrong.
- **Keep what made each picture:** save each prompt as a file beside the picture, so the set can be remade after a
  design change.
- If a part can't be modelled from what the sheets show, that's a missing sheet, not a licence to guess.

### 2. The parts list, before any geometry
Written as the header comment of the build script, from measurements taken off the elevations (pixels per metre,
where the ground and the top are). It settles:
- the axes and origin (front faces -Y; see the `model-facing` skill), and the overall dimensions;
- for anything that moves or carries load, how it carries its own weight (this question makes thin parts believable);
- every part that would be a separate piece of metal, wood or plastic, with the bone it rides on if it moves;
- where the views disagree, and which one wins;
- where you leave the sheet on purpose (taller, closed, simplified), and why.

### 3. The model is one script
One Python file run headless rebuilds everything: parts, materials, UVs, the rig and actions if any, export.
- **One part, one mesh,** touching its neighbours where the real one is bolted or welded.
- **Two kinds of geometry:** what changes the silhouette at playing distance is real geometry. Bolt heads, rivets,
  weld beads, panel seams and anything under about 8 mm is built too, but flagged bake-only (for example named
  `hi_*` or in a `bake_only` collection) so it shapes the normal map and stays out of the export. See
  `blender-texture-baking`.
- **Assert the rules, so the build fails instead of the review:**

  ```python
  # at the end of the build script: game_meshes are the exported meshes, not the bake-only ones
  for o in game_meshes:
      o.data.calc_loop_triangles()
  tris = sum(len(o.data.loop_triangles) for o in game_meshes)
  assert tris <= BUDGET_TRIS, f"{tris} tris over the {BUDGET_TRIS} budget"
  pts = [o.matrix_world @ Vector(c) for o in game_meshes for c in o.bound_box]
  size = [max(p[i] for p in pts) - min(p[i] for p in pts) for i in range(3)]
  assert all(abs(size[i] - TARGET[i]) <= 0.02 * TARGET[i] for i in range(3)), f"size {size} vs {TARGET}"
  assert not {b.name for b in rig.data.bones} & {o.name for o in game_meshes}, "a mesh shares a name with a bone"
  ```
- **Let the script choose what a person would fudge** (an actuator's anchor points, a cable's sag) by sampling the
  real motion, and fail if the choice is impossible.
- **Write the numbers out:** a `stats.json` or `<name>_info.json` beside the export with every part, bone, size and
  triangle count, for whoever imports it.
- **Headless-ready:** relative paths only, no network, flags for anything slow (`--size`, `--spp`, `--nobake`), and a
  non-zero exit if nothing was written. Then a bake or render can run on any machine as a separate job.

### 4. Compare renders: the model beside each reference
A build that ran without errors proves nothing about how it looks. Render the model from each reference's own view
and put the two side by side:

```
blender -b model.blend -P <skills_dir>/blender-reference-first/scripts/compare_views.py -- --out //compare \
    --collection Model --refs front=//refs/front.png left=//refs/left.png three_quarter=//refs/3q.png
```
- Built-in views: front, back, left, right, top (orthographic) and three_quarter. Give a reference's exact camera in
  `--views-json` when it isn't one of those (`{"hinge_close": {"eye": [...], "target": [...], "lens": 50}}`).
- Clay (`--look clay`, the default) shows the shapes; `--look material` shows the colours.
- Open every `<view>_compare.png` and write down what differs, part by part, in the model's notes. Fix it or accept
  it with a reason. Nothing is left for someone else to find.

### 5. A second pass, planned from the start
The first pass is reliably plainer than the sheets: the right parts, too few of them, too regular. Plan a second pass
that adds what the comparison showed missing. For the busiest objects it starts with one more close-up sheet of the
parts that came out as plain boxes.

### 6. Machine checks, then a person
Only when the numbers pass does a person look:
- `blender-asset-qa`'s `qa_check.py` on the game meshes (floating parts, z-fighting, normals, scale);
- for anything that moves, `blender-motion-verify`: the clash sweep, foot slip and the re-import check of the export;
- the compare images and the notes of what differs, attached to the asset's task as evidence.

## What it costs
This is thorough, not quick. AnyMachine's 30 m walker took 40 reference pictures, about 2,200 lines of script and
about 40 check renders; a helicopter took ten sheets and 950 lines. Equipment pieces were 80 to 250 lines each. The
results are still game props, simpler and flatter than their sheets: fewer pipes, blockier castings, even wear. Use
it for hero objects and anything with mechanisms; a background crate doesn't need it.

## Related skills
- `blender-hardsurface-modeling`: the modifier-stack way of modelling the parts; this skill is the order of work and
  the review loop around it.
- `blender-texture-baking`: baking the bake-only detail and the wear into the game mesh.
- `blender-asset-qa`: `review_shots.py` renders angle sheets of the model alone; `compare_views.py` here pairs each
  view with its reference.
- `blender-references` (arjun988's pack): matching one reference picture by eye; this skill is for a set of them.
- `blender-motion-verify` and `blender-kit-cohesion` in this pack.
