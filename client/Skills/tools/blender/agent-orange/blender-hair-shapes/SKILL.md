---
name: blender-hair-shapes
description: Realistic strand hair in Blender 5.3 built from shapes. Measure the references with cameras fitted to them, make a hair cap that all hair grows from, block the hairstyle out as locks (curves with a radius, or creased mesh strips), match the blockout to the references in millimetres, then convert it to strand hair with Geometry Nodes. It stays editable by hand, and bakes to brushable curves and Alembic grooms for game engines. The locks become the clumps; tapered tips, stray hairs and baby hair are added on top. Use for any styled hair from references: bangs, parts, pigtails, braids, buns, ponytails.
license: MIT
---

# Hair from shapes

Combing guide hairs by script makes helmets: one even sheet, no clumps, cut-off tips. Here you
shape the hair instead. Every lock you can see in the reference becomes a tube: a curve from root
to tip, with a radius at each point. Geometry Nodes turns each tube into a bundle of strands that
fills it. So the clumps, the parting and the silhouette are exactly what you modelled.

`scripts/hair_shapes.py` does the conversion and the detail. Import it into your own Blender script:
```python
import sys; sys.path.insert(0, r"<this skill>/scripts"); import hair_shapes as hs
```
`scripts/hair_match.py` (imported by it) fits cameras to the references and measures against them.
`blender -b -P hair_shapes.py -- --selftest <dir>` builds a test head (bangs, side locks, a pigtail
into a braid, a mesh strip), converts it and renders before and after. Run it once if you change the script.

## 1. Measure the references
Don't eyeball the references; measure them. Eyeballed hair comes out with bangs at the wrong height,
a crown too wide and braids too long, and in the wrong colour.

When the references are drawn or rendered on the head you're given, fit a camera to each view:
```python
cam = hs.ref_camera(r"refs/hair_front.png", head, az=0, out="fit_front.png")   # az: 0 front, 90 her left, 180 back
cam45 = hs.ref_camera(r"refs/hair_left_45.png", head, az=45, search=10)        # "45" views are often 40-50
print(hs.measure(cam))
```
- `ref_camera` finds the framing from the bare head, so the hair is left to compare. Check its
  printed misfit (a few % is good) and its `out` picture: the head's outline in green should sit on the
  reference's face and shoulders.
- `measure` gives the reference's hair in metres: the top of the hair, its outer edges every 2 cm down,
  the bang line and hairline (where hair first ends on skin), and the lowest points (braid ends).
  Write them into your plan. They're the targets for the locks' ends and widths.
- The default finds dark hair, greys and blue-to-purple dye. For other hair, pass `hair_colors=` with a
  few colours picked from the reference (highlight, mid, shadow).
- Without references on this head, set the cameras up by hand and compare side by side, but still
  measure what you can: bang line against the eyes or brows, length against the chin and shoulders.

## 2. Make the hair cap first
Three rules hold for every hairstyle:
1. **There's always a hair cap.** It's the scalp inside the hairline, as its own mesh in the hair's
   root colour.
2. **Hair grows only from the cap.** Every strand's root is on it.
3. **No hair goes through the model.**

```python
hairline = hs.hairline(head)        # found from the head: the ears, then a human hairline round them
cap = hs.hair_cap(head, hairline, color=root_rgb)
```
- **The hairline, found:** `hs.hairline(head)` finds the ears, as thin flaps sticking out of the sides,
  and builds the line the way hair grows:
  - the forehead;
  - temples a little higher;
  - a sideburn down in front of each ear;
  - over each ear just above where it joins the head;
  - down behind it to the lobe;
  - the nape below.
- **Adjusting it:** use `front`, `sideburn` and `nape` against the references, or edit the points.
  Too high around the ears leaves roots with nowhere to grow.
- **Where the cap comes from:** it's the head's surface on the crown side of the hairline, reached
  from the top of the head, so ear tips stay bare. It sits 0.3 mm over the head, keeps the head's UVs,
  and its edge fades out over 6 mm for a soft hairline.
- **Why it matters:** between strands it shows hair-coloured scalp, not skin, as a game groom's cap
  does.
- **Getting the hairline right:** take it from the references: where `measure` puts the hairline,
  and the temples and nape in the side and back views. Render the head with the cap and compare it
  with the reference before any lock.
- **It stays in Blender:** only the curves go to the engine. The cap is where they grow from, what
  the brushes and `follow` attach to, and where the roots' UVs come from: it keeps the head's UVs.

