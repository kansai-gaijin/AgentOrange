using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrange;

/// <summary>T3 Code's kinds of background work, and Agent Orange's waits, which hold a turn while it runs.</summary>
public enum BackgroundKind { Subagent, Command, Monitor, Task, Wait }

/// <summary>
/// One piece of work an agent left going: a command it started in the background, a Monitor following
/// one, a helper agent (a subagent, or a workflow of them), or some other background task, such as a
/// long MCP call. Kept only while its agent program runs, as they end with it.
/// </summary>
public sealed class BackgroundWork
{
    /// <summary>Its provider's own id for it: Claude Code's task id (or the tool use's, until the task is told), or a Codex thread or item id.</summary>
    public required string Id { get; set; }
    public string? ToolUseId { get; set; }
    public BackgroundKind Kind { get; set; }

    /// <summary>The agent's own one-line description of it, such as "Run the test suite until it finishes".</summary>
    public string Title { get; set; } = "";

    /// <summary>A command's or Monitor's command line, as the agent gave it.</summary>
    public string? Command { get; set; }

    /// <summary>What a helper agent is doing now, as its progress says.</summary>
    public string? Activity { get; set; }
    public DateTimeOffset ActivityAt { get; set; }

    /// <summary>
    /// The agent said it goes on once this ends, so the conversation waits on it as it does on a
    /// helper agent. Only the agent knows whether a command is a wait (tests, a build) or something it
    /// left running (a dev server), so a command counts only once it says so. A Monitor always does.
    /// </summary>
    public bool Awaited { get; set; }

    public DateTimeOffset Started { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Ended { get; set; }

    /// <summary>False while the turn that started it is held up by it, as a helper agent in the foreground holds it.</summary>
    public bool InBackground { get; set; } = true;

    /// <summary>A Codex command's process, for stopping it.</summary>
    public string? ProcessId { get; set; }

    public bool IsRunning => Ended is null;

    /// <summary>T3's rule: helper agents and other background tasks hold the conversation, and commands and Monitors only once awaited.</summary>
    public bool Holds => Awaited || Kind is BackgroundKind.Subagent or BackgroundKind.Task;
}

/// <summary>
/// The background work of a conversation, from what its provider says, as T3 Code keeps it. Claude Code
/// tells each task's start and end (task_started, task_updated, task_notification), what a helper agent
/// is up to (task_progress), and the whole set still running in the background whenever it changes
/// (background_tasks_changed), which wins, as a missed start or end can't leave one showing. Codex's
/// helper agents are threads whose parent is this one, and its commands still running when a turn ends
/// are its background commands. Once a turn ends, work that holds it (see <see cref="BackgroundWork.Holds"/>)
/// means it's waiting, not done, and the person is told it finished only once none is left.
/// </summary>
public sealed partial class AgentConversation
{
    private static readonly TimeSpan FinishedAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StoppingFor = TimeSpan.FromSeconds(10);

    /// <summary>Everything it left going since its program started, the latest last.</summary>
    [JsonIgnore] public List<BackgroundWork> Work { get; } = [];

    /// <summary>Its background work still running, in the order it started: what the line above the box and the Background work panel list.</summary>
    [JsonIgnore] public List<BackgroundWork> PendingWork => [.. Work.Where(work => work.IsRunning && work.InBackground && work.Kind != BackgroundKind.Wait)];

    /// <summary>How many of those it waits on.</summary>
    [JsonIgnore] public int BackgroundJobs => PendingWork.Count(work => work.Holds);

    /// <summary>Its turn ended, but it goes on once work it waits on ends, so it isn't done.</summary>
    [JsonIgnore] public bool IsWaitingOnWork => !IsWorking && BackgroundJobs > 0;

    /// <summary>Its turn is held up by a wait (Agent Orange's wait_for, Codex's sleep or its wait for helper agents), as T3's sidebar says Waiting for one.</summary>
    [JsonIgnore] public bool IsBlockedOnWait => IsWorking && Work.Any(work => work.IsRunning && work.Kind == BackgroundKind.Wait);

