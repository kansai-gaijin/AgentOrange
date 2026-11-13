using System.IO;
using System.Text.RegularExpressions;

namespace AgentOrange;

public sealed record AgentSkill(string Name, string Description, string Folder);

/// <summary>
/// Agent Orange's own skills, which its agent sessions load whatever the provider: one folder each
/// in the agent files folder, holding a SKILL.md, the layout Claude Code and Codex both read. A
/// session never loads the skills someone has installed for their own use of a provider.
/// </summary>
public static partial class AgentSkills
{
    public static string FolderIn(string agentFolder)
    {
        var folder = Path.Combine(agentFolder, "skills");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static List<AgentSkill> List(string agentFolder)
    {
        var skills = new List<AgentSkill>();
        foreach (var folder in Directory.EnumerateDirectories(FolderIn(agentFolder)).Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.Combine(folder, "SKILL.md");
            if (!File.Exists(file)) continue;

            // Its name and description are in the front matter at the top.
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var front = FrontMatter().Match(text);
            string Field(string name) => front.Success && Regex.Match(front.Groups[1].Value, $@"^{name}:\s*(.+)$", RegexOptions.Multiline) is { Success: true } found ? found.Groups[1].Value.Trim().Trim('"', '\'') : "";
            var skillName = Field("name");
            skills.Add(new AgentSkill(skillName.Length > 0 ? skillName : Path.GetFileName(folder), Field("description"), folder));
        }
        return skills;
    }

    /// <summary>
    /// Claude Code reads skills from a plugin passed for the session, so the skills are copied into
    /// one, fresh each time, that only Agent Orange's sessions use. Their names then start with
    /// agent-orange:, the way every plugin's do.
    /// </summary>
    public static string ClaudePlugin(string agentFolder)
    {
        var plugin = Path.Combine(agentFolder, "providers", "claude", "agent-orange");
        if (Directory.Exists(plugin)) Directory.Delete(plugin, recursive: true);
        Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        File.WriteAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json"), """{ "name": "agent-orange", "description": "Agent Orange's own skills." }""");

        var from = FolderIn(agentFolder);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(plugin, "skills", Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        return plugin;
    }

    [GeneratedRegex(@"\A---\r?\n(.*?)\r?\n---", RegexOptions.Singleline)]
    private static partial Regex FrontMatter();
}
