using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrange;

public enum BackgroundKind { Command, Monitor, Agent, Workflow, Tool, Wait }

/// <summary>
/// One piece of work an agent left going while it does something else, or while it waits: a command
/// it started in the background, a Monitor following one, a helper agent (a subagent), a workflow, or
/// a long tool call. Kept only while its agent program runs, as they end with it.
/// </summary>
public sealed class BackgroundWork
{
    /// <summary>Its provider's own id for it: Claude Code's task id (or the tool use's, until the task is told), or a Codex thread or item id.</summary>
    public required string Id { get; set; }
    public string? ToolUseId { get; set; }
    public BackgroundKind Kind { get; set; }

    /// <summary>The agent's own one-line description of it, such as "Watch the build until it finishes".</summary>
    public string Title { get; set; } = "";

    /// <summary>A command's or Monitor's command line, as the agent gave it.</summary>
    public string? Command { get; set; }

    /// <summary>A helper agent's kind, such as "general-purpose", and what it did last, such as the last tool it used.</summary>
    public string? AgentType { get; set; }
    public string? Doing { get; set; }

    public DateTimeOffset Started { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Ended { get; set; }

    /// <summary>"running", "completed", "failed" or "stopped".</summary>
    public string State { get; set; } = "running";

    /// <summary>False while the turn that started it waits for it, as a helper agent's turn does unless it's sent to the background.</summary>
    public bool InBackground { get; set; } = true;

    /// <summary>What it said when it ended, and where its output went.</summary>
    public string? Summary { get; set; }
    public string? OutputFile { get; set; }

    public bool IsRunning => Ended is null;
}

/// <summary>
/// The background work of a conversation, from what its provider says. Claude Code tells each task's
/// start and end (task_started, task_updated, task_notification), what a helper agent is up to
/// (task_progress), and the whole set still running whenever it changes (background_tasks_changed),
/// which wins over the rest, as a missed start or end can't leave one showing. Codex tells its helper
/// agents as threads of their own, with this one as their parent. While any of it runs and the agent
/// isn't working, it's waiting on that, not done.
/// </summary>
public sealed partial class AgentConversation
{
    private static readonly TimeSpan FinishedAfter = TimeSpan.FromSeconds(5);

    /// <summary>Everything it left going since its program started, the latest last.</summary>
    [JsonIgnore] public List<BackgroundWork> Work { get; } = [];

    /// <summary>Background work still running that the turn isn't waiting for: what it waits on once the turn ends.</summary>
    [JsonIgnore] public int BackgroundJobs => Work.Count(work => work.IsRunning && work.InBackground);

    /// <summary>It isn't working, but work it started is still going, so it isn't done.</summary>
    [JsonIgnore] public bool IsWaitingOnWork => !IsWorking && BackgroundJobs > 0;

    /// <summary>On the window's thread: it finished, with nothing it started left running, which is when the person is told.</summary>
    public event Action? Finished;

    private readonly Dictionary<string, (string Tool, JsonObject? Input)> _toolInputs = []; // Tools that can start background work, by tool use.
    private bool _tasksTold; // Claude Code says when its background work starts and ends, so nothing is guessed.
    private long _finishCheck;

    /// <summary>Stops one piece of background work, as T3 Code's Stop does.</summary>
    public void StopWork(BackgroundWork work)
    {
        if (!work.IsRunning) return;
        if (_codex)
        {
            StopCodexWork(work);
            return;
        }
        Control(new JsonObject { ["subtype"] = "stop_task", ["task_id"] = work.Id }, reply =>
        {
            if (reply is null) Notice($"Couldn't stop {Describe(work)}.");
        });
    }

    internal static string Describe(BackgroundWork work) => work.Title.Length > 0 ? work.Title : work.Command ?? work.Kind switch
    {
        BackgroundKind.Agent => "a helper agent",
        BackgroundKind.Monitor => "a Monitor",
        BackgroundKind.Workflow => "a workflow",
        _ => "a background command",
    };

