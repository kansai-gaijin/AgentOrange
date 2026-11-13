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
    public static string Write(EditorMonitor editors, string agentFolder)
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

            ## What's in a project

            - Stages, such as Alpha, Beta, Demo and Production. Each keeps its own boards and cards. The person works in one stage at a time.
            - Overview: a node board for the game as a whole.
            - Story (story beats), Characters, Levels, Mechanics and Assets: pages of cards. Each card has a picture and a node board of its own.
            - An asset also has a type (such as 3D model, sprite sheet or sound effect), the story beat, character, level or mechanic it belongs to, tags, a date to deliver it by, and its deliverable: one file in the project's folder.
            - Tasks: every asset with a date to deliver it by is a task, done once the person has delivered it. Nothing else is a task.
            - Boards hold nodes: Title, Rich Text and Image nodes, and reference nodes that stand for a story beat, character, level, mechanic or asset card.

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

        var skills = AgentSkills.List(agentFolder);
        if (skills.Count > 0)
        {
            said.AppendLine();
            said.AppendLine("## Agent Orange's skills");
            said.AppendLine();
            foreach (var skill in skills) said.AppendLine($"- agent-orange:{skill.Name}{(skill.Description.Length > 0 ? $": {skill.Description}" : "")}");
        }

        var path = Path.Combine(agentFolder, "briefing.md");
        File.WriteAllText(path, said.ToString());
        return path;
    }
}
