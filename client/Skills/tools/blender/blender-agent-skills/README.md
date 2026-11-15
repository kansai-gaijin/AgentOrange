# Blender skills from EL4CTEO/blender-skills

Agent Orange's main Blender skills. Every agent session gets them while Blender is set up
(`client/ToolSkills.cs`). Start with `blender-core`, which routes to the rest.

They're from [EL4CTEO/blender-skills](https://github.com/EL4CTEO/blender-skills) at commit `b1b21df`,
under the MIT licence (`LICENSE-blender-agent-skills.txt`). They're written for Blender 5.2 LTS and
newer, verified on 5.3, and every skill's scripts are tested in a real Blender. In them, `<skills_dir>`
means this folder.

Agent Orange changed them:

- **Front matter:** cut to name, description and licence.
- **MCP tool names:** the few places that name ahujasid's Blender MCP now name Blender Lab's, which
  Agent Orange uses. Poly Haven assets are downloaded from its site, as Blender Lab's has no asset
  search.
- **`look` tool:** each skill that asks for it now has a note on what to use instead
  (`blender-asset-qa`'s `review_shots.py`, or a viewport screenshot).

The scripts are unchanged. They make no network calls. `blender-core/scripts/run_in_blender.py`
starts Blender in the background, using `BLENDER_EXE`, then PATH, then the usual install folders.

To update them, run
`python -I tools/import-blender-agent-skills.py <checkout> client/Skills/tools/blender/blender-agent-skills`
against a new checkout, then review what changed.
