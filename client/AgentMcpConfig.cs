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

    public static string Write(EditorMonitor editors, string agentFolder)
    {
        var servers = new JsonObject();
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

                // Only a port, which agents can't talk to without something of their own in between.
                case McpKind.Port:
                    break;
            }
        }

        var folder = Path.Combine(agentFolder, "mcp");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "claude.json");
        File.WriteAllText(path, new JsonObject { ["mcpServers"] = servers }.ToJsonString(Indented));
        return path;
    }
}