    /// <summary>When its last turn ended, for how long it has waited since.</summary>
    [JsonIgnore] public DateTimeOffset TurnEndedAt { get; private set; }

    /// <summary>Stop was asked for its background work, which shows as Stopping... until it has gone, or for 10 s.</summary>
    [JsonIgnore] public bool IsStoppingWork => _stoppingWork is { } asked && DateTimeOffset.UtcNow - asked < StoppingFor && PendingWork.Count > 0;
    private DateTimeOffset? _stoppingWork;

    /// <summary>On the window's thread: it finished, with nothing it waits on left running, which is when the person is told.</summary>
    public event Action? Finished;

    private readonly Dictionary<string, (string Tool, JsonObject? Input)> _toolInputs = []; // Tools that can start background work, by tool use.
    private bool _tasksTold; // Claude Code says when its background work starts and ends, so nothing is guessed.
    private long _finishCheck;

    /// <summary>
    /// T3 Code's Stop for background work: Claude Code's program is ended, which ends everything it
    /// started (the next message picks the conversation back up); Codex's helper agents' turns are
    /// interrupted and its background commands' terminals ended.
    /// </summary>
    public void StopBackgroundWork()
    {
        if (PendingWork.Count == 0 || IsStoppingWork) return;
        _stoppingWork = DateTimeOffset.UtcNow;
        if (_codex) StopCodexWork();
        else if (_process is not null) End();
#if DEBUG
        else if (Headless.Active) WrittenForChecks.Add("(its program ended)");
#endif
        StateChanged?.Invoke();
    }

    /// <summary>The agent says it goes on once this work ends (Agent Orange's background_wait), or no longer waits on it. False when it isn't running here.</summary>
    public bool Await(string taskId, bool waiting)
    {
        if (Work.FirstOrDefault(work => work.Id == taskId && work.IsRunning && work.Kind != BackgroundKind.Wait) is not { } work) return false;
        work.Awaited = waiting;
        if (!waiting && !IsWorking && BackgroundJobs == 0) FinishSoon();
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>T3's words for each kind, one and several.</summary>
    internal static (string One, string Several) KindWords(BackgroundKind kind) => kind switch
    {
        BackgroundKind.Subagent => ("subagent", "subagents"),
        BackgroundKind.Monitor => ("monitor", "monitors"),
        BackgroundKind.Task => ("background task", "background tasks"),
        BackgroundKind.Wait => ("wait", "waits"),
        _ => ("command", "commands"),
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
                Awaited = name == "Monitor",
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
                var backgrounded = json["is_backgrounded"]?.GetValueKind();
                // A command run in the foreground is only a tool call, as T3 has it.
                if (Str(json, "task_type") == "local_bash" && backgrounded == JsonValueKind.False) return;
                var work = TaskWork(taskId!, Str(json, "tool_use_id"), Str(json, "task_type"), Str(json, "description"));
                if (backgrounded is JsonValueKind.True or JsonValueKind.False) work.InBackground = backgrounded == JsonValueKind.True;
                break;
            case "task_updated" when Work.FirstOrDefault(each => each.Id == taskId) is { } updated && json["patch"] is JsonObject patch:
                if (Str(patch, "description") is { Length: > 0 } description) updated.Title = description;
                if (patch["is_backgrounded"]?.GetValueKind() == JsonValueKind.True) updated.InBackground = true;
                if (Str(patch, "status") is "completed" or "failed" or "killed" && updated.IsRunning) End(updated);
                break;
            case "task_progress" when Work.FirstOrDefault(each => each.Id == taskId) is { Kind: BackgroundKind.Subagent } going:
                if (Str(json, "description")?.Trim() is { Length: > 0 } now)
                {
                    going.Activity = now;
                    going.ActivityAt = DateTimeOffset.UtcNow;
                }
                break;
            case "task_notification":
                _tasksTold = true;
                var ended = Work.FirstOrDefault(each => each.Id == taskId) ?? (Str(json, "tool_use_id") is { } toolUse ? Work.FirstOrDefault(each => each.ToolUseId == toolUse && each.IsRunning) : null);
                if (ended is not { IsRunning: true }) return;
                End(ended);
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
                }
                foreach (var gone in Work.Where(each => each.IsRunning && each.InBackground && each.Kind != BackgroundKind.Wait && !running.Contains(each.Id)).ToList()) End(gone);
                break;
            default:
                return;
        }
        StateChanged?.Invoke();
    }

