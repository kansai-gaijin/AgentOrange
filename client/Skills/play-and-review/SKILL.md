---
name: play-and-review
description: Prove a game or a change to it works by playing it, capturing evidence and reviewing that evidence against a fixed list of failures, then fixing each in the layer it belongs to. Use before saying a game, level, mechanic, asset or UI is done or playable, after putting a new asset into the game, after a retarget, re-import or axis change, and whenever the person asks whether something works.
---

# Play and review

"It compiles", "it launches" and "no errors in the log" are not evidence that a game works. A mesh can
face backwards, a weapon can sit inside the torso and a camera can clip through a wall with a clean
log. Only playing and looking finds them.

## 1. Know what you're checking

Read what the work was for before playing: the card it's about (`read_card`), its tasks (`list_tasks`)
and anything settled in the project's documents. Write down the player operations to exercise, such as
move, turn, jump, camera, the main verb (attack, fire, interact, pick up), damage and death, respawn,
win and lose, pause, and every screen the work promised. Include the edge cases the design names.

## 2. Play it

Use the game's engine through Agent Orange (`open_tool`, `tool_tools`, `use_tool`), or run it
unattended where the engine's own skills say how (for Unreal, `unreal-headless-run`). Do most of the
listed operations, not only start-up, and run the core loop end to end at least once.

- **Play as a client too.** In a game for several players, the host's view hides bugs that only
  other players see. Check each operation from a client as well as the host.
- **Freeze real-world inputs.** Fix the clock, the weather and anything else read from outside the
  game for the check, so it gives the same answer every time it runs.
- **Skip time only on a copy.** Run long time skips on a copy of a save or level, never on one
  others share.
- **Bring the tool to the front** for timed checks: some editors slow down in the background.

## 3. Capture evidence and look at it

Take screenshots from the engine at the moments that matter, or a short low-resolution recording where
the engine can make one: idle, moving, turning, the main verb, any vehicle, an effect firing, each UI
screen, a scene change. Look at every picture yourself. Never judge the game from its source or from
one screenshot.

Say what render or quality settings each picture was taken at. Don't pass a lighting check from a
picture whose settings hide lighting, such as a low-quality, unlit or preview mode. Where a data
probe can answer, such as a light's direction or an overlap test, prefer it to the eye.

## 4. Review against this list

Each row is a failure a compile or a file check can't catch.

| Area | Look for |
|---|---|
| Picture | Silhouette and topology faults, texture resolution, material response, seams, pop-in. Scenes: holes, gaps between ground and walls, a missing floor collision, draw distance, fog |
| Lighting | Exposure, harsh or missing shadows, light leaking through geometry, surfaces lit pitch black or blown out, characters lit from a different direction than the world |
| Style | Palette, contrast, post-processing and mood match what the design asks for. A clean frame in the wrong style is a failure |
| Effects | Fire on the right event and stop (no stuck loop, one-frame flash, or effect left behind after death); sensible size and intensity; at the right place and following it; sound and feedback in time with the animation |
| Facing | Characters face where they move, not strafing or walking backwards. Weapons point away from the body and shots leave along the barrel. Vehicles drive front first and steer the right way. Projectiles, mounts and drones travel the way they face. See the `model-facing` skill |
| Parts together | Weapon held at the grip in the right hand, in front of or beside the body. Wheels under the wheel arches, touching the ground, turning on the right axis. Feet on the floor; props on their tables and walls; helmets, backpacks and shields on their sockets and following the skeleton. Sensible sizes against each other. Nothing passing through anything while it animates, not only in the idle pose |
| Playability | No falling through the floor, getting stuck or jittering; the camera doesn't clip into walls or the character and frames the action; controls respond and no axis is inverted; UI readable, anchored and showing the real game state; no stutter that spoils the operations |

## 5. Fix in the owning layer, then play again

Note each finding as: what you saw, the layer it belongs to (asset, import settings, mechanic, UI,
scene, lighting, effects), the fix, and the new evidence that shows it fixed. Fix the cause where it
lives: never turn a mesh round in gameplay code to make up for an asset that faces the wrong way. Play,
capture and review again until every row passes for what you touched.

## 6. Report honestly

Say which operations you exercised, what you found and fixed, and where the evidence is. Put what's
still wrong in a task (`add_task`) rather than leaving it in a reply. If something couldn't be checked,
say so; never call it playable on the strength of the code alone. Keep evidence files out of the
game's folder unless the person wants them there.

When a milestone or task is ready for the person to judge, put the review on its review page with
`write_review` rather than in a long reply: what it did, the pictures, short clips and before-and-after
compares that show it, each with the render settings it was taken at, the decisions you made, and what
you didn't check. The person looks at it all in one place and accepts it or asks for changes there;
`read_review` gives their answer.

---

Adapted for Agent Orange from GameFactory-3A's game-generation guide (`agent_skills/setting_overview.md`,
github.com/OpenDCAI/GameFactory-3A), Apache License 2.0; rewritten for Agent Orange's tools and pages.
The licence is in `LICENSE-GameFactory-3A.txt` beside Agent Orange's built-in skills.
