using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>
/// The MCP servers an Agent Orange session gets: the engines and tools set up on this PC, and
/// nothing else. A session never picks up the MCP servers someone has set up for their own use of
/// the provider, as those belong to their normal setup, not to this app. Written to the agent files
/// folder before each session, so it always matches what's set up now.
/// </summary>
public static class AgentMcpConfig
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // The program agents start as Agent Orange's MCP relay. A dev build runs it from a copy of
    // itself, as its own files are rebuilt while agents run, and a relay running from them locks them.
    internal static string RelayProgram(string agentFolder)
    {
#if DEBUG
        var copy = Path.Combine(agentFolder, "mcp-relay");
        var built = Path.Combine(AppContext.BaseDirectory, "AgentOrange.dll");
        var copied = Path.Combine(copy, "AgentOrange.dll");
        if (!File.Exists(copied) || File.GetLastWriteTimeUtc(copied) != File.GetLastWriteTimeUtc(built))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
                {
                    var to = Path.Combine(copy, Path.GetRelativePath(AppContext.BaseDirectory, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(file, to, overwrite: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A relay is running from the copy. The one there does the same job.
            }
        }
        return Path.Combine(copy, "AgentOrange.exe");
#else
        return Path.Combine(AppContext.BaseDirectory, "AgentOrange.exe");
#endif
    }

    public static string Write(EditorMonitor editors, string agentFolder, string? projectId = null)
    {
        var servers = new JsonObject();

        // Agent Orange's own, for the project the session is about: the design, the tasks and the settings.
        // Always loaded in Claude Code, not only once searched for: its few tools are used from the
        // start, and ones loaded on demand are forgotten when a conversation is summed up.
        if (projectId is not null)
        {
            servers["agent-orange"] = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = RelayProgram(agentFolder),
                ["args"] = new JsonArray("--mcp", projectId),
                ["alwaysLoad"] = true,
            };
        }
        foreach (var editor in EditorMonitor.Definitions.Where(editors.IsSetUp))
        {
            var settings = editors.SettingsOf(editor);
            if (settings.Address.Length == 0) continue;
            switch (settings.Kind)
            {
                case McpKind.Http:
                    servers[editor.Id] = new JsonObject { ["type"] = "http", ["url"] = settings.Address };
                    break;
                case McpKind.Relay when File.Exists(settings.ProgramPath):
                    var arguments = new JsonArray(settings.Address.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(argument => (JsonNode)argument).ToArray());
                    servers[editor.Id] = new JsonObject { ["type"] = "stdio", ["command"] = settings.ProgramPath, ["args"] = arguments };
                    break;

                // Only a port, which agents talk to through the server made for it: Blender Lab's, for Blender.
                case McpKind.Port when editor.Id == "blender" && File.Exists(BlenderMcp.ServerProgram(agentFolder)):
                    servers[editor.Id] = new JsonObject { ["type"] = "stdio", ["command"] = BlenderMcp.ServerProgram(agentFolder) };
                    break;
                case McpKind.Port:
                    break;
            }
        }

        var folder = Path.Combine(agentFolder, "mcp");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, projectId is null ? "claude.json" : $"claude-{projectId}.json");
        File.WriteAllText(path, new JsonObject { ["mcpServers"] = servers }.ToJsonString(Indented));
        return path;
    }
}
