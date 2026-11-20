using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// How Agent Orange passes calls on to a creative tool's or engine's own MCP, beyond sending them
/// (#100). Agents driving an editor hit the same few failures again and again and wrote their own
/// scaffolding around them: a first call turned away with 404 while the tool was still starting, a
/// "busy" answer with no word on when to try again, long calls cut off by the time limit, and a
/// script that quits the editor reported as cancelled, then a run of polls to learn it had closed.
/// So: open_tool says ready only once the tool answers a real call; a call turned away or dropped
/// while the tool starts is sent again; a busy answer is waited out as it asks; start_tool_call runs
/// a call with no time limit, with poll_tool_call and cancel_tool_call; and close_tool waits for the
/// program to end and says so. All of it is plain MCP, for any tool.
/// </summary>
public partial class MainWindow
{
    // How long a tool counts as starting, from when its program started or open_tool found it not
    // ready: a call it turns away or drops then is sent again, as it's only not ready yet.
    private static readonly TimeSpan StartUp = TimeSpan.FromMinutes(5);

    // How long to keep sending again: while it starts, and for a call it turned away before doing
    // anything (a 404, or no connection), which is always safe to send again.
    private static readonly TimeSpan StartUpRetries = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan TurnedAwayRetries = TimeSpan.FromSeconds(20);

    // How long a call that may have closed its tool waits to see the program end.
    private static readonly TimeSpan ClosingWait = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, DateTime> _toolStartedAt = [];

    /// <summary>The longest a call waits for its answer, and in all for a tool that says it's busy.</summary>
    private sealed record RelayLimits(TimeSpan Answer, TimeSpan Busy);

