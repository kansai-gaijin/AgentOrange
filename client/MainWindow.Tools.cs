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
    // Each tool's session, or the one being made, shared by every call to it.
    private readonly Dictionary<string, Task<McpClient>> _toolClients = [];

    // The creative tools set up on this PC, and the game's own engine.
    private IEnumerable<EditorDefinition> AgentTools(Project project) =>
        EditorMonitor.Definitions.Where(editor => (!editor.IsEngine || editor.Name == project.Engine) && Settings.Editors.IsSetUp(editor));

    private EditorDefinition ToolNamed(Project project, string asked) =>
        AgentTools(project).FirstOrDefault(editor => editor.Name.Equals(asked, StringComparison.OrdinalIgnoreCase) || editor.Id.Equals(asked, StringComparison.OrdinalIgnoreCase)
            || (editor.Id == "unreal" && asked.Equals("Unreal", StringComparison.OrdinalIgnoreCase)))
        ?? throw new McpException(-32602, AgentTools(project).Any()
            ? $"tool: no tool called {asked} is set up on this PC. Set up: {string.Join(", ", AgentTools(project).Select(editor => editor.Name))}."
            : $"tool: no tool called {asked} is set up on this PC, as none are yet. The person sets them up in Settings › Engines and Settings › Tools.");

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

    // Before passing a call on: an editor that's open but busy, as Unreal is for a while after it
    // opens, can miss one look, so it's given half a minute.
    private async Task<bool> AnswersAsync(EditorDefinition editor)
    {
        for (var waited = 0; ; waited += 2)
        {
            if (await ReachableAsync(editor)) return true;
            var program = Path.GetFileNameWithoutExtension(editor.Id == "unreal" ? "UnrealEditor" : Settings.Editors.SettingsOf(editor).ProgramPath);
            if (waited >= 30 || program.Length == 0 || Process.GetProcessesByName(program).Length == 0) return false;
            await Task.Delay(2000);
        }
    }

    /// <summary>
    /// Opens the tool in the project's folder if it isn't open, and waits until its MCP answers a real
    /// call, not only until its port takes connections, as a tool still starting turns calls away.
    /// </summary>
    internal async Task<string> OpenToolAsync(EditorDefinition editor, string? folder)
    {
        var settings = Settings.Editors.SettingsOf(editor);
        if (await ReadinessAsync(editor, folder) is { NotYet: null } ready) return $"{editor.Name} is open, and its MCP answers calls, with {ready.Tools} tools. tool_tools lists them.";
        if (editor.Id == "unreal") return await OpenUnrealAsync(editor, folder);
        if (!File.Exists(settings.ProgramPath)) return $"{editor.Name}'s program isn't where Agent Orange has it ({settings.ProgramPath}). It's set in Settings › Tools.";

        // Blender Lab's Blender MCP, set up the first time: its extension in Blender and its server for agents.
        if (editor.Id == "blender" && await BlenderMcp.SetUpAsync(Settings.Folders.AgentFolder, settings.ProgramPath, said => AppLog.Write(said)) is { } notSetUp) return notSetUp;

        // Its extension only starts on its own when Blender may go online.
        string[] arguments = editor.Id == "blender" ? ["--online-mode", "--python-expr", BlenderMcp.WithoutSplash] : [];

        // Looked at now, as the monitor's last look may have caught the setup's own runs of the program.
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(settings.ProgramPath)).Length > 0;
        if (running && editor.Id == "blender")
        {
            return "Blender is open, but its MCP doesn't answer. Ask the person to turn it on in Blender (Edit › Preferences › Add-ons › MCP › Start), "
                + "or to close Blender so open_tool can start it with its MCP on.";
        }
        // On its own, so it stays open when Agent Orange restarts, and is still there once it's back (#101).
        var hidden = false;
#if DEBUG
        // A headless run shows nothing on the person's screen.
        hidden = Headless.Active;