Then the rules carry through every step:
- `hs.braid(..., root=cap)`: each braid strand starts on the cap, under the gathered hair, and the tail
  locks run inside the braid from there.
- `hs.to_cap(cap, "Section")` after the blockout: any lock whose root is off the cap gets a root on it.
  It prints the ones it had to join from far away; route those by hand, under other hair.
- `hs.hair(..., parent=head, cap=cap)` and `hs.strays(..., cap=cap)`: a live "Keep Out Of Head" modifier
  puts every root on the cap. It also pushes back out any point that frizz, noise or clumping took
  into the head. `hs.baby_hair(..., cap=cap)` grows only on the cap.
- `hs.report(hair, head, cap=cap)`: `inside` (strands that go into the head) and `off_cap` (roots off the
  cap) must both be 0.
- **Anything else on the head** (horns, a crown, a helmet) is an obstacle too:
  - Put it in a collection with `avoid = hs.avoid_collection([horns])`.
  - Pass `avoid=avoid` to `hair`, `strays`, `baby_hair` and `report`.
  - The keep-out step then keeps hair out of it, and `inside` counts it.
  - Part the locks round it in the blockout: hair that has to be pushed out of a horn flattens
    against it.

## 3. Plan the style in sections
From the references, list the sections and how many locks each one needs:
- **Bangs:** 10–16 locks side by side, ending on the measured bang line. How thick they look comes
  from how far back they're rooted: heavy, blunt bangs take a triangle of scalp reaching 6–9 cm back
  from the hairline; wispy ones only 2–3 cm. Look at their ends in the reference: a blunt cut, points,
  or gaps between clumps, and how long the corners are next to the face.
- **Side locks.**
- **The top and back:** for a centre part, 10–20 locks per side, fanned from the part line to where they're gathered.
- **Ties, braids and loose tails.**
- **A bun:** locks wound round it.

Write down where each section starts (its root line), where it goes, and where it ends. Fix the
part line, the bang cut line and the gather points first; they read most.

Then put the sections in layers, from the scalp out. Hair lies like shingles: hair from higher on
the head lies over hair from lower down.
- The nape is under the back.
- The hair around the ears is under the hair coming down from the crown.
- The sides are under the bangs' outer corners.

Where two sections meet, they share the border:
- Their edge locks overlap by 30–50% and run the same way there.
- The upper one covers the seam.
- The bangs' outer locks start where the side hair starts and turn the way it goes, so the bangs and
  the sides read as one mass of hair, not a block set in front of it.
- Sections built side by side at the same height, instead of one over the other, cross through each
  other. Their strands then interleave at an angle and read as a scribbled seam.

Give each section its own collection inside `HairShapes`, so it can be layered, checked and
converted on its own.

## 4. Block it out
Build all of this in one collection, `HairShapes`.
- `hs.lock(name, points, radii)`: points run root to tip and are smoothed. radii is one value,
  (root, tip), (root, mid, tip) or one per point. The radius is half the clump's width.
  - Taper to about 1 mm at the tip.
  - The root point sits 1–2 mm inside the scalp.
- `hs.on_surface(points, head, lift)`: for the stretch of a lock that lies on the head.
  - `lift` must be at least the lock's radius there, or the tube, and so its hair, sinks into the head.
- `hs.braid(name, points, width, turns, tail=[...], tail_locks=8, up=face)`: three strands woven
  along the path, for a three-strand braid.
  - `width=(top, bottom)`: real braids narrow towards the end, often to about 60%. The plaits shrink with it.
  - `up`: the direction the braid's flat face looks, roughly towards the camera that sees it.
  - With `tail`, the strands carry on through the bottom elastic into a loose tail that flares,
    then closes to a tapered brush. Each strand's share ends at its own length (`tail_stagger`),
    and `tail_locks` adds thin locks of their own length and lean, so the tip is uneven, not cut.
  - It returns the three strands, then the tail locks. Move the tail locks to their own
    collection when the tail gets its own material or settings.
- `hs.tie(name, center, axis, radius)`: an elastic, a small torus.
- `hs.strip(name, root_points, tip_points, rows)`: a mesh sheet with its root edge creased.
  - It suits flat sections; you can also sculpt it further.
  - Guides follow its columns, so give it as many columns as you want guides.
  - Keep its loops running across the hair, and its root edge creased (`crease_edge` = 1).

