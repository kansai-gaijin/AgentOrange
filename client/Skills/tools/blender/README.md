# Blender skills

Agent Orange brings these for Blender. Every agent session gets them while Blender is set up
(`client/ToolSkills.cs`).

They're from [arjun988/blender-skills](https://github.com/arjun988/blender-skills) at commit `904f095`,
under the MIT licence (`LICENSE-blender-skills.txt`). Agent Orange changed them:

- **Kept as they are:** the 47 skills with real substance. Their front matter is cut to name,
  description and licence, as the director's repeated a key.
- **Gathered into one skill:** the 47 that were a line or two of style, mood, world or genre direction
  became `blender-art-direction`, one reference each.
- **Shared references made a skill:** they became `blender-references`, and the links to them were
  changed to match.
- **Written for the MCP Agent Orange uses:** `mcp-tools.md` and `mcp-integration.md` now cover Blender
  Lab's official Blender MCP reached through Agent Orange, in place of ahujasid's blender-mcp. Its tool
  names were changed throughout.
- **Corrected:**
  - Godot's collision naming (import-hint suffixes, not `UCX_`).
  - Blender 4's Principled BSDF having no Subsurface Color.
  - The forward axis check now points to the `model-facing` skill.

To update them, run `python -I tools/import-blender-skills.py <checkout> client/Skills/tools/blender`
against a new checkout, then review what changed.
