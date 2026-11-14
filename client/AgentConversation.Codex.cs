using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>A model Codex offers, with the efforts it takes.</summary>
public sealed record CodexModel(string Id, string Name, bool IsDefault, IReadOnlyList<string> Efforts, string? DefaultEffort);

/// <summary>
/// Codex, driven as T3 Code drives it: "codex app-server", one long-lived program speaking JSON-RPC
/// a line at a time (with no "jsonrpc" field). It's initialised, a thread is started or resumed for
/// the conversation, and each message is a turn on it, carrying the model, effort and what the agent
/// may do, so those change from the next message without starting it again. Its approvals and
/// questions are requests of its own, answered by their id.
///
/// The app's editors go in as MCP servers in the thread's config, as T3 Code gives its own, and the
/// briefing as the thread's developer instructions.
/// </summary>
public sealed partial class AgentConversation
{
    /// <summary>The models each Codex login offers, by login, as it last listed them.</summary>
    public static readonly Dictionary<string, List<CodexModel>> CodexModels = [];

    private int _nextRpc = 1;
    private readonly Dictionary<long, Action<JsonObject?, JsonObject?>> _rpcs = []; // Replies awaited: result, or error.
    private readonly Dictionary<string, JsonNode> _codexRequests = []; // Its requests waiting on the person, by item, with its own id.
    private readonly List<JsonObject> _codexWaiting = []; // Turns sent before the thread was ready.
    private readonly Dictionary<string, string> _codexFiles = []; // File changes' paths, by item, for when it asks about them.
    private string? _codexThread;
    private string? _codexTurn;
    private string? _codexFailure; // Why the turn is failing, from its error notice, said once the turn ends.
    private bool _codexLimited;

    private bool StartCodex(Setup setup)
    {
        Process? process;
        try
        {
            process = Process.Start(AgentProviders.SessionStartInfo(setup.Provider, "app-server -c thread_unload_delay_secs=0", Folder!));
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
        _rpcs.Clear();
        _codexRequests.Clear();
        _codexWaiting.Clear();
        _codexThread = null;
        _codexTurn = null;
        _nextRpc = 1;
        ChildProcesses.Track(process);
        _ = ReadAsync(process);

        var provider = setup.Provider.Id;
        Rpc("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = "Agent Orange", ["title"] = "Agent Orange", ["version"] = "1" },
            ["capabilities"] = new JsonObject { ["experimentalApi"] = true },
        }, (_, error) =>
        {
            // Already initialised counts as done.
            if (error is not null && !(Str(error, "message") ?? "").Contains("Already initialized", StringComparison.OrdinalIgnoreCase))
            {
                Problem($"Codex wouldn't start: {Str(error, "message")}");
                return;
            }
            WriteCodex(new JsonObject { ["method"] = "initialized" });
            ListCodexModels(provider);
            OpenCodexThread(setup);
            AskCodexUsage(force: true);
        });
        return true;
    }

    private void ListCodexModels(string provider)
    {
        Rpc("model/list", new JsonObject(), (result, _) =>
        {
            if (!TakeCodexModels(provider, result)) return;
            StateChanged?.Invoke();
        });
    }

    /// <summary>Keeps a login's models from model/list's reply. False when it didn't list any.</summary>
    public static bool TakeCodexModels(string provider, JsonObject? result)
    {
        if (result?["data"] is not JsonArray models) return false;
        CodexModels[provider] = [.. models.OfType<JsonObject>()
            .Where(model => model["hidden"]?.GetValueKind() != JsonValueKind.True && Str(model, "model") is not null)
            .Select(model => new CodexModel(
                Str(model, "model")!,
                Str(model, "displayName") ?? Str(model, "model")!,
                model["isDefault"]?.GetValueKind() == JsonValueKind.True,
                [.. (model["supportedReasoningEfforts"] as JsonArray ?? []).OfType<JsonObject>().Select(effort => Str(effort, "reasoningEffort")).OfType<string>()],
                Str(model, "defaultReasoningEffort")))];
        return true;
    }