    // The task Claude Code told of, made from the tool use that started it when that's known. Only
    // its commands are background commands; helper agents, workflows of them and teammates are
    // subagents; anything else is a background task, as T3 sorts them.
    private BackgroundWork TaskWork(string taskId, string? toolUseId, string? taskType, string? description)
    {
        // Known by its task, or by the tool use that started it. The set of running work can tell of a
        // task before its start does, without the tool use, so a command not yet told of is matched by
        // its description.
        var work = Work.FirstOrDefault(each => each.Id == taskId)
            ?? (toolUseId is null
                ? Work.FirstOrDefault(each => each.IsRunning && each.Id == each.ToolUseId && each.Title == (description ?? ""))
                : Work.FirstOrDefault(each => each.ToolUseId == toolUseId && each.IsRunning));
        var input = (toolUseId ?? work?.ToolUseId) is { } used && _toolInputs.TryGetValue(used, out var launched) ? launched : default;
        if (work is null)
        {
            work = new BackgroundWork { Id = taskId, ToolUseId = toolUseId };
            Work.Add(work);
        }
        work.Id = taskId;
        work.ToolUseId ??= toolUseId;
        var kind = taskType switch
        {
            "local_bash" when input.Tool == "Monitor" || work.Kind == BackgroundKind.Monitor => BackgroundKind.Monitor,
            "local_bash" => BackgroundKind.Command,
            "local_agent" or "remote_agent" or "in_process_teammate" or "local_workflow" => BackgroundKind.Subagent,
            null when input.Tool is "Agent" or "Task" or "Workflow" => BackgroundKind.Subagent,
            null => work.Kind,
            _ => BackgroundKind.Task,
        };
        // A Monitor is waited on from its start, as T3 has it.
        if (kind == BackgroundKind.Monitor && work.Kind != BackgroundKind.Monitor) work.Awaited = true;
        work.Kind = kind;
        if (description is { Length: > 0 }) work.Title = description;
        else if (work.Title.Length == 0 && Str(input.Input, "description") is { Length: > 0 } described) work.Title = described;
        if (work.Kind is BackgroundKind.Command or BackgroundKind.Monitor) work.Command ??= Str(input.Input, "command");
        return work;
    }

    private void End(BackgroundWork work)
    {
        work.Ended = DateTimeOffset.UtcNow;
        if (!IsWorking && BackgroundJobs == 0 && work.Holds && work.InBackground) FinishSoon();
    }

    // Without word from Claude Code, a turn it starts by itself is one of its background commands ending.
    private void GuessOneEnded()
    {
        if (_tasksTold) return;
        if (Work.FirstOrDefault(work => work.IsRunning && work.InBackground && work.Kind != BackgroundKind.Wait) is { } oldest) End(oldest);
    }

    // Its program ended, and everything it started with it. Quietly when it was ended on purpose, to
    // start again with what's changed or to be settled, which isn't news to anyone.
    private void EndAllWork(bool quietly)
    {
        var held = BackgroundJobs > 0;
        var any = false;
        foreach (var work in Work.Where(work => work.IsRunning))
        {
            work.Ended = DateTimeOffset.UtcNow;
            any = true;
        }
        _toolInputs.Clear();
        _tasksTold = false;
        _stoppingWork = null;
        if (held && !quietly && !IsWorking) FinishSoon();
        if (any) StateChanged?.Invoke();
    }

