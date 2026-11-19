---
name: blender-kit-cohesion
description: Make a set of props read as one kit - a shared style paragraph in every brief, all references on one sheet before any 3D, a lineup render with a scale bar, and a critic rubric with a pass rule. Also normalises imported GLBs (metres, bottom-centre origin, front -Y) and builds a catalogued Blender asset library. Use when building several props, an asset pack or a themed set that must belong together.
license: MIT
---

# Kit cohesion

A kit is judged as a set. One beautiful item beside one off-style item is a failed kit; eight solid items that all
look like the same world is a good one. Agents that build props one at a time drift: each looks fine alone and the
set doesn't match. From AnyKit's `KIT.md` and `PROMPT.md`, with its paid generators taken out: here items are
modelled in Blender or taken from free libraries.

## 1. Plan the kit before any item
Write `kit/plan.md` (or the asset cards, in Agent Orange) with:
- **The kit's name, and one style paragraph** that goes, word for word, into every item's brief and every reference
  request: era, palette (three to five named colours), materials, level of wear, level of detail, and one invented
  maker whose name goes on every label. No real brands.
- **Every item:** an id (`snake_case`), a label, its real height in metres (or `fit` dims for boxy things), hero or
  small, and why it's in the set. Cover a range of sizes and silhouettes.
- **The dials, as the brief:** count; variety (0 = variations of one object family, 1 = a broad mix sharing only the
  style); detail (low / medium / high, mapped to the triangle tiers in `prop-artist`); wear (0 pristine to 1
  decayed); wetness; warmth (-1 cold to 1 warm); look (clean, cinematic, gritty, vintage). A dial the person set is
  an instruction. One they didn't: pick the one value the references show, and say which.
- **Real sizes, looked up, not guessed:** a desk phone is about 0.12 m tall, a coffee mug 0.095 m, a ring binder
  0.32 m, a desk lamp 0.45 m, a door 2.0-2.1 m, a kitchen counter 0.9 m, a dining chair seat 0.45 m.

## 2. Cohesion comes from the references, so gate them
Make or gather one reference picture per item, each with the style paragraph and the same camera and light: "the
entire object, centred, three-quarter front view from slightly above, plain light-grey background, soft even light".
Then **put them all on one sheet before any 3D**:

```python
import sys; sys.path.insert(0, r"<skills_dir>/blender-kit-cohesion/scripts")
import _anyx as ax
tiles = [ax.fit_height(ax.load_pixels(p), 400) for p in ref_paths]
px, w, h = ax.sheet(tiles, 4); ax.save_png(px, w, h, r"kit/refs_sheet.png")
```
Check the sheet: the right objects, one style, one palette, the same wear, no real brands, nothing cropped. Redo the
outliers now; this is the cheapest place to fix cohesion. A whole-scene style picture is a guide to the world, not
a list of things to copy.

