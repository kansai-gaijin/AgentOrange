---
name: blender-any-body-motion
description: Put a person's motion (a BVH, mocap or an animated rig) on any standing body - biped, bird, four legs or more - reading both skeletons by shape, so bone names, counts, rolls and rest poses don't have to match; and stop feet sliding in any action. Use for mocap on a creature or on a rig with its own bone names, for a BVH of unknown scale or axis, and whenever feet skate.
---

# Motion on any body

`blender-animation`'s `retarget.py` copies bone turns and maps bones by name: right for one person's rig to another
with known names and a matching rest pose. This is for the rest. It carries over where each limb *reaches* from the
body and how far it's stretched, then solves the target's limb to reach the same way, however many bones it has.

Check the rig first (`blender-rig-fit`), and run these on the skeleton you export, not on a control rig.

## See how it reads a skeleton
```
blender -b rig.blend -P <skills_dir>/blender-any-body-motion/scripts/body.py -- --armature Rig
```
It names the hips, spine, chest, neck, tail, each leg (side, place from the back, bones, foot) and each arm. Read it
before going on: everything else relies on it. The body has to stand on the ground, Z up, facing -Y, its middle in X
(`model-facing`), with its scale applied. If it's wrong, the rig's rest pose is usually why: a leg off the ground, a
limb starting off the middle line, or a stray bone below the feet.

## Put motion on it
```
blender -b rig.blend -P <skills_dir>/blender-any-body-motion/scripts/retarget_any.py -- --target Rig --bvh walk.bvh \
    --name Walk --save out.blend            (or --source Mocap, an animated armature in the file)
```
Live: `import retarget_any; print(retarget_any.run(target="Rig", bvh="//walk.bvh", name="Walk"))`. `--plan` says what
would follow what and changes nothing. It makes a new action, `Rig|Walk`, and keeps the others.

- **The source is a person,** standing in its rest pose. A BVH is stood up and turned by its own shape, so its axes
  and units don't matter, and travel is scaled by leg length. An armature given with `--source` has to stand already.
- **Set the scene's frame rate to the mocap's first** (a BVH's `Frame Time`): frames go over one to one.
- **Legs:** hind legs follow the person's, front legs the opposite one (a trot), half as far. Six legs or more
  alternate, with shorter steps.
- **Arms** follow arms on an upright body. On any other, wings and hanging forelimbs go with the body.
- **A body that isn't upright** follows `--lean` (0.35) of the person's leaning, so a bow doesn't put its nose in the
  ground. Raise it for a lively body, lower it for a heavy one.
- **Planted feet stay put.** Where the person's foot is planted the target's is held, and the body sinks a little if
  a leg can't reach. `--in-place` leaves the travel and the planting out, for a cycle the engine moves.

Read `planted_slip_mm` and `planted_out_of_reach_mm` (both near 0) and `most_lowered_m` (a few hundredths: more means
the target's legs are too straight at rest, so bend them slightly in the rest pose).

## Stop feet sliding
```
blender -b rig.blend -P <skills_dir>/blender-any-body-motion/scripts/plant_feet.py -- --armature Rig --action Walk --check
blender -b rig.blend -P <skills_dir>/blender-any-body-motion/scripts/plant_feet.py -- --armature Rig --action Walk --save out.blend
```
For any action on any body. It finds when each foot is down and hardly moving, holds it at the middle of where it was
and solves the leg, into a new action. `--check` only measures. A cycle that walks on the spot slides by design, so
it isn't for those. A foot that slides fast isn't read as planted at all: fix the travel first, so the body moves
about as far as the steps say.

## Then look
Render frames from the side and the front through the whole clip, and look at every one: knees bending the right way,
the body's height, what the spine does. Then `motion_check.py` (`blender-motion-verify`) for clashes and slip against
the mesh. Person's motion on an animal is a start, not a finish: timing and weight are a person's. Use it for a first
pass, a funny animal or a two-legged creature; a four-legged gait that has to convince is better keyed, with
`plant_feet.py` at the end.

## What it doesn't do
Fingers, faces and toes; wings beating; creatures without legs; stairs and slopes (planted feet are put at the rest
pose's ground); sitting, lying or crawling, as feet are the only contacts it knows.

## Related skills
- `blender-animation`: named rigs, baking control rigs, loops, root motion and clip export.
- `blender-rig-fit`: whether the skeleton fits its mesh. `blender-motion-verify`: clashes, limits, slip and the export.
