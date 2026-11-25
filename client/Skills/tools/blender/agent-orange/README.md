# Agent Orange's own Blender skills

Skills written for Agent Orange, beside the packs it brings from elsewhere. Every agent session gets them while
Blender is set up (`client/ToolSkills.cs`). In them, `<skills_dir>` means this folder.

| Skill | Why |
|---|---|
| `blender-rig-fit` | The other packs check a rig by its names, weights and motion. Nothing measured the skeleton against the mesh, which is what goes wrong on creatures. |

The scripts follow the contract the other packs use: `run(**kwargs)` live through `execute_blender_code`, and
`blender -b f.blend -P script.py -- --args` headless. `_anyx.py` is the any-family pack's helper
(`../any-family`, MIT licence, `../any-family/LICENSE-any-family.txt`), unchanged.

`rig_fit.py` was run headless on Blender 5.3 on made bodies: a whole quadruped (passes, also as a GLB), one with a leg
with no bone, a joint outside, a knee lower on one side and a tail with no bone (each found), and a Rigify human
(passes). It hasn't been run on an auto-rigger's output.