Where items come from: modelled in Blender (`blender-reference-first` for complicated ones, `prop-artist` for
simple ones), or free libraries such as Kenney, Poly Haven and Quaternius (all CC0; check each asset's licence). An
image-to-3D service set up under Agent Orange's Third Party settings is optional, never required.

## 3. Normalise every item the same way
`scripts/normalize_import.py` turns a GLB into a kit item: one object, real metres (`--h` uniform, or `--fit X Y Z`
per axis for boxy things whose depth is wrong), bottom-centre origin, front facing -Y (`--yaw` turns the mesh data;
check with the `model-facing` skill), textures at most `--tex` px, and a `stats.json`. Batch it: one Blender session
for the whole kit, which also writes `kit.json`.

```
blender -b --factory-startup -P <skills_dir>/blender-kit-cohesion/scripts/normalize_import.py -- --jobs kit/prep.json
```
`prep.json`: `{"name", "style", "items": [{"id", "label", "glb", "h" | "fit": [x,y,z], "yaw"?, "tags"?, "why"?}]}`.
Items whose GLB isn't there yet are skipped; run it again as they arrive. Triangle budgets and LODs:
`blender-game-optimization`'s `make_lods.py`. Grid, pivot and texel-density checks for modular pieces:
`blender-environment-kits`' `kit_check.py`.

Gotchas, if you write your own import or fix scripts:
- glTF imports use QUATERNION rotation mode, so setting `rotation_euler` is silently ignored. Set
  `rotation_mode = "XYZ"` first, or bake the transforms into the mesh as the script does.
- glTF images arrive packed. Calling `pack()` again on an image you didn't change drops its data and the item renders
  magenta. Repack only the images you changed.
- Pass absolute paths to Blender, or relative ones resolve against the wrong folder.

## 4. The lineup: the kit's cover and its scale check
```
blender -b --factory-startup -P <skills_dir>/blender-kit-cohesion/scripts/lineup.py -- --kit kit/kit.json \
    --out kit/lineup.png --thumbs kit/thumbs [--warmth 0.3] [--engine cycles --samples 128]
blender -b props.blend -P <skills_dir>/blender-kit-cohesion/scripts/lineup.py -- --collection Props --out //lineup.png
```
Every item in a row on a neutral ground, at true relative scale, with a scale bar and a label in front of each, under
a three-point studio light whose key follows `--warmth`; plus a three-quarter thumbnail per item. Open it. A mug the
size of a fridge, a style outlier and a cut-off item are all obvious here.

## 5. The loop
What separates good kits from first drafts is a loop run many times, not a setting:
1. **Render and look.** After every change, render the views that matter, small and fast. A script that ran
   without errors proves nothing about how the item looks.
2. **Name the problem precisely:** "the handset floats 2 cm above the cradle", not "the phone looks off".
3. **Write the fix as a script** in `kit/scripts/`, so a rebuild keeps it.
4. **Check the fix from a view that shows it.** A three-quarter thumbnail hides a floating handset; a close, low
   view shows it at once.
5. **Get an outside opinion** (the critic, below): the agent that built a kit stops seeing its flaws.

Keep `kit/NOTES.md`: one line per thing that cost time.

Assembly is part of the kit: cords plugged in at both ends, lids on their hinges, lamp heads on their arms, nothing
hovering or intersecting. `blender-asset-qa`'s `qa_check.py` finds detached parts inside an item.

## 6. The critic
Run it on the lineup plus the thumbnails, either yourself in a fresh pass or, better, as a second agent that didn't
build the kit (in Agent Orange, a new conversation given only the pictures, `kit.json` and this rubric). The
reference sets the world, not designs to copy; judge at normal viewing distance, as a base a studio's artists would
then refine. No nitpicking a creative choice or something an artist would fix in ten minutes.

Score each 1-10 (7 = a solid, usable kit; 9 = exceptional):
- **cohesion:** one set: shared palette, material language, era, wear and level of detail;
- **reference fidelity:** it belongs in the references' world;
- **brief:** the items are what was asked for, at the asked count, nothing off-topic;
- **mesh quality:** clean, recognisable shapes, nothing broken, blobby or melted;
- **texture quality:** believable surfaces, nothing untextured, smeared or flat grey;
- **assembly:** parts that belong together are connected and seated;
- **scale:** each item's real size is right, alone and beside the others (the scale bar, `kit.json` dims);
- **labels:** no real brands, no garbled text on hero labels.

Then at most five fixes, most damaging first, and only what would stop a studio using the kit as a base. **It
passes with no category below 6 and an average of 7 or more.** Fix the top problems, re-render, critique again; stop
after about eight rounds, or when three rounds in a row bring no improvement, and say what's left.

## 7. The asset library
```
blender -b --factory-startup -P <skills_dir>/blender-kit-cohesion/scripts/asset_library.py -- --kit kit/kit.json \
    --out kit/library/kit.blend [--name "Night market"] [--tags street food]
```
One `.blend` where every item is an asset (label as description, tags, one catalog for the kit, `dims_m` and `tris`
as properties, a preview where Blender can make one, `<id>_lod1.glb` in a hidden LOD1 collection), packed, with a
`blender_assets.cats.txt` beside it. Add the folder as an asset library in Blender's preferences and the kit is in
the Asset Browser for every scene and project. `--objects` / `--collection` build it from a .blend instead.

## Related skills
- `blender-art-direction` (arjun988's pack): one note per genre or mood; this skill writes the project's own style
  paragraph and holds every item to it.
- `prop-artist` (arjun988's pack): tiers and kitbash rules for one prop.
- `blender-references` (arjun988's pack): matching one reference picture.
- `blender-asset-qa`: the per-asset mechanical check, run on every item.
- `blender-lighting-lookdev`: `turntable.py` for one item turning.
- `blender-reference-first` and `blender-placement-qa` in this pack.
