---
name: blender-placement-qa
description: Check a dressed scene or level for props that float, sink or clip, measured at their real contact points (feet, legs, base rings) rather than bounding boxes, with a JSON report. Reports can be accepted with a reason, and dressing scripts get drop() and settle() helpers that rest props by their feet. Use after every set-dressing pass and before final renders or export.
license: MIT
---

# Placement QA

Props that float or clip are the commonest flaw in agent-built rooms and levels, and they're nearly invisible in a
wide shot. A bounding-box check misses them whenever the box bottom isn't where the prop touches down: splayed chair
legs, a decimated foot, an origin that's off. AnyScene's notes record a stool 74 cm up on a table edge, and stools
standing on table tops that counted as grounded. From AnyScene's `pipeline/grounding.py`, the contact helpers in its
dressing script and its `validate.py`, made generic: every scene-specific list became an argument or a custom
property.

## The check
```
blender -b level.blend -P <skills_dir>/blender-placement-qa/scripts/placement_check.py -- --collection Props \
    --fixed Walls Counters --ground Floor Street --ground-only Chair Table Stool --report //placement.json
```
| Report | Means | Usual fix |
|---|---|---|
| `FLOAT` | the lowest vertices are more than `--tol` (1 cm) above what's under them | `settle()` it; or mark it `hanging` if it's wall-hung |
| `SUNK` | more than `--sink` (3 cm) into the surface below | raise it, or re-`drop()` it from above |
| `NOTHING` | nothing at all below it | it's off the set, or hanging in the air |
| `GROUND` | a ground-only prop (furniture) stands on another prop | move it to clear floor and `drop()` it again |
| `CLIP` | two props intersect (the higher one is tested 3 mm up, so resting on each other is fine) | move the smaller one, or accept it with a reason |
| `STRUCTURE` | a prop goes into the fixed set: a wall, a counter, a pillar | move it out; check the fixed set's own layout |
| `SCALE` | its height is more than 15% off its real height (`real_h` property or `--heights`) | rescale it (`blender-kit-cohesion`'s `normalize_import.py`) |
| `OUT` | outside `--bounds` | bring it back in, or delete it |

`"pass": true` means no open reports. Every report is either fixed or accepted, with its reason, never ignored:
`--accept "CLIP Wok x Range=the wok sits in the burner well"` (a regular expression on the report's text, then the
reason). Run it with `--strict` to make the run exit 1 while anything is open.

## Mark the scene so the check knows what things are
Custom properties on the objects (or name parts, through the arguments):
- `no_support`: not something to rest on, and not checked: hanging signs, lamps, cables, decals, glass. Volumes and
  volume-only materials (haze, steam, fog boxes) are skipped by themselves.
- `hanging`: checked for clipping and scale, but allowed to float (a wall clock, a pendant lamp, a shelf on a wall).
- `ground_only`: must stand on the ground (`--ground`), never on another prop. Furniture, mostly.
- `real_h`: the prop's real height in metres, for `SCALE`.

## Dress by contact points
Import the helpers into the dressing script, so a rebuild stays grounded:
```python
import sys; sys.path.insert(0, r"<skills_dir>/blender-placement-qa/scripts")
import placement_check as pc
pc.drop(stool)          # rest on the highest thing under its footprint, then settle by its feet
pc.settle(plate)        # move down (never up) until its first point touches: feet and a spread of all vertices
gap, under = pc.contact_gap(chair)   # > 0 floats, < 0 is sunk; `under` is what it stands on
```
- Re-`drop()` after any move. Fix floaters in the dressing script, not only in the saved file.
- `--fix` lowers every floater by its smallest gap and settles it, then saves with `--save`. Check what moved.
- Run the check after every dressing pass, and again on the file the finals or the export come from.

## Then look, from low down
A floating chair is obvious from knee height and invisible from eye height. Render a contact sheet of low-angle
stills along the main furniture, with the camera about 30 cm off the floor: a static shot per area in
`blender-shot-rig` (`"pos": [x, y, 0.3]`, `--still 1`), or `blender-asset-qa`'s `review_shots.py` on a group of
props. Open them before calling the pass done.

## Related skills
- `set-dressing` (arjun988's pack): density, clusters and story in placement; this skill checks the result.
- `blender-level-design`: player measurements and the grey-box; run this check on its dressed pass.
- `blender-asset-qa`: floating parts inside one asset; this skill is props against the scene.
- `blender-environment-kits`: `scatter.py` for vegetation and rubble, which this check can also cover.
- `blender-shot-rig` in this pack, for the low-angle stills and for checking a camera path's clearance.