    // A tool that may start background work, kept until Claude Code says it did.
    private void NoteToolInput(string toolId, string name, JsonObject? input)
    {
        if (name is not ("Bash" or "PowerShell" or "Monitor" or "Agent" or "Task" or "Workflow")) return;
        _toolInputs[toolId] = (name, input);

        // Without word from Claude Code, a command sent to the background is all there is to go on.
        if (!_tasksTold && name is "Bash" or "PowerShell" or "Monitor" && input?["run_in_background"]?.GetValueKind() == JsonValueKind.True && Work.All(work => work.ToolUseId != toolId))
        {
            Work.Add(new BackgroundWork
            {
                Id = toolId,
                ToolUseId = toolId,
                Kind = name == "Monitor" ? BackgroundKind.Monitor : BackgroundKind.Command,
                Title = Str(input, "description") ?? "",
                Command = Str(input, "command"),
            });
            StateChanged?.Invoke();
        }
    }

    // Claude Code's task events. A task is any work it tracks on its own: a background command, a
    // Monitor, a helper agent (in the foreground or the background), a workflow, an MCP task.
    private void ReadTask(JsonObject json)
    {
        var taskId = Str(json, "task_id");
        if (taskId is null && Str(json, "subtype") != "background_tasks_changed") return;
        switch (Str(json, "subtype"))
        {
            case "task_started":
                _tasksTold = true;
                if (json["skip_transcript"]?.GetValueKind() == JsonValueKind.True || json["ambient"]?.GetValueKind() == JsonValueKind.True) return;
                var work = TaskWork(taskId!, Str(json, "tool_use_id"), Str(json, "task_type"), Str(json, "description"));
                if (Str(json, "subagent_type") is { Length: > 0 } agentType) work.AgentType = agentType;
                if (json["is_backgrounded"] is JsonValue backgrounded && backgrounded.GetValueKind() is JsonValueKind.True or JsonValueKind.False) work.InBackground = backgrounded.GetValueKind() == JsonValueKind.True;
                break;
            case "task_updated" when Work.FirstOrDefault(each => each.Id == taskId) is { } updated && json["patch"] is JsonObject patch:
                if (Str(patch, "description") is { Length: > 0 } description) updated.Title = description;
                if (patch["is_backgrounded"]?.GetValueKind() == JsonValueKind.True) updated.InBackground = true;
                if (Str(patch, "status") is "completed" or "failed" or "killed" && updated.IsRunning) End(updated, Str(patch, "status") == "killed" ? "stopped" : Str(patch, "status")!);
                if (Str(patch, "error") is { Length: > 0 } error) updated.Summary = error;
                break;
            case "task_progress" when Work.FirstOrDefault(each => each.Id == taskId) is { } going:
                if (Str(json, "description") is { Length: > 0 } now) going.Title = now;
                going.Doing = Str(json, "summary") is { Length: > 0 } summary ? summary : Str(json, "last_tool_name") is { Length: > 0 } tool ? $"Last used {tool}" : going.Doing;
                break;
            case "task_notification":
                _tasksTold = true;
                var ended = Work.FirstOrDefault(each => each.Id == taskId) ?? (Str(json, "tool_use_id") is { } toolUse ? Work.FirstOrDefault(each => each.ToolUseId == toolUse && each.IsRunning) : null);
                if (ended is null) return;
                if (Str(json, "summary") is { Length: > 0 } said) ended.Summary = said;
                if (Str(json, "output_file") is { Length: > 0 } output) ended.OutputFile = output;
                if (ended.IsRunning) End(ended, Str(json, "status") ?? "completed");
                break;
            case "background_tasks_changed" when json["tasks"] is JsonArray live:
                // The whole set still running in the background: what isn't in it has ended.
                _tasksTold = true;
                var running = new HashSet<string>();
                foreach (var task in live.OfType<JsonObject>())
                {
                    if (Str(task, "task_id") is not { } id || task["ambient"]?.GetValueKind() == JsonValueKind.True) continue;
                    running.Add(id);
                    var known = Work.FirstOrDefault(each => each.Id == id) ?? TaskWork(id, null, Str(task, "task_type"), Str(task, "description"));
                    known.InBackground = true;
                    if (Str(task, "subagent_type") is { Length: > 0 } kind) known.AgentType = kind;
                }
                foreach (var gone in Work.Where(each => each.IsRunning && each.InBackground && !running.Contains(each.Id)).ToList()) End(gone, "completed");
                break;
            default:
                return;
        }
        StateChanged?.Invoke();
    }