#endif
        if (!running && await DetachedProcess.StartAsync(settings.ProgramPath, arguments, folder is not null && Directory.Exists(folder) ? folder : null, hidden: hidden) is (null, var problem))
        {
            return $"{editor.Name} didn't start: {problem}";
        }

        // Open but not answering yet is still starting, so it's waited for too.
        return await UntilReadyAsync(editor, folder, TimeSpan.FromMinutes(2), started: !running);
    }

    // The game's own project, in its engine, which takes a while to load.
    private async Task<string> OpenUnrealAsync(EditorDefinition editor, string? folder)
    {
        if (folder is null || UnrealProject.Find(folder) is not { } uproject)
        {
            return "This project's folder has no Unreal project yet. The person makes it with Create the Unreal project, on the project's page in Agent Orange.";
        }
        if (Process.GetProcessesByName("UnrealEditor").Length > 0)
        {
            // Its port taking connections, or an editor started a little while ago, is this one still loading.
            if (await ReachableAsync(editor) || InStartUp(editor)) return await UntilReadyAsync(editor, folder, TimeSpan.FromMinutes(10), started: false);
            return "Unreal is open, but its MCP doesn't answer on Agent Orange's port. It may be another project. Ask the person to open this one with Open in Unreal Engine, on the project's page.";
        }
        if (await UnrealProject.OpenAsync(uproject) is { } problem) return problem;

        var opened = await UntilReadyAsync(editor, folder, TimeSpan.FromMinutes(10), started: true);
        return opened.Contains("after 10 min", StringComparison.Ordinal) ? $"{opened} The first open of a project compiles shaders, which can take longer." : opened;
    }

    // Its session, made once and shared by the calls to it. One that couldn't be made is made again next time.
    private async Task<McpClient> ToolClientAsync(EditorDefinition editor, string? folder, CancellationToken cancel = default)
    {
        if (!_toolClients.TryGetValue(editor.Id, out var connecting) || connecting.IsFaulted || connecting.IsCanceled)
        {
            var settings = Settings.Editors.SettingsOf(editor);
            var server = editor.Id == "blender" ? BlenderMcp.ServerProgram(Settings.Folders.AgentFolder) : null;
            _toolClients[editor.Id] = connecting = ConnectToolAsync(settings, folder, server);
        }
        try
        {
            return await connecting.WaitAsync(cancel);
        }
        catch when (connecting.IsFaulted || connecting.IsCanceled)
        {
            if (_toolClients.GetValueOrDefault(editor.Id) == connecting) _toolClients.Remove(editor.Id);
            throw;
        }

        // Not tied to any one call, which may be cancelled while others wait on it too.
        static async Task<McpClient> ConnectToolAsync(EditorSettings settings, string? folder, string? server)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await McpClient.ConnectAsync(settings, folder, server, limit.Token);
        }
    }

    private void ForgetToolClient(EditorDefinition editor)
    {
        if (_toolClients.Remove(editor.Id, out var gone) && gone.IsCompletedSuccessfully) gone.Result.Dispose();
    }

    private async Task<object> ListToolsForAgentAsync(Project project)
    {
        var said = new StringBuilder();
        var front = ToolWindows.ForegroundProcess();
        foreach (var editor in AgentTools(project))
        {
            var reachable = await ReachableAsync(editor);
            var processes = ToolProcessIds(editor);
            var open = IsOpen(editor);
            var window = !open ? "" : processes.Contains(front) ? ", its window is in front" : ", its window is behind others";
            said.AppendLine($"- {editor.Name}: {(open ? "open" : "not open")}{window}, {(reachable ? "its MCP answers" : "its MCP doesn't answer yet")}.");
        }
        if (said.Length == 0) return "No creative tools or engine are set up on this PC. The person sets them up in Settings › Engines and Settings › Tools.";
        if (CallsOf(project).ToList() is { Count: > 0 } calls)
        {
            said.AppendLine("\nCalls start_tool_call started:");
            foreach (var call in calls) said.AppendLine($"- {call.Line}");
        }
        return said + "\nopen_tool opens one in the project's folder, and close_tool closes it. tool_tools says what one can do, and use_tool uses it; start_tool_call runs a long call without a time limit. focus_tool brings one's window in front, for timed work, as some tools slow down behind others.";
    }

    private async Task<object> OpenToolForAgentAsync(Project project, JsonObject given)
    {
        var editor = ToolNamed(project, Required(given, "tool"));
        var opened = await OpenToolAsync(editor, ProjectFolder(project.Id));
        return given["in_front"] is JsonValue front && front.TryGetValue<bool>(out var inFront) && inFront ? $"{opened} {await FocusToolAsync(editor)}" : opened;
    }

    // The tool's own running processes: the ones the editor monitor found, and any of its program's
    // name, but not copies running as its MCP relay, which aren't the tool being open.
    private List<int> ToolProcessIds(EditorDefinition editor)
    {
        var ids = Settings.Editors.StatusOf(editor.Id)?.Instances.Select(instance => instance.ProcessId).ToList() ?? [];
        var settings = Settings.Editors.SettingsOf(editor);
        var program = editor.Id == "unreal" ? "UnrealEditor" : Path.GetFileNameWithoutExtension(settings.ProgramPath);
        if (program.Length == 0) return ids;
        foreach (var process in Process.GetProcessesByName(program))
        {
            using (process)
            {
                if (settings.Kind == McpKind.Relay && EditorMonitor.IsRelay(process.Id, settings)) continue;
                ids.Add(process.Id);
            }
        }
        // The monitor's last look can be a few seconds old.
        return [.. ids.Distinct().Where(id => !HasEnded(id))];
    }

    private async Task<string> FocusToolAsync(EditorDefinition editor)
    {
        var processes = ToolProcessIds(editor);
        var window = ToolWindows.WindowOf(processes);
        if (window == 0)
        {
            return IsOpen(editor)
                ? $"{editor.Name} is running, but has no window to show yet. It may still be loading: wait a little with wait_for, then focus_tool again."
                : $"{editor.Name} isn't open, so its window can't be brought in front. Open it with open_tool, which can bring it in front with in_front.";
        }
        if (ToolWindows.ForegroundProcess() is var front && processes.Contains(front)) return $"{editor.Name}'s window is in front.";
#if DEBUG
        // A headless run never changes what's on the person's screen.
        if (Headless.Active) return $"{editor.Name}'s window is behind others. (A headless run doesn't bring windows in front.)";
#endif
        return await ToolWindows.BringToFrontAsync(window)
            ? $"{editor.Name}'s window is in front now."
            : $"{editor.Name}'s window couldn't be brought in front: Windows kept {ToolWindows.ForegroundTitle()} there. Ask the person to click on {editor.Name}'s window, or to leave it in front, before the timed work.";
    }

    private async Task<object> FocusToolForAgentAsync(Project project, JsonObject given) =>
        await FocusToolAsync(ToolNamed(project, Required(given, "tool")));

    private async Task<object> ToolToolsAsync(Project project, JsonObject given)
    {
        var editor = ToolNamed(project, Required(given, "tool"));
        if (!await AnswersAsync(editor)) return $"{editor.Name}'s MCP doesn't answer. Open it with open_tool first.";
        var notes = new List<string>();
        JsonNode? listed;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            listed = await RelayAsync(editor, ProjectFolder(project.Id), "tools/list", [], UseToolLimits, notes, limit.Token);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Net.Http.HttpRequestException or OperationCanceledException)
        {
            return ToolResult($"{editor.Name} didn't list its tools: {Why(error)}.{(notes.Count > 0 ? $" ({string.Join(" ", notes)})" : "")}", error: true);
        }
        var said = new StringBuilder($"{editor.Name}'s tools, for use_tool:\n");
        foreach (var tool in (listed?["tools"] as JsonArray ?? []).OfType<JsonObject>())
        {
            said.AppendLine($"- {tool["name"]}: {tool["description"]?.GetValue<string>()?.Replace("\n", " ")}");
            if (tool["inputSchema"]?["properties"] is JsonObject properties && properties.Count > 0) said.AppendLine($"  Arguments: {properties.ToJsonString()}");
        }
        if (notes.Count > 0) said.AppendLine($"({string.Join(" ", notes)})");
        return said.ToString();
    }

    private async Task<object> UseToolAsync(Project project, JsonObject given)
    {
        var editor = ToolNamed(project, Required(given, "tool"));
        var name = Required(given, "name");
        var arguments = given["arguments"] as JsonObject ?? [];
        if (!await AnswersAsync(editor)) return ToolResult($"{editor.Name}'s MCP doesn't answer. Open it with open_tool first.", error: true);
        return await RelayToolCallAsync(editor, ProjectFolder(project.Id), name, arguments, UseToolLimits, CancellationToken.None);
    }

    // When a relayed call fails: the tool's real name and arguments, from its own list, if what was given doesn't match them.
    private async Task<string?> ArgumentsHintAsync(EditorDefinition editor, string? folder, string name, JsonObject arguments)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var client = await ToolClientAsync(editor, folder, limit.Token);
            var tools = ((await client.RequestAsync("tools/list", [], limit.Token))?["tools"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (tools.FirstOrDefault(tool => tool["name"]?.GetValue<string>() == name) is not { } tool)
            {
                var names = tools.Select(each => each["name"]?.GetValue<string>()).OfType<string>().ToList();
                return $"{editor.Name} has no tool called {name}{(Closest(name, names) is { } close ? $" (did you mean {close}?)" : "")}. tool_tools lists its {names.Count} tools.";
            }
            return ArgumentsMismatch(name, tool["inputSchema"] as JsonObject, arguments);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            return null;
        }
    }
}
