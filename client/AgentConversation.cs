using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrange;

// Saved as numbers, so new kinds only ever go on the end.
public enum AgentItemKind { User, Assistant, Tool, Approval, Question, Notice, Problem, Plan }

public enum AgentToolState { Running, Done, Failed }

public enum AgentRequestState { Waiting, Allowed, Denied, Answered, Gone }

public sealed record AgentOption(string Label, string Description);

public sealed record AgentQuestion(string Header, string Question, List<AgentOption> Options, bool MultiSelect);

/// <summary>One step of the plan the agent keeps for itself as it works (its TodoWrite list).</summary>
public sealed record AgentStep(string Content, string Status, string? ActiveForm);

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

    /// <summary>A message from the person: the pictures and files sent with it.</summary>
    public List<AgentAttachment>? Attachments { get; set; }

    /// <summary>A message Agent Orange sent for the person, such as going on once a usage limit reset.</summary>
    public bool Automatic { get; set; }

    [JsonIgnore] public bool Streaming { get; set; }

    // What a request needs to be answered while the agent is still waiting on it.
    [JsonIgnore] public JsonNode? RequestInput { get; set; }
    [JsonIgnore] public string? ToolUseId { get; set; }
    [JsonIgnore] public JsonArray? Suggestions { get; set; } // Rules that would let it do the same again without asking.
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

    /// <summary>The model and how hard it thinks, or null for the provider's own choice. A change takes effect from the next message.</summary>
    public string? Model { get; set; }
    public string? Effort { get; set; }

    /// <summary>
    /// What it may do without asking: Claude Code's own permission modes. "acceptEdits" (the default)
    /// edits the project's files freely and asks for anything else, "default" asks first for edits too,
    /// "plan" only looks and plans, and "bypassPermissions" asks for nothing. Changed straight away,
    /// even while it works.
    /// </summary>
    public string PermissionMode { get; set; } = "acceptEdits";

    /// <summary>Done with: put away at the bottom of the list, its agent program ended. Sending to it again takes it back out.</summary>
    public bool Settled { get; set; }
    public DateTimeOffset? SettledAt { get; set; }

    /// <summary>How full its context window is, in tokens, as it last said.</summary>
    public int ContextUsed { get; set; }
    public int ContextWindow { get; set; }

    /// <summary>It stopped as its login ran out of usage. It goes on by itself once that resets, unless told not to.</summary>
    public bool IsLimited { get; set; }
    public DateTimeOffset? LimitResetsAt { get; set; }
    public bool ResumeAtReset { get; set; } = true;

    /// <summary>The plan it keeps for itself as it works, as it last wrote it.</summary>
    public List<AgentStep>? Steps { get; set; }

    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public List<AgentItem> Items { get; init; } = [];

    /// <summary>It finished, or wants something, while no one was looking.</summary>
    public bool Unseen { get; set; }

    [JsonIgnore] public string? Folder { get; set; }
    [JsonIgnore] public bool IsWorking { get; private set; }
    [JsonIgnore] public DateTimeOffset WorkingSince { get; private set; }
    [JsonIgnore] public bool IsWaiting => Items.Any(item => item.RequestState == AgentRequestState.Waiting && item.Kind is AgentItemKind.Approval or AgentItemKind.Question or AgentItemKind.Plan);

    /// <summary>What it's waiting on that isn't the person, such as a retry after an error from the model's side.</summary>
    [JsonIgnore] public string? Retrying { get; private set; }

    /// <summary>On the window's thread: its login said how much of its limits are used, all of them when complete.</summary>
    public event Action<string, IReadOnlyList<AgentLimit>, bool>? LimitsReported;

    /// <summary>On the window's thread: an item was added or changed.</summary>
    public event Action<AgentItem>? ItemChanged;

    /// <summary>On the window's thread: it started or stopped working, or wants the person.</summary>
    public event Action? StateChanged;

    private Process? _process;
    private bool _stopping;
    private bool _restarting; // Ended so the next message starts it with what's changed, which says nothing.
    private int _turns; // Messages sent that it hasn't finished with, as one sent while it works waits its turn.
    private string? _startedWith; // The model and effort it was started with.
    private string? _runningMode; // The permission mode it's in, which can change while it runs.
    private readonly Dictionary<string, Action<JsonObject?>> _controls = []; // Replies awaited, by request.
    private readonly Dictionary<string, DateTimeOffset?> _limits = []; // Limits run out during this turn, and when each resets.
    private DateTimeOffset _usageAskedAt;
    private readonly Dictionary<string, AgentItem> _byId = [];
    private string? _messageId; // The assistant message being streamed.
    private readonly HashSet<string> _confirmed = []; // Streamed text the whole message has since been read for.

    public static AgentConversation? Load(string file)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<AgentConversation>(File.ReadAllText(file), JsonOptions);
            if (loaded is null) return null;

            // Older versions could keep a reply twice, once as streamed and once whole.
            loaded.Items.RemoveAll(item => item.Kind == AgentItemKind.Assistant && loaded.Items.Any(other => !ReferenceEquals(other, item)
                && other.Kind == AgentItemKind.Assistant && other.Text == item.Text && SameMessage(other.Id, item.Id) && loaded.Items.IndexOf(other) < loaded.Items.IndexOf(item)));
            foreach (var item in loaded.Items)
            {
                loaded._byId[item.Id] = item;

                // The program that was asking is gone, so nothing can answer it now.
                if (item.RequestState == AgentRequestState.Waiting && item.Kind is AgentItemKind.Approval or AgentItemKind.Question or AgentItemKind.Plan) item.RequestState = AgentRequestState.Gone;
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
    public sealed record Setup(AgentProvider Provider, string McpConfig, IReadOnlyList<string> McpServers, string SkillsPlugin, string Briefing, string AttachmentsFolder);

    /// <summary>
    /// Sends a message, starting the agent program if it isn't running. What the person is looking at
    /// goes first, in a block Agent Orange writes, so the agent knows what "this" means.
    /// </summary>
    public void Send(string text, IReadOnlyList<AgentAttachment> attachments, string viewLabel, string viewDetail, Func<Setup> prepare, bool automatic = false)
    {
        if (Folder is null) return;
        Settled = false;
        SettledAt = null;
        IsLimited = false;
        Add(new AgentItem { Id = NewId(), Kind = AgentItemKind.User, Text = text, View = viewLabel, Attachments = attachments.Count == 0 ? null : [.. attachments], Automatic = automatic });
        var named = text.Length > 0 ? text : attachments.Any(attachment => attachment.IsImage) ? "A picture" : $"About {attachments.FirstOrDefault()?.Name}";
        if (Title.Length == 0) Title = named.Length > 60 ? named[..60].TrimEnd() + "…" : named;

        if (_process is not null && (_startedWith != $"{Model}|{Effort}" || _runningMode != PermissionMode)) Restart();
        if (_process is null && !Start(prepare)) return;

        // Nothing typed can close the block early or pass for one.
        static string Plain(string words) => words.Replace("agent-orange-view", "agent-orange-view​", StringComparison.OrdinalIgnoreCase);
        var prompt = $"<agent-orange-view>\n{Plain(viewDetail)}\n</agent-orange-view>\n\n{Plain(text)}";
        if (NeedsHandoff && SessionId is null)
        {
            // Everything up to the message being sent now, which is last.
            prompt = Handoff(Items.Take(Items.Count - 1)) + prompt;
        }
        NeedsHandoff = false;

        // Pictures and PDFs go with the message, for it to see straight away. Every file is also named
        // where it's kept, for it to open or copy into the game itself, as T3 Code does.
        var content = new JsonArray();
        var listed = new List<string>();
        foreach (var attachment in attachments)
        {
            listed.Add($"[Attached {(attachment.IsImage ? "image" : "file")} \"{attachment.Name}\" is saved at: {attachment.Path}]");
            var block = attachment.IsImage ? "image" : AgentAttachments.IsPdf(attachment.Path) ? "document" : null;
            if (block is null) continue;
            try
            {
                if (block == "document" && new FileInfo(attachment.Path).Length > AgentAttachments.LargestPdf) continue;
                content.Add(new JsonObject
                {
                    ["type"] = block,
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = block == "image" ? AgentAttachments.MediaType(attachment.Path) : "application/pdf",
                        ["data"] = Convert.ToBase64String(File.ReadAllBytes(attachment.Path)),
                    },
                });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Notice($"Couldn't send {attachment.Name}: {error.Message}");
            }
        }
        if (listed.Count > 0) prompt += "\n\n" + string.Join("\n", listed);
        content.Add(new JsonObject { ["type"] = "text", ["text"] = prompt });
        Write(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
            ["parent_tool_use_id"] = null,
        });
        _turns++;
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
            + $" --permission-mode {PermissionMode} --setting-sources project,local --strict-mcp-config"
            + $" --mcp-config \"{setup.McpConfig}\" --plugin-dir \"{setup.SkillsPlugin}\" --append-system-prompt-file \"{setup.Briefing}\"{allowed}"
            + $" --add-dir \"{setup.AttachmentsFolder}\""
            + (Model is { Length: > 0 } model ? $" --model {model}" : "")
            + (Effort is { Length: > 0 } effort ? $" --effort {effort}" : "")
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
        _restarting = false;
        _startedWith = $"{Model}|{Effort}";
        _runningMode = PermissionMode;
        _controls.Clear();
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

        if (_process != process)
        {
            process.Dispose();
            return;
        }
        _process = null;
        process.Dispose();
        foreach (var item in Items.Where(item => item.RequestState == AgentRequestState.Waiting && item.Kind is AgentItemKind.Approval or AgentItemKind.Question).ToList())
        {
            item.RequestState = AgentRequestState.Gone;
            ItemChanged?.Invoke(item);
        }
        EndStreaming();
        if (_restarting)
        {
            _restarting = false;
        }
        else if (IsWorking && !_stopping)
        {
            var detail = (await errors).Trim();
            Problem(detail.Length > 0 ? $"The agent stopped: {Shortened(detail, 600)}" : "The agent stopped without finishing.");
        }
        else if (_stopping)
        {
            Notice("Stopped.");
        }
        _turns = 0;
        SetWorking(false);
    }

    /// <summary>Asks it to stop what it's doing. If it doesn't, it's ended.</summary>
    public async void Stop()
    {
        if (_process is not { } process || !IsWorking) return;
        _stopping = true;

        // Messages waiting their turn go too, so only the one it's on is left to finish.
        _turns = Math.Min(_turns, 1);
        Write(new JsonObject { ["type"] = "control_request", ["request_id"] = NewId(), ["request"] = new JsonObject { ["subtype"] = "interrupt", ["cancel_queued"] = true } });

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

    // Ended without a word, so the message about to be sent starts it the new way, picking the conversation back up.
    private void Restart()
    {
        if (_process is not { } process) return;
        _restarting = true;
        _process = null;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>The next message carries what was said so far, as the agent program starting with it has none of it.</summary>
    public bool NeedsHandoff { get; set; }

    /// <summary>
    /// Goes on with another login of the same kind, such as a second subscription when one runs out.
    /// Claude Code keeps each conversation as a file in its login folder, so that file is copied to
    /// the new login's, and it picks up with everything as it was. Without it, the conversation so far
    /// is written out and goes with the next message.
    /// </summary>
    public void SwitchProvider(AgentProvider from, AgentProvider to)
    {
        if (from.Id == to.Id || IsWorking) return;
        Restart();
        ProviderId = to.Id;
        if (SessionId is { } session && CarrySession(from.LoginFolder, to.LoginFolder, session))
        {
            Notice($"Switched to {to.Label}. It picks up with everything said so far.");
        }
        else if (Items.Any(item => item.Kind is AgentItemKind.User or AgentItemKind.Assistant))
        {
            SessionId = null;
            NeedsHandoff = true;
            Notice($"Switched to {to.Label}. What was said so far goes with your next message.");
        }
        StateChanged?.Invoke();
    }

    private static bool CarrySession(string fromLogin, string toLogin, string session)
    {
        try
        {
            var projects = Path.Combine(fromLogin, "projects");
            if (!Directory.Exists(projects)) return false;
            var file = Directory.EnumerateFiles(projects, $"{session}.jsonl", SearchOption.AllDirectories).FirstOrDefault();
            if (file is null) return false;
            var target = Path.Combine(toLogin, Path.GetRelativePath(fromLogin, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't carry a conversation to another login: {error.Message}");
            return false;
        }
    }

    // What was said, the latest kept when it's long, for an agent program that wasn't there for it.
    private static string Handoff(IEnumerable<AgentItem> items)
    {
        const int Longest = 60_000;
        var said = new List<string>();
        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case AgentItemKind.User:
                    said.Add($"The person: {item.Text}{(item.Attachments is { Count: > 0 } sent ? $" [sent {string.Join(", ", sent.Select(attachment => attachment.Name))}]" : "")}");
                    break;
                case AgentItemKind.Assistant:
                    said.Add($"You: {item.Text}");
                    break;
                case AgentItemKind.Tool:
                    said.Add($"(You used {item.Text})");
                    break;
                case AgentItemKind.Question when item.Answers is { Count: > 0 } answers:
                    said.Add($"(You asked, and the person answered: {string.Join("; ", answers.Select(answer => $"{answer.Key} → {answer.Value}"))})");
                    break;
            }
        }
        var text = string.Join("\n\n", said);
        if (text.Length > Longest) text = "…" + text[^Longest..];
        return "<agent-orange-handoff>\nThis conversation began with another login, and its agent program couldn't come along, so here is what was said so far, the latest last. Go on from it as if you'd been there.\n\n"
            + text.Replace("agent-orange-handoff", "agent-orange-handoff​", StringComparison.OrdinalIgnoreCase) + "\n</agent-orange-handoff>\n\n";
    }

    /// <summary>Changes what it may do without asking, for what it's doing now as well as what comes next.</summary>
    public void SetPermissionMode(string mode)
    {
        if (mode == PermissionMode) return;
        PermissionMode = mode;
        if (_process is not null) Control(new JsonObject { ["subtype"] = "set_permission_mode", ["mode"] = mode }, reply =>
        {
            // Full access can only be given as it starts, so that waits for the next message, which starts it again.
            if (reply is not null) _runningMode = mode;
            else if (IsWorking && PermissionMode == mode) Notice("That takes effect from your next message, as it can't be changed while it works.");
        });
        StateChanged?.Invoke();
    }

    /// <summary>Puts it away, ending its agent program, or takes it back out.</summary>
    public bool Settle(bool settled)
    {
        if (settled == Settled) return true;
        if (settled && (IsWorking || IsWaiting)) return false;
        Settled = settled;
        SettledAt = settled ? DateTimeOffset.UtcNow : null;
        if (settled) Restart(); // Quietly: it was finished anyway.
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Allows it, and lets it do the same again for the rest of this conversation without asking.</summary>
    public void AllowAlways(AgentItem request)
    {
        if (request.RequestState != AgentRequestState.Waiting) return;
        request.RequestState = AgentRequestState.Allowed;
        var rules = new JsonArray();
        foreach (var suggestion in (request.Suggestions ?? []).OfType<JsonObject>())
        {
            var rule = (JsonObject)suggestion.DeepClone();
            rule["destination"] = "session";
            rules.Add(rule);
        }
        Respond(request, new JsonObject { ["behavior"] = "allow", ["updatedInput"] = request.RequestInput?.DeepClone() ?? new JsonObject(), ["updatedPermissions"] = rules });
    }

    /// <summary>Its plan, which it waits on: go ahead with it, editing freely, or keep planning with what the person says next.</summary>
    public void AnswerPlan(AgentItem request, bool goAhead)
    {
        if (request.RequestState != AgentRequestState.Waiting) return;
        request.RequestState = goAhead ? AgentRequestState.Allowed : AgentRequestState.Denied;
        Respond(request, goAhead
            ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = request.RequestInput?.DeepClone() ?? new JsonObject() }
            : new JsonObject { ["behavior"] = "deny", ["message"] = "The person wants to keep planning. Stop here and wait for what they say next." });
        if (goAhead && PermissionMode == "plan") SetPermissionMode("acceptEdits");
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

    // A request of Agent Orange's own, with what to do with the reply, or null for an error.
    private void Control(JsonObject request, Action<JsonObject?>? replied = null)
    {
        var id = NewId();
        if (replied is not null) _controls[id] = replied;
        Write(new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request });
    }

    // How full its context is, every turn, and its login's limits, now and then. Neither uses any of them.
    private void AskForUsage()
    {
        Control(new JsonObject { ["subtype"] = "get_context_usage" }, reply =>
        {
            if (reply?["totalTokens"] is not JsonValue used || !used.TryGetValue<int>(out var tokens)) return;
            ContextUsed = tokens;
            if (reply["maxTokens"] is JsonValue most && most.TryGetValue<int>(out var window)) ContextWindow = window;
            StateChanged?.Invoke();
        });
        if (DateTimeOffset.UtcNow - _usageAskedAt < TimeSpan.FromMinutes(2) || ProviderId is not { } provider) return;
        _usageAskedAt = DateTimeOffset.UtcNow;
        Control(new JsonObject { ["subtype"] = "get_usage", ["skip_behaviors"] = true }, reply =>
        {
            if (reply is not null) LimitsReported?.Invoke(provider, AgentUsage.FromUsage(reply), true);
        });
    }

    private void ReadLimit(JsonObject? info)
    {
        if (AgentUsage.FromEvent(info) is not { } limit) return;
        if (limit.Blocked) _limits[limit.Kind] = limit.ResetsAt;
        else _limits.Remove(limit.Kind);
        if (ProviderId is { } provider) LimitsReported?.Invoke(provider, [limit], false);
    }

    // Its words when a limit stops it without saying so otherwise, as an API key's does: "(reset after 2h5m)".
    private static bool SaysLimited(string text, out DateTimeOffset? resets)
    {
        resets = null;
        var after = System.Text.RegularExpressions.Regex.Match(text, @"reset after (?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?");
        if (after.Success && after.Length > "reset after ".Length)
        {
            static int Part(System.Text.RegularExpressions.Group group) => group.Success ? int.Parse(group.Value) : 0;
            resets = DateTimeOffset.UtcNow + new TimeSpan(Part(after.Groups[1]), Part(after.Groups[2]), Part(after.Groups[3]));
            return true;
        }
        var old = System.Text.RegularExpressions.Regex.Match(text, @"usage limit reached\|(\d{9,})");
        if (old.Success)
        {
            resets = DateTimeOffset.FromUnixTimeSeconds(long.Parse(old.Groups[1].Value));
            return true;
        }
        return text.Contains("usage limit", StringComparison.OrdinalIgnoreCase) || text.Contains("hit your limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
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
                // A reply the limit stopped carries what the limit is.
                if (Str(json, "error") == "rate_limit" || Str(message, "error") == "rate_limit") ReadLimit(json["api_error_params"]?["rate_limit_info"] as JsonObject ?? message["api_error_params"]?["rate_limit_info"] as JsonObject);
                ReadAssistant(message);
                break;
            case "rate_limit_event":
                ReadLimit(json["rate_limit_info"] as JsonObject);
                break;
            case "system":
                ReadSystem(json);
                break;
            case "control_response" when json["response"] is JsonObject reply && Str(reply, "request_id") is { } replyTo && _controls.Remove(replyTo, out var replied):
                replied(Str(reply, "subtype") == "success" ? reply["response"] as JsonObject ?? new JsonObject() : null);
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
                Retrying = null;
                var failed = json["is_error"]?.GetValueKind() == JsonValueKind.True;
                var reason = Str(json, "result") ?? "";
                DateTimeOffset? resets = null;
                if (failed && !_stopping && (_limits.Count > 0 || SaysLimited(reason, out resets)))
                {
                    // Its login ran out. It goes on once that resets, from the latest of the limits it hit.
                    IsLimited = true;
                    LimitResetsAt = _limits.Count > 0 && _limits.Values.All(at => at is not null) ? _limits.Values.Max() : resets;
                    _turns = 0;
                }
                else if (failed && !_stopping)
                {
                    Problem(reason.Length > 0 ? reason : "The agent stopped with an error.");
                }
                _limits.Clear();
                _stopping = false;
                _turns = Math.Max(0, _turns - 1);
                AskForUsage();
                SetWorking(_turns > 0);
                break;
        }
    }

    private void ReadSystem(JsonObject json)
    {
        if (Str(json, "permissionMode") is { Length: > 0 } mode) _runningMode = mode;
        switch (Str(json, "subtype"))
        {
            case "init":
                AskForUsage();
                break;
            case "status" when Str(json, "status") == "compacting":
                Retrying = "Making room in its memory";
                StateChanged?.Invoke();
                break;
            case "compact_boundary":
                Retrying = null;
                var before = json["compact_metadata"]?["pre_tokens"]?.GetValue<double>();
                var after = json["compact_metadata"]?["post_tokens"]?.GetValue<double>();
                if (after is { } left) ContextUsed = (int)left;
                Notice(before is { } was && after is { } now
                    ? $"Made room in its memory, summing up what came before ({Tokens(was)} down to {Tokens(now)})."
                    : "Made room in its memory, summing up what came before.");
                StateChanged?.Invoke();
                break;
            case "api_retry":
                var attempt = json["attempt"]?.ToString();
                var most = json["max_retries"]?.ToString();
                Retrying = attempt is null ? "Trying again" : $"Trying again ({attempt} of {most})";
                StateChanged?.Invoke();
                break;
            case "informational" when Str(json, "level") is { } level && level != "info" && (Str(json, "content") ?? Str(json, "message")) is { Length: > 0 } words:
                Notice(words);
                break;
        }
    }

    internal static string Tokens(double count) => count >= 1000 ? $"{count / 1000:0.#}k" : $"{count:0}";

    private void ReadStreamed(JsonObject streamed)
    {
        switch (Str(streamed, "type"))
        {
            case "message_start":
                _messageId = Str(streamed["message"] as JsonObject, "id");
                break;
            case "content_block_start" when streamed["content_block"] is JsonObject started && Str(started, "type") == "tool_use"
                && Str(started, "name") is { } toolName && toolName is not "AskUserQuestion" and not "ExitPlanMode" && Str(started, "id") is { } toolId && !_byId.ContainsKey(toolId):
                Add(new AgentItem { Id = toolId, Kind = AgentItemKind.Tool, Tool = toolName, Text = DescribeTool(toolName, null) });
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
                // Claude Code sends each part of a message as a message of its own, so a part's place here
                // isn't its place in the stream (a thinking part streams first, for one). Streamed text is
                // matched up in order instead: the first of this message's not yet confirmed.
                case "text" when Str(part, "text") is { } text:
                    var prefix = $"{messageId}:text:";
                    var known = Items.FirstOrDefault(item => item.Kind == AgentItemKind.Assistant && item.Id.StartsWith(prefix, StringComparison.Ordinal) && _confirmed.Add(item.Id));
                    if (known is not null)
                    {
                        known.Text = text;
                        known.Streaming = false;
                        ItemChanged?.Invoke(known);
                    }
                    else if (text.Trim().Length > 0)
                    {
                        var id = $"{messageId}:text:said{_confirmed.Count}";
                        _confirmed.Add(id);
                        Add(new AgentItem { Id = id, Kind = AgentItemKind.Assistant, Text = text });
                    }
                    break;

                // Its questions for the person come as a request of their own.
                case "tool_use" when Str(part, "name") == "TodoWrite" && part["input"] is JsonObject planned && planned["todos"] is JsonArray todos:
                    Steps = [.. todos.OfType<JsonObject>().Select(todo => new AgentStep(Str(todo, "content") ?? "", Str(todo, "status") ?? "pending", Str(todo, "activeForm")))];
                    StateChanged?.Invoke();
                    goto default;
                default:
                    if (Str(part, "type") != "tool_use" || Str(part, "name") is not { } name || name == "AskUserQuestion" || name == "ExitPlanMode" || Str(part, "id") is not { } toolId) break;
                    if (_byId.TryGetValue(toolId, out var started))
                    {
                        started.Text = DescribeTool(name, part["input"] as JsonObject);
                        ItemChanged?.Invoke(started);
                    }
                    else
                    {
                        Add(new AgentItem { Id = toolId, Kind = AgentItemKind.Tool, Tool = name, Text = DescribeTool(name, part["input"] as JsonObject) });
                    }
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
            Kind = name == "AskUserQuestion" ? AgentItemKind.Question : name == "ExitPlanMode" ? AgentItemKind.Plan : AgentItemKind.Approval,
            Tool = name,
            RequestState = AgentRequestState.Waiting,
            RequestInput = input?.DeepClone(),
            ToolUseId = Str(request, "tool_use_id"),
            Suggestions = request["permission_suggestions"]?.DeepClone() as JsonArray,
        };
        if (item.Kind == AgentItemKind.Question)
        {
            item.Questions = [.. (input?["questions"] as JsonArray ?? []).OfType<JsonObject>().Select(question => new AgentQuestion(
                Str(question, "header") ?? "",
                Str(question, "question") ?? "",
                [.. (question["options"] as JsonArray ?? []).OfType<JsonObject>().Select(option => new AgentOption(Str(option, "label") ?? "", Str(option, "description") ?? ""))],
                question["multiSelect"]?.GetValueKind() == JsonValueKind.True))];
        }
        else if (item.Kind == AgentItemKind.Plan)
        {
            item.Text = Str(input, "plan") ?? "";
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
        if (working) WorkingSince = DateTimeOffset.UtcNow;
        StateChanged?.Invoke();
    }

    private static string TextId(string? messageId, JsonNode? index) => $"{messageId}:text:{index}";

    private static bool SameMessage(string one, string other)
    {
        var at = one.IndexOf(":text:", StringComparison.Ordinal);
        return at > 0 && string.CompareOrdinal(one, 0, other, 0, at + 6) == 0;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string Shortened(string text, int longest) => text.Length > longest ? text[..longest] + "…" : text;

    private static string? Str(JsonObject? json, string name) =>
        json?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
