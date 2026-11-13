using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrange;

public enum AgentItemKind { User, Assistant, Tool, Approval, Question, Notice, Problem }

public enum AgentToolState { Running, Done, Failed }

public enum AgentRequestState { Waiting, Allowed, Denied, Answered, Gone }

public sealed record AgentOption(string Label, string Description);

public sealed record AgentQuestion(string Header, string Question, List<AgentOption> Options, bool MultiSelect);

/// <summary>One thing in a conversation, in the order it happened. Saved as it is, so a conversation reads the same after a restart.</summary>
public sealed class AgentItem
{
    public required string Id { get; init; }
    public required AgentItemKind Kind { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>A message's words, a tool's one line, or a notice.</summary>
    public string Text { get; set; } = "";

    /// <summary>A message from the person: what they were looking at in Agent Orange, in a few words.</summary>
    public string? View { get; set; }

    public string? Tool { get; set; }

    /// <summary>What a tool gave back, or what one waiting for the person wants to do, cut short.</summary>
    public string? Detail { get; set; }

    public AgentToolState ToolState { get; set; }
    public AgentRequestState RequestState { get; set; }
    public List<AgentQuestion>? Questions { get; set; }
    public Dictionary<string, string>? Answers { get; set; }

    [JsonIgnore] public bool Streaming { get; set; }

    // What a request needs to be answered while the agent is still waiting on it.
    [JsonIgnore] public JsonNode? RequestInput { get; set; }
    [JsonIgnore] public string? ToolUseId { get; set; }
}

/// <summary>
/// One conversation with an agent, about one project, working in its folder. The agent program
/// keeps running for as long as the conversation is in use, taking messages as they're sent, the
/// way the Claude Agent SDK runs it: stream JSON both ways, with its requests for permission and
/// questions for the person coming back to be answered here.
///
/// It gets only the MCP servers and skills Agent Orange gives it, and only project settings, so
/// nothing from how the person set up the provider for their own use comes with it. Edits in the
/// project's folder and the app's editors' tools are allowed. Anything else it wants to do, the
/// person is asked first.
/// </summary>
public sealed class AgentConversation
{
    private const int LongestDetail = 4000;
    private static readonly TimeSpan StopWaitsFor = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public string Title { get; set; } = "";
    public string? ProviderId { get; set; }

    /// <summary>The agent program's own name for the conversation, which picks it back up after a restart.</summary>
    public string? SessionId { get; set; }

    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public List<AgentItem> Items { get; init; } = [];

    /// <summary>It finished, or wants something, while no one was looking.</summary>
    public bool Unseen { get; set; }

    [JsonIgnore] public string? Folder { get; set; }
    [JsonIgnore] public bool IsWorking { get; private set; }
    [JsonIgnore] public bool IsWaiting => Items.Any(item => item.RequestState == AgentRequestState.Waiting && item.Kind is AgentItemKind.Approval or AgentItemKind.Question);

    /// <summary>On the window's thread: an item was added or changed.</summary>
    public event Action<AgentItem>? ItemChanged;

    /// <summary>On the window's thread: it started or stopped working, or wants the person.</summary>
    public event Action? StateChanged;

    private Process? _process;
    private bool _stopping;
    private readonly Dictionary<string, AgentItem> _byId = [];
    private string? _messageId; // The assistant message being streamed.

