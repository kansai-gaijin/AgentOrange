# Agent Orange's own Blender skills

Skills written for Agent Orange, beside the packs it brings from elsewhere. Every agent session gets them while
Blender is set up (`client/ToolSkills.cs`). In them, `<skills_dir>` means this folder.

| Skill | Why |
|---|---|
| `blender-rig-fit` | The other packs check a rig by its names, weights and motion. Nothing measured the skeleton against the mesh, which is what goes wrong on creatures. |
| `blender-any-body-motion` | The other packs' retargeting maps bones by name and copies turns, which needs matching rest poses. Nothing put motion on a creature, and slip was measured but not fixed. |
| `blender-hair-shapes` | `blender-hair-grooming` combs guide curves, which by script gave even, helmet-like hair with no clumps and blunt tips. This blocks the style out as shaped locks and converts them to strands with Geometry Nodes, so the locks are the clumps. Agents also eyeballed the references, so it fits a camera to each reference and measures the hair against it in millimetres. The hair stays editable by hand, bakes to brushable curves and exports Alembic grooms for game engines; its scripts folder is also a Blender add-on. |

The scripts follow the contract the other packs use: `run(**kwargs)` live through `execute_blender_code`, and
`blender -b f.blend -P script.py -- --args` headless. Each skill's `_anyx.py` is the any-family pack's helper
(`../any-family`, MIT licence, `../any-family/LICENSE-any-family.txt`), unchanged. Limbs are solved with FABRIK
(Aristidou and Lasenby, 2011).

All were run headless on Blender 5.3, on made bodies:
- **`rig_fit.py`:** a whole quadruped (passes, also as a GLB), one with a leg with no bone, a joint outside, a knee
  lower on one side, a tail with no bone and a foot hanging from the root (each found), a Rigify human (passes), and
  several GLBs against each other. Not run on an auto-rigger's output.
- **`retarget_any.py`:** a made walk on a person, a fox, a bird and a six-legged body, from an armature and from BVH
  files in centimetres with Z up and Y up, and looked at. Not run on real mocap.
- **`plant_feet.py`:** that walk with its travel cut to 85%: 105 mm of slide in a stance brought to 0. Measured afresh
  it reads 27 mm, as the frames where a foot eases in and out then count as part of its stance.
- **`hair_shapes.py --selftest`:** a sphere head with bangs, side locks, a pigtail into a braid and tail, and a
  creased mesh strip. The conversion gives one guide set per lock (309 guides, all the right length, roots first),
  then about 29,000 strands, strays and baby hair, rendered before and after. Its conversion follows the
  mesh-to-hair technique Xeofrios shared (80 Level, 2023), built anew here.
- **`hair_match.py`:** two agents' finished hairstyles on a real head, against front, 45, side and back references
  drawn on it. Cameras fitted to within 2–3.4% misfit. The detail measure tells the smooth, sheet-like hair (0.4–0.8 of
  the reference's) from the clumpier one (0.8–1.3), and both came out lighter than the reference, as they looked.
- **`stack` and `sections`:** one agent's Hime blockout, whose back locks poked through the top layer all over (42%
  and 48% of the back view). Stacked, the top layer lies over them (89%). The crossings too deep to lift were listed
  with their places: the bangs and sides rooted on the same patch as the top layer.
- **Baking and export:** one section of a finished Hime. Its guides baked to 1,880 real curves with the 13,160 strands still
  live on them, and moving half of each guide 5 mm moved the strands 2.5 mm on average. Baked, unbaked and baked again
  to the same 13,160 curves. Followed a head lifted 1 cm by exactly 1 cm. Exported with two braids as a three-group
  Alembic groom, which read back at the right size in centimetres with its widths. The add-on's buttons ran the same
  chain headless, and it builds as an extension. Not yet tried: brushing by hand, and importing into Unreal.
