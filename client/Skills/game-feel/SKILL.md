---
name: game-feel
description: Make a game's actions feel good and its playable camera work - the feedback each action gives (animation, sound, effects, HUD, camera motion, vibration), a single camera owner with smooth follow and clean cuts, reduced-motion settings, and how to test and tune them. Use when adding or tuning hits, jumps, shots, pickups or any player action's feedback, when building or fixing a player camera, or when the game "feels flat", "floaty", "jittery" or makes people queasy.
---

# Game feel and playable cameras

## Keep the rules and the show apart

The mechanic owns what happened; the presentation reacts to it. A swing or a trigger pull is not a
hit: feedback for a hit fires on the mechanic's confirmed hit, not on the button. Never put camera
shake, HUD, vibration or a global time scale into the game's rules, and never let a camera's shake
feed back into aiming, picking or physics.

## Decide each action's feedback

For each player action, write down in the mechanic's card (`write_card` on the Mechanics page):

| Field | The decision |
|---|---|
| Trigger | The exact confirmed event, who it's for, and its outcome |
| Cues | Animation, sound, effects and UI, by asset; it must still read with motion and vibration off |
| Motion | Camera offset in world units, field-of-view change in degrees, how long in seconds, and a cap |
| Vibration | Opt-in setting, checking the device can, how long, what happens when it can't |
| Repeats | What counts as one event, whether a new one replaces or adds, a rate limit |
| Camera | What it frames, its offset, field of view, how it follows, when it cuts, who owns it |
| Clean-up | Pause, losing focus, losing the device, respawn and leaving the scene |
| Evidence | The engine's own tests, and what was seen in play or a recording |

Shake alone isn't a good hit. Hit feel comes from recoil, animation, contact effects, the damage and
health cues and sound together. Keep motion subtle by default, with separate settings for feedback,
intensity, vibration and reduced motion. Follow the platform's reduced-motion preference until the
player overrides it. With optional motion at zero, shake, bob, roll and field-of-view pulses go, while
health, direction and outcomes stay readable through still cues. Avoid flashes. Never freeze play for
drama.

## The camera

- **One writer.** One piece of code sets the camera each frame: its base pose plus any short offsets.
  Never fold last frame's shake back into the base pose. Turn off any other controller that also moves it.
- **Follow in seconds.** Smooth with `1 - exp(-response * dt)` so it behaves the same at 30, 60 and 120
  frames a second, and cut cleanly on spawn and teleport rather than swooping.
- **Choose the shot from what the player is doing:** chase, fixed arena, over the shoulder, first
  person or authored shots. Decide target weights, look-ahead, distance and field-of-view limits and
  which shot wins before tuning numbers.
- **Don't clip.** Keep the camera out of walls, ceilings and terrain with the engine's own collision
  query for the camera's volume. One ray from the centre doesn't prove the camera fits. Test corners.
- **Fights:** frame both fighters, weapons and jumps included, not just a walk on flat ground. Return to
  a steady playable shot after each beat, and never take movement or aim away from the player.
- Don't show the player what they shouldn't see yet.

## Tune by comparing

Record before and after with the same scene, view, assets and the same inputs. Change one setting at a
time and play the same thing again. Say what reads better and what's more comfortable alongside any
numbers; passing tests don't prove it feels better.

## Check before calling it done

Hit against miss; overlapping and repeated events; limits, and the return to the base pose; the same
feel at 30, 60 and 120 frames a second; spawning and shot changes; pause and resume; losing focus;
vibration missing or refused; turning reduced motion on while playing; feedback off. When pausing or
turning feedback off, look at the camera straight away, mid-shake: stopping updates alone can freeze a
leftover offset. Look at walls, corners, ceilings and slopes in real play. Say what you couldn't test,
such as real vibration hardware, rather than calling it passed. Then review it with the
`play-and-review` skill.

---

Adapted for Agent Orange from GameFactory-3A's game-feel skill (`agent_skills/game-feel/SKILL.md`,
github.com/OpenDCAI/GameFactory-3A), Apache License 2.0; made engine-neutral, with the parts about its
own three.js framework left out. The licence is in `LICENSE-GameFactory-3A.txt` beside Agent Orange's
built-in skills.