    public static AgentConversation? Load(string file)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<AgentConversation>(File.ReadAllText(file), JsonOptions);
            if (loaded is null) return null;
            foreach (var item in loaded.Items)
            {
                loaded._byId[item.Id] = item;

                // The program that was asking is gone, so nothing can answer it now.
                if (item.RequestState == AgentRequestState.Waiting && item.Kind is AgentItemKind.Approval or AgentItemKind.Question) item.RequestState = AgentRequestState.Gone;
                if (item.Kind == AgentItemKind.Tool && item.ToolState == AgentToolState.Running) item.ToolState = AgentToolState.Failed;
            }
            return loaded;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Write($"Couldn't read an agent conversation: {error.Message}");
            return null;
        }
    }

    public void Save(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"{Id}.json");
            File.WriteAllText(file + ".new", JsonSerializer.Serialize(this, JsonOptions));
            File.Move(file + ".new", file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't save an agent conversation: {error.Message}");
        }
    }

    /// <summary>Everything a session needs set up first, made fresh each time so it matches what's set up now.</summary>
    public sealed record Setup(AgentProvider Provider, string McpConfig, IReadOnlyList<string> McpServers, string SkillsPlugin, string Briefing);

    /// <summary>
    /// Sends a message, starting the agent program if it isn't running. What the person is looking at
    /// goes first, in a block Agent Orange writes, so the agent knows what "this" means.
    /// </summary>
    public void Send(string text, string viewLabel, string viewDetail, Func<Setup> prepare)
    {
        if (Folder is null) return;
        Add(new AgentItem { Id = NewId(), Kind = AgentItemKind.User, Text = text, View = viewLabel });
        if (Title.Length == 0) Title = text.Length > 60 ? text[..60].TrimEnd() + "…" : text;

        if (_process is null && !Start(prepare)) return;

        // Nothing typed can close the block early or pass for one.
        static string Plain(string words) => words.Replace("agent-orange-view", "agent-orange-view​", StringComparison.OrdinalIgnoreCase);
        var prompt = $"<agent-orange-view>\n{Plain(viewDetail)}\n</agent-orange-view>\n\n{Plain(text)}";
        Write(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = prompt },
            ["parent_tool_use_id"] = null,
        });
        SetWorking(true);
    }

    private bool Start(Func<Setup> prepare)
    {
        Setup setup;
        try
        {
            setup = prepare();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Problem($"Couldn't set up the agent's tools: {error.Message}");
            return false;
        }

        // The editors' tools are the agent's to use without asking each time. Edits in the folder are too.
        var allowed = setup.McpServers.Count == 0 ? "" : $" --allowedTools \"{string.Join(",", setup.McpServers.Select(server => $"mcp__{server}"))}\"";
        var arguments = "-p --input-format stream-json --output-format stream-json --verbose --include-partial-messages --permission-prompt-tool=stdio"
            + " --permission-mode acceptEdits --setting-sources project,local --strict-mcp-config"
            + $" --mcp-config \"{setup.McpConfig}\" --plugin-dir \"{setup.SkillsPlugin}\" --append-system-prompt-file \"{setup.Briefing}\"{allowed}"
            + (SessionId is null ? "" : $" --resume {SessionId}");

        Process? process;
        try
        {
            process = Process.Start(AgentProviders.SessionStartInfo(setup.Provider, arguments, Folder!));
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't start {setup.Provider.Program}: {error.Message}");
            process = null;
        }
        if (process is null)
        {
            Problem($"Couldn't start \"{setup.Provider.Program}\".");
            return false;
        }

        ProviderId = setup.Provider.Id;
        _process = process;
        _stopping = false;
        ChildProcesses.Track(process);
        _ = ReadAsync(process);
        return true;
    }

    private async Task ReadAsync(Process process)
    {
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line) Read(line);
            await process.WaitForExitAsync();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            // Its pipes close under it when it's stopped partway.
        }

        if (_process != process) return;
        _process = null;
        process.Dispose();
        foreach (var item in Items.Where(item => item.RequestState == AgentRequestState.Waiting && item.Kind is AgentItemKind.Approval or AgentItemKind.Question).ToList())
        {
            item.RequestState = AgentRequestState.Gone;
            ItemChanged?.Invoke(item);
        }
        EndStreaming();
        if (IsWorking && !_stopping)
        {
            var detail = (await errors).Trim();
            Problem(detail.Length > 0 ? $"The agent stopped: {Shortened(detail, 600)}" : "The agent stopped without finishing.");
        }
        else if (_stopping)
        {
            Notice("Stopped.");
        }
        SetWorking(false);
    }

    /// <summary>Asks it to stop what it's doing. If it doesn't, it's ended.</summary>
    public async void Stop()
    {
        if (_process is not { } process || !IsWorking) return;
        _stopping = true;
        Write(new JsonObject { ["type"] = "control_request", ["request_id"] = NewId(), ["request"] = new JsonObject { ["subtype"] = "interrupt" } });

        await Task.Delay(StopWaitsFor);
        if (_process == process && IsWorking) End();
    }

    /// <summary>Ends the agent program. The conversation picks back up from where it was with the next message.</summary>
    public void End()
    {
        if (_process is not { } process) return;
        _stopping = true;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    public void Answer(AgentItem request, bool allow)
    {
        if (request.RequestState != AgentRequestState.Waiting) return;
        request.RequestState = allow ? AgentRequestState.Allowed : AgentRequestState.Denied;
        Respond(request, allow
            ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = request.RequestInput?.DeepClone() ?? new JsonObject() }
            : new JsonObject { ["behavior"] = "deny", ["message"] = "The person said no to this." });
    }

    /// <summary>The person's answers to its questions, by question, each the label of what they chose, or what they typed.</summary>
    public void AnswerQuestions(AgentItem request, Dictionary<string, string> answers)
    {
        if (request.RequestState != AgentRequestState.Waiting) return;
        request.RequestState = AgentRequestState.Answered;
        request.Answers = answers;
        var input = request.RequestInput?.DeepClone() as JsonObject ?? new JsonObject();
        input["answers"] = new JsonObject(answers.Select(answer => KeyValuePair.Create(answer.Key, (JsonNode?)answer.Value)));
        Respond(request, new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input });
    }

    private void Respond(AgentItem request, JsonObject response)
    {
        if (request.ToolUseId is { } toolUse) response["toolUseID"] = toolUse;
        Write(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = request.Id, ["response"] = response },
        });
        ItemChanged?.Invoke(request);
        StateChanged?.Invoke();
    }

    private void Write(JsonObject message)
    {
        if (_process is not { } process) return;
        try
        {
            process.StandardInput.WriteLine(message.ToJsonString());
            process.StandardInput.Flush();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // It's ending, which ReadAsync deals with.
        }
    }

    // What comes back

    // One JSON event per line. Anything unknown is skipped, as its events change between versions.
    private void Read(string line)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return; // Not an event, such as a warning printed by something it started.
        }
        if (root is not JsonObject json) return;
        if (Str(json, "session_id") is { Length: > 0 } session && session != SessionId) SessionId = session;

        // What its own subagents do stays inside them.
        if (json["parent_tool_use_id"] is JsonValue) return;

        switch (Str(json, "type"))
        {
            case "stream_event" when json["event"] is JsonObject streamed:
                ReadStreamed(streamed);
                break;
            case "assistant" when json["message"] is JsonObject message:
                ReadAssistant(message);
                break;
            case "user" when json["message"] is JsonObject said && said["content"] is JsonArray content:
                ReadToolResults(content);
                break;
            case "control_request" when json["request"] is JsonObject request && Str(request, "subtype") == "can_use_tool":
                ReadPermissionRequest(Str(json, "request_id") ?? NewId(), request);
                break;
            case "control_cancel_request" when Str(json, "request_id") is { } cancelled && _byId.TryGetValue(cancelled, out var asked):
                asked.RequestState = AgentRequestState.Gone;
                ItemChanged?.Invoke(asked);
                StateChanged?.Invoke();
                break;
            case "result":
                EndStreaming();
                if (json["is_error"]?.GetValueKind() == JsonValueKind.True && !_stopping) Problem(Str(json, "result") is { Length: > 0 } reason ? reason : "The agent stopped with an error.");
                _stopping = false;
                SetWorking(false);
                break;
        }
    }

    private void ReadStreamed(JsonObject streamed)
    {
        switch (Str(streamed, "type"))
        {
            case "message_start":
                _messageId = Str(streamed["message"] as JsonObject, "id");
                break;
            case "content_block_start" when streamed["content_block"] is JsonObject block && Str(block, "type") == "text":
                var item = new AgentItem { Id = TextId(_messageId, streamed["index"]), Kind = AgentItemKind.Assistant, Text = Str(block, "text") ?? "", Streaming = true };
                if (!_byId.ContainsKey(item.Id)) Add(item);
                break;
            case "content_block_delta" when streamed["delta"] is JsonObject delta && Str(delta, "type") == "text_delta":
                if (_byId.TryGetValue(TextId(_messageId, streamed["index"]), out var growing))
                {
                    growing.Text += Str(delta, "text");
                    ItemChanged?.Invoke(growing);
                }
                break;
            case "content_block_stop" when _byId.TryGetValue(TextId(_messageId, streamed["index"]), out var done):
                done.Streaming = false;
                ItemChanged?.Invoke(done);
                break;
        }
    }

    // The whole message, after its parts streamed in. Whatever the stream missed is filled in here.
    private void ReadAssistant(JsonObject message)
    {
        if (message["content"] is not JsonArray content) return;
        var messageId = Str(message, "id");
        for (var index = 0; index < content.Count; index++)
        {
            if (content[index] is not JsonObject part) continue;
            switch (Str(part, "type"))
            {
                case "text" when Str(part, "text") is { } text:
                    var id = TextId(messageId, JsonValue.Create(index));
                    if (_byId.TryGetValue(id, out var known))
                    {
                        known.Text = text;
                        known.Streaming = false;
                        ItemChanged?.Invoke(known);
                    }
                    else if (text.Trim().Length > 0)
                    {
                        Add(new AgentItem { Id = id, Kind = AgentItemKind.Assistant, Text = text });
                    }
                    break;

                // Its questions for the person come as a request of their own.
                case "tool_use" when Str(part, "name") is { } name && name != "AskUserQuestion" && Str(part, "id") is { } toolId && !_byId.ContainsKey(toolId):
                    Add(new AgentItem { Id = toolId, Kind = AgentItemKind.Tool, Tool = name, Text = DescribeTool(name, part["input"] as JsonObject) });
                    break;
            }
        }
    }

    private void ReadToolResults(JsonArray content)
    {
        foreach (var part in content.OfType<JsonObject>())
        {
            if (Str(part, "type") != "tool_result" || Str(part, "tool_use_id") is not { } toolId || !_byId.TryGetValue(toolId, out var tool)) continue;
            tool.ToolState = part["is_error"]?.GetValueKind() == JsonValueKind.True ? AgentToolState.Failed : AgentToolState.Done;
            tool.Detail = Shortened(ResultText(part["content"]), LongestDetail);
            ItemChanged?.Invoke(tool);
        }
    }

    private static string ResultText(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Select(part => Str(part, "text")).OfType<string>()),
        _ => "",
    };

    private void ReadPermissionRequest(string requestId, JsonObject request)
    {
        var name = Str(request, "tool_name") ?? "a tool";
        var input = request["input"] as JsonObject;
        var item = new AgentItem
        {
            Id = requestId,
            Kind = name == "AskUserQuestion" ? AgentItemKind.Question : AgentItemKind.Approval,
            Tool = name,
            RequestState = AgentRequestState.Waiting,
            RequestInput = input?.DeepClone(),
            ToolUseId = Str(request, "tool_use_id"),
        };
        if (item.Kind == AgentItemKind.Question)
        {
            item.Questions = [.. (input?["questions"] as JsonArray ?? []).OfType<JsonObject>().Select(question => new AgentQuestion(
                Str(question, "header") ?? "",
                Str(question, "question") ?? "",
                [.. (question["options"] as JsonArray ?? []).OfType<JsonObject>().Select(option => new AgentOption(Str(option, "label") ?? "", Str(option, "description") ?? ""))],
                question["multiSelect"]?.GetValueKind() == JsonValueKind.True))];
        }
        else
        {
            item.Text = Str(request, "title") is { Length: > 0 } title ? title : DescribeTool(name, input);
            item.Detail = Shortened(input?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "", LongestDetail);
        }
        Add(item);
        StateChanged?.Invoke();
    }

    // In a few words: the tool, and what it's working on.
    private static string DescribeTool(string name, JsonObject? input)
    {
        var shortName = name.StartsWith("mcp__", StringComparison.Ordinal) && name.Split("__") is [_, var server, var tool] ? $"{server}: {tool}" : name;
        foreach (var key in new[] { "file_path", "path", "command", "pattern", "url", "query", "description" })
        {
            if (Str(input, key) is { Length: > 0 } value) return $"{shortName}  {Shortened(value.ReplaceLineEndings(" "), 160)}";
        }
        return shortName;
    }

    // Keeping the list

    private void Add(AgentItem item)
    {
        Items.Add(item);
        _byId[item.Id] = item;
        Updated = DateTimeOffset.UtcNow;
        ItemChanged?.Invoke(item);
    }

    private void Problem(string text) => Add(new AgentItem { Id = NewId(), Kind = AgentItemKind.Problem, Text = text });

    private void Notice(string text) => Add(new AgentItem { Id = NewId(), Kind = AgentItemKind.Notice, Text = text });

    private void EndStreaming()
    {
        foreach (var item in Items.Where(item => item.Streaming).ToList())
        {
            item.Streaming = false;
            ItemChanged?.Invoke(item);
        }
        foreach (var tool in Items.Where(item => item.Kind == AgentItemKind.Tool && item.ToolState == AgentToolState.Running).ToList())
        {
            tool.ToolState = AgentToolState.Done;
            ItemChanged?.Invoke(tool);
        }
    }

    private void SetWorking(bool working)
    {
        if (IsWorking == working) return;
        IsWorking = working;
        StateChanged?.Invoke();
    }

    private static string TextId(string? messageId, JsonNode? index) => $"{messageId}:text:{index}";

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string Shortened(string text, int longest) => text.Length > longest ? text[..longest] + "…" : text;

    private static string? Str(JsonObject? json, string name) =>
        json?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
