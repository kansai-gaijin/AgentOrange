using System.IO;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// A skill a session loads: its name and description from its SKILL.md's front matter, the folder
/// it's in, who it comes from (Agent Orange, or such as Unreal Engine 5.9), and, for one that isn't
/// Agent Orange's own, what the session's copy of it says about where its files are.
/// </summary>
public sealed record AgentSkill(string Name, string Description, string Folder, string From = AgentSkills.OwnSource, string? Note = null)
{
    public string File => Path.Combine(Folder, "SKILL.md");
}

/// <summary>
/// Agent Orange's own skills, which its agent sessions load whatever the provider: one folder each
/// in the agent files folder, holding a SKILL.md, the layout Claude Code and Codex both read. A
/// session never loads the skills someone has installed for their own use of a provider. A game
/// made in Unreal also gets the skills Unreal ships (<see cref="UnrealSkills"/>).
/// </summary>
public static partial class AgentSkills
{
    public const string OwnSource = "Agent Orange";

    /// <summary>What Claude Code puts before the name of a skill from Agent Orange's plugin.</summary>
    public const string PluginPrefix = "agent-orange:";

    public static string FolderIn(string agentFolder)
    {
        var folder = Path.Combine(agentFolder, "skills");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static List<AgentSkill> List(string agentFolder) =>
        [.. Directory.EnumerateDirectories(FolderIn(agentFolder)).Order(StringComparer.OrdinalIgnoreCase).Select(folder => Read(folder)).OfType<AgentSkill>()];

    /// <summary>The skill in a folder, by the front matter at the top of its SKILL.md, or null when it has none.</summary>
    public static AgentSkill? Read(string folder, string from = OwnSource)
    {
        var file = Path.Combine(folder, "SKILL.md");
        if (!System.IO.File.Exists(file)) return null;
        string text;
        try
        {
            text = System.IO.File.ReadAllText(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var front = FrontMatter().Match(text);
        string Field(string name) => front.Success && Regex.Match(front.Groups[1].Value, $@"^{name}:\s*(.+)$", RegexOptions.Multiline) is { Success: true } found ? found.Groups[1].Value.Trim().Trim('"', '\'') : "";
        var skillName = Field("name");
        return new AgentSkill(skillName.Length > 0 ? skillName : Path.GetFileName(folder), Field("description"), folder, from);
    }

    /// <summary>
    /// The skill a SKILL.md an agent read belongs to, by its own front matter. A skill inside another,
    /// such as one of Unreal's unreal-trim steps, is its own.
    /// </summary>
    public static string? NameOfFile(string path)
    {
        try
        {
            return Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder
                ? Read(folder)?.Name ?? Path.GetFileName(folder)
                : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Claude Code reads skills from a plugin passed for the session, so the skills are copied into
    /// one, fresh each time, for the project's sessions. Their names then start with agent-orange:,
    /// the way every plugin's do. Of a skill from elsewhere only its Markdown is copied, with a note
    /// on where the rest is, as its scripts find the engine by where they are. One of Agent Orange's
    /// own wins over another of the same name.
    /// </summary>
    public static string ClaudePlugin(string agentFolder, string projectId, IReadOnlyList<AgentSkill> others)
    {
        var plugin = Path.Combine(agentFolder, "providers", "claude", projectId, "agent-orange");
        if (Directory.Exists(plugin)) Directory.Delete(plugin, recursive: true);
        Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        System.IO.File.WriteAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json"), """{ "name": "agent-orange", "description": "Agent Orange's own skills, and those of the game's engine." }""");

        var from = FolderIn(agentFolder);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(plugin, "skills", Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            System.IO.File.Copy(file, to);
        }

        foreach (var skill in others)
        {
            var into = Path.Combine(plugin, "skills", skill.Name);
            if (Directory.Exists(into)) continue;
            foreach (var file in Directory.EnumerateFiles(skill.Folder, "*.md", SearchOption.AllDirectories))
            {
                var to = Path.Combine(into, Path.GetRelativePath(skill.Folder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                var text = System.IO.File.ReadAllText(file);
                if (skill.Note is { } note && Path.GetFileName(file).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                {
                    var where = Path.GetDirectoryName(file)!;
                    var said = $"\n\n> {note} This file's own folder there is `{where}`.\n";
                    var front = FrontMatter().Match(text);
                    text = front.Success ? text[..(front.Index + front.Length)] + said + text[(front.Index + front.Length)..] : said.TrimStart() + "\n" + text;
                }
                System.IO.File.WriteAllText(to, text);
            }
        }
        return plugin;
    }

    [GeneratedRegex(@"\A---\r?\n(.*?)\r?\n---", RegexOptions.Singleline)]
    private static partial Regex FrontMatter();
}
