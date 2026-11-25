---
name: dream-loop
description: Build a scene, level, character or screen until a live screenshot of the game matches a target picture - the card's concept art or an approved generated image - with a separate judge scoring each round on composition, lighting, materials and detail, and clear rules for when to stop. Use when the person says "dream loop", asks for something to look like its concept art, or wants high visual fidelity from a picture.
license: MIT
---

# Dream loop

Close the gap between what the game looks like and a target picture, round by round, with a judge that
doesn't share your context.

## 1. The target picture

The target is the person's to choose. In order:

1. **A picture they give you, or the concept art on the card** you're building (`read_card`, then
   `read_picture` for its pictures). Prefer this.
2. **A generated one,** only if the person asks for it or agrees. If the game already exists, start the
   generation from a screenshot of it and ask for an improved version, so it doesn't drift from what's
   there. Ask for an in-game screenshot, not "concept art", a painting or a cinematic shot: the aim is to
   match it pixel for pixel. Generation that costs money needs the person's yes first. Show them the
   picture before building to it.

If there's no target and no way to make one, stop and ask for one.

Keep working files (the target, screenshots, verdicts) in a `.dream-loop` folder outside the game's
content, ignored by Git, and note on the card which picture is the target.

## 2. The loop

1. Build or change the thing toward the target, in the engine or the tools, through Agent Orange.
2. Check that it works and looks as you meant (the `play-and-review` skill): fix bugs, broken loading
   and models facing the wrong way (`model-facing`) before judging looks.
3. Take a screenshot from the same camera, framing and size as the target, at the same quality settings
   every round. Never turn quality down between rounds to save time: the score would move for a reason
   that has nothing to do with the work.
   Then measure it with agent-orange's `compare_pictures` (picture, and the target as reference): which
   parts are darker, greyer, warmer, softer or emptier than the target, and by how much. `region` looks
   at one part. Keep the numbers with the round: they show what moved when a judge's score wobbles. For
   a scene that should be alive, two frames some seconds apart with `moved` show what's frozen.
4. Have a judge with a fresh context compare them: a subagent if you can start one, given only the
   rubric below, the target, the latest screenshot, and the previous screenshot and verdict. Not the
   numbers, so it judges what it sees, and not the code, what you changed, or how you think it went.
   You never score your own work: if no fresh judge can be had, say the round wasn't judged.
5. Check the judge's list before acting on it: look at each place it names. Drop what isn't there, and
   what's a matter of taste rather than a difference from the target, saying so in the verdict's file.
6. Fix the rest, the person's own comments first and then by how much each shows at normal size, and go
   back to 2. Leave alone what the person has praised, whatever a judge says about it.

A difference that's still there after one real fix needs a different method, not the same one harder:
find out how that look is usually made before trying again.

If the person gave a time limit, note the time once the target is fixed and check it between rounds.
Don't trade looks for time: beautiful partial progress beats something complete and ugly. With no
limit, say that a long loop uses a lot of tokens.

## 3. The judge's brief

> Judge how close the current game is to the target picture. Score:
>
> - **Composition (0–3):** camera, framing and layout; the position and size of everything that matters.
> - **Lighting (0–3):** palette, exposure, shadows, contrast, atmosphere, reflections and glows. Not darker or brighter than the target.
> - **Materials (0–3):** every surface's texture, roughness, translucency and wetness. Nothing blocky, plasticky or fake unless the target is.
> - **Details (0–1):** every small detail matches.
>
> Fractional scores are fine. Be precise and complete: list every gap, where in the picture it is, and
> what would fix it, concretely enough that someone else could fix it all and score better. Not "this tree looks fake", but what makes
> it look fake and how to fix it. If something needs redoing from scratch, say so. Give a total out of 10.
> If you're given a previous verdict, stay consistent with it, and score lower if it got worse.

## 4. When to stop

- **8 or more, and it runs fast enough:** have a second fresh judge score it without seeing the first's
  verdict. One judge's score moves by itself from round to round, so go by the lower of the two. If it
  holds, it's done: show the person the screenshot beside the target. They decide whether it's right,
  and whether to go on.
- **The person thinks less of it than the judge did:** the judge is too kind. Give every later judge the
  person's words and what they scored it, and judge by those.
- **8 or more, but too slow:** optimise, lossless changes first, then ones with the least visual cost.
  Judge again afterwards to make sure it didn't get worse.
- **Stalling:** the best score hasn't risen by a whole point in two rounds, or the judge named the same
  gap twice running. Stop tweaking. Step back and rethink the big things: the assets themselves, where
  they are, the whole lighting, the camera. Make one big change.
- **Stalled:** the big change didn't help either. Stop, show the person where it is, and ask what's off.
- **Otherwise:** keep going.

## Assets the target needs

In order:

1. **What the game already has,** or a free licensed asset if the person allows downloads. Record its
   source and licence in its card.
2. **Make it in Blender** through Agent Orange (`blender-core`), textured to match the target.
3. **Generate it** with a service, such as image-to-3D from the target cropped to that one object on a
   plain background. This costs money each time, so it needs the person's yes, with what it will roughly
   cost.
4. **Procedural, in code:** only when it really gives the best match, not to save time.

Don't leave flat colours or noise where the target has real textures: make proper textures and normal
maps.

---

Adapted for Agent Orange from dream-loop (github.com/achimala/dream-loop), MIT licence: its Pro
workflow, judge and exit rules kept; the target taken from the game's own cards first; paid generation
and the final say left to the person; its subscription tiers and fal.ai scripts left out. Checking the judge's
list, the second judge, steady quality settings and going by the person's own score were added later.
