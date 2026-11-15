---
name: model-facing
description: Decide which way a 3D model faces, which way is up and how big it really is, by looking at labelled views of it, then record the answer and fix it in the asset or its import, never in gameplay code. Use after making, generating or importing any mesh with a front (characters, animals, vehicles, weapons, doors, chests), and whenever something walks backwards, strafes when it should walk, drives boot first or faces away from the camera.
---

# Which way a model faces

A file can't tell you which way its model faces. glTF fixes up and the unit but nothing binding about
facing, so a character modelled facing one way and the same character turned round have the same
bounding box, node tree and animation clips. Only looking settles it. Get it wrong and nothing errors:
the player just runs sideways, an enemy charges backwards, a car drives boot first.

Skip this for textures, materials, sounds and animation clips: they have no front. Skip it too for a
symmetrical prop with no front, such as a crate, a barrel or a rock, and say why.

## 1. Render labelled views

Render the model from five sides with an orthographic camera standing on each axis looking at the
origin: +X, -X, +Y, -Y and the top. In Blender, do it with `execute_blender_code` through Agent Orange's
`use_tool`, saving the pictures outside the game's folder. In Unreal, use the editor's screenshot tool,
which can aim its own camera.

Name each picture after the axis its camera stands on, such as "camera on +Y", and note which axis is
to the right of the screen. Never name one "front": that assumes the answer.

## 2. Find the face

Find the one view where the subject faces the camera, by these cues in order of how reliable they are:

| Subject | Its front is the side showing |
|---|---|
| Person or humanoid | eyes, nose, mouth, chest, belt buckle, toe tips |
| Four-legged animal | snout and ears; the tail is on the other side |
| Vehicle | bonnet, grille, headlights, the windscreen's slope |
| Gun | the muzzle, which points along the barrel: here "forward" is not where a face is |
| Building, arch, doorway | the opening a player walks through |
| Chest, cabinet, fridge | the latch; the lid's hinge is at the back |

The model faces the axis the camera of that view stands on. Check it against a side view: the face must
be on the side of the screen whose axis it faces. If it isn't, the first reading was wrong.

## 3. Check up and size

- **Up:** from the top, a standing figure is a small compact shape. If the top view shows it full
  length, it's lying down: it was made with a different up axis, so stand it up first, then decide
  its facing.
- **Size:** compare its height in the engine's units with what the subject really is. A person isn't
  4.46 m tall; that's a 1.8 m person made in another unit. As a rough check: characters 0.3 to 4 m,
  weapons 0.05 to 3 m, props 0.02 to 6 m, scenery 0.05 to 40 m, landmarks 0.5 to 200 m.

## 4. Know what each place calls forward

| Where | Up | Forward |
|---|---|---|
| glTF files | +Y | a model's front faces +Z |
| Blender | +Z | a model's front faces -Y (the Front view looks at its -Y side) |
| Unreal | +Z | an actor's forward is +X; the stock character template turns its mesh -90° yaw because the mesh faces +Y |
| Godot | +Y | a node's forward is -Z; an imported model's front is +Z (Vector3.MODEL_FRONT) |

Exporters and importers convert between these, and their settings decide how. That's why you look at
the result in the game's engine, not only in the tool that made it.

## 5. Record it, and fix it in one place

Write what you found in the asset's card (`write_card` on the Assets page, appending a `## Facing`
section): the axis it faces, its up axis, its real size, the view that showed it, and that you looked.

Fix it where every user of the asset gets it: apply the rotation and scale in the source file (in
Blender, apply transforms and export again) or in the engine's import settings. Never turn it in
gameplay code: a rotation in one script is invisible to everything else that uses the asset, and gets
undone the first time that code sets its own facing.

## When you can't tell

Say so rather than guessing. A recorded guess is worse than no record, because it stops the next check.

- A face painted into a texture that didn't load: judge from the silhouette (toes, nose, backpack, tail, hair).
- A model facing a diagonal: record the angle from the nearest axis, and that it's an estimate.
- Views nobody can recognise: the model needs making again, not a note.

Don't judge facing from animation clip names: a clip called Walking moves along whatever axis its maker chose.

---

Adapted for Agent Orange from GameFactory-3A's imported asset orientation review
(`agent_skills/asset_qa/3d_object/orientation_review.md`, github.com/OpenDCAI/GameFactory-3A), Apache
License 2.0; rewritten for Agent Orange's tools and for Blender, Unreal and Godot. The licence is in
`LICENSE-GameFactory-3A.txt` beside Agent Orange's built-in skills.
