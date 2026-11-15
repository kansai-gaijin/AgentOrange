# Working in Blender through its MCP

Do the work in Blender through its MCP; don't hand the person steps to click through. The tools and how
to reach them in Agent Orange are in `mcp-tools.md`.

## Order of work

1. See what's there first: `get_objects_summary`, and `get_object_detail_summary` for anything you'll change
2. Read a tool's arguments (from `tool_tools`) before calling it
3. If there's a reference picture, write the analysis in `reference-analysis-template.md` first
4. Plan the steps, non-destructive first
5. Do them one at a time with `execute_blender_code`, checking the result between steps
6. Screenshot the viewport (`get_screenshot_of_area_as_image`) after the camera, materials and lighting phases, and look at it
7. Report object names, counts and measurements after each phase

## Matching a reference picture

| Step | Tool |
|------|------|
| Starting point | `get_screenshot_of_area_as_image` of the 3D viewport |
| What's in the scene | `get_objects_summary`, or `execute_blender_code` for bounds, materials and collections |
| Camera, geometry, blender-pbr-materials, lights | `execute_blender_code` |
| Compare | another viewport screenshot, or a still rendered with `execute_blender_code` |
| Save | `execute_blender_code` with `bpy.ops.wm.save_mainfile()` |

See `reference-image-match.md` for the whole method and what to avoid.

## Organising the scene

When making assets, set up straight away:

```
COL_Reference    reference pictures and blockout
COL_Geometry     the asset's meshes
COL_Collision    collision proxies
COL_Lights       lights
COL_Cameras      cameras
```

Name objects by `naming-conventions.md`.

## In Agent Orange

- Save files in the game's folder only where they belong; keep tests, scratch renders and screenshots
  out of it.
- Record each asset's facing, scale and source in its card on the Assets page.
- Never fill an asset's deliverable: only the person does that.
