using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>
/// The agent skills Unreal ships, from 5.9 on, found where its editor's own skill loader looks:
/// the engine's (Engine/Extras/AgentTools/Skills), each platform's (Engine/Platforms/*/Skills),
/// each plugin's that the game has on (&lt;Plugin&gt;/Skills), and the game's own (&lt;Project&gt;/Skills).
/// A Skills folder holds a skill in each folder, or is one itself, as PCG's is.
/// </summary>
public static class UnrealSkills
{
    // Which of an engine's plugins have skills, and whether each is on unless a game says not, by engine folder.
    private static readonly Dictionary<string, List<(string Name, string Skills, bool ByDefault)>> PluginSkills = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The skills for a game in this folder: its engine's, and its own.</summary>
    public static List<AgentSkill> ForFolder(string? folder)
    {
        var uproject = folder is null ? null : UnrealProject.Find(folder);
        var engine = uproject is null ? UnrealProject.Installed().FirstOrDefault() : UnrealProject.EngineFor(uproject);
        return engine is null ? [] : For(engine, uproject);
    }

    /// <summary>An engine's skills, with those of the plugins a game has on, or those on by default without one.</summary>
    public static List<AgentSkill> For(UnrealInstall engine, string? uproject = null)
    {
        var from = $"Unreal Engine {engine.Version}";
        var note = $"This is {from}'s own skill, shipped with the engine, whose root (<E>) is `{engine.Folder}`. Its scripts, and every path it names, are in the engine, not beside this copy: run them from there.";
        var roots = new List<(string Folder, string From, string Note)>();
        var root = Path.Combine(engine.Folder, "Engine");
        roots.Add((Path.Combine(root, "Extras", "AgentTools", "Skills"), from, note));

        var platforms = Path.Combine(root, "Platforms");
        if (Directory.Exists(platforms))
        {
            roots.AddRange(Directory.EnumerateDirectories(platforms).Order(StringComparer.OrdinalIgnoreCase).Select(platform => (Path.Combine(platform, "Skills"), from, note)));
        }

        var chosen = PluginChoices(uproject);
        foreach (var (name, skills, byDefault) in EnginePluginSkills(engine))
        {
            if (chosen.TryGetValue(name, out var on) ? on : byDefault) roots.Add((skills, from, note));
        }

        if (uproject is not null)
        {
            var game = Path.GetDirectoryName(uproject)!;
            roots.Add((Path.Combine(game, "Skills"), "This game", "This is the game's own skill, from its Skills folder. Its scripts and the files it names are there, not beside this copy: run them from there."));
        }

        var found = new List<AgentSkill>();
        foreach (var (folder, source, said) in roots)
        {
            if (!Directory.Exists(folder)) continue;
            List<AgentSkill> skills = AgentSkills.Read(folder, source) is { } itself
                ? [itself]
                : [.. Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase).Select(each => AgentSkills.Read(each, source)).OfType<AgentSkill>()];
            foreach (var skill in skills)
            {
                if (!found.Any(had => had.Name == skill.Name)) found.Add(skill with { Note = said });
            }
        }
        return found;
    }

    // The plugins a game turns on or off in its .uproject.
    private static Dictionary<string, bool> PluginChoices(string? uproject)
    {
        var chosen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (uproject is null) return chosen;
        try
        {
            foreach (var plugin in (JsonNode.Parse(File.ReadAllText(uproject))?["Plugins"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (plugin["Name"]?.GetValue<string>() is { } name) chosen[name] = plugin["Enabled"]?.GetValue<bool>() ?? false;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            AppLog.Write($"Couldn't read the plugins in {uproject}: {error.Message}");
        }
        return chosen;
    }

    // Looked for once per engine: a plugin is a folder with a .uplugin, and nothing inside one is looked at.
    private static List<(string Name, string Skills, bool ByDefault)> EnginePluginSkills(UnrealInstall engine)
    {
        lock (PluginSkills)
        {
            if (PluginSkills.TryGetValue(engine.Folder, out var known)) return known;
        }

        var found = new List<(string, string, bool)>();
        var waiting = new Stack<string>([Path.Combine(engine.Folder, "Engine", "Plugins")]);
        while (waiting.Count > 0)
        {
            var folder = waiting.Pop();
            try
            {
                if (Directory.EnumerateFiles(folder, "*.uplugin").FirstOrDefault() is { } uplugin)
                {
                    var skills = Path.Combine(folder, "Skills");
                    if (Directory.Exists(skills)) found.Add((Path.GetFileNameWithoutExtension(uplugin), skills, OnByDefault(uplugin)));
                    continue;
                }
                foreach (var inside in Directory.EnumerateDirectories(folder)) waiting.Push(inside);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        lock (PluginSkills) PluginSkills[engine.Folder] = found;
        return found;
    }

    private static bool OnByDefault(string uplugin)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(uplugin), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })?["EnabledByDefault"]?.GetValue<bool>() ?? false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
