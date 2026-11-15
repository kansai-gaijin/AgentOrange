# Blender's MCP tools in Agent Orange

Agent Orange reaches Blender through Blender Lab's official Blender MCP. If its tools aren't among
yours as `mcp__blender__…`, use Agent Orange's own: `open_tool` with tool Blender opens it in the
game's folder, `tool_tools` lists what it can do, and `use_tool` calls one. Read a tool's arguments
from `tool_tools` before calling it; they're the authority, not this page.

## The tools

| Tool | Use it for |
|------|------------|
| `get_objects_summary` | What's in the scene: objects, types, collections. Run it before any edit |
| `get_object_detail_summary` | One object in depth: mesh stats, modifiers, blender-pbr-materials |
| `get_blendfile_summary_datablocks`, `_path_info`, `_missing_files`, `_of_linked_libraries`, `_usage_guess` | The .blend file itself: what it holds, where it is, missing textures, linked libraries |
| `execute_blender_code` | Any `bpy` Python: modelling, modifiers, UVs, blender-pbr-materials, blender-character-rigging, export, blender-rendering |
| `get_screenshot_of_area_as_image` | A picture of one editor area, such as the 3D viewport (`VIEW_3D`) |
| `get_screenshot_of_window_as_image` | A picture of the whole Blender window |
| `render_viewport_to_path`, `render_thumbnail_to_path` | Render pictures. They save in Blender's temporary folder whatever path they're given, and say where: copy from there, or render with `execute_blender_code` to save where the work belongs |
| `search_api_docs`, `get_python_api_docs` | Blender's own Python API docs, for the version that's running |
| `search_manual_docs` | Blender's own manual |
| `jump_to_tab_by_name`, `jump_to_tab_by_space_type`, `jump_to_view3d_object_by_name`, `jump_to_view3d_object_data_by_name` | Show the person something in Blender's UI |

There's no Poly Haven, Sketchfab or 3D generation in this MCP. Assets from elsewhere need their source
and licence recorded, and anything paid needs the person's yes first.

## How to work

```
1. get_objects_summary before any edit: never assume the scene is empty
2. Make the collection hierarchy (COL_ prefix)
3. Work in small execute_blender_code steps, checking state after each phase
4. Screenshot the viewport after camera, materials and lighting phases, and look at it
5. Run the validation checklist before export
```

Check the API for the running version with `search_api_docs` rather than trusting memory: Blender 4
and 5 renamed and removed many things (the Principled BSDF's inputs in 4.0, auto smooth in 4.1, the
EEVEE engine's id). Save the .blend before large code steps, with `bpy.ops.wm.save_mainfile()`.

## Common bpy patterns

### Make a collection

```python
import bpy
col = bpy.data.collections.new("COL_Props")
bpy.context.scene.collection.children.link(col)
```

### Rename an object and its data

```python
obj.name = "SM_Crate_01"
obj.data.name = "SM_Crate_01"
```

### Apply transforms

```python
bpy.ops.object.select_all(action='DESELECT')
obj.select_set(True)
bpy.context.view_layer.objects.active = obj
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
```

### Merge by distance

```python
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.mesh.remove_doubles(threshold=0.0001)
bpy.ops.object.mode_set(mode='OBJECT')
```

### Export

```python
import bpy
bpy.ops.export_scene.gltf(filepath="C:/path/in/the/game/SM_Asset.glb", export_format='GLB', use_selection=True)
# or FBX:
bpy.ops.export_scene.fbx(filepath="C:/path/in/the/game/SM_Asset.fbx", use_selection=True,
                         apply_scale_options='FBX_SCALE_ALL', object_types={'MESH', 'ARMATURE'})
```

Axis and scale settings depend on the engine: see `blender-export-pipelines`, `blender-export-pipelines` and
`blender-export-pipelines`, then check the result in the engine itself with the `model-facing` skill.

## When something goes wrong

| Problem | What to do |
|---------|------------|
| Its MCP doesn't answer | `list_tools`, then `open_tool` with tool Blender |
| A call times out | Split the work into smaller steps |
| Object not found | Ask for the scene again; check the name |
| Python error | Read the traceback, fix, try again |