Rules that make it look like hair:
- **Overlap neighbouring locks** by about 30% of their width, so no scalp shows between them.
- **Vary them:** widths, lengths and slight angles all differ, as no two clumps are alike. Even
  spacing reads as a helmet.
- **Parting:** the roots on both sides sit 1–3 mm apart along it, each side's locks leaving it in
  opposite directions. Near the part, keep the hair flat on the scalp: lift each lock only its
  radius, and let it rise for volume a few centimetres away. Locks that stand up at the part make
  two ridges with a dark groove between them, and the part reads as a black seam, not a thin line
  of scalp. A creased `strip` per side, rooted along the part line, gives the cleanest line.
- **Gathered locks** (into a tie or a braid) all end at the tie point. Put a tie there.

When the blockout is built, layer it:
```python
hs.stack(["Nape", "Around ears", "Back", "Sides", "Crown", "Bangs"], head)   # nearest the scalp first
```
- Wherever a lock of a later section passes over an earlier one, `stack` lifts it along the scalp's
  normal until it clears the earlier one. Roots stay put, and the lift is smoothed along the lock.
- Leave braids and tails out of the list; they hang free.
- Run it again whenever you change a section.

## 5. Check the blockout before any strands
1. Turn the tubes on with `hs.preview("HairShapes", on=True, material=grey)`.
2. Check the layers: `hs.sections(cam, ["Nape", ..., "Bangs"], "sections_left.png")` draws each
   section in its own colour, beside the reference.
   - It prints each section's share of the visible hair.
   - Where the reference shows the crown's hair sweeping over the ears, the crown's colour should be
     the one there.
   - Where the bangs meet the sides, the bangs' colour should lie over the sides' edge, with no
     skin between them unless the reference shows it.
   Repeat after converting, with the hair objects: `{"Crown": [hair, strays], ...}`.
3. Measure every view: `hs.compare(cam, "HairShapes", "cmp_front.png")`. It prints, in millimetres
   and as positions you can move points to:
   - how much of the reference's hair is missing and how much yours adds, in each part of it;
   - the outer edge every 1 cm of height (+ is yours further out);
   - the bang line and hairline (+ is yours lower);
   - and writes the reference, yours, and a picture of the differences (red missing, blue extra).
4. Fix the blockout until, in every view, the outer edges are within about 5 mm at most heights,
   the bang line within 3 mm at the median, and no part has more than about 10% missing or extra.
   Fix the silhouette, the bang line, the part, the gather points and the braid length here.
   Strands never fix a wrong blockout.
5. Also render every view from its camera, side by side with the reference. Keep these renders. The
   person reviews them, and they are the "before" in your report.

## 6. Convert
```python
hs.to_cap(cap, "HairShapes")                  # every lock rooted on the cap
hs.preview("HairShapes", on=False)            # always: the conversion reads bare curves
bpy.data.collections["HairShapes"].hide_render = True
mat = hs.hair_material("M_Hair", ((0.0, root_rgb), (0.6, mid_rgb), (1.0, tip_rgb)))   # root 0 -> tip 1
main = hs.hair("Hair", material=mat, parent=head, cap=cap)
loose = hs.strays("Strays", material=mat, parent=head, cap=cap)
baby = hs.baby_hair("BabyHair", head, hairline, material=mat, cap=cap)
print(hs.report([main, loose, baby], head, cap=cap))   # inside and off_cap must be 0
```
- **How a lock becomes strands:** each lock gives `guides` strands round its edge, two inner rings
  of guides, and its centre line. Each guide then gets `children` copies around it, so a lock is
  a solid bundle.
- **Hand-made meshes:** any mesh in the collection with a creased root edge converts too.
- **Every detail stays a live modifier** on the hair object, so you can tune it there:
  Duplicate → Clump → Trim → Frizz → Noise → (Curl) → Profile.
- **Colour:** for a colour change down a braid, choose gradient stops by where the change falls
  along that strand. A separate material per section is fine: put each section in its own shapes
  collection and call `hair()` once per collection.
- **Convert the bangs on their own** (their own collection, their own `hair()` call), so they get
  their own clump and trim. Bangs are what people look at first.