    // The turn ended, and with it anything it was held up by that wasn't sent to the background.
    private void EndForegroundWork()
    {
        if (_turns > 0) return;
        foreach (var work in Work.Where(work => work.IsRunning && !work.InBackground).ToList()) End(work);
    }

    // The last of the work it waits on ended while it wasn't working. Claude Code takes that up straight
    // away in a turn of its own, so as T3 does, it's only finished if no turn starts within 5 s.
    private async void FinishSoon()
    {
        var check = ++_finishCheck;
        await Task.Delay(FinishedAfter);
        if (check != _finishCheck || IsWorking || BackgroundJobs > 0) return;
        Finished?.Invoke();
        StateChanged?.Invoke();
    }

    // The turn ended: finished now unless work it waits on is still going.
    private void TurnEnded()
    {
        _finishCheck++;
        TurnEndedAt = DateTimeOffset.UtcNow;
        if (BackgroundJobs == 0) Finished?.Invoke();
    }

    // Waits the turn itself is held up by: Agent Orange's wait_for, which can take minutes, and Codex's
    // sleep and its wait for helper agents.
    private void StartWait(string id, string title)
    {
        if (Work.Any(work => work.Id == id)) return;
        Work.Add(new BackgroundWork { Id = id, ToolUseId = id, Kind = BackgroundKind.Wait, Title = title, InBackground = false });
        StateChanged?.Invoke();
    }

