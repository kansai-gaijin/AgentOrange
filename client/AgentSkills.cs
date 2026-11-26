using System.IO;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// A skill a session loads: its name and description from its SKILL.md's front matter, the folder
/// it's in, who it comes from (Agent Orange, or such as Unreal Engine 5.9), and, for one that isn't
/// Agent Orange's own, what the session's copy of it says about where its files are.
/// </summary>
public sealed record AgentSkill(string Name, string Description, string Folder, string From = AgentSkills.OwnSource, string? Note = null, bool BuiltIn = false)
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

    /// <summary>The skills that come with Agent Orange, beside its program (client/Skills in its source).</summary>
    public static string BuiltInFolder => Path.Combine(AppContext.BaseDirectory, "Skills");

    /// <summary>The person's own, then those that come with Agent Orange, less any of theirs replaces by name.</summary>
    public static List<AgentSkill> List(string agentFolder)
    {
        List<AgentSkill> skills = [.. Directory.EnumerateDirectories(FolderIn(agentFolder)).Order(StringComparer.OrdinalIgnoreCase).Select(folder => Read(folder)).OfType<AgentSkill>()];
        if (Directory.Exists(BuiltInFolder))
        {
            foreach (var builtIn in Directory.EnumerateDirectories(BuiltInFolder).Order(StringComparer.OrdinalIgnoreCase).Select(folder => Read(folder)).OfType<AgentSkill>())
            {
                if (!skills.Any(own => own.Name == builtIn.Name)) skills.Add(builtIn with { BuiltIn = true });
            }
        }
        return skills;
    }

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
    /// on where the rest is, as its scripts find the engine by where they are; those Agent Orange
    /// brings for a tool are copied whole, with the same note. One of Agent Orange's
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

        // Those that come with Agent Orange, where the person hasn't one of the same name.
        foreach (var builtIn in List(agentFolder).Where(skill => skill.BuiltIn))
        {
            var into = Path.Combine(plugin, "skills", Path.GetFileName(builtIn.Folder));
            if (Directory.Exists(into)) continue;
            foreach (var file in Directory.EnumerateFiles(builtIn.Folder, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(into, Path.GetRelativePath(builtIn.Folder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                System.IO.File.Copy(file, to);
            }
        }

        foreach (var skill in others)
        {
            var into = Path.Combine(plugin, "skills", skill.Name);
            if (Directory.Exists(into)) continue;
            // Those Agent Orange brings for a tool are whole, scripts too, as a guide's scripts/x.py is looked
            // for beside it: an agent once missed one that was only in the pack's folder.
            foreach (var file in Directory.EnumerateFiles(skill.Folder, skill.BuiltIn ? "*" : "*.md", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}__pycache__{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                var to = Path.Combine(into, Path.GetRelativePath(skill.Folder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.Copy(file, to);
                    continue;
                }
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
