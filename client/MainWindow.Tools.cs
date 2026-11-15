using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>
/// The creative tools set up on this PC, such as Blender, Aseprite, ArmorPaint and Material Maker,
/// opened by Agent Orange in the project's folder and used through it: an agent's session only
/// connects to MCP servers already running when it starts, so Agent Orange passes its calls on to
/// a tool opened partway through. Blender is reached through Blender Lab's official Blender MCP
/// (<see cref="BlenderMcp"/>), which Agent Orange sets up the first time it opens Blender.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, McpClient> _toolClients = [];

    private IEnumerable<EditorDefinition> CreativeTools => EditorMonitor.Definitions.Where(editor => !editor.IsEngine && Settings.Editors.IsSetUp(editor));

    private EditorDefinition ToolNamed(string asked) =>
        CreativeTools.FirstOrDefault(editor => editor.Name.Equals(asked, StringComparison.OrdinalIgnoreCase) || editor.Id.Equals(asked, StringComparison.OrdinalIgnoreCase))
        ?? throw new McpException(-32602, $"No tool called {asked} is set up here. Set up: {string.Join(", ", CreativeTools.Select(editor => editor.Name))}.");

    private bool IsOpen(EditorDefinition editor) => Settings.Editors.StatusOf(editor.Id)?.Instances.Count > 0;

    // Whether its MCP answers right now: an HTTP server or a port taking connections, or a relay's program to start.
    private async Task<bool> ReachableAsync(EditorDefinition editor)
    {
        var settings = Settings.Editors.SettingsOf(editor);
        try
        {
            switch (settings.Kind)
            {
                case McpKind.Relay:
                    return IsOpen(editor) && File.Exists(settings.ProgramPath);
                case McpKind.Port:
                    var (host, port) = settings.Address.Split(':') is [var h, var p] && int.TryParse(p, out var number) ? (h, number) : ("127.0.0.1", 9876);
                    using (var socket = new TcpClient())
                    {
                        var connecting = socket.ConnectAsync(host, port);
                        return await Task.WhenAny(connecting, Task.Delay(1500)) == connecting && socket.Connected;
                    }
                default:
                    using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                    using (var client = new System.Net.Http.HttpClient())
                    {
                        await client.GetAsync(settings.Address, cancel.Token); // Any answer at all means it's there.
                        return true;
                    }
            }
        }
        catch (Exception error) when (error is IOException or SocketException or System.Net.Http.HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Opens the tool in the project's folder if it isn't open, and waits for its MCP to answer.</summary>
    internal async Task<string> OpenToolAsync(EditorDefinition editor, string? folder)
    {
        var settings = Settings.Editors.SettingsOf(editor);
        if (await ReachableAsync(editor)) return $"{editor.Name} is open, and its MCP answers.";
        if (!File.Exists(settings.ProgramPath)) return $"{editor.Name}'s program isn't where Agent Orange has it ({settings.ProgramPath}). It's set in Settings › Tools.";

        // Blender Lab's Blender MCP, set up the first time: its extension in Blender and its server for agents.
        if (editor.Id == "blender" && await BlenderMcp.SetUpAsync(Settings.Folders.AgentFolder, settings.ProgramPath, said => AppLog.Write(said)) is { } notSetUp) return notSetUp;

        var start = new ProcessStartInfo(settings.ProgramPath) { UseShellExecute = false };
        if (folder is not null && Directory.Exists(folder)) start.WorkingDirectory = folder;
        if (editor.Id == "blender")
        {
            start.ArgumentList.Add("--online-mode"); // Its extension only starts on its own when Blender may go online.
            start.ArgumentList.Add("--python-expr");
            start.ArgumentList.Add(BlenderMcp.WithoutSplash);
        }

        // Looked at now, as the monitor's last look may have caught the setup's own runs of the program.
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(settings.ProgramPath)).Length > 0;
        if (running && editor.Id == "blender")
        {
            return "Blender is open, but its MCP doesn't answer. Ask the person to turn it on in Blender (Edit › Preferences › Add-ons › MCP › Start), "
                + "or to close Blender so open_tool can start it with its MCP on.";
        }
        if (!running)
        {
            try
            {
                Process.Start(start);
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return $"{editor.Name} didn't start: {error.Message}";
            }
        }

        for (var waited = 0; waited < 120; waited++)
        {
            await Task.Delay(1000);
            if (await ReachableAsync(editor)) return $"Opened {editor.Name}, and its MCP answers.";
        }
        return $"{editor.Name} was started, but its MCP hasn't answered in two minutes. It may still be loading, or its MCP may be off in it.";
    }

    private async Task<McpClient> ToolClientAsync(EditorDefinition editor, string? folder)
    {
        if (_toolClients.TryGetValue(editor.Id, out var kept)) return kept;
        var client = await McpClient.ConnectAsync(Settings.Editors.SettingsOf(editor), folder, editor.Id == "blender" ? BlenderMcp.ServerProgram(Settings.Folders.AgentFolder) : null);
        _toolClients[editor.Id] = client;
        return client;
    }

    private void ForgetToolClient(EditorDefinition editor)
    {
        if (_toolClients.Remove(editor.Id, out var gone)) gone.Dispose();
    }

    private async Task<object> ListToolsForAgentAsync(Project project)
    {
        var said = new StringBuilder();
        foreach (var editor in CreativeTools)
        {
            var reachable = await ReachableAsync(editor);
            said.AppendLine($"- {editor.Name}: {(IsOpen(editor) ? "open" : "not open")}, {(reachable ? "its MCP answers" : "its MCP doesn't answer yet")}.");
        }
        return said.Length == 0
            ? "No creative tools are set up on this PC. The person sets them up in Settings › Tools."
            : said + "\nopen_tool opens one in the project's folder. tool_tools says what one can do, and use_tool uses it.";
    }

    private async Task<object> OpenToolForAgentAsync(Project project, JsonObject given) =>
        await OpenToolAsync(ToolNamed(Required(given, "tool")), ProjectFolder(project.Id));

    private async Task<object> ToolToolsAsync(Project project, JsonObject given)
    {
        var editor = ToolNamed(Required(given, "tool"));
        if (!await ReachableAsync(editor)) return $"{editor.Name}'s MCP doesn't answer. Open it with open_tool first.";
        var client = await ToolClientAsync(editor, ProjectFolder(project.Id));
        var listed = await client.RequestAsync("tools/list", []);
        var said = new StringBuilder($"{editor.Name}'s tools, for use_tool:\n");
        foreach (var tool in (listed?["tools"] as JsonArray ?? []).OfType<JsonObject>())
        {
            said.AppendLine($"- {tool["name"]}: {tool["description"]?.GetValue<string>()?.Replace("\n", " ")}");
            if (tool["inputSchema"]?["properties"] is JsonObject properties && properties.Count > 0) said.AppendLine($"  Arguments: {properties.ToJsonString()}");
        }
        return said.ToString();
    }

    private async Task<object> UseToolAsync(Project project, JsonObject given)
    {
        var editor = ToolNamed(Required(given, "tool"));
        var name = Required(given, "name");
        var arguments = given["arguments"] as JsonObject ?? [];
        if (!await ReachableAsync(editor)) return ToolResult($"{editor.Name}'s MCP doesn't answer. Open it with open_tool first.", error: true);

        try
        {
            var client = await ToolClientAsync(editor, ProjectFolder(project.Id));
            var result = await client.RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() });
            return result is JsonObject passed ? (JsonObject)passed.DeepClone() : ToolResult(result?.ToJsonString() ?? "");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            ForgetToolClient(editor); // A tool closed and opened again needs a new session.
            return ToolResult($"{editor.Name} couldn't do that: {error.Message}", error: true);
        }
    }
}
