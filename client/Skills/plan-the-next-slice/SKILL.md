---
name: plan-the-next-slice
description: Turn the game's design in Agent Orange into the next small playable slice - its beats in order, what each needs (characters, scenes, props, motion, sound, effects, screens), where each asset comes from, a whitebox to play first, and tasks in the order they depend on each other - checked item by item. Use when starting work on a game or a new part of one, when asked what to build next, or before generating or making assets in bulk.
---

# Plan the next playable slice

Plan from what the player does, in the order they do it, not from a list of asset types. Map the whole
game coarsely, detail only the next slice, build and play that, and let what you learn from playing it
change the plan.

## 1. Read the design

Start with `get_project`, then `read_card` for the cards that matter: the Overview, the story beats,
characters, levels, mechanics and assets, and `list_tasks`. The person's design is canon. When you
propose something new, label it as a proposal; never present it as theirs. Ask only about choices that
block the slice.

## 2. Put the slice's beats in order

For each beat of the slice, settle:

- **Where and when:** the place, and how the player gets there.
- **What the player does:** their goal and the actions they take, not only what they watch.
- **What changes:** knowledge, an item, a relationship or the world, and what makes the next beat open.
- **Failure:** what happens when they fail, where they restart, and what they keep.

Keep estimated play time separate from time in the story. Give branches their own ends or say where they
rejoin. A game with no story uses rounds or stages instead; don't force a plot on it.

## 3. Walk each beat for what it needs

Ask what the player sees, handles and hears in each beat: characters, places, props, motion, sound,
effects, screens and any cutscene. For each asset settle:

- its card on the Assets page, its type and what it belongs to (the beat, character, level or mechanic);
- where it comes from: **made** by an agent in the tools, **generated** by a service, **reused** from
  what the game has, or **licensed** from a library, with its source and licence noted in its card;
- its variants, such as an open and a closed door, or day and night, and whether a material, an
  animation or the game's state makes each one rather than a new mesh;
- what makes it acceptable, in words someone can check by looking.

Define shared assets once and point to them from every beat that uses them. Behaviour belongs to a
mechanic, not an asset. Paid generation always needs the person's yes first: say which service, why,
and what it will roughly cost, retries included, and wait.

Agents can't add cards in Agent Orange. When the slice needs a card that isn't there, list the missing
cards and ask the person to add them, then write their documents.

## 4. Screens

List the screens the slice really needs: entry, HUD, prompts, dialogue, pause, failure and exit. For
each: when it appears and goes, what it shows, and what game state it reads or commands it sends. A
button must do something; a display must show a real state.

## 5. Whitebox first

Build the slice with simple shapes and placeholder feedback, play it, and fix what blocks the loop
before any final art. Give things that differ in what they do a flat colour of their own (a resource
against a building, a scout against a heavy unit), with a label, symbol or shape as well, and keep the
same legend everywhere. Keep scenery neutral. Never let a colour give away what the player shouldn't
know. Write the legend in the design. Placeholders are tasks still open, not finished assets.

## 6. Tasks in the order they depend on each other

Add tasks (`add_task`) under a milestone for the slice, each about its card: design, then assets and
the mechanic's rules, then the UI on top of those rules, then putting it together, then playing it.
Things that don't depend on each other can go in parallel. Generating, assembling and checking are
separate tasks. Each task says what done looks like.

A task is done only when what it says is met, with evidence: tick it (`update_task`) and say in its
notes what shows it, such as the screenshots or the play-through. A slice is playable only once it's
been played (the `play-and-review` skill), never because its code or assets exist. Never mark an
asset delivered; only the person fills a deliverable.

## 7. Check item by item

Before calling the slice's plan ready, go through each beat, asset, screen and task and give it one of
confirmed, missing, or not needed (with why). Don't sum up with "everything is covered". Check that:

- every beat has a goal, an order, a way in and a way out, and a failure path;
- every asset has a source, variants, what it depends on, and an acceptance;
- every screen has its states and the game state it shows;
- every task has what it needs, what it makes, what it waits on, and what done looks like;
- the player can't need something they could never have got, and retrying can't give a reward twice.

Write what's settled into the cards (`write_card`), say what's still missing, and start on the first task.

---

Adapted for Agent Orange from GameFactory-3A's game-design skills (`agent_skills/game_design/`,
github.com/OpenDCAI/GameFactory-3A), Apache License 2.0; rewritten for Agent Orange's pages, tasks and
tools. The licence is in `LICENSE-GameFactory-3A.txt` beside Agent Orange's built-in skills.
