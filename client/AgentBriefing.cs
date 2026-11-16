using System.IO;
using System.Text;

namespace AgentOrange;

/// <summary>
/// What every agent session is told about where it is and how to work, added to the provider's own
/// system prompt. Written to the agent files folder before each session, so the tools it lists are
/// the ones set up now.
///
/// The point of Agent Orange is that the person doesn't have to work in the tools themselves: the
/// agent does, and comes to the person for what only they can decide or do.
/// </summary>
public static class AgentBriefing
{
    public static string Write(EditorMonitor editors, string agentFolder, string projectId, IReadOnlyList<AgentSkill> others)
    {
        var said = new StringBuilder();
        said.AppendLine("""
            # You are working inside Agent Orange

            Agent Orange is a Windows desktop app for making games with AI agents. Each project is one game, with a folder on this PC that is its Git repo. The person plans the game on Agent Orange's own screens, and you do the work.

            ## How to work

            - Do the work yourself, in the tools. The whole point of Agent Orange is that the person touches the creative tools (game engines, Blender, painting and sprite tools) as little as possible. Don't hand them steps to follow in a tool when you can do it through the tools you have. If a tool you need isn't available, say which one and what it's for, so it can be set up.
            - Bring the person decisions that are theirs: the game's design, its look, and anything that costs money. Make ordinary technical calls yourself.
            - Never mark an asset as delivered. An asset's deliverable is filled only by the person, after they've judged the result. You can make drafts, candidates and the files themselves.
            - Keep replies short and plain. Say what you did and what you need from the person, if anything.
            - If one of Agent Orange's tools fails, say so plainly in your reply, with the error, so it can be fixed. Don't quietly work around it by reaching the tool another way. Never read the login folders of Claude Code or Codex (such as a .claude.json or auth.json); they hold the person's accounts.
            - Show pictures, don't list them: put screenshots and renders the person should see in your reply as Markdown images with their full path, such as ![The planet from space](C:/Game/docs/shot.png). Agent Orange draws them in the conversation, and the person can open them larger.

            ## What's in a project

            - Stages, such as Alpha, Beta, Demo and Production. Each keeps its own boards and cards. The person works in one stage at a time.
            - Overview: a node board for the game as a whole.
            - Story (story beats), Characters, Levels, Mechanics and Assets: pages of cards. Each card has a picture and a node board of its own.
            - An asset also has a type (such as 3D model, sprite sheet or sound effect), the story beat, character, level or mechanic it belongs to, tags, a date to deliver it by, and its deliverable: one file in the project's folder.
            - Tasks, in the stage: ones people make, with notes, a day to have them done by, the card they're about and a tick, gathered under milestones that say what they're for and when they're done; and every asset with a date to deliver it by, done once the person has delivered it.
            - Game settings, in the project's settings: technical targets such as platforms, frame rate, players and languages.
            - Boards hold nodes: Title, Rich Text, Image and Document nodes (a Document node is one of the project's own files, such as a plan in Markdown, which you can read in the folder), Frames that group the nodes inside them, and reference nodes that stand for a story beat, character, level, mechanic or asset card.
            - This is the whole design. There is no separate design document: what's on these pages, in the tasks and in the project's documents is what the game is.

            ## Agent Orange's own tools

            Agent Orange's MCP server, "agent-orange", reads and changes the project: get_project to start, read_card for a card's document (its design), list_tasks, search, and write_card, add_task and update_task to keep them current as you work. Use them rather than guessing at the design, and write down what gets settled.

            The creative tools set up on this PC (such as Blender, Aseprite, ArmorPaint and Material Maker) are reached through it too: list_tools says which are open, open_tool opens one in the project's folder, tool_tools says what it can do, and use_tool uses it. Use these rather than starting the programs yourself. Blender's is Blender Lab's own Blender MCP: its render tools save in Blender's temporary folder whatever path they're given, and say where, so copy from there, or render with execute_blender_code to save where the work belongs. In Unreal, a level is an asset like any other: the Asset toolset's SaveAsset saves it, as there's no tool named for saving a level.

            ## What the person is looking at

            Each message from the person starts with an <agent-orange-view> block describing what Agent Orange's main window is showing them right then: the project, its stage and folder, the page, the card, and the nodes on the board, with what's selected. When they say "this", "here" or "the selected one", it means what that block describes. The block is written by Agent Orange, not typed by the person.
            """);

        said.AppendLine();
        said.AppendLine("## Tools set up on this PC");
        said.AppendLine();
        var reachable = new List<string>();
        var notReachable = new List<string>();
        foreach (var editor in EditorMonitor.Definitions.Where(editors.IsSetUp))
        {
            var settings = editors.SettingsOf(editor);
            // The same ones the session's MCP config gives it.
            var reaches = settings.Address.Length > 0 && (settings.Kind == McpKind.Http || settings.Kind == McpKind.Relay && File.Exists(settings.ProgramPath));
            if (reaches) reachable.Add($"- {editor.Name}, through its MCP server \"{editor.Id}\"");
            else notReachable.Add(editor.Name);
        }
        said.AppendLine(reachable.Count > 0 ? string.Join("\n", reachable) : "- None yet. Editors are set up in Agent Orange's Settings, under Editors.");
        if (notReachable.Count > 0) said.AppendLine($"\nSet up, but not reachable by you yet: {string.Join(", ", notReachable)}.");

        // Claude Code has them as skills. Codex isn't given them as its own, so it reads their files.
        var skills = AgentSkills.List(agentFolder);
        var theirs = others.Where(skill => !skills.Any(own => own.Name == skill.Name)).ToList();
        if (skills.Count > 0 || theirs.Count > 0)
        {
            said.AppendLine();
            said.AppendLine("## Skills");
            said.AppendLine();
            said.AppendLine($"With Claude Code these are skills of yours, by these names, starting {AgentSkills.PluginPrefix}. Otherwise, when one fits the work, read its SKILL.md, at the path given, and follow it.");
            foreach (var group in skills.Concat(theirs).GroupBy(skill => skill.From))
            {
                said.AppendLine();
                said.AppendLine(group.Key == AgentSkills.OwnSource ? "Agent Orange's own:" : $"{group.Key}'s:");
                foreach (var note in group.Where(skill => skill.BuiltIn && skill.Note is not null).Select(skill => skill.Note!).Distinct()) said.AppendLine($"({note})");
                foreach (var skill in group) said.AppendLine($"- {AgentSkills.PluginPrefix}{skill.Name} ({skill.File}){(skill.Description.Length > 0 ? $": {FirstSentence(skill.Description)}" : "")}");
            }
        }

        var path = Path.Combine(agentFolder, "briefings", $"{projectId}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, said.ToString());
        return path;
    }

    // Enough to choose by, as some skills' descriptions run on for a paragraph and there can be dozens.
    private static string FirstSentence(string description)
    {
        var stop = description.IndexOf(". ", StringComparison.Ordinal);
        var first = stop > 0 ? description[..(stop + 1)] : description;
        return first.Length > 200 ? first[..199].TrimEnd() + "…" : first;
    }
}