    private static readonly RelayLimits UseToolLimits = new(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2));
    private static readonly RelayLimits StartedCallLimits = new(TimeSpan.FromHours(12), TimeSpan.FromMinutes(30));

    private void MarkStarting(EditorDefinition editor) => _toolStartedAt[editor.Id] = DateTime.UtcNow;

    // Starting: its program started a short while ago, or open_tool found it not ready yet.
    private bool InStartUp(EditorDefinition editor)
    {
        if (_toolStartedAt.TryGetValue(editor.Id, out var at) && DateTime.UtcNow - at < StartUp) return true;
        foreach (var id in ToolProcessIds(editor))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (DateTime.Now - process.StartTime < StartUp) return true;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // It ended, or belongs to another user.
            }
        }
        return false;
    }

    // Turned away before anything was done: not found yet, no connection, or not ready, as a tool
    // starting up does. Always safe to send again.
    private static bool TurnedAway(Exception error) =>
        error is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable }
        || SocketErrorIn(error) is SocketError.ConnectionRefused
        || (error is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } && SocketErrorIn(error) is not (SocketError.ConnectionReset or SocketError.ConnectionAborted));

    // Dropped partway, as when a tool closes or restarts: it may or may not have done it.
    private static bool Dropped(Exception error) =>
        !TurnedAway(error) && (SocketErrorIn(error) is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown
            || error is HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded or HttpRequestError.Unknown } or IOException);

    private static SocketError? SocketErrorIn(Exception error)
    {
        for (Exception? at = error; at is not null; at = at.InnerException)
        {
            if (at is SocketException socket) return socket.SocketErrorCode;
        }
        return null;
    }

    // What went wrong, in words.
    private static string Why(Exception error) =>
        error is HttpRequestException { StatusCode: { } status } ? $"it answered {(int)status} {Regex.Replace(status.ToString(), "(?<=[a-z])(?=[A-Z])", " ")}"
        : SocketErrorIn(error) switch
        {
            SocketError.ConnectionRefused => "it didn't take the connection",
            SocketError.ConnectionReset or SocketError.ConnectionAborted => "it dropped the connection",
            _ => error.Message.TrimEnd('.'),
        };

    private static string Took(TimeSpan took) =>
        took.TotalSeconds < 10 ? $"{took.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s"
        : took.TotalMinutes < 2 ? $"{took.TotalSeconds:0} s"
        : took.TotalHours < 2 ? $"{(int)took.TotalMinutes} min {took.Seconds} s"
        : $"{(int)took.TotalHours} h {took.Minutes} min";

    private static string Times(int count) => count == 1 ? "once" : count == 2 ? "twice" : $"{count} times";

    /// <summary>
    /// Whether the tool's MCP answers a real call: a session made and its tools listed, as the first
    /// use_tool needs. A port that takes connections isn't enough: a tool still starting takes them
    /// and turns calls away. Gives how many tools it lists, or how it stands when it isn't ready.
    /// </summary>
    private async Task<(int Tools, string? NotYet)> ReadinessAsync(EditorDefinition editor, string? folder)
    {
        if (!await ReachableAsync(editor)) return (0, "its MCP doesn't take connections yet");
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var client = await ToolClientAsync(editor, folder, limit.Token);
            var listed = await client.RequestAsync("tools/list", [], limit.Token);
            var count = (listed?["tools"] as JsonArray)?.Count ?? 0;
            return count > 0 ? (count, null) : (0, "its MCP answers, but lists no tools yet");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or HttpRequestException or OperationCanceledException or JsonException)
        {
            ForgetToolClient(editor);
            return (0, $"its MCP doesn't answer calls yet: {Why(error)}");
        }
    }

    /// <summary>
    /// Waits until the tool answers a real call, or the limit passes, and says which. Its program
    /// gone for ten seconds ends the wait, as it won't answer now.
    /// </summary>
    private async Task<string> UntilReadyAsync(EditorDefinition editor, string? folder, TimeSpan limit, bool started)
    {
        MarkStarting(editor);
        var waited = Stopwatch.StartNew();
        var notYet = "its MCP doesn't answer yet";
        var gone = 0;
        while (waited.Elapsed < limit)
        {
            await Task.Delay(1000);
            var (tools, why) = await ReadinessAsync(editor, folder);
            if (why is null)
            {
                return $"{(started ? $"Opened {editor.Name}" : $"{editor.Name} was open but still starting. It's ready now")}: its MCP answers calls, with {tools} tools, after {Took(waited.Elapsed)}. tool_tools lists them.";
            }
            notYet = why;
            gone = ToolProcessIds(editor).Count == 0 ? gone + 1 : 0;
            if (gone >= 10) return $"{editor.Name} closed before its MCP answered ({why}). Look at why before opening it again, such as in its log.";
        }
        return $"{editor.Name} {(started ? "was started" : "is open")}, but after {Took(limit)} {notYet}. It may still be loading, or its MCP may be off in it. Try open_tool again in a while.";
    }

    /// <summary>
    /// Passes one request on to the tool's MCP, sending it again by itself when that's safe: while
    /// the tool is starting, a call it turns away (404, no connection) or drops; at any time, one it
    /// turned away before doing anything, for a short while; and one it says it's too busy for,
    /// after the wait it asks for. What was done about it goes in notes. Throws as the last try did,
    /// or McpToolBusy once it has waited as long as it may.
    /// </summary>
    private async Task<JsonNode?> RelayAsync(EditorDefinition editor, string? folder, string method, JsonObject parameters, RelayLimits limits, List<string> notes, CancellationToken cancel)
    {
        var before = ToolProcessIds(editor);
        var trying = Stopwatch.StartNew();
        var (sentAgain, busyTimes, busyWaited) = (0, 0, TimeSpan.Zero);
        var (starting, lastWhy) = (false, "");

        void Note()
        {
            if (sentAgain > 0) notes.Add($"Agent Orange sent it again {Times(sentAgain)} over {Took(trying.Elapsed)}, as {editor.Name} {(starting ? "was still starting" : "turned it away")} ({lastWhy}).");
            if (busyTimes > 0) notes.Add($"{editor.Name} said it was busy, so Agent Orange waited as it asked and sent it again {Times(busyTimes)}, {Took(busyWaited)} in all.");
        }

        while (true)
        {
            TimeSpan again;
            ExceptionDispatchInfo? failed = null;
            try
            {
                var client = await ToolClientAsync(editor, folder, cancel);
                var result = await client.RequestAsync(method, (JsonObject)parameters.DeepClone(), cancel);

                // A tool can say it's busy as the answer to a call, rather than as an error.
                if (method == "tools/call" && result is JsonObject answer && IsToolError(answer) && McpToolBusy.From(TextOf(answer), answer) is { } busyAnswer) throw busyAnswer;
                Note();
                return result;
            }
            catch (McpToolBusy busy)
            {
                again = busy.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(busyTimes, 4)));
                again = TimeSpan.FromSeconds(Math.Clamp(again.TotalSeconds, 0.5, 60));
                if (busyWaited + again > limits.Busy)
                {
                    Note();
                    throw new McpToolBusy(busy.Message, again);
                }
                busyTimes++;
                busyWaited += again;
            }
            catch (Exception error) when (!cancel.IsCancellationRequested && error is not McpToolBusy && (TurnedAway(error) || Dropped(error)))
            {
                ForgetToolClient(editor); // A tool that's starting or restarted needs a new session.
                starting = InStartUp(editor);
                // A call it may have done is only sent again while it starts, unless only reading.
                var mayFor = TurnedAway(error) ? (starting ? StartUpRetries : TurnedAwayRetries)
                    : starting || method != "tools/call" ? StartUpRetries : TimeSpan.Zero;
                if (trying.Elapsed >= mayFor)
                {
                    Note();
                    throw;
                }
                sentAgain++;
                lastWhy = Why(error);
                again = TimeSpan.FromSeconds(2);
                failed = ExceptionDispatchInfo.Capture(error);
            }
            await Task.Delay(again, cancel);

            // Closing, such as from a script that quits it, isn't starting: say what happened.
            if (failed is not null && before.Count > 0 && before.Any(HasEnded))
            {
                Note();
                failed.Throw();
            }
        }
    }

    private static bool IsToolError(JsonObject answer) => answer["isError"] is JsonValue failed && failed.TryGetValue<bool>(out var isError) && isError;

    private static string TextOf(JsonObject answer) =>
        string.Join("\n", (answer["content"] as JsonArray ?? []).OfType<JsonObject>().Select(part => part["text"] is JsonValue text && text.TryGetValue<string>(out var words) ? words : null).OfType<string>());

    private static JsonObject WithNotes(JsonObject answer, List<string> notes)
    {
        if (notes.Count == 0) return answer;
        if (answer["content"] is not JsonArray content) answer["content"] = content = [];
        content.Add(new JsonObject { ["type"] = "text", ["text"] = $"({string.Join(" ", notes)})" });
        return answer;
    }

    // Words a tool answers with as it closes, such as Canceled when a script quits it partway.
    private static readonly Regex ClosingWords = new(@"\b(cancel+ed|shutting down|shut down|exiting|exited|quitting|closing|closed|terminated)\b", RegexOptions.IgnoreCase);

    private static bool HasEnded(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    // Every one of these programs has ended, or does within the wait.
    private static async Task<bool> EndedAsync(List<int> processes, TimeSpan wait)
    {
        if (processes.Count == 0) return false;
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (processes.All(HasEnded)) return true;
            if (watch.Elapsed >= wait) return false;
            await Task.Delay(250);
        }
    }

    private JsonObject ClosedDuringCall(EditorDefinition editor, string name, TimeSpan took)
    {
        ForgetToolClient(editor);
        _toolStartedAt.Remove(editor.Id);
        return ToolResult($"{editor.Name} closed while doing {name}, so it gave no answer of its own: its program has ended, {Took(took)} after the call began. "
            + "That's how a call that quits it ends, such as a script that closes it before a rebuild. open_tool opens it again; close_tool closes it and waits until it has.");
    }

    /// <summary>
    /// A call to one of an open tool's own tools, as use_tool and start_tool_call make it, and what
    /// to tell the agent: the tool's answer, with what Agent Orange did along the way.
    /// </summary>
    private async Task<JsonObject> RelayToolCallAsync(EditorDefinition editor, string? folder, string name, JsonObject arguments, RelayLimits limits, CancellationToken cancel)
    {
        var before = ToolProcessIds(editor);
        var notes = new List<string>();
        var took = Stopwatch.StartNew();
        using var answerLimit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        answerLimit.CancelAfter(limits.Answer);
        try
        {
            var result = await RelayAsync(editor, folder, "tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() }, limits, notes, answerLimit.Token);
            if (result is not JsonObject passed) return WithNotes(ToolResult(result?.ToJsonString() ?? ""), notes);
            var answer = (JsonObject)passed.DeepClone();
            if (IsToolError(answer))
            {
                // A script that quits the tool can be answered as cancelled as it goes.
                if (ClosingWords.IsMatch(TextOf(answer)) && await EndedAsync(before, ClosingWait)) return ClosedDuringCall(editor, name, took.Elapsed);
                if (await ArgumentsHintAsync(editor, folder, name, arguments) is { } hint)
                {
                    if (answer["content"] is not JsonArray content) answer["content"] = content = [];
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = hint });
                }
            }
            return WithNotes(answer, notes);
        }
        catch (McpToolBusy busy)
        {
            var again = (int)Math.Ceiling((busy.RetryAfter ?? TimeSpan.FromSeconds(30)).TotalSeconds);
            var said = ToolResult($"{editor.Name} is still busy, so {name} wasn't done. It said: {busy.Message.Trim()} "
                + $"Try again in {again} s{(limits == UseToolLimits ? ", or use start_tool_call, which waits longer for it" : "")}.", error: true);
            said["_meta"] = new JsonObject { ["agent-orange/retryAfterSeconds"] = again };
            return WithNotes(said, notes);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return ToolResult($"Cancelled {name}: Agent Orange stopped waiting for it and told {editor.Name} to stop it, as MCP has it. Whether work already under way stops is up to {editor.Name}, so check how things stand in it before going on.", error: true);
        }
        catch (OperationCanceledException)
        {
            return WithNotes(ToolResult($"{editor.Name} hadn't answered {name} after {Took(limits.Answer)}, so Agent Orange stopped waiting and told it to stop the call. It may still be working on it. "
                + "For work that long, use start_tool_call, then poll_tool_call, which has no such limit.", error: true), notes);
        }
        catch (InvalidOperationException error)
        {
            // The tool answered, with an error of its own, such as an argument it doesn't take.
            var hint = await ArgumentsHintAsync(editor, folder, name, arguments);
            return WithNotes(ToolResult($"{editor.Name} couldn't do that: {error.Message}{(hint is null ? "" : $"\n{hint}")}", error: true), notes);
        }
        catch (Exception error) when (error is IOException or HttpRequestException)
        {
            ForgetToolClient(editor); // A tool closed and opened again needs a new session.
            if (await EndedAsync(before, ClosingWait)) return ClosedDuringCall(editor, name, took.Elapsed);
            return WithNotes(ToolResult($"{editor.Name} couldn't do that: {Why(error)}.", error: true), notes);
        }
    }

    // Calls start_tool_call started, by their id, kept for an hour after they end so a late poll still gets the answer.

    private sealed class StartedCall(string id, string projectId, EditorDefinition editor, string name)
    {
        public string Id { get; } = id;

        public string ProjectId { get; } = projectId;

        public EditorDefinition Editor { get; } = editor;

        public string Name { get; } = name;

        public Stopwatch Running { get; } = Stopwatch.StartNew();

        public CancellationTokenSource Cancel { get; } = new();

        public Task<JsonObject> Work { get; set; } = Task.FromResult(new JsonObject());

        public DateTime? EndedAt { get; set; }

        public string Line => $"{Id}: {Name} in {Editor.Name}, {(Work.IsCompleted ? $"done after {Took(Running.Elapsed)}" : $"running for {Took(Running.Elapsed)}")}";
    }

    private readonly Dictionary<string, StartedCall> _startedCalls = [];
    private const int LongestPoll = 300;

    private IEnumerable<StartedCall> CallsOf(Project project) => _startedCalls.Values.Where(call => call.ProjectId == project.Id);

    private async Task<object> StartToolCallAsync(Project project, JsonObject given)
    {
        var editor = ToolNamed(project, Required(given, "tool"));
        var name = Required(given, "name");
        var arguments = given["arguments"] as JsonObject ?? [];
        if (!await AnswersAsync(editor)) return ToolResult($"{editor.Name}'s MCP doesn't answer. Open it with open_tool first.", error: true);

        foreach (var old in _startedCalls.Values.Where(call => call.EndedAt is { } ended && DateTime.UtcNow - ended > TimeSpan.FromHours(1)).ToList()) _startedCalls.Remove(old.Id);
        var call = new StartedCall($"call-{Guid.NewGuid():N}"[..13], project.Id, editor, name);
        _startedCalls[call.Id] = call;
        call.Work = RunStartedAsync(call, ProjectFolder(project.Id), arguments);
        return $"Started {name} in {editor.Name} as {call.Id}. It has no time limit. poll_tool_call with call {call.Id} gives its answer once it's done, and with seconds waits up to that long for it, instead of sleep; cancel_tool_call stops it.";
    }

    private async Task<JsonObject> RunStartedAsync(StartedCall call, string? folder, JsonObject arguments)
    {
        try
        {
            return await RelayToolCallAsync(call.Editor, folder, call.Name, arguments, StartedCallLimits, call.Cancel.Token);
        }
        finally
        {
            call.Running.Stop();
            call.EndedAt = DateTime.UtcNow;
        }
    }

    private StartedCall CallNamed(Project project, string asked) =>
        CallsOf(project).FirstOrDefault(call => call.Id.Equals(asked.Trim(), StringComparison.OrdinalIgnoreCase) || call.Id.Equals($"call-{asked.Trim()}", StringComparison.OrdinalIgnoreCase))
        ?? throw new McpException(-32602, CallsOf(project).Any()
            ? $"call: there's no call {asked}. The calls start_tool_call started: {string.Join("; ", CallsOf(project).Select(each => each.Line))}."
            : $"call: there's no call {asked}, as start_tool_call hasn't started any since Agent Orange opened. Calls aren't kept once it closes.");

    private async Task<object> PollToolCallAsync(Project project, JsonObject given)
    {
        var call = CallNamed(project, Required(given, "call"));
        var seconds = WholeNumber(given, "seconds") ?? 0;
        if (seconds < 0 || seconds > LongestPoll) throw new McpException(-32602, $"seconds must be from 0 to {LongestPoll}; it was {seconds}. A longer wait is another call to poll_tool_call.");
        if (!call.Work.IsCompleted && seconds > 0) await Task.WhenAny(call.Work, Task.Delay(TimeSpan.FromSeconds(seconds)));
        if (!call.Work.IsCompleted)
        {
            return $"Still running: {call.Name} in {call.Editor.Name}, for {Took(call.Running.Elapsed)} so far. Call poll_tool_call with seconds again to keep waiting, or cancel_tool_call to stop it.";
        }
        var answer = (JsonObject)(await call.Work).DeepClone();
        if (answer["content"] is not JsonArray content) answer["content"] = content = [];
        content.Insert(0, new JsonObject { ["type"] = "text", ["text"] = $"{call.Name} in {call.Editor.Name} ended after {Took(call.Running.Elapsed)}. What it gave:" });
        return answer;
    }

    private async Task<object> CancelToolCallAsync(Project project, JsonObject given)
    {
        var call = CallNamed(project, Required(given, "call"));
        if (call.Work.IsCompleted) return $"{call.Id} had already ended, after {Took(call.Running.Elapsed)}, so there was nothing to stop. poll_tool_call gives its answer.";
        await call.Cancel.CancelAsync();
        await Task.WhenAny(call.Work, Task.Delay(TimeSpan.FromSeconds(10)));
        return $"Cancelled {call.Id} ({call.Name} in {call.Editor.Name}, after {Took(call.Running.Elapsed)}): Agent Orange stopped waiting for it and told {call.Editor.Name} to stop it, as MCP has it. "
            + $"Whether work already under way stops is up to {call.Editor.Name}, so check how things stand in it before going on.";
    }

    private const int LongestClose = 300;

    /// <summary>
    /// Closes the tool as closing its window does, and waits until its program has ended: with force,
    /// one still open then is ended. Never a refusal for one that isn't open, which is done already.
    /// </summary>
    private async Task<object> CloseToolForAgentAsync(Project project, JsonObject given)
    {
        var editor = ToolNamed(project, Required(given, "tool"));
        var seconds = WholeNumber(given, "seconds") ?? 60;
        if (seconds < 1 || seconds > LongestClose) throw new McpException(-32602, $"seconds must be from 1 to {LongestClose}; it was {seconds}.");
        var force = Yes(given, "force");

        ForgetToolClient(editor); // Its session ends with it.
        _toolStartedAt.Remove(editor.Id);
        var processes = ToolProcessIds(editor);
        if (processes.Count == 0) return $"{editor.Name} isn't open: none of its programs are running, so there's nothing to close.";

        var took = Stopwatch.StartNew();
        var asked = 0;
        var title = "";
        foreach (var id in processes)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (process.MainWindowTitle is { Length: > 0 } named) title = named;
                if (process.CloseMainWindow()) asked++;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // It ended already, or belongs to another user.
            }
        }
        if (await EndedAsync(processes, asked > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(1)))
        {
            return $"Closed {editor.Name}: its program has ended, {Took(took.Elapsed)} after it was asked to close. open_tool opens it again.";
        }
        if (!force)
        {
            return ToolResult(asked == 0
                ? $"{editor.Name} has no window to close, so it couldn't be asked to. close_tool with force true ends its program, losing anything unsaved in it."
                : $"{editor.Name} is still open after {seconds} s{(title.Length > 0 ? $"; its window says \"{title}\"" : "")}. It may be asking something, such as whether to save. Ask the person, or call close_tool with force true to end it, losing anything unsaved.", error: true);
        }
        foreach (var id in processes.Where(id => !HasEnded(id)))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // It ended meanwhile, or can't be ended from here.
            }
        }
        return await EndedAsync(processes, TimeSpan.FromSeconds(10))
            ? (object)$"{editor.Name} didn't close {(asked > 0 ? $"in {seconds} s" : "by itself, as it has no window to close")}, so Agent Orange ended it, as force asked. Its program has ended; anything unsaved in it is lost. open_tool opens it again."
            : ToolResult($"{editor.Name} couldn't be ended: its program is still running. It may belong to another user or run as administrator. Ask the person to close it.", error: true);
    }
}
