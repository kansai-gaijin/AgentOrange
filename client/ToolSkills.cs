using System.IO;

namespace AgentOrange;

/// <summary>
/// The agent skills a creative tool ships beside its program, in a skill or skills folder, as
/// Material Maker's release does (skill/material-maker/SKILL.md), and those Agent Orange brings for a
/// tool (client/Skills/tools/&lt;tool id&gt;), such as Blender's. Every session gets those of the tools set
/// up on this PC, as every project can use them.
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

            // And those Agent Orange brings for it, such as Blender's (client/Skills/tools/blender), each
            // pack in a folder of its own, as its skills link to each other and to their scripts there.
            var brought = Path.Combine(AgentSkills.BuiltInFolder, "tools", editor.Id);
            if (!Directory.Exists(brought)) continue;
            foreach (var pack in Directory.EnumerateDirectories(brought).Order(StringComparer.OrdinalIgnoreCase))
            {
                var packNote = $"This skill comes with Agent Orange for {editor.Name}, and its own files are in `{pack}`, which is what `<skills_dir>` means in it. "
                    + $"{editor.Name}'s program here is `{editors.SettingsOf(editor).ProgramPath}`{(editor.Id == "blender" ? " (BLENDER_EXE for a script that starts it)" : "")}. "
                    + $"In Agent Orange, if {editor.Name}'s MCP tools aren't among yours, reach it through agent-orange's open_tool, tool_tools and use_tool; don't register it yourself.";
                foreach (var skill in Directory.EnumerateDirectories(pack).Order(StringComparer.OrdinalIgnoreCase).Select(each => AgentSkills.Read(each, editor.Name)).OfType<AgentSkill>())
                {
                    if (!found.Any(had => had.Name == skill.Name)) found.Add(skill with { Note = packNote, BuiltIn = true });
                }
            }
        }
        return found;
    }

    /// <summary>
    /// The scripting API reference of each tool a project's agents can use: the creative tools set up
    /// here and the game's own engine. It's the one the person chose in Settings › Editors, or where the
    /// tool usually keeps one. One that isn't there is left out, unless the tool says how to make it.
    /// </summary>
    public static List<ToolReference> References(EditorMonitor editors, string? projectFolder, string? engine)
    {
        var found = new List<ToolReference>();
        foreach (var editor in EditorMonitor.Definitions.Where(editor => (!editor.IsEngine || editor.Name == engine) && editors.IsSetUp(editor)))
        {
            var settings = editors.SettingsOf(editor);
            if (settings.ApiReference.Trim() is { Length: > 0 } chosen)
            {
                if (File.Exists(chosen) || Directory.Exists(chosen)) found.Add(new ToolReference(editor, Path.GetFullPath(chosen), null));
                else AppLog.Write($"{editor.Name}'s API reference isn't at {chosen}.");
                continue;
            }

            if (editor.ApiReference is not { } usual) continue;
            var program = Path.GetDirectoryName(settings.ProgramPath);
            if (usual.Contains("{project}") && string.IsNullOrEmpty(projectFolder) || usual.Contains("{program}") && string.IsNullOrEmpty(program)) continue;
            var path = Path.GetFullPath(usual.Replace("{project}", projectFolder).Replace("{program}", program));
            if (File.Exists(path) || Directory.Exists(path) || editor.ApiReferenceHow is not null) found.Add(new ToolReference(editor, path, editor.ApiReferenceHow));
        }
        return found;
    }

    /// <summary>
    /// A skill for each reference, written fresh for the project's sessions under the agent files
    /// folder, so agents look a name up there before they use it rather than guessing.
    /// </summary>
    public static List<AgentSkill> ReferenceSkills(IReadOnlyList<ToolReference> references, string agentFolder, string projectId)
    {
        var root = Path.Combine(agentFolder, "tool-references", projectId);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var skills = new List<AgentSkill>();
        foreach (var reference in references)
        {
            var folder = Path.Combine(root, ReferenceSkillName(reference.Tool));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), ReferenceSkillText(reference));
            if (AgentSkills.Read(folder, reference.Tool.Name) is { } skill) skills.Add(skill);
        }
        return skills;
    }

    public static string ReferenceSkillName(EditorDefinition tool) => $"{tool.Id}-api-reference";

    private static string ReferenceSkillText(ToolReference reference)
    {
        var name = reference.Tool.Name;
        var kind = Directory.Exists(reference.Path) ? "folder" : "file";
        return $"""
            ---
            name: {ReferenceSkillName(reference.Tool)}
            description: Look a name up in {name}'s scripting API reference before using it. Use before calling one of {name}'s functions, classes, properties or constants you haven't seen work, and after an error saying one doesn't exist.
            ---

            # {name}'s API reference

            {name}'s scripting API reference on this PC is the {kind} `{reference.Path}`.{(reference.How is { } how ? $" {how}" : "")}

            - Search it for a name before you use it, with a text search (rg, grep or Select-String), and read the lines around each match for its arguments and what it returns. It's large, so search it rather than reading it whole.
            - Don't guess a name. If it isn't in the reference, it doesn't exist under that name: search for part of it, or for what it does, and use what's there.
            - After an error saying a name doesn't exist, such as an AttributeError, search the reference before trying another name.

            """;
    }
}

/// <summary>A tool's scripting API reference on this PC, a file or a folder, and how to make it when it isn't there yet.</summary>
public sealed record ToolReference(EditorDefinition Tool, string Path, string? How)
{
    /// <summary>The folder agents need to read it: its own, or the file's.</summary>
    public string Folder => Directory.Exists(Path) ? Path : System.IO.Path.GetDirectoryName(Path)!;
}
