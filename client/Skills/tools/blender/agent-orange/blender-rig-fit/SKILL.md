---
name: blender-rig-fit
description: Check by machine that a skeleton fits its mesh, for any body - biped, quadruped, bird, tail or wings - by shape, never by bone names. Finds limbs, tails and wings with no bone through them, joints outside the mesh, bones with no partner on the other side, and vertices no bone moves. Use after any auto-rig or hand-made rig, before weight painting, animating or retargeting onto it, and on a rigged model from outside.
---

# Rig fit

A rig can have good names, normalised weights and clean rolls and still not fit its model: a back leg that follows
the hips because the rigger missed it, a knee outside the thigh, one shin shorter than the other. Nothing that reads
names finds that, and on a creature there are no standard names to read. This measures the skeleton against the
mesh's shape instead.

## Run it
```
blender -b character.blend -P <skills_dir>/blender-rig-fit/scripts/rig_fit.py -- --armature Rig --meshes Body \
    --report //rig_fit.json
```
Live through `execute_blender_code`: `import rig_fit; print(rig_fit.run(armature="Rig", meshes=["Body"]))`. It changes
and saves nothing, and measures the rest pose whatever the rig is doing. `--strict` exits 1 when it doesn't pass.

- **Name the body with `--meshes`.** Clothes made of single sheets (a cloak, a skirt) aren't limbs and have no bone
  through them, so they read as ones without. Left out, every mesh that follows the armature is measured.
- **For a GLB or FBX,** import it into an empty file first. Split vertices are joined for the measuring, and a glTF's
  guessed bone tails are only used at the ends of chains.
- **Stand the model up first:** Z up, facing -Y (`model-facing`). Lengths are shares of its height, and sides are
  left and right of its middle in X.

## What it finds, and the usual fix
| Finding | Means | Usual fix |
|---|---|---|
| a limb, tail or wing with no bone through it | the part follows a bone that's far from its middle; `follows` says which | add the chain there, or rig again with a rigger meant for that kind of body, then weight it |
| a small part moved rigidly (warning) | an ear, horn or finger with no bone of its own | fine if it should be stiff; add a bone if it should move |
| joints outside the mesh | it will swing instead of bend | move the joint to the middle of the limb in edit mode, then weight again |
| bones with no partner | one side has a joint the other doesn't have at the mirrored place | `armature.symmetrize` from the good side, then weight again |
| vertices follow no bone | they stay behind when the rig moves | `bind_mesh.py` in `blender-character-rigging` |
| sides weren't compared (warning) | the mesh itself isn't symmetric | judge the sides by eye |

Fix the skeleton before the weights: weights painted onto a joint in the wrong place are wasted.

The numbers that decide are arguments: `--limb` (0.15 of the height, from which a part counts as a limb),
`--joint-slack` (0.01) and `--mirror-slack` (0.03). Raise one only with a reason you can give, such as a hand posed
differently on purpose.

## Choosing between several rigs
When a rigger gives several tries, run it on each and keep the one with no errors. With errors in all, fix the one
whose errors are fewest and nearest the body's ends (a tail tip is an easier repair than a hip).

## What it can't tell
A pass says the bones are in the right places. It doesn't say the weights bend well or the joints turn the right
way: pose it (`stress_poses.py` in `blender-character-rigging`) and look, then animate and use `blender-motion-verify`.
A bone in the right place with the wrong parent also passes, so look at the hierarchy once.

## Related skills
- `blender-character-rigging`: builds and weights rigs; its `rig_check.py` checks names, rolls, weights and engine
  profiles. Run both: they share almost nothing.
- `blender-motion-verify`: clashes, limits, foot slip and the export, once it moves.
- `blender-animation`: `retarget.py` maps bones by name, so a creature needs its own JSON map.