    // The task Claude Code told of, made from the tool use that started it when that's known.
    private BackgroundWork TaskWork(string taskId, string? toolUseId, string? taskType, string? description)
    {
        // Known by its task, or by the tool use that started it. The set of running work can tell of a
        // task before its start does, without the tool use, so a command not yet told of is matched by
        // its description.
        var work = Work.FirstOrDefault(each => each.Id == taskId)
            ?? (toolUseId is null
                ? Work.FirstOrDefault(each => each.IsRunning && each.Id == each.ToolUseId && each.Title == (description ?? ""))
                : Work.FirstOrDefault(each => each.ToolUseId == toolUseId && each.IsRunning));
        toolUseId ??= work?.ToolUseId;
        var input = toolUseId is not null && _toolInputs.TryGetValue(toolUseId, out var used) ? used : default;
        if (work is null)
        {
            work = new BackgroundWork { Id = taskId, ToolUseId = toolUseId };
            Work.Add(work);
        }
        work.Id = taskId;
        work.ToolUseId ??= toolUseId;
        work.Kind = taskType switch
        {
            "local_agent" or "remote_agent" or "in_process_teammate" => BackgroundKind.Agent,
            "monitor_mcp" or "monitor_ws" => BackgroundKind.Monitor,
            "local_workflow" => BackgroundKind.Workflow,
            "mcp_task" => BackgroundKind.Tool,
            "local_bash" when input.Tool == "Monitor" => BackgroundKind.Monitor,
            "local_bash" => BackgroundKind.Command,
            _ => input.Tool is "Agent" or "Task" ? BackgroundKind.Agent : work.Kind,
        };
        if (description is { Length: > 0 }) work.Title = description;
        else if (work.Title.Length == 0 && Str(input.Input, "description") is { Length: > 0 } described) work.Title = described;
        if (work.Kind is BackgroundKind.Command or BackgroundKind.Monitor) work.Command ??= Str(input.Input, "command");
        if (work.Kind == BackgroundKind.Agent) work.AgentType ??= Str(input.Input, "subagent_type");
        return work;
    }

    private void End(BackgroundWork work, string state)
    {
        work.Ended = DateTimeOffset.UtcNow;
        work.State = state is "completed" or "failed" or "stopped" ? state : "completed";
        if (!IsWorking && BackgroundJobs == 0) FinishSoon();
    }

    // Without word from Claude Code, a turn it starts by itself is one of its background commands ending.
    private void GuessOneEnded()
    {
        if (_tasksTold) return;
        if (Work.FirstOrDefault(work => work.IsRunning && work.InBackground) is { } oldest) End(oldest, "completed");
    }

    // Its program ended, and everything it started with it. Quietly when it was ended on purpose, to
    // start again with what's changed or to be settled, which isn't news to anyone.
    private void EndAllWork(bool quietly)
    {
        var any = false;
        foreach (var work in Work.Where(work => work.IsRunning))
        {
            work.Ended = DateTimeOffset.UtcNow;
            work.State = "stopped";
            any = true;
        }
        _toolInputs.Clear();
        _tasksTold = false;
        if (any && !quietly && !IsWorking) FinishSoon();
        if (any) StateChanged?.Invoke();
    }