| Look | Knob (hair()) | Typical |
|---|---|---|
| Too thin or see-through | `children` ↑, `strand_radius` ↑ | 40–100 per guide; 0.05–0.08 mm |
| Lock too tight or too loose | `spread` (m around each guide), `clump` | spread ≈ lock radius / 2; clump 0.4–0.7 |
| One smooth sheet (low detail in `compare`) | `clump` ↑, fewer and more varied locks | bangs and lengths 0.6–0.8 |
| Blunt, cut-off tips | `trim` (m, random per strand) | 10–20% of the shortest lock |
| Too clean, plastic | `frizz`, `noise` (real offsets, whatever the strand's length) | 0.3–1 mm, 0.5–2 mm |
| Waves or curls | `curl` (m radius) | 2–6 mm |
| Stragglers | `strays(amount, frizz, noise, curl, trim)` | 2 per guide, a few mm of frizz |
| Baby hair | `baby_hair(hairline_points, band, length, density, flow, fan)` | band 8–14 mm, length 8–15 mm, density 250,000–650,000 |

The hairline points follow the edge of the scalp: forehead, temples, round the ears, and the nape.
- **Baby hair lies along the skin:** each hair leaves it at a low angle and lies down in the `flow`
  direction (down by default), neighbours turning together by up to `fan` degrees. Change `flow` where
  the reference's baby hair runs another way, such as forwards at the temples.
- **Keep it smooth:** frizz and curl are capped at 10% of the length. More turns a 1 cm hair into a
  kink, and the hairline reads as stubble.
- **Judge it in a close-up of the hairline.** Baby hair is fine, so it barely shows in a full view at
  low density. Where it must read further away, add short hand-placed locks (a creased `strip` along
  the hairline with one column per hair) on top.

## 7. Check the strands
- **`hs.report(hair, head, cap=cap)`:**
  - `inside` must be 0: no strand goes into the head.
  - `off_cap` must be 0: every root is on the cap.
  - `backwards` should be about 0.
- **Render the same views again** from the reference cameras, at the references' size, with at
  least 64 samples. Show before and after side by side, plus close-ups of the hairline, a clump and
  a braid tip.
- **Measure every view again with the render:** `hs.compare(cam, hair_objects, "cmp.png", render="front.png")`.
  As well as the outline, it now gives:
  - **solid:** how much of the reference's solid hair yours covers;
  - **detail:** the light and dark within a few mm (clumps, the gaps between them, strands), against
    the reference's, for each part and for the bangs' ends. Under about 0.7 reads as one smooth
    sheet; aim for 0.8–1.2;
  - **colour:** lightness top, middle and bottom. Get it within about 0.05 of the reference.
- **Look for:**
  - a scribbled seam where two sections meet: they cross instead of lying one over the other
    (`stack` them, and check with `sections`);
  - scalp showing through (more or wider locks, more overlap);
  - one even sheet (vary the locks, more clump);
  - stiff tips (more trim);
  - fuzz everywhere (less frizz, fewer strays).
- **If it's wrong, fix the shapes and convert again.** The modifiers only refine what the shapes say.

## 8. Hand it over: hand edits, bake, export
The hair is for a game engine, and sometimes for cut-scenes rendered in Blender. A person may finish
it by hand, so leave every stage editable, and bake only at the end.

| Stage | What can be edited | How |
|---|---|---|
| Live (after `hair()`) | the locks | Edit mode on a lock: move points, Alt+S for radius. The hair follows at once |
| Guides | the guides, by brush | `g = hs.bake_guides(hair, cap)`. Comb them in Curves Sculpt mode; the children, clumps and frizz stay live on top |
| Baked | every strand, by brush | `b = hs.bake(g or hair, cap)`. Plain hair curves on the cap, with its surface and UVs set for the sculpt brushes |

Bake onto the cap, not the head: the roots are on it, and the sculpt brushes then add and move hair only
where hair grows. After brushing, run `report` again: a brush can push strands into the head.

- **Nothing is lost:** each bake keeps its source, hidden. `hs.unbake(b)` removes the bake and shows the
  source again, so you can change the locks or the guides and bake again.
- **Cut-scenes in Blender:** `hs.follow(b)` makes baked hair follow the head as it deforms (armature, shape
  keys). Tested: a 1 cm lift of the head moved the roots by exactly 1 cm.
- **Fit the game budget first:** `hs.game_budget(hair_objects, 50000)`.
  - It cuts every object's children together, keeping their proportions, to about 50,000 strands
    rendered: Epic's MetaHuman head hair at LOD0 (25,000 at LOD1).
  - It widens the strands a little so the hair keeps its cover.
  - It's only modifier values, so it stays editable, and it's what you brush and bake.
  - Then measure with `compare` against the full hair. On Hime, 266,000 strands cut to 51,000 lost 1.5%
    of "solid" (93.4% to 91.9%) and kept the bangs' detail. The braids, at a tenth of their strands, read
    slimmer.
- **Game engine:** `hs.export_groom(hair_objects, "hair.abc", surface=cap)` writes an Alembic groom to
  Epic's "Alembic for Grooms" schema. Blender's own Alembic export writes none of the groom attributes,
  so it builds them on the curves first. Blender then writes them as the file's arbitrary geometry
  parameters.
  - **One curves object:** every strand carries `groom_group_id`, one group per material. Unreal gives
    each group its own material slot, takes at most 15, and runs its passes per group.
  - **`groom_root_uv`:** each root's UV on the cap (the head's UVs). Without it, Unreal invents a
    spherical projection, and root-to-tip or scalp-wide material effects come out wrong.
  - **Guides:** the locks' guides go in too, thinned to 2,000 and tagged `groom_guide`, to drive
    Unreal's hair simulation.
  - **Width and version:** `groom_width` per point (the diameter) besides the native widths, and
    `groom_version_major`/`minor` 1/5 on the object.
  - **Points:** each strand keeps only the points it needs to stay within 0.2 mm of its shape, at
    most 64 (Unreal cuts curves past 255). Don't thin harder: Unreal draws straight segments between
    points, and too few read as angular.
  - **Units and placement:** all in centimetres, roots first, transforms baked in, so the character
    needn't stand at the origin.
  - **Nothing unapplied:** only the evaluated hair is written, so no live modifier reaches the file.
    People report unapplied interpolation crashing Unreal.
  - **What it prints:** strands, guides and points per group, with a warning when over the budget.
    About 2 minutes for a 50,000-strand head.
  - **In Unreal:** import the .abc as a Groom with the default conversion settings, then bind it to
    the head's skeletal mesh. Set up LODs and physics there. Not yet tried in Unreal.
  - **Live objects:** it works on them too, so a person's edits don't need baking just to export.
