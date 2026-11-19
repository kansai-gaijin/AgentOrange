# Blender 5 and Cycles notes

What AnyScene's builds learned the hard way (its `SKILL.md`, its notes and the docstrings of `emitters.py` and
`haze.py`, MIT), kept to what `blender-core` and `blender-rendering` don't already say.

## glTF imports rotate by quaternion
glTF objects come in with `rotation_mode = "QUATERNION"`, so setting `rotation_euler` on them does nothing and
raises no error. Set `ob.rotation_mode = "XYZ"` first, or bake the transform into the mesh data
(`blender-kit-cohesion`'s `normalize_import.py` does). Also: glTF images arrive packed, and `pack()` on an image you
didn't change drops its data (the object renders magenta). Repack only what you changed.

## Emissive dense meshes become thousands of lights
With texture-driven emission, Cycles' light tree treats every emitting triangle as a light. On dense generated or
scanned meshes (a vending machine, a lantern, a fridge) one scene reached 545,000 emitters and about 290 s of sync
per frame. Their light usually comes from real lamps anyway, so stop sampling them as lights; they still glow to the
camera:
```python
def emissive(m):
    for n in (m.node_tree.nodes if m and m.node_tree else []):
        if n.type == "EMISSION":
            return True
        if n.type == "BSDF_PRINCIPLED":
            s, c = n.inputs["Emission Strength"], n.inputs["Emission Color"]
            if (s.is_linked or s.default_value > 0) and (c.is_linked or any(v > 0 for v in c.default_value[:3])):
                return True
    return False

for o in bpy.data.objects:
    if o.type == "MESH" and sum(len(p.vertices) - 2 for p in o.data.polygons) >= 2000:
        for m in o.data.materials:
            if emissive(m):
                m.cycles.emission_sampling = "NONE"   # on m.cycles in 5.x
```

## Volumes are where the render time goes
- **Homogeneous volumes are cheap; textured ones are not.** A room-sized haze driven by noise made Cycles ray-march
  the whole box: about 22 s a frame against 4 s without it. Make the room layers homogeneous (constant density, which
  Cycles samples analytically: a thin layer to head height, a denser one under the ceiling) and keep noise for
  small, local wisps such as steam over a cup.
- **Turn object motion blur off on volume objects** (`ob.cycles.use_motion_blur = False`). A moving or simulated
  volume domain makes Cycles rebuild velocity every frame: one Mantaflow domain cost about 290 s of sync a frame.
  Static volumes with animated textures have nothing to blur anyway; camera blur still works.
- **Long takes:** a procedural volume (noise animated by a driver on its 4D W input) needs no bake, where a Mantaflow
  cache for a 400-frame shot may not finish. For Mantaflow itself: `surface_distance` around 1.0 (it's in cells),
  emitters well inside the domain, negative cache start frames so plumes are developed at frame 1, and clear the
  cache folder with a plain delete, as `fluid.free_all()` runs asynchronously in background mode and can delete the
  new bake.

## Smaller ones
- **Fill lights through glass:** hide fills from camera, glossy *and* transmission rays (the light's ray
  visibility), or they show through glass as bright discs.
- **A smooth glass box without a bevel acts as a lens.** Bevel its edges.
- **Area lights shine along their local -Z.** Aim with `(target - loc).to_track_quat("-Z", "Y")`.
- **`read_factory_settings()` resets the preferences too,** GPU choice included. Set the Cycles device again after
  it. In `blender -b`, Cycles renders on the CPU unless the preferences pick a GPU (`blender-rendering`).
- **Compositor in 5.0:** Glare and Lens Distortion work; colour balance, mask and ellipse nodes misbehave (grade in
  ffmpeg), and a Render Layers node nested inside a group renders black. camrig's `lens_fx` splices its nodes into
  the scene's own tree for that reason.
- **Check the frame count before encoding:** a gap in the frame folder gives a short clip without any error.
