---
name: blender-motion-verify
description: Check a rigged, animated model by machine before anyone looks - the exported GLB re-imported and matched to the source, joints swept and every action frame checked for clashes, and foot slip measured. Each check writes a JSON report with a pass flag to attach as evidence; bone, foot and action names are arguments. Use after rigging or animating, and before handing over any export.
license: MIT
---

# Motion verify

A rig that looks fine in one pose can put a leg through the body at another, an animation can skate, and an export
can quietly lose an action or scale it. These checks find all three before a person has to, and each writes a JSON
report with a `pass` flag that can go on the asset's task as evidence. From AnyMachine's `verify_glb.py` and
`check.py` scripts (quadruped, backpacks, spider), rewritten so nothing is tied to its robots: bone, foot and action
names are arguments.

## 1. The export: re-import and compare
```
blender -b --factory-startup -P <skills_dir>/blender-motion-verify/scripts/verify_reimport.py -- \
    --source character.blend --glb export/character.glb [--objects Rig] [--report //verify.json]
```
It reads the GLB's own JSON (nodes, meshes, skins and their joint counts, animations, extras), then imports the file
into an empty scene at the source's frame rate and compares:
- **bones:** names missing from, or added to, the export;
- **meshes:** the source's against the GLB's (the importer's bone-shape sphere is left out);
- **actions:** every source action, found by name, with both frame ranges;
- **pose match:** each action posed in both at `--samples` frames, then every vertex matched to its nearest
  counterpart, both ways, as median, p99 and max in mm. A lost, scaled, shifted or retimed action shows here; a
  clean export is well under a millimetre;
- **motion:** how far each GLB action moves the mesh, so an action that came out empty is caught.

It fails on missing bones, fewer meshes, missing or empty actions, or a pose error over `--tol-mm` (1 mm). The
source is the armatures and what they deform or carry; a model without armatures is compared at rest. It loads
files, so run it headless.

## 2. The rig and its actions: clashes, limits, ground and slip
```
blender -b character.blend -P <skills_dir>/blender-motion-verify/scripts/motion_check.py -- --armature Rig \
    --feet foot.L foot.R [--in-place Walk_Cycle] [--ignore "thigh.L|thigh.R"] [--report //motion_check.json]
```
Parts are grouped by bone: a mesh parented to a bone is that bone's, and a skinned mesh is split by each vertex's
strongest deform bone. A bone and its parent touch by design, so they aren't compared (`--no-adjacent` compares
them), and nor is any pair already overlapping in the rest pose (listed as `rest_overlaps`; check those by eye).
- **sweeps:** every bone with a Limit Rotation constraint (local space), one limited axis at a time, from `--past`
  (6°) below its lower limit to 6° above its upper one, in `--sweep-step` (3°) steps. Reports the angles where parts
  clash, and how far the evaluated angle strays from the clamped one: a limit that doesn't hold.
- **random poses:** `--random` (30) poses with every limited bone inside its limits at once. Clashes here are
  warnings: some combinations may never happen in play.
- **actions:** every frame (`--step`) of every action the rig uses or that poses its bones: clashing pairs and
  frames; how far non-foot parts go below `--ground-z`; and, per `--feet` bone (with its child bones), the slip.
  While a foot's lowest vertex is within `--contact` (2 cm) of the ground on two frames running, the sideways travel
  of that same vertex is summed per stance. It fails over `--slip-mm` (5 mm) in any stance.
- Cycles that walk on the spot (the engine moves the character) slide their feet back by design: name them in
  `--in-place` so their slip isn't judged.

The rig's pose and action are put back afterwards and nothing is saved, so it can run live through
`execute_blender_code` too (`import motion_check; motion_check.run(armature="Rig", feet=[...])`), though a long
rig is quicker headless.

## Reading the results
| Finding | Usual fix |
|---|---|
| sweep clash at the end of a range | tighten the Limit Rotation, or move or reshape the part (a fork that clears its tongue) |
| `max_clamp_err_deg` over 0.5 | the constraint isn't in local space, is muted, or a driver fights it |
| action clash frames | adjust the keys on those frames; if the pair touches by design, `--ignore` it and say why |
| `ground_penetration_mm` | lift the root, or fix a foot or knee key |
| slip over the limit | pin the foot during stance (IK with a fixed target, or bake the contact), or match the root speed to the stride |
| re-import pose error | apply scale and rotation before export, check the export's frame range and sampling settings, re-export |
| action missing or empty in the GLB | the action isn't in the NLA or the export's animation mode leaves it out (`blender-export-pipelines`) |

## A rig contract worth keeping (optional)
AnyMachine's moving props give an engine one simple interface: every control is a 0-1 custom property; drivers feed
the bones and shape keys from it; each control is baked to its own one-second action named `<control>_param` (plus a
`demo` action); and the engine poses it by seeking `t = value` in that clip. Doors, hatches, flaps and equipment
states then need no engine-side rig logic. `verify_reimport.py` checks such clips like any other action.

## Related skills
- `blender-character-rigging`: `rig_check.py` checks names, rolls and weights at rest, and `stress_poses.py` poses
  the rig for a person to judge; this skill judges clashes by machine through motion.
- `blender-rig-fit`: whether the bones are in the right places in the mesh, before anything is animated.
- `blender-animation`: `loop_check.py` checks loop pops and hitches, `bake_action.py` bakes; this skill adds clashes,
  limits and slip.
- `blender-export-pipelines`: `export_asset.py` exports and summarises the file; this skill compares the export back
  against the source, pose by pose.
- `blender-reference-first` in this pack: these are its machine checks for anything that moves.
