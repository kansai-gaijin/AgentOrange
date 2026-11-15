using System.IO;

namespace AgentOrange;

/// <summary>
/// The agent skills a creative tool ships beside its program, in a skill or skills folder, as
/// Material Maker's release does (skill/material-maker/SKILL.md). Every session gets those of the
/// tools set up on this PC, as every project can use them.
/// </summary>
public static class ToolSkills
{
    public static List<AgentSkill> For(EditorMonitor editors)
    {
        var found = new List<AgentSkill>();
        foreach (var editor in EditorMonitor.Definitions.Where(editor => !editor.IsEngine && editors.IsSetUp(editor)))
        {
            if (Path.GetDirectoryName(editors.SettingsOf(editor).ProgramPath) is not { Length: > 0 } folder) continue;
            var note = $"This is {editor.Name}'s own skill, shipped with it in `{folder}`. Its files are there, not beside this copy. "
                + $"In Agent Orange, if its MCP tools aren't among yours, reach {editor.Name} through agent-orange's open_tool, tool_tools and use_tool instead; don't register it yourself.";
            foreach (var root in new[] { "skill", "skills" }.Select(name => Path.Combine(folder, name)).Where(Directory.Exists))
            {
                List<AgentSkill> skills = AgentSkills.Read(root, editor.Name) is { } itself
                    ? [itself]
                    : [.. Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase).Select(each => AgentSkills.Read(each, editor.Name)).OfType<AgentSkill>()];
                foreach (var skill in skills)
                {
                    if (!found.Any(had => had.Name == skill.Name)) found.Add(skill with { Note = note });
                }
            }
        }
        return found;
    }
}