    // The turn ended, and with it anything it was waiting for that wasn't sent to the background.
    private void EndForegroundWork()
    {
        if (_turns > 0) return;
        foreach (var work in Work.Where(work => work.IsRunning && !work.InBackground).ToList()) End(work, "completed");
    }

    // The last of its work ended while it wasn't working. Claude Code usually takes that up straight
    // away in a turn of its own, so it's only finished if no turn starts within a few seconds.
    private async void FinishSoon()
    {
        var check = ++_finishCheck;
        await Task.Delay(FinishedAfter);
        if (check != _finishCheck || IsWorking || BackgroundJobs > 0) return;
        Finished?.Invoke();
        StateChanged?.Invoke();
    }

    // The turn ended: finished now unless work it started is still going.
    private void TurnEnded()
    {
        _finishCheck++;
        if (BackgroundJobs == 0) Finished?.Invoke();
    }

    // Waits the turn itself is held up by: Agent Orange's wait_for, which can take minutes, and Codex's
    // sleep and wait for its helper agents. Listed with the rest while they last, as T3 lists waits.
    private void StartWait(string id, string title)
    {
        if (Work.Any(work => work.Id == id)) return;
        Work.Add(new BackgroundWork { Id = id, ToolUseId = id, Kind = BackgroundKind.Wait, Title = title, InBackground = false });
        StateChanged?.Invoke();
    }

    private void EndWait(string id, bool failed)
    {
        if (Work.FirstOrDefault(work => work.Id == id && work.Kind == BackgroundKind.Wait) is not { IsRunning: true } wait) return;
        End(wait, failed ? "failed" : "completed");
        StateChanged?.Invoke();
    }

    // Agent Orange's wait_for, in a few words: what it waits on.
    private static string? WaitTitle(string tool, JsonObject? input)
    {
        if (!tool.EndsWith("wait_for", StringComparison.Ordinal) || !tool.Contains("agent-orange", StringComparison.Ordinal)) return null;
        if (Str(input, "file") is { Length: > 0 } file) return Str(input, "text") is { Length: > 0 } text ? $"Wait for \"{text}\" in {file}" : $"Wait for {file}";
        if (Str(input, "port") is { Length: > 0 } port) return $"Wait for port {port}";
        if (Str(input, "process") is { Length: > 0 } process) return $"Wait for {process} to end";
        return "Wait";
    }

    // Codex
    //
    // Its helper agents are threads of their own, started by its spawnAgent tool with this thread as
    // their parent. Each of their turns is the work it was given: running from its turn starting to its
    // turn ending (or its thread closing), and its items say what it's doing meanwhile.

    private bool ReadCodexAgent(string? method, JsonObject parameters)
    {
        if (_codexThread is null) return false;
        if (method == "thread/started" && parameters["thread"] is JsonObject thread && Str(thread, "id") is { } started && started != _codexThread)
        {
            if (Str(thread, "parentThreadId") == _codexThread)
            {
                var agent = CodexAgentWork(started);
                var named = Str(thread, "agentNickname") ?? Str(thread, "agentRole");
                if (named is { Length: > 0 }) agent.AgentType = named;
                if (agent.Title.Length == 0 && Str(thread, "preview") is { Length: > 0 } preview) agent.Title = OneLineOf(preview);
                StateChanged?.Invoke();
            }
            return true;
        }
        if (Str(parameters, "threadId") is not { } threadId || threadId == _codexThread) return false;

        // Anything of another thread is a helper agent's, never this conversation's own.
        if (Work.FirstOrDefault(work => work.Id == threadId) is not { } work) return true;
        switch (method)
        {
            case "turn/started":
                work.Ended = null;
                work.State = "running";
                if (Str(parameters["turn"] as JsonObject, "id") is { } turnId) _codexAgentTurns[threadId] = turnId;
                break;
            case "turn/completed" when work.IsRunning:
                End(work, Str(parameters["turn"] as JsonObject, "status") switch { "failed" => "failed", "interrupted" => "stopped", _ => "completed" });
                break;
            case "thread/closed" when work.IsRunning:
                End(work, "completed");
                break;
            case "thread/status/changed" when Str(parameters["status"] as JsonObject, "type") == "systemError" && work.IsRunning:
                End(work, "failed");
                break;
            case "item/started" when parameters["item"] is JsonObject item && CodexDoing(item) is { } doing:
                work.Doing = doing;
                break;
            default:
                return true;
        }
        StateChanged?.Invoke();
        return true;
    }