- **For a person, the add-on:** `scripts/` is also a Blender extension, "Hair from Shapes".
  - Build it with `blender --command extension build --source-dir <this skill>/scripts --output-dir <dir>`.
  - Install the .zip from Preferences > Get Extensions > Install from Disk.
  - The sidebar (N) gets a Hair tab with tubes on and off, Stack Sections, Convert Section (with its
    settings in the redo panel), Bake Guides, Bake Strands, Unbake, Follow Head and Export Groom.
  - Sections stack in their collection order, the first nearest the scalp.
- **Heavy hair:** a whole baked head is hundreds of thousands of curves. Bake and brush a section at a
  time, and brush guides rather than strands where you can.

## Gotchas
- **Transforms:** apply the head's and every lock's. Lengths are metres.
- **Preview off before converting.** A bevel on the curves, or their preview material, gets into the hair.
- **Bundled Trim:** it defaults to Replace Length, which makes every strand 1 m. `hair()` turns that
  off. If you add Trim yourself, do the same.
- **Not attached until baked:** live hair is parented to the head, not attached to its surface. For an
  animated head, parent it to the head bone, or bake and `follow` it.
- **Bundled Frizz and Noise:** their Cumulative Offset defaults on, which adds the offsets up along a
  strand, so a long braid drifts centimetres off its lock. `hair()` turns it off. If you add them
  yourself, do the same.
- **Rendering:** look-dev in Cycles. Hair reads by its highlights and clumps, not its colour, so use a
  key light and a rim light.
- **Black hair renders grey:** under a bright world, the highlights of hundreds of thousands of
  strands add up. If `compare` says yours is lighter, darken the colour, raise the roughness, or
  dim the world and fill, then measure again. Low samples make it worse.
- **GPU memory:** call `hs.gpu(scene)` before rendering. It picks one backend (OptiX first). With
  several backends or the CPU ticked in the saved preferences, a full head (250,000+ strands) runs a
  6 GB card out of memory.
- **Viewport:** a full head is around 250,000 strands and 10 million points. Switching the viewport to
  Material Preview with all of them shown crashed Blender 5.3 (in EEVEE) on a 6 GB card. `hair()`
  shows a tenth of the children in the viewport (`viewport=0.1`); renders always get all of them. Keep
  the viewport under about 1–2 million points. The add-on's Viewport slider sets it on every hair object.
  Baked strands are all real, so bake and brush big sections one at a time.
- **Where the method comes from:** the mesh-to-hair technique shared by Xeofrios (80 Level, 2023)
  and others. This script is Agent Orange's own build of it.
