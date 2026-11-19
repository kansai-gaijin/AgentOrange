# Shot grammar: words to moves

From AnyShot's `agent/SHOTS.md` (MIT), "Shot language" and "How". Read a direction the way a crew would, then write
it as a camrig move.

| Words | camrig | Numbers |
|---|---|---|
| push in, dolly in, pull out, track along | `push_in` or `dolly`, 2+ keys | "slow" is about 0.2-0.4 m/s |
| truck, crab, slide | `truck` (sideways) | |
| crane, jib, pedestal, rise | `crane`, `pedestal` | |
| pan, tilt, whip | `pan`, `tilt`: the same `pos`, the aim moves | |
| orbit, arc, circle around X | `orbit` with `center_obj` | |
| reveal | start on a foreground element or behind an occluder (a wall edge, a shelf, a doorway) and move so the subject comes into view; end on it, framed | |
| rack focus, pull focus from A to B | `focus.mode: "rack"`: a hold, then the pull | `fstop` 1.4-2.8 so it reads |
| walkthrough, follow the path, Steadicam | `path`, 4-6 keys, `handheld: {"profile": "walk"}` when it should feel carried | walking pace 0.4-0.6 m/s, eye height 1.5-1.7 m |
| vertigo, dolly zoom | `dolly_zoom` | |
| locked off, tripod | `static` | |
| wide | | 18-24 mm |
| normal | | 35-50 mm |
| tight, telephoto | | 85-135 mm |
| low angle, eye level, top down, over the shoulder of X | the camera's height and the aim | eye level 1.5-1.7 m; low angle well under 1 m |

A length in the words ("a 6 second push") sets `frames` (seconds × fps).

## Blocking order
1. Block every shot first: `--framing` (start, middle, end), then `--preview` for the moves. Look at them.
2. Check framing and focus, and that nothing reads wrong from the new angle: floating props, missing backs, holes,
   untextured faces. Fix the camera, or fix the set, and look again.
3. Check every shot's clearance in one Blender session (`path_clearance.py` after `--rig-only --save`): more than
   0.45 m from walls, furniture and props on every frame. Dust, particles, haze, light shafts, decals, glass panes
   the move is meant to pass and anything under about 5 cm don't count: a real camera moves through dust.
4. Only then render finals, in chunks, and grade.

## A typical set of shots
- An establishing wide (24 mm, static or a slow `crane`).
- A walk into the space at eye height (`path`, walk handheld).
- Two or three close dolly reveals with a rack focus onto what matters (35-50 mm, f/2).
- A hero still (85 mm and longer, `static`) for the store page or key art.
