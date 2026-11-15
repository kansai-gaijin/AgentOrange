# Blender skills from arjun988/blender-skills

Agent Orange brings these for Blender, beside EL4CTEO's (`../blender-agent-skills`). Every agent
session gets them while Blender is set up (`client/ToolSkills.cs`).

They're from [arjun988/blender-skills](https://github.com/arjun988/blender-skills) at commit
`904f095`, under the MIT licence (`LICENSE-blender-skills.txt`). Agent Orange changed them:

- **Left out:** the 30 skills that EL4CTEO's cover, as theirs are tested on Blender 5.3 and come with
  scripts. These include the director, modelling, sculpting, retopology, UVs, materials, baking,
  rigging, animation, simulation, lighting, rendering, optimisation, export, QA and environments. Links
  to them now point to EL4CTEO's (`tools/import-blender-skills.py` lists which covers which).
- **Kept:** 17 skills EL4CTEO's don't cover:
  - styles: low-poly, stylised, realistic, horror, PSX horror, anime, voxel and isometric;
  - asset roles: props, vehicles, creatures, vegetation, character archetypes and set dressing;
  - collision proxies, VFX and cinematography.

  Their front matter is cut to name, description and licence.
- **Gathered into one skill:** the 47 skills that were a line or two of style, mood, world or genre
  direction became `blender-art-direction`, one reference each.
- **Shared references made a skill:** they became `blender-references`, including matching a
  reference picture, and the links to them were changed to match.
- **Written for the MCP Agent Orange uses:** `mcp-tools.md` and `mcp-integration.md` now cover Blender
  Lab's official Blender MCP reached through Agent Orange, in place of ahujasid's. Its tool names were
  changed throughout.
- **Corrected:**
  - Godot's collision naming (import-hint suffixes, not `UCX_`).
  - Blender 4's Principled BSDF having no Subsurface Color.
  - The forward-axis check now points to the `model-facing` skill.

To update them, run
`python -I tools/import-blender-skills.py <checkout> client/Skills/tools/blender/blender-skills-arjun988`
against a new checkout, then review what changed.