    private void EndWait(string id)
    {
        if (Work.FirstOrDefault(work => work.Id == id && work.Kind == BackgroundKind.Wait) is not { IsRunning: true } wait) return;
        End(wait);
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
    // turn ending (or its thread closing). Its commands still running when a turn ends are background
    // commands until each completes, as T3 keeps them; Codex commands are never awaited.

    private readonly Dictionary<string, string> _codexAgentTurns = []; // Each helper agent's turn going on, by its thread.
    private readonly Dictionary<string, string> _codexProcesses = []; // Each command's process, by its item.

    private bool ReadCodexAgent(string? method, JsonObject parameters)
    {
        if (_codexThread is null) return false;
        if (method == "thread/started" && parameters["thread"] is JsonObject thread && Str(thread, "id") is { } started && started != _codexThread)
        {
            if (Str(thread, "parentThreadId") == _codexThread)
            {
                var agent = CodexAgentWork(started);
                // What it was asked, which its first message is, or else its name.
                if (agent.Title.Length == 0) agent.Title = (Str(thread, "preview") is { Length: > 0 } asked ? asked : null) ?? Str(thread, "agentNickname") ?? Str(thread, "agentRole") ?? "";
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
                if (Str(parameters["turn"] as JsonObject, "id") is { } turnId) _codexAgentTurns[threadId] = turnId;
                break;
            case "turn/completed" or "thread/closed" when work.IsRunning:
                End(work);
                break;
            case "thread/status/changed" when Str(parameters["status"] as JsonObject, "type") == "systemError" && work.IsRunning:
                End(work);
                break;
            case "item/started" when parameters["item"] is JsonObject item && CodexActivity(item) is { } doing:
                work.Activity = doing;
                work.ActivityAt = DateTimeOffset.UtcNow;
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
        var work = new BackgroundWork { Id = thread, Kind = BackgroundKind.Subagent };
        Work.Add(work);
        return work;
    }

    // Its collab tools: spawnAgent starts a helper agent, and wait holds the turn until they're done.
    // Each says how its helper agents stand, as T3 maps them.
    private void ReadCodexCollab(string id, JsonObject item, bool done)
    {
        var tool = Str(item, "tool");
        var prompt = Str(item, "prompt");
        foreach (var thread in (item["receiverThreadIds"] as JsonArray ?? []).Select(each => each?.GetValue<string>()).OfType<string>())
        {
            if (tool == "spawnAgent")
            {
                var agent = CodexAgentWork(thread);
                if (prompt is { Length: > 0 } && agent.Title.Length == 0) agent.Title = prompt;
            }
        }
        foreach (var (thread, state) in item["agentsStates"] as JsonObject ?? [])
        {
            if (Work.FirstOrDefault(work => work.Id == thread) is not { IsRunning: true } agent) continue;
            if (Str(state as JsonObject, "status") is "completed" or "errored" or "shutdown" or "notFound" or "interrupted") End(agent);
        }
        var words = tool switch
        {
            "spawnAgent" => $"Agent  {prompt}",
            "wait" => "Wait for its subagents",
            "sendInput" or "sendMessage" or "followupTask" => $"Message a subagent  {prompt}",
            "closeAgent" => "Close a subagent",
            "interruptAgent" => "Stop a subagent",
            _ => tool ?? "Agent",
        };
        CodexTool(id, "Agent", words.ReplaceLineEndings(" ").TrimEnd(), item, done, null);
        if (tool == "wait" && !done) StartWait(id, "Wait for its subagents");
        else if (tool == "wait") EndWait(id);
        StateChanged?.Invoke();
    }

    // Its commands (and MCP calls) still running as its turn ends go on in the background.
    private void KeepCodexCommandsGoing()
    {
        foreach (var tool in Items.Where(item => item.Kind == AgentItemKind.Tool && item.ToolState == AgentToolState.Running && (item.Tool == "Bash" || item.Tool?.StartsWith("mcp__", StringComparison.Ordinal) == true)).ToList())
        {
            if (Work.Any(work => work.Id == tool.Id)) continue;
            var command = tool.Tool == "Bash";
            Work.Add(new BackgroundWork
            {
                Id = tool.Id,
                Kind = command ? BackgroundKind.Command : BackgroundKind.Task,
                Title = command ? "" : tool.Text,
                Command = command ? tool.Text : null,
                ProcessId = _codexProcesses.GetValueOrDefault(tool.Id),
                Started = tool.At,
            });
        }
    }

    // One of those completed.
    private void CodexCommandEnded(string id)
    {
        _codexProcesses.Remove(id);
        if (Work.FirstOrDefault(work => work.Id == id && work.IsRunning && work.Kind is BackgroundKind.Command or BackgroundKind.Task) is not { } work) return;
        End(work);
        StateChanged?.Invoke();
    }

    // What a helper agent's item says it's doing, in a few words.
    private static string? CodexActivity(JsonObject item) => Str(item, "type") switch
    {
        "commandExecution" => $"Running {Str(item, "command")}",
        "fileChange" => "Editing files",
        "mcpToolCall" => $"Using {Str(item, "server")}: {Str(item, "tool")}",
        "webSearch" => "Searching the web",
        "reasoning" => "Thinking",
        _ => null,
    };

    // T3's Stop for Codex: each helper agent's turn interrupted, and each background command's terminal ended.
    private void StopCodexWork()
    {
        foreach (var work in PendingWork)
        {
            if (work.Kind == BackgroundKind.Subagent && _codexAgentTurns.TryGetValue(work.Id, out var turnId))
            {
                Rpc("turn/interrupt", new JsonObject { ["threadId"] = work.Id, ["turnId"] = turnId });
            }
            else if (work.ProcessId is { } process && _codexThread is { } thread)
            {
                Rpc("thread/backgroundTerminals/terminate", new JsonObject { ["threadId"] = thread, ["processId"] = process }, (result, error) =>
                {
                    if (error is not null) Notice($"Couldn't stop {work.Command ?? "a background command"}: {Str(error, "message")}");
                    else if (result?["terminated"]?.GetValueKind() == JsonValueKind.True) CodexCommandEnded(work.Id);
                });
            }
        }
    }
}