    private BackgroundWork CodexAgentWork(string thread)
    {
        if (Work.FirstOrDefault(work => work.Id == thread) is { } known) return known;
        var work = new BackgroundWork { Id = thread, Kind = BackgroundKind.Agent };
        Work.Add(work);
        return work;
    }

    // Its collab tools: spawnAgent starts a helper agent, and wait holds the turn until they're done.
    // Each says how its helper agents stand.
    private void ReadCodexCollab(string id, JsonObject item, bool done)
    {
        var tool = Str(item, "tool");
        var prompt = Str(item, "prompt");
        foreach (var thread in (item["receiverThreadIds"] as JsonArray ?? []).Select(each => each?.GetValue<string>()).OfType<string>())
        {
            if (tool == "spawnAgent")
            {
                var agent = CodexAgentWork(thread);
                if (prompt is { Length: > 0 } && agent.Title.Length == 0) agent.Title = OneLineOf(prompt);
            }
        }
        foreach (var (thread, state) in item["agentsStates"] as JsonObject ?? [])
        {
            if (Work.FirstOrDefault(work => work.Id == thread) is not { IsRunning: true } agent) continue;
            if (Str(state as JsonObject, "status") is { } status && status is "completed" or "errored" or "shutdown" or "notFound" or "interrupted")
            {
                if (Str(state as JsonObject, "message") is { Length: > 0 } message) agent.Summary = message;
                End(agent, status switch { "errored" => "failed", "interrupted" or "shutdown" => "stopped", _ => "completed" });
            }
        }
        var waits = tool == "wait";
        var words = tool switch
        {
            "spawnAgent" => $"Agent  {OneLineOf(prompt ?? "")}",
            "wait" => "Wait for its helper agents",
            "sendInput" or "sendMessage" or "followupTask" => $"Message a helper agent  {OneLineOf(prompt ?? "")}",
            "closeAgent" => "Close a helper agent",
            "interruptAgent" => "Stop a helper agent",
            _ => tool ?? "Agent",
        };
        CodexTool(id, "Agent", words.TrimEnd(), item, done, null);
        if (waits && !done) StartWait(id, "Wait for its helper agents");
        else if (waits) EndWait(id, Str(item, "status") == "failed");
        StateChanged?.Invoke();
    }

    // What a helper agent's item says it's doing, in a few words.
    private static string? CodexDoing(JsonObject item) => Str(item, "type") switch
    {
        "commandExecution" => $"Running {OneLineOf(Str(item, "command") ?? "")}",
        "fileChange" => "Editing files",
        "mcpToolCall" => $"Using {Str(item, "server")}: {Str(item, "tool")}",
        "webSearch" => "Searching the web",
        "reasoning" => "Thinking",
        _ => null,
    };

    // Stops a helper agent's turn, as T3 Code's Stop does.
    private void StopCodexWork(BackgroundWork work)
    {
        if (work.Kind != BackgroundKind.Agent || !_codexAgentTurns.TryGetValue(work.Id, out var turnId)) return;
        Rpc("turn/interrupt", new JsonObject { ["threadId"] = work.Id, ["turnId"] = turnId }, (_, error) =>
        {
            if (error is not null) Notice($"Couldn't stop {Describe(work)}: {Str(error, "message")}");
        });
    }

    private readonly Dictionary<string, string> _codexAgentTurns = []; // Each helper agent's turn going on, by its thread.

    private static string OneLineOf(string text)
    {
        var line = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return line.Length > 120 ? line[..120].TrimEnd() + "…" : line;
    }
}