    // The thread: new, or the conversation's own picked back up after a restart.
    private void OpenCodexThread(Setup setup)
    {
        var config = new JsonObject
        {
            ["tools.update_plan.enabled"] = true,
            ["mcp_optional_startup_grace_ms"] = 10000,
        };
        try
        {
            if (JsonNode.Parse(File.ReadAllText(setup.McpConfig))?["mcpServers"] is JsonObject servers)
            {
                foreach (var (name, server) in servers)
                {
                    if (server is not JsonObject each) continue;
                    var codex = new JsonObject();
                    if (Str(each, "url") is { } url) codex["url"] = url;
                    if (Str(each, "command") is { } command) codex["command"] = command;
                    if (each["args"] is JsonArray args) codex["args"] = args.DeepClone();
                    if (each["env"] is JsonObject env) codex["env"] = env.DeepClone();
                    codex["tool_timeout_sec"] = 3900;
                    config[$"mcp_servers.{name}"] = codex;
                }
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't give Codex the editors: {error.Message}");
        }

        var parameters = new JsonObject { ["cwd"] = Folder, ["config"] = config };
        if (Model is { Length: > 0 } model) parameters["model"] = model;
        try
        {
            parameters["developerInstructions"] = File.ReadAllText(setup.Briefing);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't give Codex its briefing: {error.Message}");
        }

        void Opened(JsonObject? result, JsonObject? error)
        {
            if (result?["thread"] is JsonObject thread && Str(thread, "id") is { } id)
            {
                _codexThread = id;
                SessionId = id;
                foreach (var waiting in _codexWaiting) StartCodexTurn(waiting);
                _codexWaiting.Clear();
                return;
            }

            // A thread it can't pick back up starts again, with what was said so far going with the next message.
            if (SessionId is not null && error is not null)
            {
                AppLog.Write($"Codex couldn't resume its thread: {Str(error, "message")}");
                SessionId = null;
                NeedsHandoff = Items.Count(item => item.Kind == AgentItemKind.User) > 1;
                Rpc("thread/start", (JsonObject)parameters.DeepClone(), Opened);
                return;
            }
            Problem($"Codex wouldn't start a conversation: {Str(error, "message") ?? "it didn't say why"}");
            _turns = 0;
            SetWorking(false);
        }

        if (SessionId is { } session)
        {
            var resume = (JsonObject)parameters.DeepClone();
            resume["threadId"] = session;
            resume["excludeTurns"] = true;
            Rpc("thread/resume", resume, Opened);
        }
        else
        {
            Rpc("thread/start", parameters, Opened);
        }
    }

    // A message: its words, its pictures as data, and its files named, with what the agent may do this turn.
    private void SendCodexTurn(string prompt, IReadOnlyList<AgentAttachment> attachments)
    {
        var input = new JsonArray();
        var listed = new List<string>();
        foreach (var attachment in attachments)
        {
            listed.Add($"[Attached {(attachment.IsImage ? "image" : "file")} \"{attachment.Name}\" is saved at: {attachment.Path}]");
            if (!attachment.IsImage) continue;
            try
            {
                input.Add(new JsonObject { ["type"] = "image", ["url"] = $"data:{AgentAttachments.MediaType(attachment.Path)};base64,{Convert.ToBase64String(File.ReadAllBytes(attachment.Path))}" });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Notice($"Couldn't send {attachment.Name}: {error.Message}");
            }
        }
        if (listed.Count > 0) prompt += "\n\n" + string.Join("\n", listed);
        input.Insert(0, new JsonObject { ["type"] = "text", ["text"] = prompt });

        var turn = new JsonObject { ["input"] = input };
        _turns++;
        SetWorking(true);
        if (_codexThread is null) _codexWaiting.Add(turn);
        else StartCodexTurn(turn);
    }

    // T3 Code's policies for its modes: what Codex may do without asking, and where it may write.
    private void StartCodexTurn(JsonObject turn)
    {
        var (approval, sandbox) = PermissionMode switch
        {
            "default" => ("untrusted", "readOnly"),
            "plan" => ("untrusted", "readOnly"),
            "bypassPermissions" => ("never", "dangerFullAccess"),
            _ => ("on-request", "workspaceWrite"),
        };
        turn["threadId"] = _codexThread;
        turn["cwd"] = Folder;
        turn["approvalPolicy"] = approval;
        turn["approvalsReviewer"] = "user";
        turn["sandboxPolicy"] = new JsonObject { ["type"] = sandbox };
        turn["summary"] = "auto";
        var model = Model is { Length: > 0 } chosen ? chosen : ProviderId is { } provider && CodexModels.TryGetValue(provider, out var models) ? models.FirstOrDefault(each => each.IsDefault)?.Id : null;
        if (Model is { Length: > 0 }) turn["model"] = Model;
        if (Effort is { Length: > 0 } effort) turn["effort"] = effort;

        // Plan mode is its own setting, always sent, so leaving it clears it.
        if (model is not null)
        {
            var settings = new JsonObject { ["model"] = model };
            if (Effort is { Length: > 0 } thinks) settings["reasoning_effort"] = thinks;
            turn["collaborationMode"] = new JsonObject { ["mode"] = PermissionMode == "plan" ? "plan" : "default", ["settings"] = settings };
        }
        _codexFailure = null;
        _codexLimited = false;
        Rpc("turn/start", turn, (result, error) =>
        {
            if (result?["turn"] is JsonObject started && Str(started, "id") is { } id) _codexTurn = id;
            if (error is null) return;
            Problem($"Codex didn't take the message: {Str(error, "message")}");
            _turns = Math.Max(0, _turns - 1);
            SetWorking(_turns > 0);
        });
    }

    private void InterruptCodex()
    {
        if (_codexThread is null || _codexTurn is null) return;
        Rpc("turn/interrupt", new JsonObject { ["threadId"] = _codexThread, ["turnId"] = _codexTurn });
    }

    private void AskCodexUsage(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - _usageAskedAt < TimeSpan.FromMinutes(2)) return;
        _usageAskedAt = DateTimeOffset.UtcNow;
        Rpc("account/rateLimits/read", null, (result, _) => ReadCodexLimits(result, complete: true));
    }

    private void ReadCodexLimits(JsonObject? holder, bool complete)
    {
        var snapshot = holder?["rateLimitsByLimitId"]?["codex"] as JsonObject ?? holder?["rateLimits"] as JsonObject;
        if (snapshot is null || ProviderId is not { } provider) return;
        if (Str(snapshot, "limitId") is { } limit && limit != "codex") return;
        LimitsReported?.Invoke(provider, AgentUsage.FromCodex(snapshot), complete);
    }

    // Replies to its approvals: accept, accept for the rest of the session, or decline.
    private void AnswerCodex(AgentItem request, string decision)
    {
        if (_codexRequests.Remove(request.Id, out var id))
        {
            var permissions = request.RequestInput?["permissions"];
            JsonObject result = request.Tool == "permissions"
                ? new JsonObject { ["permissions"] = decision == "decline" ? new JsonObject() : permissions?.DeepClone(), ["scope"] = decision == "acceptForSession" ? "session" : "turn" }
                : new JsonObject { ["decision"] = decision };
            WriteCodex(new JsonObject { ["id"] = id.DeepClone(), ["result"] = result });
        }
        ItemChanged?.Invoke(request);
        StateChanged?.Invoke();
    }

    private void AnswerCodexQuestions(AgentItem request, Dictionary<string, string> answers)
    {
        if (_codexRequests.Remove(request.Id, out var id))
        {
            var replies = new JsonObject();
            foreach (var question in (request.RequestInput?["questions"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var asked = Str(question, "question") ?? "";
                if (Str(question, "id") is { } key && answers.TryGetValue(asked, out var answer))
                {
                    replies[key] = new JsonObject { ["answers"] = new JsonArray(answer.Split(", ").Select(part => (JsonNode?)part).ToArray()) };
                }
            }
            WriteCodex(new JsonObject { ["id"] = id.DeepClone(), ["result"] = new JsonObject { ["answers"] = replies } });
        }
        ItemChanged?.Invoke(request);
        StateChanged?.Invoke();
    }

    private void Rpc(string method, JsonObject? parameters, Action<JsonObject?, JsonObject?>? replied = null)
    {
        var id = _nextRpc++;
        if (replied is not null) _rpcs[id] = replied;
        var message = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        WriteCodex(message);
    }

    private void WriteCodex(JsonObject message) => Write(message);

    // What comes back: replies to its requests, its notices, and requests of its own.

    private void ReadCodex(JsonObject json)
    {
        var method = Str(json, "method");
        var hasId = json["id"] is JsonValue;
        if (method is null && hasId)
        {
            var id = json["id"]!.GetValue<long>();
            if (_rpcs.Remove(id, out var replied)) replied(json["result"] as JsonObject ?? (json["error"] is null ? new JsonObject() : null), json["error"] as JsonObject);
            return;
        }
        var parameters = json["params"] as JsonObject ?? new JsonObject();
        if (method is not null && hasId)
        {
            ReadCodexRequest(json["id"]!, method, parameters);
            return;
        }
        switch (method)
        {
            case "item/agentMessage/delta" when Str(parameters, "itemId") is { } itemId:
                if (!_byId.TryGetValue(itemId, out var growing))
                {
                    growing = new AgentItem { Id = itemId, Kind = AgentItemKind.Assistant, Streaming = true };
                    Add(growing);
                }
                growing.Text += Str(parameters, "delta");
                ItemChanged?.Invoke(growing);
                break;
            case "item/started" when parameters["item"] is JsonObject item:
                ReadCodexItem(item, done: false);
                break;
            case "item/completed" when parameters["item"] is JsonObject item:
                ReadCodexItem(item, done: true);
                break;
            case "item/commandExecution/outputDelta" when Str(parameters, "itemId") is { } itemId && _byId.TryGetValue(itemId, out var command):
                var output = (command.Detail ?? "") + Str(parameters, "delta");
                command.Detail = output.Length > LongestDetail ? output[^LongestDetail..] : output;
                break;
            case "turn/started":
                if (parameters["turn"] is JsonObject startedTurn && Str(startedTurn, "id") is { } turnId) _codexTurn = turnId;
                break;
            case "turn/plan/updated" when parameters["plan"] is JsonArray plan:
                Steps = [.. plan.OfType<JsonObject>().Select(step => new AgentStep(Str(step, "step") ?? "", Str(step, "status") switch { "inProgress" => "in_progress", "completed" => "completed", _ => "pending" }, null))];
                StateChanged?.Invoke();
                break;
            case "thread/tokenUsage/updated" when parameters["tokenUsage"] is JsonObject usage:
                if (usage["last"]?["totalTokens"] is JsonValue total && total.TryGetValue<long>(out var tokens)) ContextUsed = (int)tokens;
                if (usage["modelContextWindow"] is JsonValue window && window.TryGetValue<long>(out var most)) ContextWindow = (int)most;
                StateChanged?.Invoke();
                break;
            case "account/rateLimits/updated":
                ReadCodexLimits(parameters, complete: false);
                break;
            case "error" when parameters["error"] is JsonObject error:
                var message = Str(error, "message") ?? "";
                if (parameters["willRetry"]?.GetValueKind() == JsonValueKind.True)
                {
                    Retrying = "Trying again";
                    StateChanged?.Invoke();
                    break;
                }
                _codexFailure = message;
                _codexLimited |= IsCodexLimit(error["codexErrorInfo"]);
                break;
            case "serverRequest/resolved" when parameters["requestId"] is JsonNode resolved:
                var asked = _codexRequests.FirstOrDefault(each => each.Value.ToJsonString() == resolved.ToJsonString()).Key;
                if (asked is not null && _byId.TryGetValue(asked, out var gone))
                {
                    _codexRequests.Remove(asked);
                    if (gone.RequestState == AgentRequestState.Waiting) gone.RequestState = AgentRequestState.Gone;
                    ItemChanged?.Invoke(gone);
                    StateChanged?.Invoke();
                }
                break;
            case "turn/completed" when parameters["turn"] is JsonObject turn:
                EndCodexTurn(turn);
                break;
        }
    }

    // Its usage limit, as T3 Code tells it: usageLimitExceeded or rateLimitExceeded, or a 429 that kept failing.
    private static bool IsCodexLimit(JsonNode? info) => info switch
    {
        JsonValue value when value.TryGetValue<string>(out var code) => code is "usageLimitExceeded" or "rateLimitExceeded",
        JsonObject codes => codes.Any(code => code.Key is "usageLimitExceeded" or "rateLimitExceeded")
            || codes["responseTooManyFailedAttempts"]?["httpStatusCode"]?.ToString() == "429",
        _ => false,
    };

    private void EndCodexTurn(JsonObject turn)
    {
        EndStreaming();
        Retrying = null;
        _codexTurn = null;
        var status = Str(turn, "status");
        if (turn["error"] is JsonObject error)
        {
            _codexFailure ??= Str(error, "message");
            _codexLimited |= IsCodexLimit(error["codexErrorInfo"]);
        }
        if (status == "failed" && !_stopping)
        {
            if (_codexLimited)
            {
                // Its login ran out. The reset comes from its limits, asked for again now, as T3 Code does.
                IsLimited = true;
                LimitResetsAt = null;
                _turns = 0;
                Rpc("account/rateLimits/read", null, (result, _) =>
                {
                    ReadCodexLimits(result, complete: true);
                    var snapshot = result?["rateLimitsByLimitId"]?["codex"] as JsonObject ?? result?["rateLimits"] as JsonObject;
                    LimitResetsAt = AgentUsage.FromCodex(snapshot).Where(limit => limit.Used >= 100).Select(limit => limit.ResetsAt).Max();
                    StateChanged?.Invoke();
                });
            }
            else
            {
                Problem(_codexFailure is { Length: > 0 } why ? why : "The agent stopped with an error.");
            }
        }
        else if (status == "interrupted" && _stopping)
        {
            Notice("Stopped.");
        }
        _stopping = false;
        _turns = Math.Max(0, _turns - 1);
        AskCodexUsage();
        SetWorking(_turns > 0);
    }

    // Its items: what it says, the commands it runs, the files it changes, the tools it uses.
    private void ReadCodexItem(JsonObject item, bool done)
    {
        if (Str(item, "id") is not { } id) return;
        switch (Str(item, "type"))
        {
            case "agentMessage":
                var text = Str(item, "text") ?? "";
                if (_byId.TryGetValue(id, out var said))
                {
                    if (done && text.Length > 0) said.Text = text;
                    said.Streaming = !done;
                    ItemChanged?.Invoke(said);
                }
                else if (text.Trim().Length > 0 || !done)
                {
                    Add(new AgentItem { Id = id, Kind = AgentItemKind.Assistant, Text = text, Streaming = !done });
                }
                break;
            case "commandExecution":
                CodexTool(id, "Bash", $"Bash  {Str(item, "command")}", item, done, Str(item, "aggregatedOutput"));
                break;
            case "fileChange":
                var paths = (item["changes"] as JsonArray ?? []).OfType<JsonObject>().Select(change => Str(change, "path")).OfType<string>().ToList();
                _codexFiles[id] = string.Join(", ", paths);
                CodexTool(id, "Edit", $"Edit  {string.Join(", ", paths.Select(Path.GetFileName))}", item, done,
                    string.Join("\n\n", (item["changes"] as JsonArray ?? []).OfType<JsonObject>().Select(change => Str(change, "diff")).OfType<string>()));
                break;
            case "mcpToolCall":
                var server = Str(item, "server") ?? "mcp";
                var tool = Str(item, "tool") ?? "tool";
                CodexTool(id, $"mcp__{server}__{tool}", $"{server}: {tool}", item, done, item["error"] is JsonObject failed ? Str(failed, "message") : item["result"]?.ToJsonString());
                break;
            case "webSearch":
                CodexTool(id, "WebSearch", $"WebSearch  {Str(item, "query")}", item, done, null);
                break;
            case "imageView":
                CodexTool(id, "Read", $"Read  {Str(item, "path")}", item, done, null);
                break;
            case "contextCompaction" when done:
                Notice("Made room in its memory, summing up what came before.");
                break;
        }
    }

    private void CodexTool(string id, string name, string text, JsonObject item, bool done, string? output)
    {
        if (!_byId.TryGetValue(id, out var tool))
        {
            tool = new AgentItem { Id = id, Kind = AgentItemKind.Tool, Tool = name, Text = text };
            Add(tool);
        }
        tool.Text = text;
        if (done)
        {
            var status = Str(item, "status");
            tool.ToolState = status is "failed" or "declined" || (item["exitCode"] is JsonValue code && code.TryGetValue<int>(out var exit) && exit != 0) ? AgentToolState.Failed : AgentToolState.Done;
            if (output is { Length: > 0 }) tool.Detail = output.Length > LongestDetail ? output[..LongestDetail] + "…" : output;
        }
        ItemChanged?.Invoke(tool);
    }

    // Its own requests: approvals for commands, file changes and permissions, and questions for the person.
    private void ReadCodexRequest(JsonNode id, string method, JsonObject parameters)
    {
        var itemId = $"codex-request-{id.ToJsonString()}-{NewId()[..6]}";
        AgentItem? item = method switch
        {
            "item/commandExecution/requestApproval" => new AgentItem
            {
                Id = itemId,
                Kind = AgentItemKind.Approval,
                Tool = "Bash",
                Text = Str(parameters, "command") ?? "a command",
                Detail = Str(parameters, "reason"),
            },
            "item/fileChange/requestApproval" => new AgentItem
            {
                Id = itemId,
                Kind = AgentItemKind.Approval,
                Tool = "Edit",
                Text = Str(parameters, "itemId") is { } changeId && _codexFiles.TryGetValue(changeId, out var files) && files.Length > 0 ? $"Edit {files}" : "Change files",
                Detail = Str(parameters, "reason"),
            },
            "item/permissions/requestApproval" => new AgentItem
            {
                Id = itemId,
                Kind = AgentItemKind.Approval,
                Tool = "permissions",
                Text = Str(parameters, "reason") ?? "More access",
                Detail = parameters["permissions"]?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            },
            "item/tool/requestUserInput" => new AgentItem
            {
                Id = itemId,
                Kind = AgentItemKind.Question,
                Tool = "AskUserQuestion",
                Questions = [.. (parameters["questions"] as JsonArray ?? []).OfType<JsonObject>().Select(question => new AgentQuestion(
                    Str(question, "header") ?? "",
                    Str(question, "question") ?? "",
                    [.. (question["options"] as JsonArray ?? []).OfType<JsonObject>().Select(option => new AgentOption(Str(option, "label") ?? "", Str(option, "description") ?? ""))],
                    false))],
            },
            _ => null,
        };
        if (item is null)
        {
            // MCP forms and anything else are turned down, as T3 Code turns down what it doesn't handle.
            if (method == "mcpServer/elicitation/request") WriteCodex(new JsonObject { ["id"] = id.DeepClone(), ["result"] = new JsonObject { ["action"] = "decline" } });
            else WriteCodex(new JsonObject { ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" } });
            return;
        }
        item.RequestState = AgentRequestState.Waiting;
        item.RequestInput = parameters.DeepClone();
        if (item.Kind == AgentItemKind.Approval) item.Suggestions = new JsonArray("session"); // So it can be allowed for the rest of the conversation.
        _codexRequests[item.Id] = id.DeepClone();
        Add(item);
        StateChanged?.Invoke();
    }
}
